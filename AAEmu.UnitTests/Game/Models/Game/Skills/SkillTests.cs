using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Plots;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
namespace AAEmu.UnitTests.Game.Models.Game.Skills;

// The per-swing combat-dice tests install and restore the shared
// Singleton<SkillManager> static, so they must not run concurrently
// with each other (or any other suite touching that singleton).
[NotInParallel]
public class SkillTests
{
    #region Constructor Tests

    [Test]
    public async Task Constructor_Default_CreatesSkillWithDefaultValues()
    {
        // Act
        var skill = new Skill();

        // Assert
        await Assert.That(skill).IsNotNull();
        await Assert.That(skill.Id).IsEqualTo(0u);
        await Assert.That(skill.Template).IsNull();
        await Assert.That(skill.Level).IsEqualTo((byte)0);
        await Assert.That(skill.TlId).IsEqualTo((byte)0);
        await Assert.That(skill.HitTypes).IsNotNull();
        await Assert.That(skill.HitTypes).IsEmpty();
    }

    [Test]
    public async Task Constructor_WithTemplateAndOwner_CreatesSkillWithValues()
    {
        // Arrange
        var template = new SkillTemplate
        {
            Id = 100u,
            AbilityId = AbilityType.Fight,
            AbilityLevel = 1,
            LevelStep = 1
        };

        var stubOwner = new StubUnit(AbilityType.Fight, 5);

        // Act
        var skill = new Skill(template, stubOwner);

        // Assert
        await Assert.That(skill).IsNotNull();
        await Assert.That(skill.Id).IsEqualTo(100u);
        await Assert.That(skill.Template).IsEqualTo(template);
        await Assert.That(skill.Level).IsEqualTo((byte)5); // (5 - 1) / 1 + 1 = 5
    }

    [Test]
    public async Task Constructor_WithTemplateAndNoOwner_SetsLevelToOne()
    {
        // Arrange
        var template = new SkillTemplate
        {
            Id = 200u
        };

        // Act
        var skill = new Skill(template, null);

        // Assert
        await Assert.That(skill.Id).IsEqualTo(200u);
        await Assert.That(skill.Level).IsEqualTo((byte)1);
    }

    #endregion

    #region Property Tests

    [Test]
    public async Task Id_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var skill = new Skill();

        // Act
        skill.Id = 123u;

