using System.Diagnostics;
using System.Text.Json;

using AAEmu.IntegrationTests.E2e;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.G2;

/// <summary>
/// G2-A5 true-dormancy ACCEPTANCE probe (pure measurement — budget verdicts
/// are recorded, not asserted): measures whether a ~100-dormant /
/// human-triggered-materialized world costs ≈ a no-bot world, and how long a
/// proximity-triggered materialization takes.
///
/// Phases (single fact — one report, both configs):
///   B  BASELINE     : flags OFF, zero bots, ONE real human client online
///                     (real login → create/select/spawn over TCP) → sample.
///   S  SEED         : bridge 'seedDormant' mints N managed accounts +
///                     characters through HeadlessSession.Provision, records
///                     each playerbot_metadata home (HasHome prerequisite),
///                     deactivates — durable dormant specs. `near` homes sit
///                     within the 200m ReducedProximityRadiusM of the human,
///                     the rest ≥500m away (never proximity-matched).
///   C  TRUE DORMANCY: AAEMU_BOT_TRUE_DORMANCY=1 + AAEMU_BOT_PROXIMITY_FIDELITY=1,
///                     restart, same human reconnects → PopulationDirector
///                     proximity sweep materializes the nearby specs through
///                     the REAL sweep path → sample. Human leaves → observe
///                     dematerialization back to 0 embodied.
///
/// Targets (roadmap G2-A5): RSS within 15 % of baseline; materialize p95 < 3 s.
/// Report: g2-a5-acceptance-report.json under $E2E_ROOT/logs.
/// </summary>
[Collection("e2e")]
public class A5AcceptanceProbeTests
{
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    private const string HumanAccount = "a5humanacct";
    private const string HumanChar = "A5Human";
    private const string HumanPassword = "e2e-secret";

    private sealed record Sample(DateTime At, double RssMb, double TickP95, double RegionMs, long Steps);

    private sealed record BaselineResult(
        int EmbodiedEnd, double RssMedianMb, double RssMinMb, double RssMaxMb,
        double TickP95MedianMs, double RegionWorstMs, long StepsPerMin);

