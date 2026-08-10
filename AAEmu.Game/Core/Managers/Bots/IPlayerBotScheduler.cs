using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Due-time scheduler + game-loop marshal for player bots (slice #6 of the
/// PlayerBot scale review — ARCHITECTURE_REVIEW deliverable 10, spec §4-5,
/// §21-5; marshal seam correction t_0a61eeb1 from the 2026-08-09 Kimi
/// audit).
///
/// Shape (spec §4): ONE scheduler owning a <c>PriorityQueue&lt;BotId,
/// NextWakeTime&gt;</c> plus an event queue; only bots whose due time has
/// arrived are processed. Execution: the per-bot execution lease guarantees
/// at most one in-flight step per bot, and a MARSHAL SEAM executes steps on
/// the game loop thread (a sync TickManager subscription) — or a single
/// fallback marshal thread when no TickManager is wired. There is NO
/// parallel worker pool and NO global behavior lock: bot step execution is
/// fully serialized, so bot Transform writes can never race the game loop's
/// Region.GetList / Transform reads (the audit's witness race,
/// Region.cs:401).
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
    /// <summary>Starts the wake-scan loop and the marshal (game-loop subscription or fallback thread). Idempotent.</summary>
    void Start();

    /// <summary>
    /// Stops the scheduler: the wake-scan stops accepting new work, queued
    /// steps drain through the marshal (graceful), in-flight steps complete.
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

    /// <summary>
    /// Execution contexts: always 1 since t_0a61eeb1 — bot steps are
    /// serialized onto the game loop thread (or the single fallback marshal
    /// thread), never a parallel worker pool.
    /// </summary>
    int WorkerCount { get; }

    /// <summary>True between Start() and StopAsync().</summary>
    bool IsRunning { get; }
}
