using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// The quest plan's pattern vocabulary: how the plan's legs accomplish the
/// quest (kill a prey for a gather, pick an npc up, use a doodad, talk a chain,
/// reach a position, craft, escort). Distinct from the template-level
/// <c>Quests.Static.QuestPattern</c> (Objective/Reward component axis) — this one
/// classifies the objective SHAPE the leg set implements. Stage 2's
/// <c>QuestDirector.Plan</c> produces <see cref="KillX"/> for a gather-from-prey
/// row and <see cref="Unknown"/> for an absent/unrecognized row (fail-closed
/// default); the remaining members are the catalog vocabulary the Stage 3 verb
/// gate maps to verb keys.
/// </summary>
public enum QuestPattern
{
    Unknown,
    KillX,
    PickupNpc,
    GatherDoodad,
    UseItem,
    TalkChain,
    ReachPos,
    Craft,
    Escort
}

/// <summary>
/// One decision leg of a quest plan. Stable ids so a wake can name the leg it
/// is evaluating (log/diag and the Stage 4 loop) without re-deriving it.
/// </summary>
public enum QuestLegId
{
    Advance,
    TurnIn,
    Target,
    Pursuit,
    Combat,
    Loot,
    Return
}

/// <summary>
/// Per-wake leg inputs: the actor, the wake's options, the quest under decision,
/// and the quest's derived fixture row plus objective funnel when the quest
/// carries an objective path (both null for a bootstrap-only quest). Built once
/// per quest per wake (a value type — legs read it, never store it).
/// </summary>
public readonly record struct QuestLegContext(
    IGameplayActor Actor,
    QuestDecisionScenario.QuestOptions Options,
    uint QuestId,
    QuestFixtureRow? Fixture,
    QuestObjectiveTargetSelector.ObjectiveTargetFunnel? Funnel)
{
    /// <summary>
    /// The wake's OWN perception snapshot (the one <c>QuestDirector.Run</c>
    /// captured before the plans were assembled), threaded to the legs so a leg
    /// that needs a census reads the wake's rather than perceiving again — the
    /// same discipline the objective funnel follows. Null only for a caller that
    /// built a leg context by hand (a direct leg invocation).
    /// </summary>
    public BotObservedContext? Observation { get; init; }
}

/// <summary>
/// A leg's proposal provider: emits the leg's proposal for this wake, or null
/// when the leg withdraws. <paramref name="diag"/> carries the leg's named
/// validate/dispatch fragment (unchanged when the leg withdraws) and
/// <paramref name="hpBefore"/> is the per-wake pre-attack HP sample the Combat
/// leg writes — the other legs never touch either.
/// </summary>
public delegate BotDecisionProposal? QuestLegEmit(QuestLegContext context, ref string diag, ref int hpBefore);

/// <summary>
/// A leg's convergence check: true when the emitted proposal has already
/// reached its exit state in the actor's audit trace (the Return leg's
/// settled-dialogue release). Read-only, never dispatches.
/// </summary>
public delegate bool QuestLegExit(IGameplayActor actor, BotDecisionProposal proposal);

/// <summary>
/// A leg's entry gate: null when the leg enters this wake (Stage 4 emits it),
/// otherwise the NAMED reason it was withheld. The run loop never invents a
/// reason — a withheld leg carries the gate's own vocabulary into
/// <see cref="LegEvidence"/>. A null <see cref="QuestLeg.Enter"/> means the leg
/// enters unconditionally (the plan already gated it: objective legs exist only
/// on a plan that carries a fixture row).
/// </summary>
public delegate string? QuestLegEnter(QuestLegContext context, QuestLegWake wake);

/// <summary>
/// One leg's wake evidence: which quest, which leg, whether its enter gate let
/// it run, whether it emitted a proposal, and the leg's named detail — the skip
/// reason when the gate withheld it, otherwise the leg's validate/dispatch diag
/// fragment. Both are the leg's own vocabulary, carried verbatim.
/// </summary>
public readonly record struct LegEvidence(
    uint QuestId,
    QuestLegId Leg,
    bool Entered,
    bool Emitted,
    string Detail);

/// <summary>
/// Per-wake leg-loop state for ONE quest: the evidence row every leg produced,
/// the Return leg's proposal (the one leg result another leg's enter gate reads
/// — TurnIn's return arbitration), and the wake's pre-attack HP sample. The plan
/// is immutable and shared; this is the scratch the legs and their enter gates
/// share within one wake. Created once per quest per wake and never stored.
/// </summary>
public sealed class QuestLegWake
{
    /// <summary>A leg's untouched diag: the loop's per-leg default, unchanged when a leg withdraws early.</summary>
    public const string NoProposal = "proposal=none";

    private readonly List<LegEvidence> _evidence = [];

    /// <summary>The quest's pre-attack HP sample, shared by every leg of this wake (only Combat writes it).</summary>
    public int HpBefore { get; internal set; } = -1;

    /// <summary>
    /// The Return leg's proposal this wake (null when the return slice withdrew
    /// or the plan carries no return leg). Named because it is the only leg
    /// result another leg reads: <see cref="QuestLegId.TurnIn"/> enters only
    /// when the return leg does not own the wake.
    /// </summary>
    public BotDecisionProposal? ReturnProposal { get; internal set; }

