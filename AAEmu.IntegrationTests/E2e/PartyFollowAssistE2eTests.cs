using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M7 Party v1 slice 2 LIVE hook: a REAL game server boots (same binaries,
/// same MySQL, same config precedence as prod), TWO bots are provisioned
/// through the shared lifecycle (HeadlessSession.Provision via the scenario
/// bridge — real managed accounts + character rows), and the
/// m7-party-follow-assist multi-actor scenario drives a real party
/// follow/assist run through the M5 IGameplayActor contract ONLY:
///
///   party invite/accept through TeamManager (real team membership) →
///   member Move legs to the leader (position convergence inside the
///   follow distance) → assist copies the leader's CurrentTarget
///   (SetTarget on the same objId).
///
/// The machine-readable PASS/FAIL report + trace evidence land in the E2E
/// logs (same evidence convention as the M7 adventurer spike hook).
/// H stays UNKNOWN: proxy/bot-functional evidence only — Josh's feel
/// verdicts are never derived from scripted actors.
/// </summary>
[Collection("e2e")]
public class PartyFollowAssistE2eTests
{
    private const string TemplateName = "m7-party-follow-assist";
    // Hyphen-free: NameManager rejects '-' in character names (InvalidCharacters).
    private const string LeaderBotName = "M7PfaLeader";
    private const string MemberBotName = "M7PfaMember";

    private static string EvidenceDir => Path.Combine(
        Environment.GetEnvironmentVariable("E2E_ROOT") ?? "/root/aaemu-e2e", "logs");

    private static string GameLogPath => Path.Combine(EvidenceDir, "game.log");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task PartyFollowAssist_OnLiveServer_MemberFollowsLeaderAndCopiesTarget()
    {
        E2eStack.EnsureUp();

        // Log-tail baseline: the unhandled-exception scan covers only what
        // the run appends.
        var logOffset = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var response = bridge.Call(
            $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\",\"leader\":\"{LeaderBotName}\"," +
            $"\"member\":\"{MemberBotName}\",\"npc\":3492,\"followDistance\":3.0,\"moveSpeed\":5.0," +
            "\"moveTimeoutSeconds\":30}",
            timeoutMs: 300_000); // provisions 2 bots + spawns an NPC + pumps real movement legs on the live world

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
        // gate evidence convention — same shape as the M7 spike hook).
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            scenario = TemplateName,
            milestone = "M7 Party v1 slice 2",
            verdict = passed ? "PASS" : "FAIL",
            failStage,
            failure,
            failReason,
            note = "party member follows the real team owner and assists the owner's current target through the M5 contract; H (feel) stays UNKNOWN",
            stages = JsonDocument.Parse(stages).RootElement,
            criteria = JsonDocument.Parse(criteria).RootElement,
            rigNotes = JsonDocument.Parse(rigNotes).RootElement,
            trace_count = JsonDocument.Parse(trace).RootElement.GetArrayLength(),
            evidence
        };
        var reportPath = Path.Combine(EvidenceDir, "m7-party-follow-assist-report.json");
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var traceRecords = JsonDocument.Parse(trace).RootElement;
        var tracePath = Path.Combine(EvidenceDir, "m7-party-follow-assist-trace.jsonl");
        await File.WriteAllLinesAsync(tracePath, traceRecords
            .EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .ToList());

        // No unhandled exceptions in the game log tail the run appended.
        var unhandled = CountLogTailMatches(logOffset, "Unhandled exception");
        var fatals = CountLogTailMatches(logOffset, "|FATAL|");
        Assert.True(unhandled == 0 && fatals == 0,
            $"game log tail carries {unhandled} unhandled exception(s) + {fatals} fatal(s) during the party follow/assist run");

        Assert.True(passed,
            $"M7 party follow/assist FAIL at {failStage} ({failure}): {failReason}\nEvidence:\n{evidence}\nReport: {reportPath}");

        // Stage coverage: the FOLLOW and ASSIST legs must both have run.
        var stageNames = JsonDocument.Parse(stages).RootElement
            .EnumerateArray()
            .Select(s => s.TryGetProperty("Stage", out var sn) ? sn.GetString() ?? "" : "")
            .ToList();
        Assert.Contains("FOLLOW", stageNames);
        Assert.Contains("ASSIST", stageNames);

        // Acceptance criteria: position convergence + copied target must be
        // present AND passed.
        var criterionVerdicts = JsonDocument.Parse(criteria).RootElement
            .EnumerateArray()
            .Select(c => (
                Name: c.TryGetProperty("Name", out var cn) ? cn.GetString() ?? "" : "",
                Passed: c.TryGetProperty("Passed", out var cp) && cp.GetBoolean()))
            .ToList();
        foreach (var required in new[] { "member-followed-leader", "member-assisted-leader-target" })
        {
            var verdict = criterionVerdicts.FirstOrDefault(c => c.Name == required);
            Assert.True(verdict.Name == required,
                $"criterion '{required}' missing\nEvidence:\n{evidence}");
            Assert.True(verdict.Passed,
                $"criterion '{required}' failed ({string.Join("; ", criterionVerdicts.Where(c => !c.Passed).Select(c => c.Name))})\nEvidence:\n{evidence}");
        }

        // Trace shape: a Move leg then a Target leg (follow before assist).
        var actions = traceRecords
            .EnumerateArray()
            .Select(e =>
            {
                using var rec = JsonDocument.Parse(e.GetString() ?? "{}");
                return rec.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            })
            .ToList();
        var moveIndex = actions.IndexOf("Move");
        var targetIndex = actions.IndexOf("Target");
        Assert.True(moveIndex >= 0,
            $"no Move record in the audit trace ({actions.Count} records)\nTrace: {tracePath}");
        Assert.True(targetIndex >= 0,
            $"no Target record in the audit trace ({actions.Count} records)\nTrace: {tracePath}");
        Assert.True(moveIndex < targetIndex,
            $"Move (index {moveIndex}) did not precede Target (index {targetIndex})\nTrace: {tracePath}");

        // Real team membership: nonzero teamId owned by the leader. NOTE:
        // TeamManager.OwnerId is the leader's characters.id (the engine
        // registry key), NOT the live-world objId — both namespaces are in
        // the characters payload.
        var characters = response.GetProperty("characters");
        var leaderObjId = 0u;
        var leaderId = 0u;
        foreach (var c in characters.EnumerateArray())
        {
            // Case-insensitive: the bridge NormalizeName()s bot names
            // (e.g. "M7PfaLeader" -> "M7pfaleader").
            if (c.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), LeaderBotName, StringComparison.OrdinalIgnoreCase))
            {
                leaderObjId = c.GetProperty("objId").GetUInt32();
                leaderId = c.GetProperty("id").GetUInt32();
            }
        }

        Assert.True(leaderObjId != 0,
            $"leader '{LeaderBotName}' missing from the characters payload\nEvidence:\n{evidence}");
        Assert.True(leaderId != 0,
            $"leader '{LeaderBotName}' carries no character id in the characters payload\nEvidence:\n{evidence}");

        var party = response.GetProperty("party");
        var teamId = party.GetProperty("teamId").GetUInt32();
        var ownerId = party.GetProperty("ownerId").GetUInt32();
        Assert.True(teamId != 0,
            $"party.teamId is zero after the run\nEvidence:\n{evidence}");
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
