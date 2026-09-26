#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Combat;

/// <summary>
/// The COMBAT BRAIN — one wake's combat decision for one actor, derived in a
/// fixed chain from an immutable <see cref="CombatBrainInputs"/>: a survival
/// veto, the disengage break, the crowd-control hold, target commitment (with
/// hysteresis), validity dropping, the heal threshold, band management, the
/// opener/rotation skill choice, and the sustained auto-attack hold.
///
/// Scope (binding for this increment):
///  - PURE: no engine query, no world scan, no mutation, no clock read — every
///    number arrives on the inputs, and the same inputs always decide the same
///    wake. The stateful half lives in <see cref="CombatBrainEngagement"/>, read
///    by the caller and carried in as inputs;
///  - VERBS ONLY: the decision names a verb and its arguments
///    (<c>IGameplayActor</c> Move/Cast/UseItem/AutoAttack). It never executes,
///    never writes a Character field, never opens a second gameplay path. The
///    ordered <c>StopAutoAttack</c>+<c>Stop</c> teardown a disengage needs is
///    composed by the dispatcher from public verbs, exactly as the existing
///    pursuit dispatch composes <c>PreemptCurrent</c>+<c>MoveToUnit</c>;
///  - FAIL-CLOSED: an unreadable hp is NaN and never triggers the heal or the
///    flee arm; an unreadable distance is NaN and never reads as "in band"; a
///    target whose truth could not be established is never a candidate.
///
/// The chain, in evaluation order (the first arm that applies owns the wake):
///   1. SURVIVAL VETO      — the actor cannot act at all → publish Vetoed, hold;
///   2. DISENGAGE          — hp at/below the flee band, critical threat with no
///                           escape tool, or the target at its leash edge →
///                           break contact (retreat Move; the dispatcher tears the
///                           loop and any live leg down first), publish Disengaging;
///   3. CROWD-CONTROL HOLD — under CC: no move, no cast. Held, not broken: the
///                           CC-break consumable taxonomy is a later increment;
///   4. COMMIT             — no commitment, or the incumbent is provably gone →
///                           rank the candidates and take the winner (an
///                           incumbent that is still present keeps its window);
///   5. INVALID DROP       — the committed target could not be read this frame →
///                           hold the commitment, never drop it;
///   6. HEAL               — hp strictly below the heal threshold and a usable
///                           consumable exists → use it;
///   7. BAND               — outside the desired band → close in / back off;
///   8. OPENER             — the engagement's first legal skill;
///   9. ROTATION           — a follow-up skill that continues the current one;
///  10. SUSTAIN / HOLD     — no skill legal: keep the auto-attack loop alive, or
///                           hold when it is already live.
/// </summary>
public static class CombatBrain
{
    /// <summary>
    /// The aggro band the scorer reads: the same flat radius the perception
    /// census uses (<c>BotRadarProjection.PerceivedRadiusM</c>, 25 m). Quoted as
    /// a literal because the decision paths may not reference the perception
    /// stack's types, and the value is pinned by the perception tests.
    /// </summary>
    public const float AggroBandM = 25f;

    /// <summary>Melee band floor (engine-mirrored: <see cref="CombatDecisionTree.DefaultMeleeMin"/>).</summary>
    public const float MeleeMinM = CombatDecisionTree.DefaultMeleeMin;

    /// <summary>Melee band ceiling (engine-mirrored: <see cref="CombatDecisionTree.DefaultMeleeMax"/>).</summary>
    public const float MeleeMaxM = CombatDecisionTree.DefaultMeleeMax;

    /// <summary>Ranged-physical band floor (engine-mirrored: <see cref="CombatDecisionTree.DefaultRangedMin"/>).</summary>
    public const float RangedPhysicalMinM = CombatDecisionTree.DefaultRangedMin;

    /// <summary>Ranged-physical band ceiling (engine-mirrored: <see cref="CombatDecisionTree.DefaultRangedMax"/>).</summary>
    public const float RangedPhysicalMaxM = CombatDecisionTree.DefaultRangedMax;

    /// <summary>Ranged-magic band floor: a caster needs more room than an archer, hence above the shared ranged floor.</summary>
    public const float RangedMagicMinM = 14.0f;

    /// <summary>Ranged-magic band ceiling (engine-mirrored: <see cref="CombatDecisionTree.DefaultRangedMax"/>).</summary>
    public const float RangedMagicMaxM = CombatDecisionTree.DefaultRangedMax;

