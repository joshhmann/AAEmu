using System.Diagnostics;
using System.Text.Json;

using AAEmu.IntegrationTests.E2e;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.G2;

/// <summary>
/// G2-A3 wake-storm ACCEPTANCE probe (pure measurement — budget verdicts are
/// recorded, not asserted): seeds a population of registered dormant bots in
/// one cluster, drives the REAL proximity-materialization wake storm with a
/// live TCP human, and measures the fidelity-transition latency distribution.
///
/// Acceptance (roadmap G2-A3): 1,000-registered-dormant wake-storm transition
/// p99 &lt; 100 ms, without behavior change. The transition number is the
/// director's own wall-clock ring over TrySetFidelity/Wake/Sleep operations
/// (population.transitions block) — materialization is A5's budget-paced path
/// and is reported separately (population.dormancy).
///
/// Phases:
///   B  BASELINE   : flags OFF, zero bots, one real human online → sample.
///   S  SEED       : bridge 'seedDormant' mints A3_STORM_COUNT managed specs
///                   in an annulus 90–180 m from the human home (Reduced
///                   proximity tier — inside the 200 m radius, outside Full's
///                   75 m) through the REAL provisioning path.
///   U  UNSTAGGERED: AAEMU_BOT_TRUE_DORMANCY=1 + AAEMU_BOT_PROXIMITY_FIDELITY=1,
///                   staggered wakes OFF (production default) → human connects,
///                   the sweep materializes the roster at the engine-default
///                   budget (TrueDormancyMaterializePerSweepMax=3/sweep), every
///                   materialization takes the Dormant→Reduced transition +
///                   PB-004 Wake() re-arm. Sampled end-to-end.
///   T  STAGGERED  : identical, plus AAEMU_BOT_STAGGERED_WAKES=1 — first steps
///                   scheduled at deterministic per-bot phase offsets within
///                   StaggeredWakeWindowMs (default 5 s).
///
/// Report: g2-a3-storm-report.json under $E2E_ROOT/logs.
/// </summary>
[Collection("e2e")]
public class A3StormProbeTests
{
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    private const string HumanAccount = "a3humanacct";
    private const string HumanChar = "A3Human";
    private const string HumanPassword = "e2e-secret";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Probe_A3WakeStorm_Acceptance()
    {
        var stormCount = int.TryParse(Environment.GetEnvironmentVariable("A3_STORM_COUNT"), out var sc) && sc > 0 ? sc : 1000;
        var settleMinutes = int.TryParse(Environment.GetEnvironmentVariable("SCALING_PROBE_MINUTES"), out var m) && m > 0 ? m : 2;
        var runAt = DateTime.UtcNow;

        E2eStack.EnsureUp();
        WipeManagedBotRows();

        // ------------------------------------------------------ PHASE B: baseline
        Console.WriteLine("[a3] phase B: no-bot baseline (flags off, human online)");
        ClearFeatureEnv();
        E2eStack.RestartGameServer();
        WaitBoot();

        float hx, hy, hz;
        List<Sample> baselineSamples;
        using (var human = await ConnectHumanAsync())
        {
            await Task.Delay(30_000);
            baselineSamples = SampleWindow(settleMinutes / 2 > 0 ? settleMinutes / 2 : 1, 15_000);

            if (!TryReadHumanHome(out hx, out hy, out hz))
                throw new InvalidOperationException("could not read the human character's home coordinates from MySQL");
        }

        Console.WriteLine($"[a3] baseline sampled ({baselineSamples.Count} samples); human at ({hx:F0},{hy:F0},{hz:F0})");

        // ------------------------------------------------------------ PHASE S: seed
        Console.WriteLine($"[a3] phase S: seeding {stormCount} dormant specs in the 90–180 m annulus");
        SeedDormant(hx, hy, hz, stormCount);
        var seededCount = CountManagedCharacters();
        Assert.True(seededCount >= stormCount,
            $"seed produced only {seededCount}/{stormCount} discoverable managed characters");

        // ------------------------------------------------- PHASES U + T: the storm
        var unstaggered = await RunStormPhase("U (unstaggered)", staggered: false, stormCount, settleMinutes);
        var staggered = await RunStormPhase("T (staggered)", staggered: true, stormCount, settleMinutes);

        WriteReport(runAt, stormCount, seededCount, settleMinutes, baselineSamples, unstaggered, staggered);

        // Validity only (budget verdicts live in the report): both storms must
        // have taken the REAL materialization+transition path to mean anything.
        Assert.True(unstaggered.MaterializeCount > 0,
            "unstaggered storm never materialized — trigger path broken");
        Assert.True(staggered.MaterializeCount > 0,
            "staggered storm never materialized — trigger path broken");
    }

