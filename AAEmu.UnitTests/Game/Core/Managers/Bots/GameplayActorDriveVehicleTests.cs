using System.Net;
using System.Net.Sockets;
using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Utils;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// DriveVehicle contract tests (t_eaf1754d — Phase 2 prerequisite).
///
/// The contract: the actor drives a boarded vehicle (Slave ground vehicle or
/// Mate mount) to an absolute position through the CLIENT-AUTHORED vehicle
/// movement model — the exact engine path a client driver's CSMoveUnitPacket
/// executes (VehicleMovementModel.ApplySlaveMove / ApplyUnitMove: position
/// apply + SCOneUnitMovementPacket broadcast + FinalizeTransform). The actor
/// never assigns a vehicle Transform directly.
///
/// Real-movement evidence: vehicle World position advances across ticks, the
/// movement broadcast is captured on the wire, the driver stays attached.
/// Trace evidence: full lifecycle audit record with trace id, §17 failure
/// reasons, idempotency-key dedupe.
/// </summary>
[NotInParallel]
public class GameplayActorDriveVehicleTests
{
    private static object _previousSusManager;
    private static object _previousModelManager;

    /// <summary>
    /// FinalizeTransform runs delta-movement analysis through SusManager
    /// every 5s of accumulated movement, and Character.SetPosition consults
    /// ModelManager when the character is attached to a Slave (deck-height
    /// probe). The headless test process has no DI — seed both singletons
    /// the way SlaveLifecycleTests / NpcLineOfSightTests do, AFTER the rig's
    /// Seed() has populated WorldManager (the hook cannot run before
    /// CreateActor, hence per-test seeding).
    /// </summary>
    private static void SeedMovementSingletons()
    {
        _previousSusManager = typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new SusManager(WorldManager.Instance));

