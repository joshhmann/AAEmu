using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// NPC type extension of <see cref="PerceivedEntity"/>.
///
/// Every field is a LIVE read carried over from the radar row or resolved once
/// at capture; a read that cannot be joined (template gone, spawner unset,
/// quest index absent) stays UNKNOWN (null) — never a fabricated
/// <c>false</c>/<c>0</c>. <see cref="Merchant"/>, <see cref="QuestGiver"/> and
/// <see cref="Owner"/> are exactly the fields where a default value would read
/// as a positive claim.
/// </summary>
public sealed record PerceivedNpc(
    int? Level,
    int? Hp,
    int? MaxHp,
    bool? Alive,
    bool? Hostile,
    bool? Merchant,
    bool? QuestGiver,
    uint? Owner,
    uint? SpawnerId);

/// <summary>
/// Doodad type extension of <see cref="PerceivedEntity"/>. <see cref="Phase"/>
/// joins the live phase's function-group kind (Start/Normal/End) out of the
/// doodad template; <see cref="Container"/> reads a coffer's item container
/// (null for every non-coffer doodad — the loot-pack path grants items
/// directly and never fills <c>LootingContainer</c>).
/// </summary>
public sealed record PerceivedDoodad(
    uint? FuncGroupId,
    string? Phase,
    uint? OwnerId,
    uint? OwnerObjId,
    string? OwnerType,
    bool? Interactable,
    bool? Harvestable,
    int? Container);

/// <summary>
/// Corpse type extension, attached to an NPC row whose <c>Hp</c> has reached
/// zero. <see cref="OriginalNpcTemplate"/> is the template the corpse spawned
/// as (the corpse IS the npc object — AAEmu never swaps it), so prey identity
/// is not lost across death.
/// </summary>
public sealed record PerceivedCorpse(
    uint? OriginalNpcTemplate,
    uint? Owner,
    bool? Lootable,
    int? LootCount,
    bool? Looted);

/// <summary>
/// One perceived entity: the envelope every consumer reads, plus the type
/// extension that matches <see cref="Kind"/> (exactly one of
/// <see cref="Npc"/>/<see cref="Doodad"/> is set; <see cref="Corpse"/> is set
/// additionally for a dead NPC row).
/// </summary>
public sealed record PerceivedEntity
{
    public required uint ObjId { get; init; }

    public required RadarEntityKind Kind { get; init; }

    /// <summary>Live template id; null when the radar row carried none.</summary>
    public required uint? TemplateId { get; init; }

    public required string? Name { get; init; }

    public required Vector3 Position { get; init; }

    /// <summary>Flat distance from the observer at capture, in metres.</summary>
    public required float Distance { get; init; }

    public required PerceptionLifecycle Lifecycle { get; init; }

    /// <summary>Alive and hostile — a legal attack target for the observer.</summary>
    public required bool IsTargetable { get; init; }

    /// <summary>Within the observer's interaction range (and, for a doodad, interactable at all).</summary>
    public required bool IsInteractable { get; init; }

    /// <summary>First frame this objId was perceived (UTC).</summary>
    public required DateTime FirstSeen { get; init; }

    /// <summary>Frame this row was read in (UTC).</summary>
    public required DateTime LastSeen { get; init; }

    public PerceivedNpc? Npc { get; init; }

    public PerceivedDoodad? Doodad { get; init; }

    public PerceivedCorpse? Corpse { get; init; }
}

