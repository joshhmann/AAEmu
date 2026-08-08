using AAEmu.Commons.Utils.Gate;

namespace AAEmu.IntegrationTests.E2e.Gate;

/// <summary>
/// One density-gate stage (ARCHITECTURE_REVIEW deliverable 8 table).
/// Stages are the ONLY place bot counts, windows and budgets live — adding a
/// stage is adding one config row (see README.md "How to add a stage").
/// </summary>
public sealed record GateStageConfig
{
    public required string Name { get; init; }
    public required int BotCount { get; init; }

    /// <summary>Stage 25 (first stability gate) hard-stops when H2 is not in the server build.</summary>
    public bool RequireH2 { get; init; }

    /// <summary>Window length for budget sampling (soak uses <see cref="SoakMinutes"/> instead).</summary>
    public int WindowMinutes { get; init; } = 3;

    /// <summary>Soak window in minutes (stage 50: ≥360 per deliverable 8; overridable for smoke runs).</summary>
    public int SoakMinutes { get; init; } = 0;

    /// <summary>Number of golden-route quests each bot drives (0 = enter-world only).</summary>
    public int QuestSubset { get; init; }

    public required GateBudgets Budgets { get; init; }
}

/// <summary>Result of one stage run, with the evidence file path.</summary>
public sealed record GateStageResult(
    string StageName,
    bool Passed,
    TimeSpan Window,
    IReadOnlyList<BudgetVerdict> Verdicts,
    IReadOnlyList<string> Failures,
    string EvidencePath,
    string Detail);

/// <summary>
/// The deliverable-8 stage ladder for this card: 10 correctness → 25 first
/// stability gate (H2-gated) → 50 soak (≥6h). Budgets per stage.
/// </summary>
public static class GateStages
{
    /// <summary>Stage 1 — 10 bots: correctness (golden route) + budgets.</summary>
    public static GateStageConfig Stage10 { get; } = new()
    {
        Name = "10-correctness",
        BotCount = 10,
        RequireH2 = false,
        WindowMinutes = 3,
        QuestSubset = 16, // full golden route
        Budgets = new GateBudgets()
    };

    /// <summary>Stage 2 — 25 bots: FIRST STABILITY GATE, hard stop until H2 lands.</summary>
    public static GateStageConfig Stage25 { get; } = new()
    {
        Name = "25-stability",
        BotCount = 25,
        RequireH2 = true,
        WindowMinutes = 3,
        QuestSubset = 4, // stability focus — correctness is stage 10's job
        Budgets = new GateBudgets()
    };

    /// <summary>Stage 3 — 50 bots: soak ≥6h (override with GATE_SOAK_MINUTES).</summary>
    public static GateStageConfig Stage50 { get; } = new()
    {
        Name = "50-soak",
        BotCount = 50,
        RequireH2 = true,
        SoakMinutes = 360,
        QuestSubset = 2, // light activity — the window is the test
        Budgets = new GateBudgets()
    };
}
