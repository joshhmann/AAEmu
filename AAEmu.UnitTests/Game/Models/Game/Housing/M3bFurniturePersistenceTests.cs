using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Housing;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

/// <summary>
/// M3b-1 (t_fb3e5f8c): furniture + bound doodad persistence — rotation/attachment
/// integrity and the save-trigger contract that makes the load path restart-safe.
///
/// Audit finding this suite pins (2026-08-11): SpawnPersistentDoodads used to arm
/// <c>Doodad.IsPersistent = true</c> BEFORE restoring the row fields. The
/// <c>FuncGroupId</c> setter fires <c>Save()</c> whenever IsPersistent is true and the
/// value differs from the current group — so any doodad whose stored phase ≠ template
/// start group (open door/window, toggled furniture, grown crop) triggered a REPLACE
/// with the zeroed pre-restore state (position 0,0,0, owner 0, house 0, attach 0) on
/// EVERY boot. The row was permanently clobbered; on the next restart the doodad
/// loaded at the world origin, ownerless and detached.
///
/// Tests here are hermetic (no MySQL): they pin
///   1. the save-trigger contract — FuncGroupId/Data setters call Save() ONLY when
///      IsPersistent is true (the property the load path relies on), and
///   2. the transform round-trip — placement (quaternion → local rotation, house
///      parent) serializes to the same payload the load path restores, so world
///      position/rotation/attachment survive save → load.
/// The restart-safe load-path ordering itself is proven by the E2E restart test.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class M3bFurniturePersistenceTests
{
    private const uint FurnitureTemplateId = 5002; // seeded plain doodad template (furniture shell)
    private const uint MultiPhaseTemplateId = 5003; // seeded template with start group + an alternate phase
    private const uint TestInstanceId = 7;

    private const uint StartGroupId = 1;
    private const uint AltPhaseId = 2; // ≠ start group → the dangerous save-trigger case

    private object _previousDoodadManager;
    private object _previousWorldManager;
    private WorldInstance _world;

    [Before(Test)]
    public void SetUp()
    {
        _previousDoodadManager = GetSingletonInstance<DoodadManager>();
        _previousWorldManager = GetSingletonInstance<WorldManager>();

        SeedWorldManager();
        SeedDoodadManager();

        _world = new WorldInstance(new WorldTemplate { Id = TestInstanceId, Name = "test_world" }, 0, false, TestInstanceId);
        _world.Regions = new Region[16, 16];
        RegisterWorld(_world);

        // Hermetic guard: any accidental Save() must fail FAST (connection refused),
        // never touch a real database.
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        SetSingletonInstance(typeof(Singleton<DoodadManager>), _previousDoodadManager);
        SetSingletonInstance(typeof(Singleton<WorldManager>), _previousWorldManager);
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
    }

    // ================================================================ tests

    /// <summary>
    /// The save-trigger contract: assigning a DIFFERENT phase while NOT persistent
    /// must be a pure in-memory change (no DB write attempted). This is the property
    /// the load path relies on while restoring fields before arming persistence.
    /// </summary>
    [Test]
    public async Task FuncGroupIdSetter_NotPersistent_DoesNotAttemptDbWrite()
    {
        var doodad = DoodadManager.Instance.Create(_world, 0, MultiPhaseTemplateId, null, true);
        await Assert.That(doodad).IsNotNull();
        await Assert.That(doodad.FuncGroupId).IsEqualTo(StartGroupId);

        // MySQL is pointed at a dead port: if the setter tried to Save() it would throw.
        doodad.FuncGroupId = AltPhaseId;

        await Assert.That(doodad.FuncGroupId).IsEqualTo(AltPhaseId);
        await Assert.That(doodad.IsPersistent).IsFalse();
    }

    /// <summary>
    /// The DANGEROUS pattern (the pre-fix load-path order): arming IsPersistent BEFORE
    /// restoring the phase makes the FuncGroupId setter attempt a DB write with the
    /// current (zeroed) state. With MySQL on a dead port this manifests as an exception —
    /// the exact clobber the M3b-1 fix removes by arming persistence last.
    /// </summary>
    [Test]
    public async Task FuncGroupIdSetter_PersistentBeforeRestore_AttemptsDbWrite()
    {
        var doodad = DoodadManager.Instance.Create(_world, 0, MultiPhaseTemplateId, null, true);
        doodad.IsPersistent = true; // old load-path order: armed before restore

        // DbId=0 → Save() would allocate via DoodadIdManager, then hit the dead-port MySQL.
        await Assert.That(() => doodad.FuncGroupId = AltPhaseId).Throws<Exception>();
    }

    /// <summary>
    /// Rotation/attachment round-trip: furniture placed via the DecorateHouse recipe
    /// (house parent + local position + quaternion rotation) yields a world transform;
    /// the load path restores LOCAL position/rotation on a freshly created doodad
    /// re-parented to the same house. Both must agree on the world transform.
    /// </summary>
    [Test]
    public async Task FurniturePlacement_RoundTripsThroughLoadRestore_WorldTransformPreserved()
    {
        // ---- placement side (DecorateHouse recipe, HousingManager.cs:1557-1587) ----
        var house = MakeHouse(77, new Vector3(1000f, 2000f, 50f), yawRadians: 0.5f);
        var placed = DoodadManager.Instance.Create(_world, 0, FurnitureTemplateId, house, true);
        placed.Transform.Parent = house.Transform;
        placed.Transform.Local.SetPosition(3f, -2f, 1f);
        // 90° yaw (radians) — a rotation that must survive the round trip
        placed.Transform.Local.ApplyFromQuaternion(Quaternion.CreateFromYawPitchRoll(0f, 0f, MathF.PI / 2f));
        placed.OwnerDbId = house.Id;
        placed.AttachPoint = AttachPointKind.None;

        var placedWorldPos = placed.Transform.World.Position;
        var placedWorldRot = placed.Transform.World.ToQuaternion();

        // Capture the exact payload Doodad.Save() would write (Doodad.cs:1019-1024):
        // LOCAL position + LOCAL rotation (radians).
        var savedX = placed.Transform.Local.Position.X;
        var savedY = placed.Transform.Local.Position.Y;
        var savedZ = placed.Transform.Local.Position.Z;
        var savedRoll = placed.Transform.Local.Rotation.X;
        var savedPitch = placed.Transform.Local.Rotation.Y;
        var savedYaw = placed.Transform.Local.Rotation.Z;

        // ---- load side (SpawnPersistentDoodads recipe, SpawnManager.cs:729-806) ----
        var loaded = DoodadManager.Instance.Create(_world, 0, FurnitureTemplateId, null, true);
        loaded.IsPersistent = true; // armed ONLY after the full restore (the fix)
        loaded.DbId = 4242;
        loaded.FuncGroupId = placed.FuncGroupId;
        loaded.OwnerId = placed.OwnerId;
        loaded.OwnerType = placed.OwnerType;
        loaded.AttachPoint = placed.AttachPoint;
        loaded.OwnerDbId = placed.OwnerDbId;
        loaded.Transform.Parent = house.Transform;
        loaded.Transform.Local.SetPosition(savedX, savedY, savedZ);
        loaded.Transform.Local.SetRotation(savedRoll, savedPitch, savedYaw);

        // ---- integrity: world transform + attachment survive save → load ----
        await Assert.That(loaded.Transform.World.Position.X).IsEqualTo(placedWorldPos.X).Within(0.001f);
        await Assert.That(loaded.Transform.World.Position.Y).IsEqualTo(placedWorldPos.Y).Within(0.001f);
        await Assert.That(loaded.Transform.World.Position.Z).IsEqualTo(placedWorldPos.Z).Within(0.001f);

        var loadedRot = loaded.Transform.World.ToQuaternion();
        await Assert.That(MathF.Abs(Quaternion.Dot(placedWorldRot, loadedRot))).IsGreaterThan(0.999f);

        await Assert.That(loaded.OwnerDbId).IsEqualTo(house.Id);
        await Assert.That(loaded.AttachPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(loaded.Transform.Parent?.GameObject).IsSameReferenceAs(house);
    }

    /// <summary>
    /// A bound doodad (house template binding, e.g. door/window) keeps its attach point
    /// and template through the save → load round trip; the house owns it afterwards.
    /// </summary>
    [Test]
    public async Task BoundDoodad_RoundTrips_AttachPointAndTemplatePreserved()
    {
        var house = MakeHouse(78, new Vector3(500f, 500f, 20f), yawRadians: 0f);

        // Placement side: bound doodad created with an attach point (House.CurrentStep recipe)
        var placed = DoodadManager.Instance.Create(_world, 0, FurnitureTemplateId, house, true);
        placed.AttachPoint = AttachPointKind.HealPoint0; // 36 — e.g. a door attach
        placed.ParentObj = house;
        placed.Transform = house.Transform.CloneDetached(placed);
        placed.Transform.Parent = house.Transform;
        placed.Transform.Local.ApplyWorldSpawnPositionWithDeg(new WorldSpawnPosition
        {
            X = 1f, Y = 0f, Z = 2f,
            Roll = 0f, Pitch = 0f, Yaw = 45f
        });

        // Save payload
        var savedX = placed.Transform.Local.Position.X;
        var savedY = placed.Transform.Local.Position.Y;
        var savedZ = placed.Transform.Local.Position.Z;
        var savedRoll = placed.Transform.Local.Rotation.X;
        var savedPitch = placed.Transform.Local.Rotation.Y;
        var savedYaw = placed.Transform.Local.Rotation.Z;

        // Load side (SpawnManager recipe)
        var loaded = DoodadManager.Instance.Create(_world, 0, FurnitureTemplateId, null, true);
        loaded.IsPersistent = true;
        loaded.DbId = 4243;
        loaded.AttachPoint = placed.AttachPoint;
        loaded.OwnerDbId = house.Id;
        loaded.Transform.Parent = house.Transform;
        loaded.Transform.Local.SetPosition(savedX, savedY, savedZ);
        loaded.Transform.Local.SetRotation(savedRoll, savedPitch, savedYaw);

        await Assert.That(loaded.AttachPoint).IsEqualTo(AttachPointKind.HealPoint0);
        await Assert.That(loaded.TemplateId).IsEqualTo(placed.TemplateId);
        await Assert.That(loaded.OwnerDbId).IsEqualTo(house.Id);
        // 45° yaw survived as an offset from the house yaw (0)
        await Assert.That(loaded.Transform.World.Rotation.Z).IsEqualTo(placed.Transform.World.Rotation.Z).Within(0.001f);
    }

    // ================================================================ rig helpers

    private static House MakeHouse(uint id, Vector3 position, float yawRadians)
    {
        var house = new House
        {
            Id = id,
            ObjId = 0xC000 + id,
            TlId = (ushort)id,
            OwnerId = 1,
            Name = $"m3b_house_{id}"
        };
        house.Transform = new Transform(house, null, position, new Vector3(0f, 0f, yawRadians));
        house.Transform.InstanceId = TestInstanceId;
        return house;
    }

    private static object GetSingletonInstance<T>() where T : class
    {
        return typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static void SetSingletonInstance<T>(T instance) where T : class
        => SetSingletonInstance(typeof(Singleton<T>), instance);

    private static void SetSingletonInstance(Type singletonType, object instance)
    {
        singletonType.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, instance);
    }

    private void SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetField(worldManager, "_worlds", new System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>());
        SetSingletonInstance(worldManager);
    }

    private void SeedDoodadManager()
    {
        var doodadManager = new DoodadManager(
            new FakeObjectIdManager(),
            Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);

        // Plain furniture template (no func groups — like most decoration furniture)
        var plainTemplate = new DoodadTemplate { Id = FurnitureTemplateId };

        // Multi-phase template: start group 1 + alternate group 2
        var startGroup = new DoodadFuncGroups
        {
            Id = StartGroupId,
            Almighty = MultiPhaseTemplateId,
            GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
        };
        var altGroup = new DoodadFuncGroups
        {
            Id = AltPhaseId,
            Almighty = MultiPhaseTemplateId,
            GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Normal
        };
        var multiTemplate = new DoodadTemplate { Id = MultiPhaseTemplateId };
        multiTemplate.FuncGroups.Add(startGroup);
        multiTemplate.FuncGroups.Add(altGroup);

        SetField(doodadManager, "_templates", new Dictionary<uint, DoodadTemplate>
        {
            [FurnitureTemplateId] = plainTemplate,
            [MultiPhaseTemplateId] = multiTemplate
        });
        SetField(doodadManager, "_allFuncGroups", new Dictionary<uint, DoodadFuncGroups>
        {
            [StartGroupId] = startGroup,
            [AltPhaseId] = altGroup
        });
        SetField(doodadManager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>());
        SetField(doodadManager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>());

        SetSingletonInstance(doodadManager);
    }

    private void RegisterWorld(WorldInstance world)
    {
        var field = WorldManager.Instance.GetType().GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)field?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(world.Id, world);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }
}
