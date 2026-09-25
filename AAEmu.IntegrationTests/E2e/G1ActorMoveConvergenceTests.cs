using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// GATE 1 — Movement convergence, live (NOT autonomy).
///
/// Call path under test (code-verified 2026-09-18, capability matrix):
///   POST /api/actors/move
///     → BotActionController.Move → BotActionSpec(Move + destination + MoveActionParams)
///     → BotActionCommandQueue.ExecuteKind → IGameplayActor.MoveTo
///     → GameplayActor.MoveTo (request lifecycle + ledger pre-flight + audit)
///     → per-Tick Transform stepping; arrival ⇔ flat(XZ) ≤ 0.5f AND |dZ| ≤ 0.5f
///       (ArrivalRadius, GameplayActor.cs:75).
///
/// LANE: adopts the warm Q0 lane (E2E_ROOT=/root/aaemu-e2e-q0, token
/// e2e-q0-pilot-token). No EnsureUp, no RestartGameServer — readiness is
/// probed (WebApi TCP + bridge ping) and a cold lane is an honest
/// SETUP/lane-down FAIL, never a rebuild.
///
/// FIXTURES (disclosed, never traversal proof): enroll wake (queue bot-manager
/// registration, same as Q0), teleportToNpc staging at live-proven NPC 2425
/// (designer-placed walkable point), charPos observation. Bridge is NOT used
/// for movement — no teleport-between-reads.
///
/// CLAIM (only): the production actor move capability converges a real
/// TCP-connected character to within 1.0m flat / 1.0m vertical of the
/// requested target (engine arrival is 0.5f; 1.0m is test tolerance).
/// NOT claimed: routing/obstacle avoidance (that is Navigate, queue-only),
/// unit-follow, autonomy.
/// </summary>
[Collection("e2e")]
public class G1ActorMoveConvergenceTests
{
    private const string Password = "e2e-secret";
    private const string Token = "e2e-q0-pilot-token";

    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "G1MovePilot" + Stamp;
    private static readonly string BotAccount = ("g1movepilot" + Stamp).ToLowerInvariant();
    private const uint StageNpc2425 = 2425;
    private const float StepX = 4f;
    private const float Speed = 2.0f;
    private const int MoveTimeoutSec = 20;

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "g1-move-convergence-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task ExplicitMove_Plus4X_ConvergesViaActor()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# G1 move-convergence gate — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
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
        uint charId = 0;
        string startPos = "UNAVAILABLE", targetPos = "UNAVAILABLE", lastPos = "UNAVAILABLE";
        string remaining = "UNAVAILABLE", cmdOutcome = "UNAVAILABLE", lastRefusal = "UNAVAILABLE", audit = "UNAVAILABLE";

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
            // LANE PASSWORD ADOPT (test-file-only fix #1 for this gate): E2eStack
            // sources DbPassword from $E2E_ROOT/.env DB_PASSWORD= inside EnsureUp's
            // EnsureEnvFile (E2eStack.cs:267-270) — skipped here by design (no
            // rebuild). Mirror that read so the sanctioned CleanupBotRows path
            // authenticates against the warm lane's MySQL.
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
            // ---- ENROLL at spawn: queue bot-manager registration (Q0 pattern) ----
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var stepped = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            Leg("enroll", true, $"stepped={stepped} (registration only; no quest asserted in this gate)",
                enrollSw.Elapsed.TotalSeconds);
            // ---- FIXTURE (PRE-LOOP, disclosed): stage at live-proven NPC 2425 ----
            var fixSw = Stopwatch.StartNew();
            bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{BotName}\",\"op\":\"teleportToNpc\",\"npc\":{StageNpc2425}}}", 60_000);
            var stagedNpc = PollNpcObjId(bridge, StageNpc2425);
            startPos = ReadPos(bridge);
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            var staged = stagedNpc != 0 && !startPos.StartsWith("UNAVAILABLE");
            Leg("fixture-stage", staged,
                $"npcObjId={stagedNpc} start=[{startPos}] (staging only; not traversal proof)",
                fixSw.Elapsed.TotalSeconds);
            if (!staged)
            {
                failBoundary = "SETUP/stage-unresolved";
                Assert.Fail($"staging failed: npcObjId={stagedNpc} start=[{startPos}]");
            }

            // ---- EXECUTE: POST /api/actors/move (+4 X, speed 2.0, timeout 20s) ----
            var execSw = Stopwatch.StartNew();
            var s = ParsePos(startPos);
            var tx = s.X + StepX;
            targetPos = $"{F1(tx)},{F1(s.Y)},{F1(s.Z)}";
            var moveBody = $"{{\"bot\":\"{BotName}\",\"x\":{F1(tx)},\"y\":{F1(s.Y)},\"z\":{F1(s.Z)}," +
                $"\"speed\":{Speed},\"timeoutSec\":{MoveTimeoutSec},\"idempotencyKey\":\"g1-move-{BotAccount}\"}}";
            var move = await PostJsonAsync(http, "/api/actors/move", moveBody);
            var traceId = move.GetProperty("trace_id").GetGuid();
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
                lastPos = ReadPos(bridge);
                remaining = ComputeRemaining(lastPos, targetPos);
                Leg("explicit-move", false, ex.Message, execSw.Elapsed.TotalSeconds);
                Assert.Fail($"move action never reached terminal state within 30s (trace {traceId})");
                return;
            }
            var state = poll.GetProperty("state").GetString() ?? "";
            if (poll.TryGetProperty("actor_id", out var actorEl))
                charId = actorEl.GetUInt32();
            cmdOutcome = $"{state}: {(poll.TryGetProperty("detail", out var dEl) ? dEl.GetString() : "")}";
            if (poll.TryGetProperty("failure", out var fEl) && fEl.GetString() is { Length: > 0 })
                lastRefusal = fEl.GetString()!;
            audit = poll.TryGetProperty("audit", out var aEl) ? aEl.ToString() : "UNAVAILABLE (no audit payload)";
            lastPos = ReadPos(bridge);
            remaining = ComputeRemaining(lastPos, targetPos);
            execSeconds = execSw.Elapsed.TotalSeconds;

            var (flatOk, dzOk, flat, dz) = CheckConvergence(lastPos, targetPos);
            passed = state == "Completed" && flatOk && dzOk;
            if (!passed && string.IsNullOrEmpty(failBoundary))
                failBoundary = state != "Completed" ? "RUN/engine-refused" : "RUN/not-converged";
            Leg("explicit-move", passed,
                $"cmd={cmdOutcome} refusal=[{lastRefusal}] start=[{startPos}] target=[{targetPos}] last=[{lastPos}] " +
                $"flat={F1(flat)} dz={F1(dz)} trace={traceId}",
                execSeconds);
            Assert.True(passed,
                $"G1 FAIL at {failBoundary}: char={charId} start=[{startPos}] target=[{targetPos}] last=[{lastPos}] " +
                $"remaining=[{remaining}] outcome=[{cmdOutcome}] refusal=[{lastRefusal}] elapsed={execSeconds:0.0}s");
        }
        finally
        {
            totalWall.Stop();
            await WriteReportAsync(passed, failBoundary, legs, evidence.ToString(),
                new { charId, startPos, targetPos, lastPos, remaining, cmdOutcome, lastRefusal },
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
                Console.WriteLine("[g1] " + line);
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
                    Console.WriteLine("[g1] " + kline);
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
                return $"{F1(x.GetDouble())},{F1(y.GetDouble())},{F1(z.GetDouble())}";
            return el.ToString();
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE ({ex.GetType().Name})";
        }
    }

    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static (float X, float Y, float Z) ParsePos(string pos)
    {
        var p = pos.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return ((float)p[0], (float)p[1], (float)p[2]);
    }

    private static string ComputeRemaining(string lastPos, string targetPos)
    {
        try
        {
            var (flatOk, dzOk, flat, dz) = CheckConvergence(lastPos, targetPos);
            return $"flat={F1(flat)}m(ok={flatOk}) dz={F1(dz)}m(ok={dzOk})";
        }
        catch
        {
            return "UNAVAILABLE (position parse failed)";
        }
    }

    private static (bool FlatOk, bool DzOk, double Flat, double Dz) CheckConvergence(string lastPos, string targetPos)
    {
        var l = lastPos.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var t = targetPos.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var flat = Math.Sqrt(Math.Pow(l[0] - t[0], 2) + Math.Pow(l[1] - t[1], 2));
        var dz = Math.Abs(l[2] - t[2]);
        return (flat <= 1.0, dz <= 1.0, flat, dz);
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
                scenario = "g1-move-convergence",
                verdict = passed ? "PASS" : "FAIL",
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                claim = "production GameplayActor.MoveTo converges a real TCP-connected character to within 1.0m flat / 1.0m vertical of POST /api/actors/move target (+4 X, speed 2.0)",
                notClaimed = new[] { "routing/obstacle avoidance", "unit-follow", "autonomous selection" },
                callPath = "POST /api/actors/move → BotActionSpec(Move) → BotActionCommandQueue → GameplayActor.MoveTo → per-Tick Transform stepping (arrival 0.5f flat+Z)",
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
