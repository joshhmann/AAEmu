using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// PACK-SALE-01 live-stack proof: a REAL game server boots, ONE bot is
/// provisioned through the scenario bridge, and the m8 economy cycle runs
/// with hauler=true — full craft → summon → board → load → drive →
/// unload → SELL-GOLD chain through the M5 contract ONLY. The SELL-GOLD
/// Completed stage + the payout-formula criterion (mail copper delta ==
/// round(base × ratio × 1.05), pack consumed) is the proof.
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class SpecialtyPackSaleE2eTests
{
    private const string TemplateName = "m8-economy-cycle-v0";
    private const string BotName = "M8PackSale";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PackSale_OnLiveServer_SellsAtGoldTraderWithFormulaPayout()
    {
        E2eStack.EnsureUp();

        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            "{\"cmd\":\"scenario\",\"template\":\"" + TemplateName + "\"," +
            "\"bot\":\"" + BotName + "\",\"fresh\":true,\"cycles\":1,\"hauler\":true}",
            timeoutMs: 600_000);

        var passed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
        var failure = response.TryGetProperty("failure", out var f) ? f.GetString() : "";
        var failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() : "";
        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";
        var criteria = response.TryGetProperty("criteria", out var cr) ? cr.ToString() : "[]";
        var stages = response.TryGetProperty("stages", out var st) ? st.ToString() : "[]";
        var rigNotes = response.TryGetProperty("rigNotes", out var rn) ? rn.ToString() : "[]";
        var trace = response.TryGetProperty("traceRecords", out var tr) ? tr.ToString() : "[]";

        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = TemplateName,
            verdict = passed ? "PASS" : "FAIL",
            failStage,
            failure,
            failReason,
            note = "one bot crafts a trade pack, drives it, and sells at the specialty gold trader with formula payout through the M5 contract; H (feel) stays UNKNOWN",
            stages = JsonDocument.Parse(stages).RootElement,
            criteria = JsonDocument.Parse(criteria).RootElement,
            rigNotes = JsonDocument.Parse(rigNotes).RootElement,
            trace_count = JsonDocument.Parse(trace).RootElement.GetArrayLength(),
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "specialty-pack-sale-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffset, "|FATAL|");
        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the pack-sale run");

        Assert.True(passed,
            $"pack sale failed at {failStage} ({failure}): {failReason}\n{evidence}");

        var stageNames = JsonDocument.Parse(stages).RootElement
            .EnumerateArray()
            .Select(s => s.TryGetProperty("Stage", out var sn) ? sn.GetString() ?? "" : "")
            .ToList();
        Assert.Contains(stageNames, n => n.StartsWith("PACK-CRAFT-", StringComparison.Ordinal));
        Assert.Contains(stageNames, n => n.StartsWith("SELL-GOLD-", StringComparison.Ordinal));

        var criterionVerdicts = JsonDocument.Parse(criteria).RootElement
            .EnumerateArray()
            .Select(c => (
                Name: c.TryGetProperty("Name", out var cn) ? cn.GetString() ?? "" : "",
                Passed: c.TryGetProperty("Passed", out var cp) && cp.GetBoolean()))
            .ToList();
        var payout = criterionVerdicts.FirstOrDefault(c => c.Name.StartsWith("haul-payout-formula-", StringComparison.Ordinal));
        Assert.True(payout.Name.StartsWith("haul-payout-formula-", StringComparison.Ordinal),
            "payout formula criterion missing\nEvidence:\n" + evidence);
        Assert.True(payout.Passed, "payout formula criterion failed\nEvidence:\n" + evidence);
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
