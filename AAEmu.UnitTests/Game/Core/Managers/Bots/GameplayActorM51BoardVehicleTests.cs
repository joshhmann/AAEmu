using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 BoardVehicle/UnboardVehicle (t_15343fdd — salvage of t_3b6f7135) —
/// the vehicle/transfer manager surface on the IGameplayActor contract
/// through REAL engine paths (NOT the mate path covered by B1 Mount/Dismount):
///
///   slave   : SlaveManager.BindSlave / UnbindSlave — the exact calls
///             CSBindSlavePacket (driver) and DoodadFuncAttachment's ship
///             branch (passenger) / CSDiscardSlavePacket make.
///   transfer: the seat doodad bond path (DoodadFuncAttachment:
///             Seat.LoadPassenger + BondDoodad + transform parenting +
///             SCBondDoodadPacket) — the same interaction a passenger
///             boarding a route carriage performs; unboard via the
///             CSUnbondDoodadPacket path.
///   glider  : equips/unequips the glider item into the Backpack slot
///             through the ordinary inventory path (SplitOrMoveItem /
///             TakeoffBackpack) — the real 1.2 "board a glider" step.
///
/// Retry tests prove the idempotency guarantee: a same-key retry never
/// double-boards (no duplicate AttachedCharacters entry, no double bond,
/// no double equip) and never double-unboards. All assertions run headless
/// — no controller, no client, no packets required (Unit.SendPacket is
/// null-safe without a Connection).
/// </summary>
[NotInParallel]
public class GameplayActorM51BoardVehicleTests
{
    #region Slave — real engine path

    [Test]
    public async Task BoardVehicle_SlaveDriver_CompletesThroughRealEnginePath()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-slave-1");
        var slave = SummonSlave(session, actor);

