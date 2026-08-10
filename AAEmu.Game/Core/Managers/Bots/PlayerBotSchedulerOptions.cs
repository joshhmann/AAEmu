namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Configuration for <see cref="PlayerBotScheduler"/> (slice #6, marshal
/// seam t_0a61eeb1). Step execution is serialized onto the game loop (sync
/// TickManager subscription) or a single fallback marshal thread — there is
/// NO parallel worker pool anymore (the 2026-08-09 Kimi audit: 4-8
/// unsynchronized workers raced the game loop on Transform/Region reads).
/// </summary>
public sealed class PlayerBotSchedulerOptions
{
    /// <summary>
    /// Obsolete — execution is serialized onto the game loop; WorkerCount is
    /// always 1. Kept for source compatibility; ignored by the scheduler.
    /// </summary>
    public int WorkerCount { get; init; } = 4;

    /// <summary>Wake-scan cadence. Default 100 ms.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Fallback marshal-loop cadence, used only when no TickManager is wired
    /// (standalone/tests). Default 20 ms.
    /// </summary>
    public TimeSpan MarshalInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Max steps drained per marshal cycle (per game-loop tick in
    /// production). Bounds the game loop's per-tick bot time so a backlog can
    /// never starve the loop — surplus stays queued for the next tick.
    /// Default 8.
    /// </summary>
    public int MarshalBatchSize { get; init; } = 8;

    /// <summary>Bounded work channel capacity (backpressure for the marshal). Default 256.</summary>
    public int WorkChannelCapacity { get; init; } = 256;

    /// <summary>Per-step timeout; the lease is revoked and the step cancelled when exceeded. Default 30 s. Zero/negative disables.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long StopAsync waits for in-flight steps before giving up. Default 10 s.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
