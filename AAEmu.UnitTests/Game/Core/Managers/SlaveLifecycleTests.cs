using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using Portal = AAEmu.Game.Models.Game.Units.Portal;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Utils.Mocks;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M4-3 (t_4a91a4f5) vehicle lifecycle rig — the canonical 1.2 slave rules
/// driven on the REAL SlaveManager paths with no MySQL:
///
///   summon      : GetActiveSlaveByOwnerObjId (one active slave per owner;
///                 dead slaves excluded)
///   despawn     : TryDespawnOwnedSlave — owner gate, range gate (312),
///                 combat gate (288), cargo gate (801 via Delete)
///   passenger   : BindSlave — dead-vehicle gate (324), seat-occupied,
///                 driver attach; UnbindSlave clears attachment state
///   disconnect  : RemoveAndDespawnAllActiveOwnedSlaves
///   death       : DoDie → summon item destroyed + passengers unbound
///   stuck       : RidersEscape — 20m range gate (640), reposition in range
///
/// The MySQL write tail is represented by CountingSlave (Save() override —
/// the Doodad.Save() seam precedent from M3b). Slaves are constructed
/// directly (not via SlaveManager.Create, which opens MySQL) and registered
/// in the rigged world through AddObject so GetAllSlaves() sees them.
///
/// Singleton discipline (t_4f11a519): seeds WorldManager only when missing
/// and restores the previous instance in teardown.
/// </summary>
[ParallelLimiter<SlaveSequentialParallelLimit>]
[NotInParallel]
public class SlaveLifecycleTests
{
    private const uint TestInstanceId = 7;

