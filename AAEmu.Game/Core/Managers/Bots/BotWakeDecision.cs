#nullable enable

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// What the last arbitration pass decided for one bot, kept for the operator
/// read-back (plan T7; audit L2.7 "control plane: two front doors, one side
/// door, no goal"). The arbiter's refusal reason (<see cref="BotArbitration.WhyNot"/>)
/// used to live only inside the transition log line, so an operator could see
/// the activity a bot was running but not why it was running that one — this
/// record carries the arbiter's own text, never a synthesized substitute,
/// beside the outcome, the elected activity and the candidate/refusal counts.
///
/// One entry per bot, overwritten every wake (the same overwrite-next-wake
/// semantics as the staged arbitration trace), so it describes the LAST wake
/// and never accumulates. It is a read-back only: nothing decides from it.
/// </summary>
/// <param name="Outcome">The arbitration outcome of that wake
/// (<see cref="BotArbitrationOutcome"/> name; <c>None</c> = inert arbiter).</param>
/// <param name="Activity">The activity the wake put in force, when it put one
/// in force (<c>Activated</c>/<c>Unchanged</c>); null for a wake that elected
/// nothing.</param>
/// <param name="Reason">The arbiter's first refusal recorded during that wake,
/// or null when nothing refused. Null is the honest answer: a wake where every
/// module allowed has no refusal to report.</param>
/// <param name="Candidates">Modules that allowed during the wake.</param>
/// <param name="Rejections">Modules that refused during the wake.</param>
public sealed record BotWakeDecision(
    string Outcome,
    string? Activity,
    string? Reason,
    int Candidates,
    int Rejections);
