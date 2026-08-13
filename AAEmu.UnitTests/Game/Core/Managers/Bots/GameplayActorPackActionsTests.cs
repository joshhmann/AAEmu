using System.Text.Json;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 (t_64ecf525): PackPickup and PutDown on the IGameplayActor v2
/// surface through the REAL engine paths — no direct DB manipulation, no
/// GM repair, no direct Transform/ZoneId assignment:
///  - PackPickup drives RecoverItem.Execute — the exact call
///    CSLootOpenBagPacket makes for pack-style pickup with the generic
///    world recover skill (11361). DoodadFuncRecoverItem grants the pack
///    back into the Backpack equipment slot (auto-equip trade pack path)
///    and RecoverItem deletes the placed-pack doodad. The actor's
///    completion proof is the post-state container transition
///    (System → Equipment), because the engine signals refusal only via
///    error packets.
///  - PutDown drives the pack item's use skill through the exact
///    CSStartSkillPacket SkillItem branch (Skill.Use with a SkillItem
///    caster). PutDownBackpackEffect moves the pack from the Backpack
///    equipment slot into the System container. The actor's completion
///    proof is the pack leaving the Backpack slot (the effect early-
///    returns silently on public-farm / house-permission refusals).
///  - Retry tests prove the idempotency guarantee: a same-key retry never
///    grants a pack twice and never places a pack twice; the engine state
///    (deleted doodad / System-container pack) is the backstop for
///    fresh-key retries after a timeout ambiguity.
/// Headless notes: the rig's placed-pack doodad is non-persistent so the
/// MySQL tails (Doodad.Save / persistent Delete) stay out of unit tests —
/// the persistent-row path is the M4_2TradePackRestartE2eTests rig's
/// concern. The put-down doodad template is intentionally NOT registered
/// in the DoodadManager, so PutDownBackpackEffect's item move is the
/// exercised engine surface and the DB-persist tail is skipped the same
/// way (Create returns null → effect bails after the move, no MySQL
/// connection attempted).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class GameplayActorPackActionsTests
{
    private static readonly System.Numerics.Vector3 TestPosition = new(1000f, 1000f, 100f);

    [Before(Test)]
    public void SetUp()
    {
        // Base surface (missing-only) + the M5.1 pack surface: pack item
        // template, put-down skill + effect, PublicFarm/Housing singletons.
        GameplayActorTestRig.SeedPackSurface();
        SeedEquipSurface();

        // The equip path + the put-down effect's level gate need a real
        // level (canonical: packs are level-10+ content).
        // (Actors are created per test with unique names.)
    }

    // ================================================================ PackPickup — real engine path

    [Test]
    public async Task PackPickup_PlacedPack_CompletesThroughRealEnginePath_EquipsToBackpackSlot()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-packpickup-1");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);

        var request = actor.PackPickup(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(pack.Id);
        // Real engine state: the pack is equipped in the Backpack slot.
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped!.Id).IsEqualTo(pack.Id);
        // The placed-pack doodad was deleted by the engine path.
        await Assert.That(session.World.GetDoodad(doodadObjId)).IsNull();

        // Full audit record shape: {trace_id, actor_id, action, target_id,
        // requested_at, started_at, completed_at, result, state_changes}.
        var record = actor.AuditTrace[0];
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.PackPickup);
        await Assert.That(record.TargetId).IsEqualTo(doodadObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.First().StartsWith("Requested")).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Completed")).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (picking up placed pack"))).IsTrue();
    }

    [Test]
    public async Task PackPickup_AuditRecord_ToJson_CarriesFullTraceShape()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-packpickup-2");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);

        actor.PackPickup(doodadObjId);
        using var doc = JsonDocument.Parse(actor.AuditTrace[0].ToJson());
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("PackPickup");
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(doodadObjId);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(4);
        await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
    }

    [Test]
    public async Task PackPickup_UnknownDoodad_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-packpickup-3");

        var request = actor.PackPickup(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in world")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task PackPickup_OutOfRange_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-packpickup-4");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);
        // Move the doodad far outside the interaction range.
        var doodad = session.World.GetDoodad(doodadObjId);
        doodad.Transform.Local.SetPosition(TestPosition + new System.Numerics.Vector3(500f, 0f, 0f));

        var request = actor.PackPickup(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("out of interaction range")).IsTrue();
        // Nothing moved: the pack is still in the System container.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
    }

    [Test]
    public async Task PackPickup_NonRecoverableDoodad_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-packpickup-5");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        // A loot-type doodad (DoodadFuncLootItem) is NOT a recoverable pack:
        // the routing rule (DoodadFuncRecoverItem + 11361) must reject it.
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);
        var doodad = session.World.GetDoodad(doodadObjId);
        doodad.Transform.Local.SetPosition(TestPosition + new System.Numerics.Vector3(1f, 0f, 0f));

        var request = actor.PackPickup(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not a recoverable trade pack")).IsTrue();
        await Assert.That(session.World.GetDoodad(doodadObjId)).IsNotNull();
    }

    [Test]
    public async Task PackPickup_AlreadyCarryingPack_RejectedStateTransition()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-packpickup-6");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        // The actor already carries a pack in the Backpack slot.
        GameplayActorTestRig.EquipPack(actor);
        // A second pack lies placed in the same world (its item lives in
        // the actor's System container, as PutDownBackpackEffect leaves it).
        var placed = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, placed);

        var request = actor.PackPickup(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains("backpack slot occupied")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        // The placed pack was not consumed (its item stays in the System
        // container; only one pack is carried).
        await Assert.That(session.World.GetDoodad(doodadObjId)).IsNotNull();
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(placed.Id)).IsNotNull();
        var carried = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(carried).IsNotNull();
        await Assert.That(carried!.Id).IsNotEqualTo(placed.Id);
    }

    [Test]
    public async Task PackPickup_AlreadyPickedUp_Rejected_NoDoubleGrant()
    {
        // Anti-dupe arrangement: actor A already picked the pack up (it
        // sits in A's Backpack slot — no longer in a System container),
        // but the placed-pack doodad still exists (stale). Actor B, whose
        // backpack slot is free, tries to pick it up: the actor pre-flight
        // passes and the ENGINE's System-container check
        // (DoodadFuncRecoverItem) refuses the re-grant.
        var (actorA, _) = GameplayActorTestRig.CreateActor("m51-packpickup-7a");
        actorA.Character.Level = 10;
        GameplayActorTestRig.EquipPack(actorA);
        var pack = actorA.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);

        var (actorB, sessionB) = GameplayActorTestRig.CreateActor("m51-packpickup-7b");
        GameplayActorTestRig.SetPosition(actorB, TestPosition);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(sessionB, actorB, pack!);

        var request = actorB.PackPickup(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("did not take effect")).IsTrue();
        // No double grant: B's Backpack slot stayed empty and the pack is
        // still on A.
        await Assert.That(actorB.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(actorA.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id)
            .IsEqualTo(pack!.Id);
    }

    // ================================================================ PackPickup — retry idempotency

    [Test]
    public async Task PackPickup_RetrySameKey_Rejected_PackGrantedExactlyOnce()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-packpickup-retry-1");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);

        var original = actor.PackPickup(doodadObjId, idempotencyKey: "pack-pickup:1001");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id)
            .IsEqualTo(pack.Id);

        // Controller-level timeout retry with the SAME key: refused
        // pre-flight — the pack is NOT granted a second time.
        var retry = actor.PackPickup(doodadObjId, idempotencyKey: "pack-pickup:1001");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // A THIRD retry is refused too (the refusal never replaced the lock).
        var third = actor.PackPickup(doodadObjId, idempotencyKey: "pack-pickup:1001");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);

        // Exactly one pack was granted (the Backpack slot holds one pack).
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped!.Id).IsEqualTo(pack.Id);

        // FindByKey correlates back to the ORIGINAL completed attempt.
        var byKey = actor.FindByKey("pack-pickup:1001");
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);
    }

    [Test]
    public async Task PackPickup_TimeoutAmbiguity_FreshKeyRetry_GrantsNothing()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-packpickup-retry-2");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);

        var original = actor.PackPickup(doodadObjId, idempotencyKey: "pack-pickup:1002");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // The controller lost the outcome and retries with a FRESH key:
        // the engine state is the backstop — the doodad is gone, so the
        // retry cannot grant a second pack.
        var fresh = actor.PackPickup(doodadObjId);
        await Assert.That(fresh.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(fresh.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(fresh.Detail?.Contains("not found in world")).IsTrue();

        // Still exactly one pack (the Backpack slot holds one pack).
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped!.Id).IsEqualTo(pack.Id);
    }

    // ================================================================ PutDown — real engine path

    [Test]
    public async Task PutDown_CarriedPack_CompletesThroughRealEnginePath_MovesToSystemContainer()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-1");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor);
        var pack = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);

        var request = actor.PutDown(GameplayActorTestRig.PackTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(true);
        // Real engine state: the pack left the Backpack slot…
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        // …and moved into the System container (PutDownBackpackEffect's
        // move — the retry-proof state).
        var inSystem = actor.Character.Inventory.SystemContainer.GetItemByItemId(pack!.Id);
        await Assert.That(inSystem).IsNotNull();
        await Assert.That(inSystem!.TemplateId).IsEqualTo(GameplayActorTestRig.PackTemplateId);

        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.PutDown);
        await Assert.That(record.TargetId).IsEqualTo(GameplayActorTestRig.PackTemplateId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.First().StartsWith("Requested")).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Completed")).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (putting down trade pack"))).IsTrue();
    }

    [Test]
    public async Task PutDown_AuditRecord_ToJson_CarriesFullTraceShape()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-2");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor);

        actor.PutDown(GameplayActorTestRig.PackTemplateId);
        using var doc = JsonDocument.Parse(actor.AuditTrace[0].ToJson());
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("PutDown");
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(GameplayActorTestRig.PackTemplateId);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(4);
        await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
    }

    [Test]
    public async Task PutDown_NoPackInBackpackSlot_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-3");

        var request = actor.PutDown(GameplayActorTestRig.PackTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not carried in the backpack slot")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task PutDown_NonPackItemInSlot_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-4");
        // A backpack-slot item that is NOT an auto-equip trade pack: a
        // BackpackTemplate with BindOnEquip (the exact predicate
        // IsAutoEquipTradePack rejects — bind-on-equip packs never
        // auto-equip on pickup). It still fits the Backpack slot
        // (EquipmentContainer.CanAccept accepts any BackpackTemplate there).
        const uint bindOnEquipTemplateId = 92_002;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.ContainsKey(bindOnEquipTemplateId))
            templates[bindOnEquipTemplateId] = new BackpackTemplate
            {
                Id = bindOnEquipTemplateId,
                MaxCount = 1,
                BackpackType = BackpackType.TradePack,
                BindType = ItemBindType.BindOnEquip
            };
        var item = ItemManager.Instance.Create(bindOnEquipTemplateId, 1, 0);
        actor.Character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, item, (int)EquipmentItemSlot.Backpack);

        var request = actor.PutDown(bindOnEquipTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not an auto-equip trade pack")).IsTrue();
        // The item was not consumed.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id)
            .IsEqualTo(item.Id);
    }

    [Test]
    public async Task PutDown_PackWithoutUseSkill_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-5");
        // A trade-pack-shaped template without a put-down use skill.
        const uint skilllessTemplateId = 92_003;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.ContainsKey(skilllessTemplateId))
            templates[skilllessTemplateId] = new BackpackTemplate
            {
                Id = skilllessTemplateId,
                MaxCount = 1,
                BackpackType = BackpackType.TradePack,
                UseSkillId = 0
            };
        GameplayActorTestRig.EquipPack(actor, skilllessTemplateId);

        var request = actor.PutDown(skilllessTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("no put-down use skill")).IsTrue();
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();
    }

    [Test]
    public async Task PutDown_UnknownUseSkill_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-6");
        // A trade pack whose use skill template does not exist.
        const uint unknownSkillTemplateId = 92_004;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.ContainsKey(unknownSkillTemplateId))
            templates[unknownSkillTemplateId] = new BackpackTemplate
            {
                Id = unknownSkillTemplateId,
                MaxCount = 1,
                BackpackType = BackpackType.TradePack,
                UseSkillId = 92_999
            };
        GameplayActorTestRig.EquipPack(actor, unknownSkillTemplateId);

        var request = actor.PutDown(unknownSkillTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("put-down skill 92999 not found")).IsTrue();
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();
    }

    // ================================================================ PutDown — retry idempotency

    [Test]
    public async Task PutDown_RetrySameKey_Rejected_PackPlacedExactlyOnce()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-retry-1");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor);
        var pack = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);

        var original = actor.PutDown(GameplayActorTestRig.PackTemplateId, idempotencyKey: "pack-putdown:2001");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();

        // Controller-level timeout retry with the SAME key: refused
        // pre-flight — the pack is NOT placed a second time.
        var retry = actor.PutDown(GameplayActorTestRig.PackTemplateId, idempotencyKey: "pack-putdown:2001");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // A THIRD retry is refused too (the refusal never replaced the lock).
        var third = actor.PutDown(GameplayActorTestRig.PackTemplateId, idempotencyKey: "pack-putdown:2001");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);

        // Exactly one placement: the System container holds the single pack
        // instance (one pack, one move).
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(pack!.Id)).IsNotNull();

        // FindByKey correlates back to the ORIGINAL completed attempt.
        var byKey = actor.FindByKey("pack-putdown:2001");
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);
    }

    [Test]
    public async Task PutDown_TimeoutAmbiguity_FreshKeyRetry_NoDoublePlacement()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-putdown-retry-2");
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor);
        var pack = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);

        var original = actor.PutDown(GameplayActorTestRig.PackTemplateId, idempotencyKey: "pack-putdown:2002");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // The controller lost the outcome and retries with a FRESH key:
        // the engine state is the backstop — the pack is already in the
        // System container, so the retry finds no pack in the slot and
        // cannot place it twice.
        var fresh = actor.PutDown(GameplayActorTestRig.PackTemplateId);
        await Assert.That(fresh.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(fresh.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(fresh.Detail?.Contains("not carried in the backpack slot")).IsTrue();

        // The System container still holds the single pack instance.
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(pack!.Id)).IsNotNull();
        var systemPacks = actor.Character.Inventory.SystemContainer.GetAllItemsByTemplate(GameplayActorTestRig.PackTemplateId, -1, out var items, out _);
        await Assert.That(items.Count).IsEqualTo(1);
    }

    // ================================================================ rig helpers

    /// <summary>
    /// Creates a pack item in the actor's System container — the exact
    /// post-put-down state PutDownBackpackEffect leaves (anti-dupe
    /// invariant for pickup: the item must live in a System container).
    /// </summary>
    private static Item CreateSystemPack(GameplayActor actor)
    {
        var pack = ItemManager.Instance.Create(GameplayActorTestRig.PackTemplateId, 1, 0);
        pack.OwnerId = actor.Character.Id;
        pack.SlotType = SlotType.System;
        actor.Character.Inventory.SystemContainer.AddOrMoveExistingItem(ItemTaskType.DropBackpack, pack);
        return pack;
    }

    /// <summary>
    /// Equipping a pack runs the real Unit.UpdateGearBonuses path
    /// (ItemGameData.GetItemBuff + SkillManager buff lookups + QuestManager
    /// acquire events). Seed the registry surfaces so equip doesn't NRE
    /// (BotBodyPartEquipmentTests.SeedPacketSurface pattern; missing-only).
    /// </summary>
    private static void SeedEquipSurface()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var skillManager = SkillManager.Instance;
        foreach (var field in typeof(SkillManager).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(skillManager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(skillManager, Activator.CreateInstance(dictType));
            }
        }

        var buffGameData = AAEmu.Game.GameData.BuffGameData.Instance;
        foreach (var field in typeof(AAEmu.Game.GameData.BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        var itemGameData = AAEmu.Game.GameData.ItemGameData.Instance;
        if (GetField(itemGameData, "_itemGradeBuffs") == null)
            SetField(itemGameData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
    }

    private static object GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        return field.GetValue(target)!;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }
}
