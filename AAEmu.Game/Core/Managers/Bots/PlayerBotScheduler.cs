using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Due-time scheduler + bounded worker pool for player bots (slice #6).
/// See <see cref="IPlayerBotScheduler"/> for the contract.
///
/// Concurrency model (spec §4-5): one <see cref="PriorityQueue{TElement,TPriority}"/>
/// of (BotId, NextWakeTime) plus an event queue, both guarded by a single
/// small queue lock; a per-bot execution lease (<see cref="_leases"/>) that
/// guarantees at most one in-flight step per bot; a bounded Channel of steps
/// consumed by a fixed pool of workers. There is NO global behavior lock and
/// no per-bot thread or TickManager subscription — the wake-scan is exactly
/// one dedicated background loop (<see cref="ScanLoopAsync"/>), the review's
/// allowed "dedicated thread" option.
///
/// Execution boundary (M5 A1): worker threads are PURE WAKE PRODUCERS — they
/// pop due steps and marshal them into the execution-boundary queue
/// (<see cref="_tickQueue"/>). Steps execute ONLY on the single execution
/// boundary: the game-loop thread (TickManager OnTick, useAsync: false →
/// <see cref="DrainTickQueue"/>), never on a worker thread. ZERO
/// Character/world mutation happens off that boundary; the
/// <see cref="ExecutionBoundary"/> debug assertion proves the rule.
///
/// Step scheduling: a step returns its next wake delay (or null = dormant).
/// A wake that arrives while a bot is leased is folded into a per-bot
/// pending-wake and honored as soon as the current step completes, so no
/// wake is ever lost. Bots that leave the Active set while queued are
/// skipped at execution time (registry consumption).
/// </summary>
public sealed class PlayerBotScheduler : IPlayerBotScheduler
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IPlayerBotManager _manager;
    private readonly IBotStepExecutor _executor;
    private readonly PlayerBotSchedulerOptions _options;
    private readonly TimeProvider _timeProvider;

    // Due-time queue + event queue + dedup/pending bookkeeping (queue lock only).
    private readonly object _queueLock = new();
    private readonly PriorityQueue<uint, DateTime> _due = new();
    private readonly ConcurrentQueue<uint> _events = new();
    private readonly Dictionary<uint, DateTime> _scheduled = [];     // botId → next due (dedup)
    private readonly Dictionary<uint, DateTime> _pendingWake = [];   // botId → due while leased

    // Per-bot execution lease: TryAdd on enqueue, TryRemove on completion.
    private readonly ConcurrentDictionary<uint, byte> _leases = new();

    // M5 A1 execution boundary: steps handed by the worker pool (wake
    // producers) into this marshal queue, drained on the single execution
    // boundary — the game-loop thread (TickManager OnTick, useAsync: false).
    private readonly ConcurrentQueue<BotStep> _tickQueue = new();

    // Bounded work channel consumed by the worker pool (wake marshalling only).
    private Channel<BotStep> _work = null!;
    private Task[] _workers = [];
    private Task _scanTask = Task.CompletedTask;
    private CancellationTokenSource _scanCts = new();

    private int _started;
    private int _stopped;
    private long _startedAtTicks;

    // Metrics (Interlocked; Volatile.Read for snapshots).
    private int _activeWorkers;
    private long _totalStepsRun;
    private long _totalStepsSkipped;
    private long _totalStepsFailed;
    private long _totalStepsTimedOut;
    private long _totalDuePopped;
    private long _lastCycleDue;
    private long _maxCycleDue;
    private long _totalWakeLatencyMs;
    private long _maxWakeLatencyMs;
    private long _busyTicks;
    private long _totalResurrections;

    // M6.2 death watch: per-bot first-seen-dead timestamp (UTC). An entry is
    // added when a dead bot's step is skipped and removed on resurrection or
    // when the bot is seen alive again.
    private readonly ConcurrentDictionary<uint, DateTime> _deadSince = new();

    public PlayerBotScheduler(
        IPlayerBotManager manager,
        IBotStepExecutor executor,
        PlayerBotSchedulerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? new PlayerBotSchedulerOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Spec §5: bounded worker pool, 4-8.
        WorkerCount = Math.Clamp(_options.WorkerCount, 4, 8);
    }

    /// <inheritdoc />
    public int WorkerCount { get; }

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _stopped) == 0;

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _scanCts = new CancellationTokenSource();
        _work = Channel.CreateBounded<BotStep>(new BoundedChannelOptions(_options.WorkChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        _startedAtTicks = Stopwatch.GetTimestamp();

        // Exactly ONE wake-scan loop (spec §21-5: never per-bot subscriptions).
        _scanTask = Task.Run(() => ScanLoopAsync(_scanCts.Token), CancellationToken.None);

        // Bounded pool: WorkerCount tasks, each draining the work channel and
        // MARSHALLING steps to the execution-boundary queue (M5 A1). Workers
        // never execute steps and never touch Character/world state.
        _workers = new Task[WorkerCount];
        for (var i = 0; i < WorkerCount; i++)
        {
            _workers[i] = Task.Run(() => WorkerLoopAsync(), CancellationToken.None);
        }

        // M5 A1: subscribe the marshal drain INLINE (useAsync: false) so bot
        // steps run on the game-loop thread — the single execution boundary.
        // The rig sets SubscribeToTickManager=false and pumps the drain
        // manually on its simulated boundary thread.
        if (_options.SubscribeToTickManager)
            TickManager.Instance.OnTick.Subscribe(TickDrain, _options.TickDrainInterval, useAsync: false, name: "PlayerBotScheduler.TickDrain");

        Logger.Debug("PlayerBotScheduler started: {WorkerCount} workers, scan {ScanIntervalMs}ms, tick drain {TickDrainMs}ms",
            WorkerCount, _options.ScanInterval.TotalMilliseconds, _options.TickDrainInterval.TotalMilliseconds);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0)
            return; // never started — nothing to stop
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        // Unsubscribe the marshal drain: after this, no bot step can execute
        // anywhere (the boundary is the only execution path).
        if (_options.SubscribeToTickManager)
            TickManager.Instance.OnTick.UnSubscribe(TickDrain);

        // Stop the wake-scan; workers finish marshalling the remaining queued
        // steps into the tick queue. Steps that were never drained are
        // DROPPED — the execution boundary (game loop) is gone at shutdown,
        // and executing them off-boundary would violate the M5 contract
        // (nothing may mutate Character/world off the single boundary).
        _scanCts.Cancel();
        _work.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_workers).WaitAsync(_options.ShutdownTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            Logger.Warn("PlayerBotScheduler shutdown timed out after {Timeout}s — {Running} workers still busy",
                _options.ShutdownTimeout.TotalSeconds, Volatile.Read(ref _activeWorkers));
        }

        await _scanTask.WaitAsync(cancellationToken);
        Logger.Debug("PlayerBotScheduler stopped (steps run: {StepsRun})", Volatile.Read(ref _totalStepsRun));
    }

    /// <summary>UTC now from the injectable clock (TimeProvider returns DateTimeOffset).</summary>
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public bool Wake(uint characterId)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return false;
        _events.Enqueue(characterId);
        return true;
    }

    /// <inheritdoc />
    public bool WakeAt(uint characterId, DateTime utcDue)
        => Schedule(characterId, utcDue);

    /// <inheritdoc />
    public bool WakeAfter(uint characterId, TimeSpan delay)
        => Schedule(characterId, UtcNow + delay);

    /// <inheritdoc />
    public bool IsLeased(uint characterId) => _leases.ContainsKey(characterId);

    /// <inheritdoc />
    public PlayerBotSchedulerMetrics GetMetrics()
    {
        lock (_queueLock)
        {
            var elapsed = _startedAtTicks == 0
                ? 0d
                : (double)(Stopwatch.GetTimestamp() - _startedAtTicks) / Stopwatch.Frequency * 1000d;
            var utilization = _startedAtTicks == 0 || elapsed <= 0
                ? 0d
                : Math.Clamp((double)Volatile.Read(ref _busyTicks) / (Stopwatch.GetTimestamp() - _startedAtTicks) / WorkerCount, 0d, 1d);

            return new PlayerBotSchedulerMetrics(
                WorkerCount: WorkerCount,
                ActiveWorkers: Volatile.Read(ref _activeWorkers),
                DueQueueDepth: _due.Count,
                EventQueueDepth: _events.Count,
                InFlight: _leases.Count,
                TotalStepsRun: Volatile.Read(ref _totalStepsRun),
                TotalStepsSkipped: Volatile.Read(ref _totalStepsSkipped),
                TotalStepsFailed: Volatile.Read(ref _totalStepsFailed),
                TotalStepsTimedOut: Volatile.Read(ref _totalStepsTimedOut),
                TotalDuePopped: Volatile.Read(ref _totalDuePopped),
                LastCycleDue: Volatile.Read(ref _lastCycleDue),
                MaxCycleDue: Volatile.Read(ref _maxCycleDue),
                TotalWakeLatencyMs: Volatile.Read(ref _totalWakeLatencyMs),
                MaxWakeLatencyMs: Volatile.Read(ref _maxWakeLatencyMs),
                WorkerUtilization: utilization,
                ElapsedMs: (long)elapsed,
                TotalResurrections: Volatile.Read(ref _totalResurrections));
        }
    }

    /// <summary>
    /// One wake-scan cycle: drain the event queue, then pop every due bot and
    /// hand it to the worker pool. Internal so the rig can drive cycles
    /// deterministically (the loop calls it on the scan interval).
    /// </summary>
    internal void RunScanCycle()
    {
        var now = UtcNow;

        // Event queue → due entries (events are wake-now signals).
        while (_events.TryDequeue(out var botId))
            Schedule(botId, now);

        // Pop everything due; lease each; hand to the pool outside the lock.
        var batch = new List<(uint BotId, DateTime Due)>();
        lock (_queueLock)
        {
            while (_due.TryPeek(out var botId, out var due) && due <= now)
            {
                _due.Dequeue();

                // Stale entry (superseded by an earlier schedule for this bot)?
                if (!_scheduled.TryGetValue(botId, out var scheduled) || scheduled != due)
                    continue;

                // Already running/queued? Fold into pending wake — never drop a wake.
                if (_leases.ContainsKey(botId))
                {
                    _scheduled.Remove(botId);
                    FoldPendingWakeLocked(botId, due);
                    continue;
                }

                _scheduled.Remove(botId);
                _leases.TryAdd(botId, 0);
                batch.Add((botId, due));
            }

            Interlocked.Add(ref _totalDuePopped, batch.Count);
            Interlocked.Exchange(ref _lastCycleDue, batch.Count);
            UpdateMax(ref _maxCycleDue, batch.Count);
        }

        foreach (var (botId, due) in batch)
        {
            if (!_work.Writer.TryWrite(new BotStep(botId, due)))
            {
                // Channel saturated → release the lease and re-schedule for the
                // next cycle (bounded backpressure; no lost wake).
                _leases.TryRemove(botId, out _);
                lock (_queueLock)
                {
                    _scheduled[botId] = now;
                    _due.Enqueue(botId, now);
                }
            }
        }
    }

    private async Task ScanLoopAsync(CancellationToken cancellationToken)
    {
        // Delay first: Start() returns before the first cycle, and the rig
        // (1h fake interval, manual pumps) stays fully deterministic — the
        // loop's first cycle can never race a test's explicit pump.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.ScanInterval, _timeProvider, cancellationToken);
                RunScanCycle();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayerBotScheduler wake-scan cycle failed");
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        // M5 A1: wake producer — workers ONLY marshal steps from the bounded
        // channel into the execution-boundary queue. They never run steps and
        // never mutate Character/world state; execution happens exclusively on
        // the game-loop thread via DrainTickQueue.
        await foreach (var step in _work.Reader.ReadAllAsync(CancellationToken.None))
        {
            _tickQueue.Enqueue(step);
        }
    }

    /// <summary>
    /// Executes one bot step ON the single execution boundary (the game-loop
    /// thread). Called only by <see cref="DrainTickQueue"/>. The execution is
    /// synchronous (GetResult) so the whole step — executor call, lease
    /// release, next-wake scheduling — stays on the boundary thread; an async
    /// hop could resume the continuation on a thread-pool thread, which would
    /// violate the M5 contract.
    /// </summary>
    private void ExecuteStepOnExecutionBoundary(BotStep step)
    {
        // M5 A1 (execution boundary): every bot step must run on the single
        // execution boundary (the game-loop thread). This assertion fires
        // whenever a step executes off the boundary — it is the ROADMAP §M5
        // thread-affinity proof. It lives here, at the one place every step
        // goes through, so both the old worker path and the marshal drain
        // are covered.
        ExecutionBoundary.AssertOnExecutionThread("bot step execution");

        Interlocked.Increment(ref _activeWorkers);
        var sw = Stopwatch.StartNew();
        try
        {
            // Registry consumption: resolve the runtime now; a bot that left the
            // Active set while queued is skipped, never stepped.
            if (!_manager.TryGet(step.BotId, out var runtime) || runtime!.State != PlayerBotState.Active)
            {
                Interlocked.Increment(ref _totalStepsSkipped);
                ReleaseLease(step.BotId);
                return;
            }

            // wake → start latency (due time to actual step start).
            var now = UtcNow;
            var latencyMs = Math.Max(0, (long)(now - step.DueUtc).TotalMilliseconds);
            Interlocked.Add(ref _totalWakeLatencyMs, latencyMs);
            UpdateMax(ref _maxWakeLatencyMs, latencyMs);

            TimeSpan? nextDelay = null;
            var ok = true;
            ExecutionBoundary.EnterBotStep();
            try
            {
                if (_options.ResurrectionEnabled && runtime.Character.IsDead)
                {
                    // M6.2 death watch: a dead bot gets no work steps — the
                    // scheduler polls the corpse and resurrects it once the
                    // delay elapses, then normal stepping resumes (the step
                    // executor re-engages its route/behavior from the new
                    // position). All of this stays on the boundary thread.
                    HandleDeathWatch(step.BotId, runtime.Character);
                    nextDelay = _options.DeathPollInterval;
                }
                else
                {
                    _deadSince.TryRemove(step.BotId, out _);
                    if (_options.StepTimeout > TimeSpan.Zero)
                    {
                        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                        stepCts.CancelAfter(_options.StepTimeout);
                        // The executors are synchronous-bodied; GetResult returns
                        // immediately. A genuinely hung async executor blocks the
                        // tick thread until the timeout — a loud, deliberate
                        // failure mode (a bot step must never run off-thread).
                        nextDelay = _executor.StepAsync(runtime, stepCts.Token)
                            .WaitAsync(stepCts.Token).GetAwaiter().GetResult();
                    }
                    else
                    {
                        nextDelay = _executor.StepAsync(runtime, CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                ok = false;
                Interlocked.Increment(ref _totalStepsTimedOut);
                Logger.Warn("PlayerBot step timed out after {Timeout}s: character {CharacterId}",
                    _options.StepTimeout.TotalSeconds, step.BotId);
            }
            catch (Exception ex)
            {
                ok = false;
                Interlocked.Increment(ref _totalStepsFailed);
                Logger.Error(ex, "PlayerBot step failed: character {CharacterId}", step.BotId);
            }
            finally
            {
                ExecutionBoundary.ExitBotStep();
            }

            if (ok)
                Interlocked.Increment(ref _totalStepsRun);

            // Lease release + next scheduling decision, atomic with pending wake.
            // External wakes are always honored; step-driven cadence only after success.
            DateTime? pending = null;
            lock (_queueLock)
            {
                _leases.TryRemove(step.BotId, out _);
                if (_pendingWake.TryGetValue(step.BotId, out var wake))
                {
                    _pendingWake.Remove(step.BotId);
                    pending = wake;
                }
            }

            if (pending is { } pendingDue)
            {
                var target = pendingDue <= UtcNow ? UtcNow : pendingDue;
                Schedule(step.BotId, target);
            }
            else if (ok && nextDelay is { } delay)
            {
                var clamped = delay > TimeSpan.Zero ? delay : _options.ScanInterval;
                Schedule(step.BotId, UtcNow + clamped);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
            Interlocked.Add(ref _busyTicks, sw.ElapsedTicks);
        }
    }

    /// <summary>
    /// M6.2 death watch (death/resurrection — the 6.2 safety item that did
    /// not exist): polls a dead bot's corpse and, once
    /// <see cref="PlayerBotSchedulerOptions.ResurrectDelay"/> has elapsed,
    /// resurrects it through <see cref="CharacterResurrection"/> — the SAME
    /// engine path the CSResurrectCharacterPacket handler uses (portal
    /// selection, 10% HP/MP, revival debuffs, broadcasts). Headless bots
    /// have no client to re-enter at the portal, so the watch then performs
    /// the server-side relocation itself through the real region-aware
    /// Character.SetPosition move (gated on the same portal.X != 0 condition
    /// the packet uses for its broadcast).
    /// </summary>
    private void HandleDeathWatch(uint botId, Character character)
    {
        var now = UtcNow;
        var since = _deadSince.GetOrAdd(botId, now);
        if (now - since < _options.ResurrectDelay)
            return;

        var portal = CharacterResurrection.Resurrect(character, inPlace: false, _options.PortalResolver);
        _deadSince.TryRemove(botId, out _);
        Interlocked.Increment(ref _totalResurrections);

        if (portal is { X: not 0 })
        {
            character.SetPosition(portal.X, portal.Y, portal.Z, 0, 0, 0);
            Logger.Info("PlayerBotScheduler: resurrected dead bot {CharacterId} at return portal ({X:0.##},{Y:0.##},{Z:0.##}) — M6.2 death watch",
                botId, portal.X, portal.Y, portal.Z);
        }
        else
        {
            Logger.Info("PlayerBotScheduler: resurrected dead bot {CharacterId} in place (no return portal resolved) — M6.2 death watch",
                botId);
        }
    }

    private bool Schedule(uint characterId, DateTime utcDue)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return false;

        lock (_queueLock)
        {
            if (_leases.ContainsKey(characterId))
            {
                // Bot already in flight — remember the wake, honor it on completion.
                FoldPendingWakeLocked(characterId, utcDue);
                return true;
            }

            if (_scheduled.TryGetValue(characterId, out var existing) && existing <= utcDue)
                return true; // already scheduled earlier (or equal) — dedup

            _scheduled[characterId] = utcDue;
            _due.Enqueue(characterId, utcDue);
            return true;
        }
    }

    private void FoldPendingWakeLocked(uint characterId, DateTime utcDue)
    {
        // Earliest pending wake wins.
        if (_pendingWake.TryGetValue(characterId, out var existing) && existing <= utcDue)
            return;
        _pendingWake[characterId] = utcDue;
    }

    private void ReleaseLease(uint characterId) => _leases.TryRemove(characterId, out _);

    private static void UpdateMax(ref long field, long candidate)
    {
        long current;
        while (candidate > (current = Volatile.Read(ref field)))
        {
            if (Interlocked.CompareExchange(ref field, candidate, current) == current)
                return;
        }
    }

    /// <summary>
    /// Tick subscriber entry — production runs this INLINE on the game-loop
    /// thread (TickManager OnTick, useAsync: false), which is the single
    /// execution boundary. The first call pins the boundary thread.
    /// </summary>
    private void TickDrain(TimeSpan delta) => DrainTickQueue();

    /// <summary>
    /// Drains the marshal queue on the single execution boundary (the
    /// game-loop thread). Each queued step executes synchronously here;
    /// worker threads never run steps (M5 A1). Internal so the rig can pump
    /// the drain on its simulated boundary thread.
    /// </summary>
    internal void DrainTickQueue()
    {
        // Pin the boundary on first drain: production = the tick thread;
        // the rig = its test thread (or an explicit test pin).
        ExecutionBoundary.RegisterExecutionThread();

        while (_tickQueue.TryDequeue(out var step))
            ExecuteStepOnExecutionBoundary(step);
    }

    /// <summary>A step handed from the due queue to the worker pool.</summary>
    private readonly record struct BotStep(uint BotId, DateTime DueUtc);
}
