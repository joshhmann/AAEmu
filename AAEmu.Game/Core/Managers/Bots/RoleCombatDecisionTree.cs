using System.Numerics;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Multi-role combat decision engine for companion bot parties.
/// Dynamically coordinates Tank, Healer, and DPS behaviors:
/// - Tank: Peels threats off allies, maintains boss aggro, activates defensive cooldowns.
/// - Healer: Monopolizes triage on the lowest-health ally (Resurgence / Antithesis), stays safe.
/// - DPS: Assist-targets leader's focus, executes optimal combo chains, maintains tactical spacing.
/// </summary>
public static class RoleCombatDecisionTree
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public const float AllyHealThreshold = 0.75f;
    public const float CriticalHealThreshold = 0.40f;
    public const float TankDefensiveThreshold = 0.50f;
    public const float SafeHealerDistance = 14.0f;

    /// <summary>
    /// Evaluates tactical combat decision for a companion bot based on its assigned party role.
    /// </summary>
    public static CombatDecision EvaluatePartyCombat(
        Character bot,
        CompanionRole role,
        Character? partyLeader,
        IReadOnlyList<Character>? partyMembers,
        Unit? primaryTarget,
        IReadOnlyList<Unit>? nearbyEnemies = null,
        IReadOnlyList<uint>? availableSkills = null,
        uint lastSkillUsed = 0)
    {
        ArgumentNullException.ThrowIfNull(bot);

        var resolvedSkills = availableSkills ?? (bot.Skills?.Skills.Keys.ToList() as IReadOnlyList<uint>) ?? [];
        var skillSet = new HashSet<uint>(resolvedSkills);
        var enemies = nearbyEnemies ?? [];

        // -------------------------------------------------------------
        // 1. TANK ROLE
        // -------------------------------------------------------------
        if (role == CompanionRole.Tank)
        {
            return EvaluateTank(bot, partyLeader, partyMembers, primaryTarget, enemies, skillSet, lastSkillUsed);
        }

        // -------------------------------------------------------------
        // 2. HEALER ROLE
        // -------------------------------------------------------------
        if (role == CompanionRole.Healer)
        {
            return EvaluateHealer(bot, partyLeader, partyMembers, primaryTarget, enemies, skillSet, lastSkillUsed);
        }

        // -------------------------------------------------------------
        // 3. DPS ROLE
        // -------------------------------------------------------------
        return EvaluateDps(bot, partyLeader, primaryTarget, enemies, skillSet, lastSkillUsed);
    }

    private static CombatDecision EvaluateTank(
        Character bot,
        Character? partyLeader,
        IReadOnlyList<Character>? partyMembers,
        Unit? primaryTarget,
        IReadOnlyList<Unit> enemies,
        HashSet<uint> skills,
        uint lastSkillUsed)
    {
        var botHpPercent = bot.MaxHp > 0 ? (float)bot.Hp / bot.MaxHp : 1.0f;
        var botPos = bot.Transform.World.Position;

        // Check if any ally (leader or fellow bot) is being targeted by an enemy
        var allAllies = new List<Character>();
        if (partyLeader != null && partyLeader.ObjId != bot.ObjId)
            allAllies.Add(partyLeader);
        if (partyMembers != null)
        {
            foreach (var m in partyMembers)
            {
                if (m.ObjId != bot.ObjId && !allAllies.Any(a => a.ObjId == m.ObjId))
                    allAllies.Add(m);
            }
        }

        // Peel check: find enemy attacking non-tank ally
        Unit? threatToPeel = null;
        foreach (var enemy in enemies)
        {
            var enemyTargetId = enemy.CurrentTarget?.ObjId ?? 0;
            if (enemyTargetId > 0 && allAllies.Any(a => a.ObjId == enemyTargetId))
            {
                threatToPeel = enemy;
                break;
            }
        }

        var effectiveTarget = threatToPeel ?? primaryTarget;
        if (effectiveTarget == null)
        {
            return new CombatDecision(CombatTacticalAction.HoldAndRegen, "tank-no-target", 100);
        }

        var targetPos = effectiveTarget.Transform.World.Position;
        var dist = Vector3.Distance(botPos, targetPos);

        // Tank defensive trigger when low on health
        if (botHpPercent < TankDefensiveThreshold)
        {
            // Defensive / crowd control skill
            if (skills.Contains(CombatDecisionTree.DefenseShieldSlamSkillId) &&
                CombatDecisionTree.IsSkillInRangeAndReady(bot, effectiveTarget, CombatDecisionTree.DefenseShieldSlamSkillId, dist))
            {
                return new CombatDecision(
                    CombatTacticalAction.CastSkill,
                    $"tank-defensive-shield-slam-stun (hp={botHpPercent:P0})",
                    850,
                    SkillId: CombatDecisionTree.DefenseShieldSlamSkillId,
                    TargetObjId: effectiveTarget.ObjId);
            }
        }

        // Gap closer to hold threat
        if (dist > CombatDecisionTree.DefaultMeleeMax)
        {
            if (dist <= 12.0f && skills.Contains(CombatDecisionTree.BattlerageChargeSkillId) &&
                CombatDecisionTree.IsSkillInRangeAndReady(bot, effectiveTarget, CombatDecisionTree.BattlerageChargeSkillId, dist))
            {
                return new CombatDecision(
                    CombatTacticalAction.CastSkill,
                    $"tank-peel-charge ({effectiveTarget.ObjId})",
                    750,
                    SkillId: CombatDecisionTree.BattlerageChargeSkillId,
                    TargetObjId: effectiveTarget.ObjId);
            }

            return new CombatDecision(
                CombatTacticalAction.CloseGap,
                $"tank-close-gap ({dist:F1}m > {CombatDecisionTree.DefaultMeleeMax:F1}m)",
                600,
                TargetPosition: targetPos,
                TargetObjId: effectiveTarget.ObjId);
        }

        // Taunt / Aggro rotation: Shield Slam (10399) -> Bull Rush (10501) -> Triple Slash (18131)
        if (skills.Contains(CombatDecisionTree.DefenseShieldSlamSkillId) &&
            CombatDecisionTree.IsSkillInRangeAndReady(bot, effectiveTarget, CombatDecisionTree.DefenseShieldSlamSkillId, dist))
        {
            return new CombatDecision(
                CombatTacticalAction.CastSkill,
                "tank-shield-slam-stun",
                500,
                SkillId: CombatDecisionTree.DefenseShieldSlamSkillId,
                TargetObjId: effectiveTarget.ObjId);
        }

        if (skills.Contains(CombatDecisionTree.DefenseBullRushSkillId) &&
            CombatDecisionTree.IsSkillInRangeAndReady(bot, effectiveTarget, CombatDecisionTree.DefenseBullRushSkillId, dist))
        {
            return new CombatDecision(
                CombatTacticalAction.CastSkill,
                "tank-bull-rush-trip",
                480,
                SkillId: CombatDecisionTree.DefenseBullRushSkillId,
                TargetObjId: effectiveTarget.ObjId);
        }

        if (skills.Contains(CombatDecisionTree.BattlerageTripleSlashSkillId) &&
            CombatDecisionTree.IsSkillInRangeAndReady(bot, effectiveTarget, CombatDecisionTree.BattlerageTripleSlashSkillId, dist))
        {
            return new CombatDecision(
                CombatTacticalAction.CastSkill,
                "tank-triple-slash-threat",
                450,
                SkillId: CombatDecisionTree.BattlerageTripleSlashSkillId,
                TargetObjId: effectiveTarget.ObjId);
        }

        return CombatDecisionTree.Evaluate(bot, effectiveTarget, CombatRole.Melee, dist, skills.ToList(), lastSkillUsed);
    }

    private static CombatDecision EvaluateHealer(
        Character bot,
        Character? partyLeader,
        IReadOnlyList<Character>? partyMembers,
        Unit? primaryTarget,
        IReadOnlyList<Unit> enemies,
        HashSet<uint> skills,
        uint lastSkillUsed)
    {
        var botPos = bot.Transform.World.Position;

        // Build roster of allies including self
        var roster = new List<Character> { bot };
        if (partyLeader != null && partyLeader.ObjId != bot.ObjId)
            roster.Add(partyLeader);
        if (partyMembers != null)
        {
            foreach (var m in partyMembers)
            {
                if (!roster.Any(r => r.ObjId == m.ObjId))
                    roster.Add(m);
            }
        }

        // Triage: find lowest HP percentage ally
        Character? lowestAlly = null;
        float lowestHpRatio = 1.0f;
        foreach (var ally in roster)
        {
            if (ally.MaxHp <= 0) continue;
            var ratio = (float)ally.Hp / ally.MaxHp;
            if (ratio < lowestHpRatio)
            {
                lowestHpRatio = ratio;
                lowestAlly = ally;
            }
        }

        // If any ally needs healing (< 75%)
        if (lowestAlly != null && lowestHpRatio < AllyHealThreshold)
        {
            var allyDist = Vector3.Distance(botPos, lowestAlly.Transform.World.Position);

            // Critical HoT or Burst Heal
            if (skills.Contains(CombatDecisionTree.VitalismResurgenceSkillId) &&
                CombatDecisionTree.IsSkillInRangeAndReady(bot, lowestAlly, CombatDecisionTree.VitalismResurgenceSkillId, allyDist))
            {
                return new CombatDecision(
                    CombatTacticalAction.CastSkill,
                    $"healer-resurgence-hot on {lowestAlly.Name} ({lowestHpRatio:P0})",
                    900,
                    SkillId: CombatDecisionTree.VitalismResurgenceSkillId,
                    TargetObjId: lowestAlly.ObjId);
            }

            if (skills.Contains(CombatDecisionTree.VitalismAntithesisSkillId) &&
                CombatDecisionTree.IsSkillInRangeAndReady(bot, lowestAlly, CombatDecisionTree.VitalismAntithesisSkillId, allyDist))
            {
                return new CombatDecision(
                    CombatTacticalAction.CastSkill,
                    $"healer-antithesis-burst on {lowestAlly.Name} ({lowestHpRatio:P0})",
                    880,
                    SkillId: CombatDecisionTree.VitalismAntithesisSkillId,
                    TargetObjId: lowestAlly.ObjId);
            }

            // Close distance if ally is out of heal range (> 25m)
            if (allyDist > 25.0f)
            {
                return new CombatDecision(
                    CombatTacticalAction.CloseGap,
                    $"healer-move-to-injured-ally ({allyDist:F1}m > 25m)",
                    700,
                    TargetPosition: lowestAlly.Transform.World.Position,
                    TargetObjId: lowestAlly.ObjId);
            }
        }

        // Party is healthy: position safely and support DPS from range
        if (primaryTarget != null)
        {
            var targetDist = Vector3.Distance(botPos, primaryTarget.Transform.World.Position);

            // Step back if enemy gets too close (< 8m)
            if (targetDist < 8.0f)
            {
                var kiteDir = Vector3.Normalize(botPos - primaryTarget.Transform.World.Position);
                if (kiteDir == Vector3.Zero || !float.IsFinite(kiteDir.X))
                    kiteDir = new Vector3(0, -1, 0);

                var kiteTarget = botPos + kiteDir * 8.0f;
                return new CombatDecision(
                    CombatTacticalAction.KiteSpacing,
                    $"healer-spacing-too-close ({targetDist:F1}m < 8m)",
                    650,
                    TargetPosition: kiteTarget,
                    TargetObjId: primaryTarget.ObjId);
            }

            // Ranged poke
            if (skills.Contains(CombatDecisionTree.VitalismAntithesisSkillId) &&
                CombatDecisionTree.IsSkillInRangeAndReady(bot, primaryTarget, CombatDecisionTree.VitalismAntithesisSkillId, targetDist))
            {
                return new CombatDecision(
                    CombatTacticalAction.CastSkill,
                    "healer-antithesis-offensive-support",
                    400,
                    SkillId: CombatDecisionTree.VitalismAntithesisSkillId,
                    TargetObjId: primaryTarget.ObjId);
            }

            if (skills.Contains(CombatDecisionTree.SongcraftCriticalDiscordSkillId) &&
                CombatDecisionTree.IsSkillInRangeAndReady(bot, primaryTarget, CombatDecisionTree.SongcraftCriticalDiscordSkillId, targetDist))
            {
                return new CombatDecision(
                    CombatTacticalAction.CastSkill,
                    "healer-songcraft-discord-support",
                    380,
                    SkillId: CombatDecisionTree.SongcraftCriticalDiscordSkillId,
                    TargetObjId: primaryTarget.ObjId);
            }
        }

        return new CombatDecision(CombatTacticalAction.HoldAndRegen, "healer-hold-watch-party", 150);
    }

    private static CombatDecision EvaluateDps(
        Character bot,
        Character? partyLeader,
        Unit? primaryTarget,
        IReadOnlyList<Unit> enemies,
        HashSet<uint> skills,
        uint lastSkillUsed)
    {
        // Focus fire: assist leader's target if available
        Unit? effectiveTarget = null;
        var leaderTargetObjId = partyLeader?.CurrentTarget?.ObjId ?? 0;
        if (leaderTargetObjId > 0)
        {
            effectiveTarget = enemies.FirstOrDefault(e => e.ObjId == leaderTargetObjId) ?? primaryTarget;
        }
        else
        {
            effectiveTarget = primaryTarget ?? enemies.FirstOrDefault();
        }

        if (effectiveTarget == null)
            return new CombatDecision(CombatTacticalAction.HoldAndRegen, "dps-no-target", 100);

        // Infer DPS role (Melee vs Ranged Physical vs Ranged Magic)
        var combatRole = CombatDecisionTree.InferRole(bot);
        if (combatRole == CombatRole.HealerSupport)
            combatRole = CombatRole.RangedMagic;

        return CombatDecisionTree.Evaluate(bot, effectiveTarget, combatRole, null, skills.ToList(), lastSkillUsed);
    }
}
