#nullable enable

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Runtime PlayerBot brain tuning: one process-wide snapshot layered over the
/// canonical brain defaults. Every field is a nullable override — null means
/// "no override, the canonical default wins" — so a default-constructed
/// snapshot is byte-identical to no tuning at all.
///
/// Sourced from the real field names in the code (no invented knobs):
/// <list type="bullet">
/// <item><c>LootSafeRadiusM</c> — <see cref="Loot.LootBrain.DefaultSafeRadiusM"/> (15 m)
///   via <c>QuestOptions.LootSafeRadiusM</c>.</item>
/// <item><c>GiveUpAfterUnresolvedWakes</c> / <c>GiveUpAfterPlanFailures</c> —
///   <c>QuestOptions</c> give-up rule (default 10 each).</item>
/// <item><c>CombatStallWakes</c> — <see cref="QuestBehavior.CombatStallWakes"/> (10):
///   stall-trip and leash-stale watchdog threshold.</item>
/// <item><c>LeashEdgeFraction</c> —
///   <see cref="Combat.CombatBrainEngagement.LeashEdgeFraction"/> (0.9).</item>
/// </list>
///
/// Threading: a single volatile holder swapped atomically per POST, read
/// lock-free per wake. No restart, no Login/Game/bot/quest state touched.
/// </summary>
public sealed record BotBrainTuning(
    float? LootSafeRadiusM,
    int? GiveUpAfterUnresolvedWakes,
    int? GiveUpAfterPlanFailures,
    int? CombatStallWakes,
    float? LeashEdgeFraction)
{
    /// <summary>Canonical defaults, from the same constants the brains quote.</summary>
    public static float DefaultLootSafeRadiusM => Loot.LootBrain.DefaultSafeRadiusM;
    public const int DefaultGiveUpAfterUnresolvedWakes = 10;
    public const int DefaultGiveUpAfterPlanFailures = 10;
    public const int DefaultCombatStallWakes = 10;
    public static float DefaultLeashEdgeFraction => Combat.CombatBrainEngagement.LeashEdgeFraction;

    /// <summary>Bounds for POST validation (fail-closed: outside → 400, never silent).</summary>
    public const float MinLootSafeRadiusM = 0.5f;
    public const float MaxLootSafeRadiusM = 100f;
    public const int MinWakeThreshold = 0;
    public const int MaxWakeThreshold = 1000;
    public const float MinLeashEdgeFraction = 0.1f;
    public const float MaxLeashEdgeFraction = 1.0f;

    /// <summary>The no-override snapshot: every default wins.</summary>
    public static BotBrainTuning Empty { get; } = new(null, null, null, null, null);

    /// <summary>
    /// Validates one POST body. Returns the error message, or null when every
    /// PRESENT field is in bounds (absent = no change, never an error).
    /// NaN/negative-zero and out-of-range values are rejected, never coerced.
    /// </summary>
    public static string? Validate(BotBrainTuningPatch patch)
    {
        if (patch.LootSafeRadiusM is { } safe
            && (float.IsNaN(safe) || float.IsInfinity(safe)
                || safe < MinLootSafeRadiusM || safe > MaxLootSafeRadiusM))
            return $"lootSafeRadiusM must be within [{MinLootSafeRadiusM}, {MaxLootSafeRadiusM}]";
        if (patch.GiveUpAfterUnresolvedWakes is { } gu
            && (gu < MinWakeThreshold || gu > MaxWakeThreshold))
            return $"giveUpAfterUnresolvedWakes must be within [{MinWakeThreshold}, {MaxWakeThreshold}]";
        if (patch.GiveUpAfterPlanFailures is { } gp
            && (gp < MinWakeThreshold || gp > MaxWakeThreshold))
            return $"giveUpAfterPlanFailures must be within [{MinWakeThreshold}, {MaxWakeThreshold}]";
        if (patch.CombatStallWakes is { } stall
            && (stall < MinWakeThreshold || stall > MaxWakeThreshold))
            return $"combatStallWakes must be within [{MinWakeThreshold}, {MaxWakeThreshold}]";
        if (patch.LeashEdgeFraction is { } leash
            && (float.IsNaN(leash) || float.IsInfinity(leash)
                || leash < MinLeashEdgeFraction || leash > MaxLeashEdgeFraction))
            return $"leashEdgeFraction must be within [{MinLeashEdgeFraction}, {MaxLeashEdgeFraction}]";
        return null;
    }

    /// <summary>Resolved loot safe radius: the override, or the canonical default.</summary>
    public float ResolveLootSafeRadiusM(float canonical) => LootSafeRadiusM ?? canonical;
    /// <summary>Resolved give-up threshold: the override, or the canonical default.</summary>
    public int ResolveGiveUpAfterUnresolvedWakes(int canonical) => GiveUpAfterUnresolvedWakes ?? canonical;
    /// <summary>Resolved plan-failure threshold: the override, or the canonical default.</summary>
    public int ResolveGiveUpAfterPlanFailures(int canonical) => GiveUpAfterPlanFailures ?? canonical;
    /// <summary>Resolved stall threshold: the override, or the canonical default.</summary>
    public int ResolveCombatStallWakes(int canonical) => CombatStallWakes ?? canonical;
    /// <summary>Resolved leash fraction: the override, or the canonical default.</summary>
    public float ResolveLeashEdgeFraction(float canonical) => LeashEdgeFraction ?? canonical;
}

/// <summary>
/// POST /api/bots/tuning body: partial update — every field optional, only
/// present fields change; explicit null CLEARS that override back to default.
/// </summary>
public sealed record BotBrainTuningPatch(
    float? LootSafeRadiusM,
    int? GiveUpAfterUnresolvedWakes,
    int? GiveUpAfterPlanFailures,
    int? CombatStallWakes,
    float? LeashEdgeFraction)
{
    /// <summary>True when the JSON body carried the property at all (vs omitted).</summary>
    public bool HasLootSafeRadiusM { get; init; }
    public bool HasGiveUpAfterUnresolvedWakes { get; init; }
    public bool HasGiveUpAfterPlanFailures { get; init; }
    public bool HasCombatStallWakes { get; init; }
    public bool HasLeashEdgeFraction { get; init; }
}
