using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Director;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Behavior-level lifecycle vocabulary. Distinct from the request-level
/// <see cref="ActorLifecycleState"/> (which tracks ONE actor request from
/// Requested to terminal): this tracks ONE behavior instance across wakes.
/// Only the quest slice is hosted today — the other roam branches keep their
/// inline leg discipline until their own extraction.
/// </summary>
public enum BotBehaviorStatus : byte
{
    /// <summary>No tick has run yet for this bot.</summary>
    NotStarted = 0,
    /// <summary>A tick is in flight (transient — the quest tick is synchronous).</summary>
    Running = 1,
    /// <summary>Tick deferred: the actor was busy, work stays queued for next wake.</summary>
    Waiting = 2,
    /// <summary>Decision work landed (terminal Completed request).</summary>
    Completed = 3,
    /// <summary>No legal proposal — nothing to do, not an error (travel fallback may arm).</summary>
    Blocked = 4,
    /// <summary>Dispatch left the terminal surface, or the run threw.</summary>
    Failed = 5,
    /// <summary>Superseded: the electing activity moved away while a tick was pending.</summary>
    Cancelled = 6,
}

/// <summary>
/// Minimal owner for behavior lifecycle, derived from AAEmu semantics: one
/// wake runs at most one synchronous quest tick against the bot's EXISTING
/// live actor (never a second instance — the arbiter-executor precedent);
/// execution stays GameplayActor → engine; engine legality stays in the
/// actor; cadence stays executor-owned (NextWake is a hint, never consumed
/// for scheduling today).
///
/// The runtime hosts the quest leg ONLY; all other roam branches stay
/// untouched. It owns no navigation state (Path/PendingLeg stay with the
/// route layer), never mutates Character.Quests, performs no HTTP, and
/// creates no scheduler, thread, or second perception system — perception
/// rides the behavior's existing BotObservedContext captures.
/// </summary>
public sealed class BotBehaviorRuntime
{
    /// <summary>The only behavior hosted today.</summary>
    public const string QuestBehaviorName = "quest";

    private readonly Func<DateTimeOffset> _utcNow;

    public BotBehaviorRuntime(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }
    /// <summary>
    /// True while a dispatched-but-running quest request is owned across
    /// turns (the G5 pursuit/return/approach Move shape). While set, the
    /// tick holds the exact request — no re-selection, no fallback arm —
    /// until its terminal lands, which is consumed once before the next
    /// fresh decision. Safety preemptions retire the leg through the actor;
    /// <see cref="Cancel"/> preserves this flag so the terminal is still
    /// consumed on resume. Generation-scoped with the runtime itself.
    /// </summary>
    private bool _ownsRunning;

    /// <summary>The last engine refusal of a dispatched quest action and how many times it repeated.</summary>
    private (uint QuestId, ActorActionType Action, string Detail)? _refusal;
    private int _refusalCount;

    /// <summary>
    /// True when <paramref name="request"/> is this runtime's live owned
    /// Running job (identity by reference — the actor's single-writer slot
    /// holds the dispatched object itself). The executor's sole pre-check
    /// before letting the quest leg hold a busy wake.
    /// </summary>
    public bool OwnsRunningRequest(ActorRequest? request)
        => _ownsRunning
            && CurrentActorRequest is { IsTerminal: false } owned
            && request is { IsTerminal: false } live
            && ReferenceEquals(live, owned);

    /// <summary>Electing activity holding this bot (e.g. quest.progress), as gated by the caller.</summary>
    public string? CurrentActivity { get; private set; }

    /// <summary>Behavior under lifecycle. Quest-only today.</summary>
    public string CurrentBehavior { get; } = QuestBehaviorName;

    /// <summary>Leg-level phase of the last tick: quest-leg while running, then the landed action name or fail stage.</summary>
    public string CurrentPhase { get; private set; } = "idle";

    /// <summary>Per-wake instance id (the caller's CycleId) — the wake→terminal join key.</summary>
    public string? BehaviorInstanceId { get; private set; }

    /// <summary>Last request the quest tick dispatched (null when nothing was dispatched).</summary>
    public ActorRequest? CurrentActorRequest { get; private set; }

    /// <summary>When the current instance started.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When decision work last landed.</summary>
    public DateTimeOffset? LastProgressAt { get; private set; }

    /// <summary>Full result of the last tick (decision-path evidence attached).</summary>
    public QuestDecisionScenario.QuestRunResult? LastResult { get; private set; }

