using System.Text.Json;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Phase 2 prerequisite (t_a7756a00): LoadPackOntoVehicle on the
/// IGameplayActor v2 surface through the REAL gameplay path — no manual
/// attachment, no direct Transform write, no GM/reflection/DB shortcut:
///
///  - carried pack : PackVehicleService.TryLoadCarriedPack moves the pack
///    out of the Backpack equipment slot into the System container (the
///    same container move PutDownBackpackEffect performs), creates the pack
///    doodad through DoodadManager.Create and snaps it onto the vehicle's
///    first free cargo point through the SlaveManager attach seam
///    (ApplyAttachPointLocation — the model's attach-point data, retail
///    snap-to-cargo-point).
///  - placed pack  : PackVehicleService.TryLoadPlacedPack re-parents the
///    standing recoverable pack doodad to the slave and snaps it onto the
///    free cargo point WITHOUT re-running its phase (the recover funcs must
///    not re-fire during the move).
///
/// The rig vehicle is the canonical 1.2 Farm Wagon shape: slave template
/// with four pack-storage-box bindings ("등짐 보관 상자" doodad 3446) at
/// attach points 9-12, and SlaveGameData seeded with the canonical model
/// 1008 attach-point positions. The snap assertion compares the doodad's
/// LOCAL transform against those canonical positions.
///
/// Idempotency: after a carried load the Backpack slot is empty (fresh-key
/// retry refused pre-flight); a same-key retry is refused by the ledger; an
/// already-attached placed pack is refused StateTransition; a full vehicle
/// refuses further packs.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class GameplayActorLoadPackOntoVehicleTests
{
    private static readonly System.Numerics.Vector3 TestPosition = new(1000f, 1000f, 100f);

    private static uint _nextSlaveObjId = 0x3000;

    private WorldInstance? _registeredWorld;

    /// <summary>AppConfiguration.Instance.World captured in SetUp (null in unit
    /// tests) and restored in TearDown — the established convention
    /// (CropHarvestLoopTests / PlantActionsTests): Doodad.InitDoodad reads
    /// Template.TotalDoodadGrowthTime / World.GrowthRate.</summary>
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
        // Identity-guarded unregister (t_449d0c41 discipline): never remove
        // a sibling class's id-1 world; only the world this test registered.
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

    /// <summary>
    /// Registers the headless session world in the shared WorldManager for
    /// the duration of the test. The engine's world-lookup paths
    /// (DoodadManager.Create's ParentWorld setter, Region.AddObject's
    /// Transform.InstanceId assignment) resolve through
    /// WorldManager.GetWorld — the rig worlds are unregistered by design
    /// (t_449d0c41), so the real engine factory would NRE on them. The
    /// registration is removed in TearDown with an identity guard.
    /// </summary>
    private void RegisterWorld(HeadlessSession session)
    {
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(WorldManager.Instance)!;
        worlds.TryAdd(session.World.Id, session.World);
        _registeredWorld = session.World;
    }

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
        if (GetField(itemGameData, "_itemGradeBuffs") == null)
            SetField(itemGameData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
    }

    private static object? GetField(object instance, string name)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        return instance.GetType().GetField(name, flags)?.GetValue(instance);
    }

    private static void SetField(object instance, string name, object value)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        instance.GetType().GetField(name, flags)!.SetValue(instance, value);
    }

    // ================================================================ carried pack — real engine path

    [Test]
    public async Task LoadCarriedPack_CompletesThroughRealEnginePath_SnapsToCargoPoint()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-1");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var pack = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);

        var request = actor.LoadPackOntoVehicle(slave.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // Real engine state 1: the pack LEFT the Backpack slot into the System container.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack))
            .IsNull();
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(pack!.Id)).IsNotNull();
        // Real engine state 2: a pack doodad exists, attached to the slave at the FIRST
        // free cargo point with the canonical snap position (model 1008 attach point 9).
        var attached = slave.AttachedDoodads.FirstOrDefault(d => d.ItemId == pack.Id);
        await Assert.That(attached).IsNotNull();
        await Assert.That(attached!.ParentObjId).IsEqualTo(slave.ObjId);
        await Assert.That(attached.AttachPoint).IsEqualTo(AttachPointKind.Cannon0);
        await Assert.That(attached.Transform.Parent?.GameObject).IsEqualTo(slave);
        await Assert.That(attached.Transform.Local.Position.X).IsEqualTo(-0.55f);
        await Assert.That(attached.Transform.Local.Position.Y).IsEqualTo(-2.0f);
        await Assert.That(attached.Transform.Local.Position.Z).IsEqualTo(1.15f);
        // The doodad is live in the world (spawned through the ordinary path).
        await Assert.That(session.World.GetDoodad(attached.ObjId)).IsEqualTo(attached);
        // The completion payload carries the engine-side data.
        await Assert.That(request.Result).IsNotNull();
    }

    [Test]
    public async Task LoadCarriedPack_AuditRecord_ToJson_CarriesFullTraceShape()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-2");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);

        actor.LoadPackOntoVehicle(slave.ObjId);
        using var doc = JsonDocument.Parse(actor.AuditTrace[0].ToJson());
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("LoadPackOntoVehicle");
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(slave.ObjId);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(4);
        await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
        await Assert.That(root.GetProperty("state_changes").EnumerateArray()
            .Any(s => s.GetString()!.Contains("Running (loading pack onto vehicle"))).IsTrue();
    }

    [Test]
    public async Task LoadCarriedPack_NoPackInBackpackSlot_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-3");
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);

        var request = actor.LoadPackOntoVehicle(slave.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("no trade pack carried")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task LoadCarriedPack_NonTradePackInSlot_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-4");
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);
        // A backpack-slot item that is NOT an auto-equip trade pack
        // (bind-on-equip — the exact predicate IsAutoEquipTradePack rejects).
        const uint bindOnEquipTemplateId = 290_910;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(ItemManager.Instance, "_templates");
        if (!templates.ContainsKey(bindOnEquipTemplateId))
        {
            templates[bindOnEquipTemplateId] = new BackpackTemplate
            {
                Id = bindOnEquipTemplateId,
                MaxCount = 1,
                BackpackType = BackpackType.TradePack,
                BindType = ItemBindType.BindOnEquip
            };
        }

        var item = ItemManager.Instance.Create(bindOnEquipTemplateId, 1, 0);
        actor.Character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, item, (int)EquipmentItemSlot.Backpack);

        var request = actor.LoadPackOntoVehicle(slave.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not a trade pack")).IsTrue();
        // The item was not consumed.
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id)
            .IsEqualTo(item.Id);
    }

    [Test]
    public async Task LoadCarriedPack_UnknownVehicle_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-5");
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);

        var request = actor.LoadPackOntoVehicle(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in world")).IsTrue();
    }

    [Test]
    public async Task LoadCarriedPack_OutOfRange_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-6");
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);
        // Park the vehicle far outside the load range.
        slave.Transform.Local.SetPosition(TestPosition + new System.Numerics.Vector3(500f, 0f, 0f));

        var request = actor.LoadPackOntoVehicle(slave.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("out of interaction range")).IsTrue();
    }

    [Test]
    public async Task LoadCarriedPack_NotACargoVehicle_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-7");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++, cargoPoints: 0);

        var request = actor.LoadPackOntoVehicle(slave.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("no cargo points")).IsTrue();
    }

    [Test]
    public async Task LoadCarriedPack_CargoFull_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-8");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);
        // Occupy all four cargo points with pack doodads (the state a loaded
        // vehicle is in: attached doodads with item links on every point).
        for (var i = 0; i < 4; i++)
        {
            slave.AttachedDoodads.Add(new Doodad
            {
                ObjId = 0x3100u + (uint)i,
                AttachPoint = (AttachPointKind)((int)AttachPointKind.Cannon0 + i),
                ItemId = 0x3200u + (uint)i,
                ItemTemplateId = GameplayActorTestRig.CargoPackTemplateId
            });
        }

        var request = actor.LoadPackOntoVehicle(slave.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("cargo is full")).IsTrue();
        // The pack stayed in the Backpack slot (nothing was consumed).
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack))
            .IsNotNull();
    }

    // ================================================================ carried pack — retry idempotency

    [Test]
    public async Task LoadCarriedPack_RetrySameKey_Rejected_PackLoadedExactlyOnce()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-retry-1");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var pack = actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);

        var original = actor.LoadPackOntoVehicle(slave.ObjId, idempotencyKey: "pack-load:2001");
        var retry = actor.LoadPackOntoVehicle(slave.ObjId, idempotencyKey: "pack-load:2001");

        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        // Exactly ONE pack doodad on the vehicle (no second load).
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId == pack!.Id)).IsEqualTo(1);
        // The pack instance is in the System container exactly once.
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(pack.Id)).IsNotNull();
    }

    [Test]
    public async Task LoadCarriedPack_RetryFreshKeyAfterSuccess_Rejected_NoCarriedPack()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-carry-retry-2");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.EquipPack(actor, GameplayActorTestRig.CargoPackTemplateId);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);

        var original = actor.LoadPackOntoVehicle(slave.ObjId);
        // Fresh key (no ledger correlation): the engine state is the
        // backstop — the Backpack slot is empty, so the retry is refused
        // pre-flight and the pack can never be loaded twice.
        var retry = actor.LoadPackOntoVehicle(slave.ObjId);

        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail?.Contains("no trade pack carried")).IsTrue();
    }

    // ================================================================ placed pack — real engine path

    [Test]
    public async Task LoadPlacedPack_CompletesThroughRealEnginePath_DoodadReparentsAndSnaps()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-placed-1");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);
        // Mirror the REAL placed state: PutDownBackpackEffect sets
        // AttachPoint = None on the placed pack (the Doodad ctor default is
        // System).
        session.World.GetDoodad(doodadObjId)!.AttachPoint = AttachPointKind.None;

        var request = actor.LoadPackOntoVehicle(slave.ObjId, doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // The SAME doodad object re-parented onto the slave (not deleted,
        // not re-created): the placed pack's object identity survives.
        var doodad = session.World.GetDoodad(doodadObjId);
        await Assert.That(doodad).IsNotNull();
        await Assert.That(doodad!.ParentObjId).IsEqualTo(slave.ObjId);
        await Assert.That(doodad.AttachPoint).IsEqualTo(AttachPointKind.Cannon0);
        await Assert.That(doodad.Transform.Parent?.GameObject).IsEqualTo(slave);
        await Assert.That(doodad.Transform.Local.Position.X).IsEqualTo(-0.55f);
        await Assert.That(doodad.Transform.Local.Position.Y).IsEqualTo(-2.0f);
        await Assert.That(doodad.Transform.Local.Position.Z).IsEqualTo(1.15f);
        await Assert.That(slave.AttachedDoodads.Contains(doodad)).IsTrue();
        // The pack item stayed in the System container (the phase was NOT
        // re-run — DoodadFuncRecoverItem would have granted it back).
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack))
            .IsNull();
        await Assert.That(actor.Character.Inventory.SystemContainer.GetItemByItemId(pack.Id)).IsNotNull();
    }

    [Test]
    public async Task LoadPlacedPack_AlreadyAttached_Rejected_StateTransition()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-placed-2");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);
        // Pre-attach the doodad (the state a successful placed load leaves).
        var doodad = session.World.GetDoodad(doodadObjId);
        doodad.ParentObjId = slave.ObjId;
        doodad.AttachPoint = AttachPointKind.Cannon0;

        var request = actor.LoadPackOntoVehicle(slave.ObjId, doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail?.Contains("already attached")).IsTrue();
    }

    [Test]
    public async Task LoadPlacedPack_UnknownDoodad_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-placed-3");
        RegisterWorld(session);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);

        var request = actor.LoadPackOntoVehicle(slave.ObjId, 999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in world")).IsTrue();
    }

    [Test]
    public async Task LoadPlacedPack_NotRecoverable_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("t-a7756a00-placed-4");
        RegisterWorld(session);
        actor.Character.Level = 10;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var pack = CreateSystemPack(actor);
        var doodadObjId = GameplayActorTestRig.PlacePackDoodad(session, actor, pack,
            groupId: 92_399, funcId: 92_499); // an empty func group — no recover row
        var doodad = session.World.GetDoodad(doodadObjId);
        doodad!.AttachPoint = AttachPointKind.None;
        doodad.CurrentFuncs.Clear(); // no recover func on the current phase
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, _nextSlaveObjId++);

        var request = actor.LoadPackOntoVehicle(slave.ObjId, doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not a recoverable trade pack")).IsTrue();
    }

    // ================================================================ rig helpers (mirror the pack-actions class)

    private static Item CreateSystemPack(GameplayActor actor)
    {
        var item = ItemManager.Instance.Create(GameplayActorTestRig.CargoPackTemplateId, 1, 0);
        actor.Character.Inventory.SystemContainer.AddOrMoveExistingItem(ItemTaskType.DropBackpack, item);
        return item;
    }
}
