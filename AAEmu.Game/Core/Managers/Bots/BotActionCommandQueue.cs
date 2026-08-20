using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;

using Microsoft.Extensions.DependencyInjection;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M5 contract actions exposed by the control-plane API — 1:1 with the
/// <see cref="IGameplayActor"/> vocabulary. Interrupt is a control op (it
/// cancels a running request by trace id), not a gameplay action; it stays
/// on the same queue so every actor mutation runs on the execution boundary.
/// </summary>
public enum BotActionKind : byte
{
    Observe = 0,
    Move = 1,
    MoveToUnit = 2,
    Stop = 3,
    Target = 4,
    Cast = 5,
    Interact = 6,
    Loot = 7,
    UseItem = 8,
    Mount = 9,
    Dismount = 10,
    AcceptQuest = 11,
    AdvanceQuest = 12,
    TurnInQuest = 13,
    TurnInDoodad = 14,
    AutoTurnIn = 15,
    Interrupt = 16,

    /// <summary>One engine craft step (M5.1 economy surface — workbench in the payload).</summary>
    Craft = 17
}

/// <summary>Move speed for Move/MoveToUnit commands.</summary>
public sealed record MoveActionParams(float Speed = 5f);

/// <summary>UseItem secondary target (0 = self).</summary>
public sealed record ItemUseActionParams(uint TargetObjId = 0);

/// <summary>Interact interaction skill (0 = skill-less loot-func branch).</summary>
public sealed record InteractActionParams(uint SkillId = 0);

/// <summary>Dismount mate objId (0 = whatever the actor is riding).</summary>
public sealed record DismountActionParams(uint MateObjId = 0);

/// <summary>Craft workbench doodad objId (the station the engine step runs at).</summary>
public sealed record CraftActionParams(uint DoodadObjId);

/// <summary>Interrupt target: the trace id of the running request to cancel.</summary>
public sealed record InterruptActionParams(Guid TraceId);

/// <summary>
/// One validated control-plane command. Immutable after enqueue; execution
/// state lives in the queue entry snapshot. Quest commands reuse the B1
/// contract payloads (<see cref="QuestAcceptParams"/> /
/// <see cref="QuestTurnInParams"/>) so the wire shape matches the actor.
/// </summary>
public sealed record BotActionSpec(
    BotActionKind Kind,
    uint TargetId = 0,
    Vector3? Destination = null,
    uint SkillId = 0,
    TimeSpan? Timeout = null,
    string? IdempotencyKey = null,
    object? Payload = null);

/// <summary>Enqueue outcome: the API trace id to poll, or the failure reason.</summary>
public sealed record BotActionEnqueueResult(Guid TraceId, uint CharacterId, string BotName, bool Ok, string Error)
{
    public static BotActionEnqueueResult Success(Guid traceId, uint characterId, string botName)
        => new(traceId, characterId, botName, true, string.Empty);

    public static BotActionEnqueueResult Failure(uint characterId, string botName, string error)
        => new(Guid.Empty, characterId, botName, false, error);
}

/// <summary>
/// Immutable point-in-time view of one queued command — the ONLY surface the
/// API threads read (never the actor). Published by the drain on the
/// execution boundary via a volatile swap; safe for concurrent readers.
/// Field names are contract (the control-plane API serializes them as-is).
/// </summary>
public sealed record BotActionSnapshot(
    Guid TraceId,
    uint ActorId,
    string BotName,
    string Action,
    string State,
    string? Failure,
    string? Detail,
    DateTime RequestedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<string> StateChanges,
    string? AuditJson,
    object? Result);

/// <summary>Configuration for <see cref="BotActionCommandQueue"/>.</summary>
public sealed class BotActionQueueOptions
{
    /// <summary>
    /// When true (production default), the first enqueue subscribes the
    /// drain to <see cref="TickManager"/> OnTick with useAsync:false so
    /// commands execute INLINE on the game-loop thread — the single
    /// execution boundary (M5 A1). Tests set this false and pump
    /// <c>DrainCommands()</c> manually on a pinned thread.
    /// </summary>
    public bool SubscribeToTickManager { get; init; } = true;