    /// <summary>Healer band floor: outside the melee scrum, inside casting reach.</summary>
    public const float HealerMinM = 10.0f;

    /// <summary>Healer band ceiling: inside the short support casts (<c>CombatDecisionTree.GetKnownSkillRange</c>).</summary>
    public const float HealerMaxM = 20.0f;

    /// <summary>Heal threshold (engine-mirrored); heal fires STRICTLY below it.</summary>
    public const float HealHpThreshold = CombatDecisionTree.DefaultDefensiveHealHpPercent;

    /// <summary>Flee threshold (engine-mirrored); flee fires at or below it.</summary>
    public const float FleeHpThreshold = CombatDecisionTree.DefaultEmergencyFleeHpPercent;

    /// <summary>Enemy count at or above which the actor is outnumbered (mirrors the belief layer's escalation rule).</summary>
    public const int OutnumberedEnemyCount = 2;

    /// <summary>Contact range: inside this the observer is being hit (engine melee reach).</summary>
    public const float ContactRangeM = CombatDecisionTree.DefaultMeleeMax;

    /// <summary>Retreat leg length (engine precedent: <c>CombatDecisionTree.Evaluate</c>'s emergency flee steps 25 m).</summary>
    public const float RetreatDistanceM = 25.0f;

    /// <summary>Close-in destination: land this far inside the band's ceiling so the next wake is already in band.</summary>
    public const float CloseInMarginM = 1.0f;

    /// <summary>The band the role wants, before the learned skill's own range is intersected in.</summary>
    public static (float Min, float Max) RoleBand(CombatRole role) => role switch
    {
        CombatRole.RangedPhysical => (RangedPhysicalMinM, RangedPhysicalMaxM),
        CombatRole.RangedMagic => (RangedMagicMinM, RangedMagicMaxM),
        CombatRole.HealerSupport => (HealerMinM, HealerMaxM),
        _ => (MeleeMinM, MeleeMaxM)
    };

    /// <summary>
    /// The DESIRED BAND: the role's band intersected with the learned skill's own
    /// range when the caller could read one. A skill range of 0/0 (or NaN) means
    /// "not established", so the role band stands alone — an unreadable skill
    /// never narrows the band to nothing.
    ///
    /// The intersection is fail-closed in one direction only: when the two
    /// disagree outright (a melee band against a 20 m-only skill) the SKILL wins,
    /// so the band stays a legal place to stand rather than collapsing to an
    /// empty interval the bot could never satisfy.
    /// </summary>
    public static (float Min, float Max) DesiredBand(CombatRole role, float skillMinRangeM, float skillMaxRangeM)
    {
        var (roleMin, roleMax) = RoleBand(role);
        if (skillMaxRangeM <= 0f || float.IsNaN(skillMaxRangeM))
            return (roleMin, roleMax);

        var skillMin = skillMinRangeM > 0f && !float.IsNaN(skillMinRangeM) ? skillMinRangeM : 0f;
        var max = Math.Min(roleMax, skillMaxRangeM);
        var min = Math.Max(roleMin, skillMin);
        if (min > max)
        {
            min = skillMin;
            max = Math.Max(skillMin, skillMaxRangeM);
        }
        return (min, max);
    }

    /// <summary>
    /// The wake's threat verdict, derived from the decision layer's own
    /// primitives (never the belief stack's <c>ThreatLevel</c>).
    ///
    /// Escalation degrades DOWNWARD on unreadable input: no hostile at all is
    /// <see cref="CombatThreat.None"/> regardless of hp (a low bar with nothing
    /// attacking is a recovery problem, not a combat one), and an unreadable hp
    /// ratio can never reach <see cref="CombatThreat.Critical"/>.
    /// </summary>
    public static CombatThreat DeriveThreat(in CombatBrainInputs inputs)
    {
        if (!inputs.HasEnemy)
            return CombatThreat.None;

        if (inputs.HasSelfHp && inputs.SelfHpRatio <= FleeHpThreshold)
            return CombatThreat.Critical;

        if ((!float.IsNaN(inputs.NearestEnemyDistanceM) && inputs.NearestEnemyDistanceM <= ContactRangeM)
            || inputs.TookDamageThisFrame
            || inputs.EnemyCount >= OutnumberedEnemyCount
            || (inputs.HasSelfHp && inputs.SelfHpRatio <= HealHpThreshold))
            return CombatThreat.Engaged;

        return CombatThreat.Ambient;
    }

