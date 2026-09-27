#nullable enable

using System.Globalization;
using System.Numerics;

using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.Game.Core.Managers.Bots.Combat;

/// <summary>
/// The decision-layer threat verdict for ONE wake.
///
/// This is the COMBAT layer's own reading of the frame it was handed, and it is
/// deliberately NOT the perception stack's <c>ThreatLevel</c>: the belief layer
/// (<c>...Bots.Belief</c>) is a planning/diagnostic surface the decision paths
/// must not reference (see <c>PerceptionStackSurfaceTests</c>), so the combat
/// brain derives its own verdict from the primitives on
/// <see cref="CombatBrainInputs"/> using thresholds that live in exactly one
/// place — <see cref="CombatDecisionTree.DefaultEmergencyFleeHpPercent"/> for the
/// critical band, <see cref="CombatDecisionTree.DefaultDefensiveHealHpPercent"/>
/// for the healthy band, <see cref="CombatDecisionTree.DefaultMeleeMax"/> for
/// contact range.
///
/// There is deliberately NO <c>Unknown</c> member: the escalation degrades toward
/// the LOWER level when its inputs are unreadable (an unknown self hp never reads
/// as an emergency), never toward a fabricated one.
/// </summary>
public enum CombatThreat
{
    /// <summary>No hostile is on the frame.</summary>
    None = 0,

    /// <summary>A hostile is present, but the bot is healthy, unhurt and not outnumbered.</summary>
    Ambient = 1,

    /// <summary>Fighting is on: a hostile inside contact range, damage taken this frame, outnumbered, or in the wounded band.</summary>
    Engaged = 2,

    /// <summary>Own hp is at or below the flee threshold with a hostile present.</summary>
    Critical = 3
}

/// <summary>The actor-face verb a combat decision asks the actor for. The dispatcher maps these onto <see cref="ActorActionType"/>.</summary>
public enum CombatVerb
{
    /// <summary>No actor call this wake (the leg withdraws with the decision as its evidence).</summary>
    Hold = 0,

    /// <summary>A destination Move (close in, back off, or retreat).</summary>
    Move = 1,

    /// <summary>An offensive skill cast through the engine's skill path.</summary>
    Cast = 2,

    /// <summary>A consumable use through the engine's item-use path (self-targeted).</summary>
    UseItem = 3,

    /// <summary>The engine's own continuous attack loop.</summary>
    AutoAttack = 4
}

/// <summary>
/// The combat brain's published lifecycle for one actor, carried out as an
/// observable fact. <see cref="Disengaging"/> and <see cref="Engaged"/> are the
/// two facts the pursuit leg and lane diagnostics read; <see cref="Vetoed"/> is
/// the survival-veto fact.
/// </summary>
public enum CombatBrainState
{
    /// <summary>No engagement this wake.</summary>
    Idle = 0,

    /// <summary>The engagement is live: a target is committed.</summary>
    Engaged = 1,

    /// <summary>Breaking contact: the retreat owns the engagement until the threat clears.</summary>
    Disengaging = 2,

    /// <summary>Committed but with no legal verb this wake (cooldowns, crowd control).</summary>
    Holding = 3,

    /// <summary>A survival condition owns the wake; combat must not act.</summary>
    Vetoed = 4
}

/// <summary>Which arm of the decision chain produced the wake's decision — the decision's own vocabulary for diagnostics.</summary>
public enum CombatArm
{
    /// <summary>Nothing to decide (no inputs at all).</summary>
    None = 0,

    /// <summary>Arm 1: the actor cannot act — the survival veto is published.</summary>
    SurvivalVeto = 1,

    /// <summary>Arm 2: critical hp / critical-with-no-escape / leash edge — break contact.</summary>
    Disengage = 2,

    /// <summary>Arm 3: crowd-controlled — no move, no cast.</summary>
    CrowdControl = 3,

    /// <summary>Arm 4: commit to (or re-commit) a ranked target.</summary>
    Commit = 4,

    /// <summary>Arm 5: the committed target is provably gone — drop the commitment.</summary>
    InvalidDrop = 5,

    /// <summary>Arm 6: below the heal threshold with a usable consumable.</summary>
    Heal = 6,

    /// <summary>Arm 7: out of band and too far — close in.</summary>
    CloseRange = 7,

    /// <summary>Arm 7: out of band and too close — back off.</summary>
    BackOff = 8,

    /// <summary>Arm 8: the first skill of the engagement.</summary>
    Opener = 9,

    /// <summary>Arm 9: a follow-up skill of the engagement's rotation.</summary>
    Rotation = 10,

    /// <summary>Arm 10: no skill legal — keep the sustained auto-attack loop alive.</summary>
    Sustain = 11,

    /// <summary>Arm 10: nothing legal and the loop is already live — hold.</summary>
    Hold = 12
}

/// <summary>Why the engagement was broken — named so the three causes are distinguishable in a lane.</summary>
public enum CombatDisengageReason
{
    /// <summary>Not disengaging.</summary>
    None = 0,