    /// <summary>Drain cadence on the game-loop thread. Default 10 ms (same as the scheduler).</summary>
    public TimeSpan TickDrainInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Max queue entries retained (eviction drops oldest TERMINAL entries first).</summary>
    public int HistoryCap { get; init; } = 1024;

    /// <summary>Max commands executed per drain pass (bounded per-tick work).</summary>
    public int MaxCommandsPerDrain { get; init; } = 256;
}

/// <summary>
/// The control-plane lifecycle queue (M5, stage 3 — replaces BotDriveBridge
/// semantics): the ONLY path from the API into bot execution.
///
/// Contract (ROADMAP M5 + this card):
///  - ENQUEUE-ONLY: API threads push <see cref="BotActionSpec"/>s and get a
///    trace id back; they never touch a Character, an actor or the world.
///    Execution happens on the single execution boundary (the game-loop
///    thread, M5 A1) via the TickManager OnTick inline drain — the same
///    boundary the scheduler uses, so an enqueued command drives the SAME
///    actor instance the scheduler ticks.
///  - FULL LIFECYCLE: every command walks Requested → Accepted → Running →
///    Completed | Rejected(reason) | Interrupted | TimedOut and emits the
///    B1 audit record (trace_id from the actor's trace). The API polls
///    lifecycle transitions by trace id; nothing blocks on the client.
///  - SINGLE-WRITER (contract rule): the actor runs at most one request.
///    A command against a bot with a still-running API command is
///    Rejected(StateTransition, busy). A command against a bot with a
///    world-internal request (roam leg, scenario step) PREEMPTS it via
///    actor.Interrupt — the interrupt is audited (Interrupted) and the
///    command lands deterministically, so control-plane commands work
///    against roaming bots (the default state of managed bots).
///  - CRASH ISOLATION: no world locks are ever held by a client; a
///    disconnected/crashed caller leaves only a queued command that
///    completes or times out on its own. Per-request lifetime.
///  - NO WEDGE: the queue owns a deadline backstop — a command whose
///    request stays Running past its budget (e.g. the scheduler is stopped
///    and nothing ticks the actor) is expired here, never left hanging.
/// </summary>
public sealed class BotActionCommandQueue
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public const int DefaultTraceLimit = 20;
    public const int MaxTraceLimit = 100;

    private readonly IPlayerBotManager _manager;
    private readonly IPlayerBotScheduler _scheduler;
    private readonly BotRoamStepExecutor _stepExecutor;
    private readonly BotActionQueueOptions _options;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentQueue<Guid> _queue = new();
    private readonly ConcurrentDictionary<Guid, BotActionCommand> _history = new();

    /// <summary>
    /// In-flight API commands: request trace id → entry trace id. The keys
    /// are the ACTOR request's trace ids (what the busy check compares
    /// against); the values map back to the queue entry (the refresh pass
    /// keyed by entry trace id). Boundary-thread bookkeeping.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Guid> _apiOwned = new();

    private long _nextSequence;
    private int _subscribed;

    public BotActionCommandQueue(
        IPlayerBotManager manager,
        IPlayerBotScheduler scheduler,
        BotRoamStepExecutor stepExecutor,
        BotActionQueueOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _options = options ?? new BotActionQueueOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Production wiring — MUST return the DI SINGLETON (stateful: queue +
    /// trace history live on the instance; a per-call instance would strand
    /// every trace in an unreachable queue). BotAdminService's stateless
    /// FromContainer pattern does NOT apply here.
    /// </summary>
    public static BotActionCommandQueue FromContainer()
    {
        var sp = SingletonContainer.ServiceProvider
            ?? throw new InvalidOperationException("BotActionCommandQueue: DI container not ready");
        return sp.GetRequiredService<BotActionCommandQueue>();
    }

    // ------------------------------------------------------------ enqueue

    /// <summary>
    /// Accepts a command into the lifecycle queue (thread-safe, callable from
    /// any API thread). Resolves the bot by name or character id; unknown
    /// bots are refused here. Returns the API trace id for polling — the
    /// caller can disconnect immediately; execution is server-side.
    /// </summary>
    public BotActionEnqueueResult Enqueue(string botNameOrId, BotActionSpec spec)
    {
        if (!TryResolveBot(botNameOrId, out var runtime, out var error))
            return BotActionEnqueueResult.Failure(0, botNameOrId, error);

        var entry = new BotActionCommand(runtime!.CharacterId, runtime.Character.Name, spec, UtcNow,
            Interlocked.Increment(ref _nextSequence));
        _history[entry.TraceId] = entry;
        _queue.Enqueue(entry.TraceId);
        EvictIfNeeded();
        EnsureSubscribed();
        return BotActionEnqueueResult.Success(entry.TraceId, entry.CharacterId, entry.BotName);
    }

    // -------------------------------------------------------------- reads

    /// <summary>Poll one command by API trace id (thread-safe snapshot).</summary>
    public bool TryGetSnapshot(Guid traceId, out BotActionSnapshot snapshot)
    {
        if (_history.TryGetValue(traceId, out var entry))
        {
            snapshot = entry.Snapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    /// <summary>Control-plane audit trail for one bot: the queue's own history for
    /// the character, newest first (bounded). This is the API-visible trace
    /// surface; each snapshot embeds the B1 audit record JSON of the
    /// executed request. Ordering is deterministic — a monotonic sequence
    /// breaks enqueue-time ties (same-instant bursts).</summary>
    public IReadOnlyList<BotActionSnapshot> TraceFor(uint characterId, int limit = DefaultTraceLimit)
    {
        var capped = Math.Clamp(limit, 1, MaxTraceLimit);
        return [.. _history.Values
            .Where(e => e.CharacterId == characterId)
            .OrderByDescending(e => e.Sequence)
            .Take(capped)
            .Select(e => e.Snapshot)];
    }

    /// <summary>Observability: queued (undrained) / in-flight / retained history.</summary>
    public (int Queued, int InFlight, int History) GetStats()
        => (_queue.Count, _apiOwned.Count, _history.Count);

    // ----------------------------------------------------- execution drain

    /// <summary>
    /// Runs ONLY on the single execution boundary. Production: the
    /// TickManager OnTick inline subscription (game-loop thread). Tests:
    /// manual pump on a pinned thread. Executes queued commands, then
    /// refreshes in-flight entries (live lifecycle transitions + the
    /// no-wedge timeout backstop).
    /// </summary>
    internal void DrainCommands()
    {
        ExecutionBoundary.RegisterExecutionThread();
        ExecutionBoundary.AssertOnExecutionThread("bot action command execution");

        // 1. Execute newly queued commands (bounded per drain).
        var guard = 0;
        while (_queue.TryDequeue(out var traceId) && guard++ < _options.MaxCommandsPerDrain)
        {
            if (_history.TryGetValue(traceId, out var entry))
                Execute(entry);
        }

        // 2. Refresh in-flight entries (request trace id → entry trace id).
        foreach (var (requestTraceId, entryTraceId) in _apiOwned)
        {
            if (!_history.TryGetValue(entryTraceId, out var entry))
            {
                _apiOwned.TryRemove(requestTraceId, out _);
                continue;
            }

            if (entry.Request is not { } request)
            {
                _apiOwned.TryRemove(requestTraceId, out _);
                continue;
            }

            if (request.IsTerminal)
            {
                _apiOwned.TryRemove(requestTraceId, out _);
                CaptureAudit(entry, request);
                PublishSnapshot(entry);
                continue;
            }

            // No-wedge backstop: the request is still Running past its
            // budget with no tick advancing it (scheduler stopped/not
            // started). The actor's own Tick enforces the same budget when
            // it runs; this guarantees a command can never hang the actor
            // indefinitely. Measured from ENQUEUE time on the queue's clock
            // (request timestamps are real-time; the queue clock is the
            // injectable one the backstop must be testable with — a command
            // that sat in the queue past its budget is starved, which is
            // exactly the §17 Starvation/Navigation vocabulary). The audit
            // record is built locally (the actor's Finish never ran).
            if (request.Timeout is { } budget
                && UtcNow - entry.EnqueuedAtUtc > budget)
            {
                _ = request.Expire(ActorTimeoutPolicy.ReasonFor(request.Action), "action budget exceeded (queue backstop)");
                _apiOwned.TryRemove(requestTraceId, out _);
                CaptureAudit(entry, request);
                PublishSnapshot(entry);
                continue;
            }

            PublishSnapshot(entry);
        }
    }

    /// <summary>Executes one command for its bot on the boundary thread.</summary>
    private void Execute(BotActionCommand entry)
    {
        var spec = entry.Spec;
        try
        {
            // Registry consumption: the bot must still be known and embodied.
            if (!_manager.TryGet(entry.CharacterId, out var runtime) || runtime!.State != PlayerBotState.Active)
            {
                RejectEntry(entry, ActorFailureReason.StateTransition, "bot not registered or not active");
                return;
            }

            var actor = _stepExecutor.GetOrCreateActor(runtime.Character);
            entry.Actor = actor;

            // Single-writer (contract): at most one live request per actor.
            // Interrupt commands skip the gate — cancelling the active
            // request IS their job (actor.Interrupt handles "no such active
            // request" itself).
            if (spec.Kind != BotActionKind.Interrupt && actor.ActiveRequest is { IsTerminal: false } busy)
            {
                // A still-running API command is NOT preempted — the caller
                // must poll it to a terminal state first (Rejected(busy),
                // the contract's anti-race rule for concurrent callers).
                if (_apiOwned.ContainsKey(busy.TraceId))
                {
                    RejectEntry(entry, ActorFailureReason.StateTransition, $"actor busy with {busy.Action}");
                    return;
                }

                // World-internal requests (roam legs, scenario steps) are
                // preempted so the command lands deterministically; the
                // interrupt is audited by the actor (Interrupted).
                _ = actor.Interrupt(busy.TraceId);
            }

            var (request, result) = ExecuteKind(actor, spec);
            entry.Result = result;

            // Observe returns its snapshot directly and emits its audit
            // record inside the call; Interrupt is a queue-level control op
            // with no actor request of its own — the entry records the
            // outcome (the interrupted request's record stays on ITS entry).
            if (request == null)
            {
                if (spec.Kind == BotActionKind.Interrupt)
                {
                    var interrupted = result is true;
                    entry.AuditRecord = new ActorAuditRecord(
                        entry.TraceId, entry.Actor?.ActorId ?? entry.CharacterId, ActorActionType.Stop,
                        0, entry.EnqueuedAtUtc, UtcNow, UtcNow, ActorLifecycleState.Completed, null,
                        interrupted ? "interrupt delivered" : "no matching active request (idempotent)",
                        ["Requested", $"Completed ({spec.Kind})"]);
                }
                else
                {
                    entry.AuditRecord = actor.AuditTrace.LastOrDefault();
                }

                PublishSnapshot(entry);
                return;
            }

            entry.Request = request;
            _apiOwned[request.TraceId] = entry.TraceId;

            if (request.IsTerminal)
            {
                _apiOwned.TryRemove(request.TraceId, out _);
                CaptureAudit(entry, request);
            }

            PublishSnapshot(entry);

            // A still-running request (Move) needs the scheduler to keep
            // ticking the actor; the executor returns the active cadence
            // while a request is live. Wake is a no-op when the scheduler is
            // stopped — the backstop then owns the timeout.
            if (!request.IsTerminal)
                _scheduler.Wake(entry.CharacterId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "BotActionQueue: command execution failed (trace {TraceId})", entry.TraceId);
            RejectEntry(entry, ActorFailureReason.RejectedAction, $"command execution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Invokes the actor method for the command kind. Returns the created
    /// request (null for Observe — it returns the observation — and for
    /// Interrupt, a queue-level control op) and the result payload. All
    /// validation happens INSIDE the actor methods on the boundary;
    /// rejections are full-lifecycle requests.
    /// </summary>
    private (ActorRequest? Request, object? Result) ExecuteKind(IGameplayActor actor, BotActionSpec spec)
    {
        var key = spec.IdempotencyKey;
        switch (spec.Kind)
        {
            case BotActionKind.Observe:
                return (null, actor.Observe());

            case BotActionKind.Move:
            {
                var speed = spec.Payload is MoveActionParams m ? m.Speed : 5f;
                var destination = spec.Destination
                    ?? throw new ArgumentException("move requires a destination (x/y/z)");
                return (actor.MoveTo(destination, speed, spec.Timeout, key), null);
            }

            case BotActionKind.MoveToUnit:
            {
                var speed = spec.Payload is MoveActionParams m ? m.Speed : 5f;
                return (actor.MoveToUnit(spec.TargetId, speed, spec.Timeout, key), null);
            }

            case BotActionKind.Stop:
                return (actor.Stop(), null);

            case BotActionKind.Target:
                return (actor.SetTarget(spec.TargetId), null);

            case BotActionKind.Cast:
                return (actor.Cast(spec.SkillId, spec.TargetId, key), null);

            case BotActionKind.Interact:
            {
                var skill = spec.Payload is InteractActionParams p ? p.SkillId : spec.SkillId;
                return (actor.Interact(spec.TargetId, skill, key), null);
            }

            case BotActionKind.Loot:
                return (actor.Loot(spec.TargetId, key), null);

            case BotActionKind.UseItem:
            {
                var target = spec.Payload is ItemUseActionParams p ? p.TargetObjId : 0u;
                return (actor.UseItem(spec.TargetId, target, key), null);
            }

            case BotActionKind.Mount:
                return (actor.Mount(spec.TargetId, key), null);

            case BotActionKind.Dismount:
            {
                var mate = spec.Payload is DismountActionParams p ? p.MateObjId : 0u;
                return (actor.Dismount(mate, key), null);
            }

            case BotActionKind.AcceptQuest:
            {
                var p = (QuestAcceptParams)spec.Payload!;
                return (actor.AcceptQuest(spec.TargetId, p.AcceptorType, p.AcceptorId, key), null);
            }

            case BotActionKind.AdvanceQuest:
                return (actor.AdvanceQuest(spec.TargetId, key), null);

            case BotActionKind.TurnInQuest:
            case BotActionKind.TurnInDoodad:
            case BotActionKind.AutoTurnIn:
            {
                var p = (QuestTurnInParams)spec.Payload!;
                return spec.Kind switch
                {
                    BotActionKind.TurnInQuest => (actor.TurnInQuest(spec.TargetId, p.TargetObjId, p.SelectedReward, key), null),
                    BotActionKind.TurnInDoodad => (actor.TurnInAtDoodad(spec.TargetId, p.TargetObjId, p.SelectedReward, key), null),
                    _ => (actor.AutoTurnInQuest(spec.TargetId, p.SelectedReward, key), null)
                };
            }

            case BotActionKind.Interrupt:
            {
                var p = (InterruptActionParams)spec.Payload!;
                // The payload carries the API trace id (the queue entry's);
                // resolve the ACTOR request's trace id through the history so
                // callers only ever see API trace ids. The actor.Interrupt
                // itself emits the interrupted request's terminal record.
                var targetTrace = p.TraceId;
                if (_history.TryGetValue(p.TraceId, out var targetEntry)
                    && targetEntry.Request is { } targetRequest)
                    targetTrace = targetRequest.TraceId;

                var interrupted = actor.Interrupt(targetTrace);
                return (null, interrupted);
            }

            case BotActionKind.Craft:
            {
                var doodad = spec.Payload is CraftActionParams p ? p.DoodadObjId : 0u;
                return (actor.Craft(spec.TargetId, doodad, spec.Timeout, key), null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.Kind, "unknown bot action kind");
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Resolves a bot reference (name or character id) to its registry entry.
    /// Public so the API surface can resolve the same way the queue does
    /// (e.g. the trace endpoint). Unknown bots → false + error.
    /// </summary>
    public bool TryResolveBotId(string botNameOrId, out uint characterId, out string botName, out string error)
    {
        if (!TryResolveBot(botNameOrId, out var runtime, out error))
        {
            characterId = 0;
            botName = botNameOrId;
            return false;
        }

        characterId = runtime!.CharacterId;
        botName = runtime.Character.Name;
        error = string.Empty;
        return true;
    }

    private bool TryResolveBot(string botNameOrId, out PlayerBotRuntime? runtime, out string error)
    {
        runtime = null;
        if (string.IsNullOrWhiteSpace(botNameOrId))
        {
            error = "bot is required";
            return false;
        }

        if (uint.TryParse(botNameOrId, out var id) && _manager.TryGet(id, out var byId) && byId != null)
        {
            runtime = byId;
            error = string.Empty;
            return true;
        }

        var byName = _manager.GetAll().FirstOrDefault(r =>
            r.Character.Name.Equals(botNameOrId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName == null)
        {
            error = $"unknown bot '{botNameOrId}'";
            return false;
        }

        runtime = byName;
        error = string.Empty;
        return true;
    }

    /// <summary>Rejects a command that never reached the actor (queue-level refusal).</summary>
    private void RejectEntry(BotActionCommand entry, ActorFailureReason reason, string detail)
    {
        entry.Request = null;
        entry.AuditRecord = new ActorAuditRecord(
            entry.TraceId, entry.Actor?.ActorId ?? entry.CharacterId, ToActorAction(entry.Spec.Kind),
            entry.Spec.TargetId, entry.EnqueuedAtUtc, null, UtcNow,
            ActorLifecycleState.Rejected, reason, detail, ["Requested", $"Rejected ({reason}: {detail})"]);
        PublishSnapshot(entry);
    }

    /// <summary>
    /// Captures the B1 audit record for a terminal request: the actor's own
    /// record when the actor emitted it (normal path), or a locally-built
    /// record when the queue expired the request (backstop path — the
    /// actor's Finish never ran). The record is the queue entry's AuditJson.
    /// </summary>
    private static void CaptureAudit(BotActionCommand entry, ActorRequest request)
    {
        if (entry.AuditRecord != null)
            return;

        entry.AuditRecord = entry.Actor?.AuditTrace.LastOrDefault(r => r.TraceId == request.TraceId)
            ?? new ActorAuditRecord(
                request.TraceId, entry.Actor?.ActorId ?? entry.CharacterId, request.Action,
                request.TargetId, request.RequestedAtUtc, request.StartedAtUtc, request.CompletedAtUtc,
                request.State, request.Failure, request.Detail, request.StateChanges.ToList());
    }

    private void PublishSnapshot(BotActionCommand entry)
    {
        var request = entry.Request;
        var record = entry.AuditRecord;

        var state = request?.State ?? record?.Result ?? ActorLifecycleState.Requested;
        var stateChanges = request?.StateChanges.ToList()
            ?? record?.StateChanges.ToList()
            ?? ["Requested"];

        // B4 audit-trace flush: hand the terminal record to the sink exactly
        // once (in-memory append only — the DB write happens on the
        // SaveManager tick, never on this boundary thread).
        if (!entry.AuditFlushed
            && record != null
            && state is ActorLifecycleState.Completed or ActorLifecycleState.Rejected
                or ActorLifecycleState.Interrupted or ActorLifecycleState.TimedOut)
        {
            entry.AuditFlushed = true;
            PlayerBotAuditSink.Instance.Enqueue(entry.CharacterId, record.ToJson());
        }

        entry.Publish(new BotActionSnapshot(
            TraceId: entry.TraceId,
            ActorId: entry.Actor?.ActorId ?? entry.CharacterId,
            BotName: entry.BotName,
            Action: entry.Spec.Kind.ToString(),
            State: state.ToString(),
            Failure: request?.Failure?.ToString() ?? record?.Failure?.ToString(),
            Detail: request?.Detail ?? record?.Detail,
            RequestedAtUtc: request?.RequestedAtUtc ?? record?.RequestedAtUtc ?? entry.EnqueuedAtUtc,
            StartedAtUtc: request?.StartedAtUtc ?? record?.StartedAtUtc,
            CompletedAtUtc: request?.CompletedAtUtc ?? record?.CompletedAtUtc,
            StateChanges: stateChanges,
            AuditJson: record?.ToJson(),
            Result: entry.Result));
    }

    /// <summary>Maps a command kind to the actor action for constructed audit records.</summary>
    private static ActorActionType ToActorAction(BotActionKind kind)
        => kind switch
        {
            BotActionKind.Observe => ActorActionType.Observe,
            BotActionKind.Move or BotActionKind.MoveToUnit => ActorActionType.Move,
            BotActionKind.Stop => ActorActionType.Stop,
            BotActionKind.Target => ActorActionType.Target,
            BotActionKind.Cast => ActorActionType.Cast,
            BotActionKind.Interact => ActorActionType.Interact,
            BotActionKind.Loot => ActorActionType.Loot,
            BotActionKind.UseItem => ActorActionType.UseItem,
            BotActionKind.Mount => ActorActionType.Mount,
            BotActionKind.Dismount => ActorActionType.Dismount,
            BotActionKind.AcceptQuest => ActorActionType.AcceptQuest,
            BotActionKind.AdvanceQuest => ActorActionType.AdvanceQuest,
            BotActionKind.TurnInQuest => ActorActionType.TurnInQuest,
            BotActionKind.TurnInDoodad => ActorActionType.TurnInDoodad,
            BotActionKind.AutoTurnIn => ActorActionType.AutoTurnIn,
            BotActionKind.Craft => ActorActionType.Craft,
            BotActionKind.Interrupt => ActorActionType.Stop, // control op; never constructed via a running request
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown bot action kind")
        };

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Bounded history: evict the oldest TERMINAL entries beyond the cap.
    /// Live entries (Requested — still queued; Accepted/Running — in flight)
    /// are never evicted.
    /// </summary>
    private void EvictIfNeeded()
    {
        if (_history.Count <= _options.HistoryCap)
            return;

        var live = new HashSet<ActorLifecycleState>
        {
            ActorLifecycleState.Requested,
            ActorLifecycleState.Accepted,
            ActorLifecycleState.Running
        };

        foreach (var key in _history.Keys
                     .Where(k => _history.TryGetValue(k, out var e)
                                 && !live.Contains(ParseState(e.Snapshot.State)))
                     .OrderBy(k => _history[k].EnqueuedAtUtc)
                     .Take(_history.Count - _options.HistoryCap))
        {
            _history.TryRemove(key, out _);
        }
    }

    private static ActorLifecycleState ParseState(string state)
        => Enum.TryParse<ActorLifecycleState>(state, out var parsed) ? parsed : ActorLifecycleState.Requested;

    /// <summary>Subscribes the drain to the game-loop tick (idempotent, first enqueue only).</summary>
    private void EnsureSubscribed()
    {
        if (!_options.SubscribeToTickManager || Interlocked.Exchange(ref _subscribed, 1) != 0)
            return;

        TickManager.Instance.OnTick.Subscribe(
            _ => DrainCommands(), _options.TickDrainInterval, useAsync: false, name: "BotActionCommandQueue.Drain");
        Logger.Info("BotActionCommandQueue: drain subscribed to the game-loop tick (execution boundary)");
    }
}

/// <summary>One queued command entry: immutable command + boundary-written execution state.</summary>
public sealed class BotActionCommand
{
    public Guid TraceId { get; }
    public uint CharacterId { get; }
    public string BotName { get; }
    public BotActionSpec Spec { get; }
    public DateTime EnqueuedAtUtc { get; }

    /// <summary>Monotonic enqueue order (deterministic tie-break for same-instant bursts).</summary>
    public long Sequence { get; }

    // --- Written ONLY on the execution boundary (the drain); read by API threads exclusively via Snapshot. ---
    public IGameplayActor? Actor { get; set; }
    public ActorRequest? Request { get; set; }
    public ActorAuditRecord? AuditRecord { get; set; }
    public object? Result { get; set; }

    /// <summary>B4 audit flush: set once the terminal snapshot's audit JSON was handed to PlayerBotAuditSink.</summary>
    public bool AuditFlushed { get; set; }

    private volatile BotActionSnapshot _snapshot;

    public BotActionSnapshot Snapshot => _snapshot;

    public BotActionCommand(uint characterId, string botName, BotActionSpec spec, DateTime enqueuedAtUtc, long sequence)
    {
        TraceId = Guid.NewGuid();
        CharacterId = characterId;
        BotName = botName;
        Spec = spec;
        EnqueuedAtUtc = enqueuedAtUtc;
        Sequence = sequence;
        _snapshot = new BotActionSnapshot(
            TraceId, characterId, botName, spec.Kind.ToString(),
            nameof(ActorLifecycleState.Requested), null, null,
            enqueuedAtUtc, null, null, ["Requested"], null, null);
    }

    public void Publish(BotActionSnapshot snapshot) => _snapshot = snapshot;
}
