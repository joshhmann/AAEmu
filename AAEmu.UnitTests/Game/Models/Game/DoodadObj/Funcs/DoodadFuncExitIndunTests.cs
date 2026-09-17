using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj.Funcs;

/// <summary>
/// Exit-door fallback return point: GM-teleport entrants never get a
/// MainWorldPosition recorded (the enter funcs only stamp it on a normal gate
/// entry), so the exit door used to refuse them with InvalidReturnPosInstance.
/// With no recorded return the door now falls back to the character's faction
/// racial return — the same GetDistrictReturnPoint/GetRecallById path the
/// Return skill uses — and only refuses when no fallback resolves either.
/// </summary>
[NotInParallel]
public class DoodadFuncExitIndunTests
{
    private const uint TestInstanceId = 0x1D11;
    private const uint TestDistrictId = 7;
    private const uint TestReturnPointId = 42;
    private const uint TestRecallZoneId = 55;

    private static readonly Vector3 TestRecallPos = new(1000f, 2000f, 150f);
    private const float TestRecallYaw = 1.5f;

    private List<byte[]> _capturedPackets;
    private WorldInstance _world;
    private WorldInstance _mainWorld;
    private object _previousPortalManager;
    private object _previousIndunManager;
    private object _previousWorldManager;
    private object _previousZoneManager;
    private object _previousSusManager;

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

    [Before(Test)]
    public void SetUp()
    {
        _previousPortalManager = GetSingleton(typeof(Singleton<PortalManager>));
        _previousIndunManager = GetSingleton(typeof(Singleton<IndunManager>));
        _previousWorldManager = GetSingleton(typeof(Singleton<WorldManager>));
        _previousZoneManager = GetSingleton(typeof(Singleton<ZoneManager>));
        _previousSusManager = GetSingleton(typeof(Singleton<SusManager>));

        SeedWorldManager();
        SetSingleton(typeof(Singleton<SusManager>), new SusManager(WorldManager.Instance));
        SetSingleton(typeof(Singleton<ZoneManager>), NewEmptyZoneManager());
        SetSingleton(typeof(Singleton<PortalManager>), new PortalManager(
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IZoneManager>().Object,
            Mock.Of<INpcManager>().Object,
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<ITaskManager>().Object));
        SetSingleton(typeof(Singleton<IndunManager>), new IndunManager(
            Mock.Of<ITickManager>().Object,
            WorldManager.Instance,
            Mock.Of<IZoneManager>().Object,
            Mock.Of<ITeamManager>().Object));
        _world = new WorldInstance(new WorldTemplate { Id = TestInstanceId, Name = "exit-indun-test-world" }, 0, false, TestInstanceId);
        RegisterWorld(_world);
        // Production pins main_world to instance id 0 (WorldManager.Load via
        // CreateWorldInstance(..., fixedInstanceId: 0)); the fallback builds its
        // return Transform with instanceId 0 exactly like the Return skill, and
        // the Transform setter resolves the owning world through GetWorld(0).
        _mainWorld = new WorldInstance(new WorldTemplate { Id = 0, Name = "main-world-test" }, 0, false, 0);
        RegisterWorld(_mainWorld);
    }

    [After(Test)]
    public void TearDown()
    {
        // Remove exactly the entries we added (never other suites' worlds or
        // characters); the shared WorldManager registry is process-wide.
        var worlds = GetWorlds(WorldManager.PeekInstance);
        if (worlds != null)
        {
            if (worlds.TryGetValue(TestInstanceId, out var testWorld) && ReferenceEquals(testWorld, _world))
                worlds.TryRemove(TestInstanceId, out _);
            if (worlds.TryGetValue(0, out var mainWorld) && ReferenceEquals(mainWorld, _mainWorld))
                worlds.TryRemove(0, out _);
        }
        RemoveTestCharacter(0xE100);
        RemoveTestCharacter(0xE101);
        RemoveTestCharacter(0xE102);

        SetSingleton(typeof(Singleton<PortalManager>), _previousPortalManager);
        SetSingleton(typeof(Singleton<IndunManager>), _previousIndunManager);
        SetSingleton(typeof(Singleton<WorldManager>), _previousWorldManager);
        SetSingleton(typeof(Singleton<ZoneManager>), _previousZoneManager);
        SetSingleton(typeof(Singleton<SusManager>), _previousSusManager);
    }

    [Test]
    public async Task NullMainWorld_ResolvableFactionReturn_LeavesInstance()
    {
        SeedFactionReturn(TestDistrictId, FactionsEnum.Nuian, TestReturnPointId);
        var character = MakeCharacter(0xE100, "exit-fallback");
        character.Faction = new SystemFaction { Id = FactionsEnum.Nuian, Name = "Nuia" };
        character.ReturnDistrictId = TestDistrictId;
        character.MainWorldPosition = null;
        var leaveRequested = false;
        character.Events.OnDungeonLeave += (sender, args) => leaveRequested = true;

        new DoodadFuncExitIndun { ReturnPointId = 0 }.Use(character, new Doodad { ObjId = 0xD002 }, 0);

        await Assert.That(leaveRequested).IsTrue();
        await Assert.That(CapturedError(ErrorMessageType.InvalidReturnPosInstance)).IsFalse();
        await Assert.That(character.MainWorldPosition).IsNotNull();
        await Assert.That(character.MainWorldPosition.ZoneId).IsEqualTo(TestRecallZoneId);
        await Assert.That(character.MainWorldPosition.World.Position).IsEqualTo(TestRecallPos);
    }

