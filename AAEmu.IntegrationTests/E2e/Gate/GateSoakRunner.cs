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

        // M3b gate-scale scenario: seed homesteads BEFORE the metrics window so
        // the autosave budget measures a world that contains real property
        // state. The game reads housings at boot (LoadPlayerHousing), so the
        // seeded rows need a server restart to be picked up.
        if (stage.SeedHomesteads > 0)
        {
            Console.WriteLine($"[gate] seeding {stage.SeedHomesteads} homesteads + restarting game for load");
            SeedHomesteads(stage.SeedHomesteads);
            E2eStack.RestartGameServer();
        }

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

            // -- scenario templates (P1 t_5efae4f1) -------------------------
            // The template rig on the LIVE stack: each template provisions a
            // real bot server-side (managed account + character rows + shared
            // lifecycle), drives its quest scenario through the IGameplayActor
            // contract, and must PASS. A FAIL here is a red stage (engine /
            // data / wiring defect) — the evidence block is captured verbatim
            // into the stage's scenario evidence file.
            var scenarioEvidence = new List<string>();
            if (stage.ScenarioTemplates is { Length: > 0 })
            {
                using var bridge = new BotDriveClient(E2eStack.BridgePort);
                foreach (var templateName in stage.ScenarioTemplates)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var botName = "tpl" + templateName.Replace("-", "");
                        var response = bridge.Call(
                            $"{{\"cmd\":\"scenario\",\"template\":\"{templateName}\",\"bot\":\"{botName}\"}}");
                        var scenarioPassed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
                        var failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() : "";
                        var failure = response.TryGetProperty("failure", out var fr) ? fr.GetString() : "";
                        var failReason = response.TryGetProperty("failReason", out var rr) ? rr.GetString() : "";
                        var evidence = response.TryGetProperty("evidence", out var ev) ? ev.GetString() : "";
                        scenarioEvidence.Add($"## {templateName}\n```\n{evidence}\n```");
                        if (scenarioPassed)
                        {
                            Console.WriteLine($"[gate] scenario '{templateName}' PASS");
                        }
                        else
                        {
                            failures.Add($"scenario '{templateName}': FAIL at {failStage} ({failure}) — {failReason}");
                            Console.WriteLine($"[gate] scenario '{templateName}' FAIL at {failStage} ({failure})");
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"scenario '{templateName}' threw: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                WriteScenarioEvidence(stage, scenarioEvidence);
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
                PresenceBotCount = ReadPresenceCitizenCount(),
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
                SaveMetricsAvailable = tickWorst.SaveMetricsAvailable,
                SaveSampleCount = tickWorst.SaveSampleCount,
                SaveP95Ms = tickWorst.SaveP95Ms,
                SaveMaxMs = tickWorst.SaveMaxMs,
                DbWrites = Math.Max(0, dbWritesEnd - dbWritesStart),
                PhysicsWarnings = logTail.PhysicsWarnings,
                MaxSameWorldPhysicsWarningsPer60s = logTail.MaxSameWorldPhysicsWarningsPer60s,
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

    /// <summary>
    /// Number of scheduler-stepping presence-demo citizens the game server
    /// will embody, read from the SAME env contract the server reads
    /// (AAEMU_PRESENCE_DEMO=1 → AAEMU_PRESENCE_BOT_COUNT, default 3, clamped
    /// 1..10 — mirrors BotPresenceCoordinator.ReadBotCount). 0 when the demo
    /// is not enabled. The gate process and the game server it boots share
    /// this environment, so the count is authoritative for the run.
    /// </summary>
    private static int ReadPresenceCitizenCount()
    {
        var enabled = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_DEMO") is "1" or "true" or "True";
        if (!enabled)
            return 0;

        var count = 3;
        var env = Environment.GetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT");
        if (int.TryParse(env, out var envCount) && envCount > 0)
            count = envCount;

        return Math.Clamp(count, 1, 10);
    }

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
        public bool SaveMetricsAvailable;
        public long SaveSampleCount;
        public double SaveP95Ms;
        public double SaveMaxMs;

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

            // M3b autosave budget: the server's ring buffer accumulates across
            // the window, so p95/max only grow — taking the worst observed
            // sample is the honest window value.
            if (s.Save?.Available == true)
            {
                SaveMetricsAvailable = true;
                SaveSampleCount = Math.Max(SaveSampleCount, s.Save.SampleCount);
                SaveP95Ms = Math.Max(SaveP95Ms, s.Save.P95Ms);
                SaveMaxMs = Math.Max(SaveMaxMs, s.Save.MaxMs);
            }
        }
    }

    private sealed record TickProbe(bool Available, int SubscriberCount, double InvokeP50Ms, double InvokeP95Ms, double InvokeMaxMs);
    private sealed record RegionTickProbe(bool Available, int CharactersTotal, int CharactersProcessed, int SpawnersTotal, int SpawnersProcessed, double ElapsedMs, int BudgetMs);
    private sealed record SchedulerProbe(bool Available, bool IsRunning, int WorkerCount, long TotalStepsRun, long TotalStepsFailed, long TotalStepsSkipped, double AvgWakeLatencyMs, double MaxWakeLatencyMs);
    private sealed record SaveProbe(bool Available, long SampleCount, double P95Ms, double MaxMs);
    private sealed record GateMetricsProbe(TickProbe Tick, RegionTickProbe RegionTick, SchedulerProbe Scheduler, SaveProbe Save, long UptimeMs);

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
        var save = json.TryGetProperty("save", out var sv) && sv.ValueKind == JsonValueKind.Object && sv.TryGetProperty("available", out var sva) && sva.GetBoolean()
            ? new SaveProbe(true, sv.GetProperty("sampleCount").GetInt64(), sv.GetProperty("p95Ms").GetDouble(), sv.GetProperty("maxMs").GetDouble())
            : new SaveProbe(false, 0, 0, 0);
        var uptime = json.TryGetProperty("uptimeMs", out var u) ? u.GetInt64() : 0;

        // ActiveRegionTick overruns are counted from the LOG (per-pass warning
        // lines), not from the point-in-time stats — the runner scans the log
        // delta separately; the probe only carries worst-pass elapsed.
        return new GateMetricsProbe(tick, region, sched, save, uptime);
    }

    // ------------------------------------------------------------------ homestead seeding (M3b gate-scale)

    /// <summary>
    /// Seeds N player homesteads (housings rows) into the e2e MySQL so the
    /// gate-stage world contains real property state. Templates 1/2
    /// (house_design_1/2) are used — both have build steps and no binding
    /// doodads, so the load path spawns nothing extra. The game reads
    /// housings at boot, so callers restart the game after seeding.
    /// </summary>
    private static void SeedHomesteads(int count)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO housings " +
            "(`id`,`account_id`,`owner`,`co_owner`,`template_id`,`name`,`x`,`y`,`z`,`yaw`,`pitch`,`roll`," +
            "`current_step`,`current_action`,`permission`,`place_date`,`protected_until`,`faction_id`," +
            "`sell_to`,`sell_price`,`allow_recover`) VALUES " +
            "(@id,@account,@owner,@co,@template,@name,@x,@y,@z,@yaw,@pitch,@roll," +
            "@step,@action,@perm,@place,@protected,@faction,@sellto,@sellprice,@recover)";

        // Base positions taken from the SQL seed's lodestone rows — known-good
        // world coordinates that resolve to a real zone at load.
        (float X, float Y, float Z)[] basePositions =
        [
            (19643f, 24385.4f, 168.9f),
            (19952.6f, 24275.5f, 140.4f)
        ];

        for (var i = 0; i < count; i++)
        {
            var pos = basePositions[i % basePositions.Length];
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@id", 99000 + i); // above seed rows 1-12
            cmd.Parameters.AddWithValue("@account", 1);
            cmd.Parameters.AddWithValue("@owner", 1);
            cmd.Parameters.AddWithValue("@co", 0);
            cmd.Parameters.AddWithValue("@template", i % 2 == 0 ? 1 : 2);
            cmd.Parameters.AddWithValue("@name", $"Gate Homestead {i + 1}");
            cmd.Parameters.AddWithValue("@x", pos.X + i * 2);
            cmd.Parameters.AddWithValue("@y", pos.Y + i * 2);
            cmd.Parameters.AddWithValue("@z", pos.Z);
            cmd.Parameters.AddWithValue("@yaw", 0f);
            cmd.Parameters.AddWithValue("@pitch", 0f);
            cmd.Parameters.AddWithValue("@roll", 0f);
            cmd.Parameters.AddWithValue("@step", -1); // finished house
            cmd.Parameters.AddWithValue("@action", 0);
            cmd.Parameters.AddWithValue("@perm", 0);
            cmd.Parameters.AddWithValue("@place", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@protected", DateTime.UtcNow.AddDays(14));
            cmd.Parameters.AddWithValue("@faction", 2);
            cmd.Parameters.AddWithValue("@sellto", 0);
            cmd.Parameters.AddWithValue("@sellprice", 0);
            cmd.Parameters.AddWithValue("@recover", 1);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        Console.WriteLine($"[gate] seeded {count} homestead(s) into e2e MySQL");
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

    private sealed record LogTail(long PhysicsWarnings, long TickOverrunWarnings, long MaxSameWorldPhysicsWarningsPer60s);

    private static readonly System.Text.RegularExpressions.Regex PhysicsWarningRegex = new(
        @"^(\d{2}):(\d{2}):(\d{2}) .*?in (.+?) at ",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static LogTail ReadGameLogTail(long startOffset)
    {
        long physics = 0, overruns = 0;
        // Per-world warning times (seconds-of-day, adjusted across midnight
        // wraps) for the no-sustained-slow clause: the most warnings any ONE
        // world logged within a 60s window.
        var worldTimes = new Dictionary<string, List<long>>();
        try
        {
            if (!File.Exists(GameLogPath))
                return new LogTail(0, 0, 0);

            using var fs = File.OpenRead(GameLogPath);
            if (fs.Length <= startOffset)
                return new LogTail(0, 0, 0);

            fs.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);
            var dayOffset = 0L;
            long lastSec = -1;
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("Physics thread is running slow", StringComparison.Ordinal))
                {
                    physics++;
                    var m = PhysicsWarningRegex.Match(line);
                    if (m.Success)
                    {
                        var sec = int.Parse(m.Groups[1].Value) * 3600
                                  + int.Parse(m.Groups[2].Value) * 60
                                  + int.Parse(m.Groups[3].Value);
                        // Log timestamps are HH:mm:ss only — carry a day
                        // offset forward when the clock wraps (6h soak can
                        // cross midnight).
                        if (lastSec >= 0 && sec < lastSec)
                            dayOffset += 86400;
                        lastSec = sec;
                        var world = m.Groups[4].Value;
                        if (!worldTimes.TryGetValue(world, out var times))
                            worldTimes[world] = times = [];
                        times.Add(sec + dayOffset);
                    }
                }
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

        // Sliding 60s window per world: max count of warnings on one world
        // within any 60s span.
        long maxSameWorld60s = 0;
        foreach (var times in worldTimes.Values)
        {
            times.Sort();
            var head = 0;
            for (var tail = 0; tail < times.Count; tail++)
            {
                while (times[tail] - times[head] > 60)
                    head++;
                maxSameWorld60s = Math.Max(maxSameWorld60s, tail - head + 1);
            }
        }

        return new LogTail(physics, overruns, maxSameWorld60s);
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
        sb.AppendLine($"> Window: {s.WindowMinutes:F1} min · bots: {s.BotCount}" +
                      (s.PresenceBotCount > 0
                          ? $" + {s.PresenceBotCount} presence citizens = {s.EmbodiedCharacterCount} embodied (DB-write budget normalizes per embodied char)"
                          : " (DB-write budget normalizes per bot)"));
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

    /// <summary>
    /// Writes the scenario-template evidence block for a stage (one section
    /// per template, verbatim runner evidence). Deterministic content —
    /// only the filename carries the wall-clock.
    /// </summary>
    private static void WriteScenarioEvidence(GateStageConfig stage, IReadOnlyList<string> sections)
    {
        var dir = Path.Combine(E2eStack.E2eRoot, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"gate-{stage.Name}-scenarios-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");

        var sb = new StringBuilder();
        sb.AppendLine($"# Gate stage {stage.Name} — scenario templates (P1 t_5efae4f1)");
        sb.AppendLine();
        sb.AppendLine($"> Generated by GateSoakRunner. Templates provision REAL bots server-side");
        sb.AppendLine($"> (managed account + character rows + shared lifecycle) and drive quest scenarios");
        sb.AppendLine($"> through the IGameplayActor contract on the live stack.");
        sb.AppendLine();
        foreach (var section in sections)
        {
            sb.AppendLine(section);
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"[gate] scenario evidence written: {path}");
    }
}
