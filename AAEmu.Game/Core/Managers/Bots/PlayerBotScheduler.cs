using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Due-time scheduler + game-loop marshal for player bots (slice #6, audit
/// follow-up t_0a61eeb1).
/// See <see cref="IPlayerBotScheduler"/> for the contract.
///
/// Concurrency model (spec §4-5 as corrected by the 2026-08-09 Kimi audit):
/// one <see cref="PriorityQueue{TElement,TPriority}"/> of (BotId,
/// NextWakeTime) plus an event queue, both guarded by a single small queue
/// lock; a per-bot execution lease (<see cref="_leases"/>) that guarantees
/// at most one in-flight step per bot; and a MARSHAL SEAM that executes bot
/// steps on the game loop thread instead of an unsynchronized worker pool.
///
/// Marshal seam: the audit found the old 4-8 worker pool ran bot steps on
/// parallel threads that wrote Character transforms while the game loop and
/// Region.GetList (Region.cs:401) read the same state — a witness race
/// (Collection-modified / torn Transform reads). Steps are now handed to a
/// bounded channel drained INLINE on the game loop thread (a sync
/// TickManager subscription, useAsync: false) or, when no TickManager is
/// wired (standalone / tests), by exactly ONE dedicated marshal thread —
/// the card's "properly synchronized executor" fallback. Either way bot
/// step execution is fully serialized: at most one step runs at any time,
/// never concurrently with the game loop's own tick work. The wake-scan
/// stays exactly one dedicated background loop (<see cref="ScanLoopAsync"/>),
/// the review's allowed "dedicated thread" option — it only touches
/// scheduler bookkeeping, never game state.
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
    private readonly ITickManager? _tickManager;

    // Due-time queue + event queue + dedup/pending bookkeeping (queue lock only).
    private readonly object _queueLock = new();
    private readonly PriorityQueue<uint, DateTime> _due = new();
    private readonly ConcurrentQueue<uint> _events = new();
    private readonly Dictionary<uint, DateTime> _scheduled = [];     // botId → next due (dedup)
    private readonly Dictionary<uint, DateTime> _pendingWake = [];   // botId → due while leased

    // Per-bot execution lease: TryAdd on enqueue, TryRemove on completion.
    private readonly ConcurrentDictionary<uint, byte> _leases = new();

    // Bounded work channel drained by the marshal (game loop thread or the
    // single fallback marshal thread). One step at a time — fully serialized.
    private Channel<BotStep> _work = null!;
    private Task? _marshalTask;
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

    public PlayerBotScheduler(
        IPlayerBotManager manager,
        IBotStepExecutor executor,
        PlayerBotSchedulerOptions? options = null,
        TimeProvider? timeProvider = null,
        ITickManager? tickManager = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? new PlayerBotSchedulerOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tickManager = tickManager;
    }

    /// <inheritdoc />
    public int WorkerCount => 1; // serialized marshal — a single execution context

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

        // Marshal seam: the game loop thread drains the step channel inline
        // (sync subscriber → runs on the TickThread). No TickManager wired
        // (standalone/tests) → exactly one dedicated marshal thread.
        if (_tickManager != null)
        {
            _tickManager.OnTick.Subscribe(MarshalTick, TimeSpan.Zero, useAsync: false, name: "PlayerBotMarshal");
            Logger.Debug("PlayerBotScheduler started: marshal on game loop tick, scan {ScanIntervalMs}ms, batch {BatchSize}",
                _options.ScanInterval.TotalMilliseconds, _options.MarshalBatchSize);
        }
        else
        {
            _marshalTask = Task.Run(() => MarshalLoopAsync(_scanCts.Token), CancellationToken.None);
            Logger.Debug("PlayerBotScheduler started: fallback marshal thread (no TickManager), scan {ScanIntervalMs}ms, batch {BatchSize}",
                _options.ScanInterval.TotalMilliseconds, _options.MarshalBatchSize);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _started) == 0)
            return; // never started — nothing to stop
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        // Stop the wake-scan; workers drain the remaining queued steps (graceful).
        _scanCts.Cancel();
        if (_tickManager != null)
            _tickManager.OnTick.UnSubscribe(MarshalTick);
        _work.Writer.TryComplete();

        try
        {
            if (_marshalTask != null)
                await _marshalTask.WaitAsync(_options.ShutdownTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            Logger.Warn("PlayerBotScheduler marshal shutdown timed out after {Timeout}s",
                _options.ShutdownTimeout.TotalSeconds);
        }

        await _scanTask.WaitAsync(cancellationToken);

        // Graceful drain: execute any steps still queued after the loop
        // stopped (the pre-marshal StopAsync contract kept draining queued
        // work; the marshal path honors it inline).
        while (_work.Reader.TryRead(out var step))
            ExecuteStepSync(step);

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
                ElapsedMs: (long)elapsed);
        }
    }

    /// <summary>
    /// One wake-scan cycle: drain the event queue, then pop every due bot and
    /// hand it to the marshal. Internal so the rig can drive cycles
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

    /// <summary>Marshal tick handler — runs INLINE on the game loop thread (sync subscriber).</summary>
    private void MarshalTick(TimeSpan delta) => MarshalDrain();

    /// <summary>
    /// Test/observability seam: executes one marshal drain (up to
    /// <see cref="PlayerBotSchedulerOptions.MarshalBatchSize"/> queued steps)
    /// on the CALLER's thread, synchronously. The rig uses this to drive the
    /// marshal deterministically without a real TickManager (the production
    /// path is the game-loop tick; this is the same drain).
    /// </summary>
    internal void RunMarshalDrain() => MarshalDrain();

    /// <summary>
    /// The one wake-scan loop (dedicated background task — the review's
    /// allowed "dedicated thread"). Touches ONLY scheduler bookkeeping under
    /// the queue lock; never game state, so it is safe off the game loop.
    /// </summary>
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

    /// <summary>Fallback marshal loop (no TickManager wired): drains on a fixed cadence.</summary>
    private async Task MarshalLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.MarshalInterval, _timeProvider, cancellationToken);
                MarshalDrain();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayerBotScheduler marshal cycle failed");
            }
        }
    }

    /// <summary>
    /// Executes at most <see cref="PlayerBotSchedulerOptions.MarshalBatchSize"/>
    /// queued steps, one at a time, on the caller's thread (the game loop
    /// thread in production). Fully serialized by construction — a step that
    /// is still running blocks the drain, so no two bot steps ever overlap.
    /// </summary>
    private void MarshalDrain()
    {
        var batch = _options.MarshalBatchSize;
        for (var i = 0; i < batch; i++)
        {
            if (!_work.Reader.TryRead(out var step))
                break;
            ExecuteStepSync(step);
        }
    }

    private void ExecuteStepSync(BotStep step)
    {
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
            try
            {
                if (_options.StepTimeout > TimeSpan.Zero)
                {
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                    stepCts.CancelAfter(_options.StepTimeout);
                    // WaitAsync: the timeout token must release the marshal even
                    // if the executor ignores cancellation (the orphaned task is
                    // abandoned, exactly like the old worker-pool behavior).
                    nextDelay = _executor.StepAsync(runtime, stepCts.Token).WaitAsync(stepCts.Token).GetAwaiter().GetResult();
                }
                else
                {
                    nextDelay = _executor.StepAsync(runtime, CancellationToken.None).GetAwaiter().GetResult();
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

    /// <summary>A step handed from the due queue to the marshal.</summary>
    private readonly record struct BotStep(uint BotId, DateTime DueUtc);
}
