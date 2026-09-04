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

    [Test]
    public async Task Evaluate_BattlerageCombo_PrioritizesChargeThenTripleSlashThenWhirlwind()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(102, 100, 10));
        var skills = new uint[]
        {
            CombatDecisionTree.BattlerageWhirlwindSkillId,
            CombatDecisionTree.BattlerageTripleSlashSkillId,
            CombatDecisionTree.BattlerageChargeSkillId
        };

        // Round 1: Opener -> Charge
        var d1 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: 0);
        await Assert.That(d1.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d1.SkillId).IsEqualTo(CombatDecisionTree.BattlerageChargeSkillId);

        // Round 2: After Charge -> Triple Slash
        var d2 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: CombatDecisionTree.BattlerageChargeSkillId);
        await Assert.That(d2.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d2.SkillId).IsEqualTo(CombatDecisionTree.BattlerageTripleSlashSkillId);

        // Round 3: After Triple Slash -> Whirlwind Slash
        var d3 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: CombatDecisionTree.BattlerageTripleSlashSkillId);
        await Assert.That(d3.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d3.SkillId).IsEqualTo(CombatDecisionTree.BattlerageWhirlwindSkillId);
    }

    [Test]
    public async Task Evaluate_SorceryCombo_PrioritizesFlameboltThenFreezingArrow()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(116, 100, 10)); // dist = 16m
        var skills = new uint[]
        {
            CombatDecisionTree.SorceryChainLightningSkillId,
            CombatDecisionTree.SorceryFreezingArrowSkillId,
            CombatDecisionTree.SorceryFlameboltSkillId
        };

        // Round 1: Opener -> Flamebolt (inflicts Burn)
        var d1 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedMagic, availableSkills: skills, lastSkillUsed: 0);
        await Assert.That(d1.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d1.SkillId).IsEqualTo(CombatDecisionTree.SorceryFlameboltSkillId);

        // Round 2: After Flamebolt -> Freezing Arrow (bonus vs Burned)
        var d2 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedMagic, availableSkills: skills, lastSkillUsed: CombatDecisionTree.SorceryFlameboltSkillId);
        await Assert.That(d2.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d2.SkillId).IsEqualTo(CombatDecisionTree.SorceryFreezingArrowSkillId);

        // Round 3: After Freezing Arrow -> Chain Lightning
        var d3 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedMagic, availableSkills: skills, lastSkillUsed: CombatDecisionTree.SorceryFreezingArrowSkillId);
        await Assert.That(d3.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d3.SkillId).IsEqualTo(CombatDecisionTree.SorceryChainLightningSkillId);
    }

    [Test]
    public async Task Evaluate_ArcheryCombo_PrioritizesChargedBoltThenEndlessArrows()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(116, 100, 10)); // dist = 16m
        var skills = new uint[]
        {
            CombatDecisionTree.ArcheryEndlessArrowsSkillId,
            CombatDecisionTree.ArcheryChargedBoltSkillId
        };

        // Round 1: Opener -> Charged Bolt (inflicts Slow)
        var d1 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedPhysical, availableSkills: skills, lastSkillUsed: 0);
        await Assert.That(d1.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d1.SkillId).IsEqualTo(CombatDecisionTree.ArcheryChargedBoltSkillId);

        // Round 2: After Charged Bolt -> Endless Arrows (bonus vs Slowed)
        var d2 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedPhysical, availableSkills: skills, lastSkillUsed: CombatDecisionTree.ArcheryChargedBoltSkillId);
        await Assert.That(d2.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d2.SkillId).IsEqualTo(CombatDecisionTree.ArcheryEndlessArrowsSkillId);
    }

    [Test]
    public async Task Evaluate_HealerSupport_Under70Hp_PrioritizesResurgence()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(112, 100, 10)); // dist = 12m
        var skills = new uint[]
        {
            CombatDecisionTree.VitalismAntithesisSkillId,
            CombatDecisionTree.VitalismResurgenceSkillId
        };

        // When HP is healthy (100%), cast offensive Antithesis
        bot.Hp = bot.MaxHp;
        var dHealthy = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.HealerSupport, availableSkills: skills);
        await Assert.That(dHealthy.SkillId).IsEqualTo(CombatDecisionTree.VitalismAntithesisSkillId);

        // When HP is below 70% (50%), prioritize defensive HoT Resurgence
        bot.Hp = Math.Max(1, (int)(bot.MaxHp * 0.50f));
        var dInjured = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.HealerSupport, availableSkills: skills);
        await Assert.That(dInjured.SkillId).IsEqualTo(CombatDecisionTree.VitalismResurgenceSkillId);
    }

    [Test]
    public async Task Evaluate_DefenseCombo_PrioritizesShieldSlamThenBullRush()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(102, 100, 10)); // dist = 2m
        var skills = new uint[]
        {
            CombatDecisionTree.DefenseBullRushSkillId,
            CombatDecisionTree.DefenseShieldSlamSkillId
        };

        // Round 1: Opener -> Shield Slam (Stuns target)
        var d1 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: 0);
        await Assert.That(d1.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d1.SkillId).IsEqualTo(CombatDecisionTree.DefenseShieldSlamSkillId);

        // Round 2: After Shield Slam -> Bull Rush (Trips stunned target)
        var d2 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: CombatDecisionTree.DefenseShieldSlamSkillId);
        await Assert.That(d2.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d2.SkillId).IsEqualTo(CombatDecisionTree.DefenseBullRushSkillId);
    }

    [Test]
    public async Task Evaluate_ShadowplayCombo_PrioritizesOverwhelmThenShadowsmiteThenRapidStrikes()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(102, 100, 10)); // dist = 2m
        var skills = new uint[]
        {
            CombatDecisionTree.ShadowplayRapidStrikesSkillId,
            CombatDecisionTree.ShadowplayShadowsmiteSkillId,
            CombatDecisionTree.ShadowplayOverwhelmSkillId
        };

        // Round 1: Opener -> Overwhelm (Gap-close leap & stun)
        var d1 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: 0);
        await Assert.That(d1.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d1.SkillId).IsEqualTo(CombatDecisionTree.ShadowplayOverwhelmSkillId);

        // Round 2: After Overwhelm -> Shadowsmite (Trips stunned target)
        var d2 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: CombatDecisionTree.ShadowplayOverwhelmSkillId);
        await Assert.That(d2.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d2.SkillId).IsEqualTo(CombatDecisionTree.ShadowplayShadowsmiteSkillId);

        // Round 3: After Shadowsmite -> Rapid Strikes (Filler)
        var d3 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.Melee, availableSkills: skills, lastSkillUsed: CombatDecisionTree.ShadowplayShadowsmiteSkillId);
        await Assert.That(d3.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d3.SkillId).IsEqualTo(CombatDecisionTree.ShadowplayRapidStrikesSkillId);
    }

    [Test]
    public async Task Evaluate_WitchcraftCombo_PrioritizesEnervateThenEarthenGrip()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(115, 100, 10)); // dist = 15m
        var skills = new uint[]
        {
            CombatDecisionTree.WitchcraftEarthenGripSkillId,
            CombatDecisionTree.WitchcraftEnervateSkillId
        };

        // Round 1: Opener -> Enervate (Debuff & mana burn)
        var d1 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedMagic, availableSkills: skills, lastSkillUsed: 0);
        await Assert.That(d1.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d1.SkillId).IsEqualTo(CombatDecisionTree.WitchcraftEnervateSkillId);

        // Round 2: After Enervate -> Earthen Grip (Snare & life drain combo)
        var d2 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.RangedMagic, availableSkills: skills, lastSkillUsed: CombatDecisionTree.WitchcraftEnervateSkillId);
        await Assert.That(d2.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d2.SkillId).IsEqualTo(CombatDecisionTree.WitchcraftEarthenGripSkillId);
    }

    [Test]
    public async Task Evaluate_SongcraftCombo_PrioritizesStartlingStrainThenCriticalDiscord()
    {
        var (bot, target) = CreateMockCombatants(new Vector3(100, 100, 10), new Vector3(112, 100, 10)); // dist = 12m
        var skills = new uint[]
        {
            CombatDecisionTree.SongcraftCriticalDiscordSkillId,
            CombatDecisionTree.SongcraftStartlingStrainSkillId
        };

        // Round 1: Opener -> Startling Strain (Stuns and Charms target)
        var d1 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.HealerSupport, availableSkills: skills, lastSkillUsed: 0);
        await Assert.That(d1.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d1.SkillId).IsEqualTo(CombatDecisionTree.SongcraftStartlingStrainSkillId);

        // Round 2: After Startling Strain -> Critical Discord (Amplified vs Charmed)
        var d2 = CombatDecisionTree.Evaluate(bot, target, roleOverride: CombatRole.HealerSupport, availableSkills: skills, lastSkillUsed: CombatDecisionTree.SongcraftStartlingStrainSkillId);
        await Assert.That(d2.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(d2.SkillId).IsEqualTo(CombatDecisionTree.SongcraftCriticalDiscordSkillId);
    }
}