        _previousModelManager = typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        var modelManager = new ModelManager();
        // _modelTypes/_models are only initialized by Load() (game data);
        // an empty seed makes GetShipModel return null like an unloaded
        // manager, which is all the deck-height probe needs.
        typeof(ModelManager)
            .GetField("_modelTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<uint, ModelType>());
        typeof(ModelManager)
            .GetField("_models", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>>());
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, modelManager);
    }

    [After(Test)]
    public void RestoreSingletons()
    {
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousSusManager);
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousModelManager);
    }
    /// <summary>Test session sink — the same pattern the slave-lifecycle and
    /// housing tests use to capture outbound packets.</summary>
    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    // ------------------------------------------------------------------ rig helpers

    /// <summary>Attaches a real GameConnection with a capture sink to the actor.</summary>
    private static PacketCaptureSession AttachCapture(GameplayActor actor)
    {
        var capture = new PacketCaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        return capture;
    }

    /// <summary>Places the actor in the world AND its region grid so the
    /// vehicle's movement broadcast reaches it (BroadcastPacket →
    /// WorldManager.GetAround → region neighbors).</summary>
    private static void PlaceInWorld(HeadlessSession session, GameplayActor actor, Vector3 position)
    {
        GameplayActorTestRig.SetPosition(actor, position);
        WorldManager.Instance.AddVisibleObject(actor.Character);
    }

    private static void PlaceInWorld(HeadlessSession session, BaseUnit unit, Vector3 position)
    {
        // SummonMate's mate carries no ParentWorld (the mount pipeline
        // resolves it via the MateManager registry) — the region placement
        // needs the world back-reference. The public ParentWorld setter NREs
        // for headless worlds (it pokes Transform.InstanceId), so use the
        // same backing-field pattern as HeadlessSession.SetParentWorld, and
        // pre-pin the transform instance id so Region.AddObject's
        // InstanceId assignment no-ops (no registry lookup, no NRE).
        if (unit.ParentWorld == null)
            typeof(AAEmu.Game.Models.Game.World.GameObject)
                .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(unit, session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(unit.Transform, session.World.Id);
        unit.Transform.Local.SetPosition(position);
        WorldManager.Instance.AddVisibleObject(unit);
    }

    /// <summary>True when the captured stream contains the given G2C opcode
    /// (same parse as SlaveLifecycleTests.CapturedOpcode).</summary>
    private static bool CapturedOpcode(PacketCaptureSession capture, ushort opcode)
    {
        foreach (var bytes in capture.CapturedPackets)
        {
            try
            {
                var stream = new PacketStream();
                stream.Write(bytes);
                stream.ReadUInt16(); // length prefix
                stream.ReadByte();   // 0xdd
                stream.ReadByte();   // level (1)
                stream.ReadByte();   // hash (0)
                stream.ReadByte();   // count (0)
                if (stream.ReadUInt16() == opcode) // TypeId
                    return true;
            }
            catch
            {
                // malformed capture — skip
            }
        }

        return false;
    }

    /// <summary>Drives a mounted Mate to a destination through the actor
    /// contract and ticks it to completion.</summary>
    private static async Task<(GameplayActor Actor, HeadlessSession Session, Mate Mate, PacketCaptureSession Capture)> SetupMateDrive(
        string name, Vector3 start, Vector3 destination, float speed = 10f, uint? mateObjId = null)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, start);

        var mid = mateObjId ?? GameplayActorTestRig.MateObjId;
        var mateObjId2 = GameplayActorTestRig.SummonMate(session, actor, mid);
        var mate = session.World.MateManager.GetActiveMateByMateObjId(mateObjId2)!;
        PlaceInWorld(session, mate, start);

        var mount = actor.Mount(mateObjId2);
        await Assert.That(mount.State).IsEqualTo(ActorLifecycleState.Completed);
        return (actor, session, mate, capture);
    }

    // ------------------------------------------------------------------ real movement

    [Test]
    public async Task Drive_Mate_MovesThroughMovementModel_WithBroadcastAndTrace()
    {
        var start = new Vector3(512, 512, 0);
        var destination = new Vector3(612, 512, 0); // 100 units east
        var (actor, _, mate, capture) = await SetupMateDrive("drive-mate-1", start, destination);

        var drive = actor.DriveVehicle(mate.ObjId, destination, speed: 10f);
        await Assert.That(drive.State).IsEqualTo(ActorLifecycleState.Running);

        // 12 × 1s ticks at 10 m/s covers the 100-unit leg with margin.
        for (var i = 0; i < 12; i++)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(drive.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(drive.Detail).IsEqualTo("arrived");

        // REAL movement: the VEHICLE moved (the rider follows via the
        // parent transform — no rider teleport).
        await Assert.That(Math.Abs(mate.Transform.World.Position.X - destination.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(mate.Transform.World.Position.Y - destination.Y)).IsLessThan(0.5f);
        await Assert.That(actor.Character.Transform.Parent?.GameObject).IsEqualTo(mate);

        // REAL broadcast: SCOneUnitMovementPacket was emitted through the
        // client-authored model (the CSMoveUnitPacket engine path).
        await Assert.That(CapturedOpcode(capture, SCOffsets.SCOneUnitMovementPacket)).IsTrue();

        // TRACE: full lifecycle audit record correlated to the request
        // (newest last — the drive is the newest entry after the mount).
        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Drive);
        await Assert.That(record.TargetId).IsEqualTo(mate.ObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.TraceId).IsEqualTo(drive.TraceId);
    }

    [Test]
    public async Task Drive_Slave_MovesThroughMovementModel_WithBroadcastAndDriverAttached()
    {
        var start = new Vector3(512, 512, 0);
        var destination = new Vector3(612, 512, 0);
        var (actor, session) = GameplayActorTestRig.CreateActor("drive-slave-1");
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, start);

        var slaveObjId = GameplayActorTestRig.SummonSlave(session, actor, position: start);
        var slave = session.World.GetBaseUnit(slaveObjId) as Slave;
        await Assert.That(slave).IsNotNull();
        PlaceInWorld(session, slave!, start);

        // Boarding through the REAL engine path (the CSBindSlavePacket call).
        GameplayActorTestRig.BindSlaveDriver(session, actor, slaveObjId);
        await Assert.That(slave!.AttachedCharacters.TryGetValue(AttachPointKind.Driver, out var seated)
            && seated == actor.Character).IsTrue();

        var drive = actor.DriveVehicle(slaveObjId, destination, speed: 10f);
        await Assert.That(drive.State).IsEqualTo(ActorLifecycleState.Running);

        for (var i = 0; i < 12; i++)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(drive.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(Math.Abs(slave.Transform.World.Position.X - destination.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(slave.Transform.World.Position.Y - destination.Y)).IsLessThan(0.5f);

        // The movement model keeps the driver parented to the vehicle.
        await Assert.That(actor.Character.Transform.Parent?.GameObject).IsEqualTo(slave);
        await Assert.That(CapturedOpcode(capture, SCOffsets.SCOneUnitMovementPacket)).IsTrue();
    }

    [Test]
    public async Task Drive_MovementModel_AppliesPositionAndBroadcasts()
    {
        // Model-level proof: the SAME method the CSMoveUnitPacket handler
        // executes applies the position and broadcasts — the client-authored
        // path the actor drives through.
        var start = new Vector3(512, 512, 0);
        var (actor, session) = GameplayActorTestRig.CreateActor("drive-model-1");
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, start);

        var slaveObjId = GameplayActorTestRig.SummonSlave(session, actor, position: start);
        var slave = session.World.GetBaseUnit(slaveObjId) as Slave;
        PlaceInWorld(session, slave!, start);

        var vmt = VehicleMovementModel.BuildVehicleMove(new Vector3(612, 512, 0), 90f.DegToRad(), 10f);
        VehicleMovementModel.ApplySlaveMove(actor.Character, slave!, vmt);

        await Assert.That(Math.Abs(slave!.Transform.World.Position.X - 612f)).IsLessThan(0.01f);
        await Assert.That(CapturedOpcode(capture, SCOffsets.SCOneUnitMovementPacket)).IsTrue();

        // Rotation: the yaw short lives in RotationZ as a canonical
        // Z-axis quaternion component (sin(yaw/2)/α) — the handler decodes
        // it back to (roll=0, pitch=0, yaw) radians, so the transform faces
        // the travel direction on the same Z axis the walking model uses.
        var yawShort = vmt.RotationZ;
        await Assert.That(Math.Abs(yawShort * 0.00003052f - MathF.Sin(45f * MathF.PI / 180f))).IsLessThan(0.01f);
        await Assert.That(vmt.RotationX).IsEqualTo((short)0);
        await Assert.That(vmt.RotationY).IsEqualTo((short)0);
        var (_, _, yaw) = MathUtil.GetSlaveRotationInDegrees(vmt.RotationX, vmt.RotationY, vmt.RotationZ);
        await Assert.That(Math.Abs(yaw - 90f.DegToRad())).IsLessThan(0.01f);
    }

    // ------------------------------------------------------------------ rejection taxonomy

    [Test]
    public async Task Drive_NotInDriverSeat_Rejected_StateUnchanged()
    {
        var start = new Vector3(512, 512, 0);
        var destination = new Vector3(612, 512, 0);
        var (actor, session) = GameplayActorTestRig.CreateActor("drive-nodriver-1");
        SeedMovementSingletons();
        var slaveObjId = GameplayActorTestRig.SummonSlave(session, actor, position: start);
        var slave = session.World.GetBaseUnit(slaveObjId) as Slave;
        PlaceInWorld(session, slave!, start);

        var before = slave!.Transform.World.Position;
        var drive = actor.DriveVehicle(slaveObjId, destination, speed: 10f);

        await Assert.That(drive.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(drive.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(drive.Detail?.Contains("driver seat")).IsTrue();

        // No movement happened — the engine was never re-entered.
        actor.Tick(TimeSpan.FromSeconds(2));
        await Assert.That(slave.Transform.World.Position).IsEqualTo(before);
    }

    [Test]
    public async Task Drive_UnknownVehicle_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("drive-unknown-1");
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, new Vector3(512, 512, 0));

        var drive = actor.DriveVehicle(999_999, new Vector3(612, 512, 0), speed: 10f);
        await Assert.That(drive.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(drive.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(drive.Detail?.Contains("not found in world")).IsTrue();
    }

    [Test]
    public async Task Drive_Timeout_TimedOutNavigation()
    {
        var start = new Vector3(512, 512, 0);
        var destination = new Vector3(1512, 512, 0); // 1000 units — unreachable in budget
        var (actor, _, mate, _) = await SetupMateDrive("drive-timeout-1", start, destination);

        var drive = actor.DriveVehicle(mate.ObjId, destination, speed: 10f, timeout: TimeSpan.FromMilliseconds(100));
        actor.Tick(TimeSpan.FromSeconds(1)); // elapsed > budget

        await Assert.That(drive.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(drive.Failure).IsEqualTo(ActorFailureReason.Navigation);
        await Assert.That(drive.Detail?.Contains("navigation budget")).IsTrue();

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(record.Failure).IsEqualTo(ActorFailureReason.Navigation);
    }

    // ------------------------------------------------------------------ idempotency

    [Test]
    public async Task Drive_IdempotencyKey_RetryDeduped()
    {
        var start = new Vector3(512, 512, 0);
        var destination = new Vector3(612, 512, 0);
        var (actor, _, mate, _) = await SetupMateDrive("drive-idem-1", start, destination);

        var first = actor.DriveVehicle(mate.ObjId, destination, speed: 10f, idempotencyKey: "drive:mate:1");
        for (var i = 0; i < 12; i++)
            actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Same-key retry after a Completed outcome is refused pre-flight —
        // the drive can never be re-executed.
        var second = actor.DriveVehicle(mate.ObjId, destination, speed: 10f, idempotencyKey: "drive:mate:1");
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.IsDedupeRejection).IsTrue();
        await Assert.That(second.Detail?.Contains("duplicate idempotency key")).IsTrue();

        // A genuinely NEW logical drive (fresh key) is still allowed.
        var fresh = actor.DriveVehicle(mate.ObjId, new Vector3(712, 512, 0), speed: 10f, idempotencyKey: "drive:mate:2");
        await Assert.That(fresh.State).IsEqualTo(ActorLifecycleState.Running);
    }
}
