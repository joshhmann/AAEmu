using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Units;

/// <summary>
/// Regression rig for the live bot-combat crash cluster (presence demo:
/// repeated `Skill ... threw on target` errors while bots fight wildlife):
/// <list type="bullet">
/// <item>Buff tolerance duplicate key — <see cref="Buffs.AddBuff"/> re-added
/// an existing tolerance counter when the tolerance's immunity buff was
/// already active (live: Key 4 on every repeated bot CC).</item>
/// <item>Torn bonus reads — plot-thread <see cref="Unit.GetBonuses"/> racing
/// game-loop buff mutation surfaced null slots (live Npc.Armor NRE).</item>
/// </list>
/// Loot double-generation (concurrent killing blows corrupting
/// LootingContainer.Items) is fixed alongside but has no unit test — it
/// needs the full NPC/loot-pack fixture; gate + live logs cover it.
/// </summary>
[NotInParallel]
public class BuffToleranceTests
{
    private const uint TestBuffId = 9901;
    private const uint ImmuneBuffId = 9902;
    private const uint ToleranceTag = 991;

    [Test]
    public async Task AddBuff_RepeatedHitsWithActiveImmunity_DoesNotThrow()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("tol-1");
        var character = actor.Character;
        SeedToleranceSurface();

        // Hit 1: creates the tolerance counter.
        character.Buffs.AddBuff(MakeBuff(character, TestBuffId));
        // Hit 2: steps the tolerance into immunity (immune buff applied).
        character.Buffs.AddBuff(MakeBuff(character, TestBuffId));
        await Assert.That(character.Buffs.CheckBuff(ImmuneBuffId)).IsTrue();

        // Hit 3 while immune: the pre-fix code re-added the existing
        // counter (ArgumentException: Key 4). Must apply cleanly.
        character.Buffs.AddBuff(MakeBuff(character, TestBuffId));

        await Assert.That(character.Buffs.CheckBuff(ImmuneBuffId)).IsTrue();
    }

    [Test]
    public async Task GetBonuses_NullSlot_DoesNotThrow()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("tol-2");
        var character = actor.Character;

        // Simulate a torn read: a null slot in the bonus table (concurrent
        // plot-thread read vs game-loop buff mutation).
        character.Bonuses[777] = [null!];

        var result = character.GetBonuses(UnitAttribute.MaxHealth);
        await Assert.That(result).IsEmpty();
    }

    private static Buff MakeBuff(Character character, uint templateId)
    {
        var template = SkillManager.Instance.GetBuffTemplate(templateId);
        return new Buff(character, character, new SkillCasterUnit(character.ObjId), template, null, DateTime.UtcNow);
    }

    /// <summary>
    /// Seeds a two-step tolerance whose second hit escalates to immunity:
    /// steps have equal TimeReduction so the second hit takes the immune
    /// branch (mirrors the live stun-tolerance shape).
    /// </summary>
    private static void SeedToleranceSurface()
    {
        GameplayActorTestRig.SeedBuffTemplate(TestBuffId);
        GameplayActorTestRig.SeedBuffTemplate(ImmuneBuffId);

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        // Buff tick scheduling (SetInUse): EffectTaskManager has no
        // parameterless ctor — missing-only seed like ItemProcBindingTests.
        var etmField = typeof(Singleton<EffectTaskManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (etmField.GetValue(null) == null)
            etmField.SetValue(null, new EffectTaskManager(Mock.Of<ITaskManager>().Object));
        var buffs = (Dictionary<uint, BuffTemplate>)typeof(SkillManager).GetField("_buffs", flags)!.GetValue(SkillManager.Instance)!;
        buffs[TestBuffId] = new BuffTemplate { Id = TestBuffId, Kind = BuffKind.Bad, Duration = 30000 };
        buffs[ImmuneBuffId] = new BuffTemplate { Id = ImmuneBuffId, Kind = BuffKind.Bad, Duration = 30000 };

        var tags = (Dictionary<uint, List<uint>>)typeof(SkillManager).GetField("_buffTags", flags)!.GetValue(SkillManager.Instance)!;
        tags[TestBuffId] = [ToleranceTag];

        var tolerancesField = typeof(BuffGameData).GetField("_buffTolerances", flags)!;
        if (tolerancesField.GetValue(BuffGameData.Instance) is not Dictionary<uint, BuffTolerance> tolerances)
        {
            tolerances = [];
            tolerancesField.SetValue(BuffGameData.Instance, tolerances);
        }
        tolerances[ToleranceTag] = new BuffTolerance
        {
            Id = 4,
            BuffTagId = ToleranceTag,
            StepDuration = 3600,
            FinalStepBuffId = ImmuneBuffId,
            Steps =
            [
                new BuffToleranceStep { Id = 1, TimeReduction = 50 },
                new BuffToleranceStep { Id = 2, TimeReduction = 50 }
            ]
        };

        var modifiersField = typeof(BuffGameData).GetField("_buffModifiers", flags)!;
        if (modifiersField.GetValue(BuffGameData.Instance) == null)
            modifiersField.SetValue(BuffGameData.Instance, new Dictionary<uint, List<BuffModifier>>());
    }
}