    [Test]
    public async Task NullMainWorld_UnresolvableFactionReturn_SendsInvalidReturnError()
    {
        SeedFactionReturnNone();
        var character = MakeCharacter(0xE101, "exit-no-return");
        character.Faction = new SystemFaction { Id = FactionsEnum.Nuian, Name = "Nuia" };
        character.ReturnDistrictId = TestDistrictId;
        character.MainWorldPosition = null;
        var leaveRequested = false;
        character.Events.OnDungeonLeave += (sender, args) => leaveRequested = true;

        new DoodadFuncExitIndun { ReturnPointId = 0 }.Use(character, new Doodad { ObjId = 0xD002 }, 0);

        await Assert.That(CapturedError(ErrorMessageType.InvalidReturnPosInstance)).IsTrue();
        await Assert.That(leaveRequested).IsFalse();
        await Assert.That(character.MainWorldPosition).IsNull();
    }

    [Test]
    public async Task ExistingMainWorld_LeavesWithoutFallback()
    {
        // Original path: a recorded return leaves without consulting (or
        // disturbing) the faction fallback — portal data is empty here.
        SeedFactionReturnNone();
        var character = MakeCharacter(0xE102, "exit-recorded");
        character.Faction = new SystemFaction { Id = FactionsEnum.Nuian, Name = "Nuia" };
        character.MainWorldPosition = character.Transform.CloneDetached(character);
        var leaveRequested = false;
        character.Events.OnDungeonLeave += (sender, args) => leaveRequested = true;

        new DoodadFuncExitIndun { ReturnPointId = 0 }.Use(character, new Doodad { ObjId = 0xD002 }, 0);

        await Assert.That(leaveRequested).IsTrue();
        await Assert.That(CapturedError(ErrorMessageType.InvalidReturnPosInstance)).IsFalse();
        await Assert.That(character.MainWorldPosition.World.Position).IsEqualTo(character.Transform.World.Position);
    }

    // ================================================================ rig helpers

    private CharacterMock MakeCharacter(uint objId, string name)
    {
        var capture = new PacketCaptureSession();
        _capturedPackets = capture.CapturedPackets;
        var character = new CharacterMock { Id = objId, ObjId = objId, Name = name };
        character.ParentWorld = _world;
        character.Connection = new GameConnection(capture) { ActiveChar = character };
        _world.AddObject(character);
        return character;
    }

    private static void SeedFactionReturn(uint districtId, FactionsEnum faction, uint returnPointId)
    {
        var recall = new Portal
        {
            Id = returnPointId,
            ZoneId = TestRecallZoneId,
            X = TestRecallPos.X,
            Y = TestRecallPos.Y,
            Z = TestRecallPos.Z,
            Yaw = TestRecallYaw,
            SubZoneId = 9,
            WorldId = 1,
        };
        SetField(PortalManager.Instance, "_districtReturnPoints", new Dictionary<uint, DistrictReturnPoints>
        {
            [1] = new DistrictReturnPoints { Id = 1, DistrictId = districtId, FactionId = faction, ReturnPointId = returnPointId },
        });
        SetField(PortalManager.Instance, "_recalls", new Dictionary<uint, List<Portal>>
        {
            [recall.SubZoneId] = [recall],
        });
        SetField(PortalManager.Instance, "_recallsKey", new Dictionary<uint, uint>
        {
            [returnPointId] = recall.SubZoneId,
        });
    }

    private static void SeedFactionReturnNone()
    {
        SetField(PortalManager.Instance, "_districtReturnPoints", new Dictionary<uint, DistrictReturnPoints>());
        SetField(PortalManager.Instance, "_recalls", new Dictionary<uint, List<Portal>>());
        SetField(PortalManager.Instance, "_recallsKey", new Dictionary<uint, uint>());
    }

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

    private static void RemoveTestCharacter(uint objId)
    {
        if (WorldManager.PeekInstance == null)
            return;
        var characters = typeof(WorldManager).GetField("_characters", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(WorldManager.PeekInstance) as ConcurrentDictionary<uint, Character>;
        if (characters != null && characters.TryGetValue(objId, out var existing) && existing.Name.StartsWith("exit-", StringComparison.Ordinal))
            characters.TryRemove(objId, out _);
    }

    private static void SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        var field = typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(worldManager, new ConcurrentDictionary<uint, WorldInstance>());
        SetSingleton(typeof(Singleton<WorldManager>), worldManager);
    }

    private static ZoneManager NewEmptyZoneManager()
    {
        var zones = new ZoneManager(Mock.Of<IWorldManager>().Object);
        SetField(zones, "_zones", new Dictionary<uint, Zone>());
        SetField(zones, "_zoneIdToKey", new Dictionary<uint, uint>());
        SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>());
        return zones;
    }

    private void RegisterWorld(WorldInstance world)
    {
        GetWorlds(WorldManager.Instance)?.TryAdd(world.Id, world);
    }

    private static ConcurrentDictionary<uint, WorldInstance> GetWorlds(object worldManager)
    {
        return worldManager == null
            ? null
            : (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(worldManager);
    }

    private static object GetSingleton(Type singletonBase)
    {
        return singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);
    }

    private static void SetSingleton(Type singletonBase, object instance)
    {
        singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, instance);
    }

    private static void SetField(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);
    }
}
