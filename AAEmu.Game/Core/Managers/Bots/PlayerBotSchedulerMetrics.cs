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
/// <param name="TotalQuestsAccepted">Cumulative quests ACCEPTED (see <see cref="QuestProgressCounters.Accepted"/>).</param>
/// <param name="TotalQuestTurnInsRequested">Cumulative turn-in requests reaching the report seam (see <see cref="QuestProgressCounters.TurnInsRequested"/>).</param>
/// <param name="TotalQuestsCompleted">Cumulative quests COMPLETED (see <see cref="QuestProgressCounters.Completed"/>).</param>
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
    long TotalResurrections = 0,
    long TotalQuestsAccepted = 0,
    long TotalQuestTurnInsRequested = 0,
    long TotalQuestsCompleted = 0)
{
    /// <summary>Mean wake→start latency in ms (0 when no steps ran).</summary>
    public double AverageWakeLatencyMs => TotalStepsRun == 0 ? 0 : (double)TotalWakeLatencyMs / TotalStepsRun;
}

/// <summary>
/// Engine-side WRITE seam for the quest-lifecycle counters that
/// <see cref="PlayerBotSchedulerMetrics"/> projects. Deliberately a bare
/// counter triple, not a manager: the three quest seams
/// (CharacterQuests.AddQuest / QuestManager.DoReportEvents /
/// Quest.GoToNextStep's Reward branch) already live in engine files with no
/// bot dependency, and resolving a service per quest event would allocate and
/// could CONSTRUCT the scheduler singleton as a side effect. Interlocked on
/// plain statics costs one lock-free add, allocates nothing, and keeps the
/// read path a plain Volatile.Read — exactly the pattern every counter in
/// PlayerBotScheduler already uses.
///
/// LIFECYCLE: process-scoped statics, zero-initialised, never reset. One Game
/// process per E2E lane, so lane counts never cross; a lane restart begins at
/// zero by construction rather than by a reset that could silently understate
/// a run mid-flight. Like PlayerTraceService, this is per-process state.
///
/// COUNTING SEMANTICS (do not assume symmetry):
///   Accepted          — AddQuest returned true (the quest became active).
///   TurnInsRequested  — a turn-in reached the report seam, INCLUDING one the
///                       engine then refuses (quest not ready, bad target). It
///                       is an attempt rate, paired with Completed to expose
///                       "keeps trying to hand in, never finishes".
///   Completed         — the engine set the completed flag and dropped the
///                       quest. A refused turn-in does NOT reach this.
/// </summary>
public static class QuestProgressCounters
{
    private static long _accepted;
    private static long _turnInsRequested;
    private static long _completed;

    /// <summary>Quests the engine accepted.</summary>
    public static long Accepted => Volatile.Read(ref _accepted);

    /// <summary>Turn-in requests that reached the report seam.</summary>
    public static long TurnInsRequested => Volatile.Read(ref _turnInsRequested);

    /// <summary>Quests the engine completed.</summary>
    public static long Completed => Volatile.Read(ref _completed);

    public static void RecordAccepted() => Interlocked.Increment(ref _accepted);
    public static void RecordTurnInRequested() => Interlocked.Increment(ref _turnInsRequested);
    public static void RecordCompleted() => Interlocked.Increment(ref _completed);
}
