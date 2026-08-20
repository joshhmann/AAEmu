using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Models.Game.Skills;

/// <summary>
/// BUG-016 regression rig: an area skill (TargetAreaRadius > 0) with
/// TargetSelection=Target centered its target list on the selected unit via
/// WorldManager.GetAround — which EXCLUDES the center object — so the
/// primary target was never affected (live proof: 150/150 successful casts
/// of skill 18131 in the M7 spike E2E, 0 damage). The fix re-adds targetSelf
/// for Target selection (Source already had it). Census on canonical
/// compact.sqlite3: 415 skills in the class, 13 player-learnable.
/// Effects are a recording stub — the pipeline scaffolding (dice, damage
/// formulas, packets) is orthogonal to the target-list bug; real-damage
/// proof is the adventurer-spike E2E with the 18131-led rotation.
/// [NotInParallel]: with this class in the parallel pool, suite
/// interleaving shifted and exposed latent shared-singleton races in the
/// economy rigs (25 auction/buy/sell failures when parallel; 2125/0 clean
/// tree, green when serialized).
/// </summary>
[NotInParallel]
public class SkillAreaTargetPrimaryTests
{
    private sealed class RecordingEffect : EffectTemplate
    {
        public List<uint> HitObjIds { get; } = [];
        public override bool OnActionTime => false;
        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
            CompressedGamePackets packetBuilder = null)
            => HitObjIds.Add(target.ObjId);
    }

    private static (Skill skill, RecordingEffect recorder) BuildAreaSkill(
        SkillTargetSelection selection, SkillTargetRelation relation, RecordingEffect recorder = null)
    {
        recorder ??= new RecordingEffect();
        var template = new SkillTemplate
        {
            Id = 99131,
            TargetType = SkillTargetType.AnyUnit,
            TargetRelation = relation,
            TargetSelection = selection,
            TargetAreaRadius = 2,
            // Forced hit: the dice outcome is irrelevant to the target-list
            // assertion and this skips combat-dice RNG.
            LevelRuleNoConsideration = true,
            DamageTypeId = (uint)DamageType.Melee,
            Effects =
            [
                new SkillEffect
                {
                    Template = recorder,
                    ApplicationMethod = SkillEffectApplicationMethod.Target,
                    // Chance < 100 is dice-gated (Skill.cs:1137); EndLevel 0
                    // would fail the level gate for any real caster level.
                    Chance = 100,
                    EndLevel = 255,
                }
            ]
        };
        return (new Skill(template, null), recorder);
    }

    /// <summary>
    /// Places a rig NPC at a position WITH region membership. Membership is
    /// established DIRECTLY (Region.AddObject + obj.Region): the normal
    /// WorldManager.AddVisibleObject path also broadcasts SCUnitStatePacket,
    /// which NREs on a template-less rig NPC (Npc.get_Scale /
    /// ModelPostureType). Pre-sets Transform._instanceId BEFORE ParentWorld —
    /// the ParentWorld setter writes InstanceId, whose setter re-enters
    /// ParentWorld with WorldManager.Instance.GetWorld(id) == null for
    /// headless worlds; with the backing field pre-set both writes no-op.
    /// Without region membership, GetAround (ApplyEffects' AoE source) sees
    /// NOTHING in the rig.
    /// </summary>
    private static Npc PlaceNpc(HeadlessSession session, uint templateId, float x, float y, float z)
    {
        GameplayActorTestRig.SpawnNpc(session, templateId);
        var npc = session.World.GetNpcByTemplateId(templateId);
        // Minimal template: GetAround(useModelSize: true) reads ModelSize →
        // Npc.Scale, which NREs template-less (the SummonMate rig pattern).
        npc.Template = new NpcTemplate { Scale = 1f };
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(npc.Transform, session.World.Id);
        npc.ParentWorld = session.World;
        npc.Transform.Local.SetPosition(x, y, z);
        RegionRegister(npc);
        return npc;
    }

    /// <summary>Direct region membership (no visibility broadcast) — the
    /// membership half of WorldManager.AddVisibleObject's first-time
    /// placement, without the packet path.</summary>
    private static void RegionRegister(AAEmu.Game.Models.Game.World.GameObject obj)
    {
        var region = obj.ParentWorld.GetRegionByPos(obj.Transform.World.Position);
        region.AddObject(obj);
        obj.Region = region;
    }

    [Test]
    public async Task ApplyEffects_TargetSelection_AreaSkill_HitsPrimaryAndNeighbor_NotOutOfRange()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("bug016-target");
        var character = actor.Character;
        GameplayActorTestRig.SetPosition(actor, new System.Numerics.Vector3(0, 0, 0));

        // AnyUnit keeps the orthogonal dice/durability machinery (Hostile
        // TargetType gate, Skill.cs:970-1030) out of the rig — the
        // target-list bug under test lives in the radius branch and the
        // Hostile RELATION filter still applies.
        var primary = PlaceNpc(session, 7001, 0f, 0f, 0f);
        var neighbor = PlaceNpc(session, 7002, 1f, 0f, 0f);  // inside radius 2
        var far = PlaceNpc(session, 7003, 50f, 0f, 0f);      // outside radius 2

        var (skill, recorder) = BuildAreaSkill(SkillTargetSelection.Target, SkillTargetRelation.Hostile);

        skill.ApplyEffects(character, new SkillCasterUnit { ObjId = character.ObjId },
            primary, new SkillCastUnitTarget(primary.ObjId), null);

        // Pre-fix the primary was NEVER in the list (GetAround excludes the
        // center) — this is the BUG-016 assertion.
        await Assert.That(recorder.HitObjIds).Contains(primary.ObjId);
        await Assert.That(recorder.HitObjIds).Contains(neighbor.ObjId);
        await Assert.That(recorder.HitObjIds).DoesNotContain(far.ObjId);
    }

    [Test]
    public async Task ApplyEffects_SourceSelection_AreaSkill_StillIncludesCaster()
    {
        // Regression guard for the pre-existing Source-selection line the
        // BUG-016 fix extends: self-centered area skills must keep hitting
        // the caster. Relation Any — a self-buff-style skill (Hostile would
        // correctly filter the caster OUT via CanAttack(self) == false).
        var (actor, session) = GameplayActorTestRig.CreateActor("bug016-source");
        var character = actor.Character;
        GameplayActorTestRig.SetPosition(actor, new System.Numerics.Vector3(0, 0, 0));
        // The caster is the GetAround center here — region-register it too
        // (the rig's SetPosition bypasses region membership entirely).
        RegionRegister(character);
        var neighbor = PlaceNpc(session, 7004, 1f, 0f, 0f);

        var (skill, recorder) = BuildAreaSkill(SkillTargetSelection.Source, SkillTargetRelation.Any);

        skill.ApplyEffects(character, new SkillCasterUnit { ObjId = character.ObjId },
            character, new SkillCastUnitTarget(character.ObjId), null);

        await Assert.That(recorder.HitObjIds).Contains(character.ObjId);
        await Assert.That(recorder.HitObjIds).Contains(neighbor.ObjId);
    }
}