    /// <summary>Own hp at or below the flee threshold.</summary>
    CriticalHp = 1,

    /// <summary>Threat critical with no crowd-control/escape tool ready.</summary>
    CriticalNoEscape = 2,

    /// <summary>The committed target drifted past its leash edge (it is about to reset home).</summary>
    LeashEdge = 3
}

/// <summary>
/// The named reason a decision took its arm. An ENUM rather than a built string:
/// the wake path formats text only when a diagnostic actually reads it (the same
/// discipline <c>BotBeliefState.SnapshotId</c> follows), so a per-wake decision
/// allocates nothing.
/// </summary>
public enum CombatReason
{
    /// <summary>No reason recorded.</summary>
    None = 0,

    /// <summary>The actor cannot act (down, or its own hp reads at or below zero) — survival owns the wake.</summary>
    SurvivalVetoed,

    /// <summary>Own hp is inside the flee band.</summary>
    HpCritical,

    /// <summary>Threat is critical and no crowd-control/escape tool is ready.</summary>
    CriticalNoEscape,

    /// <summary>The committed target drifted past its leash edge.</summary>
    LeashEdge,

    /// <summary>Under crowd control this wake — no move, no cast.</summary>
    CrowdControlled,

    /// <summary>A target won the ranked competition; the commitment (re)starts now.</summary>
    Committed,

    /// <summary>The incumbent stayed committed (inside its window, or no challenger cleared the margin).</summary>
    CommitHeld,

    /// <summary>No candidate scored — nothing to commit to.</summary>
    NoCandidate,

    /// <summary>The committed target is provably gone (dead/despawned) — the commitment is dropped.</summary>
    TargetGone,

    /// <summary>The committed target could not be read this frame — hold, never drop.</summary>
    IncumbentUnknown,

    /// <summary>Below the heal threshold and a usable consumable exists.</summary>
    Healed,

    /// <summary>Out of band, too far — closing in.</summary>
    CloseIn,

    /// <summary>Out of band, too close — backing off.</summary>
    BackOff,

    /// <summary>The engagement's first skill.</summary>
    Opener,

    /// <summary>A follow-up skill of the engagement's rotation.</summary>
    Rotation,

    /// <summary>No skill is legal; the sustained loop carries the damage.</summary>
    Sustain,

    /// <summary>The sustained loop is already live.</summary>
    LoopLive
}

/// <summary>
/// The combat proposal's payload: the brain's own decision, carried verbatim so
/// the DISPATCHER publishes the engagement and reads the verb's arguments from
/// the decision the leg actually emitted.
///
/// Why the decision travels on the payload instead of a static slot: the leg
/// must not publish an engagement it never dispatched. Publishing at PROPOSAL
/// time would set the yield fact on a wake where a higher-priority leg (the
/// pursuit Stop) won, and the pursuit leg would then stand down for an
/// engagement that never acted — the opposite of what the fact means.
/// </summary>
/// <param name="Decision">The combat brain's own decision for the wake this proposal was built on.</param>
/// <param name="KiteDecision">
/// The TRAVEL chain's decision, when the combat <c>Move</c> is the ROUTED kite leg
/// (<see cref="CombatTravelDispatch"/>) — <c>null</c> on the leg's own pre-brain
/// fallback, and on every non-Move verb. The dispatch site banks the leg it ACTUALLY
/// issued from it (mode + destination, so the next wake's drift read is honest) and
/// tags the movement with the kite's own owner; a fallback carries none, so it keeps
/// the pre-brain owner tag and banks nothing.
/// </param>
public readonly record struct CombatDispatchParams(
    CombatBrainDecision Decision,
    TravelDecision? KiteDecision = null);

/// <summary>
/// One candidate row the combat brain may commit to. Every flag is a live
/// verdict the planner established; a row whose truth could not be established
/// is never carried as a candidate (an unknown row is not a fabricated target),
/// and the planner counts those separately.
/// </summary>
public readonly record struct CombatCandidate(
    uint ObjId,
    int AttentionScore,
    float DistanceM,
    byte TargetLevel,
    float TargetHpRatio,
    bool QuestRelevant,
    float LeashDistanceM)
{
    /// <summary>False when any measurement on the row is unreadable (NaN distance/hp/leash).</summary>
    public bool IsMeasured => !float.IsNaN(DistanceM) && !float.IsNaN(TargetHpRatio) && !float.IsNaN(LeashDistanceM);
}

