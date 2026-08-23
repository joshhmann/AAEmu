using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Containers;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Rig for the pre-LLM chatter service (ROADMAP M8.5a): recording fakes for
/// the registry/sink/tick seams, HeadlessSession characters (the suite
/// convention — ordinary Character records, no DB rows), FakeTimeProvider for
/// deterministic cooldown expiry.
/// </summary>
[NotInParallel]
public class BotChatterServiceTests
{
    private sealed class FakeBotManager : IPlayerBotManager
    {
        public List<PlayerBotRuntime> Active { get; } = [];

        public IReadOnlyList<PlayerBotRuntime> GetActive() => Active;
        public bool Spawn(Character character, string owner) => true;
        public bool Activate(uint characterId, object? botContext, string owner) => true;
        public bool Deactivate(uint characterId, string reason) => true;
        public bool TryGet(uint characterId, out PlayerBotRuntime? runtime)
        {
            runtime = null;
            return false;
        }

        public bool Remove(uint characterId) => true;
        public IReadOnlyList<PlayerBotRuntime> GetAll() => Active;
        public int Count => Active.Count;
        public int ActiveCount => Active.Count;
        public PlayerBotDiagnostics GetDiagnostics() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class RecordingSink : IBotChatterSink
    {
        public List<(Character Speaker, string Message)> Lines { get; } = [];

        public void Say(Character speaker, string message) => Lines.Add((speaker, message));
    }

    private sealed class ExplodingSink : IBotChatterSink
    {
        public int Attempts { get; private set; }
        public int Explosions { get; private set; }

        public void Say(Character speaker, string message)
        {
            Attempts++;
            Explosions++;
            throw new InvalidOperationException("sink exploded (rig)");
        }
    }

    private sealed class FakeTickManager : ITickManager
    {
        public TickManager.TickEventHandler OnTick { get; } = new();

        public void Stop() { }

        // ITickManager : IInitializable — no-op for the fake.
        public void Initialize() { }
    }

    /// <summary>
    /// Test rig: per-bot proximity lists are mutable so each test decides who
    /// stands within the greeting radius of whom.
    /// </summary>
    private sealed class Rig
    {
        public required FakeBotManager Manager { get; init; }
        public required RecordingSink Sink { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public required FakeTickManager Ticker { get; init; }
        public required Dictionary<uint, IReadOnlyList<Character>> NearbyByBot { get; init; }
        public required BotChatterService Service { get; init; }
    }

    private static Rig CreateRig(BotChatterOptions options, IBotChatterSink? sink = null,
        string personality = "cheerful")
    {
        SeedFixtureSingletons();
        var manager = new FakeBotManager();
        var recordingSink = new RecordingSink();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-20T12:00:00Z"));
        var ticker = new FakeTickManager();
        var nearbyByBot = new Dictionary<uint, IReadOnlyList<Character>>();

        var service = new BotChatterService(
            manager,
            options,
            sink ?? recordingSink,
            nearbyResolver: bot => nearbyByBot.TryGetValue(bot.Id, out var list)
                ? list
                : Array.Empty<Character>(),
            zoneNameResolver: _ => "Solzreed",
            personalityResolver: _ => personality,
            tickManager: ticker,
            timeProvider: clock);

        return new Rig
        {
            Manager = manager,
            Sink = recordingSink,
            Clock = clock,
            Ticker = ticker,
            NearbyByBot = nearbyByBot,
            Service = service
        };
    }

    private static BotChatterOptions Options(bool enabled = true,
        TimeSpan? perBotCooldown = null, TimeSpan? pairCooldown = null, int zoneBudget = 10)
        => new()
        {
            Enabled = enabled,
            GreetingRadius = 15f,
            PerBotCooldown = perBotCooldown ?? TimeSpan.FromSeconds(90),
            PairCooldown = pairCooldown ?? TimeSpan.FromMinutes(10),
            ZoneMessagesPerMinute = zoneBudget,
            ScanInterval = TimeSpan.FromSeconds(2)
        };

    private static PlayerBotRuntime MakeActiveBot(uint botId, string name)
    {
        var bot = HeadlessSession.Create(botId, name, 10).Character;
        return new PlayerBotRuntime(bot, "chatter-rig") { State = PlayerBotState.Active };
    }

    [Test]
    public async Task RunScan_NearbyHumanWithinRadius_GreetsOnceThroughSink()
    {
        var rig = CreateRig(Options());
        var bot = MakeActiveBot(1001, "Citizen01");
        var human = HeadlessSession.Create(2001, "Human01", 10).Character;
        rig.Manager.Active.Add(bot);
        rig.NearbyByBot[bot.Character.Id] = [human];

        rig.Service.RunScan();

        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(1);
        await Assert.That(rig.Sink.Lines[0].Speaker.Id).IsEqualTo(bot.Character.Id);
        await Assert.That(rig.Sink.Lines[0].Message).Contains(human.Name);
        await Assert.That(rig.Sink.Lines[0].Message).Contains("Solzreed");
    }

