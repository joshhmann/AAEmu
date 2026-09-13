using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// EXPEDITION-01 live-stack proof: a REAL game server boots, FIVE bots are
/// provisioned through the scenario bridge, and the expedition-formation
/// scenario drives party invite/accept ×4 → ExpeditionCreate through the
/// M5 IGameplayActor contract ONLY. Shared expedition membership rows on
/// all five bots are the proof.
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class ExpeditionFlowE2eTests
{
    private const string TemplateName = "expedition-formation";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ExpeditionFormation_OnLiveServer_PartyOfFiveSharesMembership()
    {
        E2eStack.EnsureUp();

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        // Unique expedition name per run (NameRegex allows letters/spaces
        // only; the expedition row persists in MySQL and duplicate names
        // are refused by the engine).
        var expeditionName = "ExpLive " + Guid.NewGuid().ToString("N")[..8]
            .Select(c => char.IsDigit(c) ? (char)('A' + (c - '0')) : char.ToUpperInvariant(c))
            .Aggregate("", (a, c) => a + c);
        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            "{\"cmd\":\"scenario\",\"template\":\"" + TemplateName + "\"," +
            "\"bots\":[\"ExpA\",\"ExpB\",\"ExpC\",\"ExpD\",\"ExpE\"]," +
            "\"name\":\"" + expeditionName + "\"}",
            timeoutMs: 300_000);

        var passed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
        var failure = response.TryGetProperty("failure", out var f) ? f.GetString() : "";
        var failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() : "";
        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";
        var criteria = response.TryGetProperty("criteria", out var cr) ? cr.ToString() : "[]";
        var stages = response.TryGetProperty("stages", out var st) ? st.ToString() : "[]";
        var trace = response.TryGetProperty("traceRecords", out var tr) ? tr.ToString() : "[]";

        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = TemplateName,
            verdict = passed ? "PASS" : "FAIL",
            failStage,
            failure,
            failReason,
            note = "five bots form a party and found an expedition through the M5 contract with shared membership; H (feel) stays UNKNOWN",
            stages = JsonDocument.Parse(stages).RootElement,
            criteria = JsonDocument.Parse(criteria).RootElement,
            trace_count = JsonDocument.Parse(trace).RootElement.GetArrayLength(),
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "expedition-formation-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffset, "|FATAL|");
        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the expedition run");

        Assert.True(passed,
            $"expedition formation failed at {failStage} ({failure}): {failReason}\n{evidence}");

        var characters = response.GetProperty("characters");
        Assert.True(characters.GetArrayLength() == 5,
            $"expected 5 characters in the payload, got {characters.GetArrayLength()}");
        var expeditionId = response.GetProperty("expeditionId").GetUInt32();
        Assert.True(expeditionId != 0, "expeditionId must be nonzero after the run");
    }

    private static int CountLogTailMatches(long startOffset, string marker)
    {
        if (!File.Exists(GameLogPath))
            return 0;
        var count = 0;
        using var stream = new FileStream(GameLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
                count++;
        }
        return count;
    }
}