    /// <summary>Last error detail (EXECUTE/RUN failures only — Blocked "no legal work" is not an error).</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// When this behavior wants its next tick. The quest leg is purely
    /// reactive (it runs when the arbiter elects quest.*), so this is always
    /// null: no self-imposed wake. Recorded for the future navigation/behavior
    /// host and telemetry joins — the executor's cadence rules apply unchanged.
    /// </summary>
    public TimeSpan? NextWake { get; private set; }

    /// <summary>Lifecycle status of the last tick.</summary>
    public BotBehaviorStatus Status { get; private set; } = BotBehaviorStatus.NotStarted;

    /// <summary>
    /// Quest-terminal feed (E-66 Gap 2): invoked once per landed quest-verb
    /// request (characterId, questId, engine-confirmed completed) — and once
    /// per completed quest id on a bulk landing wake. Null (default) =
    /// unenrolled: zero behavior change — one null check on landing wakes
    /// only, never on holds. The executor wires the owning module; the
    /// runtime never resolves DI.
    /// </summary>
    public Action<uint, uint, bool>? QuestCompletedSink { get; set; }

    /// <summary>Earned bounded retirement, distinct from authoritative quest completion.</summary>
    public Action<uint, QuestGiveUp>? QuestGiveUpSink { get; set; }

    /// <summary>
    /// Feeds quest terminals into the owning module: every quest id is
    /// reported, with engine-confirmed completion (never inferred from the
    /// terminal alone — a Completed verb can land without the quest
    /// completing, and quests can leave ActiveQuests by drop/fail too).
    /// A non-completed terminal still releases the module's purpose so the
    /// next wake re-decides from the fresh listing.
    /// </summary>
    private void FeedQuestTerminals(IGameplayActor actor, IReadOnlyList<uint> questIds)
    {
        var sink = QuestCompletedSink;
        if (sink == null || questIds.Count == 0)
            return;
        var character = actor.Character;
        foreach (var questId in questIds)
            sink.Invoke(character.Id, questId, character.Quests?.HasQuestCompleted(questId) == true);
    }

    /// <summary>
    /// Quest verbs whose terminal can carry quest credit (accept/advance and
    /// the three turn-in shapes — the request's TargetId is the quest id).
    /// Movement and all other verbs never complete quests, so an owned-hold
    /// terminal of any other action feeds nothing.
    /// </summary>
    private static bool IsQuestTerminalAction(ActorActionType action)
        => action is ActorActionType.AcceptQuest
            or ActorActionType.AdvanceQuest
            or ActorActionType.TurnInQuest
            or ActorActionType.TurnInDoodad
            or ActorActionType.AutoTurnIn;

