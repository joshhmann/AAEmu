using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// B1 action implementations (t_a5edc1e6) — UseItem and Mount/Dismount on
/// the IGameplayActor surface through REAL engine paths:
///  - UseItem resolves the item through ordinary inventory services
///    (Inventory.GetAllItemsByTemplate), validates existence / charges /
///    use skill / skill template, then applies through the exact pipeline
///    the client's item-use path drives — Skill.Use with a SkillItem caster
///    (the CSStartSkillPacket SkillItem branch). The engine evaluates
///    requirements, cooldown, GCD, mana and consumes reagents through the
///    ordinary inventory.
///  - Mount/Dismount drive the normal mount pipeline — MateManager.MountMate
///    / UnMountMate, the same methods the CSMountMatePacket /
///    CSUnMountMatePacket handlers use — with mount-state discipline:
///    already-mounted / not-mounted are StateTransition rejections,
///    invalid/unavailable targets are RejectedAction rejections.
///  - Retry tests prove the idempotency guarantee: a same-key retry never
///    consumes the item twice and never flips the mount state.
/// All assertions run headless — no controller, no client, no packets
/// required (Unit.SendPacket is null-safe without a Connection).
/// </summary>
[NotInParallel]
public class GameplayActorB1ActionsTests
{
    #region UseItem — real pipeline

    [Test]
    public async Task UseItem_ItemInInventory_CompletesThroughRealSkillPipeline_ConsumesOne()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-useitem-1");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);

