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

    /// <summary>
    /// The reach the rig's ranged auto-attack (skill 4) is enforced at — read
    /// from the seeded template's own MaxRange, which is the value
    /// <c>Skill.Use</c> compares <c>GetDistanceTo(target, true)</c> against.
    /// Derived, not re-pinned: the assertion below is about the CEILING
    /// tracking that reach, and it would keep testing the wrong number if the
    /// fixture's skill ever changed.
    /// </summary>
    private static float RangedAutoAttackReachM
        => CombatDecisionTree.GetSkillEnforcedRange(
            AAEmu.Game.Core.Managers.SkillManager.Instance.GetSkillTemplate(
                CombatDecisionTree.BasicRangedAutoAttackSkillId),
            null).MaxRange;

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

    /// <summary>
    /// THE ADMISSION RADIUS, per role: the combat brain is consulted out to the
    /// actor's OWN band ceiling, floored at the pursuit stop radius. This is the
    /// arithmetic the melee/ranged split depends on, so it is pinned rather than
    /// re-derived: a flat radius is what stranded every ranged role below its own
    /// weapon floor.
    /// </summary>
    [Test]
    public async Task AdmissionRadius_IsEachRolesOwnBandCeiling_FlooredAtThePursuitRadius()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("admit-arith");

        // Melee is UNCHANGED at 3.5 m. The engine's weapon overwrite only fires
        // when a template names a weapon slot (`WeaponSlotForRangeId > 0`); this
        // rig's skill 2 names none, so the engine reads the template's own band
        // and 3.5 m still binds. (A production melee template that DOES name the
        // slot is held to the fist default of 3.0 m instead — the value the live
        // log shows the engine refusing past: `targetDist=3.6732788,
        // maxRangeCheck=3`.)
        actor.Character.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Fight;
        await Assert.That(QuestBehavior.CombatAdmissionRadiusM(actor.Character))
            .IsEqualTo(CombatBrain.MeleeMaxM);

        // THE ARCHER IS THE ONE THAT NARROWS. Its band ceiling is 22 m but the
        // auto-attack it fires (skill 4) is enforced at 20 m, so admission stops
        // at the reach the engine will actually honour. Admitting it to 22 m is
        // what let it stand down its closing leg at 21.8 m and then have the
        // engine refuse every single cast — the frozen archer in the run log.
        actor.Character.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Wild;
        await Assert.That(QuestBehavior.CombatAdmissionRadiusM(actor.Character))
            .IsEqualTo(RangedAutoAttackReachM)
            .Because($"skill {CombatDecisionTree.BasicRangedAutoAttackSkillId} is enforced at " +
                      $"{RangedAutoAttackReachM:F0} m; a band ceiling above its own reach is unreachable");

        // The casters keep their own bands. Neither names a weapon, so the auto-
        // attack one would fire falls back to skill 2 at a contact range BELOW
        // their band floor — and a reach under the floor is an unreachable band,
        // not a spacing constraint, so it must not collapse the role to melee.
        actor.Character.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Magic;
        await Assert.That(QuestBehavior.CombatAdmissionRadiusM(actor.Character))
            .IsEqualTo(CombatBrain.RangedMagicMaxM);

        actor.Character.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Love;
        await Assert.That(QuestBehavior.CombatAdmissionRadiusM(actor.Character))
            .IsEqualTo(CombatBrain.HealerMaxM);

        // The property the defect broke: admission must COVER each role's own band
        // floor, so the BAND arm (sustain/opener at the floor, back-off below it) is
        // reachable at all. Under the old flat 3.0 m gate every ranged role's floor
        // (12 / 14 / 10 m) sat far outside the admitted range, which is why only
        // BackOff ever fired for them.
        foreach (var ability in new[]
                 {
                     AAEmu.Game.Models.Game.Skills.AbilityType.Fight,
                     AAEmu.Game.Models.Game.Skills.AbilityType.Wild,
                     AAEmu.Game.Models.Game.Skills.AbilityType.Magic,
                     AAEmu.Game.Models.Game.Skills.AbilityType.Love,
                 })
        {
            actor.Character.Ability1 = ability;
            var role = CombatDecisionTree.InferRole(actor.Character);
            var (floor, _) = CombatBrain.RoleBand(role);
            await Assert.That(QuestBehavior.CombatAdmissionRadiusM(actor.Character))
                .IsGreaterThanOrEqualTo(floor)
                .Because($"{role} must be admitted from inside its own {floor:F0}m band");
        }

        // An unreadable role falls back to the melee band, never to a zero or a
        // NaN radius that would admit nobody or everybody.
        await Assert.That(QuestBehavior.CombatAdmissionRadiusM(null))
            .IsEqualTo(CombatBrain.MeleeMaxM);
    }

    /// <summary>
    /// DEFECT A, as a CONTRACT: the admission ceiling is the actor's own reach,
    /// not its role band's constant — and it is derived by reading the engine's
    /// enforced range, so a fixture whose weapon reaches further moves the
    /// ceiling with it.
    ///
    /// Fails before the fix at 22.0 m (the role band) for an archer whose only
    /// verb is enforced at 20 m: that gap is the frozen archer, admitted at
    /// 21.8 m, closing leg stood down, every cast refused TooFarRange.
    /// </summary>
    [Test]
    public async Task AdmissionCeiling_IsTheActorsOwnReach_NotTheRoleBandConstant()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("admit-reach");
        actor.Character.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Wild;

        var (_, roleBandMax) = CombatBrain.RoleBand(CombatRole.RangedPhysical);
        var reach = RangedAutoAttackReachM;

        // Precondition: this fixture's band ceiling is genuinely wider than its
        // reach, so the assertion below is not a tautology on equal numbers.
        await Assert.That(roleBandMax).IsGreaterThan(reach)
            .Because("the defect only exists when the band ceiling outruns the reach");

        var ceiling = QuestBehavior.CombatAdmissionRadiusM(actor.Character);

        // The contract: no pose admitted here can be one the engine refuses.
        await Assert.That(ceiling).IsLessThanOrEqualTo(reach);
        await Assert.That(ceiling).IsEqualTo(reach);

        // And it moves with the reach, not with the band: a template enforced
        // beyond the band's own ceiling must NOT pull the band outward either,
        // so the ceiling stays the nearer of the two bounds.
        await Assert.That(ceiling).IsLessThanOrEqualTo(roleBandMax);
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

    /// <summary>
    /// THE MELEE CONTACT CONTRACT. The engine's melee reach includes 0 m
    /// (<c>GameplayActor.AutoAttack</c> only picks the skill by distance and
    /// refuses a null/dead target, never a short one), so a melee bot standing
    /// INSIDE its band floor must still be able to attack. Backing a melee bot
    /// off at contact walks it out of its own reach and leaves the attack arm
    /// unreachable — the failure a lane run recorded as 43 attacks that never
    /// dispatched one.
    /// </summary>
    [Test]
    public async Task AMeleeBotInsideItsBandFloor_CanStillReachTheAttackArm()
    {
        foreach (var contact in new[] { 0.1f, 0.5f, 0.9f })
        {
            var armed = CombatBrain.Decide(Inputs(
                CombatRole.Melee, selfHp: 1.0f, distance: contact, enemyCount: 1,
                selectedSkill: 0, autoAttackLive: false));
            await Assert.That(armed.Arm).IsNotEqualTo(CombatArm.BackOff);
            await Assert.That(armed.Verb).IsEqualTo(CombatVerb.AutoAttack);
            await Assert.That(armed.Arm).IsEqualTo(CombatArm.Sustain);

            // With the loop already live it holds position rather than retreating.
            var live = CombatBrain.Decide(Inputs(
                CombatRole.Melee, selfHp: 1.0f, distance: contact, enemyCount: 1,
                selectedSkill: 0, autoAttackLive: true));
            await Assert.That(live.Arm).IsNotEqualTo(CombatArm.BackOff);
            await Assert.That(live.Verb).IsEqualTo(CombatVerb.Hold);
        }
    }

    /// <summary>
    /// The melee ceiling is untouched: a melee bot beyond it still closes in.
    /// </summary>
    [Test]
    public async Task AMeleeBotBeyondItsBandCeiling_StillClosesIn()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: CombatBrain.MeleeMaxM + 1f,
            enemyCount: 1, selectedSkill: 0));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.CloseRange);
        await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Move);
    }

    /// <summary>
    /// THE RANGED CONTACT DECISION: a ranged bot cornered inside its floor —
    /// even at 0.5 m, deep inside its own contact range — BACKS OFF. It cannot
    /// shoot there and it cannot melee (its auto-attack would be a melee swing
    /// at a role built to kite), so spacing is the only arm that gives it back a
    /// verb. This is deliberately NOT relaxed for melee reach: widening it
    /// would let an archer stand inside a bear's jaws and swing.
    /// </summary>
    [Test]
    public async Task ARangedBotCorneredAtContact_StillBacksOff_ItCannotShootOrMelee()
    {
        foreach (var contact in new[] { 0.1f, 0.5f, 0.9f })
        {
            var decision = CombatBrain.Decide(Inputs(
                CombatRole.RangedPhysical, selfHp: 1.0f, distance: contact,
                enemyCount: 1, selectedSkill: 0, autoAttackLive: false));
            await Assert.That(decision.Arm).IsEqualTo(CombatArm.BackOff);
            await Assert.That(decision.Verb).IsEqualTo(CombatVerb.Move);
        }
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
    /// <summary>
    /// DRIFT-OVER-EDGE (kill27 telemetry): drift past the full budget (not just
    /// the 90% edge) still reads LeashEdge — the mob overshot the leash, and
    /// the brain refuses. Pins the verdict the stale-pin watchdog feeds from.
    /// </summary>
    [Test]
    public async Task LeashDriftOverFullBudget_StillReadsLeashEdge()
    {
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.Melee, selfHp: 1.0f, distance: 0.2f, enemyCount: 1,
            selectedSkill: 18131, commit: true, leashDrift: 74.3f, leashBudget: 50f));

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

    /// <summary>
    /// THE STALE-ANCHOR FIX. A wake that keeps the SAME committed target still
    /// re-anchors the leash on the prey's live position. A frozen anchor measured
    /// a mob that had merely wandered as a full-budget strayer on every later
    /// wake, and the disengage arm sits above every band/skill arm — so a bot
    /// standing on its prey (0.34 m in the recorded run) was told to break contact
    /// forever and never reached the attack arm.
    /// </summary>
    [Test]
    public async Task ASameTargetWake_ReAnchorsTheLeashOnThePreysLivePosition()
    {
        const uint actor = 70010;
        var spawn = new Vector3(100f, 200f, 30f);

        // The fight starts here: the wake's prey stands at `spawn` and the publish
        // banks that coordinate as the leash anchor. The brain reports no
        // competition (the quest path commits to one target and forbids a second),
        // so this first anchor comes from the missing-entry branch — which is the
        // real first-engagement shape.
        var engage = CombatInputs(spawn, actor);
        var first = CombatBrain.Decide(engage);
        await Assert.That(first.TargetObjId).IsEqualTo(BoarObjId);
        await Assert.That(first.CommittedThisWake).IsFalse();
        CombatBrainPlanner.PublishDecision(first, engage, Wake);
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var engaged)).IsTrue();
        await Assert.That(engaged.LeashAnchor).IsEqualTo(spawn);

        // The mob wanders; the next wake keeps the SAME committed target.
        var wandered = new Vector3(130f, 200f, 30f);
        var wake = CombatInputs(wandered, actor);
        var held = CombatBrain.Decide(wake);
        await Assert.That(held.CommittedThisWake).IsFalse();

        CombatBrainPlanner.PublishDecision(held, wake, Wake.AddSeconds(2));

        await Assert.That(CombatBrainEngagement.TryGet(actor, out var refreshed)).IsTrue();
        await Assert.That(refreshed.LeashAnchor).IsEqualTo(wandered);
        // The anchor tracks the prey, so it is no longer at the engagement's origin.
        await Assert.That(refreshed.LeashAnchor).IsNotEqualTo(spawn);
    }

    /// <summary>
    /// A mob that really does move out of the leash still dislodges the
    /// engagement: the disengage decision is taken from the wake's own measured
    /// drift, which a publisher does not retroactively rewrite.
    /// </summary>
    [Test]
    public async Task APreyThatStraysPastTheBudget_StillDisengagesWithLeashEdge()
    {
        const uint actor = 70011;
        var engage = CombatInputs(new Vector3(100f, 0f, 0f), actor);
        CombatBrainPlanner.PublishDecision(CombatBrain.Decide(engage), engage, Wake);

        // The same published wake, re-read at the moment the prey is 46 m from the
        // anchor it was last seen at — past the 90% edge of a 50 m budget.
        var strayed = CombatInputs(new Vector3(146f, 0f, 0f), actor, leashDrift: 46f);
        var decision = CombatBrain.Decide(strayed);

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(decision.DisengageReason).IsEqualTo(CombatDisengageReason.LeashEdge);
        await Assert.That(decision.IsDisengaging).IsTrue();

        CombatBrainPlanner.PublishDecision(decision, strayed, Wake.AddSeconds(2));
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var retreating)).IsTrue();
        await Assert.That(retreating.Disengaging).IsTrue();
        // A break-contact wake re-publishes the lifecycle flags only: the anchor
        // of the engagement stays where the fight left it.
        await Assert.That(retreating.LeashAnchor).IsEqualTo(new Vector3(100f, 0f, 0f));
    }

    /// <summary>
    /// The refresh is a re-anchor, NOT a re-commit: the engagement's age — the
    /// clock the incumbent's hysteresis window runs on — must keep growing, or a
    /// fight could never age out and the window would live forever.
    /// </summary>
    [Test]
    public async Task RepeatedSameTargetRefreshes_KeepAgingTheCommitment_TheyNeverRestartIt()
    {
        const uint actor = 70012;
        var engage = CombatInputs(new Vector3(100f, 0f, 0f), actor);
        CombatBrainPlanner.PublishDecision(CombatBrain.Decide(engage), engage, Wake);
        CombatBrainEngagement.PublishSkillUsed(actor, 10399u, Wake);

        await Assert.That(CombatBrainEngagement.TryGet(actor, out var first)).IsTrue();
        await Assert.That(first.CommittedUtc).IsEqualTo(Wake);

        for (var i = 0; i < 4; i++)
        {
            // The prey walks one metre further every wake.
            var walked = CombatInputs(new Vector3(100f + i + 1, 0f, 0f), actor);
            var decision = CombatBrain.Decide(walked);
            await Assert.That(decision.CommittedThisWake).IsFalse();

            var now = Wake.AddSeconds(i + 1);
            CombatBrainPlanner.PublishDecision(decision, walked, now);

            await Assert.That(CombatBrainEngagement.TryGet(actor, out var refreshed)).IsTrue();
            await Assert.That(refreshed.CommittedUtc).IsEqualTo(Wake);
            await Assert.That(refreshed.UpdatedUtc).IsEqualTo(now);
            await Assert.That(Math.Abs(refreshed.AgeSeconds(now) - (i + 1))).IsLessThan(0.001d);
            // The anchor followed the prey, and the rotation survived the refresh.
            await Assert.That(refreshed.LeashAnchor).IsEqualTo(new Vector3(100f + i + 1, 0f, 0f));
            await Assert.That(refreshed.LastSkillUsed).IsEqualTo(10399u);
        }
    }

    /// <summary>
    /// The two re-anchor branches that already existed still re-anchor: a
    /// commitment that starts on this wake, and a committed target that CHANGED.
    /// </summary>
    [Test]
    public async Task ANewCommitmentAndAChangedTarget_BothStillReAnchorTheLeash()
    {
        const uint actor = 70013;
        const uint challenger = 4242u;
        var elsewhere = new Vector3(400f, 0f, 0f);
        var engage = CombatInputs(new Vector3(100f, 0f, 0f), actor);
        var started = CombatBrain.Decide(engage);
        await Assert.That(started.TargetObjId).IsEqualTo(BoarObjId);
        CombatBrainPlanner.PublishDecision(started, engage, Wake);
        CombatBrainEngagement.PublishSkillUsed(actor, 10399u, Wake);

        // A challenger a full budget away DISPLACES the incumbent once the
        // commitment window has elapsed: this wake really does start a
        // commitment, and it re-anchors on the target it took.
        var displace = CombatInputs(elsewhere, actor) with
        {
            CommitmentInForce = false,
            Candidates = [Candidate(challenger, distance: 80f, level: 10, questRelevant: false)]
        };
        var recommitted = CombatBrain.Decide(displace);
        await Assert.That(recommitted.CommittedThisWake).IsTrue();
        await Assert.That(recommitted.TargetObjId).IsEqualTo(challenger);
        CombatBrainPlanner.PublishDecision(recommitted, displace, Wake.AddSeconds(20));
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var recommittedState)).IsTrue();
        await Assert.That(recommittedState.IncumbentObjId).IsEqualTo(challenger);
        await Assert.That(recommittedState.LeashAnchor).IsEqualTo(elsewhere);
        // A changed target is a NEW rotation, so the old chain did not survive.
        await Assert.That(recommittedState.LastSkillUsed).IsEqualTo(0u);
        // And the new commitment's window starts with it.
        await Assert.That(recommittedState.CommittedUtc).IsEqualTo(Wake.AddSeconds(20));
    }

    /// <summary>
    /// The fail-closed start: with no published engagement there is no anchor to
    /// measure from, so the drift reads 0 (never a fabricated "about to reset"),
    /// and the very first publish banks that same coordinate as the anchor.
    /// </summary>
    [Test]
    public async Task AFirstEngagementWithNoPriorEntry_AnchorsAndReadsDriftZero()
    {
        const uint actor = 70014;
        var inputs = CombatInputs(new Vector3(100f, 0f, 0f), actor);

        await Assert.That(CombatBrainEngagement.TryGet(actor, out _)).IsFalse();
        await Assert.That(inputs.IncumbentLeashDriftM).IsEqualTo(0f);
        await Assert.That(CombatBrain.EvaluateDisengage(inputs)).IsEqualTo(CombatDisengageReason.None);

        CombatBrainPlanner.PublishDecision(CombatBrain.Decide(inputs), inputs, Wake);

        await Assert.That(CombatBrainEngagement.TryGet(actor, out var first)).IsTrue();
        await Assert.That(first.LeashAnchor).IsEqualTo(new Vector3(100f, 0f, 0f));
    }

    /// <summary>
    /// THE SELF-SUSTAINING-LEASH FIX, observable transition: a leash trip measured
    /// ACROSS wakes must not keep the engagement in Disengaging once the prey stops
    /// running. The leash frame advances on every readable wake, so drift is the
    /// prey's movement since the last frame — a wandering boar no longer accumulates
    /// a trip that re-fires forever, and the very next wake can hold the commitment.
    /// Without the advance the anchor freezes at the first past-edge reading and
    /// every later wake re-disengages the same prey (19,756 such wakes in the
    /// recorded run).
    /// </summary>
    [Test]
    public async Task ALeashTrip_LeavesDisengagingOnceThePreyStopsRunning()
    {
        const uint actor = 70020;
        var engage = CombatInputs(new Vector3(100f, 0f, 0f), actor);
        CombatBrainPlanner.PublishDecision(CombatBrain.Decide(engage), engage, Wake);

        // The prey runs 46 m (past the 90% edge of a 50 m budget) in one frame:
        // a genuine trip, measured against the frame the last wake banked.
        var ran = CombatInputs(new Vector3(146f, 0f, 0f), actor, leashDrift: 46f);
        var trip = CombatBrain.Decide(ran);
        await Assert.That(trip.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(trip.DisengageReason).IsEqualTo(CombatDisengageReason.LeashEdge);
        CombatBrainPlanner.PublishDecision(trip, ran, Wake.AddSeconds(1));
        await Assert.That(CombatBrainEngagement.IsSurvivalVetoed(actor)).IsTrue();

        // The next wake RE-MEASURES and advances the frame to where the prey now
        // stands, then decides on that fresh drift. The prey has not moved further,
        // so the trip no longer holds.
        var stood = CombatInputs(new Vector3(147f, 0f, 0f), actor, leashDrift: 1f);
        CombatBrainEngagement.Reanchor(actor, BoarObjId, stood.IncumbentPosition, 50f, Wake.AddSeconds(2));
        var released = CombatBrain.Decide(stood);

        await Assert.That(released.Arm).IsNotEqualTo(CombatArm.Disengage);
        await Assert.That(released.DisengageReason).IsEqualTo(CombatDisengageReason.None);
        // And the fresh target can be committed and acted on.
        await Assert.That(released.TargetObjId).IsEqualTo(BoarObjId);
        await Assert.That(released.Verb).IsNotEqualTo(CombatVerb.Hold);
    }

    /// <summary>
    /// The advance is a MEASUREMENT, never a decision: it must not restart the
    /// commitment window, and <see cref="CombatBrainEngagement.Engagement.AgeSeconds"/>
    /// must keep growing across it. A re-anchor that reset the clock would make
    /// the incumbent's hysteresis window immortal.
    /// </summary>
    [Test]
    public async Task TheAnchorAdvance_NeverRestartsTheCommitmentWindow()
    {
        const uint actor = 70021;
        var engage = CombatInputs(new Vector3(100f, 0f, 0f), actor);
        CombatBrainPlanner.PublishDecision(CombatBrain.Decide(engage), engage, Wake);
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var first)).IsTrue();
        await Assert.That(first.CommittedUtc).IsEqualTo(Wake);

        var later = Wake.AddSeconds(9);
        CombatBrainEngagement.Reanchor(actor, BoarObjId, new Vector3(109f, 0f, 0f), 50f, later);
        await Assert.That(CombatBrainEngagement.TryGet(actor, out var advanced)).IsTrue();

        await Assert.That(advanced.CommittedUtc).IsEqualTo(Wake);
        await Assert.That(advanced.LeashAnchor).IsEqualTo(new Vector3(109f, 0f, 0f));
        await Assert.That(advanced.AgeSeconds(later)).IsGreaterThan(8.9d);
        // The lifecycle flags ride along untouched: a measurement never clears a
        // published survival veto.
        await Assert.That(advanced.Disengaging).IsFalse();
    }

    /// <summary>
    /// THE CONTACT REACH FIX. A caster crowded at melee contact holds a verb the
    /// engine WILL hand it — <c>GameplayActor.AutoAttack</c> issues the melee
    /// auto-attack (skill 2, range 0-4 m) to every non-ranged-physical role inside
    /// 4 m. Backing it off there denied it a verb it has and walked it out of the
    /// only range where it had any; the recorded run dispatched zero AutoAttack
    /// with every bot standing at 0.34 m. This is the failing-before/passing-after
    /// case: at these inputs the pre-fix chain returned <c>BackOff</c>.
    /// </summary>
    [Test]
    public async Task ACrowdedCasterAtContact_ReachesTheAttackArm_NotBackOff()
    {
        foreach (var role in new[] { CombatRole.RangedMagic, CombatRole.HealerSupport })
        foreach (var contact in new[] { 0.1f, 0.5f, 1.0f, 2.0f, 3.5f })
        {
            var decision = CombatBrain.Decide(Inputs(
                role, selfHp: 1.0f, distance: contact, enemyCount: 1,
                selectedSkill: 0, autoAttackLive: false));

            await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.BackOff);
            await Assert.That(decision.Verb).IsEqualTo(CombatVerb.AutoAttack);
            await Assert.That(decision.Arm).IsEqualTo(CombatArm.Sustain);
        }
    }

    /// <summary>
    /// The regression pin for that carve-out: a genuinely RANGED-PHYSICAL role
    /// still backs off when crowded. Inside 4 m the engine hands it the ranged
    /// auto-attack (skill 4) regardless of what it carries, so it has no verb at
    /// contact at all and spacing really is the only arm that gives it one back.
    /// </summary>
    [Test]
    public async Task ACrowdedArcherAtContact_StillBacksOff_ItHasNoVerbThere()
    {
        foreach (var contact in new[] { 0.1f, 0.5f, 2.0f, 3.5f })
        {
            var decision = CombatBrain.Decide(Inputs(
                CombatRole.RangedPhysical, selfHp: 1.0f, distance: contact,
                enemyCount: 1, selectedSkill: 0, autoAttackLive: false));
            await Assert.That(decision.Arm).IsEqualTo(CombatArm.BackOff);
        }

        // Outside melee reach the carve-out stops applying, so even a caster
        // reverts to spacing below its floor.
        var caster = CombatBrain.Decide(Inputs(
            CombatRole.RangedMagic, selfHp: 1.0f, distance: CombatBrain.MeleeMaxM + 0.5f,
            enemyCount: 1, selectedSkill: 0, autoAttackLive: false));
        await Assert.That(caster.Arm).IsEqualTo(CombatArm.BackOff);
    }

    /// <summary>
    /// THE RETREAT-PERCEPTION FIX. The spacing arm's anchor measures its step
    /// FROM THE ACTOR (<c>TravelBrain.SafeAnchor</c>), so a full
    /// <see cref="CombatBrain.RetreatDistanceM"/>-metre back-off from contact
    /// leaves the prey at contact + 25 m — past the census the next wake
    /// perceives through (<c>GameplayActor.Observe</c>'s <c>GetAround(..., 25f)</c>).
    /// The row drops, selection withdraws, the pursuit closes back in and
    /// re-crowds the archer: the recorded oscillation that left two archers at
    /// zero kills while standing on the prey.
    ///
    /// The fix bounds the step so the prey always lands INSIDE the census. This
    /// is the failing-before/passing-after case: at 1.7 m the pre-fix
    /// destination sat 26.7 m from the target.
    ///
    /// The bound is read from <see cref="CombatBrain.AggroBandM"/>, not from a
    /// literal 25, so a future change to the census radius is caught here.
    /// </summary>
    [Test]
    public async Task ACrowdedArcherBackingOff_LandsItsPreyInsideThePerceptionRadius()
    {
        // Every contact the spacing arm can fire at: the archer's floor is 12 m,
        // so nothing above 12 m ever reaches this arm.
        foreach (var contact in new[] { 0.1f, 1.7f, 2.0f, 3.5f, 11.9f })
        {
            var decision = CombatBrain.Decide(Inputs(
                CombatRole.RangedPhysical, selfHp: 1.0f, distance: contact,
                enemyCount: 1, selectedSkill: 0, autoAttackLive: false));

            await Assert.That(decision.Arm).IsEqualTo(CombatArm.BackOff)
                .Because("an archer inside its band floor has no verb at all — spacing is the only arm that gives it one back");

            // The prey the actor walks away from is the incumbent, on the +X axis.
            await Assert.That(PreyDistanceM(decision, contact)).IsLessThan(CombatBrain.AggroBandM)
                .Because($"at {contact} m contact the spacing step must land the prey INSIDE the census, " +
                          "not on or past its far edge");
        }
    }

    /// <summary>
    /// The bound must not be won by standing still: the prey has to actually
    /// move away far enough to reopen the archer's spacing, or the next wake
    /// reads it below the floor again and re-fires the same arm.
    /// </summary>
    [Test]
    public async Task ACrowdedArcherBackingOff_StillOpensRealSpacing()
    {
        const float contact = 1.7f;
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.RangedPhysical, selfHp: 1.0f, distance: contact,
            enemyCount: 1, selectedSkill: 0, autoAttackLive: false));

        await Assert.That(PreyDistanceM(decision, contact)).IsGreaterThan(CombatBrain.RangedPhysicalMinM)
            .Because("the spacing step must leave the archer's band floor outright, so the next wake " +
                      "reads the prey as far and closes in instead of backing off again");
    }

    /// <summary>
    /// The flee leg is NOT the spacing leg. A survival break wants maximum
    /// separation — losing sight of what you are fleeing is the point — so it
    /// keeps the engine's own full-length step and the bound stays scoped to the
    /// spacing arm.
    /// </summary>
    [Test]
    public async Task ACriticalHpDisengage_StillRetreatsTheFullEngineLength()
    {
        const float contact = 1.7f;
        var decision = CombatBrain.Decide(Inputs(
            CombatRole.RangedPhysical, selfHp: CombatBrain.FleeHpThreshold,
            distance: contact, enemyCount: 1, selectedSkill: 0, autoAttackLive: false));

        await Assert.That(decision.Arm).IsEqualTo(CombatArm.Disengage);

        await Assert.That(PreyDistanceM(decision, contact)).IsEqualTo(contact + CombatBrain.RetreatDistanceM)
            .Because("the flee leg keeps the engine's full 25 m step");
    }

    /// <summary>
    /// How far the prey ends up from the destination the actor walks to. The
    /// incumbent sits on the +X axis at <paramref name="contactM"/> (the shape
    /// <see cref="Inputs"/> always builds), and the actor starts at the origin.
    /// </summary>
    private static float PreyDistanceM(CombatBrainDecision decision, float contactM)
    {
        var destination = decision.Destination
            ?? throw new InvalidOperationException("the spacing leg must carry a destination to walk to");
        var prey = new Vector3(contactM, 0f, 0f);
        return Vector3.Distance(destination, prey);
    }

    /// <summary>
    /// THE BAND-CEILING BOUND. Perception is the wider circle; the one that
    /// decides this actor's wake is its OWN band ceiling — the combat brain is
    /// consulted only within <c>QuestBehavior.CombatAdmissionRadiusM</c>, which
    /// reads exactly this number. A spacing leg bounded by the census alone
    /// therefore pushes a crowded archer to ~24 m, past the 22 m it is consulted
    /// from: the next wake is <c>validate=out-of-range</c>, the BAND arm never
    /// runs, and the pursuit leg re-crowds it — the one loop a census bound
    /// cannot close. This is the failing-before/passing-after case: the pre-fix
    /// destination for the 4.56 m contact below sat at ~24 m, outside the band.
    /// </summary>
    [Test]
    public async Task ACrowdedArcherBackingOff_LandsInsideItsOwnBandCeiling_SoTheNextWakeIsAdmitted()
    {
        // The two contacts recorded live for char 2, the archer that dispatched no
        // attack in five consecutive runs.
        foreach (var contact in new[] { 4.56f, 3.23f })
        {
            var decision = CombatBrain.Decide(Inputs(
                CombatRole.RangedPhysical, selfHp: 1.0f, distance: contact,
                enemyCount: 1, selectedSkill: 0, autoAttackLive: false));

            await Assert.That(decision.Arm).IsEqualTo(CombatArm.BackOff);

            var landedM = PreyDistanceM(decision, contact);
            await Assert.That(landedM).IsLessThanOrEqualTo(decision.BandMaxM)
                .Because($"at {contact} m contact the spacing step must land the prey inside the archer's own " +
                          $"{decision.BandMaxM:F2} m band ceiling — the radius its brain is consulted from — " +
                          $"not merely inside the {CombatBrain.AggroBandM:F0} m census");
        }
    }

    /// <summary>
    /// THE PER-ROLE ARITHMETIC, pinned. The binding boundary is each role's own
    /// ceiling, so the leg is bounded by <c>ceiling - 1 - contact</c> — one rule,
    /// no per-role branch and no fitted constant. Read off the band the decision
    /// published rather than re-derived here, so a change to a role's band shows
    /// up as a changed step instead of a silently stale number.
    ///
    /// The caster and the healer are crowded BEYOND melee reach in this table, so
    /// their contacts are 4 m and 6 m — the <c>withinMeleeAutoAttack</c> carve-out
    /// (<c>CombatBrain.cs:336</c>) covers everything at or under
    /// <see cref="CombatBrain.MeleeMaxM"/>, where the engine hands every
    /// non-ranged-physical role the melee auto-attack that reaches, and the
    /// spacing arm correctly does NOT fire. The property this table pins is the
    /// band's own arithmetic, so it is asked at the contacts each role can
    /// actually be spaced from; that carve-out is pinned separately by
    /// <see cref="ACrowdedCasterAtContact_ReachesTheAttackArm_NotBackOff"/>.
    ///
    /// Landed distance: 21 m for the archer (from any of its three contacts, the
    /// contact having been spent), 21 m for the caster, 19 m for the healer — each
    /// its own ceiling less the margin. No role's step collapses: the arm only
    /// fires below the role's own floor, so the shortest leg any role can take is
    /// its ceiling less the margin less its floor.
    /// </summary>
    [Test]
    public async Task SpacingStepPerRole_IsBoundedByThatRolesOwnCeiling()
    {
        foreach (var role in new[]
                 {
                     CombatRole.RangedPhysical, CombatRole.RangedMagic, CombatRole.HealerSupport,
                 })
        foreach (var contact in ContactsFor(role))
        {
            var decision = CombatBrain.Decide(Inputs(
                role, selfHp: 1.0f, distance: contact, enemyCount: 1,
                selectedSkill: 0, autoAttackLive: false));

            await Assert.That(decision.Arm).IsEqualTo(CombatArm.BackOff)
                .Because($"{role} is crowded at {contact} m, below its {decision.BandMinM:F2} m floor");

            var (_, ceiling) = CombatBrain.RoleBand(role);
            await Assert.That(decision.BandMaxM).IsEqualTo(ceiling);
            await Assert.That(PreyDistanceM(decision, contact))
                .IsCloseTo(ceiling - CombatBrain.CloseInMarginM, 0.001f)
                .Because($"the spacing leg for {role} is its ceiling less the close-in margin — the contact is already spent");
        }
    }

    /// <summary>
    /// The contacts at which <paramref name="role"/> can actually be given the
    /// spacing arm: crowded, above the floor it wants to keep, and — for every
    /// role but the archer — beyond the melee reach that hands it a verb it has.
    /// </summary>
    private static float[] ContactsFor(CombatRole role)
        => role == CombatRole.RangedPhysical
            ? [0.5f, 3f, 4f]
            : [4f, 6f];

    /// <summary>
    /// THE ORIGINAL INTENT OF THE BOUND, preserved. The census bound was landed
    /// first and is not what this change relaxes: no role's spacing step may ever
    /// walk the prey past the <see cref="CombatBrain.AggroBandM"/> perception
    /// census, at any contact the arm can fire from. The band ceiling is the
    /// NEARER of the two boundaries, so the census property survives by
    /// construction — and it is pinned rather than assumed.
    /// </summary>
    [Test]
    public async Task NoRolesSpacingStep_EverCrossesThePerceptionCensus()
    {
        foreach (var role in new[]
                 {
                     CombatRole.RangedPhysical, CombatRole.RangedMagic, CombatRole.HealerSupport,
                 })
        {
            var (_, ceiling) = CombatBrain.RoleBand(role);
            var (_, floor) = CombatBrain.RoleBand(role);
            // Every contact the arm can fire at: below the floor, and no closer
            // than standing on the prey.
            for (var contact = 0f; contact < floor; contact += 0.5f)
            {
                var decision = CombatBrain.Decide(Inputs(
                    role, selfHp: 1.0f, distance: contact, enemyCount: 1,
                    selectedSkill: 0, autoAttackLive: false));

                // The melee carve-out: a non-ranged-physical role inside melee
                // reach holds a verb and never reaches this arm.
                if (decision.Arm != CombatArm.BackOff)
                    continue;

                await Assert.That(PreyDistanceM(decision, contact)).IsLessThan(CombatBrain.AggroBandM)
                    .Because($"{role} at {contact:F1} m must land its prey inside the census, never past its edge");
            }

            await Assert.That(ceiling).IsLessThan(CombatBrain.AggroBandM);
        }
    }

    /// <summary>
    /// THE MELEE REGRESSION, named and pinned. Bounding the spacing leg by a
    /// 3.5 m melee ceiling caps that role's leg at 2.5 m — still a real step, but
    /// a small one, so the question this change has to answer is whether the
    /// melee kite still gets room. It does not reach the arm at all: the spacing
    /// arm is exempt for <see cref="CombatRole.Melee"/>
    /// (<c>CombatBrain.cs:337</c>), because the melee floor exists so the
    /// close-in arm stops jittering at contact, not to push a melee bot out of
    /// its own reach. So the melee role's behavior is unchanged by this bound,
    /// and what is pinned here is that unchanged behavior.
    /// </summary>
    [Test]
    public async Task AMeleeRoleAtContact_StillReachesTheAttackArm_AndItsSpacingStepStaysNonDegenerate()
    {
        foreach (var contact in new[] { 0.1f, 0.5f, 1f, 2f, 3.5f })
        {
            var decision = CombatBrain.Decide(Inputs(
                CombatRole.Melee, selfHp: 1.0f, distance: contact, enemyCount: 1,
                selectedSkill: 0, autoAttackLive: false));

            await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.BackOff)
                .Because("the spacing arm is exempt for melee: its floor stops close-in jitter, not its reach");
            await Assert.That(decision.Verb).IsEqualTo(CombatVerb.AutoAttack)
                .Because($"a melee fighter at {contact} m contact holds the melee auto-attack, not a spacing walk");
        }

        // The step the bound WOULD give the melee role, read off the band's own
        // published ceiling: 2.5 m at contact, still 1.5 m at 1 m — a real step at
        // every contact the arm can fire from. A zero step would make the leg a
        // stand-still that re-fires forever; the margin is what keeps it positive.
        var (_, meleeCeiling) = CombatBrain.DesiredBand(CombatRole.Melee, 0f, 3.5f);
        await Assert.That(meleeCeiling).IsEqualTo(CombatBrain.MeleeMaxM)
            .Because("a melee-reach skill does not narrow the melee band, so the step stays ceiling-bound");
        foreach (var contact in new[] { 0f, 0.5f, 1f })
        {
            var step = Math.Min(CombatBrain.RetreatDistanceM,
                Math.Min(CombatBrain.AggroBandM - CombatBrain.CloseInMarginM - contact,
                    meleeCeiling - CombatBrain.CloseInMarginM - contact));
            await Assert.That(step).IsCloseTo(2.5f - contact, 0.001f);
            await Assert.That(step).IsGreaterThan(0f)
                .Because("a degenerate (zero) step would make the spacing leg a stand-still that re-fires forever");
        }
    }

    /// <summary>
    /// The melee exemption that makes the band-ceiling bound unreachable for the
    /// melee role, pinned so the argument in
    /// <see cref="AMeleeRoleAtContact_StillReachesTheAttackArm_AndItsSpacingStepStaysNonDegenerate"/>
    /// cannot rot — for EVERY band shape the melee role can be in, not only the
    /// widest one. A learned skill narrower than the melee band pulls the ceiling
    /// DOWN toward the floor, which is exactly where a ceiling-bound step would
    /// be at risk of collapsing, so it must still never reach the spacing arm.
    /// </summary>
    [Test]
    public async Task AMeleeFighterAtContact_ReachesTheAttackArm_WhateverBandItsSkillNarrowsItTo()
    {
        foreach (var skillMax in new[] { 3.5f, 2f, 1.2f })
        foreach (var contact in new[] { 0.1f, 0.5f, 1f })
        {
            var decision = CombatBrain.Decide(Inputs(
                CombatRole.Melee, selfHp: 1.0f, distance: contact, enemyCount: 1,
                selectedSkill: 0, skillMax: skillMax, autoAttackLive: false));

            await Assert.That(decision.Arm).IsNotEqualTo(CombatArm.BackOff)
                .Because($"with a {skillMax} m skill the melee band is narrowed and the spacing arm must still stay exempt");
        }
    }

    /// <summary>
    /// THE WIRING, live: the leash frame advances inside
    /// <see cref="CombatBrainPlanner.Prepare"/> — the ONE read that runs on every
    /// wake, including the ones the brain takes away without dispatching. That is
    /// the whole fix: leaving the advance to the publish (which only a DISPATCHED
    /// decision reaches) froze the anchor on exactly the wakes that needed it to
    /// move, and one past-edge frame then re-fired the leash on the same prey for
    /// 19,756 consecutive wakes in the recorded run.
    ///
    /// Driven through the real planner so a missing call fails here, not only the
    /// pure-chain cases above.
    /// </summary>
    [Test]
    public async Task ThePlanner_AdvancesTheLeashFrameOnEveryReadableWake()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("leash-reanchor-wiring");
        actor.Character.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Fight;
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var region = session.World.GetRegionByPos(here);
        await Assert.That(region).IsNotNull();
        region!.AddObject(actor.Character);
        actor.Character.Region = region;

        var boarObjId = session.SpawnNpc(3475u);
        var boar = session.World.GetNpc(boarObjId);
        await Assert.That(boar).IsNotNull();
        boar!.Template = new AAEmu.Game.Models.Game.NPChar.NpcTemplate { Id = 3475u, Scale = 1f };
        boar.Hp = 100;
        boar.MaxHp = 100;
        boar.IsVisible = true;

        // Wake 1: the prey is right here. The publish banks this as the anchor.
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(2f, 0f, 0f));
        CombatBrainPlanner.PublishDispatched(actor, CombatBrain.Decide(
            CombatInputs(here + new Vector3(2f, 0f, 0f), actor.ActorId, boarObjId)), Wake);
        await Assert.That(CombatBrainEngagement.TryGet(actor.ActorId, out var banked)).IsTrue();
        var anchored = banked.LeashAnchor;
        await Assert.That(anchored).IsNotEqualTo(Vector3.Zero);

        // Wake 2: the prey WANDERED — but no decision is dispatched for it (this
        // is the withheld-wake shape: the brain takes the wake away and the
        // publish never runs). The anchor must still have followed the prey.
        var wandered = here + new Vector3(30f, 0f, 0f);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, wandered);
        _ = CombatBrainPlanner.Prepare(actor, null, boarObjId, allowSkillCasts: false, Wake.AddSeconds(1));

        await Assert.That(CombatBrainEngagement.TryGet(actor.ActorId, out var advanced)).IsTrue();
        await Assert.That(advanced.LeashAnchor).IsEqualTo(wandered);
        await Assert.That(advanced.LeashAnchor).IsNotEqualTo(anchored);
        // And the window did not restart: the frame is a measurement.
        await Assert.That(advanced.CommittedUtc).IsEqualTo(banked.CommittedUtc);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThePlanner_OnlyMeasuresDriftFromTheSameMob(bool changedTarget)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("quest-target-leash-identity");
        actor.Character.Ability1 = AAEmu.Game.Models.Game.Skills.AbilityType.Fight;
        var previousPosition = new Vector3(100f, 100f, 10f);
        var nextPosition = previousPosition + new Vector3(550f, 0f, 0f);
        GameplayActorTestRig.SetPosition(actor, nextPosition - new Vector3(2f, 0f, 0f));
        var region = session.World.GetRegionByPos(actor.Character.Transform.World.Position);
        region!.AddObject(actor.Character);
        actor.Character.Region = region;

        var previousId = session.SpawnNpc(3475u);
        var selectedId = changedTarget ? session.SpawnNpc(3484u) : previousId;
        var selected = session.World.GetNpc(selectedId)!;
        selected.Template = new AAEmu.Game.Models.Game.NPChar.NpcTemplate
        {
            Id = changedTarget ? 3484u : 3475u, Scale = 1f, ReturnDistance = 50f
        };
        selected.Hp = 100;
        selected.MaxHp = 100;
        selected.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, selectedId, nextPosition);
        CombatBrainEngagement.Publish(actor.ActorId, previousId, 1000,
            previousPosition, 50f, false, false, Wake);
        CombatBrainEngagement.PublishSkillUsed(actor.ActorId, 10399u, Wake);

        var prepared = CombatBrainPlanner.Prepare(actor, null, selectedId,
            allowSkillCasts: false, Wake.AddSeconds(1));
        var decision = CombatBrainPlanner.Decide(prepared);
        await Assert.That(prepared.Inputs.IncumbentLeashDriftM)
            .IsEqualTo(changedTarget ? 0f : 550f);
        await Assert.That(decision.DisengageReason == CombatDisengageReason.LeashEdge)
            .IsEqualTo(!changedTarget);
        if (changedTarget)
        {
            await Assert.That(prepared.Inputs.Candidates.Single().LeashDistanceM).IsEqualTo(0f);
            await Assert.That(decision.TargetObjId).IsEqualTo(selectedId);
        }
        // Preparing a new opportunity cannot publish it or reset the old rotation.
        await Assert.That(CombatBrainEngagement.TryGet(actor.ActorId, out var stored)).IsTrue();
        await Assert.That(stored.IncumbentObjId).IsEqualTo(previousId);
        await Assert.That(stored.CommittedUtc).IsEqualTo(Wake);
        await Assert.That(stored.LastSkillUsed).IsEqualTo(10399u);
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

    /// <summary>
    /// A wake whose prey stands at <paramref name="preyPosition"/>: the anchor
    /// source a publish reads. Only the three fields a leash measurement needs
    /// differ from <see cref="Inputs"/> — the rest describe a healthy melee bot
    /// standing on the target.
    /// </summary>
    private static CombatBrainInputs CombatInputs(
        Vector3 preyPosition, uint actorObjId = 70001, uint incumbentObjId = BoarObjId,
        bool commitmentInForce = true, float leashDrift = 0f)
        => Inputs(CombatRole.Melee, selfHp: 1.0f, distance: 2f, enemyCount: 1,
            selectedSkill: 18131, skillMax: 4f, incumbentObjId: incumbentObjId,
            commitmentInForce: commitmentInForce, leashDrift: leashDrift) with
        {
            ActorObjId = actorObjId,
            SelfPosition = preyPosition + new Vector3(-2f, 0f, 0f),
            NearestEnemyDistanceM = 2f,
            IncumbentPosition = preyPosition
        };
}
