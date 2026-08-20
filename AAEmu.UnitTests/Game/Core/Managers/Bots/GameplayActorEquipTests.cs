using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 Adventurer v1 prerequisite — the Equip contract action on the
/// IGameplayActor surface, through the REAL engine path: the exact
/// CSSwapItemsPacket Inventory→Equipment move
/// (Inventory.SplitOrMoveItem with the SwapItems task type). The engine's
/// EquipmentContainer.CanAccept validates slot compatibility BEFORE
/// anything moves; the target slot comes from the engine's own
/// EquipmentContainer.GetAllowedGearSlots(template) — first EMPTY allowed
/// slot, else the first allowed slot (client equip-over-occupied swap
/// semantics: the occupant moves back to the vacated bag slot).
///
/// Contract tests run headless — no controller, no client, no packets
/// (Unit.SendPacket is null-safe without a Connection). The engine gates
/// equips on slot compatibility ONLY (no level check on this path —
/// recorded in the interface doc).
///
/// Idempotency proofs (the acceptance-criterion-3 family):
///  - same-key retry: rejected pre-flight by the key gate (no Running
///    transition, equipment/bag untouched);
///  - fresh-key retry after a success: the bag no longer holds the
///    template — Rejected("not found in bag"), nothing executes twice.
/// </summary>
[NotInParallel]
public class GameplayActorEquipTests
{
    // 9002x — the 9000x fixture range is shared across suites (90001/90002
    // rig, 90003 M53 cooldown, 90010 spike rotation); these ids are unique
    // to this suite. Slot-type seeding is missing-only and process-wide, so
    // a template id must keep ONE slot type across tests (90021/90022 are
    // Mainhand-only; 90020/90023 are OneHanded).
    private const uint SwordTemplateId = 90_020;
    private const uint SwordTemplateId2 = 90_021;
    private const uint MainhandOnlyTemplateId = 90_022;
    private const uint SwordTemplateId3 = 90_023;
    private const uint PlainTemplateId = 90_024; // never 1234 — TestItemTemplateId is shared; a bare seed would reset its use skill

    [Test]
    public async Task Equip_BaggedEquippable_MovesToFirstEmptyAllowedSlot()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m5-equip-1");
        GameplayActorTestRig.SeedEquipItemTemplate(SwordTemplateId); // OneHanded → Mainhand/Offhand
        GameplayActorTestRig.StockItem(session, SwordTemplateId, 1);

        var request = actor.Equip(SwordTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        // Real engine path: the item sits in the Mainhand equipment slot
        // (first empty allowed slot for OneHanded) and left the bag.
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped!.TemplateId).IsEqualTo(SwordTemplateId);
        await Assert.That(actor.Character.Inventory.Bag.GetItemByItemId(equipped.Id)).IsNull();

