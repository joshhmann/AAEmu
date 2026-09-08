using AAEmu.Commons.Utils.Gate;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Sliding-max / worst-ms / warmup-exclusion unit tests for the shared soak
/// log scan: window deltas only (never cumulative logs), per-world 60s
/// grouping, per-pass worst-ms from over-budget lines. Pure string/time
/// math — no live stack needed.
/// </summary>
public class SoakLogScanTests
{
    private static readonly DateTime WindowStart = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Local);

    private static string PhysicsLine(int second, string world = "main_world")
        => $"12:00:{second:D2} [Warn] PhysicsManager - Physics thread is running slow in {world} at 82.3 / 40.0 ms";

    private static string RegionLine(int second, int ms)
        => $"12:00:{second:D2} [Warn] WorldManager - ActiveRegionTick took {ms} ms (over 100ms budget); deferred 0 characters";

    private static string TickLine(int second, int ms)
        => $"12:00:{second:D2} [Warn] TickManager - Tick took {ms}ms to finish";

    [Test]
    public async Task Scan_NoBoot_AllBudgetLinesCounted()
    {
        var lines = new[]
        {
            PhysicsLine(5),
            PhysicsLine(9, "other_world"),
            TickLine(11, 140),
            RegionLine(13, 105),
            "12:00:15 [Info] WorldManager - unrelated line",
        };

        var tally = SoakLogScan.Scan(lines, WindowStart, []);

        await Assert.That(tally.PhysicsWarnings).IsEqualTo(2);
        await Assert.That(tally.TickOverrunWarnings).IsEqualTo(2);
        await Assert.That(tally.RegionOverruns).IsEqualTo(1);
        await Assert.That(tally.RegionTickWorstMs).IsEqualTo(105);
        await Assert.That(tally.MaxSameWorldPhysicsWarningsPer60s).IsEqualTo(1);
        await Assert.That(tally.WarmupExcludedPhysics).IsEqualTo(0);
        await Assert.That(tally.WarmupExcludedOverruns).IsEqualTo(0);
    }

    [Test]
    public async Task Scan_BootStormInsideBlind_RecordedNotCounted()
    {
        // Forensics bands: region overrun +17s, physics cluster +67s — blind.
        var boot = WindowStart;
        var lines = new[]
        {
            RegionLine(17, 105),
            PhysicsLine(30),
            PhysicsLine(67),
            TickLine(90, 140),
        };

        var tally = SoakLogScan.Scan(lines, WindowStart, [boot]);

        await Assert.That(tally.PhysicsWarnings).IsEqualTo(0);
        await Assert.That(tally.TickOverrunWarnings).IsEqualTo(0);
        await Assert.That(tally.RegionOverruns).IsEqualTo(0);
        await Assert.That(tally.RegionTickWorstMs).IsEqualTo(-1);
        await Assert.That(tally.MaxSameWorldPhysicsWarningsPer60s).IsEqualTo(0);
        await Assert.That(tally.WarmupExcludedPhysics).IsEqualTo(2);
        await Assert.That(tally.WarmupExcludedOverruns).IsEqualTo(2);
    }

    [Test]
    public async Task Scan_LineJustOutsideBlind_Counted()
    {
        var boot = WindowStart;
        var lines = new[]
        {
            PhysicsLine(30),
            "12:02:01 [Warn] PhysicsManager - Physics thread is running slow in main_world at 70.1 / 40.0 ms",
        };

        var tally = SoakLogScan.Scan(lines, WindowStart, [boot]);

        await Assert.That(tally.PhysicsWarnings).IsEqualTo(1);
        await Assert.That(tally.WarmupExcludedPhysics).IsEqualTo(1);
    }

    [Test]
    public async Task Scan_SameWorldCluster_SlidingMaxPerWorld()
    {
        // 3 warnings on main_world within 60s + 2 on another world: the
        // no-sustained-slow clause reports the per-world max (3).
        var lines = new[]
        {
            PhysicsLine(5),
            PhysicsLine(20),
            PhysicsLine(50),
            PhysicsLine(6, "other_world"),
            PhysicsLine(59, "other_world"),
            "12:05:00 [Warn] PhysicsManager - Physics thread is running slow in main_world at 70.1 / 40.0 ms",
        };

        var tally = SoakLogScan.Scan(lines, WindowStart, []);

        await Assert.That(tally.PhysicsWarnings).IsEqualTo(6);
        await Assert.That(tally.MaxSameWorldPhysicsWarningsPer60s).IsEqualTo(3);
    }

    [Test]
    public async Task Scan_LinesAcrossMidnight_GroupedByAbsoluteTime()
    {
        // A 6h soak can cross midnight: HH:mm:ss-only stamps must anchor
        // across the wrap, or two warnings 7s apart read as 24h apart.
        var start = new DateTime(2026, 9, 8, 23, 59, 0, DateTimeKind.Local);
        var lines = new[]
        {
            "23:59:58 [Warn] PhysicsManager - Physics thread is running slow in main_world at 82.3 / 40.0 ms",
            "00:00:05 [Warn] PhysicsManager - Physics thread is running slow in main_world at 82.3 / 40.0 ms",
        };

        var tally = SoakLogScan.Scan(lines, start, []);

        await Assert.That(tally.PhysicsWarnings).IsEqualTo(2);
        await Assert.That(tally.MaxSameWorldPhysicsWarningsPer60s).IsEqualTo(2);
    }

    [Test]
    public async Task Scan_UnstampedMatch_CountsFailSafe()
    {
        // A budget line without a parseable stamp cannot be attributed to a
        // boot: counted, never silently dropped.
        var tally = SoakLogScan.Scan(["Tick took 140ms to finish"], WindowStart, [WindowStart]);

        await Assert.That(tally.TickOverrunWarnings).IsEqualTo(1);
        await Assert.That(tally.WarmupExcludedOverruns).IsEqualTo(0);
    }

    [Test]
    public async Task MaxSameWorldPer60s_DirectEvents_ComputesSlidingMax()
    {
        var baseTime = WindowStart;
        var events = new List<(DateTime At, string World)>
        {
            (baseTime, "a"), (baseTime.AddSeconds(30), "a"), (baseTime.AddSeconds(61), "a"),
            (baseTime, "b"),
        };

        // World a: [0,30] and [30,61] both span ≤60s → max 2… plus the
        // triple [0,61] spans 61s → still 2.
        await Assert.That(SoakLogScan.MaxSameWorldPer60s(events)).IsEqualTo(2);
    }

    [Test]
    public async Task TryParseRegionTickElapsedMs_OverBudgetLine_ParsesMs()
    {
        await Assert.That(SoakLogScan.TryParseRegionTickElapsedMs(RegionLine(13, 105), out var ms)).IsTrue();
        await Assert.That(ms).IsEqualTo(105);
    }

    [Test]
    public async Task TryParseRegionTickElapsedMs_NonRegionLine_ReturnsFalse()
    {
        await Assert.That(SoakLogScan.TryParseRegionTickElapsedMs(TickLine(11, 140), out var ms)).IsFalse();
        await Assert.That(ms).IsEqualTo(-1);
    }
}