    /// <summary>
    /// The disengage arm's own predicate, evaluated BEFORE the crowd-control hold
    /// so a CC'd bot at critical hp still asks for the escape (movement under a
    /// root is refused by the engine's own gates rather than by this chain, and
    /// swallowing the retreat here would guarantee a death the engine would have
    /// let it escape).
    ///
    /// The three named causes:
    ///  - <see cref="CombatDisengageReason.CriticalHp"/>: own hp at or below the
    ///    flee threshold with a hostile present;
    ///  - <see cref="CombatDisengageReason.CriticalNoEscape"/>: the threat reads
    ///    critical and no crowd-control/escape tool is ready — nothing to answer
    ///    with, so leave;
    ///  - <see cref="CombatDisengageReason.LeashEdge"/>: the committed target has
    ///    drifted past its leash edge and is about to reset home, taking the fight
    ///    with it.
    ///
    /// Every arm is gated on a hostile actually being present: a low hp bar out of
    /// combat is a recovery problem for the survival layer, never this brain's
    /// retreat.
    /// </summary>
    public static CombatDisengageReason EvaluateDisengage(in CombatBrainInputs inputs)
    {
        if (!inputs.HasEnemy)
            return CombatDisengageReason.None;

        if (inputs.HasSelfHp && inputs.SelfHpRatio <= FleeHpThreshold)
            return CombatDisengageReason.CriticalHp;

        if (DeriveThreat(inputs) == CombatThreat.Critical && !inputs.CcAvailable)
            return CombatDisengageReason.CriticalNoEscape;

        if (inputs.IncumbentObjId != 0 && inputs.IncumbentValid && inputs.LeashBudgetM > 0f
            && !float.IsNaN(inputs.IncumbentLeashDriftM)
            && inputs.IncumbentLeashDriftM >= inputs.LeashBudgetM * CombatBrainEngagement.LeashEdgeFraction)
            return CombatDisengageReason.LeashEdge;

        return CombatDisengageReason.None;
    }

