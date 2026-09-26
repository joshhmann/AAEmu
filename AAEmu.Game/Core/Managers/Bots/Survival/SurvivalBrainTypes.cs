#nullable enable

using System.Globalization;
using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Survival;

/// <summary>
/// The survival layer's verdict for ONE wake — the layer's own vocabulary, three
/// members and no more:
///  - <see cref="Hold"/> — this layer asks nothing of the actor (healthy, or in a
///    fight the combat brain owns, or unable to act at all);
///  - <see cref="Flee"/> — a fight is on and the actor's hp is inside the flee
///    band (or the combat brain already published a retreat): break contact along
///    the shared safe anchor;
///  - <see cref="Recover"/> — the actor is wounded with NO fight on: a recovery
///    concern, deliberately NOT a veto (the existing out-of-combat recovery
///    module owns the act).
///
/// There is deliberately no <c>Failed</c> member and no "unknown" member: every
/// wake resolves to one of these three with a named <see cref="SurvivalReason"/>,
/// and an unreadable frame degrades to <see cref="Hold"/> with
/// <see cref="SurvivalReason.VitalsUnreadable"/> — never to a fabricated verdict.
/// </summary>
public enum SurvivalVerdict
{
    /// <summary>Nothing for the survival layer to do; whether the veto stands is the decision's own <c>Veto</c> fact.</summary>
    Hold = 0,

    /// <summary>Break contact: the actor is fighting and its hp is critical (or the combat brain already broke contact).</summary>
    Flee = 1,

    /// <summary>Wounded with no fight on — a recovery demand, never a veto and never a second recovery implementation.</summary>
    Recover = 2
}

/// <summary>
/// The actor-face verb a survival decision asks for — the EXISTING
/// <c>IGameplayActor</c> verbs only, and only the one this increment can honestly
/// ask for: <c>Move</c> (the flee leg, to the shared safe anchor). <see cref="Hold"/>
/// asks for nothing, which is how every non-flee verdict reports itself.
///
/// <see cref="Recover"/> deliberately carries no verb: recovery execution is the
/// existing <c>OutOfCombatRecoveryModule</c>'s (consumable use + sit), and naming a
/// verb here would be a second recovery path. This layer names the DEMAND, not the act.
/// </summary>
public enum SurvivalVerb
{
    /// <summary>No actor call this wake.</summary>
    Hold = 0,

    /// <summary>A destination Move: the flee anchor opposite the threat.</summary>
    Move = 1
}

/// <summary>
/// The named reason a verdict was reached — an ENUM rather than a built string, so
/// a per-wake decision allocates nothing (the same discipline <c>CombatReason</c>
/// and <c>LootReason</c> follow); <see cref="SurvivalBrain.Token"/> maps each member
/// onto the space-free token a lane line can carry. Every member names its own
/// cause, so no verdict is ever a bare fail.
/// </summary>
public enum SurvivalReason
{
    /// <summary>No reason recorded.</summary>
    None = 0,

    /// <summary>Vitals readable, above the recover line, and no fight on — nothing to do and nothing to veto.</summary>
    Healthy,

    /// <summary>Vitals readable and non-critical, but a fight is on: the combat brain owns this wake (no new veto).</summary>
    FightOwnsTheWake,

    /// <summary>Vitals readable and at or below zero: the actor cannot act, so no other leg may take the wake (the veto stands).</summary>
    Incapacitated,

    /// <summary>Vitals could not be read (unknown maximum) — no verdict is claimed and NO veto is fabricated.</summary>
    VitalsUnreadable,

    /// <summary>The combat brain has already published a retreat (its own Disengaging fact): the flee is that fact, carried forward.</summary>
    CombatRetreat,

    /// <summary>Own hp at or below the flee line with evidence of a fight (a live commitment, or a selected target).</summary>
    HpCritical,

    /// <summary>Own hp at or below the flee line with NO fight evidence — a recovery concern, never a veto.</summary>
    OutOfCombatCritical,

    /// <summary>Own hp below the recover line with no fight on — the out-of-combat recovery demand.</summary>
    OutOfCombatWounded
}