/// <summary>
/// Layer 2 of the perception stack — the PERCEPTION SNAPSHOT.
///
/// Pure projection of one <see cref="RadarSnapshot"/> (plus the live
/// <see cref="Character"/> it belongs to) into the typed, lifecycle-stamped
/// view a planner consumes: every perceived entity with its kind extension,
/// classified into <see cref="Snapshot.Characters"/>/<see cref="Snapshot.Npcs"/>/
/// <see cref="Snapshot.Doodads"/>/<see cref="Snapshot.Corpses"/>.
///
/// Contract:
///  - the radar frame is the ONLY census — capture adds no region query, no
///    packet and no world mutation; the live character is read only for the
///    engine predicates a row's facts need (hostility, lootability,
///    harvestability, interactability, quest offers);
///  - a radar row whose identity no longer resolves live carries no position
///    and is therefore not enveloped — its absence is what layer 3 reports as
///    <see cref="PerceptionLifecycle.Removed"/>;
///  - every unjoined fact stays UNKNOWN (null), never false;
///  - <c>FirstSeen</c> carries across frames through the optional
///    <paramref name="previous"/> snapshot, which is also the basis of the
///    Spawned/Live/Dead/Corpse/CorpseLooted lifecycle (this stack consumes no
///    engine spawn event — the first observation of an objId is the spawn
///    evidence).
///
/// Call sequence (layer 3 owns the frame memory):
/// <code>
/// var snapshot = BotPerceptionSnapshot.Capture(radar, character, delta.Current);
/// var diff = delta.Advance(snapshot);
/// </code>
/// </summary>
public static class BotPerceptionSnapshot
{
    /// <summary>
    /// One perception frame: the read time, the observer's pose, every
    /// perceived entity, the by-kind views and the observer's place.
    /// </summary>
    public sealed record Snapshot
    {
        /// <summary>UTC time the underlying radar frame was read.</summary>
        public required DateTime ObservedAt { get; init; }

        public required Vector3 SelfPosition { get; init; }

        /// <summary>Every perceived entity, ordered by objId (deterministic).</summary>
        public required IReadOnlyList<PerceivedEntity> Entities { get; init; }

        /// <summary>Other characters (never the observer itself).</summary>
        public required IReadOnlyList<PerceivedEntity> Characters { get; init; }

        /// <summary>NPCs, alive or dead.</summary>
        public required IReadOnlyList<PerceivedEntity> Npcs { get; init; }

        public required IReadOnlyList<PerceivedEntity> Doodads { get; init; }

        /// <summary>NPC rows carrying a <see cref="PerceivedCorpse"/> extension.</summary>
        public required IReadOnlyList<PerceivedEntity> Corpses { get; init; }

        /// <summary>Name of the observer's zone; null when the zone table is not loaded.</summary>
        public string? Place { get; init; }
    }

    /// <summary>
    /// Captures one perception snapshot from a radar frame.
    /// </summary>
    /// <param name="radar">The layer-1 frame (the census).</param>
    /// <param name="character">The observing character (live engine predicates).</param>
    /// <param name="previous">
    /// The frame this one continues (layer 3's <c>Current</c>); null for the
    /// first frame — every entity is then <see cref="PerceptionLifecycle.Spawned"/>.
    /// </param>
    public static Snapshot Capture(RadarSnapshot radar, Character character, Snapshot? previous = null)
    {
        ArgumentNullException.ThrowIfNull(radar);
        ArgumentNullException.ThrowIfNull(character);

        var world = character.ParentWorld;
        var priorById = previous == null
            ? null
            : previous.Entities.ToDictionary(static e => e.ObjId);
        var entities = new List<PerceivedEntity>(radar.Entities.Count);

        foreach (var row in radar.Entities)
        {
            // Position and distance are written together by the radar, so a
            // row with neither is an identity-only row for an objId that no
            // longer resolves: it has no position to envelope, and its absence
            // from this frame is the Removed evidence layer 3 reads.
            if (row is not { Position: { } position, DistanceM: { } distance })
                continue;

            PerceivedEntity? prior = null;
            priorById?.TryGetValue(row.ObjId, out prior);
            entities.Add(Build(row, position, distance, world, character, prior));
        }

        entities.Sort(static (a, b) => a.ObjId.CompareTo(b.ObjId));

        var characters = new List<PerceivedEntity>();
        var npcs = new List<PerceivedEntity>();
        var doodads = new List<PerceivedEntity>();
        var corpses = new List<PerceivedEntity>();
        foreach (var entity in entities)
        {
            switch (entity.Kind)
            {
                case RadarEntityKind.Npc:
                    npcs.Add(entity);
                    break;
                case RadarEntityKind.Doodad:
                    doodads.Add(entity);
                    break;
                default:
                    characters.Add(entity);
                    break;
            }

            if (entity.Corpse != null)
                corpses.Add(entity);
        }

        return new Snapshot
        {
            ObservedAt = radar.TakenAtUtc,
            SelfPosition = radar.Position,
            Entities = entities,
            Characters = characters,
            Npcs = npcs,
            Doodads = doodads,
            Corpses = corpses,
            Place = ZoneNameOf(character.Transform.ZoneId)
        };
    }

