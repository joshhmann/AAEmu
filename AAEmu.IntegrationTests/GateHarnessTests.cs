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
}
