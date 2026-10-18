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

    // Leases taken by TryHoldForEndCapture rather than by a step; only
    // ReleaseEndCaptureHold removes these.
    private readonly ConcurrentDictionary<uint, byte> _endCaptureHolds = new();

    // M5 A1 execution boundary: steps handed by the worker pool (wake
    // producers) into this marshal queue, drained on the single execution
    // boundary — the game-loop thread (TickManager OnTick, useAsync: false).
    private readonly ConcurrentQueue<BotStep> _tickQueue = new();

    /// <summary>
    /// Guards the compound take operations on <see cref="_tickQueue"/>: the
    /// boundary's peek-then-dequeue and the drop path's drain. Without it a
    /// step peeked for execution can be dequeued and counted as dropped by the
    /// shutdown thread in between (code review 2026-10-16, F3). Enqueue is not
    /// gated — producers never wait on the boundary.
    /// </summary>
    private readonly object _tickQueueGate = new();
    // The same concrete instance used by Start's existing subscription:
    // AsyncLocal execution pins are not physical game-loop thread identity.
    private TickManager? _tickManager;

    // Bounded work channel consumed by the worker pool (wake marshalling only).
    private Channel<BotStep> _work = null!;
    private Task[] _workers = [];
    private Task _scanTask = Task.CompletedTask;
    private CancellationTokenSource _scanCts = new();

    private int _started;
    private int _stopped;
    // The reason recorded by the caller that armed the host through Arm().
    private string? _armedBy;
    private long _startedAtTicks;

    // Metrics (Interlocked; Volatile.Read for snapshots).
    private int _activeWorkers;
    private long _totalStepsRun;
    // A1 converging teardown: steps that were queued when the execution
    // boundary stopped. Dropped with a record (and lease release) instead of
    // sitting in the marshal queue unaccounted.
    private long _totalStepsDropped;
    // L2-b per-tick budget: steps left in the marshal queue when one tick
    // drain hit its budget. Deferred (they run on a later tick), never
    // dropped — counted separately so a crowded tick is visible.
    private long _totalStepsDeferred;
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

    // Quest-lifecycle run measurement lives in QuestProgressCounters (the
    // engine seams that increment it never see a bot). The scheduler does not
    // own those counters — GetMetrics only PROJECTS them, so there is no
    // second source of truth to drift and Start()/StopAsync() cannot reset a
    // count the quest engine produced.

    // M6.2 death watch: per-bot first-seen-dead timestamp (UTC). An entry is
    // added when a dead bot's step is skipped and removed on resurrection or
    // when the bot is seen alive again.
    private readonly ConcurrentDictionary<uint, DateTime> _deadSince = new();
    // §4 diagnostic (additive only): last terminal outcome per bot + per-bot
    // counters. Updated ONLY at the five terminal sites of
    // ExecuteStepOnExecutionBoundary (skip/timeout/fail/fault/run) — never on the
    // hot path — so steady-state stepping allocates nothing extra.
    private readonly ConcurrentDictionary<uint, BotStepOutcome> _botStepOutcomes = new();

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

    /// <summary>
    /// The low-level primitive: starts the wake-scan loop and the bounded
    /// worker pool without recording an arming reason. Production callers use
    /// <see cref="Arm"/> — the one arming owner — and this stays for the unit
    /// rigs that drive the scheduler directly.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        StartCore();
    }

    /// <inheritdoc />
    public string? ArmedBy => Volatile.Read(ref _armedBy);

    /// <inheritdoc />
    public bool Arm(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            reason = "host-arm";

        // A host disarmed in this process is terminal: the scan loop, the
        // worker pool and the tick drain are torn down and _stopped is never
        // reset, so "arming" again would claim a host that can never step.
        // Report the refusal; never throw (callers are bootstrap paths and
        // GM/admin commands).
        if (Volatile.Read(ref _stopped) != 0)
        {
            Logger.Warn("Bot host arm refused ({Reason}): this host was already disarmed", reason);
            return false;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            Logger.Info("Bot host already armed by {ArmedBy}; {Reason} needs no second arming",
                ArmedBy ?? "an unrecorded caller", reason);
            return false;
        }

        _armedBy = reason;
        StartCore();
        Logger.Info("Bot host armed by {Reason}", reason);
        return true;
    }

    /// <summary>
    /// The arming work itself, after <c>_started</c> has been claimed.
    /// </summary>
    private void StartCore()
    {
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
        {
            _tickManager = TickManager.Instance;
            _tickManager.OnTick.Subscribe(TickDrain, _options.TickDrainInterval, useAsync: false, name: "PlayerBotScheduler.TickDrain");
        }

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

        // Every retirement registers before enqueueing. Cancellation fails
        // queued AND already-dequeued acknowledgements, including a caller
        // racing shutdown; an abandoned item can never authorize leave-save.
        _scanCts.Cancel();

        // Unsubscribe the marshal drain: after this, no bot step can execute
        // anywhere (the boundary is the only execution path).
        if (_options.SubscribeToTickManager)
            _tickManager?.OnTick.UnSubscribe(TickDrain);

        // Stop the wake-scan; workers finish marshalling the remaining queued
        // steps into the tick queue. Steps that were never drained are
        // DROPPED — the execution boundary (game loop) is gone at shutdown,
        // and executing them off-boundary would violate the M5 contract
        // (nothing may mutate Character/world off the single boundary).
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

        // A1 converging teardown: the steps the stopped execution boundary can
        // no longer run are dropped WITH a record (and their leases released)
        // instead of sitting in the marshal queue unaccounted.
        var dropped = DrainDroppedSteps("scheduler-stopped");
        Logger.Debug("PlayerBotScheduler stopped (steps run: {StepsRun}, queued steps dropped: {Dropped})",
            Volatile.Read(ref _totalStepsRun), dropped);
    }

    /// <inheritdoc />
    public BotHostTeardownReport Disarm(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            reason = "host-disarm";

        var wasRunning = IsRunning;
        // The stop drains and counts the queued steps itself; report the TOTAL
        // this disarm dropped, not just what was left after the stop.
        var droppedBefore = Interlocked.Read(ref _totalStepsDropped);

        // Ownership convergence FIRST, while this host still owns the native
        // retirement path. A retirement is refused outright once the scheduler
        // is not running (TryRetireNativeWork :321) and its step runs on the
        // execution boundary this stop is about to tear down, so stopping
        // first left every enrolled bot in DeactivationFailed on a real host
        // shutdown (live lane 2026-10-16: `disarmed (host-shutdown)
        // running=true deactivated=0 failed=2`, "Native retirement was
        // refused or abandoned"). Each bot now leaves through the ordinary
        // manager path (its own leave-save) while the host is still up.
        var deactivated = 0;
        var failed = 0;
        List<string>? failures = null;
        var counted = new HashSet<uint>();
        // Bots this loop counted as failures, kept by reference so a leave that
        // finishes during the stop can be reconciled afterwards (runtime.State
        // is mutated in place by the manager).
        var countedAsFailed = new List<PlayerBotRuntime>();
        foreach (var runtime in _manager.GetAll())
        {
            // Deactivated = already left (the manager retains the record for
            // diagnostics). Registered = known but never embodied, so there is
            // no world presence and no leave-save to take.
            if (runtime.State is PlayerBotState.Deactivated or PlayerBotState.Registered)
                continue;

            counted.Add(runtime.CharacterId);

            if (runtime.State != PlayerBotState.Active)
            {
                // Already leaving or already failed before this disarm: it did
                // NOT leave the Active lifecycle, so a green report would be a
                // false claim (code review 2026-10-16, F2a — DeactivationFailed
                // is terminal and never retried).
                failed++;
                countedAsFailed.Add(runtime);
                (failures ??= []).Add($"{runtime.CharacterId}:{runtime.State}");
                Logger.Warn("PlayerBotScheduler disarm: character {CharacterId} was already {State} — not safely deactivated",
                    runtime.CharacterId, runtime.State);
                continue;
            }

            bool left;
            try
            {
                left = _manager.Deactivate(runtime.CharacterId, reason);
            }
            catch (Exception ex)
            {
                left = false;
                Logger.Error(ex, "PlayerBotScheduler disarm: deactivation threw for character {CharacterId}",
                    runtime.CharacterId);
            }

            if (left)
            {
                deactivated++;
            }
            else
            {
                failed++;
                (failures ??= []).Add($"{runtime.CharacterId}:{runtime.State}");
                Logger.Warn("PlayerBotScheduler disarm: character {CharacterId} stayed {State} — leave refused",
                    runtime.CharacterId, runtime.State);
            }
        }

        // Then the host itself: the execution boundary is gone after this, so
        // whatever is still queued is dropped WITH a record.
        if (wasRunning)
        {
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PlayerBotScheduler disarm: stopping the scheduler failed");
            }
        }

        DrainDroppedSteps(reason);
        var dropped = (int)(Interlocked.Read(ref _totalStepsDropped) - droppedBefore);

        // A leave that was still in flight when the stop began can land in
        // DeactivationFailed AFTER the loop above: StopAsync cancels the scan
        // registration first, which fails the retirement acknowledgement even
        // when its step was already dequeued (code review 2026-10-16, F2b).
        // Re-scan so the report cannot claim convergence for a bot that never
        // left, and so the operator route's response matches the final state.
        foreach (var runtime in _manager.GetAll())
        {
            if (counted.Contains(runtime.CharacterId))
                continue;
            if (runtime.State is PlayerBotState.Deactivated or PlayerBotState.Registered)
                continue;

            failed++;
            (failures ??= []).Add($"{runtime.CharacterId}:{runtime.State}");
            Logger.Warn("PlayerBotScheduler disarm: character {CharacterId} landed in {State} while the host stopped — not safely deactivated",
                runtime.CharacterId, runtime.State);
        }

        // The mirror case: a bot counted as failed above because it was already
        // leaving can still finish its leave while the host stops, so a report
        // that keeps it as a failure would be wrong in the other direction.
        foreach (var runtime in countedAsFailed)
        {
            if (runtime.State != PlayerBotState.Deactivated)
                continue;

            failed--;
            deactivated++;
            failures?.RemoveAll(entry => entry.StartsWith($"{runtime.CharacterId}:", StringComparison.Ordinal));
            Logger.Info("PlayerBotScheduler disarm: character {CharacterId} finished its leave while the host stopped — counted as deactivated",
                runtime.CharacterId);
        }

        Logger.Info("PlayerBotScheduler disarmed ({Reason}): running={WasRunning} deactivated={Deactivated} failed={Failed} queuedStepsDropped={Dropped}",
            reason, wasRunning, deactivated, failed, dropped);

        return new BotHostTeardownReport(reason, wasRunning, dropped, deactivated, failed,
            failures ?? (IReadOnlyList<string>)[]);
    }

    /// <summary>
    /// Drops whatever is still on the marshal queue and counts it (A1). The
    /// stopped execution boundary (the game loop) can no longer run those
    /// steps, and running them off-boundary would violate the M5 contract, so
    /// they are dropped with a per-step record instead of leaking silently.
    /// Their leases are released so a later <see cref="Start"/> is not blocked
    /// by a phantom in-flight step.
    /// </summary>
    private int DrainDroppedSteps(string reason)
    {
        var dropped = 0;
        lock (_tickQueueGate)
        {
            while (_tickQueue.TryDequeue(out var step))
            {
                dropped++;
                ReleaseLease(step.BotId);
                Logger.Info("PlayerBot step dropped ({Reason}): character {CharacterId} (due {DueUtc:O}, generation {Generation})",
                    reason, step.BotId, step.DueUtc, step.Generation);
            }
        }

        if (dropped > 0)
            Interlocked.Add(ref _totalStepsDropped, dropped);

        return dropped;
    }

    /// <summary>
    /// Retires this exact closed-admission embodiment before physical leave.
    /// The manager must release owner.Sync before calling: the boundary needs
    /// that lock to revalidate ownership, not to admit an ordinary bot step.
    /// </summary>
    internal bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason)
    {
        if (expectedOwner is null || Monitor.IsEntered(expectedOwner.Sync) || !IsRunning)
            return false;

        var timeout = _options.ShutdownTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(int.MaxValue)
            || (_options.SubscribeToTickManager && _tickManager is null))
            return false;

        var retirement = new NativeRetirement(expectedOwner, expectedGeneration, reason, timeout);
        using var stopRegistration = _scanCts.Token.Register(
            static state => ((NativeRetirement)state!).Completion.TrySetResult(false), retirement);
        if (!IsRunning || !retirement.IsPending)
        {
            retirement.Completion.TrySetResult(false);
            return false;
        }

        // Dormancy is called inline from the real game loop. Only manual
        // rigs may substitute an existing pin; never pin an HTTP caller.
        if (IsNativeRetirementBoundary)
            ExecuteNativeRetirement(retirement);
        else
        {
            _tickQueue.Enqueue(new BotStep(expectedOwner.CharacterId, default, expectedGeneration, retirement));
            if (!IsRunning)
                retirement.Completion.TrySetResult(false);
        }

        // A finite blocking acknowledgement uses no new worker or timer.
        // Timeout wins even if boundary cancellation finishes just afterward.
        var remaining = retirement.Remaining;
        if (remaining <= TimeSpan.Zero || !retirement.Completion.Task.Wait(remaining))
        {
            retirement.Completion.TrySetResult(false);
            return false;
        }
        return IsRunning && !retirement.HasExpired && retirement.Completion.Task.Result;
    }

    private bool IsNativeRetirementBoundary => _options.SubscribeToTickManager
        ? _tickManager?.IsTickThread == true
        : ExecutionBoundary.IsExecutionThread;

    private void ExecuteNativeRetirement(NativeRetirement retirement)
    {
        var retired = false;
        try
        {
            if (IsRunning && IsNativeRetirementBoundary && retirement.IsPending)
            {
                var owner = retirement.ExpectedOwner;
                lock (owner.Sync)
                {
                    if (IsRunning && retirement.IsPending
                        && _manager.TryGet(owner.CharacterId, out var current)
                        && ReferenceEquals(current, owner)
                        && ReferenceEquals(owner.Character, retirement.ExpectedCharacter)
                        && owner.Character.Id == owner.CharacterId
                        && owner.Generation == retirement.ExpectedGeneration
                        && owner.State == PlayerBotState.Deactivating
                        && TryGetRoamExecutor(out var roam))
                    {
                        retired = roam!.TryRetireNativeWork(owner, retirement.ExpectedGeneration, retirement.Reason);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "PlayerBot native retirement refused: character {CharacterId}", retirement.ExpectedOwner.CharacterId);
        }
        // Cancellation can finish after abandonment, but this acknowledgement
        // cannot turn success later or itself perform a physical leave/save.
        // A retired bot is leaving whichever path asked; an end-capture hold
        // must not outlive it and park a later re-enrollment of the character.
        if (retired)
            ReleaseEndCaptureHold(retirement.ExpectedOwner.CharacterId);
        retirement.Completion.TrySetResult(retired && IsRunning && !retirement.HasExpired);
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

    /// <summary>
    /// Freezes one bot for a final observation while the host keeps running:
    /// takes the bot's execution lease once no step holds it, so no later step
    /// is admitted (wakes fold into the pending slot). Native retirement never
    /// needs the lease, so the ordinary leave that follows still retires the
    /// bot on the execution boundary — which a stopped host refuses. E2E end
    /// capture only; never an ordinary per-bot pause.
    /// </summary>
    internal bool TryHoldForEndCapture(uint characterId, TimeSpan timeout)
    {
        // A step only releases its lease on the boundary; waiting there for
        // it would wait on ourselves.
        if (ExecutionBoundary.IsExecutionThread)
            return false;

        var started = Stopwatch.GetTimestamp();
        while (IsRunning)
        {
            lock (_queueLock)
            {
                if (_leases.TryAdd(characterId, 0))
                {
                    _endCaptureHolds[characterId] = 0;
                    return true;
                }
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return false;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>True only for a lease taken by <see cref="TryHoldForEndCapture"/>, never a step's.</summary>
    internal bool IsEndCaptureHeld(uint characterId) => _endCaptureHolds.ContainsKey(characterId);

    /// <summary>
    /// Releases a hold taken by <see cref="TryHoldForEndCapture"/> and honours
    /// any wake folded while it was held. A lease a step holds is never touched.
    /// Native retirement also calls this, so every leave path clears the hold.
    /// </summary>
    internal void ReleaseEndCaptureHold(uint characterId)
    {
        if (!_endCaptureHolds.TryRemove(characterId, out _))
            return;

        DateTime? pending = null;
        lock (_queueLock)
        {
            _leases.TryRemove(characterId, out _);
            if (_pendingWake.TryGetValue(characterId, out var wake))
            {
                _pendingWake.Remove(characterId);
                pending = wake;
            }
        }
        if (pending is { } pendingDue)
            Schedule(characterId, pendingDue <= UtcNow ? UtcNow : pendingDue);
    }

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
                TotalStepsDropped: Volatile.Read(ref _totalStepsDropped),
                TotalStepsDeferred: Volatile.Read(ref _totalStepsDeferred),
                TotalDuePopped: Volatile.Read(ref _totalDuePopped),
                LastCycleDue: Volatile.Read(ref _lastCycleDue),
                MaxCycleDue: Volatile.Read(ref _maxCycleDue),
                TotalWakeLatencyMs: Volatile.Read(ref _totalWakeLatencyMs),
                MaxWakeLatencyMs: Volatile.Read(ref _maxWakeLatencyMs),
                WorkerUtilization: utilization,
                ElapsedMs: (long)elapsed,
                TotalResurrections: Volatile.Read(ref _totalResurrections),
                TotalQuestsAccepted: QuestProgressCounters.Accepted,
                TotalQuestTurnInsRequested: QuestProgressCounters.TurnInsRequested,
                TotalQuestsCompleted: QuestProgressCounters.Completed);
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
            var gen = _manager.TryGet(botId, out var queued) && queued is not null ? queued.Generation : 0;
            if (!_work.Writer.TryWrite(new BotStep(botId, due, gen)))
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
        var startTimestamp = Stopwatch.GetTimestamp(); // allocation-free busy-time probe (Stopwatch.StartNew allocated per step)
        object? runtimeSync = null;
        var runtimeLockTaken = false;
        try
        {
            // Registry consumption: resolve the runtime now; a bot that left the
            // Active set while queued is skipped, never stepped.
            if (!_manager.TryGet(step.BotId, out var runtime) || runtime is null || runtime.State != PlayerBotState.Active)
            {
                var reason = runtime is null ? "TryGet-miss" : $"State={runtime.State}";
                Logger.Debug("PlayerBot step skipped: character {CharacterId} ({Reason})", step.BotId, reason);
                Interlocked.Increment(ref _totalStepsSkipped);
                RecordBotStepOutcome(step.BotId, BotStepOutcomeKind.Skipped);
                ReleaseLease(step.BotId);
                return;
            }

            runtimeSync = runtime.Sync;
            Monitor.Enter(runtimeSync, ref runtimeLockTaken);
            if (!_manager.TryGet(step.BotId, out var current) || !ReferenceEquals(current, runtime)
                || runtime.State != PlayerBotState.Active)
            {
                Interlocked.Increment(ref _totalStepsSkipped);
                RecordBotStepOutcome(step.BotId, BotStepOutcomeKind.Skipped);
                ReleaseLease(step.BotId);
                return;
            }

            // Generation binding: a step stamped at pop time is valid only for
            // the activation that was live then. Reactivation bumps the
            // generation, so any older step is stale and refused.
            if (step.Generation != runtime.Generation)
            {
                Logger.Debug("PlayerBot step skipped: character {CharacterId} (StaleGeneration step={StepGen} live={LiveGen})", step.BotId, step.Generation, runtime.Generation);
                Interlocked.Increment(ref _totalStepsSkipped);
                RecordBotStepOutcome(step.BotId, BotStepOutcomeKind.StaleGeneration);
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
                    // delay elapses, then normal stepping resumes from the
                    // fresh listing: the death entry releases any owned
                    // pre-death running-hold (runtime) plus the actor's live
                    // leg (interrupted through the actor — the survival-flee
                    // supersede discipline), so the first post-respawn turn
                    // cannot resume the stale leg from the portal position.
                    // Death is not a step failure: ok stays true and the
                    // fault streak is untouched. All of this stays on the
                    // boundary thread.
                    ReleaseDeathHold(step.BotId);
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
                RecordBotStepOutcome(step.BotId, BotStepOutcomeKind.TimedOut);
                Logger.Warn("PlayerBot step timed out after {Timeout}s: character {CharacterId}",
                    _options.StepTimeout.TotalSeconds, step.BotId);
            }
            catch (Exception ex)
            {
                ok = false;
                Interlocked.Increment(ref _totalStepsFailed);
                RecordBotStepOutcome(step.BotId, BotStepOutcomeKind.Failed);
                Logger.Error(ex, "PlayerBot step failed: character {CharacterId}", step.BotId);
            }
            finally
            {
                ExecutionBoundary.ExitBotStep();
            }

            if (ok)
            {
                Interlocked.Increment(ref _totalStepsRun);
                RecordBotStepOutcome(step.BotId, BotStepOutcomeKind.Ran);
            }

            // Lease release + next scheduling decision, atomic with pending wake.
            // External wakes are always honored; step-driven cadence after success,
            // one ScanInterval retry after the first consecutive failure, fault after the second.
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
            else if (!ok)
            {
                // Failed-step retry-once-then-fault: RecordBotStepOutcome
                // already ran above (catch at :429/:437), so the streak
                // includes this failure — first failure => streak == 1.
                // The death-watch path sets ok = true, so it never lands here.
                var streak = _botStepOutcomes.TryGetValue(step.BotId, out var outcome)
                    ? outcome.ConsecutiveFailures
                    : 0;
                if (streak <= 1)
                    Schedule(step.BotId, UtcNow + _options.ScanInterval);
                else
                    RecordBotStepOutcome(step.BotId, BotStepOutcomeKind.Faulted);
            }
        }
        finally
        {
            if (runtimeLockTaken)
                Monitor.Exit(runtimeSync!);
            Interlocked.Decrement(ref _activeWorkers);
            Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - startTimestamp);
        }
    }

    /// <summary>
    /// P0.1 first-wake discriminator query: registry state + scheduler entry
    /// (due/lease/pending) + last terminal outcome for one bot, read-only.
    /// </summary>
    public BotEnrollmentSnapshot GetEnrollmentSnapshot(uint characterId)
    {
        var registered = _manager.TryGet(characterId, out var runtime) && runtime is not null;
        var state = runtime?.State.ToString() ?? "-";
        DateTime? due = null;
        DateTime? pending = null;
        lock (_queueLock)
        {
            if (_scheduled.TryGetValue(characterId, out var scheduledDue))
                due = scheduledDue;
            if (_pendingWake.TryGetValue(characterId, out var pendingDue))
                pending = pendingDue;
        }
        var leased = _leases.ContainsKey(characterId);
        var outcome = _botStepOutcomes.TryGetValue(characterId, out var step) ? step : null;
        return new BotEnrollmentSnapshot(
            registered,
            state,
            due,
            leased,
            pending,
            outcome?.LastOutcome.ToString(),
            outcome?.LastUtc,
            outcome?.Skipped ?? 0,
            outcome?.Failed ?? 0,
            outcome?.TimedOut ?? 0,
            outcome?.Ran ?? 0,
            IsRunning,
            runtime?.Generation ?? 0);
    }

    /// <summary>
    /// §4 diagnostic query: last terminal step outcome for one bot (null when
    /// the bot never reached a terminal site since Start). Read-only.
    /// </summary>
    public BotStepOutcome? GetBotStepState(uint characterId)
        => _botStepOutcomes.TryGetValue(characterId, out var outcome) ? outcome : null;
    private void RecordBotStepOutcome(uint botId, BotStepOutcomeKind kind)
    {
        var now = UtcNow;
        _botStepOutcomes.AddOrUpdate(botId,
            static (_, state) => BotStepOutcome.FromKind(state.kind, state.now),
            static (_, existing, state) => existing.With(state.kind, state.now),
            (kind, now));
    }

    /// <summary>§4 diagnostic: terminal disposition of one bot step.</summary>
    public enum BotStepOutcomeKind
    {
        Skipped,
        StaleGeneration,
        TimedOut,
        Failed,
        Faulted,
        Ran,
    }

    /// <summary>
    /// §4 diagnostic: last terminal outcome + UTC + per-outcome counts for one
    /// bot. Immutable; replaced (never mutated) on each terminal site.
    /// </summary>
    public sealed record BotStepOutcome(BotStepOutcomeKind LastOutcome, DateTime LastUtc, long Skipped, long TimedOut, long Failed, long Ran, int ConsecutiveFailures)
    {
        internal static BotStepOutcome FromKind(BotStepOutcomeKind kind, DateTime now) =>
            new(kind, now,
                kind is BotStepOutcomeKind.Skipped or BotStepOutcomeKind.StaleGeneration ? 1 : 0,
                kind == BotStepOutcomeKind.TimedOut ? 1 : 0,
                kind == BotStepOutcomeKind.Failed ? 1 : 0,
                kind == BotStepOutcomeKind.Ran ? 1 : 0,
                (kind == BotStepOutcomeKind.Failed || kind == BotStepOutcomeKind.TimedOut) ? 1 : 0);

        internal BotStepOutcome With(BotStepOutcomeKind kind, DateTime now) =>
            new(kind, now,
                Skipped + (kind is BotStepOutcomeKind.Skipped or BotStepOutcomeKind.StaleGeneration ? 1 : 0),
                TimedOut + (kind == BotStepOutcomeKind.TimedOut ? 1 : 0),
                Failed + (kind == BotStepOutcomeKind.Failed ? 1 : 0),
                Ran + (kind == BotStepOutcomeKind.Ran ? 1 : 0),
                kind == BotStepOutcomeKind.Ran ? 0
                    : (kind == BotStepOutcomeKind.Failed || kind == BotStepOutcomeKind.TimedOut) ? ConsecutiveFailures + 1
                    : ConsecutiveFailures);
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

    /// <summary>
    /// Death entry (M6.2 death watch): unwinds through the executor seam so
    /// the stale pre-death job cannot resume post-respawn — the executor
    /// releases the quest running-hold and interrupts the actor's live leg
    /// (the survival-flee supersede discipline: runtime observation plus
    /// actor-side preemption, never a runtime flag alone). Non-roam
    /// executors (recording rigs, GOAP, plain-actor) ignore the call — their
    /// wakes hold no quest runtime — and death stays out of the fault
    /// streak (the death-watch path sets ok=true; this helper records
    /// nothing). Runs on the execution boundary (called from the step).
    /// </summary>
    private void ReleaseDeathHold(uint botId)
    {
        try
        {
            if (TryGetRoamExecutor(out var roam))
                roam.ReleaseQuestHoldOnDeath(botId, "bot died");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "PlayerBot death release skipped: character {CharacterId}", botId);
        }
    }

    /// <summary>
    /// Resolves the live roam executor behind the production binding, which
    /// may be the roam executor itself (unit rigs) or the arbitration
    /// decorator around it (production + through-scheduler rigs), possibly
    /// behind test counting decorators. Any other shape (GOAP, plain-actor,
    /// recording executors) holds no quest runtime and resolves false.
    /// </summary>
    private bool TryGetRoamExecutor(out BotRoamStepExecutor? roam)
    {
        var current = _executor;
        while (current is not null)
        {
            switch (current)
            {
                case BotRoamStepExecutor direct:
                    roam = direct;
                    return true;
                case BotGoalArbiterStepExecutor arbiter when arbiter.InnerIs<IBotStepExecutor>(out var inner) && inner is not null:
                    current = inner;
                    continue;
                default:
                    break;
            }
            var innerProperty = current.GetType().GetProperty("Inner", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (innerProperty?.GetValue(current) is IBotStepExecutor innerExecutor)
            {
                current = innerExecutor;
                continue;
            }
            break;
        }
        roam = null;
        return false;
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
    /// Steps marshalled and waiting for the execution boundary (the next tick
    /// drain). Internal so a rig can wait for a batch to LAND without draining
    /// it: worker threads marshal asynchronously, so "the scan cycle returned"
    /// does not mean "the steps are queued yet".
    /// </summary>
    internal int TickQueueDepth => _tickQueue.Count;

    /// <summary>
    /// Drains the marshal queue on the single execution boundary (the
    /// game-loop thread). Each queued step executes synchronously here;
    /// worker threads never run steps (M5 A1). Internal so the rig can pump
    /// the drain on its simulated boundary thread.
    /// </summary>
    internal void DrainTickQueue()
    {
        // Unsubscribe takes effect at the next tick. A callback already listed
        // for this tick must not execute queued work after shutdown.
        if (Volatile.Read(ref _stopped) != 0)
            return;

        // Pin the boundary on first drain: production = the tick thread;
        // the rig = its test thread (or an explicit test pin).
        ExecutionBoundary.RegisterExecutionThread();

        // Per-tick budget (L2-b): a crowded tick defers the remainder to the
        // next tick instead of running every queued step on the game-loop
        // thread. Deferred steps are neither reordered nor dropped and their
        // leases stay held. Native retirements are control-plane teardown with
        // their own timeout: never counted, never deferred.
        var executed = 0;
        var budget = _options.MaxStepsPerTick;
        while (Volatile.Read(ref _stopped) == 0)
        {
            BotStep step;
            lock (_tickQueueGate)
            {
                if (!_tickQueue.TryPeek(out step))
                    break;

                // Budget applies to ordinary steps only; a retirement is
                // control-plane teardown with its own timeout.
                if (step.Retirement is null && budget > 0 && executed >= budget)
                    break;

                // The peeked step leaves the queue here, under the gate the
                // drop path takes, so it cannot be executed AND counted as
                // dropped (code review 2026-10-16, F3). Executing happens
                // outside the gate: a step must never run holding it.
                _tickQueue.TryDequeue(out _);
            }

            if (step.Retirement is not null)
            {
                ExecuteNativeRetirement(step.Retirement!);
                continue;
            }

            executed++;
            ExecuteStepOnExecutionBoundary(step);
        }

        var deferred = _tickQueue.Count;
        if (deferred > 0)
        {
            Interlocked.Add(ref _totalStepsDeferred, deferred);
            Logger.Debug("PlayerBotScheduler tick budget reached: {Executed} steps run, {Deferred} deferred to the next tick",
                executed, deferred);
        }
    }

    /// <summary>
    /// Existing queue value: ordinary steps retain their allocation-free
    /// representation; only retirement creates a payload and completion.
    /// </summary>
    private readonly record struct BotStep(uint BotId, DateTime DueUtc, ulong Generation, NativeRetirement? Retirement = null);

    private sealed class NativeRetirement(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason, TimeSpan timeout)
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        public PlayerBotRuntime ExpectedOwner { get; } = expectedOwner;
        public Character ExpectedCharacter { get; } = expectedOwner.Character;
        public ulong ExpectedGeneration { get; } = expectedGeneration;
        public string Reason { get; } = reason;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Remaining => timeout - Stopwatch.GetElapsedTime(_startedAt);
        public bool HasExpired => Remaining <= TimeSpan.Zero;
        public bool IsPending => !Completion.Task.IsCompleted && !HasExpired;
    }
}
