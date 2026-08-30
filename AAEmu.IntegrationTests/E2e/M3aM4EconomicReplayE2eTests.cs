using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// BACKTRACK Phase 2 (t_b4f455b0) — M3a/M4 economic replay LIVE hook: a
/// REAL game server boots in the deployment-shaped testing environment (same
/// binaries, same MySQL, same config precedence), a bot is provisioned through
/// the shared lifecycle, and the m3a-m4-replay scenario drives the curated
/// M3a contract + M4 economic/navigation route through the M5.1 + B1 CONTRACT
/// ACTIONS ONLY on the live world:
///
///   farm (Buy seeds → Plant → growth → Harvest) → craft (pack recipe
///   5403) → pack (PutDown → PackPickup) → vehicle (UseItem farm-wagon
///   summon scroll → BoardVehicle → LoadPackOntoVehicle → DriveVehicle →
///   UnboardVehicle) → bank (Deposit/Withdraw round trips) → trade (Sell).
///
/// Every economy event must complete as an actor-contract request (the
/// scenario refuses non-Completed events with their §17 reason), and the
/// conservation criteria (pack instance, seeds, currency, labor, lifecycle)
/// must all pass. The E2E stack boosts World.GrowthRate so the crop cycle
/// completes within the scenario's maturity timeout (player-facing production
/// rates would take hours — out of scope for a scripted replay), and
/// the actability-gated crop loot (the 1.2 emulator's actability multiplier
/// is flat 1.0, leaving the millet-material/seed groups at ~3.6% rolls for
/// any character) yields the craft materials a leveled farmer would get.
///
/// This test writes the machine-readable PASS/FAIL report + trace evidence
/// into the E2E logs (the same evidence convention as the M1M2 contract
/// replay). H stays UNKNOWN: proxy/bot-functional evidence only — Josh's
/// feel verdicts are never derived from scripted actors.
/// </summary>
[Collection("e2e")]
public class M3aM4EconomicReplayE2eTests
{
    private const string TemplateName = "m3a-m4-replay";
    private const string ReplayBotName = "m3a4replay";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task M3aM4EconomicReplay_OnLiveServer_PassesConservationAndLifecycle()
    {
        E2eStack.EnsureUp();

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\",\"bot\":\"{ReplayBotName}\",\"fresh\":true}}",
            timeoutMs: 420_000); // farm growth + full route on the live world

        var passed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
        var failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() : "";
        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";
        var criteria = response.TryGetProperty("criteria", out var cr) ? cr.ToString() : "[]";
        var stages = response.TryGetProperty("stages", out var st) ? st.ToString() : "[]";
        var rigNotes = response.TryGetProperty("rigNotes", out var rn) ? rn.ToString() : "[]";
        var trace = response.TryGetProperty("traceRecords", out var tr) ? tr.ToString() : "[]";

        // Machine-readable report + trace evidence into the E2E logs (the
        // gate evidence convention — same shape as the M1M2 replay hook).
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = TemplateName,
            card = "t_b4f455b0",
            verdict = passed ? "PASS" : "FAIL",
            failStage,
            failReason,
            proxy_note = "scripted-actor / bot-functional evidence — H (feel) stays UNKNOWN until Josh runs the route; never recorded as H=2",
            stages = JsonDocument.Parse(stages).RootElement,
            criteria = JsonDocument.Parse(criteria).RootElement,
            rigNotes = JsonDocument.Parse(rigNotes).RootElement,
            trace_count = JsonDocument.Parse(trace).RootElement.GetArrayLength(),
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "m3a-m4-economic-replay-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var tracePath = Path.Combine(EvidenceDir, "m3a-m4-economic-replay-trace.jsonl");
        var traceLines = JsonDocument.Parse(trace).RootElement
            .EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .ToList();
        await File.WriteAllLinesAsync(tracePath, traceLines);

        Assert.True(passed,
            $"M3a/M4 economic replay FAIL at {failStage}: {failReason}\nEvidence:\n{evidence}\nReport: {reportPath}");
    }
}
