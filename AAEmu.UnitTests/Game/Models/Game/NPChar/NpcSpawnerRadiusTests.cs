using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// H1 (P0 gate): NpcSpawner.IsPlayerInSpawnRadius must not wake for reduced-fidelity bots.
/// Humans and full-fidelity bots still count toward the spawn radius (spawner wake);
/// Dormant/Reduced bots are invisible to it (review corollary: NpcSpawner.cs:470-484).
/// </summary>
[NotInParallel] // seeds the shared WorldManager singleton — same convention as NpcMoveTowardsTests
public class NpcSpawnerRadiusTests
{
    private const uint TestWorldId = 1;
    private const uint TestInstanceId = 1;
    private const string TestWorldName = "test_world";

    private object _previousWorldManagerInstance;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManagerInstance = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        SeedWorldManager();
    }

    [After(Test)]
    public void TearDown()
    {
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousWorldManagerInstance);
    }

    private static NpcSpawner CreateSpawner(uint spawnerId, float x, float y, float radius)
    {
        return new NpcSpawner
        {
            SpawnerId = spawnerId,
            Id = spawnerId,
            Position = new WorldSpawnPosition { X = x, Y = y, Z = 0 },
            Template = new NpcSpawnerTemplate { TestRadiusPc = radius, TestRadiusNpc = radius }
        };
    }

    private static Character CreateCharacter(string name, uint objId, float x, float y, bool isBot, BotFidelity fidelity)
    {
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = objId,
            Name = name,
            IsPlayerBot = isBot,
            BotFidelity = fidelity
        };
        character.Transform.Local.SetPosition(x, y, 0);
        WorldManager.Instance.TryAddCharacter(character);
        return character;
    }

    [Test]
    public async Task IsPlayerInSpawnRadius_HumanInsideRadius_ReportsTrue()
    {
        var spawner = CreateSpawner(1, 100f, 100f, 2f);
        CreateCharacter("human", 1001, 100f, 100f, isBot: false, BotFidelity.Dormant);

        await Assert.That(spawner.IsPlayerInSpawnRadius()).IsTrue();
    }

    [Test]
    public async Task IsPlayerInSpawnRadius_ReducedBotInsideRadius_ReportsFalse()
    {
        var spawner = CreateSpawner(2, 100f, 100f, 2f);
        CreateCharacter("reduced-bot", 2001, 100f, 100f, isBot: true, BotFidelity.Reduced);

        await Assert.That(spawner.IsPlayerInSpawnRadius()).IsFalse();
    }

    [Test]
    public async Task IsPlayerInSpawnRadius_DormantBotInsideRadius_ReportsFalse()
    {
        var spawner = CreateSpawner(3, 100f, 100f, 2f);
        CreateCharacter("dormant-bot", 2002, 100f, 100f, isBot: true, BotFidelity.Dormant);

        await Assert.That(spawner.IsPlayerInSpawnRadius()).IsFalse();
    }

    [Test]
    public async Task IsPlayerInSpawnRadius_FullBotInsideRadius_ReportsTrue()
    {
        var spawner = CreateSpawner(4, 100f, 100f, 2f);
        CreateCharacter("full-bot", 2003, 100f, 100f, isBot: true, BotFidelity.Full);

        await Assert.That(spawner.IsPlayerInSpawnRadius()).IsTrue();
    }

    [Test]
    public async Task IsPlayerInSpawnRadius_OnlyReducedBotsInRadius_ReportsFalse()
    {
        var spawner = CreateSpawner(5, 100f, 100f, 2f);
        CreateCharacter("reduced-bot-a", 2004, 100f, 100f, isBot: true, BotFidelity.Reduced);
        CreateCharacter("reduced-bot-b", 2005, 100f, 100f, isBot: true, BotFidelity.Reduced);

        await Assert.That(spawner.IsPlayerInSpawnRadius()).IsFalse();
    }

    [Test]
    public async Task IsPlayerInSpawnRadius_HumanAndReducedBotInRadius_ReportsTrue()
    {
        var spawner = CreateSpawner(6, 100f, 100f, 2f);
        CreateCharacter("reduced-bot", 2006, 100f, 100f, isBot: true, BotFidelity.Reduced);
        CreateCharacter("human", 1002, 100f, 100f, isBot: false, BotFidelity.Dormant);

        await Assert.That(spawner.IsPlayerInSpawnRadius()).IsTrue();
    }

    #region Rig helpers

    private static void SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        var template = new WorldTemplate
        {
            Id = TestWorldId,
            Name = TestWorldName,
            CellX = 2,
            CellY = 2,
            ZoneKeys = [],
            ZoneKeyByRegions = new uint[2 * WorldManager.SECTORS_PER_CELL, 2 * WorldManager.SECTORS_PER_CELL]
        };

        var worldInstance = new WorldInstance(template, 0, false, TestInstanceId)
        {
            Regions = new Region[2 * WorldManager.SECTORS_PER_CELL, 2 * WorldManager.SECTORS_PER_CELL]
        };

        worldManager.WorldTemplates[TestWorldName] = template;
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [TestInstanceId] = worldInstance });
        SetField(worldManager, "_worldIdByZoneKey", new Dictionary<uint, uint>());

        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, worldManager);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    #endregion
}
