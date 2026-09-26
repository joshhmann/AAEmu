#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.Game.Core.Managers.Bots.Survival;

/// <summary>
/// The SURVIVAL BRAIN — one wake's survival verdict for one actor, derived in a
/// fixed chain from an immutable <see cref="SurvivalBrainInputs"/>: the
/// incapacitated hold, the combat brain's already-published retreat, the
/// unreadable-vitals hold, the critical-hp flee, and the two out-of-combat
/// recovery demands.
///
/// Scope (binding for this increment):
///  - PURE: no engine query, no world scan, no mutation, no clock read, no actor,
///    no perception type — every number and fact arrives on the inputs, and the
///    same inputs always decide the same wake. The live reads (the wake's frozen
///    observation, the combat layer's published engagement facts) live in
///    <see cref="SurvivalBrainPlanner"/>; the published veto lives in
///    <see cref="SurvivalVetoState"/>;
///  - VERBS ONLY, AND ONLY EXISTING ONES: the one verb this increment can ask for
///    is the EXISTING <c>IGameplayActor.Move</c>, to the shared retreat anchor. No
///    new verb is invented, no priority is changed, and the recovery verdict
///    deliberately carries no verb at all — recovery execution stays the existing
///    <c>OutOfCombatRecoveryModule</c>'s;
///  - FAIL-CLOSED: unreadable vitals (an unknown maximum) claim no verdict and
///    fabricate NO veto; a low hp bar with no fight evidence is a recovery demand,
///    never a veto over the quest legs; a fight with non-critical hp leaves the
///    wake where it already belongs — the combat brain;
///  - ONE FLEE MATH: the flee destination is
///    <see cref="TravelBrain.SafeAnchor"/> — the same single implementation
///    <c>CombatBrain</c>'s flee leg and <c>TravelBrainPlanner</c>'s retreat helper
///    both use, never a second copy of the escape ray.
///
/// The chain, in evaluation order (the first arm that applies owns the wake):
///   1. INCAPACITATED — readable vitals at or below zero → hold, veto stands;
///   2. COMBAT RETREAT — the combat brain's published Disengaging fact → flee
///                        (that fact IS the retreat; this layer carries it forward
///                        so a consumer reads one veto, not two rules);
///   3. UNREADABLE     — no readable vitals → hold, NO veto fabricated;
///   4. CRITICAL FLEE  — hp at or below the flee line WITH fight evidence → flee;
///   5. RECOVER        — hp at or below the flee line with no fight on → recover
///                        (never a veto), and the same below the recover line;
///   6. HOLD           — a fight owns a non-critical wake, or nothing is wrong.
/// </summary>
public static class SurvivalBrain
{
    /// <summary>
    /// Flee threshold (quoted from the LIVE flee arm — <see cref="CombatBrain.FleeHpThreshold"/>,
    /// itself engine-mirrored from <c>CombatDecisionTree.DefaultEmergencyFleeHpPercent</c>);
    /// flee fires AT or below it. A const fold, deliberately: one number, two arms.
    /// </summary>
    public const float FleeHpThreshold = CombatBrain.FleeHpThreshold;

    /// <summary>
    /// Recover line (quoted from the existing out-of-combat recovery module's own
    /// trigger — <c>OutOfCombatRecoveryModule.DefaultHpTriggerFraction</c>), so the
    /// survival layer's recovery DEMAND and the module that acts on it agree by
    /// construction. Recover fires STRICTLY below it.
    /// </summary>
    public const float RecoverHpThreshold = AAEmu.Game.Core.Managers.Bots.OutOfCombatRecoveryModule.DefaultHpTriggerFraction;

    /// <summary>
    /// Flee leg length (quoted from the shared anchor's own distance —
    /// <see cref="TravelBrain.RetreatAnchorDistanceM"/>, engine precedent 25 m), so
    /// this flee leg and the combat/travel retreat legs are the same shape by
    /// construction rather than by coincidence.
    /// </summary>
    public const float RetreatDistanceM = TravelBrain.RetreatAnchorDistanceM;

    /// <summary>The space-free token for a verdict, for the lane line and for unit tests that pin the vocabulary.</summary>
    public static string Token(SurvivalVerdict verdict) => verdict switch
    {
        SurvivalVerdict.Flee => "flee",
        SurvivalVerdict.Recover => "recover",
        _ => "hold"
    };

    /// <summary>The space-free token for a named reason. Every reason names its own cause; there is no bare "failed".</summary>
    public static string Token(SurvivalReason reason) => reason switch
    {
        SurvivalReason.Healthy => "healthy",
        SurvivalReason.FightOwnsTheWake => "fight-live",
        SurvivalReason.Incapacitated => "incapacitated",
        SurvivalReason.VitalsUnreadable => "vitals-unreadable",
        SurvivalReason.CombatRetreat => "combat-retreat",
        SurvivalReason.HpCritical => "hp-critical",
        SurvivalReason.OutOfCombatCritical => "out-of-combat-critical",
        SurvivalReason.OutOfCombatWounded => "out-of-combat-wounded",
        _ => "none"
    };

