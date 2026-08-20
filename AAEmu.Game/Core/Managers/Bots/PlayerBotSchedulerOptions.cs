namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Configuration for <see cref="PlayerBotScheduler"/> (slice #6). Worker
/// count is clamped to the spec's 4-8 bound (spec §5: "bounded worker pool
/// (4-8, Channel)") regardless of what the caller requests.
/// </summary>
public sealed class PlayerBotSchedulerOptions
{
    /// <summary>Worker pool size. Clamped to [4, 8]. Default 4.</summary>
    public int WorkerCount { get; init; } = 4;

    /// <summary>Wake-scan cadence. Default 100 ms.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Tick-drain cadence (M5 A1 execution boundary): how often the marshal
    /// queue is drained on the game-loop thread. Default 10 ms.
    /// </summary>
    public TimeSpan TickDrainInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// When true (production default), Start() subscribes the marshal drain
    /// to <see cref="TickManager"/> OnTick with useAsync:false so steps run
    /// INLINE on the game-loop thread — the single execution boundary. Tests
    /// set this false and pump <c>DrainTickQueue()</c> manually.
    /// </summary>
    public bool SubscribeToTickManager { get; init; } = true;

    /// <summary>Bounded work channel capacity (backpressure for the pool). Default 256.</summary>
    public int WorkChannelCapacity { get; init; } = 256;

    /// <summary>Per-step timeout; the lease is revoked and the step cancelled when exceeded. Default 30 s. Zero/negative disables.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long StopAsync waits for in-flight steps before giving up. Default 10 s.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// M6.2 death watch: when true (default), a bot whose character is dead
    /// (Hp &lt;= 0) gets no work steps — the scheduler polls the corpse and
    /// resurrects it through <see cref="AAEmu.Game.Models.Game.Char.CharacterResurrection"/>
    /// once <see cref="ResurrectDelay"/> has elapsed, then normal stepping
    /// resumes (the step executor re-engages its route/behavior from the
    /// new position).
    /// </summary>
    public bool ResurrectionEnabled { get; init; } = true;

    /// <summary>How long a bot stays dead before the death watch resurrects it. Default 5 s.</summary>
    public TimeSpan ResurrectDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Death-watch poll cadence (next-wake delay while a bot is dead). Default 1 s.</summary>
    public TimeSpan DeathPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Portal lookup override for rigs (default: PortalManager's closest
    /// return portal). Rigs return a portal with X == 0 to exercise the
    /// resurrection without the server-side relocation move (the same
    /// condition the packet path uses to choose its broadcast).
    /// </summary>
    public Func<AAEmu.Game.Models.Game.Char.Character, AAEmu.Game.Models.Game.Portal>? PortalResolver { get; init; }
}
