using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;

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
    /// Runs one quest leg tick: perceive → propose → shared-select → stage →
    /// dispatch → observe, via <see cref="QuestBehavior"/>. Returns the 0b
    /// landed contract (true only on terminal Completed work) so the caller
    /// keeps its route/cadence discipline byte-identical.
    /// </summary>
    public bool Tick(
        IGameplayActor actor,
        Func<Character, float, IEnumerable<Npc>>? nearbyNpcs,
        QuestDecisionScenario.QuestOptions options,
        string? activity)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);

        // The caller gates on actor-idle; this guard is defense-in-depth so a
        // busy actor degrades to Waiting instead of a TryBegin busy-drop.
        if (actor.ActiveRequest is { IsTerminal: false })
        {
            Status = BotBehaviorStatus.Waiting;
            CurrentPhase = "waiting-actor-busy";
            NextWake = null;
            return false;
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

        var landed = result.WorkSelected && result.Request?.State == ActorLifecycleState.Completed;
        if (landed)
        {
            Status = BotBehaviorStatus.Completed;
            CurrentPhase = result.SelectedAction?.ToString() ?? "completed";
            LastProgressAt = _utcNow();
            LastFailure = null;
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
        return false;
    }

    /// <summary>
    /// Marks the hosted tick superseded (the electing activity moved away).
    /// Observation only: transitions Running/Waiting → Cancelled and drops
    /// the quest request pointer. Any live-actor preemption stays with the
    /// owning leg via PreemptCurrent — this method never touches the actor.
    /// </summary>
    public void Cancel(string reason)
    {
        if (Status is not (BotBehaviorStatus.Running or BotBehaviorStatus.Waiting))
            return;
        Status = BotBehaviorStatus.Cancelled;
        CurrentPhase = $"cancelled:{reason}";
        CurrentActorRequest = null;
        NextWake = null;
    }
}
