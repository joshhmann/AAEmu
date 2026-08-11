using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Utils;
using AAEmu.UnitTests.Utils.Mocks;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Housing;

/// <summary>Serializes tests in this class: each test seeds its own WorldManager
/// singleton + world instance, so within-class parallelism would resolve
/// ParentWorld/GetDoodad against another test's world (t_f3700374 pattern:
/// [NotInParallel] does NOT serialize within a class).</summary>
public sealed class SequentialParallelLimit : IParallelLimit
{
    public int Limit => 1;
}

/// <summary>Minimal ISession fake that captures every encoded packet sent to the client.</summary>
public sealed class PacketCaptureSession : ISession
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

/// <summary>Sequential fake for the object-id manager (no DB needed).</summary>
public sealed class FakeObjectIdManager : IObjectIdManager
{
    private uint _next;

    public FakeObjectIdManager(uint start = 0xA000) => _next = start;

    public void Load() { }
    public bool Initialize(bool forceReset = false) => true;
    public uint GetNextId() => _next++;
    public uint[] GetNextId(int count) => Enumerable.Range(0, count).Select(_ => _next++).ToArray();
    public void ReleaseId(uint usedObjectId) { }
    public void ReleaseId(IEnumerable<uint> usedObjectIds) { }
}

/// <summary>
/// M3a-4 harness: homestead storage + furniture interactions driven on the REAL engine paths.
///
/// Storage: DoodadCoffer + CofferContainer + ItemContainer capacity mechanics
/// (put/get via AddOrMoveExistingItem/RemoveItem, capacity boundary via GetUnusedSlot),
/// open/close via DoodadManager.OpenCofferDoodad/CloseCofferDoodad (the path
/// CSCofferInteractionPacket invokes), contents pushed to the client via
/// SCCofferContentsUpdatePacket.
///
/// Furniture: DoodadManager.Create on a coffer template produces a DoodadCoffer with the
/// template's capacity; creating with a House owner binds the doodad to the house
/// (OwnerType=Housing, OwnerDbId, ParentObjId); Doodad.Use() runs the real phase-func chain
/// (DoChangePhase → DoPhaseFuncs → DoodadFuncCoffer) which opens the storage; House.AddVisibleObject
/// emits the client-visible state packets (SCUnitStatePacket 0x69, SCHouseStatePacket 0xbc,
/// SCDoodadsCreatedPacket 0x112).
///
/// Singleton discipline follows the t_4f11a519 pattern: singletons are seeded with
/// missing-only guards via reflection and restored in teardown. All doodads are created with
/// skipPhaseInitialization=true (no Task.Run) and left non-persistent (Save() short-circuits,
/// so no MySQL is needed).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class HousingStorageFurnitureTests
{
    private const uint CofferTemplateId = 5001; // seeded coffer doodad template (Capacity 20)
    private const uint FurnitureTemplateId = 5002; // seeded plain doodad template (furniture shell)
    private const uint TestInstanceId = 7;

    private List<byte[]> _capturedPackets = [];
    private GameConnection _connection;
    private WorldInstance _world;
    private CharacterMock _character;
    private uint _nextObjId = 0xA000;

    private object _previousDoodadManager;
    private object _previousItemManager;
    private object _previousSkillManager;
    private object _previousHousingManager;
    private object _previousWorldManager;

    [Before(Test)]
    public void SetUp()
    {
        // Save existing singletons so the full-suite state is restored after each test
        _previousDoodadManager = GetSingletonInstance<DoodadManager>();
        _previousItemManager = GetSingletonInstance<ItemManager>();
        _previousSkillManager = GetSingletonInstance<SkillManager>();
        _previousHousingManager = GetSingletonInstance<HousingManager>();
        _previousWorldManager = GetSingletonInstance<WorldManager>();

        SeedItemManager();
        SeedSkillManager();
        SeedHousingManager();
        SeedDoodadManager();
        SeedWorldManager();

        // ContainerIdManager needs a real id space; MySQL read fails in tests and falls back to defaults
        ContainerIdManager.Instance.Initialize(false);

        // Build a tiny world + character with a packet-capturing connection
        _world = new WorldInstance(new WorldTemplate { Id = TestInstanceId, Name = "test_world" }, 0, false, TestInstanceId);
        _world.Regions = new Region[16, 16];
        RegisterWorld(_world);

        var mockSession = new PacketCaptureSession();
        _connection = new GameConnection(mockSession);
        _capturedPackets = mockSession.CapturedPackets;

        _character = new CharacterMock { Id = 1, ObjId = 0xB000, Name = "Tester" };
        _character.ParentWorld = _world;
        _character.Connection = _connection;
    }

    [After(Test)]
    public void TearDown()
    {
        typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousDoodadManager);
        typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousItemManager);
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousSkillManager);
        typeof(Singleton<HousingManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousHousingManager);
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousWorldManager);
    }

    // ---------------------------------------------------------------- rig helpers

    private static object GetSingletonInstance<T>() where T : class
    {
        return typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static void SetSingletonInstance<T>(T instance) where T : class
    {
        typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, instance);
    }

    private static void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(obj, value);
    }

    /// <summary>
    /// Mock-backed WorldManager so singleton lookups (Transform.InstanceId →
    /// WorldManager.Instance.GetWorld) never demand a parameterless ctor.
    /// </summary>
    private void SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetPrivateField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>());
        SetSingletonInstance(worldManager);
    }

    private void RegisterWorld(WorldInstance world)
    {
        var field = WorldManager.Instance.GetType().GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)field?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(world.Id, world);
    }

    /// <summary>
    /// Real ItemManager (mock deps) with an initialized _allPersistentContainers so
    /// NewCofferContainer works without a DB.
    /// </summary>
    private void SeedItemManager()
    {
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);
        SetPrivateField(itemManager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
        SetPrivateField(itemManager, "_templates", new Dictionary<uint, ItemTemplate>());
        SetSingletonInstance(itemManager);
    }

    private void SeedSkillManager()
    {
        var skillManager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        SetPrivateField(skillManager, "_skills", new Dictionary<uint, SkillTemplate>());
        SetSingletonInstance(skillManager);
    }

    private void SeedHousingManager()
    {
        var housingManager = new HousingManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IHousingIdManager>().Object,
            Mock.Of<IHousingTldManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IMailManager>().Object,
            Mock.Of<INameManager>().Object,
            Mock.Of<IZoneManager>().Object,
            Mock.Of<IDoodadManager>().Object,
            Mock.Of<IUccManager>().Object);
        SetPrivateField(housingManager, "_houses", new Dictionary<uint, House>());
        SetPrivateField(housingManager, "_housesTl", new Dictionary<ushort, House>());
        SetSingletonInstance(housingManager);
    }

    /// <summary>
    /// Real DoodadManager (fake object-id manager) with a seeded coffer template (CofferTemplateId) and a
    /// plain furniture template (FurnitureTemplateId). The coffer template's func group carries
    /// a DoodadFuncCoffer func row + phase row wired to a DoodadFuncCoffer template (Capacity 20),
    /// mirroring how IsCofferTemplate/Create resolve real coffer templates from compact.sqlite3.
    /// </summary>
    private void SeedDoodadManager()
    {
        var doodadManager = new DoodadManager(
            new FakeObjectIdManager(),
            Mock.Of<IDoodadIdManager>().Object,
            Mock.Of<IItemManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ISusManager>().Object);

        // Coffer template + func group wiring (groupId 1, Start kind)
        var cofferFuncGroup = new DoodadFuncGroups
        {
            Id = 1,
            Almighty = CofferTemplateId,
            GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
        };
        var cofferTemplate = new DoodadCofferTemplate
        {
            Id = CofferTemplateId,
            Capacity = 20
        };
        cofferTemplate.FuncGroups.Add(cofferFuncGroup);

        var plainTemplate = new DoodadTemplate { Id = FurnitureTemplateId };

        SetPrivateField(doodadManager, "_templates", new Dictionary<uint, DoodadTemplate>
        {
            [CofferTemplateId] = cofferTemplate,
            [FurnitureTemplateId] = plainTemplate
        });
        SetPrivateField(doodadManager, "_allFuncGroups", new Dictionary<uint, DoodadFuncGroups> { [1] = cofferFuncGroup });
        SetPrivateField(doodadManager, "_funcsByGroups", new Dictionary<uint, List<DoodadFunc>>
        {
            [1] =
            [
                new DoodadFunc
                {
                    GroupId = 1,
                    FuncId = 1,
                    FuncType = "DoodadFuncCoffer",
                    NextPhase = 1,
                    SkillId = 0
                }
            ]
        });
        SetPrivateField(doodadManager, "_phaseFuncs", new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [1] =
            [
                new DoodadPhaseFunc { GroupId = 1, FuncId = 1, FuncType = "DoodadFuncCoffer" }
            ]
        });
        SetPrivateField(doodadManager, "_funcTemplates", new Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>());
        SetPrivateField(doodadManager, "_phaseFuncTemplates", new Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>
        {
            ["DoodadFuncCoffer"] = new Dictionary<uint, DoodadPhaseFuncTemplate>
            {
                [1] = new DoodadFuncCoffer { Capacity = 20 }
            }
        });

        SetSingletonInstance(doodadManager);
    }

    /// <summary>Creates a coffer on the real DoodadManager.Create path (non-persistent, no async init).</summary>
    private async Task<DoodadCoffer> CreateCofferDoodad(uint templateId = CofferTemplateId)
    {
        var doodad = DoodadManager.Instance.Create(_world, 0, templateId, null, skipPhaseInitialization: true);
        await Assert.That(doodad).IsNotNull();
        var coffer = doodad as DoodadCoffer;
        await Assert.That(coffer).IsNotNull();
        _world.AddObject(coffer);
        coffer.InitializeCoffer(_character.Id); // same wiring as CreatePlayerDoodad
        return coffer;
    }

    private ItemMock MakeItem(uint id, uint templateId, ItemTemplate template = null)
    {
        return template != null
            ? new ItemMock(id, template)
            : InventoryTestUtils.MockItem(id, templateId);
    }

    /// <summary>
    /// Packet opcode from an encoded G2C frame. Layout:
    /// [len u16 LE][0xdd][level][(hash)(count) if level==1][typeId u16 LE][body]
    /// </summary>
    private static ushort PacketOpcode(byte[] frame)
    {
        var level = frame.Length > 3 ? frame[3] : (byte)0;
        var opcodeOffset = 4 + (level == 1 ? 2 : 0);
        return (ushort)(frame[opcodeOffset] | (frame[opcodeOffset + 1] << 8));
    }

    // ================================================================ STORAGE

    [Test]
    public async Task CofferContainer_Put_AddsItemWithinCapacity()
    {
        // Arrange — real CofferContainer + real AddOrMoveExistingItem path
        var coffer = await CreateCofferDoodad();
        var item = MakeItem(1001, 2001);

        // Act
        var added = coffer.ItemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, item);

        // Assert — item landed in the container with a valid slot
        await Assert.That(added).IsTrue();
        await Assert.That(coffer.ItemContainer.Items).Contains(item);
        await Assert.That(coffer.ItemContainer.FreeSlotCount).IsEqualTo(19); // Capacity 20 - 1
    }

    [Test]
    public async Task CofferContainer_Get_RemovesItemAndFreesSlot()
    {
        // Arrange
        var coffer = await CreateCofferDoodad();
        var item = MakeItem(1002, 2002);
        coffer.ItemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, item);
        await Assert.That(coffer.ItemContainer.FreeSlotCount).IsEqualTo(19);

        // Act — real RemoveItem path
        var removed = coffer.ItemContainer.RemoveItem(ItemTaskType.Gm, item, false);

        // Assert — item gone, slot freed
        await Assert.That(removed).IsTrue();
        await Assert.That(coffer.ItemContainer.Items).DoesNotContain(item);
        await Assert.That(coffer.ItemContainer.FreeSlotCount).IsEqualTo(20);
    }

    [Test]
    public async Task CofferContainer_CapacityBoundary_RejectsOverflow()
    {
        // Arrange — capacity boundary at the container level (GetUnusedSlot returns -1 when full)
        var coffer = await CreateCofferDoodad();
        var items = Enumerable.Range(1, 20).Select(i => MakeItem((uint)(1000 + i), 2000 + (uint)i)).ToList();
        foreach (var item in items)
            await Assert.That(coffer.ItemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, item)).IsTrue();

        // Act — 21st item must be rejected by the real slot-allocation path
        var overflowItem = MakeItem(9999, 2999);
        var added = coffer.ItemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, overflowItem);

        // Assert
        await Assert.That(added).IsFalse();
        await Assert.That(coffer.ItemContainer.Items).DoesNotContain(overflowItem);
        await Assert.That(coffer.ItemContainer.FreeSlotCount).IsEqualTo(0);
    }

    [Test]
    public async Task CofferContainer_CanAccept_RejectsSoulboundAndTimedItems()
    {
        // Arrange — real CofferContainer.CanAccept rules
        var coffer = await CreateCofferDoodad();

        var soulboundItem = MakeItem(1003, 2003);
        soulboundItem.SetFlag(ItemFlag.SoulBound);
        await Assert.That(coffer.ItemContainer.CanAccept(soulboundItem, 0)).IsFalse();

        var timedTemplate = new ItemTemplate { Id = 2004, BindType = ItemBindType.Normal, ExpAbsLifetime = 60 };
        var timedItem = MakeItem(1004, 2004, timedTemplate);
        await Assert.That(coffer.ItemContainer.CanAccept(timedItem, 0)).IsFalse();

        var normalItem = MakeItem(1005, 2005);
        await Assert.That(coffer.ItemContainer.CanAccept(normalItem, 0)).IsTrue();
    }

    [Test]
    public async Task DoodadCoffer_InitializeCoffer_SetsContainerSizeFromCapacity()
    {
        // Arrange
        var coffer = await CreateCofferDoodad();

        // Assert — real ItemManager.NewCofferContainer + ContainerSize == Capacity
        await Assert.That(coffer.ItemContainer).IsNotNull();
        await Assert.That(coffer.ItemContainer.ContainerSize).IsEqualTo(coffer.Capacity);
        await Assert.That(coffer.Capacity).IsEqualTo(20);
        await Assert.That(coffer.GetItemContainerId()).IsEqualTo(coffer.ItemContainer.ContainerId);
    }

    [Test]
    public async Task OpenCofferDoodad_SetsOpenedBy_And_SendsContentsPackets()
    {
        // Arrange — real open path (same entry CSCofferInteractionPacket uses)
        var coffer = await CreateCofferDoodad();
        var item = MakeItem(1006, 2006);
        coffer.ItemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, item);

        // Act
        var opened = DoodadManager.Instance.OpenCofferDoodad(_character, coffer.ObjId);

        // Assert — OpenedBy bound + client received SCCofferContentsUpdatePacket (0x96)
        await Assert.That(opened).IsTrue();
        await Assert.That(coffer.OpenedBy).IsEqualTo(_character);
        await Assert.That(_capturedPackets.Select(PacketOpcode)).Contains(SCOffsets.SCCofferContentsUpdatePacket);
    }

    [Test]
    public async Task OpenCofferDoodad_SecondOpener_Refused()
    {
        // Arrange
        var coffer = await CreateCofferDoodad();
        DoodadManager.Instance.OpenCofferDoodad(_character, coffer.ObjId);
        var otherCharacter = new CharacterMock { Id = 2, ObjId = 0xB001, Name = "Guest" };
        otherCharacter.ParentWorld = _world;
        otherCharacter.Connection = new GameConnection(new PacketCaptureSession());

        // Act — a second character tries to open the same coffer
        var opened = DoodadManager.Instance.OpenCofferDoodad(otherCharacter, coffer.ObjId);

        // Assert — refused while OpenedBy is set
        await Assert.That(opened).IsFalse();
        await Assert.That(coffer.OpenedBy).IsEqualTo(_character);
    }

    [Test]
    public async Task CloseCofferDoodad_ClearsOpenedBy()
    {
        // Arrange
        var coffer = await CreateCofferDoodad();
        DoodadManager.Instance.OpenCofferDoodad(_character, coffer.ObjId);
        await Assert.That(coffer.OpenedBy).IsEqualTo(_character);

        // Act — real close path
        var closed = DoodadManager.Instance.CloseCofferDoodad(_character, coffer.ObjId);

        // Assert
        await Assert.That(closed).IsTrue();
        await Assert.That(coffer.OpenedBy).IsNull();
    }

    // ================================================================ FURNITURE

    [Test]
    public async Task DoodadManager_Create_CofferTemplate_ProducesDoodadCofferWithCapacity()
    {
        // Act — real Create path resolves DoodadCofferTemplate → DoodadCoffer + capacity
        var doodad = DoodadManager.Instance.Create(_world, 0, CofferTemplateId, null, skipPhaseInitialization: true);

        // Assert
        await Assert.That(doodad).IsAssignableTo<DoodadCoffer>();
        await Assert.That((doodad as DoodadCoffer).Capacity).IsEqualTo(20);
        await Assert.That(doodad.TemplateId).IsEqualTo(CofferTemplateId);
        await Assert.That(doodad.ParentWorld).IsEqualTo(_world);
    }

    [Test]
    public async Task DoodadManager_Create_WithHouseOwner_AttachesFurnitureToHouse()
    {
        // Arrange — a house (finished build step) on the real House model
        var house = new House
        {
            Id = 9001,
            ObjId = 0xC001,
            TemplateId = 6001,
            Name = "Test House",
            OwnerId = _character.Id,
            AccountId = _character.AccountId
        };
        house.Template = new HousingTemplate { Id = 6001, MainModelId = 1, HousingBindingDoodad = [] };
        house.CurrentStep = -1; // finished structure; binds nothing (no binding doodads)
        _world.AddObject(house);

        // Act — place furniture (a coffer) on the house via the real creation path
        var doodad = DoodadManager.Instance.Create(_world, 0, CofferTemplateId, house, skipPhaseInitialization: true);

        // Assert — ownership + house binding
        await Assert.That(doodad).IsNotNull();
        await Assert.That(doodad.OwnerType).IsEqualTo(DoodadOwnerType.Housing);
        await Assert.That(doodad.OwnerDbId).IsEqualTo(house.Id);
        await Assert.That(doodad.ParentObjId).IsEqualTo(house.ObjId);
        await Assert.That(doodad.OwnerId).IsEqualTo(house.OwnerId);
    }

    [Test]
    public async Task Doodad_Use_CofferFunc_OpensStorage_OnRealPhaseChain()
    {
        // Arrange — house-attached coffer furniture
        var house = new House
        {
            Id = 9002,
            ObjId = 0xC002,
            TemplateId = 6002,
            Name = "Test House 2",
            OwnerId = _character.Id,
            AccountId = _character.AccountId
        };
        house.Template = new HousingTemplate { Id = 6002, MainModelId = 1, HousingBindingDoodad = [] };
        house.CurrentStep = -1;
        _world.AddObject(house);

        var doodad = DoodadManager.Instance.Create(_world, 0, CofferTemplateId, house, skipPhaseInitialization: true);
        var coffer = (DoodadCoffer)doodad;
        _world.AddObject(coffer);
        coffer.InitializeCoffer(_character.Id);

        // Act — player uses the furniture: real Doodad.Use → DoChangePhase → DoPhaseFuncs → DoodadFuncCoffer
        doodad.Use(_character, 0);

        // Assert — the phase chain opened the storage (OpenedBy bound) + client got the contents packet
        await Assert.That(coffer.OpenedBy).IsEqualTo(_character);
        await Assert.That(_capturedPackets.Select(PacketOpcode)).Contains(SCOffsets.SCCofferContentsUpdatePacket);
    }

    [Test]
    public async Task House_AddVisibleObject_SendsClientVisibleStatePackets()
    {
        // Arrange — house with attached furniture doodad
        var house = new House
        {
            Id = 9003,
            ObjId = 0xC003,
            TemplateId = 6003,
            Name = "Test House 3",
            OwnerId = _character.Id,
            AccountId = _character.AccountId
        };
        house.Template = new HousingTemplate { Id = 6003, MainModelId = 1, HousingBindingDoodad = [] };
        house.CurrentStep = -1;
        _world.AddObject(house);

        var doodad = DoodadManager.Instance.Create(_world, 0, FurnitureTemplateId, house, skipPhaseInitialization: true);
        _world.AddObject(doodad);
        house.AttachedDoodads.Add(doodad);

        // Act — real client-visible state path
        house.AddVisibleObject(_character);

        // Assert — the client receives house + doodad state packets
        var opcodes = _capturedPackets.Select(PacketOpcode).ToList();
        await Assert.That(opcodes).Contains(SCOffsets.SCUnitStatePacket);       // 0x69
        await Assert.That(opcodes).Contains(SCOffsets.SCHouseStatePacket);      // 0xbc
        await Assert.That(opcodes).Contains(SCOffsets.SCDoodadsCreatedPacket);  // 0x112
    }
}
