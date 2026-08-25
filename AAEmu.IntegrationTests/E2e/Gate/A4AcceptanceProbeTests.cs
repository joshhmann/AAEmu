using System.Diagnostics;
using System.Text.Json;

using Xunit;

namespace AAEmu.IntegrationTests.E2e.G2;

/// <summary>
/// G2-A4 autosave ACCEPTANCE probe (pure measurement — verdicts recorded,
/// not asserted): autosave p95 at ~250 ACTIVE characters through the real
/// SaveManager cycle.
///
/// Route: presence-demo citizens (the same real provisioning/roam path as
/// the G2 scaling curve) are booted at AAEMU_ACTIVE count via
/// AAEMU_PRESENCE_BOT_COUNT / AAEMU_PRESENCE_MAX_BOTS (env-tunable clamp).
/// Roaming citizens accumulate dirty character state, so every autosave pass
/// (e2e AutoSaveInterval = 0.2 min = 12 s) persists ~N characters through
/// SaveManager.DoSave → SaveDurationMetrics ring.
///
/// NOTE: dormant-row materialization is NOT usable as the active load source
/// here — PB-004: proximity-materialized bots never re-arm the scheduler, so
/// they stand inert and accumulate no dirty state. Presence citizens step for
/// real (~300 steps/min/bot), which is exactly the "ACTIVE characters" load
/// the A4 gate describes.
///
/// Gate (ROADMAP G2-A4): autosave p95 &lt; 2 s at 250 active characters with
/// ≥30 % headroom (i.e. measured p95 ≤ 1400 ms). Baseline context: M3b
/// measured 1301 ms p95 @ 25 bots.
///
/// Skip observability: SaveManager counts autosave ticks dropped while a
/// previous DoSave was still in flight (_isSaving guard) — surfaced here as
/// save.skipCount. Skips mean saves overran their interval.
///
/// Report: g2-a4-autosave-acceptance-report.json under $E2E_ROOT/logs.
/// </summary>
[Collection("e2e")]
public class A4AcceptanceProbeTests
{
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    private sealed record Sample(DateTime At, double RssMb, double TickP95, double RegionMs,
        long Steps, int Embodied, long SaveSamples, double SaveP50Ms, double SaveP95Ms,
        double SaveMaxMs, long SaveSkips);

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Probe_A4Autosave_Acceptance()
    {
        var activeTarget = int.TryParse(Environment.GetEnvironmentVariable("A4_ACTIVE_COUNT"), out var n) && n > 0 ? n : 250;
        var soakMinutes = int.TryParse(Environment.GetEnvironmentVariable("SCALING_PROBE_MINUTES"), out var m) && m > 0 ? m : 6;
        var runAt = DateTime.UtcNow;

        E2eStack.EnsureUp();

        Console.WriteLine($"[a4] booting {activeTarget} ACTIVE presence citizens for the autosave soak");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_DEMO", "1");
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_BOT_COUNT", activeTarget.ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MAX_BOTS", Math.Max(activeTarget, 10).ToString());
        Environment.SetEnvironmentVariable("AAEMU_PRESENCE_MANIFEST", WriteManifest(activeTarget));
        E2eStack.RestartGameServer();

        // Provisioning N citizens at boot is sequential and slow — allow far
        // more than the classic 300 s poll.
        WaitEmbodied(activeTarget, TimeSpan.FromMinutes(30));

        Console.WriteLine("[a4] all citizens embodied + stepping; settling 120s before the soak");
        await Task.Delay(120_000);

