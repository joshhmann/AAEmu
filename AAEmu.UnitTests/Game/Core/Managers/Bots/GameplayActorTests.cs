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
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units.Movements;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 actor contract rig (slice #8) — the SERVER side of the spec §17 split.
///
/// These tests prove the actor executes each action correctly through the
/// REAL engine paths (Transform movement, Unit.CurrentTarget, the
/// Character.UseSkill learned-skill branch), and that the lifecycle
/// (Requested → Accepted → Running → Completed | Rejected | Interrupted |
/// TimedOut) + audit trace behave per contract. They deliberately do NOT
/// judge whether a controller chose the right action — that is the
/// behavior-test track (spec §17 split).
///
/// Every action type is proven for at least one of: accept, reject,
/// interrupt, timeout; the shared lifecycle machinery (busy gate, trace
/// emission) is proven across actions.
/// </summary>
[NotInParallel]
public class GameplayActorTests
{
    #region Observe

    [Test]
    public async Task Observe_ReturnsDirectServerState_AndEmitsAuditRecord()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("observe-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(10, 20, 30));
        actor.Character.Hp = 55;
        actor.Character.Mp = 44;

        var observation = actor.Observe();

        await Assert.That(observation.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(observation.Position).IsEqualTo(new Vector3(10, 20, 30));
        await Assert.That(observation.Hp).IsEqualTo(55);
        await Assert.That(observation.Mp).IsEqualTo(44);
        await Assert.That(observation.CurrentTargetObjId).IsEqualTo(0u);
        await Assert.That(observation.ActiveQuestIds).IsNotNull();

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Observe);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.StartedAtUtc != default).IsTrue();
        await Assert.That(record.CompletedAtUtc != default).IsTrue();
    }

    #endregion

    #region Move

    [Test]
    public async Task Move_ValidDestination_AcceptedRunningCompleted_PositionAdvancesPerTick()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var request = actor.MoveTo(new Vector3(10, 0, 0), speed: 2f);

        // MoveTo accepts synchronously and starts the leg: the returned
        // request is already Running (Requested → Accepted → Running).
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(ReferenceEquals(request, actor.ActiveRequest)).IsTrue();

        // One tick: 2 units at speed 2/s.
        actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - 2f) <= 0.001f).IsTrue();

        // Keep ticking until arrival.
        var guard = 0;
        while (request.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - 10f) <= 0.001f).IsTrue();
        await Assert.That(actor.ActiveRequest).IsNull();

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
    }

    [Test]
    public async Task Move_InvalidSpeed_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-2");

        var request = actor.MoveTo(new Vector3(5, 0, 0), speed: 0f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task Move_Timeout_ExpiresWithNavigationFailure()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-3");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var request = actor.MoveTo(new Vector3(100, 0, 0), speed: 1f, timeout: TimeSpan.FromMilliseconds(100));

        actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Navigation);
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        await Assert.That(actor.AuditTrace[0].Result).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(actor.AuditTrace[0].Failure).IsEqualTo(ActorFailureReason.Navigation);
    }

    [Test]
    public async Task Stop_InterruptsRunningMove_AndCompletesItself()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-4");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        var stop = actor.Stop();

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(stop.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move && r.Result == ActorLifecycleState.Interrupted)).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Stop && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task Move_WhileBusy_RejectedWithStateTransition()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-5");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var first = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        var second = actor.MoveTo(new Vector3(5, 0, 0), speed: 1f);

        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(second.Detail?.Contains("busy")).IsTrue();
        await Assert.That(ReferenceEquals(first, actor.ActiveRequest)).IsTrue();
    }

    [Test]
    public async Task Interrupt_ByTraceId_CancelsRunningRequest_AndIsIdempotent()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-6");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);

        await Assert.That(actor.Interrupt(move.TraceId)).IsTrue();
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        // Second interrupt of the same (now terminal) trace is a no-op.
        await Assert.That(actor.Interrupt(move.TraceId)).IsFalse();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MoveToUnit_UnknownUnit_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-7");

        var request = actor.MoveToUnit(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    #endregion

    #region Move rework — real 1.2 movement path (REQ-M5.3-3/4/7/8)

    // M5.3 Move rework (t_3cac48d4): the v1 silent Transform write
    // (ApplyPosition — bare SetPosition, no broadcast) is replaced by the
    // client-authored unit-movement model — the exact engine path
    // CSMoveUnitPacket's UnitMoveType case executes for the character
    // (VehicleMovementModel.ApplyUnitMove: position apply +
    // SCOneUnitMovementPacket broadcast + transform finalize; the same
    // model family DriveVehicle rides). These tests prove the real path:
    // broadcasts observed on the wire, arrival/completion semantics, halt
    // on Stop, idempotency, and the REQ-M5.3-7 threading-boundary
    // assertion (ExecutionBoundary — trace tests alone do NOT satisfy the
    // requirement).

    // -- capture rig (same pattern as GameplayActorDriveVehicleTests) --

    private static object? _previousSusManager;
    private static object? _previousModelManager;

    /// <summary>
    /// FinalizeTransform runs delta-movement analysis through SusManager
    /// every 5s of accumulated movement. The headless test process has no
    /// DI — seed both singletons the way GameplayActorDriveVehicleTests
    /// does, AFTER CreateActor (see its SeedMovementSingletons).
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
    public void RestoreMovementSingletons()
    {
        if (_previousSusManager != null)
            typeof(Singleton<SusManager>)
                .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, _previousSusManager);
        if (_previousModelManager != null)
            typeof(Singleton<ModelManager>)
                .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, _previousModelManager);
        _previousSusManager = null;
        _previousModelManager = null;
    }

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

    /// <summary>Attaches a real GameConnection with a capture sink to the actor.</summary>
    private static PacketCaptureSession AttachCapture(GameplayActor actor)
    {
        var capture = new PacketCaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        return capture;
    }

    /// <summary>Places the actor in the world AND its region grid so its own
    /// movement broadcast reaches it (BroadcastPacket self=true → capture).</summary>
    private static void PlaceInWorld(HeadlessSession session, GameplayActor actor, Vector3 position)
    {
        GameplayActorTestRig.SetPosition(actor, position);
        WorldManager.Instance.AddVisibleObject(actor.Character);
    }

    private static bool CapturedOpcode(PacketCaptureSession capture, ushort opcode)
    {
        return CapturedOpcodeCount(capture, opcode) > 0;
    }

    /// <summary>Counts how many captured packets carry the given opcode.</summary>
    private static int CapturedOpcodeCount(PacketCaptureSession capture, ushort opcode)
    {
        var count = 0;
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
                    count++;
            }
            catch
            {
                // malformed capture — skip
            }
        }

        return count;
    }

    [Test]
    public async Task Move_RealMovementPath_AdvancesPositionWithBroadcast_AndArrives()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m53-move-1");
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, new Vector3(512, 512, 0));

        var destination = new Vector3(612, 512, 0); // 100 units east
        var request = actor.MoveTo(destination, speed: 10f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(ReferenceEquals(request, actor.ActiveRequest)).IsTrue();

        // 12 × 1s ticks at 10 m/s covers the 100-unit leg with margin.
        for (var i = 0; i < 12; i++)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("arrived");
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - destination.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.Y - destination.Y)).IsLessThan(0.5f);

        // REAL broadcast: SCOneUnitMovementPacket was emitted through the
        // client-authored model (the CSMoveUnitPacket UnitMoveType path) —
        // the v1 silent Transform write is gone.
        await Assert.That(CapturedOpcode(capture, SCOffsets.SCOneUnitMovementPacket)).IsTrue();

        // TRACE: full lifecycle audit record correlated to the request.
        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
    }

    [Test]
    public async Task MoveToUnit_RealPath_ArrivesAtUnitPosition_WithBroadcast()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m53-move-2");
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, new Vector3(512, 512, 0));

        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);
        var npc = session.World.GetUnit(npcObjId)!;
        npc.Transform.Local.SetPosition(new Vector3(612, 512, 0));

        var request = actor.MoveToUnit(npcObjId, speed: 10f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);

        for (var i = 0; i < 12; i++)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("arrived");
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - npc.Transform.World.Position.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.Y - npc.Transform.World.Position.Y)).IsLessThan(0.5f);
        await Assert.That(CapturedOpcode(capture, SCOffsets.SCOneUnitMovementPacket)).IsTrue();
    }

    [Test]
    public async Task Move_AlreadyAtDestination_CompletesImmediately_NoBroadcast()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m53-move-3");
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, new Vector3(512, 512, 0));

        var request = actor.MoveTo(new Vector3(512, 512, 0), speed: 5f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("already at destination");
        await Assert.That(actor.ActiveRequest).IsNull();
        // No leg was walked — the movement path never ran — so no broadcast.
        await Assert.That(CapturedOpcode(capture, SCOffsets.SCOneUnitMovementPacket)).IsFalse();
    }

    [Test]
    public async Task Move_NonFiniteDestination_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-move-4");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var request = actor.MoveTo(new Vector3(float.NaN, 0, 0), speed: 1f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task Move_SameIdempotencyKeyRetry_RejectedPreFlight_NoReExecution()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-move-5");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var first = actor.MoveTo(new Vector3(10, 0, 0), speed: 1f, idempotencyKey: "m53-move-key");
        for (var i = 0; i < 30 && first.State is not ActorLifecycleState.Completed; i++)
            actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        var retry = actor.MoveTo(new Vector3(10, 0, 0), speed: 1f, idempotencyKey: "m53-move-key");

        // Pre-flight dedupe: Rejected(StateTransition) with NO Running
        // transition — the original outcome under the key is preserved.
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == retry.TraceId)).IsEqualTo(1);
        await Assert.That(actor.FindByKey("m53-move-key")!.TraceId).IsEqualTo(first.TraceId);
    }

    [Test]
    public async Task Stop_RunningMove_HaltsMovement_SecondStopIsNoOp()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m53-move-6");
        SeedMovementSingletons();
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, new Vector3(512, 512, 0));

        var move = actor.MoveTo(new Vector3(612, 512, 0), speed: 5f);
        actor.Tick(TimeSpan.FromSeconds(1));
        var broadcastsDuringMove = capture.CapturedPackets.Count;
        var positionAtStop = actor.Character.Transform.World.Position;

        var stop = actor.Stop();

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(stop.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.ActiveRequest).IsNull();

        // HALT: Stop emits exactly ONE more movement packet — the canonical
        // Stopping broadcast (zero velocity, dossier §1.6) — and then the
        // interrupted move stops advancing: position frozen, no further
        // broadcasts across subsequent ticks.
        await Assert.That(capture.CapturedPackets.Count).IsEqualTo(broadcastsDuringMove + 1);
        for (var i = 0; i < 5; i++)
            actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(positionAtStop);
        await Assert.That(capture.CapturedPackets.Count).IsEqualTo(broadcastsDuringMove + 1);

        // Second Stop is a no-op (idempotent) and still completes itself.
        var stop2 = actor.Stop();
        await Assert.That(stop2.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(3); // move + stop + second stop
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Stop && r.Result == ActorLifecycleState.Completed)).IsEqualTo(2);
    }

    [Test]
    public async Task BuildStopMove_ZeroVelocity_StoppingFlag_AtHaltPosition()
    {
        var stop = VehicleMovementModel.BuildStopMove(new Vector3(1, 2, 3), 42);

        await Assert.That(stop.X).IsEqualTo(1f);
        await Assert.That(stop.Y).IsEqualTo(2f);
        await Assert.That(stop.Z).IsEqualTo(3f);
        await Assert.That(stop.VelX).IsEqualTo((short)0);
        await Assert.That(stop.VelY).IsEqualTo((short)0);
        await Assert.That(stop.VelZ).IsEqualTo((short)0);
        await Assert.That(stop.Flags).IsEqualTo(MoveTypeFlags.Stopping);
        await Assert.That(stop.RotationZ).IsEqualTo((sbyte)42);
    }

    [Test]
    public async Task BuildCharacterMove_WalkPayload_VelocityFacingAndWalkFlags()
    {
        // Heading east (yaw 0): velocity must be detectable (dossier §1.7 —
        // non-zero velocity triggers BuffRemoveOn.Move) and carry the walk
        // gait (ActorFlags 5) + stance/alertness a client would send.
        var move = VehicleMovementModel.BuildCharacterMove(new Vector3(10, 0, 0), 0f, 2f);

        await Assert.That(move.X).IsEqualTo(10f);
        await Assert.That(move.Y).IsEqualTo(0f);
        await Assert.That(move.Z).IsEqualTo(0f);
        await Assert.That(move.VelX > 0).IsTrue(); // speed × 2048, east
        await Assert.That(move.ActorFlags).IsEqualTo((byte)5); // 5-walk
        await Assert.That(move.Stance).IsEqualTo(GameStanceType.Relaxed);
        await Assert.That(move.Alertness).IsEqualTo(MoveTypeAlertness.Idle);
    }

    [Test]
    public async Task ExecutionBoundary_MoveAndStop_OnBoundaryThread_NoViolations()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        var before = ExecutionBoundary.ViolationCount;
        try
        {
            var (actor, _) = GameplayActorTestRig.CreateActor("m53-boundary-1");
            GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

            actor.MoveTo(new Vector3(10, 0, 0), speed: 1f);
            actor.MoveToUnit(0);
            actor.Stop();

            await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(before);
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
    }

    [Test]
    public async Task ExecutionBoundary_MoveOffBoundaryThread_FiresViolation()
    {
        // Pin to an impossible thread id — any real thread is off-boundary:
        // the action-level assertion must fire (trace tests alone do NOT
        // satisfy REQ-M5.3-7).
        ExecutionBoundary.SetExecutionThreadForTest(int.MaxValue);
        var before = ExecutionBoundary.ViolationCount;
        try
        {
            var (actor, _) = GameplayActorTestRig.CreateActor("m53-boundary-2");
            GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

            actor.MoveTo(new Vector3(10, 0, 0), speed: 1f);
            actor.Stop();

            await Assert.That(ExecutionBoundary.ViolationCount).IsGreaterThan(before);
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
    }

    [Test]
    public async Task ExecutionBoundary_ObserveSetTargetCast_OnBoundaryThread_NoViolations()
    {
        // REQ-M5.3-7/E7: the assertion must hold for EVERY M5.3 action — not
        // only Move/Stop. Observe (world reads), SetTarget and Cast (both
        // mutate Character/world) all ride the A1 seam.
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        var before = ExecutionBoundary.ViolationCount;
        try
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("m53-boundary-3");
            var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);

            actor.Observe();
            actor.SetTarget(npcObjId);
            actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

            await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(before);
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
    }

    [Test]
    public async Task ExecutionBoundary_ObserveSetTargetCast_OffBoundaryThread_FiresViolation()
    {
        // Mirrors the Move negative test: pinned to an impossible thread,
        // every action-level assertion fires — SetTarget and Cast mutate
        // Character/world off the boundary, Observe reads world state that
        // is only consistent on the seam.
        ExecutionBoundary.SetExecutionThreadForTest(int.MaxValue);
        var before = ExecutionBoundary.ViolationCount;
        try
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("m53-boundary-4");
            var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);

            actor.Observe();
            actor.SetTarget(npcObjId);
            actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

            await Assert.That(ExecutionBoundary.ViolationCount).IsGreaterThan(before);
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
    }

    #endregion

    #region Target

    [Test]
    public async Task SetTarget_ValidUnit_CompletesAndSetsCurrentTarget()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("target-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);

        var request = actor.SetTarget(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.CurrentTarget).IsNotNull();
        await Assert.That(actor.Character.CurrentTarget!.ObjId).IsEqualTo(npcObjId);
        // Target emits exactly one audit record before the Observe query.
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        await Assert.That(actor.AuditTrace[0].Action).IsEqualTo(ActorActionType.Target);
        await Assert.That(actor.AuditTrace[0].TargetId).IsEqualTo(npcObjId);
        // Observe is itself a tracked query — it appends a second record.
        await Assert.That(actor.Observe().CurrentTargetObjId).IsEqualTo(npcObjId);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Observe);
    }

    [Test]
    public async Task SetTarget_UnknownUnit_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("target-2");

        var request = actor.SetTarget(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task SetTarget_ValidUnit_BroadcastsExactlyOneTargetChangedPacket()
    {
        // REQ-M5.3-5: the engine's resolve -> assign -> broadcast order must
        // be observable — a successful SetTarget emits exactly ONE
        // SCTargetChangedPacket so client observers see the bot's target
        // change (same capture rig as the Move broadcast tests).
        var (actor, session) = GameplayActorTestRig.CreateActor("target-3");
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, new Vector3(512, 512, 0));
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);

        var request = actor.SetTarget(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.CurrentTarget!.ObjId).IsEqualTo(npcObjId);
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCTargetChangedPacket)).IsEqualTo(1);
    }

    [Test]
    public async Task SetTarget_RejectedPath_EmitsNoTargetChangedBroadcast()
    {
        // REQ-M5.3-5: a rejected request must not mutate Character state and
        // must not emit the broadcast (the engine path only broadcasts after
        // a successful assignment).
        var (actor, session) = GameplayActorTestRig.CreateActor("target-4");
        var capture = AttachCapture(actor);
        PlaceInWorld(session, actor, new Vector3(512, 512, 0));

        var request = actor.SetTarget(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCTargetChangedPacket)).IsEqualTo(0);
        await Assert.That(capture.CapturedPackets.Count).IsEqualTo(0);
    }

    #endregion

    #region Cast

    [Test]
    public async Task Cast_LearnedSkill_CompletesThroughRealEnginePath()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-1");
        // Self-cast: the character is registered in its world, so the engine
        // resolves the caster as its own target (TargetType.Self).
        var request = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(SkillResult.Success);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Cast);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        // Audit TargetId = the cast TARGET objId (self here), not the skill id.
        await Assert.That(record.TargetId).IsEqualTo(actor.ActorId);
    }

    [Test]
    public async Task Cast_UnknownSkill_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-2");

        var request = actor.Cast(123_456, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("unknown skill")).IsTrue();
    }

    [Test]
    public async Task Cast_NotLearned_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-3");
        // Forget the seeded skill (fresh rig without AddSkill).
        actor.Character.Skills.Skills.Clear();

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not learned")).IsTrue();
    }

    [Test]
    public async Task Cast_UnknownTarget_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-4");

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, 999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("cast target not found")).IsTrue();
    }

    #endregion

    #region Audit + lifecycle machinery

    [Test]
    public async Task AuditRecord_CarriesFullTraceShape()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("audit-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1001);

        actor.SetTarget(npcObjId);
        actor.Stop();

        var record = actor.AuditTrace[0];
        // {trace_id, actor_id, action, target_id, requested_at, started_at,
        //  completed_at, result, state_changes}
        await Assert.That(record.TraceId).IsNotEqualTo(Guid.Empty);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Target);
        await Assert.That(record.TargetId).IsEqualTo(npcObjId);
        await Assert.That(record.RequestedAtUtc <= record.StartedAtUtc).IsTrue();
        await Assert.That(record.StartedAtUtc <= record.CompletedAtUtc).IsTrue();
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Count >= 3).IsTrue(); // Requested→Accepted→Running→Completed
    }

    [Test]
    public async Task Trace_IsBounded_NewestLast()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("audit-2");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1002);

        for (var i = 0; i < 20; i++)
        {
            actor.SetTarget(npcObjId);
            actor.Stop();
        }

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(40); // 20 targets + 20 stops
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Stop);
        await Assert.That(actor.AuditTrace[^2].Action).IsEqualTo(ActorActionType.Target);
    }

    #endregion
}
