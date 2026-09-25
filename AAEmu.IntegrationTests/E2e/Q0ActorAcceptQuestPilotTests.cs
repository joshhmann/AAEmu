using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Q0 pilot — explicit actor quest-acceptance capability, live (NOT autonomy).
///
/// Call-path verdict (code-verified 2026-09-18, see header):
///   POST /api/actors/accept_quest
///     → BotActionController.AcceptQuest → BotActionSpec(AcceptQuest + QuestAcceptParams)
///     → BotActionCommandQueue.ExecuteKind → IGameplayActor.AcceptQuest
///     → GameplayActor.AcceptQuest (request lifecycle + ledger pre-flight + audit)
///     → PlayerBotController.AcceptQuest → CharacterQuests.AddQuest (real engine gates)
///   Bridge `drive accept` does NOT traverse this path (controller-direct), so it
///   is not used for EXECUTE here — only for PRE-LOOP fixtures (setLevel,
///   teleportToNpc, npcObjId) and OBSERVATION (charState, charPos, isActive).
///
/// Fixture: quest 251 via NPC template 2425 (live-proven pair, cf.
/// Q4LiveHuntLootE2eTests). CharacterQuests.AddQuest performs NO range check
/// (verified CharacterQuests.cs:82-192) — staging in range is realism context,
/// not an engine gate; distance is recorded, not asserted against a threshold.
///
/// CLAIM (only): the production actor capability accepts an eligible quest for
/// a real TCP-connected character. NOT claimed: discovery, navigation,
/// autonomous selection, turn-in, rewards, persistence.
/// </summary>
[Collection("e2e")]
public class Q0ActorAcceptQuestPilotTests
{
    private const string BotName = "Q0AcceptPilot";
    private const string BotAccount = "q0acceptpilot";
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private const uint Quest251 = 251;
    private const uint AcceptorNpc2425 = 2425;
    private const int FixtureLevel = 10;

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "q0-actor-accept-pilot-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ExplicitAccept_InRangeGiver_BecomesActiveViaActor()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# Q0 actor-accept pilot — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        void Leg(string leg, bool passed, string detail, double ms)
        {
            legs.Add((leg, passed, detail, ms));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        BotNetworkSession? session = null;
        var setupSeconds = 0.0;
        var execSeconds = 0.0;
        var passed = false;
        string failBoundary = "";
        // Failure-diagnostic accumulators (marked UNAVAILABLE when unexposed).
        uint charId = 0, npcObjId = 0;
        string charPos = "UNAVAILABLE", npcPos = "UNAVAILABLE", distance = "UNAVAILABLE";
        string cmdOutcome = "UNAVAILABLE", lastRefusal = "UNAVAILABLE", audit = "UNAVAILABLE";

        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));
            using var http = NewClient();
            var bridge = new BotDriveClient(E2eStack.BridgePort);

            // ---- SETUP: real TCP enter-world (timed separately) ----
            E2eStack.CleanupBotRows(BotAccount);
            session = await BotNetworkSession.ConnectAsync(
                BotName, BotAccount, Password,
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            if (!session.InWorld)
            {
                failBoundary = "SETUP/enter-world";
                Leg("enter-world", false, "real login flow did not reach in-world", setupSw.Elapsed.TotalSeconds);
                Assert.Fail("bot must enter the live world through the real login flow");
            }
            var cs = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charState\"}}", 30_000);
            var botObjId = cs.TryGetProperty("objId", out var objEl) ? objEl.GetUInt32() : 0;
            // charState carries no character row id (name/level/objId only);
            // the authoritative row id comes from the queue snapshot below.
            Leg("enter-world", true, $"inWorld={session.InWorld} objId={botObjId}", setupSw.Elapsed.TotalSeconds);
            // ---- ENROLL at spawn (before staging near the giver): registers the
            // networked character with the bot manager so the queue can resolve
            // it. waitMs=1000 bounds the single wake step; quest-251 legs are
            // no-ops here (giver not yet in scope). Disclosed control-plane.
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var stepped = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            Leg("enroll", !activeBefore,
                $"stepped={stepped} activeBefore={activeBefore} (must be False: no autonomy claim yet)",
                enrollSw.Elapsed.TotalSeconds);
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest already active after enroll wake — autonomy pre-empted the explicit leg; " +
                    "re-run with a fresh account (existing-behavior gate, not this pilot's claim)");
            }

            // ---- FIXTURE (PRE-LOOP, disclosed; never traversal proof) ----
            var fixSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{FixtureLevel}}}", 30_000);
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{AcceptorNpc2425}}}", 60_000);
            npcObjId = PollNpcObjId(bridge, AcceptorNpc2425);
            charPos = ReadPos(bridge, "charPos");
            npcPos = ReadNpcPos(bridge);
            distance = ComputeDistance(charPos, npcPos);
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            var staged = npcObjId != 0;
            Leg("fixture-stage", staged,
                $"lvl={FixtureLevel} npcObjId={npcObjId} char=[{charPos}] npc=[{npcPos}] dist={distance}m (range informational: AddQuest has no range gate)",
                fixSw.Elapsed.TotalSeconds);
            if (!staged)
            {
                failBoundary = "SETUP/npc-unresolved";
                Assert.Fail($"giver NPC template {AcceptorNpc2425} never materialized (objId=0 after 30s poll)");
            }

            // ---- EXECUTE: existing command surface → production actor (timed separately) ----
            var execSw = Stopwatch.StartNew();
            var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest251},\"acceptorType\":\"Npc\",\"acceptorId\":{AcceptorNpc2425},\"idempotencyKey\":\"q0-pilot-{BotAccount}\"}}";
            var accept = await PostJsonAsync(http, "/api/actors/accept_quest", acceptBody);
            var traceId = accept.GetProperty("trace_id").GetGuid();
            JsonElement poll;
            try
            {
                poll = await PollTerminalAsync(http, traceId, TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException ex)
            {
                failBoundary = "RUN/action-deadline";
                cmdOutcome = "TimedOut (poll deadline 30s)";
                audit = await ReadTraceAsync(http, traceId);
                Leg("explicit-accept", false, ex.Message, execSw.Elapsed.TotalSeconds);
                Assert.Fail($"accept action never reached terminal state within 30s (trace {traceId})");
                return;
            }
            var state = poll.GetProperty("state").GetString() ?? "";
            if (poll.TryGetProperty("actor_id", out var actorEl))
                charId = actorEl.GetUInt32();
            cmdOutcome = $"{state}: {(poll.TryGetProperty("detail", out var dEl) ? dEl.GetString() : "")}";
            if (poll.TryGetProperty("failure", out var fEl) && fEl.GetString() is { Length: > 0 })
                lastRefusal = fEl.GetString()!;
            audit = poll.TryGetProperty("audit", out var aEl) ? aEl.ToString() : "UNAVAILABLE (no audit payload)";
            var activeAfter = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest251);
            execSeconds = execSw.Elapsed.TotalSeconds;
            passed = state == "Completed" && activeAfter;
            if (!passed && string.IsNullOrEmpty(failBoundary))
                failBoundary = state == "Completed" ? "RUN/state-not-observed" : "RUN/engine-refused";
            Leg("explicit-accept", passed,
                $"cmd={cmdOutcome} refusal=[{lastRefusal}] active={activeAfter} trace={traceId}",
                execSeconds);
            Assert.True(passed,
                $"Q0 pilot FAIL at {failBoundary}: quest={Quest251} char={charId} npc={AcceptorNpc2425}/{npcObjId} " +
                $"char=[{charPos}] npc=[{npcPos}] dist={distance}m outcome=[{cmdOutcome}] refusal=[{lastRefusal}]");
        }
        finally
        {
            totalWall.Stop();
            await WriteReportAsync(passed, failBoundary, legs, evidence.ToString(),
                new { charId, quest = Quest251, npcTemplate = AcceptorNpc2425, npcObjId, charPos, npcPos, distance, cmdOutcome, lastRefusal },
                audit, setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
            session?.Dispose();
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }

    // ---- helpers (same shapes as BotControlApiE2eTests / Q4LiveHuntLootE2eTests) ----

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    private static async Task WaitForWebApiAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", E2eStack.WebApiPort);
                return;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        throw new TimeoutException("WebApi never came up on port " + E2eStack.WebApiPort);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {path} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PollTerminalAsync(HttpClient client, Guid traceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement.Clone();
                var state = root.GetProperty("state").GetString();
                if (state is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                    return root;
            }
            await Task.Delay(300);
        }
        throw new TimeoutException($"trace {traceId} never reached a terminal state within {timeout}");
    }

    private static async Task<string> ReadTraceAsync(HttpClient client, Guid traceId)
    {
        try
        {
            using var response = await client.GetAsync($"/api/actors/actions/{traceId}");
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static uint PollNpcObjId(BotDriveClient bridge, uint templateId, int seconds = 30)
    {
        var deadline = Environment.TickCount64 + seconds * 1000;
        while (Environment.TickCount64 < deadline)
        {
            var objId = bridge.Call(
                $"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"npcObjId\",\"npc\":{templateId}}}",
                30_000).GetProperty("objId").GetUInt32();
            if (objId != 0)
                return objId;
            Thread.Sleep(1000);
        }
        return 0;
    }

    private static string ReadPos(BotDriveClient bridge, string op)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"{op}\"}}", 30_000);
            // charPos returns {x,y,z}; be tolerant to shape drift.
            if (el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y) && el.TryGetProperty("z", out var z))
                return $"{x.GetDouble():0.1},{y.GetDouble():0.1},{z.GetDouble():0.1}";
            return el.ToString();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
        }
    }

    private static string ReadNpcPos(BotDriveClient bridge)
    {
        // No dedicated npc-pos op verified; mark honestly rather than inventing one.
        return "UNAVAILABLE (no verified npc-pos bridge op; npc objId proves materialization)";
    }

    private static string ComputeDistance(string charPos, string npcPos)
    {
        try
        {
            var c = charPos.Split(',').Select(double.Parse).ToArray();
            var n = npcPos.Split(',').Select(double.Parse).ToArray();
            if (c.Length != 3 || n.Length != 3)
                return "UNAVAILABLE";
            var d = Math.Sqrt(Math.Pow(c[0] - n[0], 2) + Math.Pow(c[1] - n[1], 2) + Math.Pow(c[2] - n[2], 2));
            return $"{d:0.1}";
        }
        catch
        {
            return "UNAVAILABLE (npc position unexposed; co-location via teleportToNpc staging)";
        }
    }

    private async Task WriteReportAsync(bool passed, string failBoundary,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText,
        object fixture, string auditJson, double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var report = new
            {
                scenario = "q0-actor-accept-pilot",
                verdict = passed ? "PASS" : "FAIL",
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim = "production GameplayActor.AcceptQuest accepts eligible quest 251 for a real TCP-connected character staged at giver NPC 2425",
                notClaimed = new[] { "discovery", "navigation", "autonomous selection", "turn-in", "rewards", "persistence" },
                callPath = "POST /api/actors/accept_quest → BotActionSpec(AcceptQuest) → BotActionCommandQueue → GameplayActor.AcceptQuest → PlayerBotController.AcceptQuest → CharacterQuests.AddQuest",
                timings = new
                {
                    setupSeconds = Math.Round(setupSeconds, 1),
                    execSeconds = Math.Round(execSeconds, 1),
                    wallSeconds = Math.Round(wallSeconds, 1)
                },
                legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
                fixture,
                audit = auditJson,
                provenance = new
                {
                    sourceRevision = E2eStack.SourceRevision,
                    runtimeSqliteMd5 = E2eStack.RuntimeSqliteMd5(),
                    canonicalSqliteMd5 = E2eStack.CanonicalSqliteMd5
                },
                evidence = evidenceText
            };
            await File.WriteAllTextAsync(ReportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Evidence write is best-effort; the xUnit verdict is authoritative.
        }
    }
}