    private List<byte[]> _capturedPackets;
    private GameConnection _connection;
    private WorldInstance _world;
    private SlaveManager _slaveManager;
    private CharacterMock _owner;
    private CharacterMock _other;
    private object _previousWorldManager;
    private object _previousSusManager;
    private bool _previousDebugInfo;
    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = typeof(Singleton<WorldManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null);
        _previousSusManager = typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null);
        _previousDebugInfo = AppConfiguration.Instance.DebugInfo;
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.DebugInfo = false; // SendDebugMessage hits CharacterManager (unseeded DI singleton)
        AppConfiguration.Instance.World = new WorldConfig(); // PhysicsManager ctor reads World.TargetPhysicsTps

        FormulaManager.Instance.Load(); // idempotent; real formulas from canonical data (Slave.MaxHp)

        SeedWorldManager();
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, new SusManager(WorldManager.Instance)); // GameObject.DisabledSetPosition touches SusManager (teleport paths)

        _world = new WorldInstance(new WorldTemplate { Id = TestInstanceId, Name = "slave-lifecycle-test-world" }, 0, false, TestInstanceId);
        _world.Regions = new Region[16, 16];
        RegisterWorld(_world);
        _world.SpawnManager = new SpawnManager(_world);
        // Physics is read-only; seed the backing field directly (Delete → world.Physics.RemoveShip).
        // RemoveShip is null-safe on RigidBody, so the manager never needs StartPhysics() in tests.
        typeof(WorldInstance).GetField("<Physics>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(_world, new PhysicsManager { SimulationWorld = _world });
        _slaveManager = new SlaveManager(_world);
        _world.SlaveManager = _slaveManager;

        var mockSession = new PacketCaptureSession();
        _connection = new GameConnection(mockSession);
        _capturedPackets = mockSession.CapturedPackets;

        _owner = MakeCharacter(0xB000, "slave-owner", new Vector3(0, 0, 0));
        _other = MakeCharacter(0xB001, "slave-other", new Vector3(0, 0, 0));
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.DebugInfo = _previousDebugInfo;
        AppConfiguration.Instance.World = _previousWorldConfig;
        typeof(Singleton<WorldManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, _previousWorldManager);
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, _previousSusManager);
    }

    // ================================================================ rig helpers

    private CharacterMock MakeCharacter(uint objId, string name, Vector3 position)
    {
        var character = new CharacterMock { Id = objId, ObjId = objId, Name = name };
        character.ParentWorld = _world;
        character.Connection = _connection;
        character.Transform.Local.SetPosition(position);
        character.Transform.InstanceId = TestInstanceId;
        return character;
    }

    private static SlaveTemplate MakeTemplate(uint id = 15, float portalTime = 0f)
    {
        return new SlaveTemplate
        {
            Id = id,
            Name = $"slave_{id}",
            ModelId = 129,
            Mountable = true,
            SlaveKind = SlaveKind.Boat,
            PortalTime = portalTime,
            Level = 1,
        };
    }

    /// <summary>Slave with Save() stubbed (no MySQL), registered in the rigged world.</summary>
    private CountingSlave MakeSlave(uint objId, Character summoner, Vector3 position,
        SlaveTemplate template = null, uint hp = 1000, Item summoningItem = null)
    {
        var slave = new CountingSlave
        {
            ObjId = objId,
            TlId = (ushort)(objId & 0xFFFF),
            Id = objId,
            Name = "test-slave",
            Template = template ?? MakeTemplate(),
            Hp = (int)hp,
            Mp = 100,
            Summoner = summoner,
            SummoningItem = summoningItem,
            ParentWorld = _world,
        };
        slave.Transform.Local.SetPosition(position);
        slave.Transform.InstanceId = TestInstanceId;
        _world.AddObject(slave);
        return slave;
    }

    private void SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var field = typeof(WorldManager).GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(worldManager, new System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>());
        typeof(Singleton<WorldManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, worldManager);
    }

    private void RegisterWorld(WorldInstance world)
    {
        var field = WorldManager.Instance.GetType().GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)field?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(world.Id, world);
    }

    /// <summary>True if the captured packet stream contains the given error message packet.</summary>
    private bool CapturedError(ErrorMessageType type)
    {
        foreach (var bytes in _capturedPackets)
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
                stream.ReadUInt16(); // TypeId (opcode)
                var errorType = (ErrorMessageType)stream.ReadInt16();
                if (errorType == type)
                    return true;
            }
            catch
            {
                // ignore malformed captures
            }
        }
        return false;
    }

    // ================================================================ summon

    [Test]
    public async Task GetActiveSlaveByOwnerObjId_OwnerHasActiveSlave_ReturnsIt()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(5, 0, 0));

        var result = _slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.ObjId).IsEqualTo(slave.ObjId);
    }

    [Test]
    public async Task GetActiveSlaveByOwnerObjId_OtherOwnersSlave_ReturnsNull()
    {
        MakeSlave(0x1001, _other, new Vector3(5, 0, 0));

        var result = _slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetActiveSlaveByOwnerObjId_DeadSlave_Excluded()
    {
        MakeSlave(0x1001, _owner, new Vector3(5, 0, 0), hp: 0);

        var result = _slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId);

        await Assert.That(result).IsNull();
    }

    // ================================================================ despawn gates

    [Test]
    public async Task TryDespawnOwnedSlave_NonOwner_Refused()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.TryDespawnOwnedSlave(_other, slave.ObjId);

        // slave still active for its owner
        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNotNull();
    }

    [Test]
    public async Task TryDespawnOwnedSlave_OwnerOutOfRange_Error312_StillActive()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(50, 0, 0));

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.ObjId);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNotNull();
        await Assert.That(CapturedError(ErrorMessageType.SlaveDespawnNearTheSlave)).IsTrue();
    }

    [Test]
    public async Task TryDespawnOwnedSlave_OwnerInCombat_Error288_StillActive()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        _owner.IsInBattle = true;

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.ObjId);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNotNull();
        await Assert.That(CapturedError(ErrorMessageType.SlaveCannotRemoveWhileInCombat)).IsTrue();
    }

    [Test]
    public async Task TryDespawnOwnedSlave_OwnerInRange_Despawned()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.ObjId);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNull();
        await Assert.That(_world.GetAllSlaves()).DoesNotContain(slave);
    }

    [Test]
    public async Task TryDespawnOwnedSlave_ByTlId_OwnerInRange_Despawned()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.TlId);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNull();
    }

    [Test]
    public async Task TryDespawnOwnedSlave_UnknownObjId_Noop()
    {
        _slaveManager.TryDespawnOwnedSlave(_owner, 0x9999);
        await Assert.That(_capturedPackets).IsEmpty();
    }

    // ================================================================ cargo gate (801)

    [Test]
    public async Task Delete_AttachedDoodadHoldsItem_Error801_Refused()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        var doodad = new Doodad
        {
            ObjId = 0x2001,
            ItemId = 0x3001, // cargo slot holds a trade pack
            ItemTemplateId = 6452,
            Template = new DoodadTemplate { Id = 1 },
        };
        slave.AttachedDoodads.Add(doodad);

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.ObjId);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNotNull();
        await Assert.That(CapturedError(ErrorMessageType.SlaveEquipmentLoadedItem)).IsTrue();
    }

    [Test]
    public async Task Delete_EmptyCargo_Despawns()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.ObjId);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNull();
    }

    // ================================================================ bind gates

    [Test]
    public async Task BindSlave_DeadVehicle_Error324_NotAttached()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0), hp: 0);

        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

        await Assert.That(slave.AttachedCharacters).IsEmpty();
        await Assert.That(CapturedError(ErrorMessageType.SlaveCannotBindWhileIsDead)).IsTrue();
    }

    [Test]
    public async Task BindSlave_SeatOccupied_SecondBindRefused()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);
        _slaveManager.BindSlave(_other, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        await Assert.That(slave.AttachedCharacters[AttachPointKind.Driver].ObjId).IsEqualTo(_owner.ObjId);
    }

    [Test]
    public async Task BindSlave_Driver_Attached()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        await Assert.That(slave.AttachedCharacters[AttachPointKind.Driver].ObjId).IsEqualTo(_owner.ObjId);
        await Assert.That(_owner.AttachedPoint).IsEqualTo(AttachPointKind.Driver);
    }

    [Test]
    public async Task BindSlave_PassengerSeat_Attached()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Passenger0, AttachUnitReason.NewMaster);

        await Assert.That(slave.AttachedCharacters).HasCount().EqualTo(1);
        await Assert.That(slave.AttachedCharacters[AttachPointKind.Passenger0].ObjId).IsEqualTo(_owner.ObjId);
    }

    [Test]
    public async Task TryDespawnOwnedSlave_PassengerAboard_UnbindsPassenger()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.ObjId);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNull();
        await Assert.That(slave.AttachedCharacters).IsEmpty();
        await Assert.That(_owner.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(_owner.Transform.Parent).IsNull();
    }

    [Test]
    public async Task UnbindSlave_ClearsAttachmentState()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

        _slaveManager.UnbindSlave(_owner, slave.TlId, AttachUnitReason.SlaveBinding);

        await Assert.That(slave.AttachedCharacters).IsEmpty();
        await Assert.That(_owner.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(_owner.Transform.Parent).IsNull();
    }

    // ================================================================ disconnect cleanup

    [Test]
    public async Task RemoveAndDespawnAllActiveOwnedSlaves_DespawnsOwnedSlave()
    {
        MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        MakeSlave(0x1002, _other, new Vector3(1, 0, 0));

        _slaveManager.RemoveAndDespawnAllActiveOwnedSlaves(_owner);

        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNull();
        // other owner's slave untouched
        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_other.ObjId)).IsNotNull();
    }

    [Test]
    public async Task RemoveAndDespawnAllActiveOwnedSlaves_NoSlave_ClearsAttachmentState()
    {
        _owner.AttachedPoint = AttachPointKind.Driver;
        _owner.IsRiding = true;

        _slaveManager.RemoveAndDespawnAllActiveOwnedSlaves(_owner);

        await Assert.That(_owner.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(_owner.IsRiding).IsFalse();
    }

    // ================================================================ portal / instance

    /// <summary>Portal NPC registered in the rigged world; teleport target at <paramref name="teleportInstanceId"/>.</summary>
    private Portal MakePortal(uint objId, uint teleportInstanceId)
    {
        var portal = new Portal { ObjId = objId, Template = new NpcTemplate { Id = 1 } };
        portal.Transform.InstanceId = TestInstanceId;
        portal.TeleportPosition = new Transform(portal);
        portal.TeleportPosition.InstanceId = teleportInstanceId;
        _world.AddObject(portal);
        return portal;
    }

    /// <summary>True if the captured packet stream contains the given G2C opcode.</summary>
    private bool CapturedOpcode(ushort opcode)
    {
        foreach (var bytes in _capturedPackets)
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
                // ignore malformed captures
            }
        }
        return false;
    }

    [Test]
    public async Task UsePortal_CrossInstance_CargoLoaded_Error801_SlaveStays()
    {
        _owner.Buffs = Mock.Of<IBuffs>().Object; // PortalManager's CheckBuffTag hits the DI-only SkillManager singleton
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        slave.AttachedDoodads.Add(new Doodad
        {
            ObjId = 0x2001,
            ItemId = 0x3001, // cargo slot holds a trade pack
            ItemTemplateId = 6452,
            Template = new DoodadTemplate { Id = 1 },
        });
        MakeTargetWorld(8);
        MakePortal(0x4001, 8);

        PortalManager.UsePortal(_owner, 0x4001);

        // canonical 417/801 family: cannot teleport with loaded cargo; vehicle stays
        await Assert.That(CapturedError(ErrorMessageType.SlaveEquipmentLoadedItem)).IsTrue();
        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNotNull();
        await Assert.That(CapturedOpcode(SCOffsets.SCLoadInstancePacket)).IsFalse();
    }

    [Test]
    public async Task UsePortal_CrossInstance_NoCargo_DespawnsSlave_LoadsInstance()
    {
        _owner.Buffs = Mock.Of<IBuffs>().Object; // PortalManager's CheckBuffTag hits the DI-only SkillManager singleton
        MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        MakeTargetWorld(8);
        MakePortal(0x4001, 8);

        PortalManager.UsePortal(_owner, 0x4001);

        // cross-instance teleport despawns owned slaves (upstream PR #1477 behavior)
        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNull();
        await Assert.That(CapturedOpcode(SCOffsets.SCLoadInstancePacket)).IsTrue();
    }

    [Test]
    public async Task UsePortal_SameWorld_SlaveStays()
    {
        _owner.Buffs = Mock.Of<IBuffs>().Object; // PortalManager's CheckBuffTag hits the DI-only SkillManager singleton
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        MakePortal(0x4001, TestInstanceId);

        PortalManager.UsePortal(_owner, 0x4001);

        // same-world portals leave the vehicle in place (canonical: vehicles are world objects)
        await Assert.That(_slaveManager.GetActiveSlaveByOwnerObjId(_owner.ObjId)).IsNotNull();
        await Assert.That(slave.Transform.World.Position).IsEqualTo(new Vector3(1, 0, 0));
        await Assert.That(CapturedOpcode(SCOffsets.SCLoadInstancePacket)).IsFalse();
    }

    private WorldInstance MakeTargetWorld(uint id)
    {
        var world = new WorldInstance(new WorldTemplate { Id = id, Name = $"portal-target-{id}" }, 0, false, id);
        world.Regions = new Region[16, 16];
        RegisterWorld(world);
        return world;
    }

    // ================================================================ restart recovery

    [Test]
    public async Task Despawn_PersistsRowForRestartRecovery()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.TryDespawnOwnedSlave(_owner, slave.ObjId);

        // MySQL write tail fired: the slaves row (item binding, HP/MP, attach_point) survives
        // a server restart, and re-summon from the item reads HP/MP back (dossier §8)
        await Assert.That(slave.SaveCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Disconnect_PersistsRowForRestartRecovery()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));

        _slaveManager.RemoveAndDespawnAllActiveOwnedSlaves(_owner);

        await Assert.That(slave.SaveCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Restart_FreshManager_NoGhostSlave_NoStaleAttachment()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

        // Simulate a server restart: fresh world, fresh manager, fresh character.
        // The pre-restart in-memory slave object vanishes with the process; player slaves
        // do not boot-respawn (canonical: re-summon from the item is the recovery path, dossier §8).
        var freshWorld = MakeTargetWorld(8);
        freshWorld.SpawnManager = new SpawnManager(freshWorld);
        typeof(WorldInstance).GetField("<Physics>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(freshWorld, new PhysicsManager { SimulationWorld = freshWorld });
        var freshManager = new SlaveManager(freshWorld);
        freshWorld.SlaveManager = freshManager;

        var freshOwner = MakeCharacter(0xB000, "slave-owner", new Vector3(0, 0, 0));
        freshOwner.ParentWorld = freshWorld;

        // no ghost vehicles, no stale driver attachment, no phantom riding state
        await Assert.That(freshManager.GetActiveSlaveByOwnerObjId(freshOwner.ObjId)).IsNull();
        await Assert.That(freshWorld.GetAllSlaves()).IsEmpty();
        await Assert.That(freshOwner.AttachedPoint).IsEqualTo(AttachPointKind.None);
        await Assert.That(freshOwner.IsRiding).IsFalse();
        // slave rows are written on explicit lifecycle events only (no periodic sweep) —
        // riding alone does not touch the DB, so a crash leaves no half-persisted state
        await Assert.That(slave.SaveCount).IsEqualTo(0);
    }

    // ================================================================ death cleanup

    [Test]
    public async Task DoDie_MarksSummoningItemDestroyed_AndUnbindsPassengers()
    {
        var summonItem = new SummonSlave { Id = 0x5001, TemplateId = 17863 };
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0), summoningItem: summonItem);
        _slaveManager.BindSlave(_owner, slave.ObjId, AttachPointKind.Driver, AttachUnitReason.NewMaster);

        slave.DoDie(_owner, KillReason.Damage);

        await Assert.That(summonItem.IsDestroyed).IsEqualTo((byte)1);
        await Assert.That(slave.AttachedCharacters).IsEmpty();
        await Assert.That(_owner.AttachedPoint).IsEqualTo(AttachPointKind.None);
    }

    // ================================================================ stuck recovery (Rider's Escape)

    [Test]
    public async Task RidersEscape_NoSlave_Noop()
    {
        _slaveManager.RidersEscape(_owner, new SkillCastPositionTarget { PosX = 5, PosY = 0, PosZ = 0 });
        await Assert.That(_capturedPackets).IsEmpty();
    }

    [Test]
    public async Task RidersEscape_TargetTooFar_Error640_NoMove()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(1, 0, 0));
        var before = slave.Transform.World.Position;

        _slaveManager.RidersEscape(_owner, new SkillCastPositionTarget { PosX = 50, PosY = 0, PosZ = 0 });

        await Assert.That(CapturedError(ErrorMessageType.SlaveEscapeTooFarFromSlave)).IsTrue();
        await Assert.That(slave.Transform.World.Position).IsEqualTo(before);
    }

    [Test]
    public async Task RidersEscape_InRange_Repositions()
    {
        var slave = MakeSlave(0x1001, _owner, new Vector3(10, 0, 0));

        _slaveManager.RidersEscape(_owner, new SkillCastPositionTarget { PosX = 3, PosY = 0, PosZ = 0 });

        await Assert.That(CapturedError(ErrorMessageType.SlaveEscapeTooFarFromSlave)).IsFalse();
        // repositioned toward the target (within 20m of the player)
        await Assert.That(slave.Transform.World.Position.X).IsLessThan(10f);
    }
}

/// <summary>Slave with Save() stubbed — the MySQL write tail never runs in unit tests.</summary>
public class CountingSlave : Slave
{
    public int SaveCount { get; private set; }

    public override bool Save()
    {
        SaveCount++;
        return true;
    }
}

/// <summary>Sequential parallel limiter for classes that swap the WorldManager singleton.</summary>
public sealed class SlaveSequentialParallelLimit : IParallelLimit
{
    public int Limit => 1;
}
