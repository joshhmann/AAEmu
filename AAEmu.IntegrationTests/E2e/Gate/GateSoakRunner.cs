using System.Text;
using System.Text.Json;
using AAEmu.Commons.Utils.Gate;
using MySql.Data.MySqlClient;

namespace AAEmu.IntegrationTests.E2e.Gate;

/// <summary>
/// Gate harness — staged soak runner (ARCHITECTURE_REVIEW deliverable 8/10,
/// slice #10). Boots the REAL E2E stack (MySQL compose + Login + Game, same
/// binaries as prod), embodies N bots through the REAL login/enter-world
/// flow, drives golden-route quests through the BotDriveBridge, then samples
/// a metrics window and fails HARD on the first budget overrun.
///
/// Metrics sampled per window:
///   - TickManager invoke p95/max + ActiveRegionTick worst pass (H2 bridge
///     surface — worst-of-three samples taken at window start/mid/end)
///   - PlayerBotScheduler wake latency (bridge surface; n/a when the citizen
///     path isn't wired)
///   - DB writes: MySQL SHOW GLOBAL STATUS Com_* deltas across the window
///   - physics warning rate + tick overrun rate: game-log scan across the
///     window ("Physics thread is running slow", "Tick took", ActiveRegionTick
///     over-budget lines)
///
/// Evidence: one markdown file per stage under E2E_ROOT/logs/gate-&lt;stage&gt;.md.
/// </summary>
public static class GateSoakRunner
{
    // Solzreed golden route (same curriculum as the M2b E2E rig).
    private static readonly uint[] GoldenRoute =
        [251, 330, 252, 254, 255, 256, 257, 259, 260, 261, 265, 266, 354, 4292, 4294, 4295];

    private static readonly Dictionary<uint, E2eQuestManifest> Manifests = LoadManifests();

    public static string GameLogPath => Path.Combine(E2eStack.E2eRoot, "logs", "game.log");

