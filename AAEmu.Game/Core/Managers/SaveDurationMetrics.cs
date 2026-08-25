namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Immutable snapshot of save-duration metrics: how long DoSave passes take.
/// The gate harness reads this through the bridge metrics surface to enforce
/// the M3b autosave budget (p95 &lt; 2s at gate scale — two homesteads + 25
/// bots embodied).
/// </summary>
public sealed class SaveDurationMetricsSnapshot
{
    /// <summary>Number of DoSave passes recorded so far (ring-buffer cap 1024).</summary>
    public long SampleCount { get; init; }

    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double MaxMs { get; init; }

    /// <summary>
    /// Autosave ticks dropped because a DoSave pass was still in flight
    /// (SaveManager._isSaving guard). Cumulative since boot; nonzero means
    /// saves overran their interval (A4 gate observability).
    /// </summary>
    public long SkipCount { get; init; }
}

/// <summary>
/// Thread-safe save-duration metrics: ring buffer of DoSave wall-clock
/// durations. Owned by <see cref="SaveManager"/> under its save lock.
/// </summary>
internal sealed class SaveDurationMetrics
{
    private readonly SampleRing _samples = new();

    public void Record(TimeSpan duration)
    {
        _samples.Add(duration.TotalMilliseconds);
    }

    public SaveDurationMetricsSnapshot Snapshot()
    {
        var (count, p50, p95, max) = _samples.Summarize();
        return new SaveDurationMetricsSnapshot
        {
            SampleCount = count,
            P50Ms = p50,
            P95Ms = p95,
            MaxMs = max
        };
    }
}