        var request = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(true);
        // Real engine state: attached at the driver seat through BindSlave.
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.Driver);
        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        await Assert.That(slave.AttachedCharacters[AttachPointKind.Driver].ObjId).IsEqualTo(actor.ActorId);
        var mounted = session.World.SlaveManager.GetIsMounted(actor.ActorId, out var attachPoint);
        await Assert.That(mounted).IsNotNull();
        await Assert.That(mounted!.ObjId).IsEqualTo(slave.ObjId);
        await Assert.That(attachPoint).IsEqualTo(AttachPointKind.Driver);

        // Structured trace record emitted with the full contract shape.
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.BoardVehicle);
        await Assert.That(record.TargetId).IsEqualTo(slave.ObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
    }

    [Test]
    public async Task BoardVehicle_SlavePassengerSeat_Completes()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-slave-2");
        var slave = SummonSlave(session, actor);

        var request = actor.BoardVehicle(slave.ObjId, AttachPointKind.Passenger0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        await Assert.That(slave.AttachedCharacters[AttachPointKind.Passenger0].ObjId).IsEqualTo(actor.ActorId);
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.Passenger0);
    }

    [Test]
    public async Task BoardVehicle_AlreadyBoarded_Rejected_StateUnchanged()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-slave-3");
        var slave = SummonSlave(session, actor);

        var first = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // A second board (fresh key — the caller lost correlation) is
        // refused pre-flight: the engine is never re-entered, so the
        // slave can never be boarded twice.
        var second = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);

        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(second.Detail?.Contains("already boarded")).IsTrue();
        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        await Assert.That(slave.AttachedCharacters[AttachPointKind.Driver].ObjId).IsEqualTo(actor.ActorId);
    }

    [Test]
    public async Task BoardVehicle_SlaveOccupiedSeat_Rejected()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-slave-5");
        var slave = SummonSlave(session, actor);
        // Occupy the driver seat with ANOTHER rider (the engine's own
        // AttachedCharacters surface). Must not be the actor itself — the
        // already-boarded pre-flight would fire first.
        var (other, _) = CreateActorOnUniqueWorld("m51-slave-5-rider");
        slave.AttachedCharacters[AttachPointKind.Driver] = other.Character;

        var request = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("occupied")).IsTrue();
        // The engine never re-entered: the occupier is untouched.
        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        await Assert.That(slave.AttachedCharacters[AttachPointKind.Driver].ObjId).IsEqualTo(other.ActorId);
    }

    [Test]
    public async Task BoardVehicle_DeadVehicle_Rejected()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-slave-4");
        var slave = SummonSlave(session, actor);
        slave.Hp = 0; // destroyed vehicle (the engine's 324 refusal)

        var request = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("destroyed")).IsTrue();
        await Assert.That(slave.AttachedCharacters).IsEmpty();
    }

    [Test]
    public async Task BoardVehicle_DriverSeatLockedToOwner_Rejected()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-slave-6");
        var slave = SummonSlave(session, actor);
        // Owner's-Mark (4867): the vehicle is locked to ITS summoner, who is
        // a DIFFERENT character than the actor.
        var (owner, _) = CreateActorOnUniqueWorld("m51-slave-6-owner");
        slave.Summoner = owner.Character;
        GameplayActorTestRig.SeedBuffTemplate((uint)AAEmu.Game.Models.Game.Skills.BuffConstants.OwnersMark);
        slave.Buffs.AddBuff((uint)AAEmu.Game.Models.Game.Skills.BuffConstants.OwnersMark, owner.Character);

        var request = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("locked to its owner")).IsTrue();
        await Assert.That(slave.AttachedCharacters).IsEmpty();
    }

    [Test]
    public async Task BoardVehicle_OutOfRange_Rejected()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-slave-7");
        var slave = SummonSlave(session, actor);
        // Move the vehicle beyond boarding range (the actor stays at origin).
        slave.Transform.Local.SetPosition(new System.Numerics.Vector3(1000f, 0f, 0f));

        var request = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("out of boarding range")).IsTrue();
        await Assert.That(slave.AttachedCharacters).IsEmpty();
    }

    [Test]
    public async Task BoardVehicle_UnknownTarget_Rejected()
    {
        var (actor, _) = CreateActorOnUniqueWorld("m51-slave-8");

        var request = actor.BoardVehicle(0x7FFF_FFFF, AttachPointKind.Driver);

        // Not a slave, not a transfer, and no such glider in inventory.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("no glider")).IsTrue();
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.BoardVehicle);
    }

    #endregion

    #region Transfer — seat bond path (DoodadFuncAttachment)

    [Test]
    public async Task BoardVehicle_TransferSeat_CompletesThroughBondPath()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-transfer-1");
        var transferObjId = GameplayActorTestRig.SpawnTransferWithSeat(session, actor);
        var transfer = session.World.TransferManager.GetTransfers().First(t => t.ObjId == transferObjId);

        var request = actor.BoardVehicle(transferObjId, AttachPointKind.Passenger0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // Real engine state: the bond landed on the seat doodad and the
        // transfer's passenger list gained the character.
        await Assert.That(actor.Character.Bonding).IsNotNull();
        await Assert.That(actor.Character.Bonding!.ObjId).IsEqualTo(GameplayActorTestRig.TransferSeatDoodadObjId);
        await Assert.That(transfer.AttachedCharacters).Contains(actor.Character);
        // Transform parenting (the engine's own seat bond side effect).
        await Assert.That(actor.Character.Transform.Parent).IsNotNull();
    }

    [Test]
    public async Task UnboardVehicle_TransferSeat_CompletesThroughUnbondPath()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-transfer-2");
        var transferObjId = GameplayActorTestRig.SpawnTransferWithSeat(session, actor);
        var transfer = session.World.TransferManager.GetTransfers().First(t => t.ObjId == transferObjId);

        var board = actor.BoardVehicle(transferObjId, AttachPointKind.Passenger0);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.UnboardVehicle();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Bonding).IsNull();
        await Assert.That(transfer.AttachedCharacters).IsEmpty();
        await Assert.That(actor.Character.Transform.Parent).IsNull();
    }

    #endregion

    #region Glider — inventory equip path

    [Test]
    public async Task BoardVehicle_Glider_EquipsThroughInventoryPath()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-glider-1");
        GameplayActorTestRig.StockGlider(session, actor);

        var request = actor.BoardVehicle(GameplayActorTestRig.GliderItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // Real engine state: the glider instance sits in the Backpack slot
        // (the CSSwapItemsPacket equip path).
        var equipped = actor.Character.Inventory!.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped!.TemplateId).IsEqualTo(GameplayActorTestRig.GliderItemTemplateId);
        // The bag no longer holds it (moved, not duplicated).
        await Assert.That(actor.Character.Inventory.Bag.Items.Count(i => i.TemplateId == GameplayActorTestRig.GliderItemTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task UnboardVehicle_Glider_UnequipsThroughTakeoffBackpack()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-glider-2");
        GameplayActorTestRig.StockGlider(session, actor);

        var board = actor.BoardVehicle(GameplayActorTestRig.GliderItemTemplateId);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.UnboardVehicle();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Inventory!.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(actor.Character.Inventory.Bag.Items.Count(i => i.TemplateId == GameplayActorTestRig.GliderItemTemplateId)).IsEqualTo(1);
    }

    #endregion

    #region Unboard — slave + rejection taxonomy

    [Test]
    public async Task UnboardVehicle_Slave_CompletesThroughRealEnginePath()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-unboard-1");
        var slave = SummonSlave(session, actor);

        var board = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.UnboardVehicle();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(true);
        // Real engine state: UnbindSlave detached the rider.
        await Assert.That(slave.AttachedCharacters).IsEmpty();
        await Assert.That(session.World.SlaveManager.GetIsMounted(actor.ActorId, out _)).IsNull();
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.UnboardVehicle);
    }

    [Test]
    public async Task UnboardVehicle_NotBoarded_RejectedStateTransition()
    {
        var (actor, _) = CreateActorOnUniqueWorld("m51-unboard-2");

        var request = actor.UnboardVehicle();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains("not boarded")).IsTrue();
        // No Running transition — the engine was never entered.
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task UnboardVehicle_WrongSlave_RejectedStateTransition()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-unboard-3");
        var slave = SummonSlave(session, actor);
        // A second, DIFFERENT vehicle in the same world (the actor is not
        // mounted on it) — the wrong-vehicle gate must refuse before the
        // engine is entered.
        var otherObjId = GameplayActorTestRig.SummonSlave(session, actor, 0x3010);

        var board = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.UnboardVehicle(otherObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains($"not {otherObjId}")).IsTrue();
        // Still boarded — the wrong-vehicle refusal never unboarded.
        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
    }

    [Test]
    public async Task UnboardVehicle_WrongGlider_RejectedStateTransition()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-unboard-4");
        GameplayActorTestRig.StockGlider(session, actor);
        var board = actor.BoardVehicle(GameplayActorTestRig.GliderItemTemplateId);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.UnboardVehicle(0x3001); // a slave objId, not the glider template

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains("not 12289")).IsTrue();
        // Still equipped.
        await Assert.That(actor.Character.Inventory!.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();
    }

    #endregion

    #region Idempotency — no double board / double unboard

    [Test]
    public async Task BoardVehicle_RetrySameKey_RejectedPreFlight_NoDoubleBoard()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-idem-1");
        var slave = SummonSlave(session, actor);

        var original = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver, idempotencyKey: "board:slave-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Controller-level retry with the SAME key (timeout ambiguity): the
        // ActorEffectLedger refuses pre-flight — no Running transition, so
        // the engine path is never re-entered and no second attachment can
        // land (the engine would otherwise refuse an occupied seat silently).
        var retry = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver, idempotencyKey: "board:slave-1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        // The refusal never replaced the locked outcome: a THIRD retry is still refused.
        var third = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver, idempotencyKey: "board:slave-1");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);
    }

    [Test]
    public async Task BoardVehicle_FreshKeyAfterSuccess_Rejected_AlreadyBoarded_NoDoubleBoard()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-idem-2");
        var slave = SummonSlave(session, actor);

        var original = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // A fresh-key retry (no caller correlation) is refused by the
        // already-boarded gate before any engine call.
        var retry = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver, idempotencyKey: "board:slave-fresh-key");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("already boarded")).IsTrue();
        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
    }

    [Test]
    public async Task UnboardVehicle_RetrySameKey_RejectedPreFlight_NoDoubleUnboard()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-idem-3");
        var slave = SummonSlave(session, actor);

        var board = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);

        var original = actor.UnboardVehicle(idempotencyKey: "unboard:slave-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Same-key retry: refused pre-flight by the ledger — and even a
        // fresh-key retry would hit the not-boarded gate. Either way the
        // rider can never be detached twice.
        var retry = actor.UnboardVehicle(idempotencyKey: "unboard:slave-1");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(slave.AttachedCharacters).IsEmpty();
    }

    [Test]
    public async Task UnboardVehicle_FreshKeyAfterSuccess_Rejected_NotBoarded_NoDoubleUnboard()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-idem-4");
        var slave = SummonSlave(session, actor);

        var board = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);
        var unboard = actor.UnboardVehicle();
        await Assert.That(unboard.State).IsEqualTo(ActorLifecycleState.Completed);

        // A fresh-key retry resolves no mounted vehicle — the not-boarded
        // gate refuses before any engine call.
        var retry = actor.UnboardVehicle(idempotencyKey: "unboard:slave-fresh-key");

        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("not boarded")).IsTrue();
        await Assert.That(slave.AttachedCharacters).IsEmpty();
    }

    #endregion

    #region Trace record

    [Test]
    public async Task BoardVehicle_TraceRecord_EmittedWithFullShape()
    {
        var (actor, session) = CreateActorOnUniqueWorld("m51-trace-1");
        var slave = SummonSlave(session, actor);

        var request = actor.BoardVehicle(slave.ObjId, AttachPointKind.Driver);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.BoardVehicle);
        await Assert.That(record.TargetId).IsEqualTo(slave.ObjId);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
        await Assert.That(record.StateChanges.Last()).Contains("Completed");
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.StartedAtUtc != default).IsTrue();
        await Assert.That(record.CompletedAtUtc != default).IsTrue();
        // Structured wire shape (ROADMAP M5 field names).
        var json = record.ToJson();
        await Assert.That(json).Contains("\"trace_id\"");
        await Assert.That(json).Contains("\"actor_id\"");
        await Assert.That(json).Contains("\"action\":\"BoardVehicle\"");
        await Assert.That(json).Contains("\"target_id\"");
        await Assert.That(json).Contains("\"requested_at\"");
        await Assert.That(json).Contains("\"started_at\"");
        await Assert.That(json).Contains("\"completed_at\"");
        await Assert.That(json).Contains("\"result\":\"Completed\"");
        await Assert.That(json).Contains("\"state_changes\"");
    }

    #endregion

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// The headless session world is created with instance id 1 for EVERY
    /// actor (HeadlessSession.CreateWorld). WorldManager._worlds is keyed by
    /// that id and TryAdd is first-wins: the first test's world registers,
    /// every later test's world silently fails to register, and the rig's
    /// transform-instance-id bypass would then resolve the wrong world.
    /// Assign a unique instance id per test so each slave/transfer world
    /// resolves through its OWN registration slot.
    ///
    /// NOTE (t_15343fdd): the base is 0x7000_0000, NOT 0x4000_0000 — the
    /// sibling M5.1 rigs own 0x4000_0000 (rig base / Plant), 0x5000_0000
    /// (HouseBuild) and 0x6000_0000 (Harvest) with process-wide first-wins
    /// registration; sharing a base would let this class's worlds win the
    /// registry slots and strand every later Plant/HouseBuild/Harvest test.
    /// </summary>
    private static uint _nextWorldInstanceId = 0x7000_0000;

    private static (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);

        var uniqueWorldId = _nextWorldInstanceId++;
        var worldIdField = typeof(WorldInstance).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        worldIdField?.SetValue(session.World, uniqueWorldId);
        // Character transform instance id must match the patched world id so
        // the rig's ParentWorld resolution lands on THIS world.
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, uniqueWorldId);

        return (actor, session);
    }

    /// <summary>Summons the rig's test slave and returns the Slave object
    /// (the rig's SummonSlave registers it in the session world's slave
    /// registry — the surface SlaveManager.GetSlaveByObjId resolves).</summary>
    private static Slave SummonSlave(HeadlessSession session, GameplayActor actor)
        => session.World.SlaveManager.GetSlaveByObjId(GameplayActorTestRig.SummonSlave(session, actor))
           ?? throw new InvalidOperationException("rig slave not registered");
}
