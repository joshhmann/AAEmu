using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// GATE 2 — NPC Talk capability, live (NOT autonomy).
///
/// Call path under test (code-verified 2026-09-18, capability matrix §E):
///   POST /api/actors/talk
///     → BotActionController.Talk → BotActionSpec(Talk)
///     → BotActionCommandQueue.ExecuteKind → IGameplayActor.Talk
///     → GameplayActor.Talk: live-NPC resolve + 25m range pre-flight +
///       QuestManager.DoTalkMadeEvents per active quest carrying a
///       QuestActObjTalk/QuestActObjTalkNpcGroup objective (the exact
///       CSQuestTalkMadePacket 0x0da path) + step-machine drain +
///       observable-delta post-check (no-credit talk is Rejected, never silent).
///
/// FIXTURE (data-verified read-only against compact.sqlite3, 2026-09-18):
///   quest 532: ConAcceptNpc(333, npc 2425) → ObjTalk(242, npc 2426) →
///   ConReportNpc(398, npc 2425). Start component 2096 reqs: Level ≥ 26
///   (kind 1) + MotherFaction 148/NuiaAlliance (kind 42) — satisfied by
///   `drive setLevel 30` on the Nuian-male bot (template faction 101,
///   mother 148). NPC 2425 is the Q0 live-proven giver (spawner 2401);
///   NPC 2426's spawner 2402 is adjacent (same area). Quest 251 (Q0) was
///   REJECTED as a fixture: no talk act, so Talk would be Rejected-by-design.
///   Quest 1720 (accept==talk npc 2200) was REJECTED at staging: the live
///   lane reports "no spawner found for NPC template 2200" (spawner 2176 is
///   not in the bot's world) — evidence in the prior g2 report run.
///
/// Flow: accept 532 via POST /api/actors/accept_quest at staged NPC 2425,
/// teleport to NPC 2426 spawner, snapshot `drive questState`, then POST
/// /api/actors/talk with the polled live npcObjId → assert Completed AND
/// questState delta.
///
/// FORBIDDEN here (matrix §B): `drive talk` (synthetic event-fire bypass),
/// Interact/InteractWith for NPCs (doodad-only verbs).
///
/// LANE: adopts the warm Q0 lane (no EnsureUp, no RestartGameServer — cold lane
/// is an honest SETUP/lane-down FAIL, never a rebuild).
///
/// CLAIM (only): the production actor talk capability credits a live NPC talk
/// objective through the real packet path with an independently observed quest
/// delta. NOT claimed: dialogue UI, discovery, autonomy.
/// </summary>
[Collection("e2e")]
public class G2ActorTalkCapabilityTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G2TalkPilot" + Stamp;
    private static readonly string BotAccount = ("g2talk" + "pilot" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private const uint Quest532 = 532;
    private const uint AcceptNpc2425 = 2425;
    private const uint TalkNpc2426 = 2426;
    private const int FixtureLevel = 30;

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "g2-talk-capability-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ExplicitTalk_TalkObjectiveNpc_CreditsViaActor()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G2 talk-capability gate — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        uint charId = 0, npcObjId = 0;
        string charPos = "UNAVAILABLE";
        string questBefore = "UNAVAILABLE", questAfter = "UNAVAILABLE";
        string cmdOutcome = "UNAVAILABLE", lastRefusal = "UNAVAILABLE", audit = "UNAVAILABLE";

        try
        {
            // ---- ADOPT (probe-only; never EnsureUp/Restart — see class doc) ----
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = KillForeignWebApiListeners();
            evidence.AppendLine($"- stale-proc guard (shared :{E2eStack.WebApiPort} listeners outside this lane): {staleGuardDetail}");
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "warm lane answering (WebApi TCP + bridge ping)" : "lane cold — refusing to rebuild (see class doc)",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                failBoundary = "SETUP/lane-down";
                Assert.Fail("warm Q0 lane not answering; refusing to rebuild per workstream constraints");
            }
            // LANE PASSWORD ADOPT (test-file-only fix #1 for this gate): mirrors
            // EnsureEnvFile's DB_PASSWORD read (E2eStack.cs:267-270) without rebuilding.
            AdoptLaneDbPassword();

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
            Leg("enter-world", true, $"inWorld={session.InWorld}", setupSw.Elapsed.TotalSeconds);

            // ---- ENROLL at spawn (before staging): scheduler must NOT pre-empt ----
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var stepped = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            var activeBefore = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest532);
            Leg("enroll", !activeBefore,
                $"stepped={stepped} activeBefore={activeBefore} (must be False: no autonomy claim)",
                enrollSw.Elapsed.TotalSeconds);
            if (activeBefore)
            {
                failBoundary = "SETUP/autonomy-preempted";
                Assert.Fail("quest already active after enroll wake — autonomy pre-empted the explicit leg; " +
                    "re-run with a fresh account");
            }

            // ---- FIXTURE (PRE-LOOP, disclosed): level 30 (Level>=26 gate) + stage at giver NPC 2425 ----
            var fixSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"setLevel\",\"level\":{FixtureLevel}}}", 30_000);
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{AcceptNpc2425}}}", 60_000);
            var giverObjId = PollNpcObjId(bridge, AcceptNpc2425);
            charPos = ReadPos(bridge);
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            var staged = giverObjId != 0;
            Leg("fixture-stage", staged,
                $"lvl={FixtureLevel} giverObjId={giverObjId} (staging only; not traversal proof)",
                fixSw.Elapsed.TotalSeconds);
            if (!staged)
            {
                failBoundary = "SETUP/npc-unresolved";
                Assert.Fail($"giver NPC template {AcceptNpc2425} never materialized (objId=0 after 30s poll)");
            }

            // ---- EXECUTE A: accept 532 via the Q0 actor path ----
            var execSw = Stopwatch.StartNew();
            var acceptBody = $"{{\"bot\":\"{BotName}\",\"questId\":{Quest532},\"acceptorType\":\"Npc\",\"acceptorId\":{AcceptNpc2425},\"idempotencyKey\":\"g2-accept-{BotAccount}\"}}";
            var accept = await PostJsonAsync(http, "/api/actors/accept_quest", acceptBody);
            var acceptTrace = accept.GetProperty("trace_id").GetGuid();
            JsonElement acceptPoll;
            try
            {
                acceptPoll = await PollTerminalAsync(http, acceptTrace, TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException ex)
            {
                failBoundary = "RUN/accept-deadline";
                Leg("explicit-accept", false, ex.Message, execSw.Elapsed.TotalSeconds);
                Assert.Fail($"accept action never reached terminal state within 30s (trace {acceptTrace})");
                return;
            }
            var acceptState = acceptPoll.GetProperty("state").GetString() ?? "";
            var acceptActive = E2eQuestDriver.IsQuestActive(bridge, BotName, Quest532);
            var acceptOk = acceptState == "Completed" && acceptActive;
            Leg("explicit-accept", acceptOk,
                $"state={acceptState} active={acceptActive} trace={acceptTrace}",
                execSw.Elapsed.TotalSeconds);
            if (!acceptOk)
            {
                failBoundary = "RUN/accept-refused";
                Assert.Fail($"accept leg failed: state={acceptState} active={acceptActive} — cannot reach the talk precondition");
            }

            // ---- STAGE for talk: teleport to talk-NPC 2426 spawner, poll live objId ----
            var talkFixSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{TalkNpc2426}}}", 60_000);
            npcObjId = PollNpcObjId(bridge, TalkNpc2426);
            charPos = ReadPos(bridge);
            var talkStaged = npcObjId != 0;
            Leg("talk-stage", talkStaged,
                $"talkNpcObjId={npcObjId} char=[{charPos}] (staging only; not traversal proof)",
                talkFixSw.Elapsed.TotalSeconds);
            if (!talkStaged)
            {
                failBoundary = "SETUP/talk-npc-unresolved";
                Assert.Fail($"talk NPC template {TalkNpc2426} never materialized (objId=0 after 30s poll)");
            }
            // ---- OBSERVE: quest snapshot before talk (independent delta basis) ----
            questBefore = ReadQuestState(bridge, Quest532);

            // ---- EXECUTE B: POST /api/actors/talk with the live npcObjId ----
            var talkSw = Stopwatch.StartNew();
            var talkBody = $"{{\"bot\":\"{BotName}\",\"npcObjId\":{npcObjId},\"idempotencyKey\":\"g2-talk-{BotAccount}\"}}";
            var talk = await PostJsonAsync(http, "/api/actors/talk", talkBody);
            var talkTrace = talk.GetProperty("trace_id").GetGuid();
            JsonElement poll;
            try
            {
                poll = await PollTerminalAsync(http, talkTrace, TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException ex)
            {
                failBoundary = "RUN/action-deadline";
                cmdOutcome = "TimedOut (poll deadline 30s)";
                audit = await ReadTraceAsync(http, talkTrace);
                Leg("explicit-talk", false, ex.Message, talkSw.Elapsed.TotalSeconds);
                Assert.Fail($"talk action never reached terminal state within 30s (trace {talkTrace})");
                return;
            }
            var state = poll.GetProperty("state").GetString() ?? "";
            if (poll.TryGetProperty("actor_id", out var actorEl))
                charId = actorEl.GetUInt32();
            cmdOutcome = $"{state}: {(poll.TryGetProperty("detail", out var dEl) ? dEl.GetString() : "")}";
            if (poll.TryGetProperty("failure", out var fEl) && fEl.GetString() is { Length: > 0 })
                lastRefusal = fEl.GetString()!;
            audit = poll.TryGetProperty("audit", out var aEl) ? aEl.ToString() : "UNAVAILABLE (no audit payload)";
            questAfter = ReadQuestState(bridge, Quest532);
            var delta = questBefore != questAfter && questBefore != "UNAVAILABLE (read failed)" && questAfter != "UNAVAILABLE (read failed)";
            execSeconds = execSw.Elapsed.TotalSeconds;
            passed = state == "Completed" && delta;
            if (!passed && string.IsNullOrEmpty(failBoundary))
                failBoundary = state != "Completed" ? "RUN/engine-refused" : "RUN/no-observed-delta";
            Leg("explicit-talk", passed,
                $"cmd={cmdOutcome} refusal=[{lastRefusal}] delta={delta} before=[{questBefore}] after=[{questAfter}] trace={talkTrace}",
                talkSw.Elapsed.TotalSeconds);
            Assert.True(passed,
                $"G2 FAIL at {failBoundary}: char={charId} npc={TalkNpc2426}/{npcObjId} char=[{charPos}] " +
                $"outcome=[{cmdOutcome}] refusal=[{lastRefusal}] before=[{questBefore}] after=[{questAfter}]");
        }
        finally
        {
            totalWall.Stop();
            await WriteReportAsync(passed, failBoundary, legs, evidence.ToString(),
                new { charId, quest = Quest532, acceptNpc = AcceptNpc2425, npcTemplate = TalkNpc2426, npcObjId, charPos, questBefore, questAfter, cmdOutcome, lastRefusal },
                audit, setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
            session?.Dispose();
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }

    // ---- helpers (Q0 shapes, adopted-lane variant: no EnsureUp/Restart) ----

    /// <summary>
    /// Stale-lane guard: before the gate adopts a warm lane, ANY process
    /// LISTENING on the shared WebApi port whose cwd is NOT this lane's
    /// E2E_ROOT runtime dir is a foreign leftover (a sibling lane or an
    /// orphaned prior run) and would answer the adopt probes for the wrong
    /// stack. Such a listener is SIGTERM'd, then SIGKILL'd after 10 s if it
    /// is still alive. Only listeners are considered, so the current lane's
    /// own pids (which appear as CLIENTS on 127.0.0.1:PORT) are never
    /// touched; the log line is emitted for every kill and returned as
    /// evidence.
    /// </summary>
    private static string KillForeignWebApiListeners()
    {
        var laneRoot = Path.GetFullPath(Path.Combine(E2eStack.E2eRoot, "runtime"))
            .TrimEnd(Path.DirectorySeparatorChar);
        var summary = new List<string>();
        foreach (var pid in ListenersOnPort(E2eStack.WebApiPort))
        {
            try
            {
                var cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget;
                if (cwd == null)
                    continue;
                var full = Path.GetFullPath(cwd).TrimEnd(Path.DirectorySeparatorChar);
                var inThisLane = full.Equals(laneRoot, StringComparison.Ordinal)
                    || full.StartsWith(laneRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (inThisLane)
                    continue;
                var cmdline = "";
                try { cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { }
                var line = $"stale-proc-guard: pid={pid} listening :{E2eStack.WebApiPort} cwd={full} (not this lane {laneRoot}) cmd=[{cmdline}] — SIGTERM";
                Console.WriteLine("[g2] " + line);
                summary.Add(line);
                using (var term = Process.Start(new ProcessStartInfo("kill", $"-TERM {pid}")
                       { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }))
                {
                    term!.WaitForExit(5_000);
                }
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var killed = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (!Directory.Exists($"/proc/{pid}"))
                    {
                        killed = true;
                        break;
                    }
                    Thread.Sleep(500);
                }
                if (!killed)
                {
                    var kline = $"stale-proc-guard: pid={pid} alive 10s after SIGTERM — SIGKILL";
                    Console.WriteLine("[g2] " + kline);
                    summary.Add(kline);
                    using var hard = Process.Start(new ProcessStartInfo("kill", $"-KILL {pid}")
                        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                    hard!.WaitForExit(5_000);
                }
            }
            catch (Exception ex)
            {
                summary.Add($"stale-proc-guard: pid={pid} check/kill failed ({ex.GetType().Name}: {ex.Message})");
            }
        }
        return summary.Count == 0 ? "none" : string.Join(" | ", summary);
    }

    /// <summary>
    /// PIDs holding a LISTEN socket on <paramref name="port"/> (IPv4/IPv6),
    /// read from /proc/net/tcp{,6} + inode → /proc/*/fd. Never matches a mere
    /// outbound client connection, so the current lane's own pids cannot be
    /// returned by accident.
    /// </summary>
    private static List<int> ListenersOnPort(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                var lines = File.ReadAllLines(table);
                for (var i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 10)
                        continue;
                    if (!parts[3].Equals("0A", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var local = parts[1];
                    var colon = local.LastIndexOf(':');
                    if (colon < 0)
                        continue;
                    if (!int.TryParse(local[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) || p != port)
                        continue;
                    inodes.Add(parts[9]);
                }
            }
            catch
            {
            }
        }
        var pids = new List<int>();
        if (inodes.Count == 0)
            return pids;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid))
                continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                {
                    var link = new FileInfo(fd).LinkTarget;
                    if (link == null || !link.StartsWith("socket:[", StringComparison.Ordinal))
                        continue;
                    var inode = link["socket:[".Length..].TrimEnd(']');
                    if (inodes.Contains(inode))
                    {
                        pids.Add(pid);
                        break;
                    }
                }
            }
            catch
            {
            }
        }
        return pids;
    }

    private static async Task<bool> ProbeLaneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", E2eStack.WebApiPort);
                using var bridge = new BotDriveClient(E2eStack.BridgePort);
                if (bridge.Call("{\"cmd\":\"ping\"}", 3000).GetProperty("pong").GetBoolean())
                    return true;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        return false;
    }
    private static void AdoptLaneDbPassword()
    {
        var envFile = Path.Combine(E2eStack.E2eRoot, ".env");
        foreach (var line in File.ReadAllLines(envFile))
        {
            if (!line.StartsWith("DB_PASSWORD="))
                continue;
            var pwd = line["DB_PASSWORD=".Length..].Trim();
            if (string.IsNullOrEmpty(pwd))
                throw new InvalidOperationException($"lane {envFile} has an empty DB_PASSWORD");
            typeof(E2eStack).GetProperty("DbPassword")!.SetValue(null, pwd);
            return;
        }
        throw new InvalidOperationException($"lane {envFile} has no DB_PASSWORD entry");
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
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

    private static string ReadPos(BotDriveClient bridge)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"charPos\"}}", 30_000);
            if (el.TryGetProperty("x", out var x) && el.TryGetProperty("y", out var y) && el.TryGetProperty("z", out var z))
                return $"{x.GetDouble():0.1},{y.GetDouble():0.1},{z.GetDouble():0.1}";
            return el.ToString();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
        }
    }

    private static string ReadQuestState(BotDriveClient bridge, uint questId)
    {
        try
        {
            var el = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"questState\",\"quest\":{questId}}}", 30_000);
            return el.GetRawText();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
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
                scenario = "g2-talk-capability",
                verdict = passed ? "PASS" : "FAIL",
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim = "production GameplayActor.Talk credits quest 532 talk objective at live NPC 2426 through QuestManager.DoTalkMadeEvents with an independently observed quest delta",
                notClaimed = new[] { "dialogue UI", "discovery", "autonomous selection" },
                callPath = "POST /api/actors/accept_quest (setup leg) then POST /api/actors/talk → BotActionSpec(Talk) → BotActionCommandQueue → GameplayActor.Talk → QuestManager.DoTalkMadeEvents",
                fixtureData = new
                {
                    quest = Quest532,
                    acceptAct = "QuestActConAcceptNpc(333, npc 2425)",
                    talkAct = "QuestActObjTalk(242, npc 2426)",
                    startGate = "Level >= 26 + MotherFaction 148 (NuiaAlliance); Nuian-male template faction 101 mother 148; quest 251 rejected (no talk act); quest 1720 rejected (NPC 2200 has no live spawner)"
                },
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
            // K4 artifact preservation: every run writes a new timestamped file
            // (logs/<gate>-report.<utc>.json); the bare <gate>-report.json is
            // only a latest-pointer COPY refreshed here (copy, not symlink, so
            // the pointer survives stamped-file cleanup and needs no privilege).
            // A rerun therefore never destroys a prior report.
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var stampedPath = Path.Combine(EvidenceDir,
                Path.GetFileNameWithoutExtension(ReportPath) + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'") + ".json");
            await File.WriteAllTextAsync(stampedPath, reportJson);
            File.Copy(stampedPath, ReportPath, overwrite: true);
        }
        catch
        {
            // Evidence write is best-effort; the xUnit verdict is authoritative.
        }
    }
}
