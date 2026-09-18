using System.Numerics;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Tactical combat roles determining spacing, engagement bands, and behavior.
/// </summary>
public enum CombatRole
{
    Melee = 0,            // Battlerage, Defense, Shadowplay melee (ideal 1.0 - 3.5m)
    RangedPhysical = 1,   // Archery (ideal 12.0 - 22.0m)
    RangedMagic = 2,      // Sorcery, Occultism (ideal 14.0 - 22.0m)
    HealerSupport = 3     // Vitalism, Songcraft (ideal 10.0 - 20.0m)
}

/// <summary>
/// High-level tactical action proposed by the combat tree.
/// </summary>
public enum CombatTacticalAction
{
    CloseGap = 0,         // Move closer to target to enter engagement band
    KiteSpacing = 1,      // Step/kite backwards to preserve distance from advancing enemy
    CastSkill = 2,        // Fire prioritized offensive or combo skill
    EmergencyFlee = 3,    // Disengage and retreat to safety when critically low on HP
    DefensiveHeal = 4,    // Cast defensive shield, heal, or consume potion
    HoldAndRegen = 5      // Wait for resources/global cooldown
}

/// <summary>
/// Result of evaluating the combat decision tree for one engagement round.
/// </summary>
public sealed record CombatDecision(
    CombatTacticalAction Action,
    string Rationale,
    int Priority,
    Vector3? TargetPosition = null,
    uint SkillId = 0,
    uint TargetObjId = 0
);