/// <summary>
/// Every input the combat decision chain reads for one wake.
///
/// Value type: the planner builds it once per wake from the wake's own
/// observation (no second perception, no engine scan) plus the live
/// re-resolution the quest legs already perform, and the brain never stores it.
/// The engagement facts (<see cref="IncumbentScore"/>, <see cref="CommitmentInForce"/>)
/// likewise arrive as inputs, read from <see cref="CombatBrainEngagement"/> by the
/// caller — so the decision function itself stays a pure function of its inputs.
///
/// Honesty contract (the decision layer's own fail-closed rule):
///  - a ratio that could not be established is NaN, never 0 — a fabricated 0
///    would read as "dead" and a fabricated 1 as "full";
///  - a distance that could not be measured is NaN, never 0 — a fabricated 0
///    would read as "inside every band";
///  - <see cref="IncumbentObjId"/> 0 means "no commitment". A non-zero incumbent
///    is tri-state through <see cref="IncumbentValid"/>/<see cref="IncumbentUnknown"/>:
///    valid = act on it, unknown = hold (never drop a target the frame could not
///    read), neither = it is provably gone and the commitment is dropped.
/// </summary>
public readonly record struct CombatBrainInputs(
    uint ActorObjId,
    CombatRole Role,
    bool Alive,
    float SelfHpRatio,
    byte SelfLevel,
    Vector3 SelfPosition,
    int EnemyCount,
    float NearestEnemyDistanceM,
    bool TookDamageThisFrame,
    bool CrowdControlled,
    bool CcAvailable,
    bool IsAutoAttackLive,
    uint HealItemTemplateId,
    float SkillMinRangeM,
    float SkillMaxRangeM,
    uint SelectedSkillId,
    uint LastSkillUsed,
    uint IncumbentObjId,
    bool IncumbentValid,
    bool IncumbentUnknown,
    int IncumbentScore,
    bool CommitmentInForce,
    Vector3 IncumbentPosition,
    float IncumbentDistanceM,
    float IncumbentLeashDriftM,
    float LeashBudgetM,
    IReadOnlyList<CombatCandidate> Candidates,
    DateTime NowUtc)
{
    /// <summary>True when the actor carried usable vitals this wake.</summary>
    public bool HasSelfHp => !float.IsNaN(SelfHpRatio);

    /// <summary>True when a hostile was actually perceived this frame.</summary>
    public bool HasEnemy => EnemyCount > 0;

    /// <summary>True when a target is committed and readable this wake.</summary>
    public bool HasUsableIncumbent => IncumbentObjId != 0 && IncumbentValid && !float.IsNaN(IncumbentDistanceM);

    /// <summary>
    /// The survival veto: the actor cannot act — it is down, or its own hp reads
    /// at or below zero. Fail-closed in the other direction: an UNREADABLE hp
    /// (NaN) never fabricates a veto; it only leaves combat to the normal chain.
    /// </summary>
    public bool SurvivalVetoed => !Alive || (HasSelfHp && SelfHpRatio <= 0f);
}

/// <summary>
/// One wake's combat decision: the arm that fired, the verb (with its target /
/// skill / item / destination), the band the wake was evaluated against, the
/// commitment transition, and the named reason — everything a dispatcher and a
/// lane log need, and nothing that executes gameplay itself.
/// </summary>
public readonly record struct CombatBrainDecision(
    CombatArm Arm,
    CombatVerb Verb,
    CombatBrainState State,
    uint TargetObjId,
    uint SkillId,
    uint ItemTemplateId,
    Vector3? Destination,
    float BandMinM,
    float BandMaxM,
    float DistanceM,
    float HpRatio,
    int ThreatValue,
    CombatDisengageReason DisengageReason,
    CombatReason Reason)
{
    /// <summary>The wake's threat verdict (typed accessor over <see cref="ThreatValue"/>).</summary>
    public CombatThreat Threat => (CombatThreat)ThreatValue;

    /// <summary>True when this wake (re)started the commitment — the planner publishes the window from this.</summary>
    public bool CommittedThisWake { get; init; }

    /// <summary>The committed target's ranked score (0 when nothing was ranked).</summary>
    public int TargetScore { get; init; }

    /// <summary>True when this decision asks the actor for a verb (false = the leg withdraws with this decision as evidence).</summary>
    public bool HasVerb => Verb != CombatVerb.Hold;

    /// <summary>True while the retreat owns the engagement (the observable Disengaging fact).</summary>
    public bool IsDisengaging => State == CombatBrainState.Disengaging;

    /// <summary>True while a target is committed and the engagement is live (the observable engaged fact).</summary>
    public bool IsEngaged => State is CombatBrainState.Engaged or CombatBrainState.Holding;

    /// <summary>
    /// Compact, space-free diagnostic token for the wake line. Carries no spaces
    /// or square brackets so the DECIDE bracket the lane funnel parser scans
    /// stays parseable. Built on read only.
    /// </summary>
    public string Describe()
        => $"arm={Arm}:verb={Verb}:state={State}:threat={Threat}" +
           $":band={Fmt(BandMinM)}-{Fmt(BandMaxM)}:distM={Fmt(DistanceM)}:hp={Fmt(HpRatio)}" +
           $":target={TargetObjId}:skill={SkillId}:item={ItemTemplateId}" +
           $":disengage={DisengageReason}:commit={(CommittedThisWake ? "new" : "held")}:reason={Reason}";

    private static string Fmt(float value)
        => float.IsNaN(value) ? "NA" : value.ToString("F2", CultureInfo.InvariantCulture);
}