    // ------------------------------------------------------------------ phases

    private sealed record Sample(DateTime At, double RssMb, double TickP95, double RegionMs,
        long LastCycleDue, long MaxCycleDue, long DueQueueDepth, long Steps, int Embodied);

    private sealed record StormPhase(
        string Name,
        double TransitionP50Ms, double TransitionP95Ms, double TransitionP99Ms, double TransitionMaxMs, long TransitionCount,
        long MaterializeCount, double MaterializeP50Ms, double MaterializeP95Ms, double MaterializeP99Ms, double MaterializeMaxMs,
        double MaterializeWindowSec,
        double TickP95WorstMs, double RegionWorstMs, double RssMaxMb,
        long MaxCycleDueSeen, double CycleDueP95,
        double AvgWakeLatencyMsEnd, double SchedulerMaxWakeLatencyMsEnd,
        long StepsPerMinSettled,
        bool DematerializeClean, JsonElement FinalMetricsJson);

    private async Task<StormPhase> RunStormPhase(string name, bool staggered, int stormCount, int settleMinutes)
    {
        Console.WriteLine($"[a3] phase {name}: restart + flags on" + (staggered ? " + AAEMU_BOT_STAGGERED_WAKES=1" : ""));
        Environment.SetEnvironmentVariable("AAEMU_BOT_TRUE_DORMANCY", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FIDELITY", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_STAGGERED_WAKES", staggered ? "1" : null);
        E2eStack.RestartGameServer();
        WaitBoot();
        WaitDormantDiscovered(stormCount);

        var samples = new List<Sample>();
        JsonElement final = default;
        Pop popEnd = default;
        double windowSec = -1;
        var cycleDueSeries = new List<long>();
        long stepsPerMin = -1;
        long matzEnd = 0;

        DateTime connectedAt, allMaterializedAt = DateTime.MinValue;
        long matzAtConnect = 0;
        // Baseline captured BEFORE the human can trigger anything: the very
        // first proximity sweep after connect starts materializing (a sweep
        // fires within 2 s), so a post-connect baseline would make the
        // absolute target unreachable.
        matzAtConnect = Population(Metrics()).totalMaterializations;

        using (var human = await ConnectHumanAsync())
        {
            connectedAt = DateTime.UtcNow;
            var stormDeadline = connectedAt + TimeSpan.FromMinutes(45);
            while (DateTime.UtcNow < stormDeadline)
            {
                samples.Add(TakeSample());
                var pop = Population(Metrics());
                if (pop.totalMaterializations - matzAtConnect >= stormCount)
                {
                    allMaterializedAt = DateTime.UtcNow;
                    break;
                }
                await Task.Delay(1_000);
            }

            windowSec = allMaterializedAt > connectedAt
                ? (allMaterializedAt - connectedAt).TotalSeconds : -1;
            Console.WriteLine($"[a3] phase {name}: {Population(Metrics()).totalMaterializations - matzAtConnect}/{stormCount} " +
                              $"materialized in {windowSec:F0}s; settling {settleMinutes}min");

            // ---- settled stepping: spike distribution + steps/min.
            var settledStart = DateTime.UtcNow;
            var stepsFirst = StepsOf(Metrics());
            while ((DateTime.UtcNow - settledStart).TotalMinutes < settleMinutes)
            {
                var s = Metrics();
                cycleDueSeries.Add(NumOr(s, "scheduler", "lastCycleDue"));
                await Task.Delay(500);
            }
            var stepsLast = StepsOf(Metrics());

            final = Metrics();
            popEnd = Population(final);
            var spanMin = Math.Max(0.1, (DateTime.UtcNow - settledStart).TotalMinutes);
            stepsPerMin = stepsLast >= stepsFirst ? (long)((stepsLast - stepsFirst) / spanMin) : -1;
            matzEnd = popEnd.totalMaterializations;
        }
        // Human gone → by-design steady state: the whole roster dematerializes
        // (TrueDormancyNoHumanSweepsToDematerialize consecutive no-human sweeps).
        var dematDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        var dematClean = false;
        var lastPop = Population(Metrics());
        while (DateTime.UtcNow < dematDeadline)
        {
            lastPop = Population(Metrics());
            if (lastPop.embodied == 0 && lastPop.totalDematerializations >= matzEnd)
            {
                dematClean = true;
                break;
            }
            await Task.Delay(3_000);
        }

        Console.WriteLine($"[a3] phase {name}: post-leave embodied={lastPop.embodied} " +
                          $"demat={lastPop.totalDematerializations} clean={dematClean}");

        return BuildPhaseResult(name, samples, final, popEnd, windowSec, cycleDueSeries, stepsPerMin, dematClean);
    }

