using System.Diagnostics;
using System.Text.Json;

using AAEmu.IntegrationTests.E2e;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.G2;

/// <summary>
/// G2-A5 FINAL Tier-3 shape ACCEPTANCE probe (pure measurement — budget
/// verdicts are recorded, not asserted): scales the A5AcceptanceProbeTests
/// shape to the roadmap FINAL target 1,000 registered dormant / ≤50 embodied,
/// and measures it against a like-for-like ACTIVE-load baseline.
///
/// Arms (single fact — one report, both arms):
///   B  BASELINE : dormancy OFF, 50 ACTIVE presence-demo citizens (real
///                 provisioning + roam, ~300 steps/min/bot), one real human
///                 client online → sample. This is the "50 embodied" cost
///                 reference the RSS gate is measured against.
///   T  TIER-3   : AAEMU_BOT_TRUE_DORMANCY=1 + AAEMU_BOT_PROXIMITY_FIDELITY=1,
///                 seed 1,000 dormant specs through the REAL provisioning path
///                 (bridge 'seedDormant': Provision → RecordHome(+schedule) →
///                 Deactivate; durable rows re-discovered by
///                 MySqlDormantBotSource after restart). Human reconnects →
///                 PopulationDirector proximity sweep materializes the 50
///                 near-home specs → sample. Human leaves → dematerialize.
///
/// Pacing note (honest choice): TrueDormancyMaterializePerSweepMax stays at
/// its default 3/sweep at the default 2 s sweep cadence → 1.5 materializations/s
/// → all 50 near specs embodied ≈ 34 s after human connect. The p95 gate is
/// per-materialization latency (row-load → home restore → activate), not the
/// total window, so no env raise of AAEMU_BOT_DORMANCY_MATERIALIZE_PER_SWEEP is
/// needed; raising the budget would only front-load the storm we want to see
/// paced.
///
/// Seed timebox: 1,000 provision→deactivate round-trips extrapolate to ~90 min
/// sequentially (~9 min at 100-scale); seeding therefore runs on 4 concurrent
/// bridge connections with per-batch elapsed logging and a HARD 30-minute box —
/// if the projection bursts, seeding stops and partials are recorded honestly.
///
/// Targets (ROADMAP G2-A5 FINAL): RSS within 15 % of the 50-active baseline;
/// wake-to-visible (materialize) p95 < 3 s; ≤50 embodied steady state.
/// Report: g2-a5-tier3-report.json under $E2E_ROOT/logs.
/// </summary>
[Collection("e2e")]
public class A5Tier3AcceptanceProbeTests
{
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    private const string HumanAccount = "a5humanacct";
    private const string HumanChar = "A5Human";
    private const string HumanPassword = "e2e-secret";

    private static readonly TimeSpan SeedBox = TimeSpan.FromMinutes(30);

    private sealed record Sample(DateTime At, double RssMb, double TickP95, double RegionMs, long Steps);

