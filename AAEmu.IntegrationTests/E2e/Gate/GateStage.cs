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

    /// <summary>
    /// Scenario templates (P1 t_5efae4f1) run against the live stack as part
    /// of this stage — each template provisions a real bot server-side
    /// (HeadlessSession.Provision), drives its quest scenario through the
    /// IGameplayActor contract, and must PASS. Staged soaks run templates
    /// too (default: the full library on every stage).
    /// </summary>
    public string[] ScenarioTemplates { get; init; } = [];

    /// <summary>
    /// Number of homesteads (housings rows) to seed into the stack before the
    /// game boots, so the autosave budget measures a world that contains real
    /// homesteads (M3b gate-scale scenario: two homesteads + N bots embodied).
    /// </summary>
    public int SeedHomesteads { get; init; }

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
    /// <summary>
    /// Budgets for the 10-bot IDLE soak (the M6 exit-record shape, t_18fccd09).
    /// The idle world's tick/region threads do no work, so the strict H2
    /// density budgets (100ms ceiling, zero tolerance) false-RED on a single
    /// host-jitter deschedule over a 6h window: the 2026-08-11 360-min re-soak
    /// measured ONE 105ms region pass + 2 "over 100ms budget" warnings, all
    /// inside a single 76s provisioning/GC pause storm (deferred 0 characters,
    /// thread recovered, 5h50m clean after). Soak keeps the recalibrated
    /// warning-rate budgets (physics 0.1/min) but widens the region-tick
    /// ceiling to 200ms (~1.9× over the measured 105ms) and the tick-overrun
    /// rate to 0.1/min (18× over the measured 0.0056/min). LOAD stages
    /// (Stage25/Stage50) keep the strict budgets — a real region overload
    /// fires many overrun warnings per minute and still trips.
    /// </summary>
    public static GateBudgets SoakBudgets { get; } = new()
    {
        RegionTickMaxElapsedMs = 200,
        MaxTickOverrunWarningsPerMin = 0.1
    };

    /// <summary>Stage 1 — 10 bots: correctness (golden route) + budgets.</summary>
    public static GateStageConfig Stage10 { get; } = new()
    {
        Name = "10-correctness",
        BotCount = 10,
        RequireH2 = false,
        WindowMinutes = 3,
        QuestSubset = 16, // full golden route
        ScenarioTemplates = ["level22-gate", "ability-gate", "cat34-daily", "ah-conservation"],
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
        ScenarioTemplates = ["level22-gate", "ability-gate", "cat34-daily", "ah-conservation"],
        Budgets = new GateBudgets()
    };

    /// <summary>
    /// Stage 2-homestead — the M3b gate-scale save budget: 25 bots embodied
    /// with TWO seeded homesteads in the world, enforcing the autosave p95
    /// &lt; 2s budget (ROADMAP M3b). Same density as stage 25 plus homestead
    /// state; the exit scenario (t_accb1c63) re-runs this after the full
    /// place → decorate → plant → harvest → restart cycles.
    /// </summary>
    public static GateStageConfig Stage25Homesteads { get; } = new()
    {
        Name = "25-homesteads-save-budget",
        BotCount = 25,
        RequireH2 = true,
        WindowMinutes = 3,
        QuestSubset = 4,
        SeedHomesteads = 2,
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
        ScenarioTemplates = ["level22-gate", "ability-gate", "cat34-daily", "ah-conservation"],
        Budgets = new GateBudgets()
    };
}
