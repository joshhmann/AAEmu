namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// How one leg (one actor request the executor issued) ended — the readable
/// summary of <see cref="ActorLifecycleState"/> the return path carries.
/// </summary>
public enum BotLegOutcomeKind
{
    /// <summary>The leg did what it was asked to do.</summary>
    Completed = 0,

    /// <summary>The leg was rejected or timed out: it did not do the job.</summary>
    Failed = 1,

    /// <summary>The leg was stopped by something other than itself (a preemption, an operator stop, a death release).</summary>
    Interrupted = 2,
}

/// <summary>
/// One leg's terminal outcome, as the executor saw it (plan T4 / layer-audit
/// L3-b: "no completion/failure return path into choice").
///
/// The step executor is the only thing that sees a leg end, so it reports the
/// fact through <c>BotRoamStepExecutor.LegOutcomeSink</c> — stamped with the
/// activity that issued the leg — and the arbiter hands it to that module's next
/// <see cref="IBotActivityModule.CanActivate"/> through
/// <see cref="BotActivityContext.LastLegOutcome"/>. Neither side decides what a
/// module should do about it: the outcome is a fact, the module's answer is the
/// module's business.
///
/// <see cref="State"/> is the authority (<see cref="Kind"/> is derived from it);
/// <see cref="Detail"/> is the leg's own detail line, which for a rejected or
/// expired leg already carries the failure reason ("{reason}: {detail}").
/// </summary>
/// <param name="CharacterId">The bot whose leg ended.</param>
/// <param name="Action">The verb the leg was running.</param>
/// <param name="State">The terminal lifecycle state (never a running state).</param>
/// <param name="Detail">The leg's own detail line ("" when the leg carried none).</param>
/// <param name="MoveOwner">The move owner the leg was issued under (null when the leg had no owner).</param>
/// <param name="TraceId">The request identity — one outcome per leg, keyed on this.</param>
/// <param name="ActivityName">
/// The activity that was in force while the leg ran, as the executor read it
/// when the leg was issued (null only when no activity provider is wired, or
/// when it answered null). The arbiter refuses to hand a leg to a module whose
/// activity is no longer the active one, so this is what keeps a fact about a
/// superseded activity away from the module that took the bot over. A module
/// that renames its own activity (a schedule phase, a conflict zone) also drops
/// its pending leg this way: the stamp names the activity the leg was issued
/// under, not the module that issued it.
/// </param>
public sealed record BotLegOutcome(
    uint CharacterId,
    ActorActionType Action,
    ActorLifecycleState State,
    string Detail,
    string? MoveOwner,
    Guid TraceId,
    string? ActivityName = null)
{
    /// <summary>Completed, failed or interrupted (derived from <see cref="State"/>).</summary>
    public BotLegOutcomeKind Kind => State switch
    {
        ActorLifecycleState.Completed => BotLegOutcomeKind.Completed,
        ActorLifecycleState.Interrupted => BotLegOutcomeKind.Interrupted,
        _ => BotLegOutcomeKind.Failed,
    };

    /// <summary>True when the leg did not do the job (rejected or timed out).</summary>
    public bool IsFailure => Kind == BotLegOutcomeKind.Failed;
}