    /// <summary>
    /// Evaluates the wake's decision. Pure over the inputs: the caller owns the
    /// engagement memory, the clock, and every world read that produced them.
    /// </summary>
    public static CombatBrainDecision Decide(in CombatBrainInputs inputs)
    {
        var (bandMin, bandMax) = DesiredBand(inputs.Role, inputs.SkillMinRangeM, inputs.SkillMaxRangeM);
        var threat = DeriveThreat(inputs);

        // ------------------------------------------------ 1. SURVIVAL VETO
        if (inputs.SurvivalVetoed)
        {
            return Withdraw(
                CombatArm.SurvivalVeto, CombatBrainState.Vetoed, CombatReason.SurvivalVetoed,
                inputs, bandMin, bandMax, inputs.IncumbentObjId, threat);
        }

        // ------------------------------------------------ 2. DISENGAGE
        var disengage = EvaluateDisengage(inputs);
        if (disengage != CombatDisengageReason.None)
        {
            return new CombatBrainDecision(
                CombatArm.Disengage, CombatVerb.Move, CombatBrainState.Disengaging,
                inputs.IncumbentObjId, 0, 0, RetreatDestination(inputs), bandMin, bandMax,
                inputs.IncumbentDistanceM, inputs.SelfHpRatio, (int)threat, disengage,
                disengage switch
                {
                    CombatDisengageReason.CriticalHp => CombatReason.HpCritical,
                    CombatDisengageReason.CriticalNoEscape => CombatReason.CriticalNoEscape,
                    _ => CombatReason.LeashEdge
                });
        }

        // ------------------------------------------------ 3. CROWD-CONTROL HOLD
        if (inputs.CrowdControlled)
        {
            return Withdraw(
                CombatArm.CrowdControl, CombatBrainState.Holding, CombatReason.CrowdControlled,
                inputs, bandMin, bandMax, inputs.IncumbentObjId, threat);
        }

        // ------------------------------------------------ 4./5. TARGET
        var targetObjId = inputs.IncumbentObjId;
        var targetDistance = inputs.IncumbentDistanceM;
        var targetScore = inputs.IncumbentScore;
        var committedThisWake = false;

        if (targetObjId != 0 && inputs.IncumbentUnknown)
        {
            // The frame could not read our own target: hold the commitment. An
            // unreadable frame must never cost a live engagement.
            return Withdraw(
                CombatArm.InvalidDrop, CombatBrainState.Holding, CombatReason.IncumbentUnknown,
                inputs, bandMin, bandMax, targetObjId, threat);
        }

        // The ranked competition runs when the incumbent is provably gone, and —
        // once the commitment window has elapsed — against a still-live incumbent
        // ONLY when the wake actually carries a candidate set to compete with.
        // A wake with no census (the quest path's committed target) has nothing to
        // rank against, so the incumbent is kept: an absent census is never
        // evidence that a better target exists.
        var hadIncumbent = targetObjId != 0;
        var incumbentLegal = hadIncumbent && inputs.IncumbentValid;
        var shouldCompete = !incumbentLegal
            || (!inputs.CommitmentInForce && inputs.Candidates.Count > 0);
        if (shouldCompete)
        {
            if (!TryCommit(inputs, incumbentLegal, out var chosen, out var chosenScore))
            {
                // Nothing to act on. A commitment that was PROVABLY gone is
                // reported as dropped (target 0) so the caller clears it; an idle
                // wake with no commitment keeps its own (also 0) target.
                return Withdraw(
                    hadIncumbent ? CombatArm.InvalidDrop : CombatArm.Commit,
                    CombatBrainState.Idle,
                    hadIncumbent ? CombatReason.TargetGone : CombatReason.NoCandidate,
                    inputs, bandMin, bandMax, hadIncumbent ? 0u : targetObjId, threat);
            }
            var changed = chosen.ObjId != inputs.IncumbentObjId;
            targetObjId = chosen.ObjId;
            targetDistance = chosen.DistanceM;
            targetScore = chosenScore;
            committedThisWake = changed;
        }

        // ------------------------------------------------ 6. HEAL
        if (inputs.HasSelfHp && inputs.SelfHpRatio < HealHpThreshold && inputs.HealItemTemplateId != 0)
        {
            return new CombatBrainDecision(
                CombatArm.Heal, CombatVerb.UseItem, CombatBrainState.Engaged,
                targetObjId, 0, inputs.HealItemTemplateId, null, bandMin, bandMax,
                targetDistance, inputs.SelfHpRatio, (int)threat, CombatDisengageReason.None,
                CombatReason.Healed)
            { CommittedThisWake = committedThisWake, TargetScore = targetScore };
        }

        // ------------------------------------------------ 7. BAND
        if (!float.IsNaN(targetDistance) && targetDistance > bandMax)
        {
            return new CombatBrainDecision(
                CombatArm.CloseRange, CombatVerb.Move, CombatBrainState.Engaged,
                targetObjId, 0, 0, ApproachDestination(inputs, bandMax), bandMin, bandMax,
                targetDistance, inputs.SelfHpRatio, (int)threat, CombatDisengageReason.None,
                CombatReason.CloseIn)
            { CommittedThisWake = committedThisWake, TargetScore = targetScore };
        }
        // Backing off is a SPACING arm, so it belongs to the roles that lose their
        // verb by being crowded: a ranged archer/caster inside its floor cannot
        // shoot, while a melee fighter inside its floor is exactly where it wants
        // to be (the floor exists so the close-in arm does not jitter at contact,
        // not to push a melee bot out of its own reach). This mirrors the engine
        // tree, which kites only for the ranged roles.
        if (inputs.Role != CombatRole.Melee
            && !float.IsNaN(targetDistance) && targetDistance < bandMin)
        {
            return new CombatBrainDecision(
                CombatArm.BackOff, CombatVerb.Move, CombatBrainState.Engaged,
                targetObjId, 0, 0, RetreatDestination(inputs), bandMin, bandMax,
                targetDistance, inputs.SelfHpRatio, (int)threat, CombatDisengageReason.None,
                CombatReason.BackOff)
            { CommittedThisWake = committedThisWake, TargetScore = targetScore };
        }

        // ------------------------------------------------ 8./9. SKILL
        if (inputs.SelectedSkillId != 0)
        {
            var isOpener = inputs.LastSkillUsed == 0;
            return new CombatBrainDecision(
                isOpener ? CombatArm.Opener : CombatArm.Rotation,
                CombatVerb.Cast, CombatBrainState.Engaged,
                targetObjId, inputs.SelectedSkillId, 0, null, bandMin, bandMax,
                targetDistance, inputs.SelfHpRatio, (int)threat, CombatDisengageReason.None,
                isOpener ? CombatReason.Opener : CombatReason.Rotation)
            { CommittedThisWake = committedThisWake, TargetScore = targetScore };
        }

        // ------------------------------------------------ 10. SUSTAIN / HOLD
        if (!inputs.IsAutoAttackLive)
        {
            return new CombatBrainDecision(
                CombatArm.Sustain, CombatVerb.AutoAttack, CombatBrainState.Engaged,
                targetObjId, 0, 0, null, bandMin, bandMax,
                targetDistance, inputs.SelfHpRatio, (int)threat, CombatDisengageReason.None,
                CombatReason.Sustain)
            { CommittedThisWake = committedThisWake, TargetScore = targetScore };
        }

        return new CombatBrainDecision(
            CombatArm.Hold, CombatVerb.Hold, CombatBrainState.Holding,
            targetObjId, 0, 0, null, bandMin, bandMax,
            targetDistance, inputs.SelfHpRatio, (int)threat, CombatDisengageReason.None,
            CombatReason.LoopLive)
        { CommittedThisWake = committedThisWake, TargetScore = targetScore };
    }

