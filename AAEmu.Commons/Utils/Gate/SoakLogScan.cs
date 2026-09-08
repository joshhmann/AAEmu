using System.Text.RegularExpressions;

namespace AAEmu.Commons.Utils.Gate;

/// <summary>
/// Window tallies from one soak-window game-log delta. Counts cover the soak
/// window ONLY (the caller passes post-offset lines — never cumulative logs),
/// and warmup-blind lines are recorded separately, never counted.
/// </summary>
public sealed record SoakLogTally(
    long PhysicsWarnings,
    long TickOverrunWarnings,
    long RegionOverruns,
    long MaxSameWorldPhysicsWarningsPer60s,
    double RegionTickWorstMs,
    long WarmupExcludedPhysics,
    long WarmupExcludedOverruns);

/// <summary>
/// Pure game-log scan for soak budget drivers (forensics fix: cumulative-log
/// counting polluted tallies, and point-in-time sampler maxima missed
/// between-sample over-budget passes).
///
/// Covers the three budget log surfaces:
///   - "Physics thread is running slow in {world} at …" (per-world 60s
///     sliding maxima for the no-sustained-slow clause),
///   - "Tick took …ms to finish" + "ActiveRegionTick took …ms (over …ms
///     budget)" (tick-overrun rate),
///   - "ActiveRegionTick took {N} ms" worst-ms capture: every over-budget
///     pass logs, so the log max is the honest per-cycle worst — the 30s
///     sampler can only observe a subset.
///
/// Log lines carry HH:mm:ss only (NLog short-time layout, server-local
/// clock); <see cref="Scan"/> anchors them to absolute local times from the
/// runner's window-start wall clock (same host, same clock), applies the
/// <see cref="SoakWarmup"/> blind windows, and groups physics lines into
/// per-world 60s windows. File IO stays with the callers (see
/// <see cref="ReadWindowLines"/>); all math here is headless-testable.
/// </summary>
public static partial class SoakLogScan
{
    [GeneratedRegex(@"^(\d{2}):(\d{2}):(\d{2}) .*?in (.+?) at ", RegexOptions.Compiled)]
    private static partial Regex PhysicsWarningRegex();

    [GeneratedRegex(@"^(\d{2}):(\d{2}):(\d{2})", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"ActiveRegionTick took (\d+) ms", RegexOptions.Compiled)]
    private static partial Regex RegionElapsedRegex();

    /// <summary>True for TickManager "Tick took" lines and ActiveRegionTick over-budget lines.</summary>
    public static bool IsTickOverrunLine(string line)
        => line.Contains("Tick took ", StringComparison.Ordinal)
            || line.Contains("over 100ms budget", StringComparison.Ordinal)
            || line.Contains("ActiveRegionTick took", StringComparison.Ordinal);

    public static bool IsPhysicsWarningLine(string line)
        => line.Contains("Physics thread is running slow", StringComparison.Ordinal);

