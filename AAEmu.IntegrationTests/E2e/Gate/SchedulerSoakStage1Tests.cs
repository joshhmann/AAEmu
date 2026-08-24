using System.Diagnostics;
using System.Text;
using System.Text.Json;

using AAEmu.Commons.Utils.Gate;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.Gate;

/// <summary>
/// STAGE 1 of the scheduler-driven soak (closes the M6 exit caveat "previous
/// soaks ran with PlayerBotScheduler DISABLED"): boots the REAL e2e stack
/// with the presence demo ENABLED and a 10-citizen manifest roster
/// ("Bots.PresenceManifest" / AAEMU_PRESENCE_MANIFEST seam), so every bot's
/// work flows through the real IPlayerBotScheduler lease/wake path
/// (BotPresenceBootstrap → BotPresenceCoordinator → PlayerBotScheduler.Wake),
/// then samples a 30-minute window and fails hard on the repo's numeric
/// budgets.
///
/// RUN VALIDITY CONTRACT: the run is INVALID unless the bridge metrics show
/// scheduler available=true AND totalStepsRun>0 (and still growing at window
/// end) — an engine/harness defect that silently leaves the scheduler off
/// must never read as a green soak.
///
/// Budgets mirror the existing gate numbers exactly (no new numerics):
///   - GateBudgets defaults (scheduler wake avg ≤250ms / max ≤1000ms,
///     step failures 0, tick p95 ≤100ms / max ≤250ms, DB ≤500/min/embodied-
///     char, physics ≤0.1/min + ≤30 same-world/60s, autosave p95 ≤4000ms /
///     max ≤10000ms)
///   - GateStages.SoakBudgets idle-stage overrides (ActiveRegionTick worst
///     pass ≤200ms, tick-overrun warnings ≤0.1/min)
///   - Scheduler step timeouts: reported and enforced at 0 — the same
///     zero-tolerance clause as MaxSchedulerStepFailures applied to its
///     sibling counter (a timeout is a cancelled step, not a cheap skip).
///
/// Evidence: structured JSON + markdown summary under
/// $E2E_ROOT/logs/scheduler-soak-stage1-*.
/// </summary>
[Collection("e2e")]
public class SchedulerSoakStage1Tests
{
    /// <summary>
    /// Stage 1 — 10 manifest citizens driven by real scheduler wakes for
    /// 30 minutes (SCHEDULER_SOAK_MINUTES overrides; smaller values are for
    /// smoke runs). Skipped unless explicitly requested: a 30-min soak is a
    /// scheduled gate run, not something a plain suite invocation should
    /// block on (same convention as Gate_Stage50_Soak).
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task SchedulerSoak_Stage1_SchedulerDriven()
    {
        var soakEnv = Environment.GetEnvironmentVariable("SCHEDULER_SOAK_MINUTES");
        if (string.IsNullOrWhiteSpace(soakEnv))
        {
            Assert.Skip("SCHEDULER_SOAK_MINUTES not set — stage-1 scheduler-driven soak (30min) is an explicit gate run.");
            return;
        }

        if (!int.TryParse(soakEnv, out var soakMinutes) || soakMinutes <= 0)
            throw new InvalidOperationException($"SCHEDULER_SOAK_MINUTES must be a positive integer, got '{soakEnv}'");

        var result = await SchedulerSoakStage1Runner.RunAsync(soakMinutes, CancellationToken.None);
        Assert.True(result.Passed, result.Detail + "\nEvidence: " + result.EvidenceMdPath);
    }
}

/// <summary>Result of one scheduler-driven soak stage.</summary>
public sealed record SchedulerSoakStage1Result(
    string StageName,
    bool Passed,
    bool Valid,
    TimeSpan Window,
    IReadOnlyList<BudgetVerdict> Verdicts,
    IReadOnlyList<string> Failures,
    string EvidenceJsonPath,
    string EvidenceMdPath,
    string Detail);

/// <summary>
/// The stage-1 runner: manifest provisioning + env enablement before boot,
/// validity gate, sampled window, budget enforcement, dual evidence files.
/// </summary>
public static class SchedulerSoakStage1Runner
{
    private const string StageName = "scheduler-soak-stage1";

    /// <summary>Citizens provisioned through the manifest seam (~10 per task spec).</summary>
    public const int CitizenCount = 10;

    /// <summary>Sampling cadence (~every 30s per stage-1 spec).</summary>
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(30);

