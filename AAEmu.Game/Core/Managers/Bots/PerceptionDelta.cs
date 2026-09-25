using System.Numerics;

using AAEmu.Game.Utils;

using Snapshot = AAEmu.Game.Core.Managers.Bots.BotPerceptionSnapshot.Snapshot;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Lifecycle of one perceived objId, derived from successive frames (there is
/// no engine spawn/despawn event in this stack).
///
/// <see cref="Spawned"/> — first frame the objId was perceived.
/// <see cref="Live"/> — alive, or no hp concept (doodad/character).
/// <see cref="Dead"/> — dead, loot state unknown or unreachable.
/// <see cref="Corpse"/> — dead with a reachable, non-empty loot container.
/// <see cref="CorpseLooted"/> — the container of an observed corpse has emptied.
/// <see cref="Removed"/> — the objId was perceived before and is gone now.
/// </summary>
public enum PerceptionLifecycle
{
    Spawned = 0,
    Live = 1,
    Dead = 2,
    Corpse = 3,
    CorpseLooted = 4,
    Removed = 5
}

/// <summary>One entity that changed position between two frames.</summary>
public sealed record EntityMove(
    uint ObjId,
    RadarEntityKind Kind,
    Vector3 From,
    Vector3 To,
    float FromDistance,
    float ToDistance)
{
    /// <summary>Flat (XY) distance travelled — the same measure the radar uses.</summary>
    public float TraveledM => MathUtil.CalculateDistance(From, To, false);
}

/// <summary>One entity whose hp changed between two frames.</summary>
public sealed record EntityHealthChange(
    uint ObjId,
    RadarEntityKind Kind,
    uint? TemplateId,
    int FromHp,
    int ToHp);

/// <summary>
/// The derived difference between two perception frames. Every list is ordered
/// by objId and is empty — never null — when nothing of that kind happened.
///
/// The first frame (<see cref="IsFirstFrame"/>) reports every entity as
/// appeared and nothing else: there is no prior state to diff against.
/// </summary>
public sealed record PerceptionFrameDiff
{
    /// <summary>UTC time of the newer frame.</summary>
    public required DateTime ObservedAtUtc { get; init; }

    /// <summary>UTC time of the older frame; null on the first frame.</summary>
    public DateTime? PreviousObservedAtUtc { get; init; }

    /// <summary>Perceived now, not perceived before.</summary>
    public required IReadOnlyList<PerceivedEntity> Appeared { get; init; }

    /// <summary>
    /// Perceived before, gone now. Each row is the LAST observed state of the
    /// entity, re-stamped <see cref="PerceptionLifecycle.Removed"/> — the
    /// removal is the only thing this frame knows about it.
    /// </summary>
    public required IReadOnlyList<PerceivedEntity> Disappeared { get; init; }

    public required IReadOnlyList<EntityMove> Moved { get; init; }

    public required IReadOnlyList<EntityHealthChange> HealthChanged { get; init; }

    /// <summary>Was alive, is now dead (whatever the loot state).</summary>
    public required IReadOnlyList<PerceivedEntity> BecameDead { get; init; }

    /// <summary>Was not a corpse, now has loot available.</summary>
    public required IReadOnlyList<PerceivedEntity> BecameCorpse { get; init; }

    /// <summary>Was not lootable, now is.</summary>
    public required IReadOnlyList<PerceivedEntity> BecameLootable { get; init; }

    /// <summary>Was a corpse with loot, now has an emptied container.</summary>
    public required IReadOnlyList<PerceivedEntity> BecameLooted { get; init; }

    public bool IsFirstFrame => PreviousObservedAtUtc == null;

    public bool IsEmpty =>
        Appeared.Count == 0 && Disappeared.Count == 0 && Moved.Count == 0 && HealthChanged.Count == 0 &&
        BecameDead.Count == 0 && BecameCorpse.Count == 0 && BecameLootable.Count == 0 && BecameLooted.Count == 0;
}

/// <summary>
/// Layer 3 of the perception stack — the TEMPORAL DIFF.
///
/// A bounded ring of perception frames plus the transition set derived from
/// each consecutive pair. The ring is the ONLY memory in the stack: layers 1
/// and 2 are pure functions of the live world, and every transition here is a
/// diff of two frames — never an engine event (the engine emits spawns for
/// scripted spawners only and no despawn at all).
///
/// Ring shape: <see cref="Current"/> (newest), <see cref="Previous"/> (the one
/// before it) and <see cref="History"/> (everything older, oldest first). The
/// ring holds at most <see cref="HistoryCapacity"/> frames
/// (<see cref="MinHistoryCapacity"/>..<see cref="MaxHistoryCapacity"/>), so a
/// long-lived bot's perception memory is bounded by construction.
///
/// Thread affinity: the ring itself is not synchronized — it is owned by
/// whoever drives the perception loop (one actor's step), exactly like the
/// actor's other per-actor state.
/// </summary>
public sealed class PerceptionDelta
{
    /// <summary>Smallest permitted ring (frames).</summary>
    public const int MinHistoryCapacity = 16;

    /// <summary>Largest permitted ring (frames).</summary>
    public const int MaxHistoryCapacity = 32;

    /// <summary>Default ring size.</summary>
    public const int DefaultHistoryCapacity = 24;

    /// <summary>
    /// Flat movement below this (metres) is read as float noise, not motion:
    /// the engine's own positions carry sub-centimetre jitter from the client
    /// movement model, which must never read as a Moved transition.
    /// </summary>
    public const float MoveEpsilonM = 0.05f;

    private readonly List<Snapshot> _frames;

    public PerceptionDelta(int historyCapacity = DefaultHistoryCapacity)
    {
        HistoryCapacity = Math.Clamp(historyCapacity, MinHistoryCapacity, MaxHistoryCapacity);
        _frames = new List<Snapshot>(HistoryCapacity);
    }

    /// <summary>Bounded number of frames retained (clamped to 16..32).</summary>
    public int HistoryCapacity { get; }

    /// <summary>Frames currently retained.</summary>
    public int FrameCount => _frames.Count;

    /// <summary>Number of frames in <see cref="History"/>.</summary>
    public int HistoryCount => Math.Max(0, _frames.Count - 2);

    /// <summary>Newest frame, or null before the first <see cref="Advance"/>.</summary>
    public Snapshot? Current => _frames.Count > 0 ? _frames[^1] : null;

    /// <summary>Frame before <see cref="Current"/>, or null when fewer than two frames are retained.</summary>
    public Snapshot? Previous => _frames.Count > 1 ? _frames[^2] : null;

    /// <summary>Frames older than <see cref="Previous"/>, oldest first.</summary>
    public IReadOnlyList<Snapshot> History =>
        _frames.Count <= 2 ? [] : _frames.GetRange(0, _frames.Count - 2);

    /// <summary>The diff of the last <see cref="Advance"/>; null before the first one.</summary>
    public PerceptionFrameDiff? Diff { get; private set; }

    /// <summary>
    /// Pushes one frame and derives its diff against the frame it continues.
    /// </summary>
    public PerceptionFrameDiff Advance(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var diff = Derive(Current, snapshot);
        _frames.Add(snapshot);
        if (_frames.Count > HistoryCapacity)
            _frames.RemoveAt(0);

        Diff = diff;
        return diff;
    }

    /// <summary>Drops every retained frame and the last diff.</summary>
    public void Reset()
    {
        _frames.Clear();
        Diff = null;
    }

    /// <summary>
    /// Derives the transition set between two frames. Public and static so a
    /// consumer (or a test) can diff two recorded frames without owning a ring.
    /// </summary>
    public static PerceptionFrameDiff Derive(Snapshot? previous, Snapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var appeared = new List<PerceivedEntity>();
        var disappeared = new List<PerceivedEntity>();
        var moved = new List<EntityMove>();
        var healthChanged = new List<EntityHealthChange>();
        var becameDead = new List<PerceivedEntity>();
        var becameCorpse = new List<PerceivedEntity>();
        var becameLootable = new List<PerceivedEntity>();
        var becameLooted = new List<PerceivedEntity>();

        if (previous == null)
        {
            appeared.AddRange(current.Entities);
            return Frame(current, null, appeared, disappeared, moved, healthChanged,
                becameDead, becameCorpse, becameLootable, becameLooted);
        }

        var priorById = new Dictionary<uint, PerceivedEntity>(previous.Entities.Count);
        foreach (var entity in previous.Entities)
            priorById[entity.ObjId] = entity;

        var currentIds = new HashSet<uint>();
        foreach (var now in current.Entities)
        {
            currentIds.Add(now.ObjId);
            if (!priorById.TryGetValue(now.ObjId, out var before))
            {
                appeared.Add(now);
                continue;
            }

            if (MathUtil.CalculateDistance(before.Position, now.Position, false) > MoveEpsilonM)
                moved.Add(new EntityMove(now.ObjId, now.Kind, before.Position, now.Position, before.Distance, now.Distance));

            if (before.Npc?.Hp is { } fromHp && now.Npc?.Hp is { } toHp && fromHp != toHp)
                healthChanged.Add(new EntityHealthChange(now.ObjId, now.Kind, now.TemplateId, fromHp, toHp));

            if (before.Lifecycle is PerceptionLifecycle.Spawned or PerceptionLifecycle.Live &&
                now.Lifecycle is PerceptionLifecycle.Dead or PerceptionLifecycle.Corpse or PerceptionLifecycle.CorpseLooted)
                becameDead.Add(now);

            if (before.Lifecycle is not (PerceptionLifecycle.Corpse or PerceptionLifecycle.CorpseLooted) &&
                now.Lifecycle is PerceptionLifecycle.Corpse)
                becameCorpse.Add(now);

            if (before.Corpse?.Lootable != true && now.Corpse?.Lootable == true)
                becameLootable.Add(now);

            if (before.Lifecycle is PerceptionLifecycle.Corpse &&
                now.Lifecycle is PerceptionLifecycle.CorpseLooted)
                becameLooted.Add(now);
        }

        foreach (var before in previous.Entities)
        {
            if (!currentIds.Contains(before.ObjId))
                disappeared.Add(before with { Lifecycle = PerceptionLifecycle.Removed });
        }

        return Frame(current, previous.ObservedAt, appeared, disappeared, moved, healthChanged,
            becameDead, becameCorpse, becameLootable, becameLooted);
    }

    private static PerceptionFrameDiff Frame(
        Snapshot current,
        DateTime? previousObservedAt,
        List<PerceivedEntity> appeared,
        List<PerceivedEntity> disappeared,
        List<EntityMove> moved,
        List<EntityHealthChange> healthChanged,
        List<PerceivedEntity> becameDead,
        List<PerceivedEntity> becameCorpse,
        List<PerceivedEntity> becameLootable,
        List<PerceivedEntity> becameLooted) => new()
    {
        ObservedAtUtc = current.ObservedAt,
        PreviousObservedAtUtc = previousObservedAt,
        Appeared = appeared,
        Disappeared = disappeared,
        Moved = moved,
        HealthChanged = healthChanged,
        BecameDead = becameDead,
        BecameCorpse = becameCorpse,
        BecameLootable = becameLootable,
        BecameLooted = becameLooted
    };
}
