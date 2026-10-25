using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using System.Reflection;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// A refused auto-attack must be distinguishable from a landed one.
///
/// <c>GameplayActor.AutoAttack</c> discarded the <see cref="SkillResult"/> from
/// <c>skill.Use(...)</c> and reported <c>Complete(...)</c> unconditionally, so a
/// cast the engine REFUSED (e.g. <c>TooFarRange</c>) and a cast that actually
/// fired produced byte-identical detail:
/// <c>auto-attack started (4) on 44327</c>.
///
/// A lane run read that as <c>verb=AutoAttack state=Completed delta=0</c> and
/// concluded skill 4 never dealt damage — which was wrong: the boar's HP did
/// fall on later ticks. The refusal was invisible, so a zero delta could not be
/// told apart from "the engine never let the swing happen".
///
/// These tests drive the real <see cref="GameplayActor.AutoAttack"/> seam and
/// assert the observable contract: the detail states the engine's verdict.
/// </summary>
[NotInParallel]
public class GameplayActorAutoAttackRefusalTests
{
    /// <summary>Ranged NPC template id used by the rig's default spawn.</summary>
    private const uint RangedNpcTemplateId = 1000;

    private static readonly Vector3 Here = new(100f, 100f, 10f);

    /// <summary>
    /// The engine refuses the swing — the request still completes (the
    /// auto-attack loop starts either way; this is observability, not control
    /// flow) but the detail must name the refusal so a consumer can tell it
    /// apart from a landed swing.
    /// </summary>
    [Test]
    public async Task AutoAttack_TargetBeyondWeaponRange_DetailNamesTheEngineRefusal()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-refused");
        var targetObjId = SpawnTarget(actor, session, Here + new Vector3(400f, 0f, 0f));

        var request = actor.AutoAttack(targetObjId);

