using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.World;

/// <summary>
/// H1 (P0 gate): Region activity split — bot characters must NOT wake the NPC world.
///
/// Mechanism under test (Region.cs): AddObject/RemoveObject increment/decrement
/// Region._playerCount on the region and its neighbors; HasPlayerActivity() = _playerCount > 0,
/// consumed by NpcAi.Tick, AreaTriggerManager, SphereQuestManager, spawner radius,
/// AddToCharacters ShouldTick and region visibility.
///
/// Before H1 ANY Character woke all six consumers (spec §7 confirmed). After H1 only humans
/// count by default; bots count only when they explicitly opt in at Full fidelity via
/// AddBotActivity/RemoveBotActivity.
/// </summary>
[NotInParallel] // seeds the shared WorldManager singleton — same convention as NpcMoveTowardsTests
public class RegionActivityTests
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

    private static WorldInstance TestWorld() => WorldManager.Instance.GetWorld(TestInstanceId);

    /// <summary>Pre-creates the 3x3 region block around (cx, cy) so AddObject wakes existing neighbors.</summary>
    private static void CreateRegionBlock(int cx, int cy)
    {
        for (var x = cx - 1; x <= cx + 1; x++)
        for (var y = cy - 1; y <= cy + 1; y++)
            TestWorld().GetRegion(x, y);
    }

    private static Character CreateCharacter(string name, uint objId, bool isBot)
    {
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = objId,
            Name = name,
            IsPlayerBot = isBot
        };
        // Inside region (4,4): REGION_SIZE = 64
        character.Transform.Local.SetPosition(4 * WorldManager.REGION_SIZE + 1, 4 * WorldManager.REGION_SIZE + 1, 0);
        return character;
    }

    #region Human baseline (must stay byte-identical)

    [Test]
    public async Task AddObject_HumanCharacter_CountsAsRegionActivity()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var human = CreateCharacter("human-1", 1001, isBot: false);

        region.AddObject(human);

        // Self + every existing neighbor region reports activity (3x3 wake)
        await Assert.That(region.HasPlayerActivity()).IsTrue();
        await Assert.That(TestWorld().GetRegion(3, 3).HasPlayerActivity()).IsTrue();
        await Assert.That(TestWorld().GetRegion(5, 5).HasPlayerActivity()).IsTrue();
    }

    [Test]
    public async Task RemoveObject_HumanCharacter_StopsCountingActivity()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var human = CreateCharacter("human-2", 1002, isBot: false);
        region.AddObject(human);
        await Assert.That(region.HasPlayerActivity()).IsTrue();

        region.RemoveObject(human);

        await Assert.That(region.HasPlayerActivity()).IsFalse();
        await Assert.That(TestWorld().GetRegion(3, 3).HasPlayerActivity()).IsFalse();
    }

    #endregion

    #region Bot exclusion (fail-before: bots wake the world today)

    [Test]
    public async Task AddObject_BotCharacter_DoesNotCountAsRegionActivity()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-1", 2001, isBot: true);

        region.AddObject(bot);

        await Assert.That(region.HasPlayerActivity()).IsFalse();
        await Assert.That(TestWorld().GetRegion(3, 3).HasPlayerActivity()).IsFalse();
    }

    [Test]
    public async Task AddObject_DormantBot_DoesNotCountAsRegionActivity()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-dormant", 2002, isBot: true);
        bot.BotFidelity = BotFidelity.Dormant;

        region.AddObject(bot);

        await Assert.That(region.HasPlayerActivity()).IsFalse();
    }

    [Test]
    public async Task AddObject_ReducedBot_DoesNotCountAsRegionActivity()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-reduced", 2003, isBot: true);
        bot.BotFidelity = BotFidelity.Reduced;

        region.AddObject(bot);

        await Assert.That(region.HasPlayerActivity()).IsFalse();
    }

    [Test]
    public async Task AddObject_FullBot_DoesNotCountUnlessOptedIn()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-full", 2004, isBot: true);
        bot.BotFidelity = BotFidelity.Full;

        region.AddObject(bot);

        await Assert.That(region.HasPlayerActivity()).IsFalse();
    }

    [Test]
    public async Task RemoveObject_BotCharacter_LeavesActivityQuiet()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-2", 2005, isBot: true);
        bot.BotFidelity = BotFidelity.Full;
        region.AddObject(bot);

        region.RemoveObject(bot);

        // Bots must never leave a wake behind (Add + Remove must balance to zero)
        await Assert.That(region.HasPlayerActivity()).IsFalse();
    }

    #endregion

    #region Bot activity opt-in (AddBotActivity/RemoveBotActivity — explicit wake)

    [Test]
    public async Task AddBotActivity_FullBot_WakesRegionActivity()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-full-optin", 2006, isBot: true);
        bot.BotFidelity = BotFidelity.Full;
        region.AddObject(bot);
        await Assert.That(region.HasPlayerActivity()).IsFalse();

        var granted = region.AddBotActivity(bot);

        await Assert.That(granted).IsTrue();
        await Assert.That(region.HasPlayerActivity()).IsTrue();
        await Assert.That(TestWorld().GetRegion(3, 3).HasPlayerActivity()).IsTrue();
    }

    [Test]
    public async Task RemoveBotActivity_FullBot_StopsWakingRegionActivity()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-full-optout", 2007, isBot: true);
        bot.BotFidelity = BotFidelity.Full;
        region.AddObject(bot);
        region.AddBotActivity(bot);
        await Assert.That(region.HasPlayerActivity()).IsTrue();

        var revoked = region.RemoveBotActivity(bot);

        await Assert.That(revoked).IsTrue();
        await Assert.That(region.HasPlayerActivity()).IsFalse();
        await Assert.That(TestWorld().GetRegion(3, 3).HasPlayerActivity()).IsFalse();
    }

    [Test]
    public async Task AddBotActivity_ReducedBot_Rejected()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-reduced-optin", 2008, isBot: true);
        bot.BotFidelity = BotFidelity.Reduced;
        region.AddObject(bot);

        var granted = region.AddBotActivity(bot);

        await Assert.That(granted).IsFalse();
        await Assert.That(region.HasPlayerActivity()).IsFalse();
    }

    [Test]
    public async Task AddBotActivity_IsIdempotent_SingleGrantPerCharacter()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-idempotent", 2009, isBot: true);
        bot.BotFidelity = BotFidelity.Full;
        region.AddObject(bot);

        await Assert.That(region.AddBotActivity(bot)).IsTrue();
        // Second grant must not double-count — one revoke must fully silence the region
        await Assert.That(region.AddBotActivity(bot)).IsFalse();

        region.RemoveBotActivity(bot);

        await Assert.That(region.HasPlayerActivity()).IsFalse();
    }

    [Test]
    public async Task RemoveObject_OptedInBot_AutoRevokesActivityGrant()
    {
        CreateRegionBlock(4, 4);
        var region = TestWorld().GetRegion(4, 4);
        var bot = CreateCharacter("bot-auto-revoke", 2010, isBot: true);
        bot.BotFidelity = BotFidelity.Full;
        region.AddObject(bot);
        region.AddBotActivity(bot);
        await Assert.That(region.HasPlayerActivity()).IsTrue();

        region.RemoveObject(bot);

        // The grant is scoped to the bot's presence — leaving the region/world must not leak a wake
        await Assert.That(region.HasPlayerActivity()).IsFalse();
    }

    #endregion

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
