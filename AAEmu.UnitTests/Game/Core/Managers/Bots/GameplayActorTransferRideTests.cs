using System.Reflection;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Utils;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// TRANSFER-01 C5 gap — end-to-end proof that fixed-route transports are
/// boardable and ridable, headless:
///
///   board (the seat bond path DoodadFuncAttachment.Use drives) →
///   ride across ≥ 2 route points (the real TransferManager 100ms
///   MoveTo tick body, Transfer.MoveTo) with the rider stuck to the
///   transfer's transform → disembark (the CSUnbondDoodadPacket path)
///   at the transfer's current position.
///
/// Fail-closed: boarding a nonexistent transfer and boarding an
/// already-full seat refuse without mutating any engine state.
/// </summary>
[NotInParallel]
public class GameplayActorTransferRideTests
{
    [Test]
    public async Task Board_RideAcrossTwoRoutePoints_Disembark_AtCurrentTransferPosition()
    {
        var (actor, session) = CreateActorOnUniqueWorld("tr-ride-1");
        var transferObjId = GameplayActorTestRig.SpawnTransferWithSeat(session, actor);
        var transfer = session.World.TransferManager.GetTransfers().First(t => t.ObjId == transferObjId);
        SeedVehicleModel(transfer.Template.ModelId);

        // A cyclic 3-point route starting where the carriage sits.
        transfer.Template.Cyclic = true;
        transfer.Template.PathSmoothing = 0f;
        transfer.Routes[0] =
        [
            new WorldSpawnPosition { X = 0f, Y = 0f, Z = 0f },
            new WorldSpawnPosition { X = 40f, Y = 0f, Z = 0f },
            new WorldSpawnPosition { X = 90f, Y = 0f, Z = 0f }
        ];
        transfer.TransferPath = transfer.Routes[0];
        transfer.MoveStepIndex = 0;
        transfer.IsInPatrol = true;
        transfer.GoToPath(transfer);

        // (a) Board via the seat-interaction engine path.
        var board = actor.BoardVehicle(transferObjId, AttachPointKind.Passenger0);
        await Assert.That(board.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Bonding).IsNotNull();
        await Assert.That(actor.Character.Transform.Parent).IsNotNull();

        // (b) Ride: drive the real MoveTo tick body until the carriage has
        //     crossed its second checkpoint (targeting the third).
        var startPos = transfer.Transform.World.Position;
        var ticks = 0;
        while (transfer.MoveStepIndex < 2 && ticks++ < 1000)
        {
            transfer.MoveTo(transfer); // exactly what TransferManager.TransferTick runs
            transfer.Transform.FinalizeTransform();
        }

        await Assert.That(transfer.MoveStepIndex).IsGreaterThanOrEqualTo(2);
        var riddenDistance = MathUtil.CalculateDistance(startPos, transfer.Transform.World.Position, false);
        await Assert.That(riddenDistance).IsGreaterThan(30f); // actually left the station

        // The rider followed: still attached and within seat distance of the carriage.
        await Assert.That(actor.Character.Bonding).IsNotNull();
        var followGap = MathUtil.CalculateDistance(
            actor.Character.Transform.World.Position,
            transfer.Transform.World.Position, false);
        await Assert.That(followGap).IsLessThan(15f);

        // (c) Disembark mid-route: detached at the carriage's CURRENT position.
        var transferPosAtUnboard = transfer.Transform.World.Position;
        var unboard = actor.UnboardVehicle();
        await Assert.That(unboard.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Bonding).IsNull();
        await Assert.That(actor.Character.Transform.Parent).IsNull();
        await Assert.That(actor.Character.Transform.StickyParent).IsNull();
        await Assert.That(transfer.AttachedCharacters).IsEmpty();

        var disembarkGap = MathUtil.CalculateDistance(
            actor.Character.Transform.World.Position, transferPosAtUnboard, false);
        await Assert.That(disembarkGap).IsLessThan(15f);
    }