    /// <summary>
    /// Runs one quest leg tick: perceive → propose → shared-select → stage →
    /// dispatch → observe, via <see cref="QuestBehavior"/>. Returns true when
    /// the quest owns the wake: applied work, an unchanged quest advance, or
    /// an owned Running request. Status and LastProgressAt distinguish progress
    /// from a hold — the caller keeps its
    /// route/cadence discipline byte-identical either way.
    /// A Running dispatch becomes owned: later ticks hold it (same request,
    /// no new decision) until its exact terminal lands, which is consumed
    /// once before the next fresh decision reads it through the audit trace.
    /// </summary>
    public bool Tick(
        IGameplayActor actor,
        Func<Character, float, IEnumerable<Npc>>? nearbyNpcs,
        QuestDecisionScenario.QuestOptions options,
        string? activity)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);

        // Single-owner hold (P0.4): while we own a Running job and the actor
        // still carries that exact request, hold it — no re-selection. Holds
        // any Running request (quest or quest-travel), because either is the
        // quest slice's own landed work. Its pursuit observes only the live
        // target and useful combat range; it never selects new work here.
        // The executor's top-of-wake safety gate may also supersede it: survival
        // preempts through the actor first (StepSurvivalWake), so a retired
        // leg falls through to fresh work below by identity, never by flag.
        if (_ownsRunning && CurrentActorRequest is { } ownedRunning)
        {
            if (!ownedRunning.IsTerminal
                && QuestBehavior.TryRetireOwnedPursuit(actor, ownedRunning, LastResult?.SelectedProposal))
            {
                // The exact chase was stopped through the ordinary actor. Its
                // interruption is already audited; reaching attack range or
                // losing prey is neither navigation failure nor quest credit.
                _ownsRunning = false;
                CurrentActorRequest = null;
                LastResult = null;
                Status = BotBehaviorStatus.Blocked;
                CurrentPhase = "pursuit-recheck";
                LastFailure = null;
                NextWake = null;
                return true;
            }
            if (!ownedRunning.IsTerminal)
            {
                Status = BotBehaviorStatus.Running;
                CurrentPhase = "quest-leg-running";
                NextWake = null;
                return true;
            }
            // The exact terminal landed: consume it once — carry the same
            // landed/failed surface a fresh decision would, then release so
            // the NEXT wake re-decides.
            _ownsRunning = false;
            CurrentActorRequest = null;
            LastResult = null;
            return ConsumeOwnedTerminal(actor, ownedRunning, activity, options);
        }

        CurrentActivity = activity;
        BehaviorInstanceId = options.CycleId;
        StartedAt = _utcNow();
        Status = BotBehaviorStatus.Running;
        CurrentPhase = "quest-leg";
        NextWake = null;

        var result = QuestDirector.Run(actor, nearbyNpcs, options);
        LastResult = result;
        CurrentActorRequest = result.Request;

        // A live request still owns execution. Only an earned verdict with no
        // running work may release the retained goal; interruption itself never
        // counts as give-up, and completion memory remains engine-owned.
        if (result.Request?.IsTerminal != false && actor.ActiveRequest?.IsTerminal != false)
        {
            foreach (var giveUp in result.GiveUps)
                QuestGiveUpSink?.Invoke(actor.Character.Id, giveUp);
        }

        // A dispatched-but-running Move leg IS landed work (the pursuit,
        // return, loot-approach and gather/interact closes take many wakes).
        // Own it: later ticks hold it until its exact terminal lands, and the
        // travel fallback below must not arm against our own leg.
        if (result.WorkSelected && result.Request is { IsTerminal: false })
        {
            _ownsRunning = true;
            Status = BotBehaviorStatus.Running;
            CurrentPhase = "quest-leg-running";
            return true;
        }
        // A Completed terminal only means the step machine ran; the request's
        // payload carries the applied verdict (false = ran but changed nothing,
        // e.g. a declined advance). That is not landed progress — report a wake
        // that produced none instead of letting the caller read success.
        var completedTerminal = result.Request?.State == ActorLifecycleState.Completed;
        var appliedNothing = result.WorkSelected && completedTerminal && result.Request!.Result is false;
        var landed = result.WorkSelected && completedTerminal && !appliedNothing;
        if (appliedNothing)
        {
            Status = BotBehaviorStatus.Blocked;
            CurrentPhase = "ran-without-progress";
            LastFailure = result.Request!.Detail;
            // The elected quest still owns this turn. Returning unhandled here
            // starts an unrelated patrol, which is stopped next turn and keeps
            // resetting the goal owner's existing idle recovery.
            return BotActivityDispatchSurface.Resolve(activity) == BotActivityCarrier.Quest;
        }
        if (landed)
        {
            _refusal = null;
            _refusalCount = 0;
            Status = BotBehaviorStatus.Completed;
            CurrentPhase = result.SelectedAction?.ToString() ?? "completed";
            LastProgressAt = _utcNow();
            LastFailure = null;
            if (result.CompletedQuestIds.Count > 0)
                FeedQuestTerminals(actor, result.CompletedQuestIds);
            return true;
        }


        if (!result.WorkSelected && result.FailStage == "DECIDE")
        {
            Status = BotBehaviorStatus.Blocked;
            CurrentPhase = "no-legal-work";
            LastFailure = null;
            return false;
        }

        Status = BotBehaviorStatus.Failed;
        CurrentPhase = result.FailStage.ToLowerInvariant();
        LastFailure = result.FailReason;
        CountRefusal(actor, result.Request, options);
        return false;
    }

    /// <summary>
    /// Gives a quest up when the engine refuses the SAME dispatched action the
    /// same way <see cref="QuestDecisionScenario.QuestOptions.GiveUpAfterRefusedActions"/>
    /// times in a row: the next decision would only make the identical request
    /// again (populated-area run 3: a quest-261 item use refused "UrkTargetNpc"
    /// ~3,400 times per bot). The give-up names the refusal verbatim and goes
    /// through the ordinary give-up sink, so the owning module defers the quest
    /// exactly like any other earned give-up. Landed progress clears the count.
    /// </summary>
    private void CountRefusal(IGameplayActor actor, ActorRequest? request, QuestDecisionScenario.QuestOptions options)
    {
        if (request is not { State: ActorLifecycleState.Rejected, DecisionQuestId: { } questId and not 0 }
            || options.GiveUpAfterRefusedActions <= 0)
            return;
        var key = (questId, request.Action, request.Detail ?? "");
        _refusalCount = _refusal == key ? _refusalCount + 1 : 1;
        _refusal = key;
        if (_refusalCount < options.GiveUpAfterRefusedActions)
            return;
        var giveUp = new QuestGiveUp(questId, $"GIVE-UP/ACTION-REFUSED: {request.Action} ({request.Detail})",
            _refusalCount, options.GiveUpAfterRefusedActions);
        _refusal = null;
        _refusalCount = 0;
        QuestGiveUpSink?.Invoke(actor.Character.Id, giveUp);
    }
    /// <summary>
    /// Consumes the owned request's exact terminal once (the hold released
    /// above): a Completed leg lands progress exactly like a fresh Completed
    /// decision; any other terminal fails closed with the request's own
    /// state/detail (never a re-selection, never a bare failure).
    /// </summary>
    private bool ConsumeOwnedTerminal(IGameplayActor actor, ActorRequest request, string? activity, QuestDecisionScenario.QuestOptions options)
    {
        CurrentActivity = activity;
        BehaviorInstanceId = options.CycleId;
        StartedAt = _utcNow();
        NextWake = null;
        if (request.State == ActorLifecycleState.Completed && request.Result is not false)
        {
            _refusal = null;
            _refusalCount = 0;
            Status = BotBehaviorStatus.Completed;
            CurrentPhase = request.Action.ToString();
            LastProgressAt = _utcNow();
            LastFailure = null;
            if (IsQuestTerminalAction(request.Action))
                FeedQuestTerminals(actor, [request.TargetId]);
            return true;
        }
        if (request.State == ActorLifecycleState.Completed)
        {
            // Ran but applied nothing (a declined step): Completed by design, the
            // payload is the applied verdict. Consume it as a wake that produced no
            // progress — never as landed work — and still own the wake for a quest
            // leg so the old patrol cannot start a Move under it.
            Status = BotBehaviorStatus.Blocked;
            CurrentPhase = "ran-without-progress";
            LastFailure = request.Detail;
            // The elected-name vocabulary is declared once (plan T9, audit L3-a):
            // this wake belongs to a quest leg exactly when the surface says so.
            return BotActivityDispatchSurface.Resolve(activity) == BotActivityCarrier.Quest;
        }
        Status = BotBehaviorStatus.Failed;
        CurrentPhase = "execute";
        LastFailure = $"{request.Action} ended {request.State} ({request.Detail})";
        CountRefusal(actor, request, options);
        // Consuming this exact quest request owns the wake even on failure.
        // Otherwise the old patrol can start a new Move before the following
        // quest decision. Keep Failed and the original progress timestamp.
        return BotActivityDispatchSurface.Resolve(activity) == BotActivityCarrier.Quest;
    }

    /// <summary>
    /// Death release (M6.2 death watch): the bot died while this runtime
    /// owned a Running job. Drops the hold and the quest request pointer so
    /// the first post-respawn turn re-decides from the fresh listing instead
    /// of resuming the stale pre-death leg from the portal position. The
    /// actor's live request is retired by the caller through the actor
    /// (PreemptCurrent) — this method never touches the actor. No terminal
    /// is consumed and no failure recorded: death is not a step failure.
    /// </summary>
    public void ReleaseOnDeath(string reason)
    {
        _ownsRunning = false;
        CurrentActorRequest = null;
        LastResult = null;
        if (Status is BotBehaviorStatus.Running or BotBehaviorStatus.Waiting)
        {
            Status = BotBehaviorStatus.Cancelled;
            CurrentPhase = $"cancelled:{reason}";
        }
        NextWake = null;
    }

    /// <summary>
    /// Marks the hosted tick superseded (the electing activity moved away).
    /// Observation only: transitions Running/Waiting → Cancelled, releases
    /// any owned Running hold, and drops the quest request pointer. Any
    /// live-actor preemption stays with the owning leg via PreemptCurrent —
    /// this method never touches the actor.
    /// </summary>
    public void Cancel(string reason)
    {
        // P0.4: the safety supersede retires the owned job through the actor
        // (executor StepSurvivalWake preempts first); the runtime drops its
        // hold so the next quest wake re-decides instead of consuming a leg
        // that no longer belongs to it.
        _ownsRunning = false;
        if (Status is not (BotBehaviorStatus.Running or BotBehaviorStatus.Waiting))
            return;
        Status = BotBehaviorStatus.Cancelled;
        CurrentPhase = $"cancelled:{reason}";
        CurrentActorRequest = null;
        NextWake = null;
    }
}