    private static StormPhase BuildPhaseResult(string name, List<Sample> samples, JsonElement final,
        Pop popEnd, double windowSec, List<long> cycleDueSeries, long stepsPerMin, bool dematClean)
    {
        double Trans(JsonElement f, string field) =>
            f.TryGetProperty("population", out var po) && po.ValueKind == JsonValueKind.Object &&
            po.TryGetProperty("transitions", out var tr) && tr.ValueKind == JsonValueKind.Object &&
            tr.TryGetProperty(field, out var v) ? v.GetDouble() : -1;

        long Mat(JsonElement f, string field) =>
            f.TryGetProperty("population", out var po) && po.ValueKind == JsonValueKind.Object &&
            po.TryGetProperty("dormancy", out var dor) && dor.ValueKind == JsonValueKind.Object &&
            dor.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;

        double MatD(JsonElement f, string field) =>
            f.TryGetProperty("population", out var po) && po.ValueKind == JsonValueKind.Object &&
            po.TryGetProperty("dormancy", out var dor) && dor.ValueKind == JsonValueKind.Object &&
            dor.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : -1;

        var tickWorst = samples.Where(s => s.TickP95 >= 0).Select(s => s.TickP95).DefaultIfEmpty(-1).Max();
        var regionWorst = samples.Where(s => s.RegionMs >= 0).Select(s => s.RegionMs).DefaultIfEmpty(-1).Max();
        var rssMax = samples.Where(s => s.RssMb >= 0).Select(s => s.RssMb).DefaultIfEmpty(-1).Max();
        var cycleSorted = cycleDueSeries.OrderBy(x => x).ToList();
        var cycleP95 = cycleSorted.Count > 0 ? cycleSorted[(int)Math.Min(cycleSorted.Count - 1, Math.Ceiling(0.95 * cycleSorted.Count) - 1)] : -1;

        return new StormPhase(
            Name: name,
            TransitionP50Ms: Trans(final, "p50Ms"),
            TransitionP95Ms: Trans(final, "p95Ms"),
            TransitionP99Ms: Trans(final, "p99Ms"),
            TransitionMaxMs: Trans(final, "maxMs"),
            TransitionCount: (long)Trans(final, "count"),
            MaterializeCount: Mat(final, "materializeCount"),
            MaterializeP50Ms: MatD(final, "materializeP50Ms"),
            MaterializeP95Ms: MatD(final, "materializeP95Ms"),
            MaterializeP99Ms: MatD(final, "materializeP99Ms"),
            MaterializeMaxMs: MatD(final, "materializeMaxMs"),
            MaterializeWindowSec: windowSec,
            TickP95WorstMs: tickWorst,
            RegionWorstMs: regionWorst,
            RssMaxMb: rssMax,
            MaxCycleDueSeen: cycleSorted.DefaultIfEmpty(-1).Max(),
            CycleDueP95: cycleP95,
            AvgWakeLatencyMsEnd: NumOr(final, "scheduler", "avgWakeLatencyMs"),
            SchedulerMaxWakeLatencyMsEnd: NumOr(final, "scheduler", "maxWakeLatencyMs"),
            StepsPerMinSettled: stepsPerMin,
            DematerializeClean: dematClean,
            FinalMetricsJson: final);
    }

    private static long NumOr(JsonElement m, string block, string field)
    {
        // Bridge latency fields are doubles; GetInt64 would throw on them.
        return m.TryGetProperty(block, out var b) && b.ValueKind == JsonValueKind.Object &&
               b.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
            ? (long)v.GetDouble()
            : -1;
    }
    private Sample TakeSample()
    {
        var m = Metrics();
        double TickP() => m.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                          tk.TryGetProperty("invokeP95Ms", out var tp) ? tp.GetDouble() : -1;
        double Region() => m.TryGetProperty("regionTick", out var rt) && rt.ValueKind == JsonValueKind.Object
            ? rt.GetProperty("elapsedMs").GetDouble() : -1;

        return new Sample(
            DateTime.UtcNow,
            ReadRssMb(),
            TickP(),
            Region(),
            NumOr(m, "scheduler", "lastCycleDue"),
            NumOr(m, "scheduler", "maxCycleDue"),
            NumOr(m, "scheduler", "dueQueueDepth"),
            NumOr(m, "scheduler", "totalStepsRun"),
            Population(m).embodied);
    }