    private static Dictionary<uint, E2eQuestManifest> LoadManifests()
    {
        var manifestDir = Path.Combine(E2eStack.RepoRoot, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t1");
        var result = new Dictionary<uint, E2eQuestManifest>();
        foreach (var questId in GoldenRoute)
        {
            var path = Path.Combine(manifestDir, $"{questId}.json");
            if (!File.Exists(path))
                throw new InvalidOperationException($"manifest missing: {path}");
            result[questId] = E2eQuestManifest.LoadFromFile(path);
        }

        return result;
    }

    /// <summary>
    /// Runs one stage: stack up → N bots in-world → quest drive → metrics
    /// window → budget enforcement → evidence file. Throws on setup failure;
    /// returns a failed result (with verdicts) when a budget overruns — the
    /// caller decides how a red stage surfaces.
    /// </summary>
    public static async Task<GateStageResult> RunStageAsync(GateStageConfig stage, CancellationToken ct = default)
    {
        Console.WriteLine($"[gate] stage {stage.Name}: {stage.BotCount} bots, window {(stage.SoakMinutes > 0 ? stage.SoakMinutes : stage.WindowMinutes)}min (soak={stage.SoakMinutes > 0})");
        E2eStack.EnsureUp();

        var failures = new List<string>();
        var windowMinutes = stage.SoakMinutes > 0 ? stage.SoakMinutes : stage.WindowMinutes;

        // -- H2 probe (stage 25 gate) -------------------------------------
        GateMetricsProbe h2Probe;
        using (var probeClient = new BotDriveClient(E2eStack.BridgePort))
        {
            h2Probe = await ProbeMetricsAsync(probeClient, ct);
        }

        // -- connect N bots through the REAL login flow --------------------
        var bots = new List<(string Account, string CharName, BotNetworkSession Session)>();
        try
        {
            for (var i = 1; i <= stage.BotCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var account = $"gate{stage.BotCount}b{i:D2}";
                var charName = $"g{stage.BotCount}b{i:D2}";
                var session = await BotNetworkSession.ConnectAsync(
                    charName, account, "e2e-secret",
                    "127.0.0.1", E2eStack.LoginPort,
                    "127.0.0.1", E2eStack.GamePort,
                    "127.0.0.1", E2eStack.StreamPort);
                bots.Add((account, charName, session));
                if (i % 10 == 0)
                    Console.WriteLine($"[gate] {i}/{stage.BotCount} bots in-world");
            }
        }
        catch (Exception ex)
        {
            // A bot that fails to enter the world is a correctness failure.
            // Teardown still runs via the finally below — never leak sessions.
            failures.Add($"bot connect failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            // -- quest drive (correctness / activity) ----------------------
            if (stage.QuestSubset > 0)
            {
                using var bridge = new BotDriveClient(E2eStack.BridgePort);
                var driveQuestIds = GoldenRoute.Take(stage.QuestSubset).ToArray();
                var drives = 0;
                foreach (var (_, charName, _) in bots)
                {
                    foreach (var questId in driveQuestIds)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var manifest = Manifests[questId];
                            var result = E2eQuestDriver.DriveQuest(bridge, charName, manifest, manifest.Level);
                            if (!result.Passed)
                                failures.Add($"bot {charName} quest {questId}: " + result.ReproTrace());
                        }
                        catch (Exception ex)
                        {
                            // One quest's bridge failure is a red stage but must
                            // NOT leak sessions or skip evidence — record and
                            // continue so the metrics window still runs.
                            failures.Add($"bot {charName} quest {questId} threw: {ex.GetType().Name}: {ex.Message}");
                        }

                        drives++;
                    }
                }

                Console.WriteLine($"[gate] quest drive done: {drives} drives, {failures.Count} failures");
            }

            // -- metrics window ----------------------------------------------
            long dbWritesStart = 0, dbWritesEnd = 0;
            long logLenStart = 0;
            var tickWorst = new TickSample();
            DateTime windowStart;
            try
            {
                using var bridge = new BotDriveClient(E2eStack.BridgePort);

                // Window start: DB write counters + log offset + first probe.
                dbWritesStart = ReadDbWriteCounters();
                logLenStart = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
                windowStart = DateTime.UtcNow;
                var s0 = await ProbeMetricsAsync(bridge, ct);
                tickWorst.Merge(s0);

                var deadline = windowStart.AddMinutes(windowMinutes);
                var samples = 1;
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, (deadline - DateTime.UtcNow).TotalSeconds)), ct);
                    var s = await ProbeMetricsAsync(bridge, ct);
                    tickWorst.Merge(s);
                    samples++;
                    Console.WriteLine($"[gate] sample {samples}: tick p95={s.Tick?.InvokeP95Ms ?? -1:F1}ms regionElapsed={s.RegionTick?.ElapsedMs ?? -1}ms");
                }

                dbWritesEnd = ReadDbWriteCounters();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A dead bridge (crashed game server) must NOT abort the stage
                // before evidence lands — record the failure and write the
                // evidence file with whatever the window captured (zeroed
                // metrics). The failure list is the stage's contract.
                failures.Add($"metrics window failed: {ex.GetType().Name}: {ex.Message} — bridge unreachable (game server likely crashed); budgets not sampled");
                windowStart = DateTime.UtcNow;
            }

            var windowSpan = DateTime.UtcNow - windowStart;
            var logTail = ReadGameLogTail(logLenStart);

            var snapshot = new GateMetricsSnapshot
            {
                WindowMinutes = windowSpan.TotalMinutes,
                BotCount = stage.BotCount,
                TickMetricsAvailable = h2Probe.Tick?.Available == true,
                TickInvokeP95Ms = tickWorst.TickP95Ms,
                TickInvokeMaxMs = tickWorst.TickMaxMs,
                TickSubscriberCount = tickWorst.SubscriberCount,
                RegionTickBudgetAvailable = h2Probe.RegionTick?.Available == true,
                RegionTickMaxElapsedMs = tickWorst.RegionTickMaxElapsedMs,
                RegionTickOverruns = tickWorst.RegionTickOverruns,
                SchedulerStarted = tickWorst.SchedulerStarted,
                SchedulerStepsRun = tickWorst.SchedulerStepsRun,
                SchedulerStepsFailed = tickWorst.SchedulerStepsFailed,
                SchedulerAvgWakeLatencyMs = tickWorst.SchedulerAvgWakeLatencyMs,
                SchedulerMaxWakeLatencyMs = tickWorst.SchedulerMaxWakeLatencyMs,
                DbWrites = Math.Max(0, dbWritesEnd - dbWritesStart),
                PhysicsWarnings = logTail.PhysicsWarnings,
                TickOverrunWarnings = logTail.TickOverrunWarnings
            };

            var verdicts = GateBudgetEvaluator.Evaluate(snapshot, stage.Budgets, stage.RequireH2);
            var overruns = verdicts.Where(v => !v.Passed).ToList();
            foreach (var f in overruns)
                failures.Add($"{f.Name}: {f.Detail} (measured {f.Measured} / limit {f.Limit})");

            var evidencePath = WriteEvidence(stage, snapshot, verdicts, failures);

            var passed = failures.Count == 0;
            var detail = passed
                ? $"stage {stage.Name} GREEN — {stage.BotCount} bots, {windowSpan.TotalMinutes:F1}min window, {verdicts.Count(v => !v.NotApplicable)}/{verdicts.Count} budgets enforced"
                : $"stage {stage.Name} RED — {failures.Count} failure(s): " + string.Join("; ", failures.Take(5));

            Console.WriteLine($"[gate] {detail}");
            return new GateStageResult(stage.Name, passed, windowSpan, verdicts, failures, evidencePath, detail);
        }
        finally
        {
            // -- teardown (ALWAYS runs — failures must not leak sessions) ---
            foreach (var (account, _, session) in bots)
            {
                try
                {
                    session.Disconnect();
                }
                catch
                {
                }

                try
                {
                    E2eStack.CleanupBotRows(account);
                }
                catch
                {
                }
            }
        }
    }

    // ------------------------------------------------------------------ probes

    private sealed class TickSample
    {
        public double TickP95Ms = -1;
        public double TickMaxMs = -1;
        public int SubscriberCount;
        public double RegionTickMaxElapsedMs = -1;
        public long RegionTickOverruns;
        public bool SchedulerStarted;
        public long SchedulerStepsRun;
        public long SchedulerStepsFailed;
        public double SchedulerAvgWakeLatencyMs;
        public double SchedulerMaxWakeLatencyMs;

        public void Merge(GateMetricsProbe s)
        {
            if (s.Tick?.Available == true)
            {
                TickP95Ms = Math.Max(TickP95Ms, s.Tick.InvokeP95Ms);
                TickMaxMs = Math.Max(TickMaxMs, s.Tick.InvokeMaxMs);
                SubscriberCount = Math.Max(SubscriberCount, s.Tick.SubscriberCount);
            }

            if (s.RegionTick?.Available == true)
            {
                RegionTickMaxElapsedMs = Math.Max(RegionTickMaxElapsedMs, s.RegionTick.ElapsedMs);
            }

            if (s.Scheduler?.Available == true)
            {
                SchedulerStarted |= s.Scheduler.IsRunning;
                SchedulerStepsRun = Math.Max(SchedulerStepsRun, s.Scheduler.TotalStepsRun);
                SchedulerStepsFailed = Math.Max(SchedulerStepsFailed, s.Scheduler.TotalStepsFailed);
                SchedulerAvgWakeLatencyMs = Math.Max(SchedulerAvgWakeLatencyMs, s.Scheduler.AvgWakeLatencyMs);
                SchedulerMaxWakeLatencyMs = Math.Max(SchedulerMaxWakeLatencyMs, s.Scheduler.MaxWakeLatencyMs);
            }
        }
    }

    private sealed record TickProbe(bool Available, int SubscriberCount, double InvokeP50Ms, double InvokeP95Ms, double InvokeMaxMs);
    private sealed record RegionTickProbe(bool Available, int CharactersTotal, int CharactersProcessed, int SpawnersTotal, int SpawnersProcessed, double ElapsedMs, int BudgetMs);
    private sealed record SchedulerProbe(bool Available, bool IsRunning, int WorkerCount, long TotalStepsRun, long TotalStepsFailed, long TotalStepsSkipped, double AvgWakeLatencyMs, double MaxWakeLatencyMs);
    private sealed record GateMetricsProbe(TickProbe Tick, RegionTickProbe RegionTick, SchedulerProbe Scheduler, long UptimeMs);

    private static async Task<GateMetricsProbe> ProbeMetricsAsync(BotDriveClient bridge, CancellationToken ct)
    {
        var json = await Task.Run(() => bridge.Call("{\"cmd\":\"metrics\"}"), ct);
        var tick = json.TryGetProperty("tick", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("available", out var ta) && ta.GetBoolean()
            ? new TickProbe(true, t.GetProperty("subscriberCount").GetInt32(), t.GetProperty("invokeP50Ms").GetDouble(), t.GetProperty("invokeP95Ms").GetDouble(), t.GetProperty("invokeMaxMs").GetDouble())
            : new TickProbe(false, 0, 0, 0, 0);
        var region = json.TryGetProperty("regionTick", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("available", out var ra) && ra.GetBoolean()
            ? new RegionTickProbe(true, r.GetProperty("charactersTotal").GetInt32(), r.GetProperty("charactersProcessed").GetInt32(), r.GetProperty("spawnersTotal").GetInt32(), r.GetProperty("spawnersProcessed").GetInt32(), r.GetProperty("elapsedMs").GetDouble(), r.GetProperty("budgetMs").GetInt32())
            : new RegionTickProbe(false, 0, 0, 0, 0, 0, 0);
        var sched = json.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object && sc.TryGetProperty("available", out var sa) && sa.GetBoolean()
            ? new SchedulerProbe(true, sc.GetProperty("isRunning").GetBoolean(), sc.GetProperty("workerCount").GetInt32(), sc.GetProperty("totalStepsRun").GetInt64(), sc.GetProperty("totalStepsFailed").GetInt64(), sc.GetProperty("totalStepsSkipped").GetInt64(), sc.GetProperty("avgWakeLatencyMs").GetDouble(), sc.GetProperty("maxWakeLatencyMs").GetDouble())
            : new SchedulerProbe(false, false, 0, 0, 0, 0, 0, 0);
        var uptime = json.TryGetProperty("uptimeMs", out var u) ? u.GetInt64() : 0;

        // ActiveRegionTick overruns are counted from the LOG (per-pass warning
        // lines), not from the point-in-time stats — the runner scans the log
        // delta separately; the probe only carries worst-pass elapsed.
        return new GateMetricsProbe(tick, region, sched, uptime);
    }

    // ------------------------------------------------------------------ DB / log

    private static long ReadDbWriteCounters()
    {
        long total = 0;
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SHOW GLOBAL STATUS WHERE Variable_name IN ('Com_insert','Com_update','Com_delete','Com_replace')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                total += reader.GetInt64(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gate] db counter read failed: {ex.GetType().Name}: {ex.Message}");
        }

        return total;
    }

    private sealed record LogTail(long PhysicsWarnings, long TickOverrunWarnings);

    private static LogTail ReadGameLogTail(long startOffset)
    {
        long physics = 0, overruns = 0;
        try
        {
            if (!File.Exists(GameLogPath))
                return new LogTail(0, 0);

            using var fs = File.OpenRead(GameLogPath);
            if (fs.Length <= startOffset)
                return new LogTail(0, 0);

            fs.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("Physics thread is running slow", StringComparison.Ordinal))
                    physics++;
                if (line.Contains("Tick took ", StringComparison.Ordinal) ||
                    line.Contains("over 100ms budget", StringComparison.Ordinal) ||
                    line.Contains("ActiveRegionTick took", StringComparison.Ordinal))
                    overruns++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gate] game log scan failed: {ex.GetType().Name}: {ex.Message}");
        }

        return new LogTail(physics, overruns);
    }

    // ------------------------------------------------------------------ evidence

    private static string WriteEvidence(GateStageConfig stage, GateMetricsSnapshot s, IReadOnlyList<BudgetVerdict> verdicts, IReadOnlyList<string> failures)
    {
        var dir = Path.Combine(E2eStack.E2eRoot, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"gate-{stage.Name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");

        var sb = new StringBuilder();
        sb.AppendLine($"# Gate stage {stage.Name} — {stage.BotCount} bots");
        sb.AppendLine();
        sb.AppendLine($"> Generated by GateSoakRunner (deterministic budgets; wall-clock only for the window).");
        sb.AppendLine($"> Stack: REAL Login (:1237) + Game (:1239/:1250) + MySQL, canonical compact.sqlite3, bots over the REAL network path.");
        sb.AppendLine($"> Window: {s.WindowMinutes:F1} min · bots: {s.BotCount}");
        sb.AppendLine();
        sb.AppendLine("| Metric | Measured | Limit | Verdict |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var v in verdicts)
        {
            var tag = v.NotApplicable ? "n/a" : v.Passed ? "PASS" : "**FAIL**";
            sb.AppendLine($"| {v.Name} | {v.Measured:F2} | {v.Limit:F2} | {tag} |");
        }

        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failures (every entry is a regression card)");
            sb.AppendLine();
            foreach (var f in failures)
                sb.AppendLine($"- {f.Replace("\n", " ")}");
        }

        sb.AppendLine();
        sb.AppendLine("## Raw snapshot");
        sb.AppendLine();
        sb.AppendLine($"```json");
        sb.AppendLine(JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine($"```");

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"[gate] evidence written: {path}");
        return path;
    }
}