        // Full audit record shape.
        var record = actor.AuditTrace[0];
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Equip);
        await Assert.That(record.TargetId).IsEqualTo(SwordTemplateId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.First().StartsWith("Requested")).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (equipping"))).IsTrue();
        await Assert.That(record.StateChanges.Last().StartsWith("Completed")).IsTrue();
    }

    [Test]
    public async Task Equip_OccupiedOnlySlot_SwapsOccupantBackToBag()
    {
        // Mainhand-slot-type templates allow ONLY the Mainhand slot, so the
        // second equip lands on the occupied slot — client swap semantics:
        // the occupant must move back to the bag, not vanish.
        var (actor, session) = GameplayActorTestRig.CreateActor("m5-equip-2");
        GameplayActorTestRig.SeedEquipItemTemplate(MainhandOnlyTemplateId, EquipmentItemSlotType.Mainhand);
        GameplayActorTestRig.SeedEquipItemTemplate(SwordTemplateId2, EquipmentItemSlotType.Mainhand);
        GameplayActorTestRig.StockItem(session, MainhandOnlyTemplateId, 1);
        GameplayActorTestRig.StockItem(session, SwordTemplateId2, 1);

        await Assert.That(actor.Equip(MainhandOnlyTemplateId).State).IsEqualTo(ActorLifecycleState.Completed);
        var request = actor.Equip(SwordTemplateId2);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped!.TemplateId).IsEqualTo(SwordTemplateId2);
        // The swapped-out occupant is back in the BAG (not lost).
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(MainhandOnlyTemplateId, -1, out var bagItems, out _);
        await Assert.That(bagItems.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Equip_OneHandedSecondItem_TakesOffhandNotSwap()
    {
        // OneHanded allows Mainhand AND Offhand: the second equip must take
        // the EMPTY Offhand slot, not displace the mainhand weapon.
        var (actor, session) = GameplayActorTestRig.CreateActor("m5-equip-3");
        GameplayActorTestRig.SeedEquipItemTemplate(SwordTemplateId);
        GameplayActorTestRig.SeedEquipItemTemplate(SwordTemplateId3);
        GameplayActorTestRig.StockItem(session, SwordTemplateId, 1);
        GameplayActorTestRig.StockItem(session, SwordTemplateId3, 1);

        await Assert.That(actor.Equip(SwordTemplateId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Equip(SwordTemplateId3).State).IsEqualTo(ActorLifecycleState.Completed);

        var equipment = actor.Character.Inventory.Equipment;
        await Assert.That(equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand)?.TemplateId).IsEqualTo(SwordTemplateId);
        await Assert.That(equipment.GetItemBySlot((int)EquipmentItemSlot.Offhand)?.TemplateId).IsEqualTo(SwordTemplateId3);
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(SwordTemplateId, -1, out var leftover, out _);
        await Assert.That(leftover.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Equip_NotInBag_Rejected_NothingMoves()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m5-equip-4");
        GameplayActorTestRig.SeedEquipItemTemplate(SwordTemplateId);

        var request = actor.Equip(SwordTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in bag")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand)).IsNull();
    }

    [Test]
    public async Task Equip_NonEquippable_Rejected_NothingMoves()
    {
        // Plain ItemTemplate (SeedItemTemplate) — the engine's slot table
        // maps no gear slot for it ("must be a equip-able item"). Own
        // fixture id: NEVER the shared TestItemTemplateId 1234 — a bare
        // seed here would reset the use skill the B1 suites rely on.
        var (actor, session) = GameplayActorTestRig.CreateActor("m5-equip-5");
        GameplayActorTestRig.SeedItemTemplate(PlainTemplateId);
        GameplayActorTestRig.StockItem(session, PlainTemplateId, 1);

        var request = actor.Equip(PlainTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not equippable")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        // Still in the bag, nothing equipped.
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(PlainTemplateId, -1, out var stillBagged, out _);
        await Assert.That(stillBagged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Equip_RetrySameKey_RejectedPreFlight_NoDoubleEquip()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m5-equip-6");
        GameplayActorTestRig.SeedEquipItemTemplate(SwordTemplateId);
        GameplayActorTestRig.StockItem(session, SwordTemplateId, 1);

        var original = actor.Equip(SwordTemplateId, idempotencyKey: "equip:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Controller-level timeout retry with the SAME key: refused
        // pre-flight by the ledger; the audit record shows no Running.
        var retry = actor.Equip(SwordTemplateId, idempotencyKey: "equip:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Exactly one equip landed: item still in Mainhand, bag empty.
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        await Assert.That(equipped?.TemplateId).IsEqualTo(SwordTemplateId);

        // Correlation: the key still resolves to the ORIGINAL outcome.
        var correlated = actor.FindByKey("equip:1");
        await Assert.That(correlated).IsNotNull();
        await Assert.That(correlated!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(correlated.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Equip_RetryFreshKeyAfterSuccess_EngineBackstop_NoDoubleEquip()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m5-equip-7");
        GameplayActorTestRig.SeedEquipItemTemplate(SwordTemplateId);
        GameplayActorTestRig.StockItem(session, SwordTemplateId, 1);

        // Timeout ambiguity: the controller retries with a FRESH key. The
        // bag no longer holds the template — the engine-true backstop
        // refuses with no Running transition. Exactly one equip landed.
        await Assert.That(actor.Equip(SwordTemplateId, idempotencyKey: "a").State).IsEqualTo(ActorLifecycleState.Completed);
        var retry = actor.Equip(SwordTemplateId, idempotencyKey: "b");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        var equipped = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Mainhand);
        await Assert.That(equipped?.TemplateId).IsEqualTo(SwordTemplateId);
    }
}
