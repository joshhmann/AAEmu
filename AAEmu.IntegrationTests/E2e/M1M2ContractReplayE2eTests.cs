using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// BACKTRACK Phase 1 (t_61a0eebb) — the M1 route + M2 baseline contract
/// replay run headless against the REAL stack via the bridge `scenario` cmd
/// (the same channel the gate harness uses for template stages): zero human
/// in the loop. The server provisions a real bot (production
/// HeadlessSession path) and drives the curated Solzreed golden route (16
/// quests through the first-mount chain) through IGameplayActor CONTRACT
/// ACTIONS ONLY (accept_quest/advance_quest/use_item/turn_in_quest/
/// auto_turn_in/mount), asserting completion, item conservation and
/// lifecycle correctness from the audit trace records. This test asserts
/// the verdict and writes the machine-readable report + trace evidence into
/// the workspace + E2E logs.
///
/// H stays UNKNOWN: this is proxy/bot-functional evidence — Josh's feel
/// verdicts are never derived from scripted actors.
/// </summary>
[Collection("e2e")]
public class M1M2ContractReplayE2eTests
{
    private const string TemplateName = "m1m2-replay";
    private const string ReplayBotName = "m1m2replay";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task M1M2ContractReplay_HeadlessPass_WithTraceEvidence()
    {
        E2eStack.EnsureUp();

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\",\"bot\":\"{ReplayBotName}\",\"fresh\":true}}",
            timeoutMs: 300_000); // the full 16-quest route with real NPC
                                  // resolution (spawn polls per turn-in
                                  // target) runs several minutes; the 30s
                                  // bridge default is for single ops

        var passed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
        var failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() : "";
        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";
        var criteria = response.TryGetProperty("criteria", out var cr) ? cr.ToString() : "[]";
        var stages = response.TryGetProperty("stages", out var st) ? st.ToString() : "[]";
        var rigNotes = response.TryGetProperty("rigNotes", out var rn) ? rn.ToString() : "[]";
        var trace = response.TryGetProperty("traceRecords", out var tr) ? tr.ToString() : "[]";

        // Machine-readable report + trace evidence into the workspace +
        // E2E logs (the gate evidence convention).
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = TemplateName,
            card = "t_61a0eebb",
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
        var reportPath = Path.Combine(EvidenceDir, "m1m2-contract-replay-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        // Trace evidence: one audit record per line (JSONL).
        var tracePath = Path.Combine(EvidenceDir, "m1m2-contract-replay-trace.jsonl");
        var traceLines = JsonDocument.Parse(trace).RootElement
            .EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .ToList();
        await File.WriteAllLinesAsync(tracePath, traceLines);

        Assert.True(passed, $"M1/M2 contract replay FAIL at {failStage}: {failReason}\nEvidence:\n{evidence}\nReport: {reportPath}");
    }
}
