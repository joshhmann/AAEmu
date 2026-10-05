namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// What a quest advance actually PROVED — the distinction the
/// <see cref="ActorLifecycleState.Completed"/> terminal cannot carry on its own.
///
/// WHY THE SPLIT. <c>AdvanceQuest</c> completes when the step machine ran to
/// completion for the wake, which is a fact about TRANSPORT: the request was
/// accepted, the engine was invoked once, and it returned. That is not a fact
/// about the WORLD. A quest whose objective cannot be credited still ran, still
/// completed, and left its step and status byte-identical. Both rows read
/// <c>Completed</c>, so a reader of the audit trail alone could not tell "the
/// world moved" from "the engine was called and declined" — the transport-vs-
/// acceptance conflation the agent-wow session lesson names
/// (<c>world/session.go:173-186</c>: accepting a command is not performing it).
///
/// The vocabulary is therefore two independent facts, never one verdict:
///  - <see cref="Accepted"/> — DISPATCH CONFIRMED. The step machine ran once and
///    returned; the request completed. This is what the lifecycle terminal
///    already means, and it is what every caller depends on.
///  - <see cref="Applied"/> — WORLD VERIFIED CHANGED. The post-call quest state
///    actually differs from the pre-call state: the step advanced, the status
///    advanced, or the quest dropped out of the active set (a Reward completion).
///    Read from the live quest AFTER the call, never inferred from the return
///    value — a byte-identical quest never reads as applied.
///
/// ACCEPTED WITHOUT APPLIED IS A REAL, EXPECTED OUTCOME, not a defect: it is a
/// declined step, and it is exactly the row that must not be counted as landed
/// world progress. Neither fact is synthesized: a request that never reached the
/// step machine reports neither.
/// </summary>
public readonly record struct QuestAdvanceOutcome(bool Accepted, bool Applied)
{
    /// <summary>
    /// Space-free diagnostic token for a wake row, e.g. <c>accepted applied</c>
    /// or <c>accepted not-applied</c> — greppable by the lane parser, and
    /// carrying the same two facts a reader would otherwise have to infer from
    /// prose. The <c>not-</c> prefix is explicit so a substring scan for
    /// <c>applied</c> cannot mistake a declined advance for a landed one.
    /// </summary>
    public string Token => $"accepted {(Applied ? "applied" : "not-applied")}";

    /// <summary>
    /// The outcome of one advance: <paramref name="accepted"/> is the step
    /// machine having run (the request completed), <paramref name="applied"/> is
    /// the verified world-state change.
    /// </summary>
    public static QuestAdvanceOutcome From(bool accepted, bool applied) => new(accepted, applied);
}