    private sealed record DormancyResult(
        int EmbodiedEnd, long DormantSpecsEnd,
        long MaterializeCount, double MaterializeP50Ms, double MaterializeP95Ms, double MaterializeMaxMs,
        double MaterializeWindowSec,
        double RssMedianMb, double RssMinMb, double RssMaxMb,
        double TickP95MedianMs, double RegionWorstMs, long StepsPerMin);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Probe_A5TrueDormancy_Acceptance()
    {
        var minutesPerPhase = int.TryParse(Environment.GetEnvironmentVariable("SCALING_PROBE_MINUTES"), out var m) && m > 0 ? m : 2;
        var dormantTarget = int.TryParse(Environment.GetEnvironmentVariable("A5_DORMANT_COUNT"), out var dc) && dc > 0 ? dc : 100;
        var nearTarget = int.TryParse(Environment.GetEnvironmentVariable("A5_NEAR_COUNT"), out var nc) && nc > 0 ? nc : 10;
        var runAt = DateTime.UtcNow;

        E2eStack.EnsureUp();
        var ownedNames = BuildOwnershipNames(dormantTarget, nearTarget);
        var ownershipBefore = E2eStack.SnapshotOwnedRows(ownedNames);
        try
        {

        // ------------------------------------------------------ PHASE B: baseline
        Console.WriteLine("[a5] phase B: no-bot baseline (flags off, human online)");
        ClearFeatureEnv();
        E2eStack.RestartGameServer();
        WaitBoot();

        BaselineResult baseline;
        float hx, hy, hz;
        using (var human = await ConnectHumanAsync())
        {
            await Task.Delay(30_000); // provisioning/GC storms pass
            var samples = SampleWindow(minutesPerPhase);
            baseline = SummarizeBaseline(samples);

            if (!TryReadHumanHome(out hx, out hy, out hz))
                throw new InvalidOperationException("could not read the human character's home coordinates from MySQL");
        }
        Console.WriteLine($"[a5] baseline: rssMed={baseline.RssMedianMb}MB tickP95med={baseline.TickP95MedianMs}ms " +
                          $"regionWorst={baseline.RegionWorstMs}ms steps/min={baseline.StepsPerMin}; human at ({hx:F0},{hy:F0},{hz:F0})");

        // ------------------------------------------------------------ PHASE S: seed
        Console.WriteLine($"[a5] phase S: seeding {dormantTarget} dormant specs ({nearTarget} near / {dormantTarget - nearTarget} far)");
        SeedDormant(hx, hy, hz, nearTarget, dormantTarget - nearTarget);
        var seededCount = CountManagedCharacters();
        Assert.True(seededCount >= dormantTarget,
            $"seed produced only {seededCount}/{dormantTarget} discoverable managed characters");

        // ------------------------------------------------ PHASE C: true dormancy
        Console.WriteLine("[a5] phase C: true dormancy on, waiting for proximity materializations");
        Environment.SetEnvironmentVariable("AAEMU_BOT_TRUE_DORMANCY", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_PROXIMITY_FIDELITY", "1");
        E2eStack.RestartGameServer();
        WaitDormantDiscovered(dormantTarget);

        DormancyResult dormancy;
        JsonElement finalMetrics;
        using (var human = await ConnectHumanAsync())
        {
            var connectedAt = DateTime.UtcNow;
            var materializeDeadline = connectedAt + TimeSpan.FromSeconds(180);
            var allMaterializedAt = DateTime.MinValue;
            while (DateTime.UtcNow < materializeDeadline)
            {
                if (Population(Metrics()).totalMaterializations >= nearTarget)
                {
                    allMaterializedAt = DateTime.UtcNow;
                    break;
                }
                await Task.Delay(1000);
            }

            var materializeWindowSec = allMaterializedAt > connectedAt
                ? (allMaterializedAt - connectedAt).TotalSeconds : -1;
            Console.WriteLine($"[a5] all {nearTarget} nearby specs materialized {materializeWindowSec:F1}s after connect");

            await Task.Delay(60_000); // settle
            var samples = SampleWindow(minutesPerPhase);
            finalMetrics = Metrics();
            dormancy = SummarizeDormancy(samples, finalMetrics, Population(finalMetrics), materializeWindowSec);
        }
        Console.WriteLine($"[a5] dormancy: rssMed={dormancy.RssMedianMb}MB embodiedEnd={dormancy.EmbodiedEnd} " +
                          $"matCount={dormancy.MaterializeCount} p50/p95/max={dormancy.MaterializeP50Ms:F0}/{dormancy.MaterializeP95Ms:F0}/{dormancy.MaterializeMaxMs:F0}ms " +
                          $"steps/min={dormancy.StepsPerMin}");

        // Dematerialization evidence: with the human gone the by-design
        // steady state is 0 embodied again (~3 no-human sweeps).
        var postLeave = await WaitDematerialized(nearTarget, TimeSpan.FromSeconds(45));

        // ------------------------------------------------------------- verdicts
        var rssDeltaPct = baseline.RssMedianMb > 0
            ? Math.Round((dormancy.RssMedianMb - baseline.RssMedianMb) / baseline.RssMedianMb * 100.0, 2) : -1;
        var rssPass = rssDeltaPct is >= -15 and <= 15;
        var matPass = dormancy.MaterializeP95Ms >= 0 && dormancy.MaterializeP95Ms < 3000;

        WriteReport(runAt, minutesPerPhase, dormantTarget, nearTarget,
            seededCount, baseline, dormancy, postLeave, rssDeltaPct, rssPass, matPass);

        // Validity only (budget verdicts live in the report): the real sweep
        // path must have materialized SOMETHING for the numbers to mean anything.
        Assert.True(dormancy.MaterializeCount > 0,
            "no proximity materialization ever fired — trigger path broken");
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
                Console.WriteLine($"[a5] ownership cleanup skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

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
            Console.WriteLine($"[a5] human home read failed: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------- seed

    private static List<string> BuildOwnershipNames(int dormantTarget, int nearTarget)
    {
        var names = new List<string>(dormantTarget + 1) { HumanAccount };
        for (var i = 1; i <= nearTarget; i++)
            names.Add($"bot_managed_dormnear{i:D3}");
        for (var i = 1; i <= dormantTarget - nearTarget; i++)
            names.Add($"bot_managed_dormfar{i:D3}");
        return names;
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

    private static void SeedDormant(float hx, float hy, float hz, int nearCount, int farCount)
    {
        var bots = new List<object>();
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
                name = $"DormFar{i + 1:D3}",
                home = new { x = hx + 500f + 50f * (i % 30), y = hy + 400f * (i / 30), z = hz }
            });
        }

        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        const int batch = 25;
        for (var offset = 0; offset < bots.Count; offset += batch)
        {
            var chunk = bots.Skip(offset).Take(batch).ToList();
            var reply = bridge.Call(JsonSerializer.Serialize(new { cmd = "seedDormant", level = 5, bots = chunk }), 300_000);
            Console.WriteLine($"[a5] seed batch @{offset}: seeded={reply.GetProperty("seeded").GetInt32()}");
        }
    }

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

    private static (long count, double p50, double p95, double max) Latency(JsonElement m)
    {
        if (m.TryGetProperty("population", out var po) && po.ValueKind == JsonValueKind.Object &&
            po.TryGetProperty("dormancy", out var dor) && dor.ValueKind == JsonValueKind.Object &&
            dor.TryGetProperty("materializeP95Ms", out _))
            return (
                dor.GetProperty("materializeCount").GetInt64(),
                dor.GetProperty("materializeP50Ms").GetDouble(),
                dor.GetProperty("materializeP95Ms").GetDouble(),
                dor.GetProperty("materializeMaxMs").GetDouble());
        return (0, -1, -1, -1);
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
        // Reading dormantSpecs itself triggers the registry's one-time lazy
        // discovery — poll until every seeded managed character shows up
        // (nothing is embodied at this point: presence is off).
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

    private static async Task<JsonElement> WaitDematerialized(int nearTarget, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var last = Population(Metrics());
        while (DateTime.UtcNow < deadline)
        {
            last = Population(Metrics());
            if (last.totalDematerializations >= nearTarget && last.embodied == 0)
                break;
            await Task.Delay(3000);
        }
        Console.WriteLine($"[a5] post-leave: embodied={last.embodied} dematerialized={last.totalDematerializations}");
        return JsonSerializer.SerializeToElement(new
        {
            embodied = last.embodied,
            totalDematerializations = last.totalDematerializations,
            dormantSpecs = last.dormantSpecs
        });
    }

    // ---------------------------------------------------------------- sampling

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
                Console.WriteLine($"[a5] sample lost: {ex.Message}");
            }
            Thread.Sleep(15_000);
        }
        return samples;
    }

    private static BaselineResult SummarizeBaseline(List<Sample> samples)
    {
        var rss = Pos(samples.Select(s => s.RssMb));
        var tick = samples.Where(s => s.TickP95 >= 0).Select(s => s.TickP95).ToList();
        var region = samples.Where(s => s.RegionMs >= 0).Select(s => s.RegionMs).ToList();

        var first = samples.FirstOrDefault()?.Steps ?? -1;
        var last = samples.LastOrDefault()?.Steps ?? -1;
        var spanMin = samples.Count > 1 ? (samples[^1].At - samples[0].At).TotalMinutes : 1;

        return new BaselineResult(
            EmbodiedEnd: 0,
            RssMedianMb: R(rss.Count > 0 ? rss[rss.Count / 2] : -1),
            RssMinMb: R(rss.Count > 0 ? rss.Min() : -1),
            RssMaxMb: R(rss.Count > 0 ? rss.Max() : -1),
            TickP95MedianMs: R(tick.Count > 0 ? tick[tick.Count / 2] : -1),
            RegionWorstMs: R(region.Count > 0 ? region.Max() : -1),
            StepsPerMin: first >= 0 && last >= first && spanMin > 0 ? (long)((last - first) / spanMin) : -1);
    }

    private static DormancyResult SummarizeDormancy(List<Sample> samples, JsonElement finalMetrics,
        Pop endPop, double materializeWindowSec)
    {
        var rss = Pos(samples.Select(s => s.RssMb));
        var tick = samples.Where(s => s.TickP95 >= 0).Select(s => s.TickP95).ToList();
        var region = samples.Where(s => s.RegionMs >= 0).Select(s => s.RegionMs).ToList();

        var first = samples.FirstOrDefault()?.Steps ?? -1;
        var last = samples.LastOrDefault()?.Steps ?? -1;
        var spanMin = samples.Count > 1 ? (samples[^1].At - samples[0].At).TotalMinutes : 1;
        var lat = Latency(finalMetrics);

        return new DormancyResult(
            EmbodiedEnd: endPop.embodied,
            DormantSpecsEnd: endPop.dormantSpecs,
            MaterializeCount: lat.count,
            MaterializeP50Ms: lat.p50,
            MaterializeP95Ms: lat.p95,
            MaterializeMaxMs: lat.max,
            MaterializeWindowSec: Math.Round(materializeWindowSec, 1),
            RssMedianMb: R(rss.Count > 0 ? rss[rss.Count / 2] : -1),
            RssMinMb: R(rss.Count > 0 ? rss.Min() : -1),
            RssMaxMb: R(rss.Count > 0 ? rss.Max() : -1),
            TickP95MedianMs: R(tick.Count > 0 ? tick[tick.Count / 2] : -1),
            RegionWorstMs: R(region.Count > 0 ? region.Max() : -1),
            StepsPerMin: first >= 0 && last >= first && spanMin > 0 ? (long)((last - first) / spanMin) : -1);
    }

    private static List<double> Pos(IEnumerable<double> v) => v.Where(x => x > 0).OrderBy(x => x).ToList();
    private static double R(double v) => Math.Round(v, 1);

    // --------------------------------------------------------------------- io

    private static void WriteReport(DateTime runAt, int minutesPerPhase, int dormantTarget, int nearTarget,
        int seededCount, BaselineResult baseline, DormancyResult dormancy, JsonElement postLeave,
        double rssDeltaPct, bool rssPass, bool matPass)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            probe = "G2-A5 true-dormancy acceptance (pure measurement — verdicts recorded, not asserted)",
            runAtUtc = runAt.ToString("O"),
            commit = E2eStack.SourceRevision,
            config = new
            {
                dormantTarget,
                nearTarget,
                seededCount,
                minutesPerPhase,
                flags = "AAEMU_BOT_TRUE_DORMANCY=1 AAEMU_BOT_PROXIMITY_FIDELITY=1",
                triggerRoute = "REAL live-human client session (BotNetworkSession TCP login/enter-world) near dormant homes → PopulationDirector.RunProximitySweep → MaterializeNearbyDormantSpecs"
            },
            baseline = baseline,
            dormancy = dormancy,
            postHumanLeave = JsonSerializer.Deserialize<object>(postLeave.GetRawText()),
            verdicts = new
            {
                rssWithin15PctOfBaseline = rssPass,
                rssDeltaPct,
                materializeP95Under3s = matPass,
                materializeP95Ms = dormancy.MaterializeP95Ms
            }
        };

        var path = Path.Combine(EvidenceDir, "g2-a5-acceptance-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[a5] report written: {path}");
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
