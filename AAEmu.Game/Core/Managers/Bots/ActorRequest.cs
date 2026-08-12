using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// One validated action request and its lifecycle.
///
/// Transitions (single-writer, driven by <see cref="IGameplayActor"/>):
///   Requested → Accepted → Running → Completed | Rejected | Interrupted | TimedOut
/// Terminal states are final; a request can never be re-run. The state
/// change log is the "state_changes" field of the audit record.
/// </summary>
public sealed class ActorRequest
{
    /// <summary>Correlation id — retries/timeouts must reuse or reference it; never re-execute a terminal request.</summary>
    public Guid TraceId { get; }

    /// <summary>
    /// Explicit idempotency key (null = no retry semantics; the request
    /// always executes and is recorded only in the audit trace). When a
    /// caller reuses a key, the actor dedupes against the effect ledger:
    /// a prior Completed/Interrupted/TimedOut attempt is never re-executed
    /// (the duplicate is Rejected(StateTransition) pre-flight). See
    /// <see cref="ActorEffectLedger"/> for the exact rule.
    /// </summary>
    public string? IdempotencyKey { get; }

    public ActorActionType Action { get; }

    /// <summary>Primary target objId (0 when not applicable, e.g. Move to position).</summary>
    public uint TargetId { get; }

    /// <summary>Move destination (Move only; null otherwise).</summary>
    public System.Numerics.Vector3? Destination { get; }

    /// <summary>Skill id (Cast only; 0 otherwise).</summary>
    public uint SkillId { get; }

    /// <summary>
    /// Action-specific parameters (quest actions): null for the v1
    /// vocabulary (Observe/Move/Stop/Target/Cast). Quest actions carry
    /// <see cref="QuestAcceptParams"/> / <see cref="QuestTurnInParams"/>.
    /// Payloads are execution inputs, never serialized into audit output.
    /// </summary>
    public object? Payload { get; }

    /// <summary>Max wall-clock budget; TimedOut when Running exceeds it (null = no timeout).</summary>
    public TimeSpan? Timeout { get; }

    public ActorLifecycleState State { get; private set; } = ActorLifecycleState.Requested;

    /// <summary>Spec §17 taxonomy reason (Rejected/TimedOut; null otherwise).</summary>
    public ActorFailureReason? Failure { get; private set; }

    /// <summary>Human-readable detail for the failure/interrupt (never "bot got stuck").</summary>
    public string? Detail { get; private set; }

    public DateTime RequestedAtUtc { get; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>Transition log, newest last (audit state_changes).</summary>
    public IReadOnlyList<string> StateChanges => _stateChanges;

    private readonly List<string> _stateChanges = [];

    /// <summary>Elapsed running time accumulated by Tick().</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>Result payload of a Completed request (e.g. SkillResult for Cast).</summary>
    public object? Result { get; private set; }

    /// <summary>
    /// True when this request was refused by the idempotency gate (a
    /// duplicate of a key that may have executed). Such a refusal must not
    /// replace the original attempt's ledger outcome — the lock survives
    /// so a third retry is still refused.
    /// </summary>
    internal bool IsDedupeRejection { get; set; }

    public ActorRequest(ActorActionType action, uint targetId, System.Numerics.Vector3? destination,
        uint skillId, TimeSpan? timeout, object? payload = null, string? idempotencyKey = null)
    {
        TraceId = Guid.NewGuid();
        Action = action;
        TargetId = targetId;
        Destination = destination;
        SkillId = skillId;
        Payload = payload;
        Timeout = timeout;
        IdempotencyKey = idempotencyKey;
        RequestedAtUtc = DateTime.UtcNow;
        // The lifecycle starts here: Requested is the initial state and must
        // appear in the audit state_changes (full transition log, oldest first).
        _stateChanges.Add(nameof(ActorLifecycleState.Requested));
    }

    public bool IsTerminal => State is ActorLifecycleState.Completed or ActorLifecycleState.Rejected
        or ActorLifecycleState.Interrupted or ActorLifecycleState.TimedOut;

    internal bool Accept(string detail)
    {
        if (State != ActorLifecycleState.Requested)
            return false;
        StartedAtUtc ??= DateTime.UtcNow; // instant actions never call Start; accept marks execution begin
        Transition(ActorLifecycleState.Accepted, detail);
        return true;
    }

    internal bool Start(string detail)
    {
        if (State != ActorLifecycleState.Accepted)
            return false;
        StartedAtUtc ??= DateTime.UtcNow;
        Transition(ActorLifecycleState.Running, detail);
        return true;
    }

    internal bool Complete(object? result = null, string detail = "")
    {
        if (!CanTerminate())
            return false;
        Result = result;
        Detail = string.IsNullOrEmpty(detail) ? "completed" : detail;
        Transition(ActorLifecycleState.Completed, Detail);
        return true;
    }

    internal bool Reject(ActorFailureReason reason, string detail)
    {
        if (!CanTerminate())
            return false;
        Failure = reason;
        Detail = $"{reason}: {detail}";
        Transition(ActorLifecycleState.Rejected, Detail);
        return true;
    }

    internal bool Interrupt(string detail)
    {
        if (!CanTerminate())
            return false;
        Detail = detail;
        Transition(ActorLifecycleState.Interrupted, detail);
        return true;
    }

    internal bool Expire(ActorFailureReason reason, string detail)
    {
        if (!CanTerminate())
            return false;
        Failure = reason;
        Detail = $"{reason}: {detail}";
        Transition(ActorLifecycleState.TimedOut, Detail);
        return true;
    }

    internal void AddElapsed(TimeSpan elapsed)
    {
        if (State == ActorLifecycleState.Running)
            Elapsed += elapsed;
    }

    private bool CanTerminate() => State is ActorLifecycleState.Accepted or ActorLifecycleState.Running;

    private void Transition(ActorLifecycleState next, string detail)
    {
        State = next;
        if (next is ActorLifecycleState.Completed or ActorLifecycleState.Rejected
            or ActorLifecycleState.Interrupted or ActorLifecycleState.TimedOut)
            CompletedAtUtc ??= DateTime.UtcNow;
        _stateChanges.Add($"{next}{(!string.IsNullOrEmpty(detail) ? $" ({detail})" : "")}");
    }
}

/// <summary>AcceptQuest request payload — the real-gate acceptor spec.</summary>
/// <param name="AcceptorType">QuestAcceptorType passed to CharacterQuests.AddQuest.</param>
/// <param name="AcceptorId">Acceptor id (NPC objId-ish template id, item template id, …).</param>
public sealed record QuestAcceptParams(QuestAcceptorType AcceptorType, uint AcceptorId);

/// <summary>Turn-in request payload — the world target + reward selection.</summary>
/// <param name="TargetObjId">Live world objId of the turn-in NPC/doodad (0 for auto turn-in).</param>
/// <param name="SelectedReward">1-based selected reward index, -1 = default.</param>
public sealed record QuestTurnInParams(uint TargetObjId, int SelectedReward);
