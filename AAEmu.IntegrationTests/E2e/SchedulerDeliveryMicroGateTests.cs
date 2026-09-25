using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Scheduler-delivery micro-gate (no quest logic): proves the production
/// scheduler delivers an attributable StepAsync to OUR freshly-enrolled
/// networked bot within a 30s budget. Per-bot proof only (wakeSeq growth,
/// questLegActive flip, non-empty questTravelReason, or own-actor trace
/// growth above the enroll baseline); the GLOBAL `stepped` flag
/// (TotalStepsRun) is recorded contrast-only, never proof.
/// </summary>
[Collection("e2e")]
public class SchedulerDeliveryMicroGateTests
{
    private static readonly string Stamp = DateTime.UtcNow.ToString("HHmmss");
    private static readonly string BotName = "SchedGate" + Stamp;
    private static readonly string BotAccount = ("schedgate" + Stamp).ToLowerInvariant();
    private const string Password = "e2e-secret";

    private const int WakeWaitMs = 30000;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "scheduler-microgate-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task SchedulerWake_DeliversPerBotStep()
    {
        var totalWall = Stopwatch.StartNew();
        var setupSw = Stopwatch.StartNew();
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var evidence = new StringBuilder();
        evidence.AppendLine($"# scheduler-delivery micro-gate — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        void Leg(string leg, bool passed, string detail, double ms)
        {
            legs.Add((leg, passed, detail, ms));
            evidence.AppendLine($"- [{(passed ? "x" : " ")}] {leg} ({ms:0.0}s): {detail}");
        }

        BotNetworkSession? session = null;
        BotDriveClient? laneBridge = null;
        var setupSeconds = 0.0;
        var execSeconds = 0.0;
        var passed = false;
        string verdict = "UNKNOWN";
        string failBoundary = "";
        object? failingCondition = null;
        uint ourCharacterId = 0;
        int baselineQuestActions = 0;
        long baselineWakeSeq = 0, baselineTraceWakeSeq = 0, baselineMaxTraceWakeSeq = 0;
        string wakeSummary = "UNAVAILABLE";

        try
        {
            // ---- ADOPT (probe-only; never EnsureUp/Restart) ----
            var adoptSw = Stopwatch.StartNew();
            var staleGuardDetail = KillForeignWebApiListeners();
            evidence.AppendLine($"- stale-proc guard (shared :{E2eStack.WebApiPort} listeners outside this lane): {staleGuardDetail}");
            var laneOk = await ProbeLaneAsync(TimeSpan.FromSeconds(60));
            Leg("adopt-lane", laneOk,
                laneOk ? "warm lane answering (WebApi TCP + bridge ping)" : "lane cold — refusing to rebuild",
                adoptSw.Elapsed.TotalSeconds);
            if (!laneOk)
            {
                failBoundary = "SETUP/lane-down";
                Assert.Fail("warm Q0 lane not answering; refusing to rebuild per workstream constraints");
            }

            AdoptLaneDbPassword();
            var bridge = new BotDriveClient(E2eStack.BridgePort);
            laneBridge = bridge;

            // ---- SETUP: real TCP enter-world ----
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

            // ---- ENROLL via production path (per-bot baseline; GLOBAL
            // `stepped` recorded contrast-only, never proof) ----
            var enrollSw = Stopwatch.StartNew();
            var enroll = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":1000}}", 60_000);
            var enrollSteppedGlobal = enroll.TryGetProperty("stepped", out var stEl) && stEl.GetBoolean();
            ourCharacterId = GetUInt(enroll, "id");
            baselineQuestActions = GetInt(enroll, "questActionCount");
            baselineWakeSeq = GetSeq(enroll, "wakeSeq");
            baselineTraceWakeSeq = GetSeq(enroll, "traceWakeSeq");
            baselineMaxTraceWakeSeq = GetSeq(enroll, "maxTraceWakeSeq");
            setupSeconds = setupSw.Elapsed.TotalSeconds;
            var enrolled = ourCharacterId != 0;
            Leg("enroll", enrolled,
                $"charId={ourCharacterId} baselineQuestActions={baselineQuestActions} baselineWakeSeq={baselineWakeSeq} baselineTraceWakeSeq={baselineTraceWakeSeq} baselineMaxTraceWakeSeq={baselineMaxTraceWakeSeq} steppedGlobal={enrollSteppedGlobal}(contrast-only)",
                enrollSw.Elapsed.TotalSeconds);
            if (!enrolled)
            {
                failBoundary = "SETUP/enroll-no-id";
                Assert.Fail("enroll wake returned no character id");
            }

            // ---- START: one bounded production wake, then observe ONLY ----
            // No drive/gameplay commands after START — observe-only polling.
            var execSw = Stopwatch.StartNew();
            var wakeStart = Environment.TickCount64;
            var wake = bridge.Call(
                $"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"wake\",\"waitMs\":{WakeWaitMs}}}", 120_000);
            if (ourCharacterId == 0)
                ourCharacterId = GetUInt(wake, "id");
            var observe = wake;
            var wakeProven = HasPerBotStepSignal(observe, ourCharacterId, baselineQuestActions, baselineWakeSeq, baselineTraceWakeSeq, baselineMaxTraceWakeSeq);
            while (!wakeProven && Environment.TickCount64 - wakeStart < WakeWaitMs)
            {
                Thread.Sleep(1000);
                try
                {
                    observe = bridge.Call($"{{\"cmd\":\"quest\",\"bot\":\"{BotName}\",\"sub\":\"observe\"}}", 60_000);
                }
                catch (Exception ex)
                {
                    wakeSummary = $"UNPROVEN (observe poll failed: {ex.GetType().Name})";
                    break;
                }
                wakeProven = HasPerBotStepSignal(observe, ourCharacterId, baselineQuestActions, baselineWakeSeq, baselineTraceWakeSeq, baselineMaxTraceWakeSeq);
            }
            var wakeSteppedGlobal = wake.TryGetProperty("stepped", out var wsEl) && wsEl.GetBoolean();
            wakeSummary = (wakeProven ? "PROVEN per-bot" : "UNPROVEN (no per-bot step signal within 30s)")
                + $" charId={ourCharacterId} [{SummarizeWake(observe)}] steppedGlobal={wakeSteppedGlobal}(contrast-only, GLOBAL TotalStepsRun)";
            execSeconds = execSw.Elapsed.TotalSeconds;
            Leg("wake", wakeProven, wakeSummary, execSw.Elapsed.TotalSeconds);

            var (bootstrapOn, bootstrapGameValue, bootstrapSource) = ReadGameSideBootstrapFlag();
            var bootstrapSummary = $"gameSide={bootstrapGameValue ?? "<absent>"} [{bootstrapSource}]";

            passed = wakeProven;
            if (passed)
            {
                verdict = "PASS";
                failBoundary = "none";
                failingCondition = null;
            }
            else
            {
                verdict = "FAIL";
                failBoundary = "SCHEDULER/no-per-bot-step";
                failingCondition = new
                {
                    boundary = failBoundary,
                    wakeSummary,
                    ourCharacterId,
                    baselineQuestActions,
                    baselineWakeSeq,
                    baselineTraceWakeSeq,
                    baselineMaxTraceWakeSeq,
                    questActionCount = GetInt(observe, "questActionCount"),
                    wakeSeq = GetSeq(observe, "wakeSeq"),
                    traceWakeSeq = GetSeq(observe, "traceWakeSeq"),
                    maxTraceWakeSeq = GetSeq(observe, "maxTraceWakeSeq"),
                    lastActorId = GetUInt(observe, "lastActorId"),
                    bootstrapEnabledGameSide = bootstrapOn,
                    bootstrap = bootstrapSummary
                };
            }
            Leg("scheduler-delivery", passed, $"{verdict} at {failBoundary}: [{wakeSummary}] [{bootstrapSummary}]",
                execSw.Elapsed.TotalSeconds);
            Assert.True(passed,
                $"scheduler-microgate {verdict} at {failBoundary}: charId={ourCharacterId} [{wakeSummary}] [{bootstrapSummary}]");
        }
        finally
        {
            totalWall.Stop();
            session?.Dispose();
            var cleanupSummary = TryReleaseBot(laneBridge, BotName, ourCharacterId);
            await WriteReportAsync(passed, verdict, failBoundary, failingCondition, legs, evidence.ToString(),
                new { charId = ourCharacterId, bot = BotName, wakeSummary, cleanupSummary },
                setupSeconds, execSeconds, totalWall.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// PER-BOT step proof for OUR characterId (never GLOBAL TotalStepsRun):
    /// questLegActive true, non-empty questTravelReason, quest-action audit
    /// rows above the enroll baseline, or per-bot wake-sequence growth above
    /// baseline (wakeSeq / traceWakeSeq / maxTraceWakeSeq).
    /// </summary>
    private static bool HasPerBotStepSignal(JsonElement sample, uint ourCharacterId, int baselineQuestActions, long baselineWakeSeq = 0, long baselineTraceWakeSeq = 0, long baselineMaxTraceWakeSeq = 0)
    {
        if (sample.TryGetProperty("questLegActive", out var leg) && leg.GetBoolean())
            return true;
        if (sample.TryGetProperty("questTravelReason", out var reason)
            && !string.IsNullOrEmpty(reason.GetString()))
            return true;
        if (GetInt(sample, "questActionCount") > baselineQuestActions)
            return true;
        if (ourCharacterId != 0
            && GetUInt(sample, "lastActorId") == ourCharacterId
            && GetInt(sample, "questActionCount") > baselineQuestActions)
            return true;
        if (GetSeq(sample, "wakeSeq") > baselineWakeSeq && GetSeq(sample, "wakeSeq") > 0)
            return true;
        if (GetSeq(sample, "traceWakeSeq") > baselineTraceWakeSeq && GetSeq(sample, "traceWakeSeq") > 0)
            return true;
        if (GetSeq(sample, "maxTraceWakeSeq") > baselineMaxTraceWakeSeq && GetSeq(sample, "maxTraceWakeSeq") > 0)
            return true;
        return false;
    }

    private static uint GetUInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetUInt32(out var n) ? n : 0;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static long GetSeq(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>
    /// GAME-side bootstrap flag read (never runner env): scans
    /// /proc/&lt;pid&gt;/environ of the live game process (dotnet hosting
    /// AAEmu.Game.dll) for AAEMU_QUEST_BOOTSTRAP_ENABLED.
    /// </summary>
    private static (bool On, string? Raw, string Source) ReadGameSideBootstrapFlag()
    {
        const string flag = "AAEMU_QUEST_BOOTSTRAP_ENABLED";
        try
        {
            foreach (var proc in Process.GetProcessesByName("dotnet"))
            {
                string cmdline;
                try { cmdline = File.ReadAllText($"/proc/{proc.Id}/cmdline").Replace('\0', ' '); }
                catch { continue; }
                if (!cmdline.Contains("AAEmu.Game.dll", StringComparison.Ordinal))
                    continue;
                string env;
                try { env = File.ReadAllText($"/proc/{proc.Id}/environ"); }
                catch (Exception ex) { return (false, null, $"game-proc-environ pid={proc.Id} UNREADABLE ({ex.GetType().Name})"); }
                foreach (var entry in env.Split('\0'))
                {
                    if (!entry.StartsWith(flag + "=", StringComparison.Ordinal))
                        continue;
                    var raw = entry[(flag.Length + 1)..];
                    return (raw is "1" or "true" or "True", raw, $"game-proc-environ pid={proc.Id}");
                }
                return (false, null, $"game-proc-environ pid={proc.Id} (flag absent)");
            }
            return (false, null, "game-proc-missing (no dotnet/AAEmu.Game.dll process found)");
        }
        catch (Exception ex)
        {
            return (false, null, $"game-proc-environ UNAVAILABLE ({ex.GetType().Name})");
        }
    }

    private static string TryReleaseBot(BotDriveClient? bridge, string botName, uint characterId)
    {
        if (bridge == null)
            return "UNAVAILABLE (no bridge handle; TCP session disposed, registry entry left in place)";
        try
        {
            var res = bridge.Call($"{{\"cmd\":\"deactivate\",\"bot\":\"{botName}\"}}", 30_000);
            if (res.TryGetProperty("removed", out var removed) && removed.GetBoolean())
                return $"RELEASED via bridge deactivate (charId={characterId})";
            if (res.TryGetProperty("error", out var err))
                return $"UNAVAILABLE (bridge deactivate refused for networked char '{botName}' charId={characterId}: {err.GetString()}; TCP disposed, registry entry left in place)";
            return $"UNAVAILABLE (bridge deactivate unexpected shape for networked char '{botName}' charId={characterId}: {res}; TCP disposed, registry entry left in place)";
        }
        catch (Exception ex)
        {
            return $"UNAVAILABLE (bridge deactivate threw {ex.GetType().Name} for networked char '{botName}' charId={characterId}; TCP disposed, registry entry left in place)";
        }
    }

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
                Console.WriteLine("[sched] " + line);
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
                    Console.WriteLine("[sched] " + kline);
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

    private static string SummarizeWake(JsonElement wake)
    {
        try
        {
            var stepped = wake.TryGetProperty("stepped", out var s) && s.GetBoolean();
            var leg = wake.TryGetProperty("questLegActive", out var l) && l.GetBoolean();
            var reason = wake.TryGetProperty("questTravelReason", out var r) ? r.GetString() ?? "" : "";
            return $"stepped={stepped} questLegActive={leg} questActions={GetInt(wake, "questActionCount")} lastActorId={GetUInt(wake, "lastActorId")} wakeSeq={GetSeq(wake, "wakeSeq")} traceWakeSeq={GetSeq(wake, "traceWakeSeq")} maxTraceWakeSeq={GetSeq(wake, "maxTraceWakeSeq")} reason=[{reason}]";
        }
        catch
        {
            return "UNAVAILABLE (wake shape drift)";
        }
    }

    private async Task WriteReportAsync(bool passed, string verdict, string failBoundary,
        object? failingCondition,
        List<(string Leg, bool Passed, string Detail, double Ms)> legs, string evidenceText,
        object fixture, double setupSeconds, double execSeconds, double wallSeconds)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var (bootstrapOn, bootstrapGameValue, bootstrapSource) = ReadGameSideBootstrapFlag();
            var report = new
            {
                scenario = "scheduler-delivery-microgate",
                verdict,
                failBoundary = string.IsNullOrEmpty(failBoundary) ? "none" : failBoundary,
                callPath = "bridge quest wake → manager Spawn/Activate + scheduler Wake → per-bot StepAsync (no quest logic in this gate)",
                failingCondition,
                timings = new
                {
                    setupSeconds = Math.Round(setupSeconds, 1),
                    execSeconds = Math.Round(execSeconds, 1),
                    wallSeconds = Math.Round(wallSeconds, 1)
                },
                legs = legs.Select(l => new { leg = l.Leg, passed = l.Passed, detail = l.Detail, seconds = Math.Round(l.Ms, 1) }).ToList(),
                fixture,
                wakeSignals = new
                {
                    bootstrapEnabledGameSide = bootstrapOn,
                    bootstrapGameValue = bootstrapGameValue ?? "<absent>",
                    bootstrapSource
                },
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