        var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(SkillResult.Success);
        // Real pipeline consumption: the skill's reagent entry consumed one
        // unit through Inventory.ConsumeItem (2 → 1).
        await Assert.That(session.Character.Inventory.GetItemsCount(GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
        await Assert.That(request.Detail?.Contains("used")).IsTrue();

        // Full audit record shape: {trace_id, actor_id, action, target_id,
        // requested_at, started_at, completed_at, result, state_changes}.
        var record = actor.AuditTrace[0];
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.UseItem);
        await Assert.That(record.TargetId).IsEqualTo(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.First().StartsWith("Requested")).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Completed")).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (using item"))).IsTrue();
    }

    [Test]
    public async Task UseItem_AuditRecord_ToJson_CarriesFullTraceShape()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-useitem-2");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 1);

        actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
        using var doc = JsonDocument.Parse(actor.AuditTrace[0].ToJson());
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("UseItem");
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(4);
        await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
    }

    [Test]
    public async Task UseItem_ItemNotInInventory_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-useitem-3");

        var request = actor.UseItem(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in inventory")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task UseItem_ItemWithoutUseSkill_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-useitem-4");
        // A plain item template with no use skill (registered + stocked).
        const uint plainTemplateId = 5678;
        GameplayActorTestRig.RegisterPlainItemTemplate(plainTemplateId);
        GameplayActorTestRig.StockItem(session, plainTemplateId, 1);

        var request = actor.UseItem(plainTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not usable (no use skill)")).IsTrue();
        // Nothing was consumed by the refusal.
        await Assert.That(session.Character.Inventory.GetItemsCount(plainTemplateId)).IsEqualTo(1);
    }

    [Test]
    public async Task UseItem_ExhaustedCharges_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-useitem-5");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 1);

        var first = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(session.Character.Inventory.GetItemsCount(GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(0);

        // The stack is spent. The engine REMOVES exhausted stacks from the
        // container (ConsumeItem), so the charge check (0-count item still
        // present) or the not-found check both hold — either way the request
        // is refused before any engine call and nothing is consumed.
        var second = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(
            second.Detail?.Contains("has no charges left") == true
            || second.Detail?.Contains("not found in inventory") == true).IsTrue();
        await Assert.That(session.Character.Inventory.GetItemsCount(GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(0);
    }

    #endregion

    #region UseItem — retry idempotency (no duplicate consumption)

    [Test]
    public async Task UseItem_RetrySameKey_Rejected_ItemConsumedExactlyOnce()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-useitem-retry-1");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);

        var original = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "use-item:1234:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(session.Character.Inventory.GetItemsCount(GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);

        // Controller-level timeout retry with the SAME key: refused
        // pre-flight — the item is NOT consumed a second time.
        var retry = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "use-item:1234:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(session.Character.Inventory.GetItemsCount(GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);

        // A THIRD retry is refused too (the refusal never replaced the lock).
        var third = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "use-item:1234:1");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(session.Character.Inventory.GetItemsCount(GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);

        // FindByKey correlates back to the ORIGINAL completed attempt.
        var byKey = actor.FindByKey("use-item:1234:1");
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);

        // A genuinely NEW logical use (fresh key) is still allowed: 1 → 0.
        var fresh = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(fresh.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(session.Character.Inventory.GetItemsCount(GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(0);
    }

    #endregion

    #region Mount — real engine path

    [Test]
    public async Task Mount_ValidMate_CompletesThroughRealEnginePath()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-mount-1");
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor);

        var request = actor.Mount(mateObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(true);
        // Real engine state: the rider is seated and the registry resolves.
        await Assert.That(actor.Character.IsRiding).IsTrue();
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.Driver);
        var mounted = session.World.MateManager.GetIsMounted(actor.ActorId, out var attachPoint);
        await Assert.That(mounted).IsNotNull();
        await Assert.That(mounted!.ObjId).IsEqualTo(mateObjId);
        await Assert.That(attachPoint).IsEqualTo(AttachPointKind.Driver);

        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Mount);
        await Assert.That(record.TargetId).IsEqualTo(mateObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Mount_AlreadyMounted_Rejected_StateUnchanged()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-mount-2");
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor);

        var first = actor.Mount(mateObjId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        var second = actor.Mount(mateObjId);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(second.Detail?.Contains("already mounted")).IsTrue();

        // Still mounted exactly once — no double attach.
        await Assert.That(actor.Character.IsRiding).IsTrue();
        await Assert.That(session.World.MateManager.GetIsMounted(actor.ActorId, out _)).IsNotNull();
    }

    [Test]
    public async Task Mount_UnknownTarget_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-mount-3");

        var request = actor.Mount(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found or not active")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Mount_NotOwned_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-mount-4");
        // Mount owned by a different character (objId 0x5000).
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor, ownerObjId: 0x5000);

        var request = actor.Mount(mateObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not owned by actor")).IsTrue();
        await Assert.That(actor.Character.IsRiding).IsFalse();
    }

    [Test]
    public async Task Mount_DriverSeatTaken_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-mount-5");
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor);
        // Occupy the driver seat with another rider.
        var mate = (Mate)session.World.GetUnit(mateObjId)!;
        mate.Passengers[AttachPointKind.Driver]._objId = 0x5000;

        var request = actor.Mount(mateObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("driver seat unavailable")).IsTrue();
        await Assert.That(actor.Character.IsRiding).IsFalse();
    }

    #endregion

    #region Mount/Dismount — retry idempotency (no state flip)

    [Test]
    public async Task Mount_RetrySameKey_Rejected_MountStateNeverFlips()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-mount-retry-1");
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor);

        var original = actor.Mount(mateObjId, idempotencyKey: "mount:2001:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.IsRiding).IsTrue();

        // Timeout retry with the same key: refused pre-flight; the mount
        // state is NOT toggled twice (still mounted, same seat, same mate).
        var retry = actor.Mount(mateObjId, idempotencyKey: "mount:2001:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.Character.IsRiding).IsTrue();
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.Driver);
        var mounted = session.World.MateManager.GetIsMounted(actor.ActorId, out _);
        await Assert.That(mounted).IsNotNull();
        await Assert.That(mounted!.ObjId).IsEqualTo(mateObjId);
    }

    [Test]
    public async Task Dismount_NotMounted_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-dismount-1");

        var request = actor.Dismount();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains("not mounted")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task Dismount_AfterMount_CompletesThroughRealEnginePath()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-dismount-2");
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor);
        actor.Mount(mateObjId);

        var request = actor.Dismount();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(true);
        // Real engine state: rider detached, registry empty, transform unparented.
        await Assert.That(actor.Character.IsRiding).IsFalse();
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(session.World.MateManager.GetIsMounted(actor.ActorId, out _)).IsNull();
        await Assert.That(actor.Character.Transform.Parent).IsNull();
        // The driver seat is free again.
        var mate = (Mate)session.World.GetUnit(mateObjId)!;
        await Assert.That(mate.Passengers[AttachPointKind.Driver]._objId).IsEqualTo(0u);

        var record = actor.AuditTrace[1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Dismount);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Dismount_RetrySameKey_Rejected_MountStateNeverFlips()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-dismount-retry-1");
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor);
        actor.Mount(mateObjId);

        var original = actor.Dismount(idempotencyKey: "dismount:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.IsRiding).IsFalse();

        // Timeout retry with the same key: refused pre-flight — the state
        // does NOT flip back to mounted.
        var retry = actor.Dismount(idempotencyKey: "dismount:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(actor.Character.IsRiding).IsFalse();
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(session.World.MateManager.GetIsMounted(actor.ActorId, out _)).IsNull();

        // A fresh (unkeyed) dismount is also refused: still unmounted.
        var fresh = actor.Dismount();
        await Assert.That(fresh.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(fresh.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(actor.Character.IsRiding).IsFalse();
    }

    #endregion
}
