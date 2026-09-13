using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// TRADE-01 live-stack proof: a REAL game server boots, TWO bots are
/// provisioned through the scenario bridge, and the trade-handshake
/// scenario drives offer → put-up → lock+ok on both sides through the M5
/// IGameplayActor contract ONLY, with item conservation as the proof.
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class DirectTradeE2eTests
{
    private const string TemplateName = "trade-handshake";
    private const string AliceBotName = "TradeAlice";
    private const string BobBotName = "TradeBob";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task TradeHandshake_OnLiveServer_ItemsSwapWithConservation()
    {
        E2eStack.EnsureUp();

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            "{\"cmd\":\"scenario\",\"template\":\"" + TemplateName + "\"," +
            "\"alice\":\"" + AliceBotName + "\",\"bob\":\"" + BobBotName + "\"," +
            "\"itemTemplate\":7992,\"itemCount\":1}",
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
            note = "two bots trade one potato through the M5 contract with conservation; H (feel) stays UNKNOWN",
            stages = JsonDocument.Parse(stages).RootElement,
            criteria = JsonDocument.Parse(criteria).RootElement,
            trace_count = JsonDocument.Parse(trace).RootElement.GetArrayLength(),
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "trade-handshake-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffset, "|FATAL|");
        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the trade handshake run");

        Assert.True(passed,
            $"trade handshake failed at {failStage} ({failure}): {failReason}\n{evidence}");
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
