namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Immutable metrics snapshot of <see cref="PlayerBotScheduler"/> (slice #6,
/// review deliverable 9 item 4). Counters are cumulative and monotonic;
/// depth/activity values are point-in-time.
/// </summary>
/// <param name="WorkerCount">Configured pool size (clamped 4-8).</param>
/// <param name="ActiveWorkers">Workers currently executing a step.</param>
/// <param name="DueQueueDepth">Entries waiting in the due-time queue.</param>
/// <param name="EventQueueDepth">Unconsumed wake events.</param>
/// <param name="InFlight">Bots leased (queued on the work channel or running).</param>
/// <param name="TotalStepsRun">Cumulative steps executed.</param>
/// <param name="TotalStepsSkipped">Cumulative steps skipped (bot left Active while queued).</param>
/// <param name="TotalStepsFailed">Cumulative steps that threw.</param>
/// <param name="TotalStepsTimedOut">Cumulative steps cancelled by the step timeout.</param>
/// <param name="TotalDuePopped">Cumulative due entries processed by the wake-scan.</param>
/// <param name="LastCycleDue">Due entries popped in the most recent wake-scan cycle.</param>
/// <param name="MaxCycleDue">Largest single-cycle pop count.</param>
/// <param name="TotalWakeLatencyMs">Sum of wake→start latencies (ms) — average = / TotalStepsRun.</param>
/// <param name="MaxWakeLatencyMs">Worst observed wake→start latency (ms).</param>
/// <param name="WorkerUtilization">Pool busy-time ratio (0..1) since Start().</param>
/// <param name="ElapsedMs">Milliseconds since Start().</param>
/// <param name="TotalResurrections">Cumulative M6.2 death-watch resurrections.</param>
public sealed record PlayerBotSchedulerMetrics(
    int WorkerCount,
    int ActiveWorkers,
    int DueQueueDepth,
    int EventQueueDepth,
    int InFlight,
    long TotalStepsRun,
    long TotalStepsSkipped,
    long TotalStepsFailed,
    long TotalStepsTimedOut,
    long TotalDuePopped,
    long LastCycleDue,
    long MaxCycleDue,
    long TotalWakeLatencyMs,
    long MaxWakeLatencyMs,
    double WorkerUtilization,
    long ElapsedMs,
    long TotalResurrections = 0)
{
    /// <summary>Mean wake→start latency in ms (0 when no steps ran).</summary>
    public double AverageWakeLatencyMs => TotalStepsRun == 0 ? 0 : (double)TotalWakeLatencyMs / TotalStepsRun;
}