    private static List<Sample> SampleWindow(int minutes, int intervalMs)
    {
        var samples = new List<Sample>();
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMinutes < minutes)
        {
            try
            {
                var m = Metrics();
                double TickP() => m.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                                  tk.TryGetProperty("invokeP95Ms", out var tp) ? tp.GetDouble() : -1;
                double Region() => m.TryGetProperty("regionTick", out var rt) && rt.ValueKind == JsonValueKind.Object
                    ? rt.GetProperty("elapsedMs").GetDouble() : -1;
                samples.Add(new Sample(DateTime.UtcNow, ReadRssMb(), TickP(), Region(),
                    NumOr(m, "scheduler", "lastCycleDue"), NumOr(m, "scheduler", "maxCycleDue"),
                    NumOr(m, "scheduler", "dueQueueDepth"), NumOr(m, "scheduler", "totalStepsRun"),
                    Population(m).embodied));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[a3] sample lost: {ex.Message}");
            }
            Thread.Sleep(intervalMs);
        }
        return samples;
    }

    private static long StepsOf(JsonElement m) => NumOr(m, "scheduler", "totalStepsRun");

    // ------------------------------------------------------------------ human

    private static async Task<BotNetworkSession> ConnectHumanAsync()
        => await BotNetworkSession.ConnectAsync(
            HumanChar, HumanAccount, HumanPassword,
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

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
            Console.WriteLine($"[a3] human home read failed: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------- seed

    private static void WipeManagedBotRows()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        foreach (var sql in new[]
                 {
                     "DELETE FROM aaemu_game.playerbot_metadata WHERE character_id IN " +
                     "(SELECT id FROM aaemu_game.characters WHERE account_id IN " +
                     "(SELECT id FROM aaemu_login.users WHERE username LIKE 'bot_managed_%'))",
                     "DELETE FROM aaemu_game.characters WHERE account_id IN " +
                     "(SELECT id FROM aaemu_login.users WHERE username LIKE 'bot_managed_%')",
                     "DELETE FROM aaemu_login.users WHERE username LIKE 'bot_managed_%'"
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Console.WriteLine("[a3] stale managed bot rows wiped");
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
    /// Seeds <paramref name="count"/> specs on a sunflower spiral between 90 m
    /// and 170 m from the human home — every spec sits inside the 200 m
    /// ReducedProximityRadiusM (the storm reaches them all) but outside the
    /// 75 m Full radius (Reduced tier keeps broadcast cost sane at scale).
    /// </summary>
    private static void SeedDormant(float hx, float hy, float hz, int count)
    {
        const float innerR = 90f, outerR = 170f;
        var goldenAngle = Math.PI * (3 - Math.Sqrt(5));

        var bots = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            var r = innerR + (outerR - innerR) * (float)Math.Sqrt((i + 0.5) / count);
            var theta = goldenAngle * i;
            bots.Add(new
            {
                name = $"A3Storm{i + 1:D4}",
                home = new { x = hx + r * (float)Math.Cos(theta), y = hy + r * (float)Math.Sin(theta), z = hz }
            });
        }

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        const int batch = 25;
        for (var offset = 0; offset < bots.Count; offset += batch)
        {
            var chunk = bots.Skip(offset).Take(batch).ToList();
            var reply = bridge.Call(JsonSerializer.Serialize(new { cmd = "seedDormant", level = 5, bots = chunk }), 600_000);
            Console.WriteLine($"[a3] seed batch @{offset}: seeded={reply.GetProperty("seeded").GetInt32()}");
        }
    }

    // ------------------------------------------------------- metrics plumbing

    private static void ClearFeatureEnv()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_TRUE_DORMANCY", null);
        Environment.SetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FIDELITY", null);
        Environment.SetEnvironmentVariable("AAEMU_BOT_STAGGERED_WAKES", null);
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

    private static void WaitBoot()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(300);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var m = Metrics();
                if (m.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                    tk.TryGetProperty("available", out var av) && av.GetBoolean())
                    return;
            }
            catch { /* bridge hiccup during boot */ }
            Thread.Sleep(5000);
        }
        throw new TimeoutException("game server never became metric-ready after restart");
    }

    private static void WaitDormantDiscovered(int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Population(Metrics()).dormantSpecs >= expected)
                    return;
            }
            catch { /* bridge hiccup */ }
            Thread.Sleep(2000);
        }
        throw new TimeoutException($"dormant discovery never reached {expected} specs");
    }

    // --------------------------------------------------------------------- io

    private static void WriteReport(DateTime runAt, int stormCount, int seededCount, int settleMinutes,
        List<Sample> baselineSamples, StormPhase unstaggered, StormPhase staggered)
    {
        Directory.CreateDirectory(EvidenceDir);

        static object[] SamplesAsObjects(List<Sample> samples) =>
            samples.Select(s => (object)new
            {
                at = s.At.ToString("O"),
                rssMb = s.RssMb,
                tickP95Ms = s.TickP95,
                regionMs = s.RegionMs,
                lastCycleDue = s.LastCycleDue,
                maxCycleDue = s.MaxCycleDue,
                dueQueueDepth = s.DueQueueDepth,
                steps = s.Steps,
                embodied = s.Embodied
            }).ToArray();

        var baselineTickWorst = baselineSamples.Where(s => s.TickP95 >= 0).Select(s => s.TickP95).DefaultIfEmpty(-1).Max();
        var baselineRssMed = Median(baselineSamples.Where(s => s.RssMb > 0).Select(s => s.RssMb).ToList());

        static object PhaseObject(StormPhase p, JsonElement finalMetrics) => new
        {
            name = p.Name,
            acceptance = new
            {
                transitionP99Ms = p.TransitionP99Ms,
                transitionP99Under100ms = p.TransitionP99Ms >= 0 && p.TransitionP99Ms < 100
            },
            transitions = new
            {
                count = p.TransitionCount,
                p50Ms = p.TransitionP50Ms,
                p95Ms = p.TransitionP95Ms,
                p99Ms = p.TransitionP99Ms,
                maxMs = p.TransitionMaxMs
            },
            materialization = new
            {
                count = p.MaterializeCount,
                p50Ms = p.MaterializeP50Ms,
                p95Ms = p.MaterializeP95Ms,
                p99Ms = p.MaterializeP99Ms,
                maxMs = p.MaterializeMaxMs,
                fullRosterWindowSec = p.MaterializeWindowSec
            },
            load = new
            {
                tickP95WorstMs = p.TickP95WorstMs,
                regionWorstMs = p.RegionWorstMs,
                rssMaxMb = p.RssMaxMb,
                maxCycleDueSeen = p.MaxCycleDueSeen,
                cycleDueP95 = p.CycleDueP95,
                avgWakeLatencyMsEnd = p.AvgWakeLatencyMsEnd,
                schedulerMaxWakeLatencyMsEnd = p.SchedulerMaxWakeLatencyMsEnd,
                stepsPerMinSettled = p.StepsPerMinSettled
            },
            finalMetrics = JsonSerializer.Deserialize<object>(finalMetrics.GetRawText())
        };

        var report = new
        {
            probe = "G2-A3 wake-storm acceptance (pure measurement — verdicts recorded, not asserted)",
            runAtUtc = runAt.ToString("O"),
            config = new
            {
                stormCount,
                seededCount,
                settleMinutes,
                clusterRadiusM = "90–170 (Reduced tier annulus)",
                flagsUnstaggered = "AAEMU_BOT_TRUE_DORMANCY=1 AAEMU_BOT_PROXIMITY_FIDELITY=1",
                flagsStaggered = "same + AAEMU_BOT_STAGGERED_WAKES=1",
                triggerRoute = "REAL live-human client session near the seeded homes → RunProximitySweep → MaterializeNearbyDormantSpecs → Dormant→Reduced transition + Wake() re-arm"
            },
            baseline = new
            {
                tickP95WorstMs = baselineTickWorst,
                rssMedianMb = baselineRssMed,
                samples = SamplesAsObjects(baselineSamples)
            },
            unstaggered = PhaseObject(unstaggered, unstaggered.FinalMetricsJson),
            staggered = PhaseObject(staggered, staggered.FinalMetricsJson)
        };

        var path = Path.Combine(EvidenceDir, "g2-a3-storm-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[a3] report written: {path}");
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0)
            return -1;
        var sorted = v.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
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
        if (_gamePid.HasValue)
        {
            try { using var _ = Process.GetProcessById(_gamePid.Value); return _gamePid; }
            catch { _gamePid = null; }
        }

        foreach (var proc in Process.GetProcessesByName("dotnet"))
        {
            try
            {
                using var p = proc;
                if (!p.MainModule!.FileName!.Contains("dotnet"))
                    continue;
                var cmdline = File.ReadAllText($"/proc/{p.Id}/cmdline");
                if (!cmdline.Contains("AAEmu.Game.dll"))
                    continue;
                _gamePid = p.Id;
                return _gamePid;
            }
            catch { }
        }
        return null;
    }
}