    private static PerceivedEntity Build(
        RadarEntity row,
        Vector3 position,
        float distance,
        Models.Game.World.WorldInstance? world,
        Character character,
        PerceivedEntity? prior)
    {
        var npc = row.Kind == RadarEntityKind.Npc ? world?.GetNpc(row.ObjId) : null;
        var doodad = row.Kind == RadarEntityKind.Doodad ? world?.GetDoodad(row.ObjId) : null;

        var npcExtension = npc == null ? null : BuildNpc(row, npc, character);
        var doodadExtension = doodad == null ? null : BuildDoodad(row, doodad, character, distance);
        // The corpse view exists only once the npc is dead: a living npc is
        // not a corpse, and its (empty) loot state is not a corpse fact.
        var corpseExtension = npc != null && row.Hp is <= 0 ? BuildCorpse(row, npc) : null;

        return new PerceivedEntity
        {
            ObjId = row.ObjId,
            Kind = row.Kind,
            TemplateId = row.TemplateId == 0 ? null : row.TemplateId,
            Name = row.Name,
            Position = position,
            Distance = distance,
            Lifecycle = LifecycleOf(row, corpseExtension, prior),
            IsTargetable = IsTargetable(row, npcExtension),
            IsInteractable = distance <= GameplayActor.MaxInteractRange
                             && (row.Kind != RadarEntityKind.Doodad || doodadExtension?.Interactable == true),
            FirstSeen = prior?.FirstSeen ?? row.ObservedAtUtc,
            LastSeen = row.ObservedAtUtc,
            Npc = npcExtension,
            Doodad = doodadExtension,
            Corpse = corpseExtension
        };
    }

    private static PerceivedNpc BuildNpc(RadarEntity row, Npc npc, Character character)
    {
        var hp = row.Hp;
        return new PerceivedNpc(
            Level: row.Level,
            Hp: hp,
            MaxHp: TryValue(() => npc.MaxHp),
            Alive: hp == null ? null : hp > 0,
            Hostile: TryValue(() => CombatDecisionTree.IsHostileTarget(character, npc)),
            Merchant: row.Merchant,
            QuestGiver: row.TemplateId is not { } templateId || templateId == 0
                ? null
                : TryJoin(() => QuestManager.PeekInstance?.GetQuestsOfferedByNpc(templateId)) is { } offers
                    ? offers.Count > 0
                    : null,
            Owner: npc.OwnerId == 0 ? null : npc.OwnerId,
            SpawnerId: SpawnerIdOf(npc));
    }

    private static PerceivedDoodad BuildDoodad(RadarEntity row, Doodad doodad, Character character, float distance)
        => new(
            FuncGroupId: row.PhaseFuncGroupId,
            Phase: PhaseOf(doodad, row.PhaseFuncGroupId),
            OwnerId: row.OwnerId,
            OwnerObjId: row.OwnerObjId,
            OwnerType: row.OwnerType,
            Interactable: distance <= GameplayActor.MaxInteractRange
                          && row.DespawnAtUtc == null
                          && TryValue(() => doodad.AllowedToInteract(character)) == true,
            Harvestable: TryValue(() => M3aM4ReplayScenario.IsHarvestable(doodad)),
            Container: (doodad as DoodadCoffer)?.ItemContainer?.GetItemsSnapshot().Count);