/// <summary>
/// Every input the survival decision chain reads for one wake — vitals and the
/// engagement facts, and nothing else.
///
/// Value type: the live adapter (<see cref="SurvivalBrainPlanner"/>) builds it from
/// the wake's OWN frozen observation plus the combat layer's published facts, and
/// the brain never stores it. There is no world handle, no clock, no perception
/// type and no actor: the same inputs always decide the same wake.
///
/// Honesty contract (the decision layer's own fail-closed rule):
///  - a ratio that could not be established is NaN, never 0 — a fabricated 0 would
///    read as "down" (a fabricated veto) and a fabricated 1 as "healthy";
///  - a zero maximum therefore produces <see cref="HasVitals"/> false, so an
///    unreadable frame can never fabricate a veto;
///  - fight evidence is exactly two positive facts — a live commitment
///    (<see cref="HasLiveCommitment"/>) or a selected target
///    (<see cref="HasSelectedTarget"/>) — never an ambient "there might be mobs".
/// </summary>
public readonly record struct SurvivalBrainInputs(
    uint ActorObjId,
    float SelfHpRatio,
    bool CombatRetreatPublished,
    uint CommittedTargetObjId,
    uint SelectedTargetObjId,
    Vector3 SelfPosition,
    Vector3 ThreatPosition)
{
    /// <summary>True when the actor carried usable vitals this wake (a readable maximum and ratio).</summary>
    public bool HasVitals => !float.IsNaN(SelfHpRatio) && !float.IsInfinity(SelfHpRatio);

    /// <summary>True when the actor cannot act: readable vitals at or below zero.</summary>
    public bool IsIncapacitated => HasVitals && SelfHpRatio <= 0f;

    /// <summary>True when the combat layer holds a live commitment for this actor.</summary>
    public bool HasLiveCommitment => CommittedTargetObjId != 0;

    /// <summary>True when the actor has a selected target.</summary>
    public bool HasSelectedTarget => SelectedTargetObjId != 0;

    /// <summary>
    /// THE FIGHT GATE: evidence that a fight is actually on. A low hp bar out of
    /// combat is a recovery concern for the existing recovery module, never a
    /// survival veto over the quest legs.
    /// </summary>
    public bool HasFightEvidence => HasLiveCommitment || HasSelectedTarget;

    /// <summary>The threat the flee anchor is measured against: the commitment if there is one, otherwise the selection (0 = none).</summary>
    public uint ThreatObjId => HasLiveCommitment ? CommittedTargetObjId : HasSelectedTarget ? SelectedTargetObjId : 0u;
}

/// <summary>
/// One wake's survival decision: the verdict, the named reason, the verb (with the
/// flee destination when there is one), the threat the flee is measured against,
/// and the veto FACT — everything a consumer and a lane log need, and nothing that
/// executes gameplay itself.
///
/// The veto is a first-class property rather than a fourth verdict member so the
/// three-verdict vocabulary stays exactly as specified: the <see cref="Hold"/>
/// verdict with <see cref="SurvivalReason.Incapacitated"/> is the one hold that
/// still vetoes (a down actor asks for no action AND no leg may take its wake).
/// </summary>
public readonly record struct SurvivalBrainDecision(
    SurvivalVerdict Verdict,
    SurvivalReason Reason,
    SurvivalVerb Verb,
    uint ThreatObjId,
    Vector3? Destination,
    float HpRatio,
    bool FightEvidence)
{
    /// <summary>THE PUBLISHED FACT: true while a survival condition owns the actor's wake.</summary>
    public bool Veto => Verdict == SurvivalVerdict.Flee || Reason == SurvivalReason.Incapacitated;

    /// <summary>True when this decision asks the actor for a verb (only <see cref="SurvivalVerdict.Flee"/> does).</summary>
    public bool HasVerb => Verb != SurvivalVerb.Hold;

    /// <summary>True while the flee owns the wake (the observable breaking-contact verdict).</summary>
    public bool IsFlee => Verdict == SurvivalVerdict.Flee;

    /// <summary>True while the out-of-combat recovery demand owns the wake (never a veto, never a dispatch).</summary>
    public bool IsRecover => Verdict == SurvivalVerdict.Recover;

    /// <summary>
    /// Compact, space-free diagnostic token for the wake line: no spaces and no
    /// square brackets, so a lane parser scanning a DECIDE bracket keeps working.
    /// Built on read only.
    /// </summary>
    public string Describe()
        => $"verdict={Verdict}:reason={SurvivalBrain.Token(Reason)}:verb={Verb}" +
           $":veto={(Veto ? "true" : "false")}:fight={(FightEvidence ? "true" : "false")}" +
           $":hp={Fmt(HpRatio)}:threat={ThreatObjId}";

    private static string Fmt(float value)
        => float.IsNaN(value) ? "NA" : value.ToString("F2", CultureInfo.InvariantCulture);
}