    private sealed record ArmResult(
        int EmbodiedEnd, long DormantSpecsEnd,
        long MaterializeCount, double MaterializeP50Ms, double MaterializeP95Ms, double MaterializeP99Ms, double MaterializeMaxMs,
        double MaterializeWindowSec,
        double RssMedianMb, double RssMinMb, double RssMaxMb,
        double TickP95MedianMs, double TickP95MaxMs, double RegionWorstMs, long StepsPerMin);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Probe_A5Tier3Shape_Acceptance()
    {
        var minutesPerArm = int.TryParse(Environment.GetEnvironmentVariable("SCALING_PROBE_MINUTES"), out var m) && m > 0 ? m : 2;
        var dormantTarget = int.TryParse(Environment.GetEnvironmentVariable("A5_DORMANT_COUNT"), out var dc) && dc > 0 ? dc : 1000;
        var embodiedTarget = int.TryParse(Environment.GetEnvironmentVariable("A5_EMBODIED_COUNT"), out var ec) && ec > 0 ? ec : 50;
        var runAt = DateTime.UtcNow;

        E2eStack.EnsureUp();
        var ownedNames = BuildOwnershipNames(dormantTarget, embodiedTarget);
        var ownershipBefore = E2eStack.SnapshotOwnedRows(ownedNames);
        try
        {

        // ------------------------------------------------ ARM B: baseline (50 active)
        Console.WriteLine($"[a5t3] arm B: baseline — {embodiedTarget} ACTIVE presence citizens, dormancy OFF");
        ClearFeatureEnv();
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", embodiedTarget.ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS", Math.Max(embodiedTarget, 10).ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MANIFEST", WriteManifest(embodiedTarget));
        E2eStack.RestartGameServer();
        WaitBoot();
        WaitEmbodied(embodiedTarget, TimeSpan.FromMinutes(30));
        Console.WriteLine("[a5t3] baseline citizens stepping; settling 120s");

        ArmResult baseline;
        float hx, hy, hz;
        using (var human = await ConnectHumanAsync())
        {
            await Task.Delay(30_000);
            var samples = SampleWindow(minutesPerArm);
            baseline = Summarize(samples, embodied: embodiedTarget, dormantSpecs: -1,
                lat: (0, -1, -1, -1, -1), windowSec: -1);

            if (!TryReadHumanHome(out hx, out hy, out hz))
                throw new InvalidOperationException("could not read the human character's home coordinates from MySQL");
        }
        Console.WriteLine($"[a5t3] baseline: rssMed={baseline.RssMedianMb}MB tickP95med={baseline.TickP95MedianMs}ms " +
                          $"steps/min={baseline.StepsPerMin}; human at ({hx:F0},{hy:F0},{hz:F0})");

        // ------------------------------------------- ARM T: tier-3 dormancy shape
        Console.WriteLine("[a5t3] arm T: clean restart before dormant seeding");
        ClearFeatureEnv();

        // Existing rows remain untouched. The ownership snapshot captured
        // before the run limits both this arm's baseline cleanup and final
        // cleanup to rows created by this run.
        E2eStack.RestartGameServer();
        WaitBoot();
        CleanupBaselinePresenceBots(ownershipBefore, embodiedTarget);

        TimeSpan seedElapsed;
        using (var seedBoxCts = new CancellationTokenSource(SeedBox))
        {
            var seedStart = Stopwatch.StartNew();
            try
            {
                seedElapsed = await SeedDormant(hx, hy, hz, embodiedTarget, dormantTarget - embodiedTarget,
                    seedBoxCts.Token);
            }
            catch (OperationCanceledException) when (seedBoxCts.IsCancellationRequested)
            {
                seedStart.Stop();
                var partial = 0;
                try { partial = CountManagedCharacters(); } catch { /* cleanup/report follows */ }
                Console.WriteLine($"[a5t3] seed canceled after {seedStart.Elapsed.TotalMinutes:F1} min; " +
                                  $"partial discoverable managed characters: {partial}/{dormantTarget}");
                throw;
            }
        }
        var seededCount = CountManagedCharacters();
        Console.WriteLine($"[a5t3] seed done in {seedElapsed.TotalMinutes:F1} min — discoverable managed characters: {seededCount}/{dormantTarget}");

        Environment.SetEnvironmentVariable("AAEMU_BOT_TRUE_DORMANCY", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FIDELITY", "1");
        E2eStack.RestartGameServer();
        WaitBoot();
        WaitDormantDiscovered(seededCount);

        ArmResult tier3;
        JsonElement finalMetrics;
        using (var human = await ConnectHumanAsync())
        {
            var connectedAt = DateTime.UtcNow;
            // Budget-paced expectation: 3/sweep × 2 s cadence → ~34 s for 50;
            // generous deadline, hard stop, no loop-polling beyond it.
            var materializeDeadline = connectedAt + TimeSpan.FromMinutes(10);
            var allMaterializedAt = DateTime.MinValue;
            while (DateTime.UtcNow < materializeDeadline)
            {
                if (Population(Metrics()).totalMaterializations >= embodiedTarget)
                {
                    allMaterializedAt = DateTime.UtcNow;
                    break;
                }
                await Task.Delay(1000);
            }

            var windowSec = allMaterializedAt > connectedAt ? (allMaterializedAt - connectedAt).TotalSeconds : -1;
            Console.WriteLine($"[a5t3] {Population(Metrics()).totalMaterializations} specs materialized {windowSec:F1}s after connect");

            await Task.Delay(60_000); // settle
            var samples = SampleWindow(minutesPerArm);
            finalMetrics = Metrics();
            tier3 = Summarize(samples, Population(finalMetrics).embodied,
                Population(finalMetrics).dormantSpecs, Latency(finalMetrics), windowSec);
        }
        Console.WriteLine($"[a5t3] tier3: rssMed={tier3.RssMedianMb}MB embodiedEnd={tier3.EmbodiedEnd} " +
                          $"matCount={tier3.MaterializeCount} p50/p95/p99/max=" +
                          $"{tier3.MaterializeP50Ms:F0}/{tier3.MaterializeP95Ms:F0}/{tier3.MaterializeP99Ms:F0}/{tier3.MaterializeMaxMs:F0}ms " +
                          $"steps/min={tier3.StepsPerMin}");

        // Dematerialize-on-leave cleanliness: by-design steady state is 0 embodied.
        var postLeave = await WaitDematerialized(embodiedTarget, TimeSpan.FromSeconds(90));

        // ------------------------------------------------------------- verdicts
        var rssDeltaPct = baseline.RssMedianMb > 0
            ? Math.Round((tier3.RssMedianMb - baseline.RssMedianMb) / baseline.RssMedianMb * 100.0, 2) : -1;
        var rssPass = rssDeltaPct is >= -15 and <= 15;
        var matPass = tier3.MaterializeP95Ms >= 0 && tier3.MaterializeP95Ms < 3000;
        var embodiedPass = tier3.MaterializeCount > 0 && tier3.MaterializeCount <= embodiedTarget && tier3.EmbodiedEnd <= embodiedTarget;

        WriteReport(runAt, minutesPerArm, dormantTarget, embodiedTarget, seededCount, seedElapsed,
            baseline, tier3, postLeave, rssDeltaPct, rssPass, matPass, embodiedPass);

        // Validity only (budget verdicts live in the report): the real sweep path
        // must have materialized something AND discovery must be near-complete for
        // the numbers to mean anything.
        Assert.True(tier3.MaterializeCount > 0, "no proximity materialization ever fired — trigger path broken");
        Assert.True(seededCount >= dormantTarget * 0.95,
            $"seed produced only {seededCount}/{dormantTarget} discoverable managed characters (partial-seed run)");
    }
        finally
        {
            try
            {
                var ownershipAfter = E2eStack.SnapshotOwnedRows(ownedNames);
                var ownedRows = E2eStack.FindNewOwnedRows(ownershipBefore, ownershipAfter);
                E2eStack.CleanupOwnedRows(ownedRows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[a5t3] ownership cleanup skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
    private sealed record DormantTimerSample(
        DateTime At, long UptimeMs, double RssMb, int Embodied, long DormantSpecs,
        long Materializations, long Dematerializations,
        double TickP95Ms, double TickMaxMs, double RegionMs,
        long DueQueueDepth, long EventQueueDepth, long InFlight,
        long SchedulerFailures, long SaveSampleCount, double SaveP95Ms,
        double SaveMaxMs, long SaveSkips, long DbWrites);

    private sealed record StartupRssTracker
    {
        public double PeakMb { get; private set; } = -1;

        public void Observe(double rssMb)
        {
            if (rssMb > PeakMb)
                PeakMb = rssMb;
        }
    }

    private sealed record RssQuiescenceResult(
        DateTime WarmupReadyAtUtc, double BaselineRssMb, double StartupPeakRssMb,
        TimeSpan WarmupDuration, TimeSpan QuiescenceDuration,
        long DormantSpecs, int GamePid, string ReadyMarker);

    private sealed record DormantTimerResult(
        TimeSpan Window, bool CompletedFullWindow, long InitialDbWrites, long FinalDbWrites,
        DateTime WarmupReadyAtUtc, double WarmupDurationSeconds, long WarmupDormantSpecs,
        int WarmupGamePid, string WarmupReadyMarker, double BaselineRssMb,
        double StartupPeakRssMb, double SteadyStatePeakRssMb, double RssGrowthMb,
        TimeSpan QuiescenceDuration, DateTime? FailureAtUtc, TimeSpan? FailureElapsed,
        IReadOnlyList<DormantTimerSample> Samples, IReadOnlyList<string> Failures);

    private const double DormantTickP95BudgetMs = 100;
    private const double DormantTickMaxBudgetMs = 250;
    private const double DormantRegionBudgetMs = 200;
    private const double DormantSaveP95BudgetMs = 4000;
    private const double DormantSaveMaxBudgetMs = 10000;
    private const double DormantRssGrowthBudgetMb = 512;
    private const double DormantDbWritesBudgetPerMin = 500;
    private const int RssQuiescenceConsecutiveSamples = 3;
    private const double RssQuiescenceMaxRangeMb = 64;
    private static readonly TimeSpan RssQuiescenceSampleInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RssQuiescenceTimeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void RssQuiescenceRule_RequiresThreeConsecutiveStableReadings()
    {
        Assert.False(IsRssQuiescent([1000, 1020]));
        Assert.True(IsRssQuiescent([1000, 1060, 1040]));
        Assert.False(IsRssQuiescent([1000, 1100, 1010]));
    }

    [Fact]
    public void RssBudgetRule_UsesStrictlyMoreThan512Mb()
    {
        Assert.False(ExceedsRssBudget(1400, 1912));
        Assert.True(ExceedsRssBudget(1400, 1912.1));
        Assert.False(ExceedsRssBudget(-1, 5000));
    }
    [Fact]
    public void ValidatorMessage_DistinguishesStartupPeakFromSteadyStateGrowth()
    {
        var sample = new DormantTimerSample(
            DateTime.UtcNow, 1, 1912.1, 0, 100, 0, 0,
            1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 0, 1);
        var failures = new List<string>();

        Assert.True(ValidateDormantTimerSample(sample, 100, 1400, 5800, null, failures));
        var failure = Assert.Single(failures);
        Assert.Contains("steady-state RSS growth", failure);
        Assert.Contains("startup peak=5800.0MB", failure);
    }


    /// <summary>
    /// Optional natural-home dormant timer soak. This is deliberately skipped
    /// unless the operator opts in and supplies the duration; ordinary gate
    /// runs never wait for this six-hour stage. RSS warmup readiness is bounded
    /// by WaitBoot, WaitDormantDiscovered, and three consecutive 15-second
    /// readings whose range is at most 64 MB; no fixed elapsed sleep is used.
    /// </summary>
    [Fact]
    public async Task Probe_A5Tier3DormantTimers_SixHour()
    {
        if (Environment.GetEnvironmentVariable("A5_TIER3_SIX_HOUR") != "1")
        {
            Assert.Skip("A5_TIER3_SIX_HOUR=1 is required for the six-hour dormant timer stage.");
            return;
        }

        var windowMinutes = ReadRequiredPositiveInt("A5_TIER3_SIX_HOUR_MINUTES");
        if (windowMinutes < 360)
            throw new ArgumentOutOfRangeException(nameof(windowMinutes),
                "A5_TIER3_SIX_HOUR_MINUTES must be at least 360 for the six-hour stage.");
        var sampleSeconds = ReadRequiredPositiveInt("A5_TIER3_SIX_HOUR_SAMPLE_SECONDS");
        if (sampleSeconds > 300)
            throw new ArgumentOutOfRangeException(nameof(sampleSeconds),
                "A5_TIER3_SIX_HOUR_SAMPLE_SECONDS must be at most 300.");
        var dormantTarget = ReadRequiredPositiveInt("A5_DORMANT_COUNT");
        var ownedNames = BuildOwnershipNames(dormantTarget, 0);
        E2eStack.EnsureUp();
        var ownershipBefore = E2eStack.SnapshotOwnedRows(ownedNames);
        const float homeX = 15578.042f, homeY = 15382.122f, homeZ = 126.484f;

        try
        {
            ClearFeatureEnv();
            using (var seedDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                       TestContext.Current.CancellationToken))
            {
                seedDeadline.CancelAfter(SeedBox);
                await SeedDormant(homeX, homeY, homeZ, 0, dormantTarget, seedDeadline.Token);
            }

            var seededCount = CountManagedCharacters();
            Assert.True(seededCount >= dormantTarget * 0.95,
                $"seed produced only {seededCount}/{dormantTarget} discoverable managed characters");

            Environment.SetEnvironmentVariable("AAEMU_BOT_TRUE_DORMANCY", "1");
            Environment.SetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FIDELITY", "1");
            var startupRss = new StartupRssTracker();
            var warmupStartedAtUtc = DateTime.UtcNow;
            E2eStack.RestartGameServer();
            WaitBoot(startupRss.Observe, TestContext.Current.CancellationToken);
            WaitDormantDiscovered(seededCount, startupRss.Observe, TestContext.Current.CancellationToken);
            var quiescence = await WaitForRssQuiescenceAsync(
                startupRss, seededCount, warmupStartedAtUtc, TestContext.Current.CancellationToken);
            Console.WriteLine($"[a5t3-sixhour] startup quiescent after {quiescence.WarmupDuration.TotalSeconds:F0}s: " +
                              $"baseline={quiescence.BaselineRssMb:F1}MB startupPeak={quiescence.StartupPeakRssMb:F1}MB");
            var result = await RunDormantTimerSoakAsync(
                seededCount, TimeSpan.FromMinutes(windowMinutes),
                TimeSpan.FromSeconds(sampleSeconds), quiescence, TestContext.Current.CancellationToken);
            WriteDormantTimerReport(windowMinutes, sampleSeconds, dormantTarget, seededCount, result);
            Assert.Empty(result.Failures);
        }
        finally
        {
            try
            {
                var ownershipAfter = E2eStack.SnapshotOwnedRows(ownedNames);
                var ownedRows = E2eStack.FindNewOwnedRows(ownershipBefore, ownershipAfter);
                E2eStack.CleanupOwnedRows(ownedRows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[a5t3-sixhour] ownership cleanup skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static int ReadRequiredPositiveInt(string variable)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (!int.TryParse(raw, out var value) || value <= 0)
            throw new InvalidOperationException($"{variable} must be set to a positive integer.");
        return value;
    }

    private static async Task<DormantTimerResult> RunDormantTimerSoakAsync(
        int seededCount, TimeSpan window, TimeSpan sampleInterval,
        RssQuiescenceResult quiescence, CancellationToken cancellationToken)
    {
        var samples = new List<DormantTimerSample>();
        var failures = new List<string>();
        var started = Stopwatch.StartNew();
        var initialDbWrites = ReadDbWriteCounters();
        if (initialDbWrites < 0)
            AddFailure(failures, "initial DB write counters unavailable");
        long? previousUptime = null;
        DateTime? failureAtUtc = null;
        TimeSpan? failureElapsed = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(window + TimeSpan.FromMinutes(5));

        while (started.Elapsed < window)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                var metrics = await MetricsAsync(deadline.Token);
                var sample = ReadDormantTimerSample(metrics, ReadDbWriteCounters());
                samples.Add(sample);
                var rssBudgetBreached = ValidateDormantTimerSample(
                    sample, seededCount, quiescence.BaselineRssMb,
                    quiescence.StartupPeakRssMb, previousUptime, failures);
                previousUptime = sample.UptimeMs;
                if (rssBudgetBreached)
                {
                    failureAtUtc = sample.At;
                    failureElapsed = started.Elapsed;
                    Console.WriteLine($"[a5t3-sixhour] steady-state RSS budget breached at " +
                                      $"{failureElapsed.Value.TotalSeconds:F1}s; aborting soak");
                    break;
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AddFailure(failures, $"metrics/recovery sample failed: {ex.GetType().Name}: {ex.Message}");
            }

            var remaining = window - started.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining < sampleInterval ? remaining : sampleInterval, deadline.Token);
        }

        if (samples.Count == 0)
            AddFailure(failures, "no six-hour metrics samples collected");

        started.Stop();
        var finalDbWrites = ReadDbWriteCounters();
        if (finalDbWrites < 0)
            AddFailure(failures, "final DB write counters unavailable");
        var dbWritesPerMinute = started.Elapsed.TotalMinutes > 0
            ? Math.Max(0, finalDbWrites - initialDbWrites) / started.Elapsed.TotalMinutes
            : double.PositiveInfinity;
        if (dbWritesPerMinute > DormantDbWritesBudgetPerMin)
            AddFailure(failures, $"DB writes exceeded dormant budget: {dbWritesPerMinute:F1}/min > {DormantDbWritesBudgetPerMin:F0}/min");

        var steadyStatePeak = samples.Where(s => s.RssMb > 0)
            .Select(s => s.RssMb).DefaultIfEmpty(-1).Max();
        var rssGrowth = quiescence.BaselineRssMb > 0 && steadyStatePeak > 0
            ? steadyStatePeak - quiescence.BaselineRssMb
            : -1;
        return new DormantTimerResult(
            started.Elapsed, failureAtUtc is null && started.Elapsed >= window,
            initialDbWrites, finalDbWrites, quiescence.WarmupReadyAtUtc,
            quiescence.WarmupDuration.TotalSeconds, quiescence.DormantSpecs,
            quiescence.GamePid, quiescence.ReadyMarker, quiescence.BaselineRssMb,
            quiescence.StartupPeakRssMb, R(steadyStatePeak), R(rssGrowth),
            quiescence.QuiescenceDuration, failureAtUtc, failureElapsed, samples, failures);
    }

    private static async Task<JsonElement> MetricsAsync(CancellationToken cancellationToken)
    {
        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        return await bridge.CallAsync("{\"cmd\":\"metrics\"}", 15000, cancellationToken);
    }

    private static DormantTimerSample ReadDormantTimerSample(JsonElement metrics, long dbWrites)
    {
        var population = metrics.GetProperty("population");
        var dormancy = population.GetProperty("dormancy");
        var tick = metrics.GetProperty("tick");
        var region = metrics.GetProperty("regionTick");
        var scheduler = metrics.GetProperty("scheduler");
        var save = metrics.GetProperty("save");
        return new DormantTimerSample(
            DateTime.UtcNow,
            metrics.GetProperty("uptimeMs").GetInt64(),
            ReadRssMb(),
            population.GetProperty("embodied").GetInt32(),
            dormancy.GetProperty("dormantSpecs").GetInt64(),
            dormancy.GetProperty("totalMaterializations").GetInt64(),
            dormancy.GetProperty("totalDematerializations").GetInt64(),
            tick.GetProperty("invokeP95Ms").GetDouble(),
            tick.GetProperty("invokeMaxMs").GetDouble(),
            region.GetProperty("elapsedMs").GetDouble(),
            scheduler.GetProperty("dueQueueDepth").GetInt64(),
            scheduler.GetProperty("eventQueueDepth").GetInt64(),
            scheduler.GetProperty("inFlight").GetInt64(),
            scheduler.GetProperty("totalStepsFailed").GetInt64(),
            save.GetProperty("sampleCount").GetInt64(),
            save.GetProperty("p95Ms").GetDouble(),
            save.GetProperty("maxMs").GetDouble(),
            save.GetProperty("skipCount").GetInt64(),
            dbWrites);
    }

    private static bool ValidateDormantTimerSample(
        DormantTimerSample sample, int seededCount, double baselineRss,
        double startupPeakRss, long? previousUptime, List<string> failures)
    {
        if (sample.DbWrites < 0)
            AddFailure(failures, "DB write counters unavailable");
        if (sample.Embodied != 0)
            AddFailure(failures, $"embodied population is {sample.Embodied}, expected 0 without a human");
        if (sample.DormantSpecs < seededCount)
            AddFailure(failures, $"dormant specs fell to {sample.DormantSpecs}/{seededCount}");
        if (sample.Materializations != 0 || sample.Dematerializations != 0)
            AddFailure(failures, $"unexpected materialization counters {sample.Materializations}/{sample.Dematerializations}");
        if (sample.UptimeMs <= 0 || previousUptime is { } previous && sample.UptimeMs < previous)
            AddFailure(failures, $"server uptime regressed: previous/current={previousUptime}/{sample.UptimeMs}ms");
        if (sample.TickP95Ms > DormantTickP95BudgetMs || sample.TickMaxMs > DormantTickMaxBudgetMs)
            AddFailure(failures, $"tick budget exceeded p95/max={sample.TickP95Ms:F1}/{sample.TickMaxMs:F1}ms");
        if (sample.RegionMs > DormantRegionBudgetMs)
            AddFailure(failures, $"region tick budget exceeded: {sample.RegionMs:F1}ms > {DormantRegionBudgetMs:F0}ms");
        if (sample.DueQueueDepth != 0 || sample.EventQueueDepth != 0 || sample.InFlight != 0)
            AddFailure(failures, $"scheduler queues not empty: due/event/inflight={sample.DueQueueDepth}/{sample.EventQueueDepth}/{sample.InFlight}");
        if (sample.SchedulerFailures != 0 || sample.SaveSkips != 0)
            AddFailure(failures, $"scheduler/save recovery counters nonzero: failures/skips={sample.SchedulerFailures}/{sample.SaveSkips}");
        if (sample.SaveP95Ms > DormantSaveP95BudgetMs || sample.SaveMaxMs > DormantSaveMaxBudgetMs)
            AddFailure(failures, $"save budget exceeded p95/max={sample.SaveP95Ms:F1}/{sample.SaveMaxMs:F1}ms");

        var rssBudgetBreached = ExceedsRssBudget(baselineRss, sample.RssMb);
        if (rssBudgetBreached)
            AddFailure(failures,
                $"steady-state RSS growth exceeded {DormantRssGrowthBudgetMb:F0}MB: " +
                $"baseline/sample={baselineRss:F1}/{sample.RssMb:F1}MB; " +
                $"startup peak={startupPeakRss:F1}MB is excluded from this steady-state budget");
        return rssBudgetBreached;
    }

    private static bool IsRssQuiescent(IReadOnlyList<double> readings)
        => readings.Count >= RssQuiescenceConsecutiveSamples &&
           readings.TakeLast(RssQuiescenceConsecutiveSamples).Max() -
           readings.TakeLast(RssQuiescenceConsecutiveSamples).Min() <= RssQuiescenceMaxRangeMb;

    private static bool ExceedsRssBudget(double baselineRss, double sampleRss)
        => baselineRss > 0 && sampleRss > baselineRss + DormantRssGrowthBudgetMb;

    private static void AddFailure(List<string> failures, string failure)
    {
        if (!failures.Contains(failure, StringComparer.Ordinal))
            failures.Add(failure);
    }

    private static long ReadDbWriteCounters()
    {
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SHOW GLOBAL STATUS WHERE Variable_name IN ('Com_insert','Com_update','Com_delete','Com_replace')";
            using var reader = cmd.ExecuteReader();
            long total = 0;
            while (reader.Read())
                total += reader.GetInt64(1);
            return total;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[a5t3-sixhour] DB counter read failed: {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    private static void WriteDormantTimerReport(
        int windowMinutes, int sampleSeconds, int dormantTarget, int seededCount,
        DormantTimerResult result)
    {
        Directory.CreateDirectory(EvidenceDir);
        var dbWrites = Math.Max(0, result.FinalDbWrites - result.InitialDbWrites);
        var dbWritesPerMinute = result.Window.TotalMinutes > 0
            ? dbWrites / result.Window.TotalMinutes
            : double.PositiveInfinity;
        var report = new
        {
            probe = "G2-A5 Tier-3 natural dormant-timer soak (operator opt-in)",
            runAtUtc = DateTime.UtcNow.ToString("O"),
            commit = E2eStack.SourceRevision,
            config = new { dormantTarget, seededCount, windowMinutes, sampleSeconds },
            budgets = new
            {
                embodied = 0,
                dormantSpecsMinimum = seededCount,
                materializations = 0,
                dematerializations = 0,
                tickP95Ms = DormantTickP95BudgetMs,
                tickMaxMs = DormantTickMaxBudgetMs,
                regionMs = DormantRegionBudgetMs,
                queueDepth = 0,
                schedulerFailures = 0,
                saveP95Ms = DormantSaveP95BudgetMs,
                saveMaxMs = DormantSaveMaxBudgetMs,
                saveSkips = 0,
                rssGrowthMb = DormantRssGrowthBudgetMb,
                dbWritesPerMinute = DormantDbWritesBudgetPerMin
            },
            // `window` is measured from the post-warmup baseline; an RSS
            // breach intentionally produces an ABORTED partial window.
            window = result.Window.TotalMinutes,
            targetWindowMinutes = windowMinutes,
            windowCompleted = result.CompletedFullWindow,
            windowStatus = result.CompletedFullWindow ? "FULL" : "PARTIAL",
            warmupReadyAtUtc = result.WarmupReadyAtUtc.ToString("O"),
            warmupDurationSeconds = Math.Round(result.WarmupDurationSeconds, 1),
            warmupBaselineRssMb = result.BaselineRssMb,
            startupPeakRssMb = result.StartupPeakRssMb,
            steadyStatePeakRssMb = result.SteadyStatePeakRssMb,
            rssGrowthMb = result.RssGrowthMb,
            quiescenceDurationSeconds = Math.Round(result.QuiescenceDuration.TotalSeconds, 1),
            warmup = new
            {
                readyMarker = result.WarmupReadyMarker,
                readyAtUtc = result.WarmupReadyAtUtc.ToString("O"),
                warmupDurationSeconds = Math.Round(result.WarmupDurationSeconds, 1),
                quiescenceDurationSeconds = Math.Round(result.QuiescenceDuration.TotalSeconds, 1),
                signal = "WaitBoot tick.available + WaitDormantDiscovered expected dormantSpecs, then three RSS readings",
                deferredStartupLimitation = "No product-level world-load-complete marker; RSS quiescence is the bounded readiness guard",
                dormantSpecs = result.WarmupDormantSpecs,
                gamePid = result.WarmupGamePid,
                baselineRssMb = result.BaselineRssMb
            },
            rss = new
            {
                startupPeakRssMb = result.StartupPeakRssMb,
                baselineRssMb = result.BaselineRssMb,
                steadyStatePeakRssMb = result.SteadyStatePeakRssMb,
                rssGrowthMb = result.RssGrowthMb,
                steadyStateBudgetMb = DormantRssGrowthBudgetMb
            },
            failureAtUtc = result.FailureAtUtc?.ToString("O"),
            failureElapsedSeconds = result.FailureElapsed?.TotalSeconds,
            initialDbWrites = result.InitialDbWrites,
            finalDbWrites = result.FinalDbWrites,
            dbWrites,
            dbWritesPerMinute,
            sampleCount = result.Samples.Count,
            samples = result.Samples,
            failures = result.Failures,
            passed = result.Failures.Count == 0,
            sixHourDormantTimersLeg = result.CompletedFullWindow ? "RUN" : "ABORTED"
        };
        File.WriteAllText(
            Path.Combine(EvidenceDir, "g2-a5-tier3-sixhour-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<BotNetworkSession> ConnectHumanAsync()
    {
        // First connect after a fresh boot can flake (account/char provisioning
        // race) — bounded retries, no loop-polling.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await BotNetworkSession.ConnectAsync(
                    HumanChar, HumanAccount, HumanPassword,
                    "127.0.0.1", E2eStack.LoginPort,
                    "127.0.0.1", E2eStack.GamePort,
                    "127.0.0.1", E2eStack.StreamPort);
            }
            catch when (attempt < 3)
            {
                Console.WriteLine($"[a5t3] human connect attempt {attempt} failed — retrying");
                await Task.Delay(15_000);
            }
        }
    }

    private static bool TryReadHumanHome(out float x, out float y, out float z)
    {
        x = y = z = 0;
        try
        {
            using var conn = E2eStack.OpenDb("aaemu_game");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT c.`x`, c.`y`, c.`z` FROM `characters` c " +
                              "JOIN aaemu_login.users u ON u.`id` = c.`account_id` " +
                              "WHERE u.`username` = @u AND c.`deleted` = 0 LIMIT 1";
            cmd.Parameters.AddWithValue("@u", HumanAccount);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return false;
            x = reader.GetFloat("x");
            y = reader.GetFloat("y");
            z = reader.GetFloat("z");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[a5t3] human home read failed: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------- seed


    private static List<string> BuildOwnershipNames(int dormantTarget, int embodiedTarget)
    {
        var names = new List<string>(dormantTarget + embodiedTarget + 1) { HumanAccount };
        for (var i = 1; i <= embodiedTarget; i++)
            names.Add($"bot_managed_presence_{i:D3}");
        for (var i = 1; i <= embodiedTarget; i++)
            names.Add($"bot_managed_dormnear{i:D3}");
        for (var i = 1; i <= dormantTarget - embodiedTarget; i++)
            names.Add($"bot_managed_dormfar{i:D4}");
        return names;
    }
    private static void CleanupBaselinePresenceBots(
        IReadOnlyCollection<E2eStack.OwnedBotRow> ownershipBefore, int embodiedTarget)
    {
        var names = Enumerable.Range(1, embodiedTarget)
            .Select(i => $"bot_managed_presence_{i:D3}")
            .ToArray();
        var after = E2eStack.SnapshotOwnedRows(names);
        var ownedRows = E2eStack.FindNewOwnedRows(ownershipBefore, after);
        E2eStack.CleanupOwnedRows(ownedRows);
        Console.WriteLine($"[a5t3] removed {ownedRows.Count} baseline presence rows before dormant seeding");
    }


    private static int CountManagedCharacters()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM characters c " +
                          "JOIN aaemu_login.users u ON u.id = c.account_id " +
                          "WHERE u.username LIKE 'bot_managed_%' AND c.delete_time = '0001-01-01 00:00:00'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    /// <summary>
    /// Seeds the dormant roster through the real provisioning path via the
    /// bridge bulk command, BOUNDED-PARALLEL: SEED_CONCURRENCY workers (env
    /// knob, default 4) each drive their own bridge connection so provisioning
    /// runs concurrently on the server. Safe since the NameManager registry
    /// lock fix (tier3 report §11.2 corruption); SEED_CONCURRENCY=1 restores
    /// the legacy sequential behavior. Per-batch elapsed logging drives an
    /// honest projection; a failed batch aborts further batches with partials
    /// recorded.
    /// </summary>

    private static async Task<TimeSpan> SeedDormant(
        float hx, float hy, float hz, int nearCount, int farCount,
        CancellationToken cancellationToken = default)
    {
        var bots = new List<object>(nearCount + farCount);
        for (var i = 0; i < nearCount; i++)
        {
            var angle = 2 * Math.PI * i / Math.Max(1, nearCount);
            bots.Add(new
            {
                name = $"DormNear{i + 1:D3}",
                home = new { x = hx + 40f * (float)Math.Cos(angle), y = hy + 40f * (float)Math.Sin(angle), z = hz }
            });
        }
        for (var i = 0; i < farCount; i++)
        {
            // ≥500m east grid — never inside ReducedProximityRadiusM (200m).
            bots.Add(new
            {
                name = $"DormFar{i + 1:D4}",
                home = new { x = hx + 500f + 50f * (i % 60), y = hy + 400f * (i / 60), z = hz }
            });
        }

        var sw = Stopwatch.StartNew();
        const int batch = 25;
        var chunks = new List<List<object>>();
        for (var offset = 0; offset < bots.Count; offset += batch)
            chunks.Add(bots.Skip(offset).Take(batch).ToList());

        var workers = Math.Max(1, ParseSeedConcurrency());
        var next = 0;
        var aborted = 0;
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task RunWorkerAsync()
        {
            while (true)
            {
                if (stopCts.IsCancellationRequested)
                    return;
                var idx = Interlocked.Increment(ref next) - 1;
                if (Volatile.Read(ref aborted) != 0 || idx >= chunks.Count)
                    return;

                var chunk = chunks[idx];
                var batchStart = Stopwatch.StartNew();
                try
                {
                    using var bridge = new BotDriveClient(E2eStack.BridgePort);
                    var reply = await bridge.CallAsync(
                        JsonSerializer.Serialize(new { cmd = "seedDormant", level = 5, bots = chunk }),
                        600_000, stopCts.Token);
                    batchStart.Stop();
                    Console.WriteLine($"[a5t3] seed batch {idx + 1}/{chunks.Count}: seeded={reply.GetProperty("seeded").GetInt32()} " +
                                      $"in {batchStart.Elapsed.TotalSeconds:F1}s (total {sw.Elapsed.TotalMinutes:F1}min)");
                }
                catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref aborted, 1) == 0)
                        Console.WriteLine($"[a5t3] SEED BATCH FAILED @{idx}: {ex.Message} — aborting further batches (partials kept)");
                    stopCts.Cancel();
                    return;
                }
            }
        }

        var workerTasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(RunWorkerAsync, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(workerTasks);
        cancellationToken.ThrowIfCancellationRequested();

        // A failed batch keeps the legacy partial-seed behavior. A caller
        // cancellation/deadline, however, is propagated after all in-flight
        // bridge reads have cooperatively stopped.
        if (Volatile.Read(ref aborted) != 0)
            Console.WriteLine("[a5t3] seed stopped after a failed batch (partials kept)");

        sw.Stop();
        if (sw.Elapsed > SeedBox)
            Console.WriteLine($"[a5t3] WARNING: seed exceeded the {SeedBox.TotalMinutes:F0}-min box ({sw.Elapsed.TotalMinutes:F1} min)");

        return sw.Elapsed;
    }

    /// <summary>SEED_CONCURRENCY env knob — bounded parallelism of seedDormant
    /// batch workers (default 4; 1 = sequential).</summary>
    private static int ParseSeedConcurrency()
        => int.TryParse(Environment.GetEnvironmentVariable("SEED_CONCURRENCY"), out var n) && n > 0
            ? n
            : 4;

    // ------------------------------------------------------- metrics plumbing

    private static void ClearFeatureEnv()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_TRUE_DORMANCY", null);
        Environment.SetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FIDELITY", null);
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", null);
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", null);
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS", null);
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MANIFEST", null);
    }

    private static JsonElement Metrics()
    {
        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        return bridge.Call("{\"cmd\":\"metrics\"}", 15000);
    }

    private sealed record Pop(
        int embodied, long totalMaterializations, long totalDematerializations, long dormantSpecs);

    private static Pop Population(JsonElement m)
    {
        if (!m.TryGetProperty("population", out var po) || po.ValueKind != JsonValueKind.Object)
            return new Pop(0, 0, 0, 0);
        var embodied = po.TryGetProperty("embodied", out var em) ? em.GetInt32() : 0;
        var matz = po.TryGetProperty("totalMaterializations", out var tm) ? tm.GetInt64() : 0;
        var dematz = po.TryGetProperty("totalDematerializations", out var td) ? td.GetInt64() : 0;
        long specs = 0;
        if (po.TryGetProperty("dormancy", out var dor) && dor.ValueKind == JsonValueKind.Object &&
            dor.TryGetProperty("dormantSpecs", out var ds))
            specs = ds.GetInt64();
        return new Pop(embodied, matz, dematz, specs);
    }

    private static (long count, double p50, double p95, double p99, double max) Latency(JsonElement m)
    {
        if (m.TryGetProperty("population", out var po) && po.ValueKind == JsonValueKind.Object &&
            po.TryGetProperty("dormancy", out var dor) && dor.ValueKind == JsonValueKind.Object &&
            dor.TryGetProperty("materializeP95Ms", out _))
            return (
                dor.GetProperty("materializeCount").GetInt64(),
                dor.GetProperty("materializeP50Ms").GetDouble(),
                dor.GetProperty("materializeP95Ms").GetDouble(),
                dor.GetProperty("materializeP99Ms").GetDouble(),
                dor.GetProperty("materializeMaxMs").GetDouble());
        return (0, -1, -1, -1, -1);
    }

    private static void WaitBoot(Action<double>? observeRss = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(300);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                observeRss?.Invoke(ReadRssMb());
                var m = Metrics();
                if (m.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                    tk.TryGetProperty("available", out var av) && av.GetBoolean())
                    return;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* bridge hiccup during boot */ }
            if (cancellationToken.WaitHandle.WaitOne(5000))
                cancellationToken.ThrowIfCancellationRequested();
        }
        throw new TimeoutException("game server never became metric-ready after restart");
    }

    private static void WaitDormantDiscovered(int expected, Action<double>? observeRss = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var last = -1L;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                observeRss?.Invoke(ReadRssMb());
                last = Population(Metrics()).dormantSpecs;
                if (last >= expected)
                    return;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* bridge hiccup */ }
            if (cancellationToken.WaitHandle.WaitOne(2000))
                cancellationToken.ThrowIfCancellationRequested();
        }
        throw new TimeoutException($"dormant discovery stalled at {last}/{expected} specs");
    }
    private static async Task<RssQuiescenceResult> WaitForRssQuiescenceAsync(
        StartupRssTracker startupRss, int expectedDormantSpecs,
        DateTime warmupStartedAtUtc, CancellationToken cancellationToken)
    {
        var readings = new List<double>(RssQuiescenceConsecutiveSamples);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RssQuiescenceTimeout);
        var started = Stopwatch.StartNew();
        var quiescenceStartedAtUtc = DateTime.UtcNow;
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var rss = ReadRssMb();
            startupRss.Observe(rss);
            if (rss > 0)
            {
                readings.Add(rss);
                if (readings.Count > RssQuiescenceConsecutiveSamples)
                    readings.RemoveAt(0);

                if (IsRssQuiescent(readings))
                {
                    var metrics = await MetricsAsync(timeout.Token);
                    var dormantSpecs = Population(metrics).dormantSpecs;
                    if (dormantSpecs >= expectedDormantSpecs)
                    {
                        var baseline = readings.Average();
                        var readyAt = DateTime.UtcNow;
                        var warmupDuration = readyAt - warmupStartedAtUtc;
                        var quiescenceDuration = readyAt - quiescenceStartedAtUtc;
                        var gamePid = FindGamePid()
                            ?? throw new InvalidOperationException("game PID unavailable at warmup boundary");
                        var marker = $"A5_WARMUP_READY utc={readyAt:O} gamePid={gamePid} " +
                                     $"dormantSpecs={dormantSpecs} baselineRssMb={baseline:F1} " +
                                     $"startupPeakRssMb={startupRss.PeakMb:F1} " +
                                     $"warmupDurationSeconds={warmupDuration.TotalSeconds:F1}";
                        Directory.CreateDirectory(EvidenceDir);
                        File.AppendAllText(
                            Path.Combine(EvidenceDir, "g2-a5-tier3-sixhour-soak.log"),
                            marker + Environment.NewLine);
                        Console.WriteLine($"[a5t3-sixhour] {marker}");
                        started.Stop();
                        return new RssQuiescenceResult(
                            readyAt, baseline, startupRss.PeakMb, warmupDuration,
                            quiescenceDuration, dormantSpecs, gamePid, marker);
                    }
                    readings.Clear();
                }
            }

            var remaining = RssQuiescenceTimeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException(
                    $"RSS did not quiesce within {RssQuiescenceTimeout.TotalMinutes:F0} minutes");
            await Task.Delay(
                remaining < RssQuiescenceSampleInterval ? remaining : RssQuiescenceSampleInterval,
                timeout.Token);
        }
    }