    [Test]
    public async Task Board_NonexistentTransfer_Rejected_NoMutation()
    {
        var (actor, session) = CreateActorOnUniqueWorld("tr-ride-2");
        var transfersBefore = session.World.TransferManager.GetTransfers().Length;

        var request = actor.BoardVehicle(0x7FFF_FFFF, AttachPointKind.Passenger0);

        // Not a registered transfer anywhere — refused without touching any state.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.Character.Bonding).IsNull();
        await Assert.That(actor.Character.Transform.Parent).IsNull();
        await Assert.That(actor.Character.Transform.StickyParent).IsNull();
        await Assert.That(session.World.TransferManager.GetTransfers().Length).IsEqualTo(transfersBefore);
    }

    [Test]
    public async Task Board_FullSeat_Rejected_NoMutation()
    {
        var (actor, session) = CreateActorOnUniqueWorld("tr-ride-3");
        var transferObjId = GameplayActorTestRig.SpawnTransferWithSeat(session, actor);
        var transfer = session.World.TransferManager.GetTransfers().First(t => t.ObjId == transferObjId);
        var seat = transfer.AttachedDoodads.Single();

        // Occupy the single spot through the engine's own seat registry
        // (VehicleSeat._seats) — Space=1 chair, all places taken.
        var seatsField = typeof(VehicleSeat).GetField("_seats", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var seats = (Dictionary<uint, List<uint>>)seatsField.GetValue(seat.Seat)!;
        seats[GameplayActorTestRig.TransferSeatDoodadObjId] = [987_654]; // some other rider's character id

        var request = actor.BoardVehicle(transferObjId, AttachPointKind.Passenger0);

        // Seat.LoadPassenger returned -1 → Use was a no-op → the post-state
        // verification refuses the request. Nothing mutated.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("did not take effect")).IsTrue();
        await Assert.That(actor.Character.Bonding).IsNull();
        await Assert.That(transfer.AttachedCharacters).IsEmpty();
        // The occupier kept the seat.
        await Assert.That(seats[GameplayActorTestRig.TransferSeatDoodadObjId][0]).IsEqualTo(987_654u);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>World instance-id base for THIS class — see the first-wins
    /// registry note on the sibling M5.1 rig (they own 0x40000000..0x70000000).</summary>
    private static uint _nextWorldInstanceId = 0x7100_0000;

    private static (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);

        var uniqueWorldId = _nextWorldInstanceId++;
        var worldIdField = typeof(WorldInstance).GetField("<Id>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        worldIdField?.SetValue(session.World, uniqueWorldId);
        typeof(Transform)
            .GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, uniqueWorldId);

        return (actor, session);
    }

    /// <summary>
    /// Seeds a headless vehicle_model + models row into ModelManager so
    /// Transfer.MoveTo's vehicle-model lookup succeeds (it early-returns
    /// without one, which would silently freeze the carriage).
    /// </summary>
    private static void SeedVehicleModel(uint modelId)
    {
        var manager = ModelManager.Instance;
        var modelsField = typeof(ModelManager).GetField("_models", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var typesField = typeof(ModelManager).GetField("_modelTypes", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var models = (Dictionary<string, Dictionary<uint, Model>>?)modelsField.GetValue(manager);
        if (models == null)
        {
            models = [];
            modelsField.SetValue(manager, models);
        }
        if (!models.TryGetValue("VehicleModel", out var vehicles))
        {
            vehicles = [];
            models["VehicleModel"] = vehicles;
        }

        var modelTypes = (Dictionary<uint, ModelType>?)typesField.GetValue(manager);
        if (modelTypes == null)
        {
            modelTypes = [];
            typesField.SetValue(manager, modelTypes);
        }

        const uint subId = 653_101;
        if (!vehicles.ContainsKey(subId))
        {
            vehicles[subId] = new VehicleModel
            {
                Id = subId,
                Velocity = 5f,
                AngVel = 50f,
                WheeledVehicleMass = 1f,
                WheeledVehicleMaxGear = 0 // VelAccel falls back to 0.3
            };
        }
        if (!modelTypes.ContainsKey(modelId))
        {
            modelTypes[modelId] = new ModelType { Id = modelId, SubId = subId, SubType = "VehicleModel" };
        }
    }
}