/// <summary>
/// Decision tree for playerbot combat: evaluates health, tactical positioning,
/// class roles (melee vs ranged/magic kiting), and combo rotations deterministically.
/// </summary>
public static class CombatDecisionTree
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public const float DefaultMeleeMin = 1.0f;
    public const float DefaultMeleeMax = 3.5f;

    /// <summary>Faction id of ordinary open-world monster wildlife (compact.sqlite3 factions).</summary>
    public const uint MonsterWildlifeFactionId = 115;

    /// <summary>
    /// Canonical hostility test for bot target acquisition and perception.
    /// An NPC is hostile when it belongs to monster wildlife (faction 115) or
    /// carries no faction at all (bare fixture NPCs read attackable); otherwise
    /// the engine's own rule decides — <see cref="BaseUnit.CanAttack"/> (faction
    /// relation, safe-zone and zone-conflict rules) plus an explicit Hostile
    /// relation. Merchants, quest givers and other friendly NPCs are not hostile.
    /// Safe against a missing FactionManager singleton in test/headless runs.
    /// </summary>
    public static bool IsHostileTarget(Character bot, BaseUnit target)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(target);

        // Hp lives on Unit, not BaseUnit: only a damageable unit can be dead.
        if (target is Unit unit && unit.Hp <= 0)
            return false;

        if (target is Npc npc)
        {
            if ((int?)npc.Faction?.Id == (int)MonsterWildlifeFactionId)
                return true;

            if (npc.Faction == null)
                return true;
        }

        try
        {
            if (!bot.CanAttack(target))
                return false;

            return bot.GetRelationStateTo(target) == RelationState.Hostile;
        }
        catch
        {
            return true;
        }
    }

    public const float DefaultRangedMin = 12.0f;
    public const float DefaultRangedMax = 22.0f;

    public const float DefaultEmergencyFleeHpPercent = 0.20f;
    public const float DefaultDefensiveHealHpPercent = 0.50f;

    // Basic continuous auto-attack skills
    public const uint BasicMeleeAutoAttackSkillId = 2;      // 근접 공격 (Melee Auto Attack)
    public const uint BasicRangedAutoAttackSkillId = 4;     // 원거리 공격 (Ranged Auto Attack)

    // Canonical starter combo skill IDs
    // Battlerage (Fight)
    public const uint BattlerageTripleSlashSkillId = 18131; // 3단 베기 (Triple Slash)
    public const uint BattlerageChargeSkillId = 11918;      // 돌격 (Charge)
    public const uint BattlerageWhirlwindSkillId = 13282;   // 회오리 베기 (Whirlwind Slash)

    // Defense (Adamant)
    public const uint DefenseShieldSlamSkillId = 10399;     // 방패 휘두르기 (Shield Slam) [Stuns]
    public const uint DefenseBullRushSkillId = 10501;       // 제압 (Bull Rush) [Trips stunned target, silences]

    // Shadowplay (Vocation)
    public const uint ShadowplayRapidStrikesSkillId = 18125;// 연속 베기 (Rapid Strikes)
    public const uint ShadowplayOverwhelmSkillId = 10648;   // 덮치기 (Overwhelm) [Gap-close, stuns]
    public const uint ShadowplayShadowsmiteSkillId = 10496; // 어둠의 일격 (Shadowsmite) [Trips stunned target]

    // Sorcery (Magic)
    public const uint SorceryFlameboltSkillId = 10752;       // 불꽃 송이 (Flamebolt)
    public const uint SorceryFreezingArrowSkillId = 10667;   // 얼음 화살 (Freezing Arrow)
    public const uint SorceryChainLightningSkillId = 11967; // 연쇄 번개 (Chain Lightning)

    // Witchcraft (Illusion)
    public const uint WitchcraftEarthenGripSkillId = 14376; // 대지의 손아귀 (Earthen Grip) [Snare, life steal]
    public const uint WitchcraftEnervateSkillId = 10159;    // 정신 파괴 (Enervate) [Mana burn, debuff]

    // Archery (Wild)
    public const uint ArcheryShootSkillId = 16064;          // 활쏘기 (Archery Shoot)
    public const uint ArcheryChargedBoltSkillId = 16210;    // 충격 화살 (Charged Bolt)
    public const uint ArcheryEndlessArrowsSkillId = 14835;  // 연속 쏘기 (Endless Arrows)

    // Vitalism (Love)
    public const uint VitalismAntithesisSkillId = 10534;    // 빛과 어둠 (Antithesis)
    public const uint VitalismResurgenceSkillId = 10547;    // 샘솟는 생명력 (Resurgence)

    // Songcraft (Romance)
    public const uint SongcraftCriticalDiscordSkillId = 11973; // 칼의 화음 (Critical Discord) [Damage, Discord]
    public const uint SongcraftStartlingStrainSkillId = 11934; // 매혹의 노래 (Startling Strain) [Stuns, Charmed]

    // Occultism (Death)
    public const uint OccultismHellSpearSkillId = 10135;    // 지옥의 창 (Hell Spear)
    public const uint OccultismManaStarsSkillId = 12759;    // 활력 추출 / 마나 스타 (Mana Stars)

    // Auramancy (Will)
    public const uint AuramancyThwartSkillId = 16486;       // 기선 제압 (Thwart) [Shaken debuff, Inspired stack]
    public const uint AuramancyConversionShieldSkillId = 11869; // 활력 방패 (Conversion Shield)

    /// <summary>
    /// Gets canonical or weapon-derived effective (minRange, maxRange) in meters for a skill.
    /// </summary>
    public static (float MinRange, float MaxRange) GetSkillEffectiveRange(SkillTemplate template, Character? bot = null)
    {
        float minRange = template.MinRange;
        float maxRange = template.MaxRange;

        if (template.WeaponSlotForRangeId > 0 && bot?.Equipment != null)
        {
            var item = bot.Equipment.GetItemBySlot(template.WeaponSlotForRangeId);
            if (item?.Template is WeaponTemplate weapon && weapon.HoldableTemplate != null)
            {
                minRange = Math.Max(minRange, weapon.HoldableTemplate.MinRange);
                maxRange = Math.Max(maxRange, weapon.HoldableTemplate.MaxRange);
            }
        }

        // If template has 0 max range (e.g. melee touch / self), default to 4.0m for offensive melee reach
        if (maxRange <= 0.01f)
            maxRange = 4.0f;

        return (minRange, maxRange);
    }

    /// <summary>
    /// Fallback static ranges for well-known starter and combo skills (used in unit test/mock environments).
    /// </summary>
    public static (float MinRange, float MaxRange) GetKnownSkillRange(uint skillId)
    {
        return skillId switch
        {
            // Battlerage
            BattlerageChargeSkillId => (0f, 12f),
            BattlerageTripleSlashSkillId => (0f, 4f),
            18132u => (0f, 4f),
            BattlerageWhirlwindSkillId => (0f, 4f),
            // Defense
            DefenseShieldSlamSkillId => (0f, 4f),
            DefenseBullRushSkillId => (0f, 4f),
            // Shadowplay
            ShadowplayOverwhelmSkillId => (0f, 10f),
            ShadowplayShadowsmiteSkillId => (0f, 4f),
            ShadowplayRapidStrikesSkillId => (0f, 4f),
            // Archery
            ArcheryShootSkillId => (0f, 20f),
            ArcheryChargedBoltSkillId => (0f, 20f),
            ArcheryEndlessArrowsSkillId => (0f, 20f),
            // Sorcery
            SorceryFlameboltSkillId => (0f, 20f),
            SorceryFreezingArrowSkillId => (0f, 20f),
            SorceryChainLightningSkillId => (0f, 20f),
            // Witchcraft
            WitchcraftEnervateSkillId => (0f, 20f),
            WitchcraftEarthenGripSkillId => (0f, 20f),
            // Occultism
            OccultismHellSpearSkillId => (0f, 6f),
            OccultismManaStarsSkillId => (0f, 20f),
            // Vitalism
            VitalismAntithesisSkillId => (0f, 25f),
            VitalismResurgenceSkillId => (0f, 30f),
            // Songcraft
            SongcraftStartlingStrainSkillId => (0f, 15f),
            SongcraftCriticalDiscordSkillId => (0f, 20f),
            // Basic continuous attacks
            BasicMeleeAutoAttackSkillId => (0f, 4f),
            BasicRangedAutoAttackSkillId => (4f, 20f),
            _ => (0f, 20f)
        };
    }

    /// <summary>
    /// Checks whether a skill is usable given current cooldowns, mana pool, and target distance.
    /// </summary>
    public static bool IsSkillInRangeAndReady(
        Character bot,
        Unit? target,
        uint skillId,
        float distance)
    {
        if (skillId == 0)
            return false;

        // Check cooldown on the character
        if (bot.Cooldowns?.CheckCooldown(skillId) == true)
            return false;

        // Template checks (mana, real range, template cooldown)
        if (SkillManager.Instance != null)
        {
            var template = SkillManager.Instance.GetSkillTemplate(skillId);
            if (template != null)
            {
                if (template.ManaCost > 0 && bot.Mp < template.ManaCost)
                    return false;

                if (target != null)
                {
                    var (minRange, maxRange) = GetSkillEffectiveRange(template, bot);
                    if (distance < minRange || distance > maxRange)
                        return false;
                }
                return true;
            }
        }

        // Fallback checks with known ranges when SkillManager is not loaded
        if (target != null)
        {
            var (minRange, maxRange) = GetKnownSkillRange(skillId);
            if (distance < minRange || distance > maxRange)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Infers the primary combat role from the character's primary skill tree (Ability1).
    /// </summary>
    public static CombatRole InferRole(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);

        return character.Ability1 switch
        {
            AbilityType.Wild => CombatRole.RangedPhysical,
            AbilityType.Magic or AbilityType.Death or AbilityType.Illusion => CombatRole.RangedMagic,
            AbilityType.Love or AbilityType.Romance => CombatRole.HealerSupport,
            _ => CombatRole.Melee
        };
    }

    /// <summary>
    /// Selects the optimal next skill based on class ability combos, cooldowns, mana, and target distance.
    /// </summary>
    public static uint SelectPrioritizedSkill(
        Character bot,
        Unit target,
        CombatRole role,
        IReadOnlyList<uint>? availableSkills,
        uint lastSkillUsed = 0)
    {
        if (availableSkills == null || availableSkills.Count == 0)
        {
            if (bot.Skills?.Skills.Count > 0)
                availableSkills = bot.Skills.Skills.Keys.ToList();
            else
                return 0u;
        }

        var candidates = new HashSet<uint>(availableSkills);
        var distance = target != null
            ? MathUtil.CalculateDistance(bot.Transform.World.Position, target.Transform.World.Position, false)
            : 0f;

        // Class combo rotations
        switch (role)
        {
            case CombatRole.Melee:
            {
                // Defense combo chain: Shield Slam (10399) [Stuns] -> Bull Rush (10501) [Trips stunned target]
                if (lastSkillUsed == DefenseShieldSlamSkillId && candidates.Contains(DefenseBullRushSkillId) && IsSkillInRangeAndReady(bot, target, DefenseBullRushSkillId, distance))
                    return DefenseBullRushSkillId;

                // Shadowplay combo chain: Overwhelm (10648) [Stuns] -> Shadowsmite (10496) [Trips stunned target] -> Rapid Strikes (18125)
                if (lastSkillUsed == ShadowplayOverwhelmSkillId && candidates.Contains(ShadowplayShadowsmiteSkillId) && IsSkillInRangeAndReady(bot, target, ShadowplayShadowsmiteSkillId, distance))
                    return ShadowplayShadowsmiteSkillId;

                if (lastSkillUsed == ShadowplayShadowsmiteSkillId && candidates.Contains(ShadowplayRapidStrikesSkillId) && IsSkillInRangeAndReady(bot, target, ShadowplayRapidStrikesSkillId, distance))
                    return ShadowplayRapidStrikesSkillId;

                // Battlerage combo chain: Charge (11918) -> Triple Slash (18131) -> Whirlwind Slash (13282)
                if (lastSkillUsed == BattlerageChargeSkillId && candidates.Contains(BattlerageTripleSlashSkillId) && IsSkillInRangeAndReady(bot, target, BattlerageTripleSlashSkillId, distance))
                    return BattlerageTripleSlashSkillId;

                if (lastSkillUsed == BattlerageTripleSlashSkillId && candidates.Contains(BattlerageWhirlwindSkillId) && IsSkillInRangeAndReady(bot, target, BattlerageWhirlwindSkillId, distance))
                    return BattlerageWhirlwindSkillId;

                // Openers / default priority:
                // 1. Crowd-control / gap-closers
                if (candidates.Contains(DefenseShieldSlamSkillId) && lastSkillUsed != DefenseShieldSlamSkillId && IsSkillInRangeAndReady(bot, target, DefenseShieldSlamSkillId, distance))
                    return DefenseShieldSlamSkillId;

                if (candidates.Contains(ShadowplayOverwhelmSkillId) && lastSkillUsed != ShadowplayOverwhelmSkillId && IsSkillInRangeAndReady(bot, target, ShadowplayOverwhelmSkillId, distance))
                    return ShadowplayOverwhelmSkillId;

                if (candidates.Contains(BattlerageChargeSkillId) && lastSkillUsed != BattlerageChargeSkillId && IsSkillInRangeAndReady(bot, target, BattlerageChargeSkillId, distance))
                    return BattlerageChargeSkillId;

                // 2. Follow-up finishers
                if (candidates.Contains(DefenseBullRushSkillId) && IsSkillInRangeAndReady(bot, target, DefenseBullRushSkillId, distance))
                    return DefenseBullRushSkillId;

                if (candidates.Contains(ShadowplayShadowsmiteSkillId) && IsSkillInRangeAndReady(bot, target, ShadowplayShadowsmiteSkillId, distance))
                    return ShadowplayShadowsmiteSkillId;

                // 3. Spammers / bread & butter
                if (candidates.Contains(BattlerageTripleSlashSkillId) && IsSkillInRangeAndReady(bot, target, BattlerageTripleSlashSkillId, distance))
                    return BattlerageTripleSlashSkillId;

                if (candidates.Contains(ShadowplayRapidStrikesSkillId) && IsSkillInRangeAndReady(bot, target, ShadowplayRapidStrikesSkillId, distance))
                    return ShadowplayRapidStrikesSkillId;

                if (candidates.Contains(BattlerageWhirlwindSkillId) && IsSkillInRangeAndReady(bot, target, BattlerageWhirlwindSkillId, distance))
                    return BattlerageWhirlwindSkillId;

                break;
            }

            case CombatRole.RangedMagic:
            {
                // Sorcery combo chain: Flamebolt (10752) [inflicts Burn] -> Freezing Arrow (10667) [43% bonus on Burn + Freeze] -> Chain Lightning (11967)
                if (lastSkillUsed == SorceryFlameboltSkillId && candidates.Contains(SorceryFreezingArrowSkillId) && IsSkillInRangeAndReady(bot, target, SorceryFreezingArrowSkillId, distance))
                    return SorceryFreezingArrowSkillId;

                if (lastSkillUsed == SorceryFreezingArrowSkillId && candidates.Contains(SorceryChainLightningSkillId) && IsSkillInRangeAndReady(bot, target, SorceryChainLightningSkillId, distance))
                    return SorceryChainLightningSkillId;

                // Witchcraft combo chain: Enervate (10159) -> Earthen Grip (14376) [bonus damage & life drain on Enervated]
                if (lastSkillUsed == WitchcraftEnervateSkillId && candidates.Contains(WitchcraftEarthenGripSkillId) && IsSkillInRangeAndReady(bot, target, WitchcraftEarthenGripSkillId, distance))
                    return WitchcraftEarthenGripSkillId;

                // Occultism combo chain: Hell Spear (10135) -> Mana Stars (12759)
                if (lastSkillUsed == OccultismHellSpearSkillId && candidates.Contains(OccultismManaStarsSkillId) && IsSkillInRangeAndReady(bot, target, OccultismManaStarsSkillId, distance))
                    return OccultismManaStarsSkillId;

                // Auramancy utility buff
                if (candidates.Contains(AuramancyConversionShieldSkillId) && lastSkillUsed != AuramancyConversionShieldSkillId && IsSkillInRangeAndReady(bot, target, AuramancyConversionShieldSkillId, distance))
                    return AuramancyConversionShieldSkillId;

                // Openers: Burn with Flamebolt or debuff with Enervate
                if (candidates.Contains(SorceryFlameboltSkillId) && IsSkillInRangeAndReady(bot, target, SorceryFlameboltSkillId, distance))
                    return SorceryFlameboltSkillId;

                if (candidates.Contains(WitchcraftEnervateSkillId) && IsSkillInRangeAndReady(bot, target, WitchcraftEnervateSkillId, distance))
                    return WitchcraftEnervateSkillId;

                if (candidates.Contains(SorceryFreezingArrowSkillId) && IsSkillInRangeAndReady(bot, target, SorceryFreezingArrowSkillId, distance))
                    return SorceryFreezingArrowSkillId;

                if (candidates.Contains(SorceryChainLightningSkillId) && IsSkillInRangeAndReady(bot, target, SorceryChainLightningSkillId, distance))
                    return SorceryChainLightningSkillId;

                if (candidates.Contains(WitchcraftEarthenGripSkillId) && IsSkillInRangeAndReady(bot, target, WitchcraftEarthenGripSkillId, distance))
                    return WitchcraftEarthenGripSkillId;

                if (candidates.Contains(OccultismHellSpearSkillId) && IsSkillInRangeAndReady(bot, target, OccultismHellSpearSkillId, distance))
                    return OccultismHellSpearSkillId;

                if (candidates.Contains(OccultismManaStarsSkillId) && IsSkillInRangeAndReady(bot, target, OccultismManaStarsSkillId, distance))
                    return OccultismManaStarsSkillId;

                if (candidates.Contains(AuramancyThwartSkillId) && IsSkillInRangeAndReady(bot, target, AuramancyThwartSkillId, distance))
                    return AuramancyThwartSkillId;

                break;
            }

            case CombatRole.RangedPhysical:
            {
                // Archery combo chain: Charged Bolt (16210) [inflicts Slow] -> Endless Arrows (14835) [bonus vs Slowed]
                if (lastSkillUsed == ArcheryChargedBoltSkillId && candidates.Contains(ArcheryEndlessArrowsSkillId) && IsSkillInRangeAndReady(bot, target, ArcheryEndlessArrowsSkillId, distance))
                    return ArcheryEndlessArrowsSkillId;

                // Opener: slow with Charged Bolt
                if (candidates.Contains(ArcheryChargedBoltSkillId) && IsSkillInRangeAndReady(bot, target, ArcheryChargedBoltSkillId, distance))
                    return ArcheryChargedBoltSkillId;

                if (candidates.Contains(ArcheryEndlessArrowsSkillId) && IsSkillInRangeAndReady(bot, target, ArcheryEndlessArrowsSkillId, distance))
                    return ArcheryEndlessArrowsSkillId;

                if (candidates.Contains(ArcheryShootSkillId) && IsSkillInRangeAndReady(bot, target, ArcheryShootSkillId, distance))
                    return ArcheryShootSkillId;

                // Fallback close-quarters melee skill if target closed in
                if (candidates.Contains(ShadowplayRapidStrikesSkillId) && IsSkillInRangeAndReady(bot, target, ShadowplayRapidStrikesSkillId, distance))
                    return ShadowplayRapidStrikesSkillId;

                break;
            }

            case CombatRole.HealerSupport:
            {
                // Songcraft combo chain: Startling Strain (11934) [Stuns & Charms] -> Critical Discord (11973) [amplified vs Charmed]
                if (lastSkillUsed == SongcraftStartlingStrainSkillId && candidates.Contains(SongcraftCriticalDiscordSkillId) && IsSkillInRangeAndReady(bot, target, SongcraftCriticalDiscordSkillId, distance))
                    return SongcraftCriticalDiscordSkillId;

                // Vitalism rotation: Resurgence (10547) [HoT buff] -> Antithesis (10534) [damage/heal]
                var hpPercent = bot.MaxHp > 0 ? (float)bot.Hp / bot.MaxHp : 1.0f;
                if (hpPercent < 0.70f && candidates.Contains(VitalismResurgenceSkillId) && IsSkillInRangeAndReady(bot, target, VitalismResurgenceSkillId, distance))
                    return VitalismResurgenceSkillId;

                if (candidates.Contains(SongcraftStartlingStrainSkillId) && lastSkillUsed != SongcraftStartlingStrainSkillId && IsSkillInRangeAndReady(bot, target, SongcraftStartlingStrainSkillId, distance))
                    return SongcraftStartlingStrainSkillId;

                if (candidates.Contains(SongcraftCriticalDiscordSkillId) && IsSkillInRangeAndReady(bot, target, SongcraftCriticalDiscordSkillId, distance))
                    return SongcraftCriticalDiscordSkillId;

                if (candidates.Contains(VitalismAntithesisSkillId) && IsSkillInRangeAndReady(bot, target, VitalismAntithesisSkillId, distance))
                    return VitalismAntithesisSkillId;

                if (candidates.Contains(VitalismResurgenceSkillId) && IsSkillInRangeAndReady(bot, target, VitalismResurgenceSkillId, distance))
                    return VitalismResurgenceSkillId;

                break;
            }
        }

        // Fallback: return the first available skill in candidates that is in range and ready
        foreach (var skillId in availableSkills)
        {
            if (IsSkillInRangeAndReady(bot, target, skillId, distance))
                return skillId;
        }

        return 0u;
    }

    /// <summary>
    /// Evaluates the combat decision tree against the observed battle state.
    /// </summary>
    public static CombatDecision Evaluate(
        Character bot,
        Unit target,
        CombatRole? roleOverride = null,
        float? distanceOverride = null,
        IReadOnlyList<uint>? availableSkills = null,
        uint lastSkillUsed = 0,
        float maxMeleeRange = DefaultMeleeMax,
        float fleeHpPercentThreshold = DefaultEmergencyFleeHpPercent,
        float healHpPercentThreshold = DefaultDefensiveHealHpPercent)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(target);

        var role = roleOverride ?? InferRole(bot);
        var botPos = bot.Transform.World.Position;
        var targetPos = target.Transform.World.Position;
        var distance = distanceOverride ?? Vector3.Distance(botPos, targetPos);
        var hpPercent = bot.MaxHp > 0 ? (float)bot.Hp / bot.MaxHp : 1.0f;

        // ---------------------------------------------------- 1. EMERGENCY FLEE
        if (hpPercent <= fleeHpPercentThreshold)
        {
            var fleeDir = Vector3.Normalize(botPos - targetPos);
            if (fleeDir == Vector3.Zero || !float.IsFinite(fleeDir.X))
                fleeDir = new Vector3(1, 0, 0);

            var fleeTarget = botPos + fleeDir * 25.0f;
            return new CombatDecision(
                Action: CombatTacticalAction.EmergencyFlee,
                Rationale: $"emergency-flee-hp-critical ({hpPercent:P0} <= {fleeHpPercentThreshold:P0})",
                Priority: 1000,
                TargetPosition: fleeTarget,
                TargetObjId: target.ObjId
            );
        }

        // ---------------------------------------------------- 2. TACTICAL SPACING / KITING
        if (role is CombatRole.RangedPhysical or CombatRole.RangedMagic)
        {
            // If enemy has closed inside the minimum ranged band, kite backwards
            if (distance < DefaultRangedMin)
            {
                var kiteDir = Vector3.Normalize(botPos - targetPos);
                if (kiteDir == Vector3.Zero || !float.IsFinite(kiteDir.X))
                    kiteDir = new Vector3(-1, 0, 0);

                var kiteTarget = botPos + kiteDir * 10.0f;
                return new CombatDecision(
                    Action: CombatTacticalAction.KiteSpacing,
                    Rationale: $"kite-spacing-target-too-close ({distance:F1}m < {DefaultRangedMin:F1}m)",
                    Priority: 700,
                    TargetPosition: kiteTarget,
                    TargetObjId: target.ObjId
                );
            }

            // If enemy is beyond maximum ranged casting/shooting distance, close in
            if (distance > DefaultRangedMax)
            {
                var approachDir = Vector3.Normalize(targetPos - botPos);
                var approachTarget = targetPos - approachDir * ((DefaultRangedMin + DefaultRangedMax) / 2.0f);
                return new CombatDecision(
                    Action: CombatTacticalAction.CloseGap,
                    Rationale: $"close-gap-target-too-far ({distance:F1}m > {DefaultRangedMax:F1}m)",
                    Priority: 600,
                    TargetPosition: approachTarget,
                    TargetObjId: target.ObjId
                );
            }
        }
        else if (role == CombatRole.Melee)
        {
            // If melee and beyond melee reach, check for gap-closer before walking
            if (distance > maxMeleeRange)
            {
                var resolvedSkills = availableSkills ?? (bot.Skills?.Skills.Keys.ToList() as IReadOnlyList<uint>) ?? [];
                var candSet = new HashSet<uint>(resolvedSkills);
                var gapSkill = 0u;

                if (distance <= 12.0f && candSet.Contains(BattlerageChargeSkillId) && IsSkillInRangeAndReady(bot, target, BattlerageChargeSkillId, distance))
                    gapSkill = BattlerageChargeSkillId;
                else if (distance <= 10.0f && candSet.Contains(ShadowplayOverwhelmSkillId) && IsSkillInRangeAndReady(bot, target, ShadowplayOverwhelmSkillId, distance))
                    gapSkill = ShadowplayOverwhelmSkillId;

                if (gapSkill > 0)
                {
                    return new CombatDecision(
                        Action: CombatTacticalAction.CastSkill,
                        Rationale: $"gap-closer-in-range ({gapSkill} at {distance:F1}m)",
                        Priority: 650,
                        SkillId: gapSkill,
                        TargetObjId: target.ObjId
                    );
                }

                return new CombatDecision(
                    Action: CombatTacticalAction.CloseGap,
                    Rationale: $"close-gap-melee-reach ({distance:F1}m > {maxMeleeRange:F1}m)",
                    Priority: 600,
                    TargetPosition: targetPos,
                    TargetObjId: target.ObjId
                );
            }
        }

        // ---------------------------------------------------- 3. CAST COMBAT SKILL
        var selectedSkill = SelectPrioritizedSkill(bot, target, role, availableSkills, lastSkillUsed);
        if (selectedSkill == 0)
        {
            if (role == CombatRole.Melee && distance > DefaultMeleeMin)
            {
                return new CombatDecision(
                    Action: CombatTacticalAction.CloseGap,
                    Rationale: $"close-gap-no-usable-skill ({distance:F1}m > {DefaultMeleeMin:F1}m)",
                    Priority: 600,
                    TargetPosition: targetPos,
                    TargetObjId: target.ObjId
                );
            }

            return new CombatDecision(
                Action: CombatTacticalAction.HoldAndRegen,
                Rationale: $"hold-and-regen-cooldowns (dist={distance:F1}m, role={role})",
                Priority: 200,
                TargetObjId: target.ObjId
            );
        }

        var comboInfo = (lastSkillUsed > 0 && selectedSkill > 0) ? $" (combo-following={lastSkillUsed})" : "";
        return new CombatDecision(
            Action: CombatTacticalAction.CastSkill,
            Rationale: $"cast-skill-in-ideal-range (dist={distance:F1}m, role={role}){comboInfo}",
            Priority: 300,
            SkillId: selectedSkill,
            TargetObjId: target.ObjId
        );
    }
}