    /// <summary>
    /// Evaluates the wake's decision. Pure over the inputs: the caller owns the
    /// observation, the combat facts, and every world read that produced them.
    /// </summary>
    public static SurvivalBrainDecision Decide(in SurvivalBrainInputs inputs)
    {
        // ------------------------------------------------ 1. INCAPACITATED
        // Readable vitals at or below zero: the actor cannot act, so the wake is
        // not any leg's to take. This verdict asks for nothing (the engine's death
        // path owns the actor) and is the ONE hold that still vetoes.
        if (inputs.IsIncapacitated)
        {
            return new SurvivalBrainDecision(
                SurvivalVerdict.Hold, SurvivalReason.Incapacitated, SurvivalVerb.Hold,
                0, null, inputs.SelfHpRatio, inputs.HasFightEvidence);
        }

        // ------------------------------------------------ 2. COMBAT RETREAT
        // The combat brain already broke contact (its published Disengaging fact,
        // which its own chain reaches on critical hp, a critical threat with no
        // escape tool, or a leash edge). That fact IS the retreat: this layer does
        // not re-derive the combat cause, it carries the fact forward so one veto
        // rule serves both consumers. It precedes the vitals read deliberately —
        // the published fact is authoritative on its own, exactly as it is for the
        // quest legs' existing veto.
        if (inputs.CombatRetreatPublished)
            return Flee(SurvivalReason.CombatRetreat, inputs);

        // ------------------------------------------------ 3. UNREADABLE VITALS
        // The frame could not be read (unknown maximum). NO verdict is claimed and
        // NO veto is fabricated: a fabricated veto would stand every quest leg down
        // on a frame nobody could read, and a fabricated "healthy" would pretend a
        // comparison was made. Hold, named.
        if (!inputs.HasVitals)
        {
            return new SurvivalBrainDecision(
                SurvivalVerdict.Hold, SurvivalReason.VitalsUnreadable, SurvivalVerb.Hold,
                inputs.ThreatObjId, null, inputs.SelfHpRatio, inputs.HasFightEvidence);
        }

        // ------------------------------------------------ 4. CRITICAL HP IN A FIGHT
        // The flee arm, gated on evidence that a fight is ON (a live commitment, or
        // a selected target). Without that gate every wounded bot walking home would
        // veto its own quest legs.
        if (inputs.SelfHpRatio <= FleeHpThreshold && inputs.HasFightEvidence)
            return Flee(SurvivalReason.HpCritical, inputs);

        // ------------------------------------------------ 5. RECOVER
        // At or below the flee line with NO fight on: a recovery concern, never a
        // veto. The verdict names the demand; the existing recovery module acts.
        if (inputs.SelfHpRatio <= FleeHpThreshold)
            return Recover(SurvivalReason.OutOfCombatCritical, inputs);

        // Below the recover line with no fight on: the same demand, less urgent.
        if (inputs.SelfHpRatio < RecoverHpThreshold && !inputs.HasFightEvidence)
            return Recover(SurvivalReason.OutOfCombatWounded, inputs);

        // ------------------------------------------------ 6. HOLD
        // A fight with non-critical hp leaves the wake with the combat brain (its
        // heal/disengage arms own it) — and adds no veto the frozen behaviour did
        // not already have. Otherwise the actor is simply fine.
        return new SurvivalBrainDecision(
            SurvivalVerdict.Hold,
            inputs.HasFightEvidence ? SurvivalReason.FightOwnsTheWake : SurvivalReason.Healthy,
            SurvivalVerb.Hold, inputs.ThreatObjId, null, inputs.SelfHpRatio, inputs.HasFightEvidence);
    }

    /// <summary>
    /// The wake's flee decision: the shared safe anchor, one step opposite the
    /// threat. The escape math itself lives in exactly one place —
    /// <see cref="TravelBrain.SafeAnchor"/> — so this flee leg, <c>CombatBrain</c>'s
    /// retreat <c>Move</c>, and the travel layer's retreat helper are the same shape
    /// by construction. Only the distance is this brain's own
    /// (<see cref="RetreatDistanceM"/>).
    ///
    /// An unread (zero) threat position is NOT a fabricated threat: the anchor's own
    /// documented fail-closed path still defines an escape direction (a fixed
    /// reference one meter west), so the verb never receives a NaN destination.
    /// </summary>
    private static SurvivalBrainDecision Flee(SurvivalReason reason, in SurvivalBrainInputs inputs)
        => new(
            SurvivalVerdict.Flee,
            reason,
            SurvivalVerb.Move,
            inputs.ThreatObjId,
            TravelBrain.SafeAnchor(inputs.SelfPosition, inputs.ThreatPosition, RetreatDistanceM),
            inputs.SelfHpRatio,
            inputs.HasFightEvidence);

    /// <summary>
    /// The wake's recovery decision: a DEMAND, never a dispatch and never a veto.
    /// It carries no verb because recovery execution belongs to the existing
    /// out-of-combat recovery module — this layer names that the demand exists.
    /// </summary>
    private static SurvivalBrainDecision Recover(SurvivalReason reason, in SurvivalBrainInputs inputs)
        => new(
            SurvivalVerdict.Recover, reason, SurvivalVerb.Hold,
            inputs.ThreatObjId, null, inputs.SelfHpRatio, inputs.HasFightEvidence);
}
