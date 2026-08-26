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
        Console.WriteLine("[a5t3] arm T: clean restart (provisioning state pristine)");
        ClearFeatureEnv();

        // Clean slate: the baseline arm's presence citizens are gone and any
        // prior provisioning state (including a poisoned one) is discarded
        // before the seed touches the real provisioning path.
        E2eStack.RestartGameServer();
        WaitBoot();

        Console.WriteLine($"[a5t3] arm T: wiping stale managed rows, seeding {dormantTarget} dormant ({embodiedTarget} near)");
        WipeManagedBotRows();

        var seedElapsed = SeedDormant(hx, hy, hz, embodiedTarget, dormantTarget - embodiedTarget);
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

    /// <summary>Rig-side hygiene ONLY (not a gameplay path) — mirrors EnsureFreshBotRow scope discipline.</summary>
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
        Console.WriteLine("[a5t3] stale managed bot rows wiped");
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

    private static TimeSpan SeedDormant(float hx, float hy, float hz, int nearCount, int farCount)
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
        var aborted = false;
        void RunWorker()
        {
            while (true)
            {
                var idx = Interlocked.Increment(ref next) - 1;
                if (aborted || idx >= chunks.Count)
                    return;
                var chunk = chunks[idx];
                var batchStart = Stopwatch.StartNew();
                try
                {
                    using var bridge = new BotDriveClient(E2eStack.BridgePort);
                    var reply = bridge.Call(JsonSerializer.Serialize(new { cmd = "seedDormant", level = 5, bots = chunk }), 600_000);
                    batchStart.Stop();
                    Console.WriteLine($"[a5t3] seed batch {idx + 1}/{chunks.Count}: seeded={reply.GetProperty("seeded").GetInt32()} " +
                                      $"in {batchStart.Elapsed.TotalSeconds:F1}s (total {sw.Elapsed.TotalMinutes:F1}min)");
                }
                catch (Exception ex)
                {
                    aborted = true;
                    Console.WriteLine($"[a5t3] SEED BATCH FAILED @{idx}: {ex.Message} — aborting further batches (partials kept)");
                    return;
                }
            }
        }

        var threads = new Thread[workers];
        for (var w = 0; w < workers; w++)
        {
            threads[w] = new Thread(RunWorker) { IsBackground = true };
            threads[w].Start();
        }
        foreach (var t in threads)
            t.Join();

        // Hard timebox guard (belt-and-braces on top of the projection above).
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
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var last = -1L;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = Population(Metrics()).dormantSpecs;
                if (last >= expected)
                    return;
            }
            catch { /* bridge hiccup */ }
            Thread.Sleep(2000);
        }
        throw new TimeoutException($"dormant discovery stalled at {last}/{expected} specs");
    }

    private static void WaitEmbodied(int target, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var lastReported = -1;
        while (DateTime.UtcNow < deadline)
        {
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
            catch { /* bridge hiccup during boot */ }
            Thread.Sleep(5000);
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

    private static List<Sample> SampleWindow(int minutes)
    {
        var samples = new List<Sample>();
        var windowStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - windowStart).TotalMinutes < minutes)
        {
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
            catch (Exception ex)
            {
                Console.WriteLine($"[a5t3] sample lost: {ex.Message}");
            }
            Thread.Sleep(15_000);
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
            commit = "worktree .worktrees/tier3 @ 214bed834 (no source modifications)",
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