    /// <summary>
    /// Ranks the candidates and takes the winner — the commit arm's body.
    ///
    /// The incumbent's window is honoured through the SCORER (which is handed the
    /// incumbent's objId, score, legality and the elapsed value), not
    /// re-implemented here.
    /// </summary>
    private static bool TryCommit(
        in CombatBrainInputs inputs, bool incumbentLegal, out CombatCandidate selected, out int score)
    {
        // Inside the window the incumbent cannot be displaced; outside it the
        // window is reported elapsed so the scorer applies the margin rule. The
        // scorer is the single authority on both arms.
        var elapsed = inputs.CommitmentInForce ? 0 : double.MaxValue;
        return CombatTargetScorer.TrySelect(
            inputs.Candidates,
            inputs.IncumbentObjId,
            incumbentLegal,
            inputs.IncumbentScore,
            elapsed,
            inputs.SelfLevel,
            AggroBandM,
            out selected,
            out score,
            out _);
    }

    /// <summary>Builds a verb-less decision (the leg withdraws with it as evidence).</summary>
    private static CombatBrainDecision Withdraw(
        CombatArm arm, CombatBrainState state, CombatReason reason, in CombatBrainInputs inputs,
        float bandMin, float bandMax, uint targetObjId, CombatThreat threat)
        => new(arm, CombatVerb.Hold, state, targetObjId, 0, 0, null, bandMin, bandMax,
            inputs.IncumbentDistanceM, inputs.SelfHpRatio, (int)threat,
            CombatDisengageReason.None, reason);

    /// <summary>
    /// The retreat destination: straight away from the committed target, or the
    /// engine precedent's fixed step when nothing is readable (an unreadable
    /// position must not produce a NaN destination the Move verb would refuse).
    ///
    /// The escape math itself lives in exactly one place —
    /// <see cref="AAEmu.Game.Core.Managers.Bots.Travel.TravelBrain.SafeAnchor"/> —
    /// so this flee leg and the TRAVEL brain's retreat leg (which the disengage/kite
    /// consumers dispatch) are the same shape by construction rather than by
    /// coincidence. Only the distance is this brain's own
    /// (<see cref="RetreatDistanceM"/>).
    /// </summary>
    private static Vector3 RetreatDestination(in CombatBrainInputs inputs)
        => AAEmu.Game.Core.Managers.Bots.Travel.TravelBrain.SafeAnchor(
            inputs.SelfPosition, inputs.IncumbentPosition, RetreatDistanceM);

    /// <summary>The close-in destination: a point just inside the band's ceiling, on the line to the target.</summary>
    private static Vector3 ApproachDestination(in CombatBrainInputs inputs, float bandMax)
    {
        var to = inputs.IncumbentPosition != Vector3.Zero
            ? inputs.IncumbentPosition
            : inputs.SelfPosition + new Vector3(1f, 0f, 0f);
        var direction = Vector3.Normalize(to - inputs.SelfPosition);
        if (!IsFinite(direction))
            return to;
        if (float.IsNaN(inputs.IncumbentDistanceM))
            return to; // unreadable distance: head for the target, the next wake re-decides
        var step = Math.Max(0f, inputs.IncumbentDistanceM - bandMax) + CloseInMarginM;
        return inputs.SelfPosition + direction * step;
    }

    private static bool IsFinite(Vector3 value)
        => value != Vector3.Zero && float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
