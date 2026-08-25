using System.Diagnostics;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.G2;

/// <summary>
/// G2 scaling baseline probe (A5/A3 sizing input — pure measurement, NO
/// budget assertions): boots manifest citizens at ascending embodied counts
/// and records the per-bot cost curve.
///
/// For each tier N in {10, 20, 30}: set presence env → RestartGameServer
/// (game process inherits env) → wait until the scheduler is stepping with
/// all N citizens embodied → settle 120s (provisioning/GC storms pass) →
/// sample bridge metrics + RSS every 15s for SCALING_PROBE_MINUTES (default
/// 6) per tier.
///
/// Derived per tier: RSS min/median/max, tick p95 max, region-tick worst,
/// wake avg/max, steps/min. The cross-tier deltas give the marginal embodied
/// bot cost (RSS + load) — the A5 true-dormancy sizing input and the
/// hardware-vs-software split decision.
///
/// Report: g2-scaling-curve-report.json under $E2E_ROOT/logs.
/// </summary>
[Collection("e2e")]
public class ScalingProbeTests
{
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Probe_EmbodiedCountScaling_CostCurve()
    {
        var minutesPerTier = int.TryParse(Environment.GetEnvironmentVariable("SCALING_PROBE_MINUTES"), out var m) && m > 0 ? m : 6;
        var runAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        var tiers = new List<TierResult>();
        try
        {
            foreach (var n in new[] { 10, 20, 30 })
            {
                var result = await RunTier(n, minutesPerTier);
                tiers.Add(result);
            }
        }
        finally
        {
            WriteReport(runAt, tiers);
        }

        // Validity only: every tier must have actually embodied its citizens.
        foreach (var t in tiers)
            Assert.True(t.Embodied >= t.Citizens,
                $"tier N={t.Citizens} never fully embodied (reached {t.Embodied}) — scaling curve invalid");
    }

    // -------------------------------------------------------------- runner

