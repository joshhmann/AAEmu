#nullable enable

using System.Text.Json;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The runtime brain-tuning store: one process-wide <see cref="BotBrainTuning"/>
/// snapshot plus the observability around it (revision, previous values, when
/// each field was last observed). A single volatile holder swapped atomically
/// per POST and read lock-free per wake — next-wake visibility, no restarts,
/// Login/Game/bot/quest state untouched.
///
/// Scope is GLOBAL (not per-bot): the streak maps the thresholds feed
/// (<c>GiveUpStreaks</c>, <c>CombatStallStreaks</c>, <c>LeashStaleStreaks</c>)
/// are process-wide statics, and the quest/loot planners read shared
/// <c>QuestOptions</c> defaults — a per-bot layer would need per-actor
/// counters threaded through every leg for no observed need. Global keeps the
/// cut to one holder + one options-application point.
/// </summary>
public static class BotBrainTuningStore
{
    private static readonly JsonSerializerOptions PatchOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record Holder(BotBrainTuning Tuning, long Revision, DateTime UpdatedAtUtc);

    private static volatile Holder _current =
        new(BotBrainTuning.Empty, 0, DateTime.UtcNow);

    /// <summary>Last-applied tuning per field, for current/previous observability.</summary>
    private static readonly object _historyLock = new();
    private static readonly Dictionary<string, (object? Previous, object? Current, DateTime ObservedAtUtc)> _history = new();

    /// <summary>Current revision (increments once per POST that changes anything).</summary>
    public static long Revision => _current.Revision;

    /// <summary>Current snapshot (null field = canonical default wins).</summary>
    public static BotBrainTuning Current => _current.Tuning;

    /// <summary>When the current revision was applied.</summary>
    public static DateTime UpdatedAtUtc => _current.UpdatedAtUtc;

    /// <summary>
    /// Resets the store to the no-override snapshot (test seam; revision
    /// restarts at 0 so each test observes its own revision clock).
    /// </summary>
    internal static void ResetForTests()
    {
        _current = new(BotBrainTuning.Empty, 0, DateTime.UtcNow);
        lock (_historyLock)
            _history.Clear();
    }

    /// <summary>
    /// Applies a partial POST patch: present fields set/clear the override,
    /// absent fields keep their value. Returns the new snapshot. Revision
    /// increments only when at least one field actually changed.
    /// </summary>
    public static (BotBrainTuning Tuning, long Revision, bool Changed) Apply(BotBrainTuningPatch patch)
    {
        var prior = _current;
        var t = prior.Tuning;
        var next = new BotBrainTuning(
            patch.HasLootSafeRadiusM ? patch.LootSafeRadiusM : t.LootSafeRadiusM,
            patch.HasGiveUpAfterUnresolvedWakes ? patch.GiveUpAfterUnresolvedWakes : t.GiveUpAfterUnresolvedWakes,
            patch.HasGiveUpAfterPlanFailures ? patch.GiveUpAfterPlanFailures : t.GiveUpAfterPlanFailures,
            patch.HasCombatStallWakes ? patch.CombatStallWakes : t.CombatStallWakes,
            patch.HasLeashEdgeFraction ? patch.LeashEdgeFraction : t.LeashEdgeFraction);
        if (next == prior.Tuning)
            return (prior.Tuning, prior.Revision, false);
        var now = DateTime.UtcNow;
        _current = new(next, prior.Revision + 1, now);
        lock (_historyLock)
        {
            Record("lootSafeRadiusM", t.LootSafeRadiusM, next.LootSafeRadiusM, now);
            Record("giveUpAfterUnresolvedWakes", t.GiveUpAfterUnresolvedWakes, next.GiveUpAfterUnresolvedWakes, now);
            Record("giveUpAfterPlanFailures", t.GiveUpAfterPlanFailures, next.GiveUpAfterPlanFailures, now);
            Record("combatStallWakes", t.CombatStallWakes, next.CombatStallWakes, now);
            Record("leashEdgeFraction", t.LeashEdgeFraction, next.LeashEdgeFraction, now);
        }
        return (next, prior.Revision + 1, true);
    }

    private static void Record(string field, object? previous, object? current, DateTime now)
    {
        if (Equals(previous, current))
            return;
        _history[field] = (previous, current, now);
    }

