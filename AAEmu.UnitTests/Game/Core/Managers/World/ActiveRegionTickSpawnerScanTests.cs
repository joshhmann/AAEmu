using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

/// <summary>
/// A5 ActiveRegionTick remediation regression rig — the tick must reuse ONE
/// character snapshot for the spawner radius scan instead of deep-copying
/// GetAllSpawners() and re-enumerating GetAllCharacters() per spawner.
///
/// Behavioral seam (no implementation-source assertions): pre-remediation the
/// tick activated spawners via the parameterless
/// <see cref="NpcSpawner.IsPlayerInSpawnRadius()"/>, which consulted the GLOBAL
/// <see cref="WorldManager.Instance"/> character pool on every spawner. The
/// remediation passes the tick's own snapshot into
/// <see cref="SpawnManager.GetActiveNpcSpawners"/> /
/// <see cref="NpcSpawner.IsPlayerInSpawnRadius(IReadOnlyList{Character})"/>.
/// So: seed the singleton with a decoy character INSIDE a spawner's radius,
/// run the tick with an EMPTY snapshot, and assert no spawner is activated —
/// the empty snapshot must be authoritative. Under the old code this test
/// fails (SpawnersTotal == 1 via the singleton decoy).
///
/// Observable contract defended:
/// - no-player path: empty snapshot ⇒ zero active spawners, zero spawner
///   updates, zero characters ticked, no exception, and the new
///   CharacterSnapshotMs / SpawnerScanMs metrics are recorded;
/// - active-player path: the in-radius spawner is still scanned, activated and
///   updated, and the round-robin character tick is unchanged.
///
/// Limitation: the test does NOT assert a zero-cost spawner scan — the
/// implementation still scans O(spawners) per pass (GetActiveNpcSpawners
/// iterates the spawner dictionary). It asserts the snapshot-reuse contract:
/// the singleton character pool is not consulted for radius checks, and the
/// per-spawner GetAllCharacters() re-enumeration is gone (observable only
/// through the decoy discriminator above; the GetAllSpawners() deep-copy
/// elimination has no metric seam and is not asserted directly).
/// </summary>
[NotInParallel] // seeds the shared WorldManager/GameScheduleManager singletons — same convention as NpcLineOfSightTests
[ParallelLimiter<ActiveRegionTickSpawnerScanSequentialParallelLimit>] // t_f3700374 pattern: within-class tests share the seeded singletons — must not run in parallel
public class ActiveRegionTickSpawnerScanTests
{
    private object _previousWorldManager;
    private object _previousGameScheduleManager;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManager = GetSingletonInstance(typeof(Singleton<WorldManager>));
        _previousGameScheduleManager = GetSingletonInstance(typeof(Singleton<GameScheduleManager>));
    }

    [After(Test)]
    public void TearDown()
    {
        SetSingletonInstance(typeof(Singleton<WorldManager>), _previousWorldManager);
        SetSingletonInstance(typeof(Singleton<GameScheduleManager>), _previousGameScheduleManager);
    }

    [Test]
    public async Task ActiveRegionTick_NoPlayers_EmptySnapshotSkipsSpawnerActivation()
    {
        // Arrange — empty character snapshot, but the WorldManager singleton
        // holds a decoy character INSIDE the first spawner's radius. The world
        // has one in-radius spawner, one out-of-radius spawner and one
        // null-template spawner (skipped by the scan).
        var (manager, _) = CreateManager(characterCount: 0);
        var inRadius = CreateSpawner(1, x: 0, y: 0, z: 0);
        var outOfRadius = CreateSpawner(2, x: 1000, y: 1000, z: 1000);
        var nullTemplate = new NpcSpawner
        {
            SpawnerId = 3,
            UnitId = 3,
            NpcSpawnerIds = [3],
            Position = new WorldSpawnPosition { X = 0, Y = 0, Z = 0 },
            Template = null
        };
        var world = CreateWorldWithSpawners(inRadius, outOfRadius, nullTemplate);
        SetPrivateField(manager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });

        var decoy = new Character(new UnitCustomModelParams());
        decoy.Transform.Local.SetPosition(0, 0, 0);
        SetSingletonInstance(typeof(Singleton<WorldManager>), CreateSingletonManagerWithCharacter(decoy));

        var regionTick = GetRegionTickMethod();

        // Act — must complete without exception (a throw here fails the test)
        regionTick.Invoke(manager, [TimeSpan.FromSeconds(1)]);

        // Assert — the empty snapshot is authoritative: no spawner is activated
        // even though the singleton decoy sits inside the first spawner's radius.
        var stats = manager.RegionTickStats;
        await Assert.That(stats.CharactersTotal).IsEqualTo(0);
        await Assert.That(stats.CharactersProcessed).IsEqualTo(0);
        await Assert.That(stats.SpawnersTotal).IsEqualTo(0)
            .Because("an empty character snapshot must not activate spawners via the WorldManager singleton (snapshot-reuse contract)");
        await Assert.That(stats.SpawnersProcessed).IsEqualTo(0);
        await Assert.That(stats.CharacterSnapshotMs).IsGreaterThanOrEqualTo(0);
        await Assert.That(stats.SpawnerScanMs).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task ActiveRegionTick_WithPlayerInRadius_ActivatesSpawnerAndTicksCharacter()
    {
        // Arrange — one character in the snapshot, positioned inside the
        // spawner's radius. The singleton mirrors the snapshot so the spawner's
        // Update() radius re-check (parameterless IsPlayerInSpawnRadius) stays
        // consistent, and GameScheduleManager is seeded so Update() completes
        // deterministically (no schedule data → NotFound → time-window fallback).
        var (manager, characters) = CreateManager(characterCount: 1);
        var spawner = CreateSpawner(1, x: 0, y: 0, z: 0);
        var world = CreateWorldWithSpawners(spawner);
        SetPrivateField(manager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });

        var character = characters[1];
        SetSingletonInstance(typeof(Singleton<WorldManager>), CreateSingletonManagerWithCharacter(character));
        SeedGameScheduleManager();

        var regionTick = GetRegionTickMethod();

        // Act
        regionTick.Invoke(manager, [TimeSpan.FromSeconds(1)]);

        // Assert — round-robin ticks the single character and the in-radius
        // spawner is scanned, activated and updated.
        var stats = manager.RegionTickStats;
        await Assert.That(stats.CharactersTotal).IsEqualTo(1);
        await Assert.That(stats.CharactersProcessed).IsEqualTo(1);
        await Assert.That(stats.SpawnersTotal).IsEqualTo(1)
            .Because("a character inside the spawn radius must still activate the spawner");
        await Assert.That(stats.SpawnersProcessed).IsEqualTo(1);
        await Assert.That(stats.CharacterSnapshotMs).IsGreaterThanOrEqualTo(0);
        await Assert.That(stats.SpawnerScanMs).IsGreaterThanOrEqualTo(0);
    }

    #region Helpers

    private static (WorldManager Manager, ConcurrentDictionary<uint, Character> Characters) CreateManager(int characterCount)
    {
        var manager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        var characters = new ConcurrentDictionary<uint, Character>();
        for (uint i = 1; i <= characterCount; i++)
        {
            var character = new Character(new UnitCustomModelParams());
            character.Transform.Local.SetPosition(0, 0, 0);
            characters[i] = character;
        }
        SetPrivateField(manager, "_characters", characters);
        SetPrivateField(manager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>());
        return (manager, characters);
    }

    private static WorldManager CreateSingletonManagerWithCharacter(Character character)
    {
        var manager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        var characters = new ConcurrentDictionary<uint, Character> { [character.ObjId] = character };
        SetPrivateField(manager, "_characters", characters);
        SetPrivateField(manager, "_worlds", new ConcurrentDictionary<uint, WorldInstance>());
        return manager;
    }

    private static NpcSpawner CreateSpawner(uint spawnerId, float x, float y, float z)
    {
        return new NpcSpawner
        {
            SpawnerId = spawnerId,
            UnitId = spawnerId,
            NpcSpawnerIds = [spawnerId],
            Position = new WorldSpawnPosition { X = x, Y = y, Z = z },
            Template = new NpcSpawnerTemplate
            {
                Id = spawnerId,
                TestRadiusPc = 1, // 1 * 50 * 1 * 50 = 2500 squared units
                TestRadiusNpc = 3,
                MaxPopulation = 1,
                Npcs = []
            }
        };
    }

    private static WorldInstance CreateWorldWithSpawners(params NpcSpawner[] spawners)
    {
        var template = new WorldTemplate
        {
            Id = 1,
            Name = "test_world",
            ZoneKeys = new List<uint>(),
            CellX = 2,
            CellY = 2,
            ZoneKeyByRegions = new uint[32, 32]
        };
        var world = new WorldInstance(template, 0, true, 1);
        var spawnManager = new SpawnManager(world);
        world.SpawnManager = spawnManager;

        // Inject directly into the private NpcSpawners dictionary: the public
        // AddNpcSpawner path consults the global NpcGameData singleton and
        // routes event spawners into NpcEventSpawners, which the region tick
        // does not scan. Same SetPrivateField convention as the rest of the suite.
        var npcSpawners = new Dictionary<uint, List<NpcSpawner>>();
        uint index = 1;
        foreach (var spawner in spawners)
            npcSpawners[index++] = [spawner];
        SetBackingField(spawnManager, "NpcSpawners", npcSpawners);
        return world;
    }

    private static void SeedGameScheduleManager()
    {
        var gameScheduleManager = new GameScheduleManager(Mock.Of<IGameDataManager>().Object);
        SetPrivateField(gameScheduleManager, "_gameScheduleSpawnerIds", new Dictionary<int, List<int>>());
        SetSingletonInstance(typeof(Singleton<GameScheduleManager>), gameScheduleManager);
    }

    private static MethodInfo GetRegionTickMethod()
    {
        return typeof(WorldManager).GetMethod("ActiveRegionTick", BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (field == null)
            throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    private static void SetBackingField(object obj, string propertyName, object value)
    {
        var field = obj.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Backing field '<{propertyName}>k__BackingField' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    private static object GetSingletonInstance(Type singletonType)
    {
        return singletonType.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static void SetSingletonInstance(Type singletonType, object instance)
    {
        singletonType.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, instance);
    }

    #endregion
}

/// <summary>
/// Serializes this class's tests (Limit = 1) — the rig seeds and restores the
/// shared WorldManager/GameScheduleManager singletons in Before/After hooks, so
/// within-class parallelism is a data race. [NotInParallel] alone does NOT
/// serialize within a class (t_f3700374); this limiter is required.
/// </summary>
public sealed class ActiveRegionTickSpawnerScanSequentialParallelLimit : IParallelLimit
{
    public int Limit => 1;
}