    private static async Task<TierResult> RunTier(int citizens, int minutes)
    {
        Console.WriteLine($"[scaling] tier N={citizens}, {minutes}min window");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", citizens.ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS", Math.Max(citizens, 10).ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MANIFEST", WriteManifest(citizens));

        // The game process must restart to inherit the new env.
        E2eStack.RestartGameServer();

        using var bridge = new BotDriveClient(E2eStack.BridgePort);

        // Wait until all citizens are embodied AND the scheduler is stepping.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(300);
        var embodied = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var m = bridge.Call("{\"cmd\":\"metrics\"}", 15000);
                embodied = m.TryGetProperty("population", out var po) && po.TryGetProperty("embodied", out var em)
                    ? em.GetInt32() : 0;
                var stepping = m.TryGetProperty("scheduler", out var sc) && sc.TryGetProperty("totalStepsRun", out var sr)
                    && sr.GetInt64() > 0;
                if (embodied >= citizens && stepping)
                    break;
                Console.WriteLine($"[scaling] N={citizens} boot poll: embodied={embodied}/{citizens}");
            }
            catch { /* bridge hiccup during boot */ }
            await Task.Delay(5000);
        }

        Console.WriteLine($"[scaling] N={citizens} embodied={embodied}; settling 120s before measurement...");
        await Task.Delay(120_000);

        var samples = new List<Sample>();
        var windowStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - windowStart).TotalMinutes < minutes)
        {
            try
            {
                var m = bridge.Call("{\"cmd\":\"metrics\"}", 15000);

                double TickP() => m.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                    tk.TryGetProperty("invokeP95Ms", out var tp) ? tp.GetDouble() : -1;
                double Region() => m.TryGetProperty("regionTick", out var rt) && rt.ValueKind == JsonValueKind.Object
                    ? rt.GetProperty("elapsedMs").GetDouble() : -1;
                double WakeAvg() => Sched(m, "avgWakeLatencyMs");
                double WakeMax() => Sched(m, "maxWakeLatencyMs");
                long Steps() => m.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
                    sc.TryGetProperty("available", out var sa) && sa.GetBoolean() &&
                    sc.TryGetProperty("totalStepsRun", out var sr) ? sr.GetInt64() : -1;

                samples.Add(new Sample(DateTime.UtcNow, ReadRssMb(), TickP(), Region(), WakeAvg(), WakeMax(), Steps()));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[scaling] sample lost: {ex.Message}");
            }
            await Task.Delay(15_000);
        }

        var finalMetrics = bridge.Call("{\"cmd\":\"metrics\"}", 15000);
        var embodiedEnd = finalMetrics.TryGetProperty("population", out var po2) && po2.TryGetProperty("embodied", out var em2)
            ? em2.GetInt32() : 0;

        return Summarize(citizens, embodiedEnd, samples);
    }

    private static double Sched(JsonElement m, string field)
        => m.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
           sc.TryGetProperty("available", out var sa) && sa.GetBoolean() &&
           sc.TryGetProperty(field, out var f) ? f.GetDouble() : -1;

    private static TierResult Summarize(int citizens, int embodied, List<Sample> samples)
    {
        var rss = samples.Where(s => s.RssMb > 0).Select(s => s.RssMb).OrderBy(v => v).ToList();
        var tick = samples.Where(s => s.TickP95 >= 0).Select(s => s.TickP95).ToList();
        var region = samples.Where(s => s.RegionMs >= 0).Select(s => s.RegionMs).ToList();
        var wakeAvg = samples.Where(s => s.WakeAvgMs >= 0).Select(s => s.WakeAvgMs).ToList();
        var wakeMax = samples.Where(s => s.WakeMaxMs >= 0).Select(s => s.WakeMaxMs).ToList();

        static double Med(List<double> v) => v.Count == 0 ? -1 : v[v.Count / 2];
        static double Max(List<double> v) => v.Count == 0 ? -1 : v.Max();

        var first = samples.FirstOrDefault().Steps;
        var last = samples.LastOrDefault().Steps;
        var spanMin = samples.Count > 1
            ? (samples[^1].At - samples[0].At).TotalMinutes : 1;
        var stepsPerMin = first >= 0 && last >= first && spanMin > 0 ? (last - first) / spanMin : -1;

        return new TierResult(
            Citizens: citizens,
            Embodied: embodied,
            RssMedianMb: Math.Round(Med(rss), 1),
            RssMinMb: Math.Round(rss.Count > 0 ? rss.Min() : -1, 1),
            RssMaxMb: Math.Round(Max(rss), 1),
            TickP95MedianMs: Math.Round(Med(tick), 2),
            TickP95MaxMs: Math.Round(Max(tick), 2),
            RegionWorstMs: Math.Round(Max(region), 1),
            WakeAvgMs: Math.Round(Med(wakeAvg), 1),
            WakeMaxMs: Math.Round(Max(wakeMax), 1),
            StepsPerMin: (long)stepsPerMin,
            SampleCount: samples.Count);
    }

    // ------------------------------------------------------------------ data

    private sealed record Sample(DateTime At, double RssMb, double TickP95, double RegionMs,
        double WakeAvgMs, double WakeMaxMs, long Steps);

    public sealed record TierResult(
        int Citizens, int Embodied,
        double RssMedianMb, double RssMinMb, double RssMaxMb,
        double TickP95MedianMs, double TickP95MaxMs, double RegionWorstMs,
        double WakeAvgMs, double WakeMaxMs, long StepsPerMin, int SampleCount);

    // ------------------------------------------------------------------ io

    private static string WriteManifest(int citizens)
    {
        // All entries pin the Nuian template spawn home (the documented
        // per-bot override — keeps every citizen in the same neighborhood so
        // region density scales WITH the tier).
        const float homeX = 15578.042f, homeY = 15382.122f, homeZ = 126.484f;
        string[] races = ["Nuian", "Elf"];
        string[] genders = ["Male", "Female"];

        var entries = new List<object>(citizens);
        for (var i = 0; i < citizens; i++)
        {
            entries.Add(new
            {
                name = $"ScaleCit{i + 1:D3}",
                race = races[i % races.Length],
                gender = genders[i % genders.Length],
                level = 5,
                home = new { x = homeX, y = homeY, z = homeZ },
                personality = $"scale-{i + 1:D3}"
            });
        }

        var dir = Path.Combine(E2eStack.E2eRoot, "runtime");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "scaling-probe-manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

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

    private static int? _gamePid;

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

    private static void WriteReport(DateTime runAt, List<TierResult> tiers)
    {
        Directory.CreateDirectory(EvidenceDir);

        // Marginal embodied-bot cost between adjacent tiers.
        var marginal = new List<object>();
        for (var i = 1; i < tiers.Count; i++)
        {
            var prev = tiers[i - 1];
            var cur = tiers[i];
            marginal.Add(new
            {
                fromCount = prev.Citizens,
                toCount = cur.Citizens,
                rssDeltaMb = Math.Round(cur.RssMedianMb - prev.RssMedianMb, 1),
                rssMarginalPerBotMb = Math.Round((cur.RssMedianMb - prev.RssMedianMb) /
                                                 Math.Max(1, cur.Citizens - prev.Citizens), 1),
                stepsPerMinDelta = cur.StepsPerMin - prev.StepsPerMin
            });
        }

        var report = new
        {
            probe = "G2 scaling baseline (pure measurement — no budget assertions)",
            runAtUtc = runAt.ToString("O"),
            minutesPerTier = int.TryParse(Environment.GetEnvironmentVariable("SCALING_PROBE_MINUTES"), out var mm) ? mm : 6,
            tiers = tiers.Select(t => new
            {
                citizens = t.Citizens,
                embodied = t.Embodied,
                rssMedianMb = t.RssMedianMb,
                rssMinMb = t.RssMinMb,
                rssMaxMb = t.RssMaxMb,
                tickP95MedianMs = t.TickP95MedianMs,
                tickP95MaxMs = t.TickP95MaxMs,
                regionWorstMs = t.RegionWorstMs,
                wakeAvgMs = t.WakeAvgMs,
                wakeMaxMs = t.WakeMaxMs,
                schedulerStepsPerMin = t.StepsPerMin,
                samples = t.SampleCount
            }),
            marginalEmbodiedBotCost = marginal,
            a5WallNote = "rssMarginalPerBotMb x target embodied count sizes the A5 dormancy win: " +
                         "dormant bots cost a DB row + metadata instead of this per-bot RSS"
        };

        var path = Path.Combine(EvidenceDir, "g2-scaling-curve-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[scaling] report written: {path}");
        foreach (var t in tiers)
            Console.WriteLine($"[scaling] N={t.Citizens}: rssMed={t.RssMedianMb}MB tickP95med={t.TickP95MedianMs}ms " +
                              $"regionWorst={t.RegionWorstMs}ms wake={t.WakeAvgMs}/{t.WakeMaxMs}ms steps/min={t.StepsPerMin}");
    }
}
