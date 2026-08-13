using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// FIRST CONSUMER (t_52b2b084) — the Lane D auction-house conservation
/// scenario run headless against the REAL stack via the bridge `scenario`
/// cmd (the same channel the gate harness uses for template stages): zero
/// human in the loop. The server provisions the fleet, drives every actor
/// through the IGameplayActor contract actions only (PostAuction /
/// BuyAuction), asserts conservation (items/currency, documented engine
/// lifecycle correctness from the trace records, and returns
/// the structured verdict. This test asserts the verdict and writes the
/// machine-readable report + per-action trace evidence into the workspace —
/// the trace is emitted directly by THIS test from the response's audit
/// records (real server timestamps), never transcribed from the evidence
/// block (evidence hygiene t_6e2725b5).
///
/// Density: runs at the highest GREEN stage — Stage25 (25 actors, H2
/// landed). Stage50 (50 actors) is the ≥6h soak gate (GATE_SOAK_MINUTES),
/// a scheduled run — recorded as the ceiling, not exceeded here.
/// </summary>
[Collection("e2e")]
public class LaneDAuctionHouseE2eTests
{
    private const string TemplateName = "ah-conservation";
    private const string FleetBotBase = "ahfleet";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task LaneD_AuctionHouse_Conservation_HeadlessPass()
    {
        // The scenario is server-side; the stack needs the bridge only (no
        // bot-control token — the bridge is the internal harness surface).
        E2eStack.EnsureUp();

        // Fleet density: highest green stage with H2 landed (Stage25).
        var fleetSize = Environment.GetEnvironmentVariable("AUCTION_FLEET_SIZE");
        if (!int.TryParse(fleetSize, out var n) || n < 2)
            n = 25;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\",\"bot\":\"{FleetBotBase}\",\"fresh\":true}}");

        var passed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
        var failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() : "";
        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";
        var criteria = response.TryGetProperty("criteria", out var cr) ? cr.ToString() : "[]";
        var trace = response.TryGetProperty("trace", out var tr) && tr.ValueKind == JsonValueKind.Array
            ? tr
            : default;

        // Machine-readable report + trace evidence into the workspace + E2E
        // logs (the gate evidence convention).
        Directory.CreateDirectory(EvidenceDir);
        var tracePath = Path.Combine(EvidenceDir, "lane-d-auction-house-trace.jsonl");
        var report = new
        {
            scenario = TemplateName,
            verdict = passed ? "PASS" : "FAIL",
            failStage,
            failReason,
            density_stage = n,
            density_ceiling_note = "Stage50 (50 actors) requires the >=6h soak gate (GATE_SOAK_MINUTES=360); ran at Stage25 (H2 landed)",
            criteria = JsonDocument.Parse(criteria).RootElement,
            trace_records = trace.ValueKind == JsonValueKind.Array ? trace.GetArrayLength() : 0,
            trace_path = tracePath,
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "lane-d-auction-house-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        // Per-action trace evidence, written by THIS test (evidence hygiene
        // t_6e2725b5) — one JSON line per audit record, in execution order,
        // carrying the M5 contract shape (ActorAuditRecord.ToJson) with real
        // server timestamps (requested_at/started_at/completed_at). No
        // worker-side transcription of the evidence block: the artifact is
        // the timestamped record set the server actually observed. Written
        // even on FAIL — a failed run's trace is the most valuable evidence.
        if (trace.ValueKind == JsonValueKind.Array)
        {
            await File.WriteAllLinesAsync(tracePath,
                trace.EnumerateArray().Select(e => e.ToString()));
        }
        else
        {
            await File.WriteAllTextAsync(tracePath,
                "{\"error\":\"trace array missing from scenario response (server build predates t_6e2725b5)\"}\n");
        }

        Assert.True(passed, $"auction-house scenario FAIL at {failStage}: {failReason}\nEvidence:\n{evidence}\nReport: {reportPath}\nTrace: {tracePath}");
    }
}