        var samples = new List<Sample>();
        var windowStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - windowStart).TotalMinutes < soakMinutes)
        {
            try
            {
                samples.Add(SampleNow());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[a4] sample lost: {ex.Message}");
            }
            await Task.Delay(15_000);
        }

        var last = samples.Count > 0 ? samples[^1]
            : throw new InvalidOperationException("no successful samples in the soak window");

        // Multiple save cycles must have been observed for the numbers to be
        // a gate measurement at all (12 s interval → expect ~5×soakMinutes).
        var cyclesDuringSoak = last.SaveSamples - samples[0].SaveSamples;
        Assert.True(cyclesDuringSoak >= 3,
            $"only {cyclesDuringSoak} autosave passes during the {soakMinutes}min soak — window too short or autosave disabled");

        // Gate values: the FINAL cumulative ring percentiles dominate in
        // steady state (boot-time passes are a small minority of samples);
        // the worst p95 SEEN during sampling is recorded alongside.
        var p95FinalMs = last.SaveP95Ms;
        var p95WorstSeenMs = samples.Where(s => s.SaveP95Ms >= 0).Select(s => s.SaveP95Ms).DefaultIfEmpty(-1).Max();
        var skipsDuringSoak = last.SaveSkips - samples[0].SaveSkips;
        var headroomPct = p95FinalMs >= 0 ? Math.Round((2000.0 - p95FinalMs) / 2000.0 * 100.0, 1) : -1;
        var passUnder2s = p95FinalMs is >= 0 and < 2000;
        var passWithHeadroom = passUnder2s && headroomPct >= 30.0;

        WriteReport(runAt, activeTarget, soakMinutes, samples, last, cyclesDuringSoak,
            skipsDuringSoak, p95FinalMs, p95WorstSeenMs, headroomPct, passUnder2s, passWithHeadroom);

        Console.WriteLine($"[a4] RESULT: active={last.Embodied} saveCycles={cyclesDuringSoak} " +
                          $"p50={last.SaveP50Ms:F0}ms p95={p95FinalMs:F0}ms max={last.SaveMaxMs:F0}ms " +
                          $"skips={skipsDuringSoak} headroom={headroomPct:F1}% " +
                          $"gate={(passWithHeadroom ? "PASS" : passUnder2s ? "PASS-no-headroom" : "FAIL")}");
    }

    // -------------------------------------------------------------- plumbing

    private static Sample SampleNow()
    {
        using var bridge = new BotDriveClient(E2eStack.BridgePort);
        var m = bridge.Call("{\"cmd\":\"metrics\"}", 15000);

        double TickP() => m.TryGetProperty("tick", out var tk) && tk.ValueKind == JsonValueKind.Object &&
                          tk.TryGetProperty("invokeP95Ms", out var tp) ? tp.GetDouble() : -1;
        double Region() => m.TryGetProperty("regionTick", out var rt) && rt.ValueKind == JsonValueKind.Object
            ? rt.GetProperty("elapsedMs").GetDouble() : -1;
        long Steps() => m.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
                        sc.TryGetProperty("available", out var sa) && sa.GetBoolean() &&
                        sc.TryGetProperty("totalStepsRun", out var sr) ? sr.GetInt64() : -1;
        int Embodied() => m.TryGetProperty("population", out var po) && po.TryGetProperty("embodied", out var em)
            ? em.GetInt32() : 0;

        var save = m.TryGetProperty("save", out var svEl) && svEl.ValueKind == JsonValueKind.Object ? svEl : default;
        double SaveField(string f) => save.ValueKind != default && save.TryGetProperty(f, out var el) ? el.GetDouble() : -1;
        long SaveSamples() => save.ValueKind != default && save.TryGetProperty("sampleCount", out var sc2) ? sc2.GetInt64() : -1;
        long SaveSkips() => save.ValueKind != default && save.TryGetProperty("skipCount", out var sk) ? sk.GetInt64() : -1;

        return new Sample(
            DateTime.UtcNow, ReadRssMb(), TickP(), Region(), Steps(), Embodied(),
            SaveSamples(),
            SaveField("p50Ms"),
            SaveField("p95Ms"),
            SaveField("maxMs"),
            SaveSkips());
    }

    private static void WaitEmbodied(int target, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var lastReported = -1;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var bridge = new BotDriveClient(E2eStack.BridgePort);
                var m = bridge.Call("{\"cmd\":\"metrics\"}", 15000);
                var embodied = m.TryGetProperty("population", out var po) && po.TryGetProperty("embodied", out var em)
                    ? em.GetInt32() : 0;
                var stepping = m.TryGetProperty("scheduler", out var sc) && sc.ValueKind == JsonValueKind.Object &&
                               sc.TryGetProperty("totalStepsRun", out var sr) && sr.GetInt64() > 0;
                if (embodied != lastReported)
                {
                    Console.WriteLine($"[a4] boot poll: embodied={embodied}/{target}");
                    lastReported = embodied;
                }
                if (embodied >= target && stepping)
                    return;
            }
            catch { /* bridge hiccup during boot */ }
            Thread.Sleep(5000);
        }
        throw new TimeoutException($"autosave probe: only reached partial embodiment within {window.TotalMinutes}min");
    }

    // --------------------------------------------------------------- manifest

    private static string WriteManifest(int citizens)
    {
        // Same pinned Nuian template spawn home as the G2 scaling curve —
        // keeps every citizen in one neighborhood so region density scales
        // WITH the active count.
        const float homeX = 15578.042f, homeY = 15382.122f, homeZ = 126.484f;
        string[] races = ["Nuian", "Elf"];
        string[] genders = ["Male", "Female"];

        var entries = new List<object>(citizens);
        for (var i = 0; i < citizens; i++)
        {
            entries.Add(new
            {
                name = $"A4Cit{i + 1:D3}",
                race = races[i % races.Length],
                gender = genders[i % genders.Length],
                level = 5,
                home = new { x = homeX, y = homeY, z = homeZ },
                personality = $"a4-{i + 1:D3}"
            });
        }

        var dir = Path.Combine(E2eStack.E2eRoot, "runtime");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a4-autosave-manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // --------------------------------------------------------------------- io

    private static void WriteReport(DateTime runAt, int activeTarget, int soakMinutes, List<Sample> samples,
        Sample last, long cyclesDuringSoak, long skipsDuringSoak, double p95FinalMs, double p95WorstSeenMs,
        double headroomPct, bool passUnder2s, bool passWithHeadroom)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            probe = "G2-A4 autosave acceptance (pure measurement — verdicts recorded, not asserted)",
            runAtUtc = runAt.ToString("O"),
            commit = "worktree .worktrees/a5-acceptance @ g2/a5-harness-instrumentation (uncommitted A4 additions)",
            config = new
            {
                activeTarget,
                embodiedAtSampleEnd = last.Embodied,
                soakMinutes,
                autosaveIntervalMin = 0.2,
                route = "presence-demo citizens (real provisioning + roam) → SaveManager.DoSave dirty-character cycle",
                note = "PB-004: dormant-materialized bots are inert (no scheduler re-arm), hence unusable as active load"
            },
            results = new
            {
                saveCyclesDuringSoak = cyclesDuringSoak,
                saveSamplesCumulative = last.SaveSamples,
                saveP50Ms = last.SaveP50Ms,
                saveP95MsFinal = p95FinalMs,
                saveP95MsWorstSeen = p95WorstSeenMs,
                saveMaxMs = last.SaveMaxMs,
                saveSkipsDuringSoak = skipsDuringSoak,
                rssMedianMb = Median(samples.Select(s => s.RssMb)),
                rssMaxMb = samples.Where(s => s.RssMb > 0).Select(s => s.RssMb).DefaultIfEmpty(-1).Max(),
                tickP95MaxMs = samples.Where(s => s.TickP95 >= 0).Select(s => s.TickP95).DefaultIfEmpty(-1).Max(),
                regionWorstMs = samples.Where(s => s.RegionMs >= 0).Select(s => s.RegionMs).DefaultIfEmpty(-1).Max(),
                schedulerStepsPerMin = StepsPerMin(samples),
                samplesTaken = samples.Count
            },
            baselineContext = new { source = "M3b gate-scale measurement", bots = 25, autosaveP95Ms = 1301 },
            verdicts = new
            {
                gate = "autosave p95 < 2s at 250 active characters with >=30% headroom",
                p95Under2s = passUnder2s,
                headroomPct,
                passWithHeadroom
            }
        };

        var path = Path.Combine(EvidenceDir, "g2-a4-autosave-acceptance-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[a4] report written: {path}");
    }

    private static double Median(IEnumerable<double> v)
    {
        var list = v.Where(x => x > 0).OrderBy(x => x).ToList();
        return list.Count == 0 ? -1 : Math.Round(list[list.Count / 2], 1);
    }

    private static long StepsPerMin(List<Sample> samples)
    {
        if (samples.Count < 2)
            return -1;
        var first = samples[0].Steps;
        var last = samples[^1].Steps;
        var spanMin = (samples[^1].At - samples[0].At).TotalMinutes;
        return first >= 0 && last >= first && spanMin > 0 ? (long)((last - first) / spanMin) : -1;
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