    [Test]
    public async Task RunScan_TemplateSubstitution_FillsRealValues_NoRawTokens()
    {
        var rig = CreateRig(Options(), personality: "cheerful");
        var bot = MakeActiveBot(1002, "Citizen02");
        var human = HeadlessSession.Create(2002, "Human02", 10).Character;
        rig.Manager.Active.Add(bot);
        rig.NearbyByBot[bot.Character.Id] = [human];

        rig.Service.RunScan();

        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(1);
        var expectedTemplate = BotChatterTemplates.PickLine("cheerful", bot.Character.Id, human.Id);
        var expected = BotChatterTemplates.Substitute(expectedTemplate, bot.Character.Name, human.Name, "Solzreed");
        await Assert.That(rig.Sink.Lines[0].Message).IsEqualTo(expected);
        await Assert.That(rig.Sink.Lines[0].Message).DoesNotContain("{name}");
        await Assert.That(rig.Sink.Lines[0].Message).DoesNotContain("{target}");
        await Assert.That(rig.Sink.Lines[0].Message).DoesNotContain("{zone}");
    }

    [Test]
    public async Task RunScan_PerBotCooldown_SuppressesSecondLine_UntilExpired()
    {
        var rig = CreateRig(Options(perBotCooldown: TimeSpan.FromSeconds(90)));
        var bot = MakeActiveBot(1003, "Citizen03");
        var human = HeadlessSession.Create(2003, "Human03", 10).Character;
        rig.Manager.Active.Add(bot);
        rig.NearbyByBot[bot.Character.Id] = [human];

        rig.Service.RunScan();
        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(1);

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Service.RunScan();
        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(1); // still within 90s quiet

        // Past the per-bot cooldown AND the pair cooldown → the bot speaks again.
        rig.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(91));
        rig.Service.RunScan();
        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RunScan_PairCooldown_SuppressesSameTarget_AfterBotCooldownExpires()
    {
        var rig = CreateRig(Options(perBotCooldown: TimeSpan.FromSeconds(90), pairCooldown: TimeSpan.FromMinutes(10)));
        var bot = MakeActiveBot(1004, "Citizen04");
        var human = HeadlessSession.Create(2004, "Human04", 10).Character;
        rig.Manager.Active.Add(bot);
        rig.NearbyByBot[bot.Character.Id] = [human];

        rig.Service.RunScan();
        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(1);

        rig.Clock.Advance(TimeSpan.FromSeconds(91)); // bot quiet again, pair NOT
        rig.Service.RunScan();
        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RunScan_ZoneBudget_CapsMessagesPerMinute()
    {
        // Two bots in the SAME zone, each with an eligible target; the budget
        // of one line per minute caps the whole scan at ONE send.
        var rig = CreateRig(Options(zoneBudget: 1));
        var botA = MakeActiveBot(1005, "Citizen05");
        var botB = MakeActiveBot(1006, "Citizen06");
        var humanA = HeadlessSession.Create(2005, "Human05", 10).Character;
        var humanB = HeadlessSession.Create(2006, "Human06", 10).Character;
        rig.Manager.Active.Add(botA);
        rig.Manager.Active.Add(botB);
        rig.NearbyByBot[botA.Character.Id] = [humanA];
        rig.NearbyByBot[botB.Character.Id] = [humanB];

        rig.Service.RunScan();

        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(1);

        // The budget window rolls over after a minute → the other bot speaks.
        rig.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(91));
        rig.Service.RunScan();
        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RunScan_Combat_SuppressesChatter_BotAndTarget()
    {
        // Bot in combat → silent.
        var combatRig = CreateRig(Options());
        var combatBot = MakeActiveBot(1007, "Citizen07");
        var human1 = HeadlessSession.Create(2007, "Human07", 10).Character;
        combatBot.Character.IsInBattle = true;
        combatRig.Manager.Active.Add(combatBot);
        combatRig.NearbyByBot[combatBot.Character.Id] = [human1];
        combatRig.Service.RunScan();
        await Assert.That(combatRig.Sink.Lines.Count).IsEqualTo(0);

        // Target in combat → silent.
        var targetRig = CreateRig(Options());
        var bot = MakeActiveBot(1008, "Citizen08");
        var busyHuman = HeadlessSession.Create(2008, "Human08", 10).Character;
        busyHuman.IsInBattle = true;
        targetRig.Manager.Active.Add(bot);
        targetRig.NearbyByBot[bot.Character.Id] = [busyHuman];
        targetRig.Service.RunScan();
        await Assert.That(targetRig.Sink.Lines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DisabledByDefault_ServiceInert_StartRefusesAndNoLinesSent()
    {
        var rig = CreateRig(Options(enabled: false));
        var bot = MakeActiveBot(1009, "Citizen09");
        var human = HeadlessSession.Create(2009, "Human09", 10).Character;
        rig.Manager.Active.Add(bot);
        rig.NearbyByBot[bot.Character.Id] = [human];

        var started = rig.Service.Start();
        rig.Service.RunScan();

        await Assert.That(started).IsFalse();
        await Assert.That(rig.Service.IsRunning).IsFalse();
        await Assert.That(rig.Ticker.OnTick.SnapshotMetrics().SubscriberCount).IsEqualTo(0);
        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Start_Enabled_SubscribesGameLoopTick_StopUnsubscribes()
    {
        var rig = CreateRig(Options());

        await Assert.That(rig.Service.Start()).IsTrue();
        await Assert.That(rig.Service.IsRunning).IsTrue();

        // Subscribe/UnSubscribe are deferred until the next handler Invoke.
        rig.Ticker.OnTick.Invoke();
        await Assert.That(rig.Ticker.OnTick.SnapshotMetrics().SubscriberCount).IsEqualTo(1);

        await rig.Service.StopAsync();
        rig.Ticker.OnTick.Invoke();
        await Assert.That(rig.Ticker.OnTick.SnapshotMetrics().SubscriberCount).IsEqualTo(0);
    }

    [Test]
    public async Task SendFailure_SuppressesRestOfTick_NeverThrows()
    {
        // EVERY send explodes: a scan with two eligible bots must stop at the
        // first failure and swallow the exception.
        var rig = CreateRig(Options(zoneBudget: 100), sink: new ExplodingSink());
        var botA = MakeActiveBot(1010, "Citizen10");
        var botB = MakeActiveBot(1011, "Citizen11");
        var humanA = HeadlessSession.Create(2010, "Human10", 10).Character;
        var humanB = HeadlessSession.Create(2011, "Human11", 10).Character;
        rig.Manager.Active.Add(botA);
        rig.Manager.Active.Add(botB);
        rig.NearbyByBot[botA.Character.Id] = [humanA];
        rig.NearbyByBot[botB.Character.Id] = [humanB];

        rig.Service.RunScan(); // must not throw

        await Assert.That(rig.Sink.Lines.Count).IsEqualTo(0); // recording sink untouched
    }

    [Test]
    public async Task Templates_AllArchetypes_SubstitutionAndDeterminism()
    {
        foreach (var archetype in BotChatterTemplates.Archetypes)
        {
            var lines = BotChatterTemplates.GetLines(archetype);
            await Assert.That(lines.Count).IsGreaterThanOrEqualTo(4);

            foreach (var line in lines)
            {
                var filled = BotChatterTemplates.Substitute(line, "Bot", "Target", "Zone");
                await Assert.That(filled).DoesNotContain("{name}");
                await Assert.That(filled).DoesNotContain("{target}");
                await Assert.That(filled).DoesNotContain("{zone}");
            }
        }

        // Deterministic pick: same (archetype, bot, target) → same line.
        var first = BotChatterTemplates.PickLine("guard", 42u, 99u);
        var second = BotChatterTemplates.PickLine("guard", 42u, 99u);
        await Assert.That(first).IsEqualTo(second);

        // Personality resolution: recorded personality wins; junk falls back deterministically.
        await Assert.That(BotChatterTemplates.ResolveArchetype("a greedy merchant", 1u)).IsEqualTo("greedy");
        await Assert.That(BotChatterTemplates.ResolveArchetype("lawful", 7u)).IsEqualTo("lawful");
        await Assert.That(BotChatterTemplates.ResolveArchetype(null, 5u))
            .IsEqualTo(BotChatterTemplates.ResolveArchetype("", 5u));
    }

    // -- fixture singletons (HeadlessSession convention, see
    // BotPresenceCoordinatorTests t_302b67bf / t_4f11a519) --

    private static void SeedFixtureSingletons()
    {
        SetSingletonIfMissing(typeof(Singleton<ItemManager>), BuildFixtureItemManager());
        ContainerIdManager.Instance.Initialize(false);
    }

    private static ItemManager BuildFixtureItemManager()
    {
        var itemManager = new ItemManager(
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemIdManager>().Object,
            Mock.Of<IContainerIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object);

        var containerField = typeof(ItemManager).GetField("_allPersistentContainers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var existing = containerField?.GetValue(itemManager)
            as System.Collections.Concurrent.ConcurrentDictionary<ulong, ItemContainer>;
        if (existing == null)
            containerField?.SetValue(itemManager,
                new System.Collections.Concurrent.ConcurrentDictionary<ulong, ItemContainer>());

        return itemManager;
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) != null)
            return;
        field.SetValue(null, instance);
    }
}