    /// <summary>
    /// The Return leg's convergence verdict for the proposal it emitted this
    /// wake (the leg's own <see cref="QuestLeg.Exit"/> check, evaluated by the
    /// run loop; false when the leg withdrew or the plan carries no exit check).
    /// TurnIn's enter gate reads it: a return leg that has converged releases
    /// the turn-in, one that has not keeps owning the wake.
    /// </summary>
    public bool ReturnConverged { get; internal set; }

    /// <summary>The wake's evidence rows, in leg evaluation order.</summary>
    public IReadOnlyList<LegEvidence> Evidence => _evidence;

    /// <summary>
    /// The leg's named detail for this wake: its own validate/dispatch fragment
    /// when it entered, its enter gate's skip reason when it was withheld, and
    /// <see cref="NoProposal"/> for a leg the plan never evaluated. Linear scan
    /// (at most seven rows), allocation-free.
    /// </summary>
    public string Detail(QuestLegId leg)
    {
        foreach (var row in _evidence)
        {
            if (row.Leg == leg)
                return row.Detail;
        }
        return NoProposal;
    }

    /// <summary>
    /// Records a leg that entered: its proposal (null = withdrew) and its own
    /// diag. The Return leg's result is additionally kept as
    /// <see cref="ReturnProposal"/> — the one leg result a LATER leg's enter
    /// gate reads (<see cref="QuestLegId.TurnIn"/>'s return arbitration), so
    /// the run loop records it here instead of special-casing the leg.
    /// </summary>
    internal void Record(uint questId, QuestLegId leg, BotDecisionProposal? proposal, string diag)
    {
        if (leg == QuestLegId.Return)
            ReturnProposal = proposal;
        _evidence.Add(new LegEvidence(questId, leg, Entered: true, Emitted: proposal != null, diag));
    }

    /// <summary>Records a leg its enter gate withheld: the gate's named reason, no proposal.</summary>
    internal void RecordSkip(uint questId, QuestLegId leg, string reason)
        => _evidence.Add(new LegEvidence(questId, leg, Entered: false, Emitted: false, reason));
}

/// <summary>
/// A plan that failed its fixture gate this wake, named at run-result level so
/// the failure is never silent: the quest, the plan's <see cref="QuestPlan.FailStage"/>
/// (always <c>FIXTURE</c> today) and the plan's own reason — unproven verb with
/// its gate and evidence artifact, an unrecognized act, or an unresolvable
/// objective source.
/// </summary>
public readonly record struct QuestPlanFailure(uint QuestId, string Stage, string Reason);

/// <summary>
/// One leg of a quest plan: its id, the proposal provider that evaluates it, the
/// convergence check where the leg has one (only Return does), and the entry
/// gate where the leg has one (Advance's step-work guard and TurnIn's return
/// arbitration; an unconditional leg leaves it null). <see cref="NeedsFunnel"/>
/// marks the legs that read the shared objective funnel, so the run loop
/// evaluates that funnel exactly once per wake, immediately before the first of
/// them — the plan never stores per-actor state.
///
/// A leg's position in <see cref="QuestPlan.Legs"/> IS its evaluation order:
/// the run loop walks the list as declared and never sorts it. That is why
/// Return precedes TurnIn — TurnIn's enter gate reads the return slice the same
/// wake — and why the funnel legs follow it.
/// </summary>
public sealed record QuestLeg(
    QuestLegId Id,
    QuestLegEmit Emit,
    QuestLegExit? Exit = null,
    QuestLegEnter? Enter = null,
    bool NeedsFunnel = false);

/// <summary>
/// One quest's plan for a wake: the quest id, its pattern classification, the
/// derived fixture row the legs ran against (null when the quest carries no
/// objective path), and its legs. The plan holds legs and data only — all
/// per-actor memory and all dispatch stay behavior-side. A value type so building
/// one per quest per wake (the wake path's hot loop) allocates nothing; the legs
/// it references are the shared static instances.
///
/// A plan that failed its fixture gate carries <see cref="FailStage"/> /
/// <see cref="FailReason"/> and NO legs: the failure is named before the first
/// wake (unproven verb / pattern / source, with the gate and the fixture
/// evidence in the reason), so a plan can never dispatch behavior the registry
/// has not proven.
/// </summary>
public readonly record struct QuestPlan(
    uint QuestId,
    QuestPattern Pattern,
    QuestFixtureRow? Fixture,
    IReadOnlyList<QuestLeg> Legs,
    string FailStage = "",
    string FailReason = "")
{
    /// <summary>
    /// True when the plan failed its fixture gate and therefore carries no legs;
    /// callers must not drive a failed plan (there is nothing in it to drive).
    /// Null-safe: a default-initialized plan reads as not failed.
    /// </summary>
    public bool HasFailed => FailStage is { Length: > 0 };

    /// <summary>The named leg, or null when the plan does not carry it (linear scan: at most seven legs).</summary>
    public QuestLeg? Leg(QuestLegId id)
    {
        foreach (var leg in Legs)
        {
            if (leg.Id == id)
                return leg;
        }
        return null;
    }
}
