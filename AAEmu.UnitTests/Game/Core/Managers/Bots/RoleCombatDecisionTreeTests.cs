using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class RoleCombatDecisionTreeTests
{
    private static Character CreateCharacter(string name, Vector3 pos, int hp = 1000, int maxHp = 1000)
    {
        var (_, session) = GameplayActorTestRig.CreateActor(name + "-" + Guid.NewGuid().ToString("N"));
        var chr = session.Character;
        chr.MaxHp = maxHp;
        chr.Hp = hp;
        chr.MaxMp = 500;
        chr.Mp = 500;
        chr.Transform.Local.SetPosition(pos);
        return chr;
    }

    private static Npc CreateEnemy(uint objId, string name, Vector3 pos, BaseUnit? target = null)
    {
        var npc = new Npc
        {
            ObjId = objId,
            Name = name,
            MaxHp = 800,
            Hp = 800,
            CurrentTarget = target
        };
        npc.Transform.Local.SetPosition(pos);
        return npc;
    }

    [Test]
    public async Task Tank_WhenAllyTargeted_PeelsThreatWithChargeOrGapCloser()
    {
        var tank = CreateCharacter("TankBot", new Vector3(100, 100, 10));
        var leader = CreateCharacter("Leader", new Vector3(105, 105, 10));
        var enemy = CreateEnemy(9001, "Goblin Raider", new Vector3(106, 106, 10), target: leader);

        var skills = new List<uint>
        {
            CombatDecisionTree.BattlerageChargeSkillId,
            CombatDecisionTree.DefenseShieldSlamSkillId
        };

        var decision = RoleCombatDecisionTree.EvaluatePartyCombat(
            tank,
            CompanionRole.Tank,
            partyLeader: leader,
            partyMembers: [tank, leader],
            primaryTarget: null,
            nearbyEnemies: [enemy],
            availableSkills: skills);

        await Assert.That(decision.TargetObjId).IsEqualTo(enemy.ObjId);
        await Assert.That(decision.Action == CombatTacticalAction.CastSkill || decision.Action == CombatTacticalAction.CloseGap).IsTrue();
    }

    [Test]
    public async Task Tank_InMeleeRange_ExecutesTauntAndStunRotation()
    {
        var tank = CreateCharacter("TankBot", new Vector3(100, 100, 10));
        var boss = CreateEnemy(9002, "Dungeon Boss", new Vector3(102, 100, 10)); // 2m dist

        var skills = new List<uint>
        {
            CombatDecisionTree.DefenseShieldSlamSkillId,
            CombatDecisionTree.DefenseBullRushSkillId,
            CombatDecisionTree.BattlerageTripleSlashSkillId
        };

        var decision = RoleCombatDecisionTree.EvaluatePartyCombat(
            tank,
            CompanionRole.Tank,
            partyLeader: null,
            partyMembers: [tank],
            primaryTarget: boss,
            nearbyEnemies: [boss],
            availableSkills: skills);

        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(decision.SkillId).IsEqualTo(CombatDecisionTree.DefenseShieldSlamSkillId);
        await Assert.That(decision.TargetObjId).IsEqualTo(boss.ObjId);
    }

    [Test]
    public async Task Healer_WhenAllyLowHp_PrioritizesHealingOverAttacking()
    {
        var healer = CreateCharacter("HealerBot", new Vector3(90, 100, 10));
        var injuredLeader = CreateCharacter("InjuredLeader", new Vector3(100, 100, 10), hp: 100, maxHp: 370); // ~27% HP (MaxHp in 1.2 is 370)
        var boss = CreateEnemy(9003, "Raid Boss", new Vector3(102, 100, 10));

        var skills = new List<uint>
        {
            CombatDecisionTree.VitalismResurgenceSkillId,
            CombatDecisionTree.VitalismAntithesisSkillId,
            CombatDecisionTree.SongcraftCriticalDiscordSkillId
        };

        var decision = RoleCombatDecisionTree.EvaluatePartyCombat(
            healer,
            CompanionRole.Healer,
            partyLeader: injuredLeader,
            partyMembers: [healer, injuredLeader],
            primaryTarget: boss,
            nearbyEnemies: [boss],
            availableSkills: skills);

        // Must heal the injured ally!
        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(decision.TargetObjId).IsEqualTo(injuredLeader.ObjId);
        await Assert.That(decision.SkillId).IsEqualTo(CombatDecisionTree.VitalismResurgenceSkillId);
    }

    [Test]
    public async Task Healer_WhenPartyHealthy_SupportsRangedDamage()
    {
        var healer = CreateCharacter("HealerBot", new Vector3(88, 100, 10));
        var healthyLeader = CreateCharacter("HealthyLeader", new Vector3(100, 100, 10), hp: 950, maxHp: 1000);
        var mob = CreateEnemy(9004, "Wolf", new Vector3(102, 100, 10)); // 14m dist from healer

        var skills = new List<uint>
        {
            CombatDecisionTree.VitalismAntithesisSkillId,
            CombatDecisionTree.SongcraftCriticalDiscordSkillId
        };

        var decision = RoleCombatDecisionTree.EvaluatePartyCombat(
            healer,
            CompanionRole.Healer,
            partyLeader: healthyLeader,
            partyMembers: [healer, healthyLeader],
            primaryTarget: mob,
            nearbyEnemies: [mob],
            availableSkills: skills);

        // When party is healthy, contributes damage/debuff to the enemy from safe distance
        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.CastSkill);
        await Assert.That(decision.TargetObjId).IsEqualTo(mob.ObjId);
    }

    [Test]
    public async Task Dps_AssistsLeaderTarget_AndExecutesRotation()
    {
        var dps = CreateCharacter("DpsBot", new Vector3(100, 100, 10));
        dps.Ability1 = AbilityType.Fight;
        dps.Ability2 = AbilityType.Vocation;
        dps.Ability3 = AbilityType.None;

        var leader = CreateCharacter("Leader", new Vector3(102, 100, 10));
        var mobA = CreateEnemy(9005, "Target A", new Vector3(101, 102, 10));
        var mobB = CreateEnemy(9006, "Target B", new Vector3(115, 100, 10));

        // Leader is focusing on mobA
        leader.CurrentTarget = mobA;

        var skills = new List<uint>
        {
            CombatDecisionTree.BattlerageTripleSlashSkillId,
            CombatDecisionTree.ShadowplayRapidStrikesSkillId
        };

        var decision = RoleCombatDecisionTree.EvaluatePartyCombat(
            dps,
            CompanionRole.Dps,
            partyLeader: leader,
            partyMembers: [dps, leader],
            primaryTarget: mobB, // default target is B, but leader target is A!
            nearbyEnemies: [mobA, mobB],
            availableSkills: skills);

        // Must assist leader on Target A!
        await Assert.That(decision.TargetObjId).IsEqualTo(mobA.ObjId);
        await Assert.That(decision.Action).IsEqualTo(CombatTacticalAction.CastSkill);
    }
}
