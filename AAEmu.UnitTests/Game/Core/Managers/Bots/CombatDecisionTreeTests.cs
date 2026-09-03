using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class CombatDecisionTreeTests
{
    private static (Character bot, Npc target) CreateMockCombatants(Vector3 botPos, Vector3 targetPos, int hp = 1000, int maxHp = 1000)
    {
        var (_, session) = GameplayActorTestRig.CreateActor("test-combat-bot-" + Guid.NewGuid().ToString("N"));
        var bot = session.Character;
        bot.MaxHp = maxHp;
        bot.Hp = hp;
        bot.MaxMp = 500;
        bot.Mp = 500;
        bot.Ability1 = AbilityType.Fight;
        bot.Ability2 = AbilityType.Adamant;
        bot.Ability3 = AbilityType.Will;
        bot.Transform.World.Position = botPos;

        var target = new Npc
        {
            ObjId = 8881,
            Name = "Forest Wolf",
            MaxHp = 500,
            Hp = 500
        };
        target.Transform.World.Position = targetPos;

        return (bot, target);
    }

    [Test]
    public async Task InferRole_CorrectlyClassifiesSkillTrees()
    {
        var (_, session) = GameplayActorTestRig.CreateActor("test-role-bot");
        var bot = session.Character;

        bot.Ability1 = AbilityType.Wild;
        bot.Ability2 = AbilityType.None;
        bot.Ability3 = AbilityType.None;
        await Assert.That(CombatDecisionTree.InferRole(bot)).IsEqualTo(CombatRole.RangedPhysical);

        bot.Ability1 = AbilityType.Magic;
        bot.Ability2 = AbilityType.None;
        bot.Ability3 = AbilityType.None;
        await Assert.That(CombatDecisionTree.InferRole(bot)).IsEqualTo(CombatRole.RangedMagic);

        bot.Ability1 = AbilityType.Love;
        bot.Ability2 = AbilityType.None;
        bot.Ability3 = AbilityType.None;
        await Assert.That(CombatDecisionTree.InferRole(bot)).IsEqualTo(CombatRole.HealerSupport);

        bot.Ability1 = AbilityType.Fight;
        bot.Ability2 = AbilityType.None;
        bot.Ability3 = AbilityType.None;
        await Assert.That(CombatDecisionTree.InferRole(bot)).IsEqualTo(CombatRole.Melee);
    }

    [Test]
    public async Task Evaluate_Melee_BeyondReach_ProposesCloseGap()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(108, 100, 10)); // dist = 8m
        var decision = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee);

        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.CloseGap);
        await Assert.That(decision.Priority).IsEqualTo(600);
        await Assert.That(decision.Rationale.Contains("close-gap-melee-reach")).IsTrue();
    }

    [Test]
    public async Task Evaluate_Ranged_TargetTooClose_ProposesKiteSpacing()
    {
        // Target is in melee range (3m), but bot is an Archer
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(103, 100, 10)); // dist = 3m
        var decision = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedPhysical);

        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.KiteSpacing);
        await Assert.That(decision.Priority).IsEqualTo(700);
        await Assert.That(decision.Rationale.Contains("kite-spacing-target-too-close")).IsTrue();
        await Assert.That(decision.TargetPosition.HasValue).IsTrue();
        // Target pos is at X=103, bot is at X=100. Kiting away from target means stepping towards X=90
        await Assert.That(decision.TargetPosition!.Value.X).IsLessThan(100f);
    }

    [Test]
    public async Task Evaluate_LowHp_ProposesEmergencyFlee()
    {
        // Bot HP is set to 10% of MaxHp (<= 20% critical threshold)
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(102, 100, 10));
        bot.Hp = Math.Max(1, (int)(bot.MaxHp * 0.10f));
        var decision = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee);

        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.EmergencyFlee);
        await Assert.That(decision.Priority).IsEqualTo(1000);
        await Assert.That(decision.Rationale.Contains("emergency-flee-hp-critical")).IsTrue();
    }

    [Test]
    public async Task Evaluate_InIdealRange_ProposesCastSkill()
    {
        // Melee bot at 2m (within 1.0 - 3.5m)
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(102, 100, 10));
        var decision = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: [18131]);

        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(decision.Priority).IsEqualTo(300);
        await Assert.That(decision.SkillId).IsEqualTo(18131u);
        await Assert.That(decision.Rationale.Contains("cast-skill-in-ideal-range")).IsTrue();
    }
}