    /// <summary>Parses the per-pass elapsed ms from an "ActiveRegionTick took {N} ms" line.</summary>
    public static bool TryParseRegionTickElapsedMs(string line, out double elapsedMs)
    {
        var m = RegionElapsedRegex().Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, out var ms))
        {
            elapsedMs = ms;
            return true;
        }

        elapsedMs = -1;
        return false;
    }

    /// <summary>
    /// Sliding 60s maximum over timestamped per-world events: the largest
    /// count of events on ONE world within any 60s span. Pure math over
    /// absolute times — no log format involved.
    /// </summary>
    public static long MaxSameWorldPer60s(IEnumerable<(DateTime At, string World)> events)
    {
        long max = 0;
        foreach (var group in events.GroupBy(e => e.World, StringComparer.Ordinal))
        {
            var times = group.Select(e => e.At).OrderBy(t => t).ToList();
            var head = 0;
            for (var tail = 0; tail < times.Count; tail++)
            {
                while (times[tail] - times[head] > TimeSpan.FromSeconds(60))
                    head++;
                max = Math.Max(max, tail - head + 1);
            }
        }

        return max;
    }

    /// <summary>
    /// Reads the window delta lines from each (path, startOffset) source in
    /// order. Callers pass game.log first, then game-restart.log: a restart
    /// mid-window truncates and switches the active file, so the
    /// concatenation stays chronological. Missing/short files yield nothing.
    /// </summary>
    public static List<string> ReadWindowLines(IReadOnlyList<(string Path, long StartOffset)> sources)
    {
        var lines = new List<string>();
        foreach (var (path, startOffset) in sources)
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= startOffset)
                    continue;
                fs.Seek(startOffset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                while (reader.ReadLine() is { } line)
                    lines.Add(line);
            }
            catch (IOException)
            {
                // Log rotation underfoot — the next window re-baselines.
            }
        }

        return lines;
    }

    /// <summary>
    /// Scans window-delta lines anchored at <paramref name="windowStartLocal"/>
    /// (runner-local wall clock when the start offsets were taken; same host
    /// and clock as the server log). Lines inside any boot's warmup window
    /// are recorded in the excluded counters, never in the verdict counts.
    /// </summary>
    public static SoakLogTally Scan(
        IEnumerable<string> lines,
        DateTime windowStartLocal,
        IReadOnlyList<DateTime> bootLocalTimes)
    {
        long physics = 0, overruns = 0, regionOverruns = 0;
        long excludedPhysics = 0, excludedOverruns = 0;
        var worstMs = -1d;
        var physicsEvents = new List<(DateTime At, string World)>();

        // HH:mm:ss has no date: anchor to the date nearest the window start
        // (±12h — soak windows run minutes to hours), then carry a day offset
        // forward across midnight wraps.
        var baseDate = windowStartLocal.Date;
        var dayOffset = TimeSpan.Zero;
        DateTime? lastAbsolute = null;

        foreach (var line in lines)
        {
            var isPhysics = IsPhysicsWarningLine(line);
            var isOverrun = IsTickOverrunLine(line);
            if (!isPhysics && !isOverrun)
                continue;

            var isRegion = isOverrun && line.Contains("ActiveRegionTick took", StringComparison.Ordinal);

            // Lines without a parseable stamp cannot be attributed to a boot:
            // count them (fail-safe) rather than silently dropping budget
            // signal.
            DateTime? at = null;
            string world = "";
            if (TryParseTimestamp(line, out var tod))
            {
                var candidate = baseDate + dayOffset + tod;
                if (lastAbsolute is null)
                {
                    if (candidate < windowStartLocal - TimeSpan.FromHours(12))
                        candidate += TimeSpan.FromDays(1);
                    else if (candidate > windowStartLocal + TimeSpan.FromHours(12))
                        candidate -= TimeSpan.FromDays(1);
                    dayOffset = candidate.Date - baseDate;
                }
                else
                {
                    while (candidate < lastAbsolute - TimeSpan.FromHours(12))
                    {
                        candidate += TimeSpan.FromDays(1);
                        dayOffset += TimeSpan.FromDays(1);
                    }
                }

                at = candidate;
                lastAbsolute = candidate;

                if (isPhysics)
                    world = ParsePhysicsWorld(line);
            }
            var warmup = at.HasValue && SoakWarmup.IsWarmup(at.Value, bootLocalTimes);
            if (warmup)
            {
                if (isPhysics)
                    excludedPhysics++;
                if (isOverrun)
                    excludedOverruns++;
                continue;
            }

            if (isPhysics)
            {
                physics++;
                // Lines whose world cannot be parsed count toward the rate but
                // stay out of the per-world grouping (legacy scan semantics).
                if (world.Length > 0)
                    physicsEvents.Add((at ?? windowStartLocal, world));
            }

            if (isOverrun)
            {
                overruns++;
                if (isRegion)
                {
                    regionOverruns++;
                    if (TryParseRegionTickElapsedMs(line, out var ms))
                        worstMs = Math.Max(worstMs, ms);
                }
            }
        }

        return new SoakLogTally(
            physics,
            overruns,
            regionOverruns,
            MaxSameWorldPer60s(physicsEvents),
            worstMs,
            excludedPhysics,
            excludedOverruns);
    }

    private static bool TryParseTimestamp(string line, out TimeSpan timeOfDay)
    {
        var m = TimestampRegex().Match(line);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out var h)
            && int.TryParse(m.Groups[2].Value, out var min)
            && int.TryParse(m.Groups[3].Value, out var s))
        {
            timeOfDay = new TimeSpan(h, min, s);
            return true;
        }

        timeOfDay = TimeSpan.Zero;
        return false;
    }

    private static string ParsePhysicsWorld(string line)
    {
        var m = PhysicsWarningRegex().Match(line);
        return m.Success ? m.Groups[4].Value : "";
    }
}
