using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 C4 hauler/trader v1 slice-2 (ROADMAP M8 living-village contracts) — drive
/// route through <see cref="HaulerDriveRouteCycle"/>: drive the loaded cargo
/// vehicle (pack already aboard via the slice-1 load path, actor in the driver
/// seat) from A to B through the real DriveVehicle movement model, holding
/// with the spec §17 taxonomy reason when the route is blocked.
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (skipped/mangled drive leg, swallowed timeout, invented hold reason, lost
/// pack on hold, redundant move on the no-op leg, re-executed same-key leg)
/// and PASSES on the composed real path. Raw engine rejections (unknown
/// vehicle, no driver seat, budget expiry) are already pinned by
/// GameplayActorDriveVehicleTests — these tests pin the COMPOSER's leg order,
/// hold semantics, and conservation, not the engine gates.
///
/// Blocked-route representation (honest — no pathfinding to defeat): the
/// movement model is straight-line lerp, so a blocked/unreachable leg is a
/// destination unreachable within the leg budget. The hold reason is the
/// engine's own TimedOut(Navigation) ("navigation budget exceeded"), the
/// DriveVehicle-contract precedent — the composer surfaces it, never invents
/// one.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class HaulerDriveRouteCycleTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    private static uint s_nextWorldId = 0x6200_0000; // fresh base: 0x6000 loadpack / 0x6001 housing-race / 0x6002 harvest / 0x6100 slice-1 / 0x8000 farmer

    private static object? _previousSusManager;
    private static object? _previousModelManager;
    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        GameplayActorTestRig.SeedCargoPackSurface();
        SeedEquipSurface();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousSusManager);
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousModelManager);
        _previousSusManager = null;
        _previousModelManager = null;
        if (_registeredWorld != null)
        {
            var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(WorldManager.Instance)!;
            if (worlds.TryGetValue(_registeredWorld.Id, out var registered) && ReferenceEquals(registered, _registeredWorld))
                worlds.TryRemove(_registeredWorld.Id, out _);
            _registeredWorld = null;
        }
    }

    [Test]
    public async Task Drive_HappyPath_ArrivesWithPackAttached()
    {
        var destination = new Vector3(1100f, 1000f, 100f); // 100 units east
        var (actor, _, slave, packItemId) = CreateRig("m8c4-d1-happy", 0x3250u, TestPosition);

        var result = HaulerDriveRouteCycle.Run(actor,
            Options("m8c4-d1", slave.ObjId, destination, TimeSpan.FromSeconds(30)), new HaulDrivePump());

        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[] { "precheck-clean", "drive-completed", "drive-arrival", "drive-pack-conserved" })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Real movement: the VEHICLE arrived (ArrivalRadius 0.5 per axis).
        var arrival = slave.Transform.World.Position;
        await Assert.That(Math.Abs(arrival.X - destination.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(arrival.Y - destination.Y)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(arrival.Z - destination.Z)).IsLessThan(0.5f);
        await Assert.That(result.DistanceRemaining).IsLessThan(0.5f);

        // Pack: out of the slot, into the System container, still attached
        // on a cargo point — exactly once, never duplicated by the drive.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(packItemId)).IsNotNull();
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId == packItemId)).IsEqualTo(1);
        await Assert.That(result.PacksRetained).IsEqualTo(1);
    }

    [Test]
    public async Task Drive_UnreachableDestination_HoldsWithNavigation_PackRetained()
    {
        var destination = new Vector3(2000f, 1000f, 100f); // 1000 units — unreachable in budget
        var (actor, _, slave, packItemId) = CreateRig("m8c4-d1-blocked", 0x3260u, TestPosition);

        var result = HaulerDriveRouteCycle.Run(actor,
            Options("m8c4-d1-blocked", slave.ObjId, destination, TimeSpan.FromMilliseconds(100)), new HaulDrivePump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("DRIVE");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.Navigation);
        // The engine's own budget wording — the composer surfaced the
        // taxonomy reason, it did not invent one.
        await Assert.That(result.FailReason.Contains("navigation budget")).IsTrue();
        // The vehicle never arrived (held short of the destination).
        await Assert.That(result.DistanceRemaining).IsGreaterThan(GameplayActor.ArrivalRadius);

        // The pack stayed aboard: still attached (exactly once), still owned
        // by the System container, never back in the slot, never deleted.
        var retained = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(retained).IsNull();
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(packItemId)).IsNotNull();
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId == packItemId)).IsEqualTo(1);
        await Assert.That(result.Criteria.Any(c => c.Name == "drive-hold-pack-retained" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task Drive_AlreadyThere_CompletesWithoutMoving()
    {
        var (actor, _, slave, packItemId) = CreateRig("m8c4-d1-there", 0x3270u, TestPosition);
        var before = slave.Transform.World.Position;

        var result = HaulerDriveRouteCycle.Run(actor,
            Options("m8c4-d1-there", slave.ObjId, TestPosition, TimeSpan.FromSeconds(30)), new HaulDrivePump());

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "drive-already-there" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "drive-pack-conserved" && c.Passed)).IsTrue();
        // The no-op leg moved nothing — the engine was never re-entered.
        await Assert.That(slave.Transform.World.Position).IsEqualTo(before);
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId == packItemId)).IsEqualTo(1);
    }

    [Test]
    public async Task Drive_SameKeyRetry_NeverDuplicatesLegs()
    {
        var destination = new Vector3(1100f, 1000f, 100f);
        var (actor, _, slave, packItemId) = CreateRig("m8c4-d1-retry", 0x3280u, TestPosition);

        var first = HaulerDriveRouteCycle.Run(actor,
            Options("m8c4-d1-retry", slave.ObjId, destination, TimeSpan.FromSeconds(30)), new HaulDrivePump());
        await Assert.That(first.Passed).IsTrue();
        var afterFirst = slave.Transform.World.Position;

        var retry = HaulerDriveRouteCycle.Run(actor,
            Options("m8c4-d1-retry", slave.ObjId, destination, TimeSpan.FromSeconds(30)), new HaulDrivePump());

        await Assert.That(retry.Passed).IsFalse();
        await Assert.That(retry.FailStage).IsEqualTo("DRIVE");
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        // The ledger refused the duplicate — a keyless re-drive would have
        // Completed (already-there), not Rejected.
        await Assert.That(retry.FailReason.Contains("duplicate idempotency key")).IsTrue();
        // No second leg executed: the vehicle never moved again, the pack is
        // attached exactly once, the System container still owns it.
        await Assert.That(slave.Transform.World.Position).IsEqualTo(afterFirst);
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId == packItemId)).IsEqualTo(1);
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(packItemId)).IsNotNull();
    }

    // ------------------------------------------------------------ rig below

    /// <summary>
    /// Builds the slice-2 precondition through the REAL paths: cargo vehicle
    /// summoned, pack equipped, actor boarded at the driver seat
    /// (slice-1 BOARD), pack loaded onto the first free cargo point (slice-1
    /// LOAD). Throws when setup itself refuses — a setup refusal is a rig
    /// defect, never a composer verdict.
    /// </summary>
    private (GameplayActor actor, HeadlessSession session, Slave slave, ulong packItemId) CreateRig(string name, uint slaveObjId, Vector3 start)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        SeedMovementSingletons();
        AttachCapture(actor);
        PlaceInWorld(session, actor, start);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, slaveObjId);
        PlaceInWorld(session, slave, start);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var packItemId = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)!.Id;
        var board = actor.BoardVehicle(slaveObjId, AttachPointKind.Driver, idempotencyKey: $"{name}-board");
        if (board.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"rig board failed: {board.State} ({board.Detail})");
        var load = actor.LoadPackOntoVehicle(slaveObjId, idempotencyKey: $"{name}-load");
        if (load.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"rig load failed: {load.State} ({load.Detail})");
        return (actor, session, slave, packItemId);
    }

    private static HaulerDriveRouteCycle.HaulerDriveRouteOptions Options(string cycle, uint slaveObjId, Vector3 destination, TimeSpan timeout)
        => new()
        {
            CycleId = cycle,
            SlaveObjId = slaveObjId,
            Destination = destination,
            Speed = 10f,
            DriveTimeout = timeout,
            PumpBudget = TimeSpan.FromSeconds(30)
        };

    private void RegisterWorld(HeadlessSession session)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(WorldInstance).GetField("<Id>k__BackingField", Flags)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", Flags)!
            .GetValue(WorldManager.Instance)!;
        if (!worlds.TryAdd(session.World.Id, session.World) && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held by a foreign world.");
        _registeredWorld = session.World;
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>
    /// FinalizeTransform runs delta-movement analysis through SusManager
    /// every 5s of accumulated movement, and Character.SetPosition consults
    /// ModelManager when the character is attached to a Slave (deck-height
    /// probe). The headless test process has no DI — seed both singletons
    /// the way GameplayActorDriveVehicleTests does, AFTER the rig's Seed()
    /// has populated WorldManager. Restored in TearDown so sibling suites
    /// never observe the swap.
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

    /// <summary>Test session sink — the same pattern the slave-lifecycle and
    /// housing tests use to capture outbound packets.</summary>
    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null!;
        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null!;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    /// <summary>Attaches a real GameConnection with a capture sink to the actor.</summary>
    private static void AttachCapture(GameplayActor actor)
    {
        var capture = new PacketCaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
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
        // The cargo-slave summon already backfills ParentWorld and the
        // transform instance id (the SummonCargoSlave headless bypass); the
        // guard below keeps the region placement honest for any unit whose
        // world back-reference is missing.
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

    /// <summary>
    /// Null-guards the SkillManager/BuffGameData/ItemGameData dictionaries
    /// the equip/load surface reads (the LoadPackOntoVehicle-tests shape —
    /// missing-only, never replaces populated registries).
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

        var buffGameData = BuffGameData.Instance;
        foreach (var field in typeof(BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        var itemGameData = ItemGameData.Instance;
        if (typeof(ItemGameData).GetField("_itemGradeBuffs", flags)?.GetValue(itemGameData) == null)
            typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!.SetValue(itemGameData, new Dictionary<uint, Dictionary<byte, uint>>());
    }

    /// <summary>
    /// Headless drive pump: ticks the actor in 1s steps until the drive
    /// request reaches a terminal state or the wall-clock budget runs out
    /// (12 × 1s ticks at 10 m/s cover a 100-unit leg with margin).
    /// </summary>
    private sealed class HaulDrivePump : IHaulDrivePump
    {
        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan pollBudget)
        {
            var deadline = Environment.TickCount64 + (long)pollBudget.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
                actor.Tick(TimeSpan.FromSeconds(1));
            return request;
        }
    }
}
