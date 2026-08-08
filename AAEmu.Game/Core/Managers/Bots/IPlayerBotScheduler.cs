using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Due-time scheduler + bounded worker pool for player bots (slice #6 of the
/// PlayerBot scale review — ARCHITECTURE_REVIEW deliverable 10, spec §4-5,
/// §21-5).
///
/// Shape (spec §4): ONE scheduler owning a <c>PriorityQueue&lt;BotId,
/// NextWakeTime&gt;</c> plus an event queue; only bots whose due time has
/// arrived are processed. Execution (spec §5): a bounded worker pool built
/// on a Channel (4-8 workers, configurable), a per-bot execution lease that
/// guarantees at most one in-flight step per bot, and NO global behavior
/// lock.
///
/// Hard gate (spec §21-5, review deliverable 1-B): no per-bot TickManager
/// subscriptions and no per-bot threads/tasks. The wake-scan is exactly ONE
/// dedicated background loop owned by the scheduler (the review's allowed
/// "dedicated thread" option). Bots never enter AIManager; TaskManager2 is
/// not used as the bot scheduler.
///
/// The scheduler consumes the <see cref="IPlayerBotManager"/> registry: it
/// resolves each bot's runtime at execution time and skips bots that were
/// deactivated while queued.
/// </summary>
public interface IPlayerBotScheduler
{
    /// <summary>Starts the wake-scan loop and the bounded worker pool. Idempotent.</summary>
    void Start();

    /// <summary>
    /// Stops the scheduler: the wake-scan stops accepting new work, queued
    /// steps drain through the workers (graceful), in-flight steps complete.
    /// Idempotent.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Wakes a bot now (event queue). No-op when the scheduler is stopped.
    /// If the bot is already leased (step in flight or queued) the wake is
    /// remembered and honored as soon as the current step completes.
    /// </summary>
    bool Wake(uint characterId);

    /// <summary>Schedules the bot's next wake at an absolute UTC time.</summary>
    bool WakeAt(uint characterId, DateTime utcDue);

    /// <summary>Schedules the bot's next wake relative to now.</summary>
    bool WakeAfter(uint characterId, TimeSpan delay);

    /// <summary>True while the bot has a step leased (queued or running).</summary>
    bool IsLeased(uint characterId);

    /// <summary>Thread-safe metrics snapshot (queue depth, latency, utilization).</summary>
    PlayerBotSchedulerMetrics GetMetrics();

    /// <summary>Configured worker count (clamped to 4-8).</summary>
    int WorkerCount { get; }

    /// <summary>True between Start() and StopAsync().</summary>
    bool IsRunning { get; }
}