        // Control flow is UNCHANGED by this fix: the request resolves exactly
        // as it did before — the refusal is reported, not converted into a
        // rejection.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        // The observable contract: a refusal is visible in the detail.
        var detail = request.Detail ?? string.Empty;
        await Assert.That(detail).Contains("refused");
        await Assert.That(detail).Contains(nameof(SkillResult.TooFarRange));
    }

    /// <summary>
    /// A swing the engine ACCEPTS must NOT claim a refusal — otherwise the
    /// token is noise and the two cases are merely differently-worded noise.
    /// The landed detail still names the skill and target it dispatched.
    /// </summary>
    [Test]
    public async Task AutoAttack_TargetInRange_DetailReportsNoRefusal()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-landed");
        var targetObjId = SpawnTarget(actor, session, Here + new Vector3(2f, 0f, 0f));

        var request = actor.AutoAttack(targetObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var detail = request.Detail ?? string.Empty;
        await Assert.That(detail).DoesNotContain("refused");
        await Assert.That(detail).Contains("auto-attack started");
        await Assert.That(detail).Contains(targetObjId.ToString());
    }

    // ------------------------------------------------------------ fixture

    [Test]
    public async Task NativeRequirementRefusal_CanLaterDealDamageWithoutRevivingTheCancelledSkill()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-requirement-clears");
        var target = SpawnTarget(actor, session, Here + new Vector3(2, 0, 0));
        var template = SkillManager.Instance.GetSkillTemplate(2);
        var previousEffects = template.Effects;
        var index = (Dictionary<string, List<UnitReqs>>)typeof(UnitRequirementsGameData)
            .GetField("<_unitReqsByOwnerType>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(UnitRequirementsGameData.Instance)!;
        var previousRequirements = index.GetValueOrDefault("Skill");
        try
        {
            // Supplied development fixture: one native level requirement and
            // a synchronous fixed-damage effect, not canonical skill2 data.
            var requiredLevel = (uint)actor.Character.Level + 1;
            index["Skill"] = (previousRequirements ?? []).Where(r => r.OwnerId != 2).Append(new UnitReqs
            {
                Id = 0x7f001, OwnerType = "Skill", OwnerId = 2,
                KindType = UnitReqsKindType.Level, Value1 = requiredLevel,
            }).ToList();
            template.Effects = [FixedDamage()];
            var refused = actor.AutoAttack(target);
            var task = actor.Character.AutoAttackTask!;
            var skill = task.Skill;
            await Assert.That(refused.Result).IsNotEqualTo(SkillResult.Success);
            await Assert.That(skill.Cancelled).IsTrue();
            await Assert.That(session.World.GetNpc(target)!.Hp).IsEqualTo(100);
            task.Execute();
            await Assert.That(((UseAutoAttackSkillTask)task).LastTick.State).IsEqualTo("refused");
            await Assert.That(((UseAutoAttackSkillTask)task).LastTick.Result).IsEqualTo((SkillResult)refused.Result!);

            actor.Character.Level = (byte)requiredLevel;
            actor.Character.GlobalCooldown = DateTime.MinValue;
            task.Execute();

            // Reject the tempting but incorrect theory that this flag alone
            // blocks damage: native effects can run while it remains set.
            await Assert.That(ReferenceEquals(skill, task.Skill)).IsTrue();
            await Assert.That(skill.Cancelled).IsTrue();
            await Assert.That(session.World.GetNpc(target)!.Hp).IsLessThan(100);
        }
        finally
        {
            template.Effects = previousEffects;
            if (previousRequirements == null) index.Remove("Skill");
            else index["Skill"] = previousRequirements;
            actor.StopAutoAttack();
        }
    }

    [Test]
    public async Task NativeLoop_GlobalCooldownAndRangePausesAreObservedWithoutRestart()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-paused-native");
        var target = SpawnTarget(actor, session, Here + new Vector3(2, 0, 0));
        _ = actor.AutoAttack(target);
        var task = (UseAutoAttackSkillTask)actor.Character.AutoAttackTask!;
        actor.Character.GlobalCooldown = DateTime.UtcNow.AddSeconds(30);
        task.Execute();
        await Assert.That(task.LastTick.State).IsEqualTo("global-cooldown");
        await Assert.That(task.LastTick.Result).IsNull();
        actor.Character.GlobalCooldown = DateTime.MinValue;
        GameplayActorTestRig.SetNpcPosition(session, target, Here + new Vector3(100, 0, 0));
        task.Execute();
        await Assert.That(task.LastTick.State).IsEqualTo("out-of-range");
        await Assert.That(task.LastTick.Result).IsNull();
        await Assert.That(ReferenceEquals(task, actor.Character.AutoAttackTask)).IsTrue();
        actor.StopAutoAttack();
    }

    [Test]
    public async Task CancelledNativeTask_IsReplacedInsteadOfReportedAlreadyActive()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-cancelled-task");
        var target = SpawnTarget(actor, session, Here + new Vector3(2, 0, 0));
        _ = actor.AutoAttack(target);
        var previous = actor.Character.AutoAttackTask!;
        previous.Cancel(); // Ordinary native cancellation leaves the character reference until its callback runs.
        await Assert.That(previous.Cancelled).IsTrue();

        var request = actor.AutoAttack(target);

        await Assert.That(ReferenceEquals(previous, actor.Character.AutoAttackTask)).IsFalse();
        await Assert.That(actor.Character.AutoAttackTask!.Cancelled).IsFalse();
        await Assert.That(request.Detail).DoesNotContain("already active");
        actor.StopAutoAttack();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ObsoleteNativeCallback_DoesNotStrikeOrClearReplacementAttack(bool cancelled)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-obsolete-callback");
        var target = SpawnTarget(actor, session, Here + new Vector3(2, 0, 0));
        _ = actor.AutoAttack(target);
        var previous = actor.Character.AutoAttackTask!;
        if (cancelled)
            previous.Cancel();
        // Supplied race state: the old worker was queued before cancellation,
        // and a new native task now owns the character's attack.
        var replacement = new UseAutoAttackSkillTask(new Skill(previous.Skill.Template), actor.Character);
        actor.Character.AutoAttackTask = replacement;
        actor.Character.IsAutoAttack = true;

        var template = previous.Skill.Template;
        var effects = template.Effects;
        try
        {
            template.Effects = [FixedDamage()];
            actor.Character.GlobalCooldown = DateTime.MinValue;
            previous.Execute();
            await Assert.That(session.World.GetNpc(target)!.Hp).IsEqualTo(100);
            await Assert.That(ReferenceEquals(replacement, actor.Character.AutoAttackTask)).IsTrue();
            await Assert.That(actor.Character.IsAutoAttack).IsTrue();
            await Assert.That(((UseAutoAttackSkillTask)previous).LastTick.State).IsEqualTo("superseded");
        }
        finally
        {
            template.Effects = effects;
            actor.StopAutoAttack();
        }
    }

    [Test]
    public async Task HealthyNativeTask_IsPreservedWhenTargetChanges()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-healthy-retarget");
        var first = SpawnTarget(actor, session, Here + new Vector3(2, 0, 0));
        var second = SpawnTarget(actor, session, Here + new Vector3(3, 0, 0));
        _ = actor.AutoAttack(first);
        var task = actor.Character.AutoAttackTask!;

        _ = actor.AutoAttack(second);

        await Assert.That(ReferenceEquals(task, actor.Character.AutoAttackTask)).IsTrue();
        task.Execute(); // The native loop follows CurrentTarget, not its last Skill.InitialTarget.
        await Assert.That(task.Skill.InitialTarget!.ObjId).IsEqualTo(second);
        actor.StopAutoAttack();
    }

    private static uint SpawnTarget(GameplayActor actor, HeadlessSession session, Vector3 npcPos)
    {
        GameplayActorTestRig.SetPosition(actor, Here);
        var region = session.World.GetRegionByPos(Here)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = GameplayActorTestRig.SpawnNpc(session, RangedNpcTemplateId);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = RangedNpcTemplateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
        return npcObjId;
    }

    private static SkillEffect FixedDamage() => new()
    {
        EffectId = 0x7f002,
        Template = new DamageEffect
        {
            Id = 0x7f002, DamageType = DamageType.Siege, UseFixedDamage = true,
            FixedMin = 10, FixedMax = 10, Multiplier = 1,
        },
        Chance = 100, Friendly = true, NonFriendly = true, Front = true, Back = true,
        EndLevel = byte.MaxValue, AlwaysHit = true, ApplicationMethod = SkillEffectApplicationMethod.Target,
    };
}