        // Assert
        await Assert.That(skill.Id).IsEqualTo(123u);
    }

    [Test]
    public async Task Level_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var skill = new Skill();

        // Act
        skill.Level = 5;

        // Assert
        await Assert.That(skill.Level).IsEqualTo((byte)5);
    }

    [Test]
    public async Task TlId_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var skill = new Skill();

        // Act
        skill.TlId = 456;

        // Assert
        await Assert.That(skill.TlId).IsEqualTo((ushort)456);
    }

    [Test]
    public async Task CastTimeMultiplier_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var skill = new Skill();

        // Act
        skill.CastTimeMultiplier = 1.5f;

        // Assert
        await Assert.That(skill.CastTimeMultiplier).IsEqualTo(1.5f);
    }

    #endregion

    #region HitTypes Tests

    [Test]
    public async Task HitTypes_AddAndRetrieve_ReturnsCorrectValue()
    {
        // Arrange
        var skill = new Skill();

        // Act
        skill.HitTypes[1] = SkillHitType.MeleeHit;

        // Assert
        await Assert.That(skill.HitTypes[1]).IsEqualTo(SkillHitType.MeleeHit);
    }

    // ---------------------------------------------------------------
    // Per-swing combat dice (Skill.ApplyEffects -> HitTypes).
    //
    // A combat-dice roll belongs to ONE swing. These drive the real
    // ApplyEffects write and the real DamageEffect/BuffEffect read
    // condition, with no world, DB or MySQL. Determinism comes from
    // Unit.RangedAccuracy / MeleeAccuracy: RollCombatDice compares
    // Random.Shared.Next(0,100) against that value, so 100f can never
    // miss and -1f can never hit (Skill.cs RollCombatDice).
    // ---------------------------------------------------------------

    private const uint RangedAutoAttackSkillId = 4u;
    private const uint MeleeAutoAttackSkillId = 2u;
    private const uint RangedDamageTypeId = 4u; // DamageType.Ranged

    private static Skill BuildAutoAttackSkill(uint templateId, uint damageTypeId)
    {
        var template = new SkillTemplate
        {
            Id = templateId,
            DamageTypeId = damageTypeId,
            // Hostile-only + no area => ApplyEffects rolls once for targetSelf,
            // which is the exact auto-attack shape (skills 2/3/4: plot_id NULL,
            // target_type_id 4, target_area_radius 0).
            TargetType = SkillTargetType.Hostile,
            TargetAreaRadius = 0,
            LevelRuleNoConsideration = false,
        };
        return new Skill(template);
    }

    /// <summary>Apply the skill against one target with the given forced accuracy.</summary>
    private static void ApplyAutoAttack(Skill skill, Unit caster, Unit target, float accuracy)
    {
        using var _ = InstallSkillManager();
        // Dodge/block/parry are the miss-paths that froze; zero them so the
        // only thing under test is the accuracy roll vs the stored entry.
        target.DodgeRate = 0f;
        target.BlockRate = 0f;
        target.MeleeParryRate = 0f;
        target.RangedParryRate = 0f;
        target.Flexibility = 0;
        caster.BullsEye = 0;
        caster.RangedAccuracy = accuracy;
        caster.MeleeAccuracy = accuracy;

        skill.ApplyEffects(
            caster,
            new SkillCasterUnit(caster.ObjId),
            target,
            new SkillCastUnitTarget(target.ObjId),
            SkillObject.GetByType(SkillObjectType.None));
    }

    /// <summary>Mirrors the guard in DamageEffect.cs:116-118 / BuffEffect.cs:26-27.</summary>
    private static bool ReadsAsMiss(Skill skill, Unit target)
    {
        var hitType = SkillHitType.Invalid;
        return skill.HitTypes.TryGetValue(target.ObjId, out hitType) && skill.SkillMissed(target.ObjId);
    }

    private sealed class SingletonSwap(Type singletonBase) : IDisposable
    {
        private static object? GetInstance(Type baseType) => baseType
            .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

        private readonly Type _singletonBase = singletonBase;
        private readonly object? _previous = GetInstance(singletonBase);

        public static SingletonSwap Install(Type baseType, object replacement)
        {
            var swap = new SingletonSwap(baseType);
            swap._singletonBase
                .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, replacement);
            return swap;
        }

        public void Dispose() => _singletonBase
            .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, _previous);
    }

    /// <summary>
    /// SkillManager is DI-only (no parameterless ctor), but ApplyEffects reads
    /// GetSkillReagentsBySkillId / GetSkillProductsBySkillId, which enumerate
    /// caches the game-data loader normally fills. A bare instance with empty
    /// caches keeps these tests hermetic. Restored on dispose.
    /// </summary>
    private static IDisposable InstallSkillManager()
    {
        var manager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetField(manager, "_skillReagents", new Dictionary<uint, SkillReagent>());
        SetField(manager, "_skillProducts", new Dictionary<uint, SkillProduct>());
        return SingletonSwap.Install(typeof(Singleton<SkillManager>), manager);
    }

    private static void SetField(object target, string fieldName, object value)
        => target.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    [Test]
    public async Task ApplyEffects_SecondSwingAgainstSameTarget_OverwritesFrozenRoll_AndLetsHitLand()
    {
        // Arrange — one Skill instance reused for the whole engagement, exactly
        // like UseAutoAttackSkillTask, which captures ONE Skill and re-uses it.
        var skill = BuildAutoAttackSkill(RangedAutoAttackSkillId, RangedDamageTypeId);
        var caster = new Unit { ObjId = 1u, Name = "archer" };
        var target = new Unit { ObjId = 2u, Name = "target" };
        // In front of the caster so RollCombatDice does not take the AlwaysHit
        // early-out and dodge us into a deterministic hit.
        target.Transform.Local.SetPosition(2f, 0f, 0f);

        // Swing 1 misses for real.
        ApplyAutoAttack(skill, caster, target, -1f);
        await Assert.That(skill.HitTypes[target.ObjId], "swing 1 recorded a genuine miss")
            .IsEqualTo(SkillHitType.RangedMiss);

        // Swing 2 hits for real against the SAME target.
        ApplyAutoAttack(skill, caster, target, 100f);

        // The frozen entry would still say RangedMiss here and eat the damage.
        await Assert.That(skill.HitTypes[target.ObjId], "swing 2 must record its OWN roll, not swing 1's")
            .IsEqualTo(SkillHitType.RangedHit);
        await Assert.That(ReadsAsMiss(skill, target),
            "a genuine hit after a genuine miss must NOT read as a miss").IsFalse();
    }

    [Test]
    public async Task ApplyEffects_MissThenHitThenMiss_TracksEachSwingIndependently()
    {
        // Arrange
        var skill = BuildAutoAttackSkill(RangedAutoAttackSkillId, RangedDamageTypeId);
        var caster = new Unit { ObjId = 1u, Name = "archer" };
        var target = new Unit { ObjId = 2u, Name = "target" };
        target.Transform.Local.SetPosition(2f, 0f, 0f);

        // Act / Assert — a miss after a hit must be able to MISS again, proving
        // the write is not merely "sticky toward hit".
        ApplyAutoAttack(skill, caster, target, -1f);
        await Assert.That(skill.HitTypes[target.ObjId]).IsEqualTo(SkillHitType.RangedMiss);

        ApplyAutoAttack(skill, caster, target, 100f);
        await Assert.That(skill.HitTypes[target.ObjId]).IsEqualTo(SkillHitType.RangedHit);
        await Assert.That(ReadsAsMiss(skill, target)).IsFalse();

        ApplyAutoAttack(skill, caster, target, -1f);
        await Assert.That(skill.HitTypes[target.ObjId], "a later swing may legitimately miss again")
            .IsEqualTo(SkillHitType.RangedMiss);
        await Assert.That(ReadsAsMiss(skill, target)).IsTrue();
    }

    [Test]
    public async Task ApplyEffects_RollReadTwiceForOneApplication_ReturnsSameRoll()
    {
        // Guards the single-application read path: DamageEffect.cs:116-118 and
        // BuffEffect.cs:26-27 both read HitTypes, and one swing may be observed
        // by more than one effect. Overwriting must not disturb a stored roll
        // between those reads within the same application.
        var skill = BuildAutoAttackSkill(RangedAutoAttackSkillId, RangedDamageTypeId);
        var caster = new Unit { ObjId = 1u, Name = "archer" };
        var target = new Unit { ObjId = 2u, Name = "target" };
        target.Transform.Local.SetPosition(2f, 0f, 0f);

        // Act
        ApplyAutoAttack(skill, caster, target, 100f);

        // Assert — the entry must be stable and readable repeatedly.
        await Assert.That(skill.HitTypes.Count).IsEqualTo(1);
        await Assert.That(skill.HitTypes[target.ObjId]).IsEqualTo(SkillHitType.RangedHit);
        await Assert.That(skill.HitTypes[target.ObjId]).IsEqualTo(SkillHitType.RangedHit);
        await Assert.That(skill.SkillMissed(target.ObjId), "a hit must not read as missed").IsFalse();
        await Assert.That(skill.SkillMissed(target.ObjId)).IsFalse();
    }

    [Test]
    public async Task ApplyEffects_TwoTargetsOnOneSkill_KeepIndependentResults()
    {
        // The dictionary is keyed per target: a miss on A must not make B miss.
        var skill = BuildAutoAttackSkill(RangedAutoAttackSkillId, RangedDamageTypeId);
        var caster = new Unit { ObjId = 1u, Name = "archer" };
        var missTarget = new Unit { ObjId = 2u, Name = "missTarget" };
        var hitTarget = new Unit { ObjId = 3u, Name = "hitTarget" };
        missTarget.Transform.Local.SetPosition(2f, 0f, 0f);
        hitTarget.Transform.Local.SetPosition(3f, 0f, 0f);

        // Act — swing at the target that will miss.
        ApplyAutoAttack(skill, caster, missTarget, -1f);
        await Assert.That(skill.HitTypes[missTarget.ObjId]).IsEqualTo(SkillHitType.RangedMiss);

        // Act — then swing at the other target on the SAME skill instance.
        ApplyAutoAttack(skill, caster, hitTarget, 100f);

        // Assert — both entries coexist and are independent.
        await Assert.That(skill.HitTypes.Count).IsEqualTo(2);
        await Assert.That(skill.HitTypes[missTarget.ObjId],
            "the earlier target keeps its own result").IsEqualTo(SkillHitType.RangedMiss);
        await Assert.That(skill.HitTypes[hitTarget.ObjId]).IsEqualTo(SkillHitType.RangedHit);
        await Assert.That(ReadsAsMiss(skill, hitTarget)).IsFalse();
    }

    [Test]
    public async Task PlotCondition_CombatDiceResult_RepeatedChecks_OverwritePreviousRoll()
    {
        using var _ = InstallSkillManager();
        // PlotCondition.cs:171-173 is the other production writer and already
        // overwrote. This pins that unchanged behaviour after the Skill.cs fix,
        // so the two writers cannot drift apart again.
        var skill = BuildAutoAttackSkill(RangedAutoAttackSkillId, RangedDamageTypeId);
        var caster = new Unit { ObjId = 1u, Name = "archer" };
        var target = new Unit { ObjId = 2u, Name = "target" };
        target.DodgeRate = 0f;
        target.BlockRate = 0f;
        target.MeleeParryRate = 0f;
        target.RangedParryRate = 0f;
        target.Flexibility = 0;
        caster.BullsEye = 0;
        caster.RangedAccuracy = 100f;

        var condition = new PlotCondition { Kind = PlotConditionType.CombatDiceResult, Param1 = 1 };

        // A hit is a "not condition" => false.
        var hitCheck = condition.Check(caster, new SkillCasterUnit(caster.ObjId), target,
            new SkillCastUnitTarget(target.ObjId), SkillObject.GetByType(SkillObjectType.None), skill);
        await Assert.That(hitCheck, "a combat-dice hit is a not-condition").IsFalse();
        await Assert.That(skill.HitTypes[target.ObjId]).IsEqualTo(SkillHitType.RangedHit);

        // Second check with accuracy forced to always-miss must overwrite.
        caster.RangedAccuracy = -1f;
        var missCheck = condition.Check(caster, new SkillCasterUnit(caster.ObjId), target,
            new SkillCastUnitTarget(target.ObjId), SkillObject.GetByType(SkillObjectType.None), skill);

        await Assert.That(skill.HitTypes[target.ObjId],
            "the plot writer must also overwrite, not freeze on the first check")
            .IsEqualTo(SkillHitType.RangedMiss);
        await Assert.That(skill.HitTypes.Count).IsEqualTo(1);
    }


    #endregion

    #region BypassGcd Tests

    // Note: _bypassGcd is private, so testing indirectly if possible

    #endregion

    #region Cancelled Tests

    [Test]
    public async Task Cancelled_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var skill = new Skill();

        // Act
        skill.Cancelled = true;

        // Assert
        await Assert.That(skill.Cancelled).IsTrue();
    }

    #endregion

    #region Callback Tests

    [Test]
    public async Task Callback_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var skill = new Skill();
        var callbackCalled = false;
        Action callback = () => callbackCalled = true;

        // Act
        skill.Callback = callback;
        skill.Callback?.Invoke();

        // Assert
        await Assert.That(callbackCalled).IsTrue();
    }

    #endregion

    #region Use Method Tests

    // Testing Use method requires extensive mocking, so basic tests

    [Test]
    public async Task Use_WithNonUnitCaster_ReturnsInvalidSource()
    {
        // Arrange
        var skill = new Skill();
        var mockCaster = new BaseUnit();
        var casterCaster = new SkillCasterUnit();
        var targetCaster = new SkillCastUnitTarget();

        // Act
        var result = skill.Use(mockCaster, casterCaster, targetCaster, null, false, out var value);

        // Assert
        await Assert.That(result).IsEqualTo(SkillResult.InvalidSource);
    }

    #endregion

    #region Edge Cases

    [Test]
    public async Task Constructor_WithNullTemplate_DoesNotThrow()
    {
        // Act
        var skill = new Skill(null, null);

        // Assert
        await Assert.That(skill).IsNotNull();
        await Assert.That(skill.Id).IsEqualTo(0u);
    }

    [Test]
    public async Task Level_WithZeroAbilityLevel_CalculatesCorrectly()
    {
        // Arrange
        var template = new SkillTemplate
        {
            Id = 100u,
            AbilityId = AbilityType.Fight,
            AbilityLevel = 0,
            LevelStep = 2
        };

        var stubOwner = new StubUnit(AbilityType.Fight, 10);

        // Act
        var skill = new Skill(template, stubOwner);

        // Assert
        await Assert.That(skill.Level).IsEqualTo((byte)6); // (10 - 0) / 2 + 1 = 6
    }

    #endregion

    private sealed class StubUnit : Unit
    {
        private readonly AbilityType _abilityType;
        private readonly int _level;

        public StubUnit(AbilityType abilityType, int level)
        {
            _abilityType = abilityType;
            _level = level;
        }

        public override int GetAbLevel(AbilityType type) =>
            type == _abilityType ? _level : base.GetAbLevel(type);
    }
}
