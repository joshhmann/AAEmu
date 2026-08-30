using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M7 party spike LIVE hook: a REAL game server boots in the
/// deployment-shaped testing environment (same binaries, same MySQL, same
/// config precedence), THREE bots are provisioned through the shared lifecycle
/// (HeadlessSession.Provision via the scenario bridge — real managed accounts +
/// character rows), and the m7-party-spike scenario drives a REAL party of
/// three through one elite group encounter end-to-end via the M5
/// IGameplayActor contract ONLY:
///
///   party invite/accept ×2 (real TeamManager party) → RALLY (members close
///   formation on the leader, interleaved move legs) → ENGAGE + assist
///   (shared target) → COORDINATED HUNT on elite npc 1870 (level 13 Strong:
///   per-member sustain with the verified direct-heal potion 8518,
///   standoff-band maintenance, 18131-led burst rotation on the SHARED
///   target) → kill inside its leash-reset window.
///
/// The kill is REAL: cast damage downs the elite through Npc.DoDie — no rig
/// fake. The machine-readable PASS/FAIL report lands in the E2E logs (same
/// evidence convention as the other M7 hooks). H stays UNKNOWN:
/// proxy/bot-functional evidence only.
/// </summary>
[Collection("e2e")]
public class PartySpikeE2eTests
{
    private const string TemplateName = "m7-party-spike";
    // Hyphen-free: NameManager rejects '-' in character names (InvalidCharacters).
    private const string LeaderBotName = "M7PsLeader";
    private const string Member1BotName = "M7PsMember1";
    private const string Member2BotName = "M7PsMember2";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PartySpike_OnLiveServer_PartyOfThreeKillsEliteEndToEnd()
    {
        E2eStack.EnsureUp();

        // Log-tail baseline: the unhandled-exception scan covers only what
        // the run appends.
        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\"," +
            $"\"leader\":\"{LeaderBotName}\",\"member1\":\"{Member1BotName}\",\"member2\":\"{Member2BotName}\"," +
            "\"npc\":1870,\"followDistance\":3.0,\"moveSpeed\":5.0,\"moveTimeoutSeconds\":30," +
            "\"sustainThreshold\":0.35,\"resumeThreshold\":0.8,\"maxHuntRounds\":150}",
            timeoutMs: 420_000); // provisions 3 bots + spawns an elite + pumps rally legs + a real coordinated kill

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
        // gate evidence convention — same shape as the other M7 hooks).
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = TemplateName,
            milestone = "M7 party spike",
            verdict = passed ? "PASS" : "FAIL",
            failStage,
            failure,
            failReason,
            encounterNpc = 1870,
            note = "a real party of three rallies, assists, and downs elite npc 1870 inside its leash-reset window through the M5 contract; real kill damage (no rig fake) — H (feel) stays UNKNOWN",
            stages = JsonDocument.Parse(stages).RootElement,
            criteria = JsonDocument.Parse(criteria).RootElement,
            rigNotes = JsonDocument.Parse(rigNotes).RootElement,
            trace_count = JsonDocument.Parse(trace).RootElement.GetArrayLength(),
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "m7-party-spike-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var traceRecords = JsonDocument.Parse(trace).RootElement;
        var tracePath = Path.Combine(EvidenceDir, "m7-party-spike-trace.jsonl");
        await File.WriteAllLinesAsync(tracePath, traceRecords
            .EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .ToList());

        // No unhandled exceptions in the game log tail the run appended.
        var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffset, "|FATAL|");
        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the party spike run");

        Assert.True(passed,
            $"M7 party spike FAIL at {failStage} ({failure}): {failReason}\nEvidence:\n{evidence}\nReport: {reportPath}");

        // Stage coverage: RALLY → ENGAGE → ASSIST → HUNT-KILL all ran.
        var stageNames = JsonDocument.Parse(stages).RootElement
            .EnumerateArray()
            .Select(s => s.TryGetProperty("Stage", out var sn) ? sn.GetString() ?? "" : "")
            .ToList();
        Assert.Contains("RALLY", stageNames);
        Assert.Contains("ENGAGE", stageNames);
        Assert.Contains("ASSIST", stageNames);
        Assert.Contains("HUNT-KILL", stageNames);

        // Acceptance criteria present AND passed.
        var criterionVerdicts = JsonDocument.Parse(criteria).RootElement
            .EnumerateArray()
            .Select(c => (
                Name: c.TryGetProperty("Name", out var cn) ? cn.GetString() ?? "" : "",
                Passed: c.TryGetProperty("Passed", out var cp) && cp.GetBoolean()))
            .ToList();
        foreach (var required in new[]
                 {
                     "all-members-rallied",
                     "all-members-assist-leader-target",
                     "elite-killed-within-bounds",
                     "party-intact-after-kill"
                 })
        {
            var verdict = criterionVerdicts.FirstOrDefault(c => c.Name == required);
            Assert.True(verdict.Name == required,
                $"criterion '{required}' missing\nEvidence:\n{evidence}");
            Assert.True(verdict.Passed,
                $"criterion '{required}' failed ({string.Join("; ", criterionVerdicts.Where(c => !c.Passed).Select(c => c.Name))})\nEvidence:\n{evidence}");
        }

        // THREE characters in the payload; the leader carries id + objId.
        var characters = response.GetProperty("characters");
        Assert.True(characters.GetArrayLength() == 3,
            $"expected 3 characters in the payload, got {characters.GetArrayLength()}");

        uint leaderObjId = 0, leaderId = 0;
        foreach (var c in characters.EnumerateArray())
        {
            // Case-insensitive: the bridge NormalizeName()s bot names.
            if (c.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), LeaderBotName, StringComparison.OrdinalIgnoreCase))
            {
                leaderObjId = c.GetProperty("objId").GetUInt32();
                leaderId = c.GetProperty("id").GetUInt32();
            }
        }
        Assert.True(leaderObjId != 0 && leaderId != 0,
            $"leader '{LeaderBotName}' missing/incomplete in the characters payload\nEvidence:\n{evidence}");

        // Real team membership after the run: nonzero teamId owned by the
        // leader (OwnerId = leader's characters.id).
        var party = response.GetProperty("party");
        var teamId = party.GetProperty("teamId").GetUInt32();
        var ownerId = party.GetProperty("ownerId").GetUInt32();
        Assert.True(teamId != 0, $"party.teamId is zero after the run\nEvidence:\n{evidence}");
        Assert.True(ownerId == leaderId,
            $"party.ownerId {ownerId} != leader character id {leaderId}\nEvidence:\n{evidence}");
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