    private static void WaitEmbodied(int target, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + window;
        var lastReported = -1;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var m = Metrics();
                var embodied = m.TryGetProperty("population", out var po) && po.TryGetProperty("embodied", out var em)
                    ? em.GetInt32() : 0;
                var stepping = m.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
                               sc.TryGetProperty("totalStepsRun", out var sr) && sr.GetInt64() > 0;
                if (embodied != lastReported)
                {
                    Console.WriteLine($"[a5t3] boot poll: embodied={embodied}/{target}");
                    lastReported = embodied;
                }
                if (embodied >= target && stepping)
                    return;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* bridge hiccup during boot */ }
            if (cancellationToken.WaitHandle.WaitOne(5000))
                cancellationToken.ThrowIfCancellationRequested();
        }
        throw new TimeoutException($"tier3 probe: baseline only reached partial embodiment within {window.TotalMinutes}min");
    }

    private static async Task<JsonElement> WaitDematerialized(int embodiedTarget, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var last = Population(Metrics());
        while (DateTime.UtcNow < deadline)
        {
            last = Population(Metrics());
            if (last.totalDematerializations >= embodiedTarget && last.embodied == 0)
                break;
            await Task.Delay(3000);
        }
        Console.WriteLine($"[a5t3] post-leave: embodied={last.embodied} dematerialized={last.totalDematerializations}");
        return JsonSerializer.SerializeToElement(new
        {
            embodied = last.embodied,
            totalDematerializations = last.totalDematerializations,
            dormantSpecs = last.dormantSpecs
        });
    }

    // --------------------------------------------------------------- sampling

    private static List<Sample> SampleWindow(int minutes, CancellationToken cancellationToken = default)
    {
        var samples = new List<Sample>();
        var windowStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - windowStart).TotalMinutes < minutes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var m = Metrics();
                double TickP() => m.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                                  tk.TryGetProperty("invokeP95Ms", out var tp) ? tp.GetDouble() : -1;
                double Region() => m.TryGetProperty("regionTick", out var rt) && rt.ValueKind == JsonValueKind.Object
                    ? rt.GetProperty("elapsedMs").GetDouble() : -1;
                long Steps() => m.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
                                sc.TryGetProperty("available", out var sa) && sa.GetBoolean() &&
                                sc.TryGetProperty("totalStepsRun", out var sr) ? sr.GetInt64() : -1;

                samples.Add(new Sample(DateTime.UtcNow, ReadRssMb(), TickP(), Region(), Steps()));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"[a5t3] sample lost: {ex.Message}");
            }
            if (cancellationToken.WaitHandle.WaitOne(15_000))
                cancellationToken.ThrowIfCancellationRequested();
        }
        return samples;
    }

    private static ArmResult Summarize(List<Sample> samples, int embodied, long dormantSpecs,
        (long count, double p50, double p95, double p99, double max) lat, double windowSec)
    {
        var rss = Pos(samples.Select(s => s.RssMb));
        var tick = samples.Where(s => s.TickP95 >= 0).Select(s => s.TickP95).ToList();
        var region = samples.Where(s => s.RegionMs >= 0).Select(s => s.RegionMs).ToList();

        var first = samples.FirstOrDefault()?.Steps ?? -1;
        var last = samples.LastOrDefault()?.Steps ?? -1;
        var spanMin = samples.Count > 1 ? (samples[^1].At - samples[0].At).TotalMinutes : 1;

        return new ArmResult(
            EmbodiedEnd: embodied,
            DormantSpecsEnd: dormantSpecs,
            MaterializeCount: lat.count,
            MaterializeP50Ms: R(lat.p50),
            MaterializeP95Ms: R(lat.p95),
            MaterializeP99Ms: R(lat.p99),
            MaterializeMaxMs: R(lat.max),
            MaterializeWindowSec: Math.Round(windowSec, 1),
            RssMedianMb: R(rss.Count > 0 ? rss[rss.Count / 2] : -1),
            RssMinMb: R(rss.Count > 0 ? rss.Min() : -1),
            RssMaxMb: R(rss.Count > 0 ? rss.Max() : -1),
            TickP95MedianMs: R(tick.Count > 0 ? tick[tick.Count / 2] : -1),
            TickP95MaxMs: R(tick.Count > 0 ? tick.Max() : -1),
            RegionWorstMs: R(region.Count > 0 ? region.Max() : -1),
            StepsPerMin: first >= 0 && last >= first && spanMin > 0 ? (long)((last - first) / spanMin) : -1);
    }

    private static List<double> Pos(IEnumerable<double> v) => v.Where(x => x > 0).OrderBy(x => x).ToList();
    private static double R(double v) => Math.Round(v, 1);

    // --------------------------------------------------------------- manifest

    private static string WriteManifest(int citizens)
    {
        // Same pinned Nuian template spawn home as the G2 scaling curve / A4 probe.
        const float homeX = 15578.042f, homeY = 15382.122f, homeZ = 126.484f;
        string[] races = ["Nuian", "Elf"];
        string[] genders = ["Male", "Female"];

        var entries = new List<object>(citizens);
        for (var i = 0; i < citizens; i++)
        {
            entries.Add(new
            {
                name = $"T3Cit{i + 1:D3}",
                race = races[i % races.Length],
                gender = genders[i % genders.Length],
                level = 5,
                home = new { x = homeX, y = homeY, z = homeZ },
                personality = $"a5t3-{i + 1:D3}"
            });
        }

        var dir = Path.Combine(E2eStack.E2eRoot, "runtime");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a5-tier3-manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // --------------------------------------------------------------------- io

    private static void WriteReport(DateTime runAt, int minutesPerArm, int dormantTarget, int embodiedTarget,
        int seededCount, TimeSpan seedElapsed, ArmResult baseline, ArmResult tier3, JsonElement postLeave,
        double rssDeltaPct, bool rssPass, bool matPass, bool embodiedPass)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            probe = "G2-A5 FINAL Tier-3 shape acceptance (pure measurement — verdicts recorded, not asserted)",
            runAtUtc = runAt.ToString("O"),
            commit = E2eStack.SourceRevision,
            config = new
            {
                dormantTarget,
                embodiedTarget,
                seededCount,
                seedElapsedMin = Math.Round(seedElapsed.TotalMinutes, 1),
                seedConcurrency = 1,
                minutesPerArm,
                baselineArm = "flags OFF, 50 ACTIVE presence-demo citizens (real provisioning + roam)",
                tier3Arm = "AAEMU_BOT_TRUE_DORMANCY=1 AAEMU_BOT_PROXIMITY_FIDELITY=1, seeded dormant roster",
                materializeBudget = "default 3/sweep @ 2s cadence (no env raise — 50 embodied ≈ 34s, pacing documented)",
                triggerRoute = "REAL live-human client session (BotNetworkSession TCP login/enter-world) near dormant homes → PopulationDirector.RunProximitySweep → MaterializeNearbyDormantSpecs"
            },
            baselineArm = baseline,
            tier3Arm = tier3,
            postHumanLeave = JsonSerializer.Deserialize<object>(postLeave.GetRawText()),
            verdicts = new
            {
                thousandDormantRegistered = seededCount >= dormantTarget * 0.95,
                seededCount,
                embodiedWithin50 = embodiedPass,
                rssWithin15PctOfBaseline = rssPass,
                rssDeltaPct,
                materializeP95Under3s = matPass,
                materializeP95Ms = tier3.MaterializeP95Ms,
                sixHourDormantTimersLeg = "PENDING — natural home: scheduled soak (out of scope for this measurement)"
            }
        };

        var path = Path.Combine(EvidenceDir, "g2-a5-tier3-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[a5t3] report written: {path}");
    }

    // -------------------------------------------------------------------- rss

    private static int? _gamePid;

    private static double ReadRssMb()
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
        catch { }
        return -1;
    }

    private static int? FindGamePid()
    {
        var root = Path.GetFullPath(E2eStack.E2eRoot);
        if (_gamePid.HasValue && IsGameProcess(_gamePid.Value, root))
            return _gamePid;
        _gamePid = null;

        foreach (var proc in Process.GetProcessesByName("dotnet"))
        {
            try
            {
                using var p = proc;
                if (IsGameProcess(p.Id, root))
                {
                    _gamePid = p.Id;
                    return _gamePid;
                }
            }
            catch { }
        }
        return null;
    }

    private static bool IsGameProcess(int pid, string root)
    {
        var cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
        if (!cmdline.Contains("AAEmu.Game.dll", StringComparison.Ordinal))
            return false;
        var cwd = new FileInfo($"/proc/{pid}/cwd").LinkTarget;
        return cwd != null &&
               (Path.GetFullPath(cwd).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFullPath(cwd).Equals(root, StringComparison.Ordinal));
    }
}
