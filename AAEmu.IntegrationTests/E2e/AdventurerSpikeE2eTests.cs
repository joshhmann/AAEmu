using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M7 gating spike LIVE hook (ROADMAP M7: "a scoped spike — one adventurer
/// clearing a short quest chain end-to-end — gates scheduling"): a REAL
/// game server boots in the deployment-shaped testing environment (same
/// binaries, same MySQL, same config precedence), and one adventurer bot is
/// provisioned through the shared lifecycle (HeadlessSession.Provision via
/// the scenario bridge — real managed account + character row, level 10 Nuian,
/// isolated from the presence-demo Citizen bots), and
/// drives quest 250 (Solzreed fox cull) end-to-end through the M5
/// IGameplayActor contract ONLY:
///
///   accept at the notice-board doodad 5047 → real Move legs to the fox
///   cluster (the board at (15522.9, 15285.9) and the 10 fox spawners at
///   (15468-15594, 15212-15341) sit 30-110 m apart — straight-line lerp is
///   honest at this range; no preseeds needed) → hunt loop (Observe →
///   nearest attackable fox npc 3492 → SetTarget → Cast rotation, primary
///   18134 (3단 베기 finisher — area_radius 0; the first hit 18131 has a
///   live-verified AoE-selection engine gap, documented in
///   AdventurerSpikeScenario) → loot each corpse → auto-complete.
///
/// Unlike the rig (documented rig-faked damage), the kill here is REAL:
/// cast damage downs the fox through Npc.DoDie →
/// QuestManager.DoOnMonsterHuntEvents, and the quest completes through the
/// engine's own step machine.
///
/// The machine-readable PASS/FAIL report + trace evidence land in the E2E
/// logs (same evidence convention as the M3a/M4 economic replay hook).
/// H stays UNKNOWN: proxy/bot-functional evidence only — Josh's feel
/// verdicts are never derived from scripted actors.
/// </summary>
[Collection("e2e")]
public class AdventurerSpikeE2eTests
{
    private const string TemplateName = "adventurer-spike-fox";
    private const string SpikeBotName = "m7spikefox";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task AdventurerSpike_OnLiveServer_ClearsFoxCullEndToEnd()
    {
        E2eStack.EnsureUp();

        // Log-tail baseline: the unhandled-exception scan covers only what
        // the spike's run appends.
        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\",\"bot\":\"{SpikeBotName}\",\"fresh\":true}}",
            timeoutMs: 420_000); // two ~100 m walk legs + 3 real kills (roaming prey, melee chase) on the live world

        var passed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
        var failure = response.TryGetProperty("failure", out var f) ? f.GetString() : "";
        var failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() : "";
        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";
        var criteria = response.TryGetProperty("criteria", out var cr) ? cr.ToString() : "[]";
        var stages = response.TryGetProperty("stages", out var st) ? st.ToString() : "[]";
        var rigNotes = response.TryGetProperty("rigNotes", out var rn) ? rn.ToString() : "[]";
        var trace = response.TryGetProperty("traceRecords", out var tr) ? tr.ToString() : "[]";

        // Machine-readable report + trace evidence into the E2E logs (the
        // gate evidence convention — same shape as the M3a/M4 replay hook).
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = TemplateName,
            milestone = "M7 gating spike",
            verdict = passed ? "PASS" : "FAIL",
            failStage,
            failure,
            failReason,
            quest = 250,
            note = "one adventurer clears the Solzreed fox cull end-to-end through the M5 contract; real kill damage (no rig fake) — H (feel) stays UNKNOWN",
            stages = JsonDocument.Parse(stages).RootElement,
            criteria = JsonDocument.Parse(criteria).RootElement,
            rigNotes = JsonDocument.Parse(rigNotes).RootElement,
            trace_count = JsonDocument.Parse(trace).RootElement.GetArrayLength(),
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "m7-adventurer-spike-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var tracePath = Path.Combine(EvidenceDir, "m7-adventurer-spike-trace.jsonl");
        var traceLines = JsonDocument.Parse(trace).RootElement
            .EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .ToList();
        await File.WriteAllLinesAsync(tracePath, traceLines);

        // No unhandled exceptions in the game log tail the run appended.
        var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffset, "|FATAL|");

        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the spike run");
        Assert.True(passed,
            $"M7 adventurer spike FAIL at {failStage} ({failure}): {failReason}\nEvidence:\n{evidence}\nReport: {reportPath}");
    }

    /// <summary>Counts marker lines in the game-log bytes appended since <paramref name="startOffset"/>.</summary>
    private static int CountLogTailMatches(long startOffset, string marker)
    {
        try
        {
            if (!File.Exists(GameLogPath))
                return 0;
            using var fs = File.OpenRead(GameLogPath);
            if (fs.Length <= startOffset)
                return 0;
            fs.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var count = 0;
            while (reader.ReadLine() is { } line)
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    count++;
            return count;
        }
        catch (IOException)
        {
            return 0; // log rotated/locked mid-run — the scenario verdict is the primary signal
        }
    }
}