    public static async Task<SchedulerSoakStage1Result> RunAsync(int minutes, CancellationToken ct)
    {
        Console.WriteLine($"[{StageName}] {CitizenCount} manifest citizens, {minutes}min scheduler-driven window");

        // -- enablement BEFORE boot ------------------------------------------
        // The game server process inherits this environment (the same contract
        // BotPresenceCoordinator.IsEnabled/ReadBotCount/ReadManifestPath read),
        // so the bootstrap provisions the manifest roster during world bring-up.
        var manifestPath = WriteManifest();
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", CitizenCount.ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS", CitizenCount.ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MANIFEST", manifestPath);
        Console.WriteLine($"[{StageName}] presence demo enabled via env, manifest: {manifestPath}");

        E2eStack.EnsureUp();

        var failures = new List<string>();
        long dbWritesStart = 0, dbWritesEnd = 0;
        long logLenStart = 0;
        var windowStart = DateTime.UtcNow;

        using var bridge = new BotDriveClient(E2eStack.BridgePort);

        // -- validity gate ----------------------------------------------------
        // INVALID unless the REAL wake path is demonstrably running: scheduler
        // metrics available AND totalStepsRun > 0. Poll generously — citizens
        // are provisioned only after full world wiring (up to ~300s cold boot).
        var first = WaitUntilSchedulerStepping(bridge, TimeSpan.FromSeconds(300), ct);
        if (!first.Valid)
        {
            failures.Add(
                "RUN INVALID: scheduler-driven enablement NOT verified — " +
                $"scheduler.available={first.Metrics?.SchedulerAvailable.ToString() ?? "n/a"}, " +
                "totalStepsRun stayed 0 within the post-boot validity window " +
                "(presence demo did not provision stepping citizens)");
            Console.WriteLine($"[{StageName}] {failures[^1]}");
            var paths = WriteEvidence(minutes, first.Metrics, [], [], null, null,
                false, failures, new DerivedStats(0, 0, 0, 0, 0, -1, -1));
            return new SchedulerSoakStage1Result(StageName, false, false, TimeSpan.Zero,
                [], failures, paths.JsonPath, paths.MdPath,
                $"{StageName} INVALID — scheduler never stepped; soak results meaningless");
        }

        Console.WriteLine($"[{StageName}] validity GREEN: scheduler running, {first.Metrics?.TotalStepsRun} steps run pre-window");

        // -- sampled window ----------------------------------------------------
        dbWritesStart = ReadDbWriteCounters();
        logLenStart = File.Exists(GateSoakRunner.GameLogPath) ? new FileInfo(GateSoakRunner.GameLogPath).Length : 0;
        windowStart = DateTime.UtcNow;
        var deadline = windowStart.AddMinutes(minutes);

        var samples = new List<SoakSample> { first.Sample };
        var missedSamples = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(SampleEvery.TotalSeconds, remaining.TotalSeconds)), ct);

            try
            {
                var s = await Task.Run(() => SampleOnce(bridge, DateTime.UtcNow - windowStart), ct);
                samples.Add(s);
                Console.WriteLine(
                    $"[{StageName}] t+{s.TSeconds:F0}s: stepsRun={s.TotalStepsRun} (+{s.TotalStepsRun - samples[0].TotalStepsRun}) " +
                    $"dueQ={s.DueQueueDepth} eventQ={s.EventQueueDepth} inflight={s.InFlight} " +
                    $"tickP95={s.TickInvokeP95Ms:F1}ms region={s.RegionElapsedMs:F1}ms rss={s.RssMb:F0}MB");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A dead bridge must not abort the window before evidence lands.
                missedSamples++;
                failures.Add($"sample @t+{(DateTime.UtcNow - windowStart).TotalSeconds:F0}s failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var windowSpan = DateTime.UtcNow - windowStart;
        dbWritesEnd = ReadDbWriteCounters();

        var last = samples[^1];
        var firstSample = samples[0];

        // Post-window probes for the final snapshot (best-effort).
        FinalMetrics final = null;
        try
        {
            final = await Task.Run(() => ProbeFinal(bridge), ct);
        }
        catch (Exception ex)
        {
            failures.Add($"final metrics probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Stall detection: the scheduler MUST keep stepping through the window.
        var stepsGrew = final is not null
            ? final.TotalStepsRun > firstSample.TotalStepsRun
            : last.TotalStepsRun > firstSample.TotalStepsRun;
        if (!stepsGrew)
            failures.Add(
                $"scheduler STALLED: totalStepsRun did not grow across the window " +
                $"({firstSample.TotalStepsRun} → {(final?.TotalStepsRun ?? last.TotalStepsRun)})");

        // -- derived measurements ----------------------------------------------
        var dbWrites = Math.Max(0, dbWritesEnd - dbWritesStart);
        var dbPerCharPerMin = dbWrites / Math.Max(windowSpan.TotalMinutes, 0.01) / CitizenCount;
        var logTail = ScanGameLog(logLenStart);
        var physicsPerMin = logTail.PhysicsWarnings / Math.Max(windowSpan.TotalMinutes, 0.01);
        var overrunsPerMin = logTail.TickOverrunWarnings / Math.Max(windowSpan.TotalMinutes, 0.01);
        var stepsDelta = (final?.TotalStepsRun ?? last.TotalStepsRun) - firstSample.TotalStepsRun;
        var rssSamples = samples.Select(s => s.RssMb).Where(v => v >= 0).ToList();
        var rssMin = rssSamples.Count > 0 ? rssSamples.Min() : -1;
        var rssMax = rssSamples.Count > 0 ? rssSamples.Max() : -1;

        if (missedSamples > 0)
            failures.Add($"{missedSamples}/{samples.Count + missedSamples} samples lost (bridge unreachable during window)");

        // -- budgets (repo numerics; see type doc) ------------------------------
        var b = GateStages.SoakBudgets; // region 200ms + tick-overrun 0.1/min idle-stage overrides; rest = GateBudgets defaults
        var verdicts = new List<BudgetVerdict>
        {
            new("Scheduler-driven validity", 1, 1, true,
                $"scheduler.available=true, stepsRun {firstSample.TotalStepsRun}→{(final?.TotalStepsRun ?? last.TotalStepsRun)}"),
            Budget(final?.AvgWakeLatencyMs ?? last.AvgWakeLatencyMs, b.SchedulerAvgWakeLatencyMs,
                "Scheduler avg wake latency", "ms", v => v <= b.SchedulerAvgWakeLatencyMs),
            Budget(final?.MaxWakeLatencyMs ?? last.MaxWakeLatencyMs, b.SchedulerMaxWakeLatencyMs,
                "Scheduler max wake latency", "ms", v => v <= b.SchedulerMaxWakeLatencyMs),
            Budget(final?.TotalStepsFailed ?? last.TotalStepsFailed, b.MaxSchedulerStepFailures,
                "Scheduler step failures", "steps threw", v => v <= b.MaxSchedulerStepFailures),
            Budget(final?.TotalStepsTimedOut ?? last.TotalStepsTimedOut, b.MaxSchedulerStepFailures,
                "Scheduler step timeouts", "steps timed out (zero-tolerance mirrors step failures)", v => v <= b.MaxSchedulerStepFailures),
        };

        if (last.TickInvokeP95Ms >= 0)
        {
            verdicts.Add(Budget(last.TickInvokeP95Ms, b.TickP95Ms, "TickManager invoke p95", "ms",
                v => v >= 0 && v <= b.TickP95Ms));
            verdicts.Add(Budget(last.TickInvokeMaxMs, b.TickMaxMs, "TickManager invoke max", "ms",
                v => v >= 0 && v <= b.TickMaxMs));
            verdicts.Add(Budget(samples.Where(s => s.RegionElapsedMs >= 0).DefaultIfEmpty(last).Max(s => s.RegionElapsedMs),
                b.RegionTickMaxElapsedMs, "ActiveRegionTick worst pass", "ms (idle-stage ceiling)",
                v => v <= b.RegionTickMaxElapsedMs));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("TickManager invoke p95", 0, b.TickP95Ms, "tick metrics absent on server"));
        }

        verdicts.Add(Budget(dbPerCharPerMin, b.MaxDbWritesPerBotPerMin,
            "DB writes", "writes/min/embodied-char", v => v <= b.MaxDbWritesPerBotPerMin));
        verdicts.Add(Budget(physicsPerMin, b.MaxPhysicsWarningsPerMin,
            "Physics warnings", "warnings/min", v => v <= b.MaxPhysicsWarningsPerMin));
        verdicts.Add(Budget(logTail.MaxSameWorldPhysicsWarningsPer60s, b.MaxPhysicsWarningsSameWorldPer60s,
            "Physics warnings same-world", "warnings in 60s on one world",
            v => v <= b.MaxPhysicsWarningsSameWorldPer60s));
        verdicts.Add(Budget(overrunsPerMin, b.MaxTickOverrunWarningsPerMin,
            "Tick overrun warnings", "warnings/min (idle-stage budget)", v => v <= b.MaxTickOverrunWarningsPerMin));

        if (final is { SaveMetricsAvailable: true })
        {
            verdicts.Add(Budget(final.SaveP95Ms, b.AutosaveP95Ms, "Autosave duration p95",
                $"ms over {final.SaveSampleCount} saves", v => v <= b.AutosaveP95Ms));
            verdicts.Add(Budget(final.SaveMaxMs, b.AutosaveMaxMs, "Autosave duration max",
                "ms worst pass", v => v <= b.AutosaveMaxMs));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("Autosave duration p95", 0, b.AutosaveP95Ms,
                "no save completed in window"));
        }

        foreach (var v in verdicts.Where(v => !v.NotApplicable && !v.Passed))
            failures.Add($"{v.Name}: {v.Detail} (measured {v.Measured} / limit {v.Limit})");

        var passed = failures.Count == 0;
        var detail = passed
            ? $"{StageName} GREEN — {CitizenCount} scheduler-stepping citizens, {windowSpan.TotalMinutes:F1}min window, " +
              $"steps {firstSample.TotalStepsRun}→{(final?.TotalStepsRun ?? last.TotalStepsRun)} (+{stepsDelta}), " +
              $"{verdicts.Count(v => !v.NotApplicable)} budgets enforced"
            : $"{StageName} RED — {failures.Count} failure(s): " + string.Join("; ", failures.Take(5));

        var derived = new DerivedStats(
            DbWritesTotal: dbWrites,
            DbPerEmbodiedCharPerMin: dbPerCharPerMin,
            StepsDelta: stepsDelta,
            PhysicsWarnings: logTail.PhysicsWarnings,
            TickOverrunWarnings: logTail.TickOverrunWarnings,
            RssMinMb: rssMin,
            RssMaxMb: rssMax);

        var evidencePaths = WriteEvidence(minutes, first.Metrics, samples, verdicts,
            final, windowSpan, true, failures, derived);

        Console.WriteLine($"[{StageName}] {detail}");
        return new SchedulerSoakStage1Result(StageName, passed, true, windowSpan, verdicts,
            failures, evidencePaths.JsonPath, evidencePaths.MdPath, detail);
    }

    // ------------------------------------------------------------------ probes

    private sealed record ValidityOutcome(bool Valid, SoakSample Sample, MetricsSnapshot Metrics);

    /// <summary>
    /// Polls until the scheduler is demonstrably stepping (available=true AND
    /// totalStepsRun>0) or the window expires — the run-validity gate.
    /// </summary>
    private static ValidityOutcome WaitUntilSchedulerStepping(BotDriveClient bridge, TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        MetricsSnapshot lastMetrics = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                lastMetrics = SnapshotMetrics(bridge.Call("{\"cmd\":\"metrics\"}", 15000));
                if (lastMetrics.SchedulerAvailable && lastMetrics.IsRunning &&
                    lastMetrics.TotalStepsRun > 0 && lastMetrics.PopulationEmbodied >= CitizenCount)
                {
                    return new ValidityOutcome(true, SampleFromMetrics(lastMetrics, TimeSpan.Zero, ReadRss()), lastMetrics);
                }

                Console.WriteLine(
                    $"[{StageName}] validity poll: schedulerAvail={lastMetrics.SchedulerAvailable} " +
                    $"isRunning={lastMetrics.IsRunning} stepsRun={lastMetrics.TotalStepsRun} " +
                    $"embodied={lastMetrics.PopulationEmbodied}/{CitizenCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{StageName}] validity poll failed: {ex.GetType().Name}: {ex.Message}");
            }

            Thread.Sleep(3000);
        }

        return new ValidityOutcome(false, null, lastMetrics);
    }

    private static SoakSample SampleOnce(BotDriveClient bridge, TimeSpan elapsed)
        => SampleFromMetrics(SnapshotMetrics(bridge.Call("{\"cmd\":\"metrics\"}", 15000)), elapsed, ReadRss());

    private static SoakSample SampleFromMetrics(MetricsSnapshot m, TimeSpan elapsed, double rssMb)
        => new(
            elapsed.TotalSeconds,
            m.TotalStepsRun, m.TotalStepsSkipped, m.TotalStepsFailed, m.TotalStepsTimedOut,
            m.DueQueueDepth, m.EventQueueDepth, m.InFlight, m.ActiveWorkers, m.WorkerUtilization,
            m.AvgWakeLatencyMs, m.MaxWakeLatencyMs,
            m.TickInvokeP95Ms, m.TickInvokeMaxMs, m.RegionElapsedMs,
            m.SaveP95Ms, m.SaveMaxMs,
            rssMb);

    private static FinalMetrics ProbeFinal(BotDriveClient bridge)
    {
        var m = SnapshotMetrics(bridge.Call("{\"cmd\":\"metrics\"}", 15000));
        return new FinalMetrics(m.TotalStepsRun, m.TotalStepsSkipped, m.TotalStepsFailed,
            m.TotalStepsTimedOut, m.AvgWakeLatencyMs, m.MaxWakeLatencyMs, m.WorkerUtilization,
            m.ElapsedMs, m.TotalResurrections,
            m.TickInvokeP95Ms, m.TickInvokeMaxMs, m.RegionElapsedMs, m.TickInvokeP95Ms >= 0,
            m.SaveMetricsAvailable, m.SaveSampleCount, m.SaveP95Ms, m.SaveMaxMs,
            m.PopulationEmbodied, m.PopulationFull, m.PopulationReduced, m.PopulationDormant);
    }

    /// <summary>Parses one bridge `metrics` reply into the flat snapshot shape.</summary>
    private static MetricsSnapshot SnapshotMetrics(JsonElement json)
    {
        var m = new MetricsSnapshot();
        if (json.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
            sc.TryGetProperty("available", out var sa) && sa.GetBoolean())
        {
            m.SchedulerAvailable = true;
            m.IsRunning = sc.GetProperty("isRunning").GetBoolean();
            m.WorkerCount = sc.GetProperty("workerCount").GetInt32();
            m.ActiveWorkers = sc.GetProperty("activeWorkers").GetInt32();
            m.DueQueueDepth = sc.GetProperty("dueQueueDepth").GetInt32();
            m.EventQueueDepth = sc.GetProperty("eventQueueDepth").GetInt32();
            m.InFlight = sc.GetProperty("inFlight").GetInt32();
            m.TotalStepsRun = sc.GetProperty("totalStepsRun").GetInt64();
            m.TotalStepsSkipped = sc.GetProperty("totalStepsSkipped").GetInt64();
            m.TotalStepsFailed = sc.GetProperty("totalStepsFailed").GetInt64();
            m.TotalStepsTimedOut = sc.GetProperty("totalStepsTimedOut").GetInt64();
            m.AvgWakeLatencyMs = sc.GetProperty("avgWakeLatencyMs").GetDouble();
            m.MaxWakeLatencyMs = sc.GetProperty("maxWakeLatencyMs").GetDouble();
            m.WorkerUtilization = sc.GetProperty("workerUtilization").GetDouble();
        }

        if (json.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
            tk.TryGetProperty("available", out var ta) && ta.GetBoolean())
        {
            m.TickInvokeP95Ms = tk.GetProperty("invokeP95Ms").GetDouble();
            m.TickInvokeMaxMs = tk.GetProperty("invokeMaxMs").GetDouble();
        }

        if (json.TryGetProperty("regionTick", out var rt) && rt.ValueKind == JsonValueKind.Object &&
            rt.TryGetProperty("available", out var ra) && ra.GetBoolean())
            m.RegionElapsedMs = rt.GetProperty("elapsedMs").GetDouble();

        if (json.TryGetProperty("save", out var sv) && sv.ValueKind == JsonValueKind.Object &&
            sv.TryGetProperty("available", out var sva) && sva.GetBoolean())
        {
            m.SaveMetricsAvailable = true;
            m.SaveSampleCount = sv.GetProperty("sampleCount").GetInt64();
            m.SaveP95Ms = sv.GetProperty("p95Ms").GetDouble();
            m.SaveMaxMs = sv.GetProperty("maxMs").GetDouble();
        }

        if (json.TryGetProperty("population", out var po) && po.ValueKind == JsonValueKind.Object &&
            po.TryGetProperty("available", out var pa) && pa.GetBoolean())
        {
            m.PopulationEmbodied = po.GetProperty("embodied").GetInt32();
            m.PopulationFull = po.GetProperty("full").GetInt32();
            m.PopulationReduced = po.GetProperty("reduced").GetInt32();
            m.PopulationDormant = po.GetProperty("dormant").GetInt32();
        }

        if (json.TryGetProperty("uptimeMs", out var up))
            m.ElapsedMs = up.GetInt64();

        return m;
    }

    // ------------------------------------------------------------------ manifest

    /// <summary>
    /// Writes the 10-entry citizen roster consumed through the G2-A6 manifest
    /// seam (AAEMU_PRESENCE_MANIFEST). Mixed Nuian/Elf rosters, level 5 (the
    /// demo provisioning level).
    ///
    /// Every entry pins an EXPLICIT home (the documented per-bot patrol-home
    /// override) at the Nuian template spawn — without it the coordinator's
    /// StartFromManifest path skips relocation (explicitHome == default) and
    /// leaves each bot at its OWN race template spawn while handing it a
    /// patrol route centered on the Nuian-male default home. Stage-1 run 1
    /// demonstrated the consequence: all 5 Elf citizens spawned at the Elf
    /// template start (10388,15982 — 4.3km away), walked the whole window
    /// toward the unreachable patrol circle and drowned together at the
    /// Solzreed coast (14700,15500; blood-decal doodad 878 at each spot),
    /// tripping the tick-overrun budget with the resurrection burst. Engine
    /// card: route center must follow the bot's actual spawn when no home is
    /// configured (BotPresenceCoordinator.StartFromManifest).
    /// </summary>
    private static string WriteManifest()
    {
        // The route center DefaultHomeResolver produces (Nuian male template
        // spawn — Data/CharTemplates.json id 1).
        const float homeX = 15578.042f, homeY = 15382.122f, homeZ = 126.484f;

        string[] races = ["Nuian", "Elf"];
        string[] genders = ["Male", "Female"];
        var entries = new List<object>(CitizenCount);
        for (var i = 0; i < CitizenCount; i++)
        {
            entries.Add(new
            {
                name = $"SoakCitizen{i + 1:D2}",
                race = races[i % races.Length],
                gender = genders[i % genders.Length],
                level = 5,
                home = new { x = homeX, y = homeY, z = homeZ },
                personality = $"soak-stage1-{i + 1:D2}"
            });
        }

        var dir = Path.Combine(E2eStack.E2eRoot, "runtime");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "scheduler-soak-manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // ------------------------------------------------------------------ host metrics

    private static int? _gamePid;

    /// <summary>Game-server PID located once via /proc (cwd == RuntimeGameDir), like E2eStack.KillStaleServers.</summary>
    private static int? FindGamePid()
    {
        if (_gamePid.HasValue)
        {
            try
            {
                using var _ = Process.GetProcessById(_gamePid.Value);
                return _gamePid;
            }
            catch
            {
                _gamePid = null;
            }
        }

        var root = Path.GetFullPath(E2eStack.RuntimeGameDir);
        foreach (var proc in Process.GetProcessesByName("dotnet"))
        {
            try
            {
                var cmdline = File.ReadAllText($"/proc/{proc.Id}/cmdline").Replace('\0', ' ');
                if (!cmdline.Contains("AAEmu.Game.dll"))
                    continue;
                var cwd = new FileInfo($"/proc/{proc.Id}/cwd").LinkTarget;
                if (cwd == null || Path.GetFullPath(cwd) != root)
                    continue;
                _gamePid = proc.Id;
                return _gamePid;
            }
            catch
            {
                // process vanished between enumeration and read
            }
        }

        return null;
    }

    /// <summary>Game-process RSS in MB (VmRSS from /proc — resident, matches the M6 soak's RSS band reporting).</summary>
    private static double ReadRss()
    {
        try
        {
            var pid = FindGamePid();
            if (pid == null)
                return -1;
            foreach (var line in File.ReadAllLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    continue;
                var kb = long.Parse(line["VmRSS:".Length..].Trim().Split(' ')[0]);
                return kb / 1024.0;
            }
        }
        catch
        {
        }

        return -1;
    }

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
            Console.WriteLine($"[{StageName}] db counter read failed: {ex.GetType().Name}: {ex.Message}");
        }

        return total;
    }

    // ------------------------------------------------------------------ game-log scan

    private sealed record LogTail(long PhysicsWarnings, long TickOverrunWarnings, long MaxSameWorldPhysicsWarningsPer60s);

    private static readonly System.Text.RegularExpressions.Regex PhysicsWarningRegex = new(
        @"^(\d{2}):(\d{2}):(\d{2}) .*?in (.+?) at ",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Same game-log contract as GateSoakRunner: "Physics thread is running
    /// slow" warnings (with the per-world sliding-60s clause) + "Tick took" /
    /// ActiveRegionTick over-budget lines — scanned across the WINDOW DELTA
    /// only (log offset taken at window start).
    /// </summary>
    private static LogTail ScanGameLog(long startOffset)
    {
        long physics = 0, overruns = 0;
        var worldTimes = new Dictionary<string, List<long>>();
        try
        {
            if (!File.Exists(GateSoakRunner.GameLogPath))
                return new LogTail(0, 0, 0);

            using var fs = File.OpenRead(GateSoakRunner.GameLogPath);
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
            Console.WriteLine($"[{StageName}] game log scan failed: {ex.GetType().Name}: {ex.Message}");
        }

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

    private static BudgetVerdict Budget(double measured, double limit, string name, string unit, Func<double, bool> pass)
        => pass(measured)
            ? BudgetVerdict.Ok(name, measured, limit, unit)
            : BudgetVerdict.Over(name, measured, limit, unit);

    private static (string JsonPath, string MdPath) WriteEvidence(
        int configuredMinutes,
        MetricsSnapshot validityMetrics,
        IReadOnlyList<SoakSample> samples,
        IReadOnlyList<BudgetVerdict> verdicts,
        FinalMetrics final,
        TimeSpan? windowSpan,
        bool valid,
        IReadOnlyList<string> allFailures,
        DerivedStats derived)
    {
        var dir = Path.Combine(E2eStack.E2eRoot, "logs");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(dir, $"{StageName}-{stamp}.json");
        var mdPath = Path.Combine(dir, $"{StageName}-{stamp}.md");

        var report = new Dictionary<string, object?>
        {
            ["kind"] = StageName,
            ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
            ["configuredWindowMinutes"] = configuredMinutes,
            ["measuredWindowMinutes"] = windowSpan?.TotalMinutes ?? 0,
            ["citizens"] = CitizenCount,
            ["enablement"] = new Dictionary<string, object?>
            {
                ["env"] = "AAEMU_PRESENCE_DEMO=1 + AAEMU_PRESENCE_MANIFEST",
                ["manifest"] = Path.Combine(E2eStack.E2eRoot, "runtime", "scheduler-soak-manifest.json"),
                ["schedulerAvailable"] = validityMetrics?.SchedulerAvailable ?? false,
                ["schedulerIsRunning"] = validityMetrics?.IsRunning ?? false,
                ["preWindowStepsRun"] = validityMetrics?.TotalStepsRun ?? 0,
                ["populationEmbodied"] = validityMetrics?.PopulationEmbodied ?? 0,
                ["validityContract"] = "scheduler.available=true AND totalStepsRun>0 (else run INVALID)"
            },
            ["valid"] = valid,
            ["budgets"] = verdicts.Select(v => new Dictionary<string, object?>
            {
                ["name"] = v.Name,
                ["measured"] = v.Measured,
                ["limit"] = v.Limit,
                ["passed"] = v.Passed,
                ["notApplicable"] = v.NotApplicable,
                ["detail"] = v.Detail
            }),
            ["failures"] = allFailures,
            ["derived"] = derived,
            ["final"] = final,
            ["samples"] = samples
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[{StageName}] json evidence written: {jsonPath}");

        var sb = new StringBuilder();
        sb.AppendLine($"# Scheduler-driven soak — stage 1 ({CitizenCount} manifest citizens)");
        sb.AppendLine();
        sb.AppendLine($"> Generated by SchedulerSoakStage1Runner. Bots work ONLY through the real " +
                      $"IPlayerBotScheduler lease/wake path (presence demo enabled via env + AAEMU_PRESENCE_MANIFEST roster).");
        sb.AppendLine($"> Validity contract: scheduler.available=true AND totalStepsRun>0 — otherwise the run is INVALID.");
        sb.AppendLine($"> Window: {(windowSpan?.TotalMinutes ?? 0):F1} min · citizens: {CitizenCount} · valid: {valid}");
        sb.AppendLine();
        sb.AppendLine("| Metric | Measured | Limit | Verdict |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var v in verdicts)
        {
            var tag = v.NotApplicable ? "n/a" : v.Passed ? "PASS" : "**FAIL**";
            sb.AppendLine($"| {v.Name} | {v.Measured:F2} | {v.Limit:F2} | {tag} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Scheduler throughput");
        sb.AppendLine();
        sb.AppendLine($"- stepsRun: {validityMetrics?.TotalStepsRun ?? 0} (pre-window) → {(final?.TotalStepsRun ?? samples.LastOrDefault()?.TotalStepsRun ?? 0)} (post-window)");
        sb.AppendLine($"- steps skipped/failed/timed out: {(final?.TotalStepsSkipped ?? 0)} / {(final?.TotalStepsFailed ?? 0)} / {(final?.TotalStepsTimedOut ?? 0)}");
        sb.AppendLine($"- wake latency avg/max: {(final?.AvgWakeLatencyMs ?? 0):F1} ms / {(final?.MaxWakeLatencyMs ?? 0):F1} ms · utilization {(final?.WorkerUtilization ?? 0):P1}");
        sb.AppendLine($"- population embodied: {final?.PopulationEmbodied.ToString() ?? "n/a"} (full {final?.PopulationFull}, reduced {final?.PopulationReduced}, dormant {final?.PopulationDormant})");
        sb.AppendLine($"- RSS band (game proc): {derived.RssMinMb:F0}–{derived.RssMaxMb:F0} MB (informational — no numeric precedent)");
        sb.AppendLine($"- DB writes: {derived.DbWritesTotal} total ({derived.DbPerEmbodiedCharPerMin:F1}/min/embodied-char)");
        sb.AppendLine($"- game-log window delta: {derived.PhysicsWarnings} physics-slow warnings, {derived.TickOverrunWarnings} tick-overrun lines");
        sb.AppendLine();

        if (samples.Count > 0)
        {
            sb.AppendLine("## Samples (every 5th shown; full series in the JSON)");
            sb.AppendLine();
            sb.AppendLine("| t(s) | stepsRun | dueQ | evQ | inflight | tickP95(ms) | region(ms) | rss(MB) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            for (var i = 0; i < samples.Count; i += 5)
            {
                var s = samples[i];
                sb.AppendLine($"| {s.TSeconds:F0} | {s.TotalStepsRun} | {s.DueQueueDepth} | {s.EventQueueDepth} | {s.InFlight} | {s.TickInvokeP95Ms:F1} | {s.RegionElapsedMs:F1} | {s.RssMb:F0} |");
            }

            sb.AppendLine();
        }

        if (allFailures.Count > 0)
        {
            sb.AppendLine("## Failures (every entry is a regression card)");
            sb.AppendLine();
            foreach (var f in allFailures)
                sb.AppendLine($"- {f.Replace("\n", " ")}");
            sb.AppendLine();
        }

        sb.AppendLine($"Structured series: `{Path.GetFileName(jsonPath)}`.");

        File.WriteAllText(mdPath, sb.ToString());
        Console.WriteLine($"[{StageName}] md evidence written: {mdPath}");
        return (jsonPath, mdPath);
    }

    // ------------------------------------------------------------------ shapes

    /// <summary>Window-level derived measurements (DB deltas, log scan, RSS band).</summary>
    public sealed record DerivedStats(
        long DbWritesTotal,
        double DbPerEmbodiedCharPerMin,
        long StepsDelta,
        long PhysicsWarnings,
        long TickOverrunWarnings,
        double RssMinMb,
        double RssMaxMb);

    /// <summary>One point-in-time sample of the bridge scheduler surface + host RSS.</summary>
    public sealed record SoakSample(
        double TSeconds,
        long TotalStepsRun,
        long TotalStepsSkipped,
        long TotalStepsFailed,
        long TotalStepsTimedOut,
        int DueQueueDepth,
        int EventQueueDepth,
        int InFlight,
        int ActiveWorkers,
        double WorkerUtilization,
        double AvgWakeLatencyMs,
        double MaxWakeLatencyMs,
        double TickInvokeP95Ms,
        double TickInvokeMaxMs,
        double RegionElapsedMs,
        double SaveP95Ms,
        double SaveMaxMs,
        double RssMb);

    /// <summary>Mutating parse target for one bridge `metrics` reply.</summary>
    private sealed class MetricsSnapshot
    {
        public bool SchedulerAvailable;
        public bool IsRunning;
        public int WorkerCount;
        public int ActiveWorkers;
        public int DueQueueDepth;
        public int EventQueueDepth;
        public int InFlight;
        public long TotalStepsRun;
        public long TotalStepsSkipped;
        public long TotalStepsFailed;
        public long TotalStepsTimedOut;
        public double AvgWakeLatencyMs;
        public double MaxWakeLatencyMs;
        public double WorkerUtilization;
        public double TickInvokeP95Ms = -1;
        public double TickInvokeMaxMs = -1;
        public double RegionElapsedMs = -1;
        public bool SaveMetricsAvailable;
        public long SaveSampleCount;
        public double SaveP95Ms;
        public double SaveMaxMs;
        public int PopulationEmbodied = -1;
        public int PopulationFull = -1;
        public int PopulationReduced = -1;
        public int PopulationDormant = -1;
        public long ElapsedMs;
        public long TotalResurrections;
    }

    /// <summary>Post-window final counters block for the evidence files.</summary>
    public sealed record FinalMetrics(
        long TotalStepsRun,
        long TotalStepsSkipped,
        long TotalStepsFailed,
        long TotalStepsTimedOut,
        double AvgWakeLatencyMs,
        double MaxWakeLatencyMs,
        double WorkerUtilization,
        long ElapsedMs,
        long TotalResurrections,
        double TickInvokeP95Ms,
        double TickInvokeMaxMs,
        double RegionElapsedMs,
        bool TickMetricsAvailable,
        bool SaveMetricsAvailable,
        long SaveSampleCount,
        double SaveP95Ms,
        double SaveMaxMs,
        int PopulationEmbodied,
        int PopulationFull,
        int PopulationReduced,
        int PopulationDormant);
}
