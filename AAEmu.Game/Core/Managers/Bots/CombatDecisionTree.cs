using System.Numerics;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
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

    public const float DefaultRangedMin = 12.0f;
    public const float DefaultRangedMax = 22.0f;

    public const float DefaultEmergencyFleeHpPercent = 0.20f;
    public const float DefaultDefensiveHealHpPercent = 0.50f;

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
    /// Evaluates the combat decision tree against the observed battle state.
    /// </summary>
    public static CombatDecision Evaluate(
        Character bot,
        Unit target,
        CombatRole? roleOverride = null,
        float? distanceOverride = null,
        IReadOnlyList<uint>? availableSkills = null,
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
            // If melee and beyond melee reach, close the gap
            if (distance > maxMeleeRange)
            {
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
        var selectedSkill = (availableSkills != null && availableSkills.Count > 0) ? availableSkills[0] : 0u;
        return new CombatDecision(
            Action: CombatTacticalAction.CastSkill,
            Rationale: $"cast-skill-in-ideal-range (dist={distance:F1}m, role={role})",
            Priority: 300,
            SkillId: selectedSkill,
            TargetObjId: target.ObjId
        );
    }
}
