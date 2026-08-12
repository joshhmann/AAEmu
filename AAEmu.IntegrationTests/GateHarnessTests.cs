using AAEmu.IntegrationTests.E2e;
using AAEmu.IntegrationTests.E2e.Gate;
using Xunit;

namespace AAEmu.IntegrationTests;

/// <summary>
/// Gate harness test entry points (ARCHITECTURE_REVIEW deliverable 8/10,
/// slice #10 — P2). Each Fact is one density-gate stage:
///
///   Stage 10 — correctness: 10 bots, full golden route + budget checks.
///   Stage 25 — FIRST STABILITY GATE: 25 bots, hard-stops if H2 not merged.
///   Stage 50 — soak: 50 bots, ≥6h window (GATE_SOAK_MINUTES overrides for
///              smoke runs; the real 6h gate is `GATE_SOAK_MINUTES=360`).
///
/// All stages share the e2e collection: serialized with the M2b suite, one
/// stack, no parallel boots. A red budget verdict fails the stage hard.
/// </summary>
[Collection("e2e")]
public class GateHarnessTests
{
    /// <summary>Stage 1 — 10 bots correctness. This is the P2 completion gate.</summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task Gate_Stage10_Correctness_Green()
    {
        var result = await GateSoakRunner.RunStageAsync(GateStages.Stage10);
        Assert.True(result.Passed, result.Detail + "\nEvidence: " + result.EvidencePath);
    }

    /// <summary>Stage 2 — 25 bots first stability gate (H2-gated).</summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task Gate_Stage25_FirstStabilityGate()
    {
        var result = await GateSoakRunner.RunStageAsync(GateStages.Stage25);
        Assert.True(result.Passed, result.Detail + "\nEvidence: " + result.EvidencePath);
    }

    /// <summary>
    /// M3b gate-scale save budget — 25 bots + TWO seeded homesteads, autosave
    /// p95 &lt; 2s enforced (ROADMAP M3b). The homestead seed is applied before
    /// the window so the save path carries real property state.
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task Gate_Stage25_HomesteadSaveBudget()
    {
        var result = await GateSoakRunner.RunStageAsync(GateStages.Stage25Homesteads);
        Assert.True(result.Passed, result.Detail + "\nEvidence: " + result.EvidencePath);
    }

    /// <summary>
    /// Stage 3 — 50 bots soak. Default window ≥6h (deliverable 8 stage 3).
    /// Override with GATE_SOAK_MINUTES for smoke runs (e.g. =5). Skipped
    /// unless explicitly requested: a full 6h soak is a scheduled gate run,
    /// not something a plain suite invocation should block on.
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task Gate_Stage50_Soak()
    {
        var soakEnv = Environment.GetEnvironmentVariable("GATE_SOAK_MINUTES");
        if (string.IsNullOrWhiteSpace(soakEnv))
        {
            Assert.Skip("GATE_SOAK_MINUTES not set — stage 50 soak (≥6h) is an explicit gate run. " +
                        "Set GATE_SOAK_MINUTES=360 for the real soak or a small value for a smoke run.");
            return;
        }

        if (!int.TryParse(soakEnv, out var soakMinutes) || soakMinutes <= 0)
            throw new InvalidOperationException($"GATE_SOAK_MINUTES must be a positive integer, got '{soakEnv}'");

        var stage = GateStages.Stage50 with { SoakMinutes = soakMinutes };
        var result = await GateSoakRunner.RunStageAsync(stage);
        Assert.True(result.Passed, result.Detail + "\nEvidence: " + result.EvidencePath);
    }

    /// <summary>
    /// Stage 10 as a soak window (10 bots — the M6 exit-record shape). The
    /// correctness stage's 3-min window cannot pass judgment on rare
    /// warning-rate budgets (physics slow-thread warnings run ~0.03/min:
    /// P(0 warnings | 3 min) ≈ 91%, P(0 | 360 min) ≈ 1.6e-5 — a 6h window
    /// only passes with the fix in place, not by luck). GATE_SOAK_MINUTES
    /// runs the same 10-bot stack over a longer window (360 = full M6
    /// re-soak). Skipped unless explicitly requested, like stage 50.
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task Gate_Stage10_Soak()
    {
        var soakEnv = Environment.GetEnvironmentVariable("GATE_SOAK_MINUTES");
        if (string.IsNullOrWhiteSpace(soakEnv))
        {
            Assert.Skip("GATE_SOAK_MINUTES not set — stage-10 soak is an explicit gate run.");
            return;
        }

        if (!int.TryParse(soakEnv, out var soakMinutes) || soakMinutes <= 0)
            throw new InvalidOperationException($"GATE_SOAK_MINUTES must be a positive integer, got '{soakEnv}'");

        var stage = GateStages.Stage10 with { Name = "10-soak", SoakMinutes = soakMinutes, Budgets = GateStages.SoakBudgets };
        var result = await GateSoakRunner.RunStageAsync(stage);
        Assert.True(result.Passed, result.Detail + "\nEvidence: " + result.EvidencePath);
    }
}