    private static PerceivedCorpse BuildCorpse(RadarEntity row, Npc npc) => new(
        OriginalNpcTemplate: row.TemplateId == 0 ? null : row.TemplateId,
        Owner: npc.OwnerId == 0 ? null : npc.OwnerId,
        Lootable: row.Lootable,
        LootCount: row.LootContainer,
        // "Nothing left in the container" is knowable without loot history; an
        // unknown container stays unknown instead of reading as looted.
        Looted: row.LootContainer == null ? null : row.LootContainer == 0);

    /// <summary>
    /// Per-entity lifecycle from the radar facts plus the previous frame:
    ///
    ///  - not in the previous frame → <see cref="PerceptionLifecycle.Spawned"/>;
    ///  - alive, or without an hp concept (doodad) → <see cref="PerceptionLifecycle.Live"/>;
    ///  - dead with a reachable, non-empty container → <see cref="PerceptionLifecycle.Corpse"/>;
    ///  - dead with an emptied container that was observed as a corpse →
    ///    <see cref="PerceptionLifecycle.CorpseLooted"/>;
    ///  - every other dead state (loot unknown, loot out of reach, or a corpse
    ///    that was never observed lootable) → <see cref="PerceptionLifecycle.Dead"/>.
    /// </summary>
    private static PerceptionLifecycle LifecycleOf(RadarEntity row, PerceivedCorpse? corpse, PerceivedEntity? prior)
    {
        if (prior == null)
            return PerceptionLifecycle.Spawned;

        var hp = row.Hp;
        if (hp == null || hp > 0)
            return PerceptionLifecycle.Live;

        if (corpse?.Looted == true)
            return prior.Lifecycle is PerceptionLifecycle.Corpse or PerceptionLifecycle.CorpseLooted
                ? PerceptionLifecycle.CorpseLooted
                : PerceptionLifecycle.Dead;

        return corpse?.Lootable == true
            ? PerceptionLifecycle.Corpse
            : PerceptionLifecycle.Dead;
    }

    /// <summary>
    /// A row is targetable when it is a legal attack target for the observer: a
    /// live hostile NPC, or a live character. Doodads are interacted with, never
    /// targeted, and unknown hostility stays untargetable rather than claiming a
    /// legal target.
    /// </summary>
    private static bool IsTargetable(RadarEntity row, PerceivedNpc? npc) => row.Kind switch
    {
        RadarEntityKind.Npc => npc?.Alive == true && npc.Hostile == true,
        RadarEntityKind.Character => row.Hp is > 0,
        _ => false
    };

    /// <summary>
    /// Spawner template id, or the spawner's own id when the template id is
    /// unset (scripted/fixture spawners). Null when the npc has no spawner —
    /// never zero, which would read as a real id.
    /// </summary>
    private static uint? SpawnerIdOf(Npc npc)
    {
        if (npc.Spawner is not { } spawner)
            return null;
        if (spawner.SpawnerId != 0)
            return spawner.SpawnerId;
        return spawner.Id == 0 ? null : spawner.Id;
    }

    /// <summary>
    /// Human-readable phase of the live function group: the template group's
    /// own kind (Start/Normal/End). Null when the template or the group is not
    /// loaded — never a guessed phase label.
    /// </summary>
    private static string? PhaseOf(Doodad doodad, uint? funcGroupId)
    {
        if (funcGroupId is not { } groupId || doodad.Template?.FuncGroups is not { } groups)
            return null;
        foreach (var group in groups)
        {
            if (group.Id == groupId)
                return group.GroupKindId.ToString();
        }

        return null;
    }

    private static string? ZoneNameOf(uint zoneKey)
    {
        if (zoneKey == 0)
            return null;
        try
        {
            return ZoneManager.PeekInstance?.GetZoneByKey(zoneKey)?.Name;
        }
        catch
        {
            // An unloaded zone table (headless hosts never run the zone loader)
            // throws out of the dictionary lookup: the failed read is an
            // explicit unknown, the same rule the radar applies per field.
            return null;
        }
    }

    /// <summary>
    /// Live engine query that can be unavailable in a partially-loaded host
    /// (unseeded singleton, unloaded data): a throwing query yields the UNKNOWN
    /// value instead of propagating into the perception path.
    /// </summary>
    private static T? TryValue<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static T? TryJoin<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }
}
