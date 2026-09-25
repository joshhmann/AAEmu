using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// CombatBrain: the decision chain, the ranked competition with hysteresis, and
/// the two facts other legs read.
///
/// These tests drive the PURE decision surface (<see cref="CombatBrain.Decide"/>,
/// <see cref="CombatTargetScorer"/>, <see cref="CombatBrainEngagement"/>) from
/// synthetic <see cref="CombatBrainInputs"/> — no world, no actor, no engine. The
/// live adapter's own reads (resolve/hostility/cooldown/bag/buff) are exercised by
/// the quest-leg rigs, not here; what is pinned here is the contract a consumer
/// observes: which arm fires, which verb it asks for, the band it evaluated, and
/// the engagement fact it publishes.
/// </summary>
[NotInParallel]
public class CombatBrainTests
{
    private const uint BoarObjId = 90001;
    private static readonly DateTime Wake = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Before(Test)]
    public void Reset() => CombatBrainEngagement.ClearAll();

    // ------------------------------------------------------------ band

    [Test]
    public async Task RoleBands_MatchTheEngineTreesBands()
    {
        // The bands are quoted from CombatDecisionTree, so a drift in either
        // place is a behavior change the lane would observe as a spacing change.
        await Assert.That(CombatBrain.RoleBand(CombatRole.Melee)).IsEqualTo((1.0f, 3.5f));
        await Assert.That(CombatBrain.RoleBand(CombatRole.RangedPhysical)).IsEqualTo((12.0f, 22.0f));
        await Assert.That(CombatBrain.RoleBand(CombatRole.RangedMagic)).IsEqualTo((14.0f, 22.0f));
        await Assert.That(CombatBrain.RoleBand(CombatRole.HealerSupport)).IsEqualTo((10.0f, 20.0f));
    }

    [Test]
    public async Task DesiredBand_IntersectsTheSkillRangeIntoTheRoleBand()
    {
        // A melee role with a 4 m skill keeps the melee band (the skill is not narrower).
        await Assert.That(CombatBrain.DesiredBand(CombatRole.Melee, 0f, 4f)).IsEqualTo((1.0f, 3.5f));
        // A melee role whose only skill reaches 20 m has no CONFLICT (the skill's
        // own floor is 0, so it can be used at contact) — the role band stands,
        // and it never collapses to an inverted interval.
        var (min, max) = CombatBrain.DesiredBand(CombatRole.Melee, 0f, 20f);
        await Assert.That(min).IsLessThanOrEqualTo(max);
        await Assert.That(min).IsEqualTo(1.0f);

        // A role floor ABOVE the skill's ceiling is the outright disagreement: the
        // skill wins so the bot has a band it can actually cast from.
        var (conflictMin, conflictMax) = CombatBrain.DesiredBand(CombatRole.RangedPhysical, 0f, 10f);
        await Assert.That(conflictMin).IsLessThanOrEqualTo(conflictMax);
        await Assert.That(conflictMax).IsEqualTo(10f);
        // No readable skill range: the role band stands alone (never narrowed to nothing).
        await Assert.That(CombatBrain.DesiredBand(CombatRole.RangedPhysical, 0f, 0f)).IsEqualTo((12.0f, 22.0f));
        await Assert.That(CombatBrain.DesiredBand(CombatRole.RangedPhysical, float.NaN, float.NaN)).IsEqualTo((12.0f, 22.0f));
    }

    // ------------------------------------------------------------ verbs per distance

    [Test]
    public async Task AtTwentyFiveMetres_ClosesInInsteadOfCasting()
    {
        // 25 m is outside every role band except a max-range caster's ceiling; the
        // close-in arm owns the wake so the next wake is inside the band.
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 25f, enemyCount: 1, selectedSkill: 18131, skillMax: 4f));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.CloseRange);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Move);
        await Assert.That(decision.Destination).IsNotNull();
        await Assert.That(decision.IsEngaged).IsTrue();
    }

    [Test]
    public async Task AtFifteenMetres_AMeleeBotClosesIn()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 15f, enemyCount: 1, selectedSkill: 18131, skillMax: 4f));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.CloseRange);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Move);
    }

    [Test]
    public async Task AtTwoMetres_AMeleeBotCastsItsSkill()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1, selectedSkill: 18131, skillMax: 4f));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Opener);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Cast);
        await Assert.That(decision.SkillId).IsEqualTo(18131u);
    }

    [Test]
    public async Task AtTwoMetres_WithTheLoopAlreadyLive_HoldsWhenNoSkillIsLegal()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1, selectedSkill: 0, autoAttackLive: true));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Hold);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(CombatReason.LoopLive);
    }

    [Test]
    public async Task AtTwoMetres_WithNoSkillAndNoLoop_SustainsTheAutoAttack()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1, selectedSkill: 0, autoAttackLive: false));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Sustain);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.AutoAttack);
        await Assert.That(decision.TargetObjId).IsEqualTo(BoarObjId);
    }

    [Test]
    public async Task ARangedBotInsideItsFloor_BacksOff_ButAMeleeBotDoesNot()
    {
        // An archer at 2 m cannot shoot: the spacing arm pushes it back out.
        var ranged = CombatBrain.Decide(Inputs(
            CombatRole.RangedPhysical, selfHp: 1.0f, distance: 2f, enemyCount: 1, selectedSkill: 0));
        await Assert.That(ranged.Arm).IsEqualTo(CombatArm.BackOff);
        await Assert.That(ranged.Verb).IsEqualTo(CombatVerb.Move);

        // A melee bot at 2 m is exactly where it wants to be — backing off there
        // would walk it out of its own reach every wake.
        var melee = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1, selectedSkill: 0, autoAttackLive: true));
        await Assert.That(melee.Arm).IsEqualTo(CombatArm.Hold);
    }

    // ------------------------------------------------------------ heal threshold

    [Test]
    public async Task AtFortyNinePercent_UsesTheHealItem()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.49f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, skillMax: 4f, healItem: 1234));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Heal);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.UseItem);
        await Assert.That(decision.ItemTemplateId).IsEqualTo(1234u);
    }

    [Test]
    public async Task AtFiftyOnePercent_DoesNotHeal()
    {
        // 0.51 is above the heal threshold: the wake is a normal rotation wake.
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.51f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, skillMax: 4f, healItem: 1234));

        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.Heal);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Cast);
    }

    [Test]
    public async Task BelowTheHealThresholdWithNoUsableItem_KeepsFighting()
    {
        // No consumable the engine could apply: the heal arm does not fire and no
        // item is fabricated — the engagement continues on its rotation.
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.30f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, skillMax: 4f, healItem: 0));

        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.Heal);
        await Assert.That(decision.ItemTemplateId).IsEqualTo(0u);
    }

    // ------------------------------------------------------------ disengage

    [Test]
    public async Task AtNineteenPercent_DisengagesWithTheRetreatLegAndPublishesTheFact()
    {
        var inputs = Inputs(CombatRole.Melee, selfHp: 0.19f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, skillMax: 4f, commit: true);

        var decision = CombatBrain.Decide(inputs);

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(decision.DisengageReason).IsEqualTo(CombatDisengageReason.CriticalHp);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Move);
        await Assert.That(decision.IsDisengaging).IsTrue();
        await Assert.That(decision.Destination).IsNotNull();

        // The observable Disengaging fact: published, and the yield fact must be
        // WITHDRAWN while the retreat owns the engagement (a pursuit leg must not
        // be yielded to an engagement that is falling back).
        var published = CombatBrainPlanner.PublishDecision(decision, inputs, Wake);
        await Assert.That(published.IsDisengaging).IsTrue();
        await Assert.That(CombatBrainEngagement.IsSurvivalVetoed(inputs.ActorObjId)).IsTrue();
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(inputs.ActorObjId)).IsFalse();
    }

    [Test]
    public async Task AtTwentyPercent_StillDisengages_TheBandEdgeIsInclusive()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.20f, distance: 2f, enemyCount: 1, selectedSkill: 18131));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(decision.DisengageReason).IsEqualTo(CombatDisengageReason.CriticalHp);
    }

    [Test]
    public async Task AtTwentyOnePercent_DoesNotDisengage()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.21f, distance: 2f, enemyCount: 1, selectedSkill: 18131));

        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.Disengage);
    }

    [Test]
    public async Task LowHpWithNoHostile_IsNotADisengage()
    {
        // A low bar out of combat is a RECOVERY problem, never this brain's retreat.
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.10f, distance: float.NaN, enemyCount: 0, selectedSkill: 0));

        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.Disengage);
        await Assert.That(decision.Threat).IsEqualTo(CombatThreat.None);
    }

    [Test]
    public async Task LeashEdge_DisengagesEvenAtFullHealth()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, commit: true, leashDrift: 46f, leashBudget: 50f));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(decision.DisengageReason).IsEqualTo(CombatDisengageReason.LeashEdge);
    }

    [Test]
    public async Task CriticalThreatWithNoEscapeTool_Disengages()
    {
        // Threat critical without the flee band can only come from an outnumbered,
        // unhurt bot in contact — with no CC ready there is nothing to answer with.
        var inputs = Inputs(CombatRole.Melee, selfHp: 0.45f, distance: 2f, enemyCount: 3,
            selectedSkill: 18131, ccAvailable: false, commit: true);
        await Assert.That(CombatBrain.DeriveThreat(inputs)).IsEqualTo(CombatThreat.Engaged);

        // With hp inside the flee band and no CC, the named cause is the flee band.
        var critical = Inputs(CombatRole.Melee, selfHp: 0.20f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, ccAvailable: false, commit: true);
        await Assert.That(CombatBrain.EvaluateDisengage(critical)).IsEqualTo(CombatDisengageReason.CriticalHp);
    }

    [Test]
    public async Task CrowdControlled_HoldsWithoutMovingOrCasting()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, crowdControlled: true, commit: true));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.CrowdControl);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Hold);
        await Assert.That(decision.Destination).IsNull();
        await Assert.That(decision.SkillId).IsEqualTo(0u);
        await Assert.That(decision.State).IsEqualTo(CombatBrainState.Holding);
    }

    [Test]
    public async Task CrowdControlledWhileCritical_StillAsksForTheRetreat()
    {
        // The escape is the point: swallowing the retreat under a root would
        // guarantee the death the engine's own gates might have let it escape.
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.15f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, crowdControlled: true, commit: true));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Move);
    }

    // ------------------------------------------------------------ survival veto

    [Test]
    public async Task DeadActor_PublishesTheVetoAndAsksForNothing()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0f, distance: 2f, enemyCount: 1, selectedSkill: 18131, alive: false));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.SurvivalVeto);
        await Assert.That(decision.State).IsEqualTo(CombatBrainState.Vetoed);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
    }

    [Test]
    public async Task UnreadableHp_NeverFabricatesAVetoOrADisengage()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: float.NaN, distance: 2f, enemyCount: 1, selectedSkill: 18131, skillMax: 4f));

        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.SurvivalVeto);
        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.Disengage);
    }

    [Test]
    public async Task UnreadableDistance_NeverReadsAsInBand()
    {
        // NaN distance must not satisfy `<= bandMax`; with no move possible and no
        // skill, the wake holds rather than casting at an unknown range.
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: float.NaN, enemyCount: 1, selectedSkill: 0));

        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.Opener);
        await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.CloseRange);
    }

    // ------------------------------------------------------------ commit + hysteresis

    [Test]
    public async Task NoCommittedTarget_CommitsToTheBestCandidate()
    {
        var candidates = new[]
        {
            Candidate(1, distance: 20f, level: 10, questRelevant: false),
            Candidate(2, distance: 4f, level: 10, questRelevant: true)
        };
        var inputs = Inputs(CombatRole.Melee, selfHp: 1.0f, distance: float.NaN, enemyCount: 2,
            selectedSkill: 0, autoAttackLive: false, candidates: candidates);

        var decision = CombatBrain.Decide(inputs);

        await Assert.That(decision.TargetObjId).IsEqualTo(2u); // quest-relevant + nearer
        await Assert.That(decision.CommittedThisWake).IsTrue();
    }

    [Test]
    public async Task IncumbentInsideItsWindow_KeepsTheCommitmentAgainstABetterChallenger()
    {
        // The incumbent is worse on paper (farther, not quest-relevant) but its
        // window is still open: the engagement must not flap.
        var candidates = new[]
        {
            Candidate(BoarObjId, distance: 20f, level: 10, questRelevant: false),
            Candidate(2, distance: 2f, level: 10, questRelevant: true)
        };
        var inputs = Inputs(CombatRole.Melee, selfHp: 1.0f, distance: 20f, enemyCount: 2,
            selectedSkill: 0, autoAttackLive: true, candidates: candidates, commit: true,
            incumbentObjId: BoarObjId, incumbentScore: 1000, commitmentInForce: true);

        var decision = CombatBrain.Decide(inputs);

        await Assert.That(decision.TargetObjId).IsEqualTo(BoarObjId);
        await Assert.That(decision.CommittedThisWake).IsFalse();
        // Kept the incumbent (no displacement) — at 20 m the melee spacing arm
        // then owns the wake, which is exactly the point: the engagement held.
        await Assert.That(decision.Arm).IsEqualTo(CombatArm.CloseRange);
    }

    [Test]
    public async Task IncumbentOutsideItsWindow_IsDisplacedByAClearlyBetterChallenger()
    {
        var candidates = new[]
        {
            Candidate(BoarObjId, distance: 20f, level: 10, questRelevant: false),
            Candidate(2, distance: 2f, level: 10, questRelevant: true)
        };
        var inputs = Inputs(CombatRole.Melee, selfHp: 1.0f, distance: 20f, enemyCount: 2,
            selectedSkill: 0, autoAttackLive: true, candidates: candidates, commit: true,
            incumbentObjId: BoarObjId, incumbentScore: 1000, commitmentInForce: false);

        var decision = CombatBrain.Decide(inputs);

        await Assert.That(decision.TargetObjId).IsEqualTo(2u);
        await Assert.That(decision.CommittedThisWake).IsTrue();
    }

    [Test]
    public async Task UnknownIncumbent_HoldsTheCommitment_ProvenGoneDropsIt()
    {
        // An UNREADABLE frame must never cost a live engagement.
        var unknown = Inputs(CombatRole.Melee, selfHp: 1.0f, distance: float.NaN, enemyCount: 1,
            selectedSkill: 0, autoAttackLive: true, incumbentObjId: BoarObjId,
            incumbentValid: false, incumbentUnknown: true, commitmentInForce: true);
        var held = CombatBrain.Decide(unknown);
        await Assert.That(held.Arm).IsEqualTo(CombatArm.InvalidDrop);
        await Assert.That(held.State).IsEqualTo(CombatBrainState.Holding);
        await Assert.That(held.Reason).IsEqualTo(CombatReason.IncumbentUnknown);
        await Assert.That(held.TargetObjId).IsEqualTo(BoarObjId);

        // Provably gone with nothing else to take: the commitment is dropped.
        var gone = Inputs(CombatRole.Melee, selfHp: 1.0f, distance: float.NaN, enemyCount: 0,
            selectedSkill: 0, incumbentObjId: BoarObjId, incumbentValid: false, incumbentUnknown: false,
            commitmentInForce: true, commit: true, candidates: []);
        var dropped = CombatBrain.Decide(gone);
        await Assert.That(dropped.Arm).IsEqualTo(CombatArm.InvalidDrop);
        await Assert.That(dropped.State).IsEqualTo(CombatBrainState.Idle);
        await Assert.That(dropped.Reason).IsEqualTo(CombatReason.TargetGone);
        await Assert.That(dropped.TargetObjId).IsEqualTo(0u);
    }

    // ------------------------------------------------------------ scoring

    [Test]
    public async Task Scoring_PrefersCloserWoundedQuestRelevantEasierTargets()
    {
        var closer = CombatTargetScorer.Score(Candidate(1, distance: 3f, level: 10, questRelevant: false), 10, 25f);
        var farther = CombatTargetScorer.Score(Candidate(1, distance: 20f, level: 10, questRelevant: false), 10, 25f);
        await Assert.That(closer).IsGreaterThan(farther);

        var wounded = CombatTargetScorer.Score(Candidate(1, distance: 10f, level: 10, questRelevant: false, hpRatio: 0.30f), 10, 25f);
        var healthy = CombatTargetScorer.Score(Candidate(1, distance: 10f, level: 10, questRelevant: false, hpRatio: 1.0f), 10, 25f);
        await Assert.That(wounded).IsGreaterThan(healthy);

        var relevant = CombatTargetScorer.Score(Candidate(1, distance: 10f, level: 10, questRelevant: true), 10, 25f);
        var plain = CombatTargetScorer.Score(Candidate(1, distance: 10f, level: 10, questRelevant: false), 10, 25f);
        await Assert.That(relevant).IsGreaterThan(plain);

        var overLevelled = CombatTargetScorer.Score(Candidate(1, distance: 10f, level: 30, questRelevant: false), 10, 25f);
        var onLevel = CombatTargetScorer.Score(Candidate(1, distance: 10f, level: 10, questRelevant: false), 10, 25f);
        await Assert.That(overLevelled).IsLessThan(onLevel);

        // An under-levelled target is neither rewarded nor punished.
        var under = CombatTargetScorer.Score(Candidate(1, distance: 10f, level: 1, questRelevant: false), 10, 25f);
        await Assert.That(under).IsEqualTo(onLevel);
    }

    [Test]
    public async Task Scoring_IsDeterministicAndTieBreaksOnTheLowerObjId()
    {
        var a = Candidate(50, distance: 10f, level: 10, questRelevant: false);
        var b = Candidate(20, distance: 10f, level: 10, questRelevant: false);

        var first = CombatTargetScorer.TrySelect([a, b], 0, false, 0, 0, 10, 25f, out var picked1, out _, out _);
        var second = CombatTargetScorer.TrySelect([b, a], 0, false, 0, 0, 10, 25f, out var picked2, out _, out _);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(picked1.ObjId).IsEqualTo(20u);
        await Assert.That(picked2.ObjId).IsEqualTo(20u);
    }

    [Test]
    public async Task DisplacementBar_IsSignPreservingOnNegativeScores()
    {
        // A plain 1.25x ratio inverts below zero; the bar must not.
        await Assert.That(CombatTargetScorer.DisplacementBar(1000)).IsEqualTo(1250d);
        await Assert.That(CombatTargetScorer.DisplacementBar(-1000)).IsEqualTo(-750d);
        await Assert.That(CombatTargetScorer.DisplacementBar(-750)).IsLessThan(0d);

        // A WORSE challenger must not displace a better incumbent. The incumbent's
        // own score is high (a near, on-level row); the challenger is heavily
        // over-levelled and far, so it scores far below the bar.
        var incumbentRow = Candidate(BoarObjId, distance: 3f, level: 10, questRelevant: false);
        var incumbentScore = CombatTargetScorer.Score(incumbentRow, 10, 25f);
        var challengerRow = Candidate(2, distance: 24f, level: 60, questRelevant: false);
        await Assert.That((double)CombatTargetScorer.Score(challengerRow, 10, 25f))
            .IsLessThan(CombatTargetScorer.DisplacementBar(incumbentScore));

        var inputs = Inputs(CombatRole.Melee, selfHp: 1.0f, distance: 3f, enemyCount: 2,
            selectedSkill: 0, autoAttackLive: true, candidates: [incumbentRow, challengerRow], commit: true,
            incumbentObjId: BoarObjId, incumbentScore: incumbentScore, commitmentInForce: false);
        var decision = CombatBrain.Decide(inputs);

        await Assert.That(decision.TargetObjId).IsEqualTo(BoarObjId);
        await Assert.That(decision.CommittedThisWake).IsFalse();
    }

    [Test]
    public async Task TrySelect_IgnoresUnmeasuredRows()
    {
        var unmeasured = new CombatCandidate(7, 100, float.NaN, 10, 1.0f, false, 0f);
        var measured = Candidate(8, distance: 10f, level: 10, questRelevant: false);

        var found = CombatTargetScorer.TrySelect([unmeasured, measured], 0, false, 0, 0, 10, 25f,
            out var picked, out _, out _);

        await Assert.That(found).IsTrue();
        await Assert.That(picked.ObjId).IsEqualTo(8u);
        await Assert.That(CombatTargetScorer.TrySelect([unmeasured], 0, false, 0, 0, 10, 25f,
            out _, out _, out _)).IsFalse();
    }

    // ------------------------------------------------------------ published facts

    [Test]
    public async Task PublishedEngagement_ExportsTheYieldFactAndClearsOnEnd()
    {
        var inputs = Inputs(CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, skillMax: 4f, commit: true, candidates: []);

        CombatBrainPlanner.PublishDecision(CombatBrain.Decide(inputs), inputs, Wake);
        var decision = CombatBrain.Decide(inputs);

        await Assert.That(decision.TargetObjId).IsEqualTo(BoarObjId);
        await Assert.That(decision.IsEngaged).IsTrue();
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(inputs.ActorObjId)).IsTrue();
        await Assert.That(CombatBrainEngagement.IsSurvivalVetoed(inputs.ActorObjId)).IsFalse();

        CombatBrainEngagement.Clear(inputs.ActorObjId);
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(inputs.ActorObjId)).IsFalse();
    }

    [Test]
    public async Task ANewTargetStartsAFreshRotation_TheOldChainsSkillIsNotCarried()
    {
        const uint actor = 70001;
        CombatBrainEngagement.Publish(actor, BoarObjId, 1000, new Vector3(5f, 0f, 0f), 50f, false, false, Wake);
        CombatBrainEngagement.PublishSkillUsed(actor, 10399u, Wake);
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var mid)).IsTrue();
        await Assert.That(mid.LastSkillUsed).IsEqualTo(10399u);

        // Committing to a DIFFERENT target drops the previous chain: the rotation is
        // per-engagement, and carrying it would fire a follow-up with no opener.
        CombatBrainEngagement.Publish(actor, 4242u, 900, new Vector3(5f, 0f, 0f), 50f, false, false, Wake);
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var next)).IsTrue();
        await Assert.That(next.IncumbentObjId).IsEqualTo(4242u);
        await Assert.That(next.LastSkillUsed).IsEqualTo(0u);
    }

    [Test]
    public async Task RefreshInsideTheWindow_DoesNotRestartTheCommitmentClockOrTheChain()
    {
        const uint actor = 70001;
        var anchor = new Vector3(5f, 0f, 0f);
        CombatBrainEngagement.Publish(actor, BoarObjId, 1000, anchor, 50f, false, false, Wake);
        CombatBrainEngagement.PublishSkillUsed(actor, 10399u, Wake);
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var first)).IsTrue();

        var later = Wake.AddSeconds(3);
        CombatBrainEngagement.Publish(actor, BoarObjId, 1000, anchor, 50f, false, false, later);
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var refreshed)).IsTrue();

        // The window follows the TARGET: a same-target refresh must not extend it,
        // or the incumbent could never be displaced while it keeps winning wakes.
        // The chain is likewise per-engagement, not per-wake.
        await Assert.That(refreshed.CommittedUtc).IsEqualTo(first.CommittedUtc);
        await Assert.That(refreshed.LastSkillUsed).IsEqualTo(10399u);
        await Assert.That(refreshed.UpdatedUtc).IsEqualTo(later);

        // A refresh with no world anchor keeps the ORIGINAL leash anchor, so the
        // leash arm measures drift from where the engagement began.
        await Assert.That(refreshed.LeashAnchor).IsEqualTo(anchor);
    }

    // ------------------------------------------------------------ the two exported facts

    [Test]
    public async Task SurvivalVetoClear_IsSetByTheDisengagingFactAndByAFatalObservation()
    {
        const uint actor = 70002;
        var healthy = new BotObservedContext { ActorId = actor, Hp = 100, MaxHp = 100 };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(actor, healthy)).IsFalse();

        // The published disengaging fact alone sets the veto (no vitals needed):
        // this is the precondition every objective leg reads.
        CombatBrainEngagement.Publish(actor, BoarObjId, 100, Vector3.Zero, 50f, false, disengaging: true, Wake);
        await Assert.That(QuestBehavior.IsSurvivalVetoed(actor, healthy)).IsTrue();
        CombatBrainEngagement.ClearAll();

        // A down actor is vetoed regardless of any engagement.
        var down = new BotObservedContext { ActorId = actor, Hp = 0, MaxHp = 100 };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(actor, down)).IsTrue();

        // Critical hp WITH a selected target is a combat emergency; the same hp
        // with no target is a recovery concern, not this veto.
        var criticalEngaged = new BotObservedContext
        {
            ActorId = actor, Hp = 15, MaxHp = 100, CurrentTargetObjId = BoarObjId
        };
        var criticalIdle = new BotObservedContext { ActorId = actor, Hp = 15, MaxHp = 100 };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(actor, criticalEngaged)).IsTrue();
        await Assert.That(QuestBehavior.IsSurvivalVetoed(actor, criticalIdle)).IsFalse();

        // Unreadable vitals never fabricate a veto.
        var unreadable = new BotObservedContext { ActorId = actor, Hp = 0, MaxHp = 0 };
        await Assert.That(QuestBehavior.IsSurvivalVetoed(actor, unreadable)).IsFalse();
    }

    [Test]
    public async Task YieldFact_IsWithdrawnWhileDisengaging_AndRestoredWhenEngaged()
    {
        const uint actor = 70003;
        var anchor = new Vector3(4f, 0f, 0f);

        CombatBrainEngagement.Publish(actor, BoarObjId, 100, anchor, 50f, false, disengaging: false, Wake);
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(actor)).IsTrue();

        // The retreat owns the engagement: the pursuit leg must NOT be yielded,
        // or it would stand down for a target the bot is running away from.
        CombatBrainEngagement.PublishState(actor, false, disengaging: true, Wake);
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(actor)).IsFalse();
        await Assert.That(CombatBrainEngagement.IsSurvivalVetoed(actor)).IsTrue();

        // Recovering re-arms the yield fact for the same commitment.
        CombatBrainEngagement.PublishState(actor, false, disengaging: false, Wake);
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(actor)).IsTrue();
    }

    [Test]
    public async Task LeashEdge_IsMeasuredFromTheAnchorCapturedAtEngage()
    {
        var anchor = new Vector3(100f, 0f, 0f);
        var engagement = new CombatBrainEngagement.Engagement(
            BoarObjId, 100, Wake, anchor, 50f, 0, DateTime.MinValue, false, false, Wake);

        // Near the anchor: not at the edge.
        await Assert.That(engagement.AtLeashEdge(new Vector3(110f, 0f, 0f))).IsFalse();
        // Past the 90% edge (45 m of a 50 m budget): the mob is about to reset.
        await Assert.That(engagement.AtLeashEdge(new Vector3(146f, 0f, 0f))).IsTrue();
        // A zero budget can never claim an edge (nothing to measure against).
        var noBudget = engagement with { LeashBudgetM = 0f };
        await Assert.That(noBudget.AtLeashEdge(new Vector3(999f, 0f, 0f))).IsFalse();
    }

    // ------------------------------------------------------------ describe

    [Test]
    public async Task Describe_IsBracketAndSpaceFreeForTheLaneParser()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 0.49f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, skillMax: 4f, healItem: 1234));

        var text = decision.Describe();

        await Assert.That(text.Contains(' ')).IsFalse();
        await Assert.That(text.Contains('[')).IsFalse();
        await Assert.That(text.Contains(']')).IsFalse();
        await Assert.That(text.Contains("arm=Heal")).IsTrue();
        await Assert.That(text.Contains("verb=UseItem")).IsTrue();
    }

    // ------------------------------------------------------------ fixture

    private static CombatCandidate Candidate(
        uint objId, float distance, byte level, bool questRelevant, float hpRatio = 1.0f)
        => new(objId,
            CombatTargetScorer.AttentionScoreOf(attackTarget: true, hostileInAggro: true),
            distance, level, hpRatio, questRelevant, 0f);

    private static CombatBrainInputs Inputs(
        CombatRole role,
        float selfHp,
        float distance,
        int enemyCount,
        uint selectedSkill = 0,
        float skillMax = 0f,
        uint healItem = 0,
        bool autoAttackLive = false,
        bool crowdControlled = false,
        bool ccAvailable = true,
        bool alive = true,
        uint incumbentObjId = BoarObjId,
        bool incumbentValid = true,
        bool incumbentUnknown = false,
        int incumbentScore = 0,
        bool commitmentInForce = false,
        float leashDrift = 0f,
        float leashBudget = 50f,
        bool commit = false,
        IReadOnlyList<CombatCandidate>? candidates = null)
    {
        var hasIncumbent = commit || candidates == null;
        return new CombatBrainInputs(
            ActorObjId: 70001,
            Role: role,
            Alive: alive,
            SelfHpRatio: selfHp,
            SelfLevel: 10,
            SelfPosition: Vector3.Zero,
            EnemyCount: enemyCount,
            NearestEnemyDistanceM: distance,
            TookDamageThisFrame: false,
            CrowdControlled: crowdControlled,
            CcAvailable: ccAvailable,
            IsAutoAttackLive: autoAttackLive,
            HealItemTemplateId: healItem,
            SkillMinRangeM: 0f,
            SkillMaxRangeM: skillMax,
            SelectedSkillId: selectedSkill,
            LastSkillUsed: 0,
            IncumbentObjId: hasIncumbent ? incumbentObjId : 0u,
            IncumbentValid: hasIncumbent && incumbentValid,
            IncumbentUnknown: hasIncumbent && incumbentUnknown,
            IncumbentScore: incumbentScore,
            CommitmentInForce: commitmentInForce,
            IncumbentPosition: new Vector3(distance, 0f, 0f),
            IncumbentDistanceM: distance,
            IncumbentLeashDriftM: leashDrift,
            LeashBudgetM: leashBudget,
            Candidates: candidates ?? [],
            NowUtc: Wake);
    }
}