    /// <summary>
    /// Parses a POST body into a presence-tracked patch: only properties the
    /// JSON actually carried count as present (explicit null clears). Unknown
    /// properties are ignored. Throws <see cref="JsonException"/> on malformed JSON.
    /// </summary>
    public static BotBrainTuningPatch ParsePatch(string body)
    {
        float? safe = null; var hasSafe = false;
        int? gu = null; var hasGu = false;
        int? gp = null; var hasGp = false;
        int? stall = null; var hasStall = false;
        float? leash = null; var hasLeash = false;
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            switch (prop.Name.ToLowerInvariant())
            {
                case "lootsaferadiusm":
                    hasSafe = true;
                    safe = prop.Value.ValueKind == JsonValueKind.Null ? null : (float?)prop.Value.GetSingle();
                    break;
                case "giveupafterunresolvedwakes":
                    hasGu = true;
                    gu = prop.Value.ValueKind == JsonValueKind.Null ? null : (int?)prop.Value.GetInt32();
                    break;
                case "giveupafterplanfailures":
                    hasGp = true;
                    gp = prop.Value.ValueKind == JsonValueKind.Null ? null : (int?)prop.Value.GetInt32();
                    break;
                case "combatstallwakes":
                    hasStall = true;
                    stall = prop.Value.ValueKind == JsonValueKind.Null ? null : (int?)prop.Value.GetInt32();
                    break;
                case "leashedgefraction":
                    hasLeash = true;
                    leash = prop.Value.ValueKind == JsonValueKind.Null ? null : (float?)prop.Value.GetSingle();
                    break;
            }
        }
        return new BotBrainTuningPatch(safe, gu, gp, stall, leash)
        {
            HasLootSafeRadiusM = hasSafe,
            HasGiveUpAfterUnresolvedWakes = hasGu,
            HasGiveUpAfterPlanFailures = hasGp,
            HasCombatStallWakes = hasStall,
            HasLeashEdgeFraction = hasLeash,
        };
    }

    /// <summary>
    /// GET view: every tunable with its canonical default, current effective
    /// value, override (null = default wins), previous value, revision, and
    /// when last observed. Integrates with the existing brain
    /// trace/diagnostics vocabulary (committed log detail + BrainInspect
    /// snapshot) — no parallel logger.
    /// </summary>
    public sealed record TuningFieldView(
        string Field,
        string DefaultValue,
        string EffectiveValue,
        string? OverrideValue,
        string? PreviousValue,
        long Revision,
        DateTime? ObservedAtUtc);

    public sealed record TuningView(
        long Revision,
        DateTime UpdatedAtUtc,
        IReadOnlyList<TuningFieldView> Fields);

    public static TuningView Snapshot()
    {
        var h = _current;
        var t = h.Tuning;
        (object? Prev, object? Cur, DateTime At) Get(string field)
        {
            lock (_historyLock)
                return _history.TryGetValue(field, out var e) ? e : (null, null, default);
        }
        static string Show(object? v) => v?.ToString() ?? "default";
        var e1 = Get("lootSafeRadiusM");
        var e2 = Get("giveUpAfterUnresolvedWakes");
        var e3 = Get("giveUpAfterPlanFailures");
        var e4 = Get("combatStallWakes");
        var e5 = Get("leashEdgeFraction");
        return new TuningView(h.Revision, h.UpdatedAtUtc,
        [
            new("lootSafeRadiusM",
                BotBrainTuning.DefaultLootSafeRadiusM.ToString("F1"),
                (t.LootSafeRadiusM ?? BotBrainTuning.DefaultLootSafeRadiusM).ToString("F1"),
                t.LootSafeRadiusM?.ToString("F1"), Show(e1.Prev), h.Revision,
                e1.At == default ? null : e1.At),
            new("giveUpAfterUnresolvedWakes",
                BotBrainTuning.DefaultGiveUpAfterUnresolvedWakes.ToString(),
                (t.GiveUpAfterUnresolvedWakes ?? BotBrainTuning.DefaultGiveUpAfterUnresolvedWakes).ToString(),
                t.GiveUpAfterUnresolvedWakes?.ToString(), Show(e2.Prev), h.Revision,
                e2.At == default ? null : e2.At),
            new("giveUpAfterPlanFailures",
                BotBrainTuning.DefaultGiveUpAfterPlanFailures.ToString(),
                (t.GiveUpAfterPlanFailures ?? BotBrainTuning.DefaultGiveUpAfterPlanFailures).ToString(),
                t.GiveUpAfterPlanFailures?.ToString(), Show(e3.Prev), h.Revision,
                e3.At == default ? null : e3.At),
            new("combatStallWakes",
                BotBrainTuning.DefaultCombatStallWakes.ToString(),
                (t.CombatStallWakes ?? BotBrainTuning.DefaultCombatStallWakes).ToString(),
                t.CombatStallWakes?.ToString(), Show(e4.Prev), h.Revision,
                e4.At == default ? null : e4.At),
            new("leashEdgeFraction",
                BotBrainTuning.DefaultLeashEdgeFraction.ToString("F2"),
                (t.LeashEdgeFraction ?? BotBrainTuning.DefaultLeashEdgeFraction).ToString("F2"),
                t.LeashEdgeFraction?.ToString("F2"), Show(e5.Prev), h.Revision,
                e5.At == default ? null : e5.At),
        ]);
    }

    /// <summary>
    /// Applies the current snapshot onto wake options: the single point where
    /// runtime overrides meet the canonical defaults. Null override = default
    /// wins, so behavior is byte-identical when no override is set.
    /// </summary>
    public static QuestDecisionScenario.QuestOptions ApplyTo(QuestDecisionScenario.QuestOptions options)
    {
        var t = _current.Tuning;
        if (t == BotBrainTuning.Empty)
            return options;
        return options with
        {
            LootSafeRadiusM = t.LootSafeRadiusM ?? options.LootSafeRadiusM,
            GiveUpAfterUnresolvedWakes = t.GiveUpAfterUnresolvedWakes ?? options.GiveUpAfterUnresolvedWakes,
            GiveUpAfterPlanFailures = t.GiveUpAfterPlanFailures ?? options.GiveUpAfterPlanFailures,
        };
    }

    /// <summary>Resolved combat-stall threshold for this wake (override or canonical).</summary>
    public static int ResolveCombatStallWakes() => _current.Tuning.CombatStallWakes ?? QuestBehavior.CombatStallWakes;

    /// <summary>Resolved leash-edge fraction for this wake (override or canonical).</summary>
    public static float ResolveLeashEdgeFraction() => _current.Tuning.LeashEdgeFraction ?? Combat.CombatBrainEngagement.LeashEdgeFraction;
}
