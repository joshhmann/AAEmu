namespace AAEmu.Commons.Utils.Gate;

/// <summary>
/// Warmup-blind budget evaluation for soak drivers.
///
/// Forensics fix: post-boot pause storms (GC STW + host deschedule while the
/// world provisions) trip every budget surface at once — region overruns at
/// +3..+17s post-boot, physics slow-thread clusters at +2..+67s — and boots
/// ~140s apart leave zero steady-state seconds in the window. Counting those
/// boot transients against steady-state budgets false-REDs clean soaks, while
/// cumulative tallies hide which seconds were actually evaluated.
///
/// Rule: budget events stamped inside any boot's [boot−30s, boot+120s] window
/// are RECORDED but NOT COUNTED toward verdicts; rates normalize by
/// steady-state (outside-window) minutes only. All timestamps must share one
/// clock (soak runners pass log-anchored local times for both events and
/// boots). Pure math — no game/test dependencies, fully unit-testable.
/// </summary>
public static class SoakWarmup
{
    /// <summary>Seconds BEFORE a boot that still count as warmup (clock skew + pre-boot quiesce).</summary>
    public const double LeadSeconds = 30;

    /// <summary>Seconds AFTER a boot that count as warmup (provisioning/GC pause-storm band).</summary>
    public const double LagSeconds = 120;

    /// <summary>
    /// True when <paramref name="eventTime"/> falls inside any boot's
    /// [boot−<see cref="LeadSeconds"/>, boot+<see cref="LagSeconds"/>] window
    /// (inclusive on both edges).
    /// </summary>
    public static bool IsWarmup(DateTime eventTime, IEnumerable<DateTime> bootTimes)
    {
        foreach (var boot in bootTimes)
        {
            if (eventTime >= boot.AddSeconds(-LeadSeconds) && eventTime <= boot.AddSeconds(LagSeconds))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Steady-state window length in minutes: the [windowStart, windowEnd]
    /// span minus the union of all boot warmup intervals clipped to it.
    /// Returns 0 when the window is empty/inverted or fully covered (e.g.
    /// boots ~140s apart leave zero steady-state seconds — the verdicts then
    /// fall back to the full window rather than dividing by zero; the
    /// caller records the boots so the evidence shows why).
    /// </summary>
    public static double SteadyStateMinutes(DateTime windowStart, DateTime windowEnd, IEnumerable<DateTime> bootTimes)
    {
        if (windowEnd <= windowStart)
            return 0;

        // Collect warmup intervals clipped to the window, then union them.
        var clipped = new List<(DateTime Start, DateTime End)>();
        foreach (var boot in bootTimes)
        {
            var start = boot.AddSeconds(-LeadSeconds);
            var end = boot.AddSeconds(LagSeconds);
            if (end <= windowStart || start >= windowEnd)
                continue;
            clipped.Add((
                start < windowStart ? windowStart : start,
                end > windowEnd ? windowEnd : end));
        }

        if (clipped.Count == 0)
            return (windowEnd - windowStart).TotalMinutes;

        clipped.Sort((a, b) => a.Start.CompareTo(b.Start));

        var warmupTicks = 0L;
        var curStart = clipped[0].Start;
        var curEnd = clipped[0].End;
        for (var i = 1; i < clipped.Count; i++)
        {
            if (clipped[i].Start <= curEnd)
            {
                if (clipped[i].End > curEnd)
                    curEnd = clipped[i].End;
            }
            else
            {
                warmupTicks += (curEnd - curStart).Ticks;
                curStart = clipped[i].Start;
                curEnd = clipped[i].End;
            }
        }

        warmupTicks += (curEnd - curStart).Ticks;

        var steadyTicks = (windowEnd - windowStart).Ticks - warmupTicks;
        return Math.Max(0, new TimeSpan(steadyTicks).TotalMinutes);
    }
}
