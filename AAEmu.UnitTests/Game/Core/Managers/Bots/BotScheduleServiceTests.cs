using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Rig for the C1 BotScheduleService engine (M8/G4). Recording fakes for
/// the registry/behavior/tick/metadata seams (the BotChatterServiceTests
/// convention), plus one round-trip through the REAL PlayerBotMetadataStore
/// singleton (DB-less: cache + dirty semantics) to lock the persistence
/// contract. Disabled-by-default inertness mirrors the chatter gate tests.
/// </summary>
[NotInParallel]
public class BotScheduleServiceTests
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

    private sealed class FakeTickManager : ITickManager
    {
        public TickManager.TickEventHandler OnTick { get; } = new();

        public void Stop() { }

        // ITickManager : IInitializable — no-op for the fake.
        public void Initialize() { }
    }

    private sealed class RecordingBehavior : IBotScheduleBehavior
    {
        public List<(uint BotId, Vector3 FallbackCenter)> Roams { get; } = [];
        public List<(uint BotId, Vector3 Target)> Moves { get; } = [];

        public void ResumeRoam(PlayerBotRuntime bot, string scheduleJson, Vector3 fallbackCenter) =>
            Roams.Add((bot.CharacterId, fallbackCenter));

        public void MoveToAnchor(PlayerBotRuntime bot, Vector3 target) =>
            Moves.Add((bot.CharacterId, target));
    }

    private sealed class Rig
    {
        public required FakeBotManager Manager { get; init; }
        public required FakeTickManager Ticker { get; init; }
        public required RecordingBehavior Behavior { get; init; }
        public required Dictionary<uint, PlayerBotMetadata> Metadata { get; init; }
        public required BotScheduleService Service { get; init; }

        private float _hour = 12f;
        public float Hour
        {
            get => _hour;
            set => _hour = value;
        }
    }

    private static Rig CreateRig(BotScheduleOptions? options = null, float hour = 12f)
    {
        SeedFixtureSingletons();
        var manager = new FakeBotManager();
        var ticker = new FakeTickManager();
        var behavior = new RecordingBehavior();
        var metadata = new Dictionary<uint, PlayerBotMetadata>();
        // The hour provider closes over the rig being built here — evaluated
        // lazily per tick, never during construction.
        Rig rig = null!;
        rig = new Rig
        {
            Manager = manager,
            Ticker = ticker,
            Behavior = behavior,
            Metadata = metadata,
            Service = new BotScheduleService(
                manager,
                options ?? Options(),
                ticker,
                behavior,
                gameHourProvider: () => rig.Hour,
                metadataProvider: id => metadata.TryGetValue(id, out var meta)
                    ? meta
                    : PlayerBotMetadata.Empty(id),
                scheduleWriter: (id, json) => metadata[id] = metadata.TryGetValue(id, out var meta)
                    ? meta with { Schedule = json }
                    : PlayerBotMetadata.Empty(id) with { Schedule = json })
        };
        rig.Hour = hour;
        return rig;
    }

    private static BotScheduleOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled,
        ScanInterval = TimeSpan.FromSeconds(30)
    };

    /// <summary>Adds an active embodied bot; when homeAt is set the metadata carries a recorded home.</summary>
    private static PlayerBotRuntime AddActiveBot(Rig rig, uint botId, Vector3? homeAt = null, string? scheduleJson = null)
    {
        var bot = HeadlessSession.Create(botId, $"Citizen{botId % 1000:D3}", 10).Character;
        var runtime = new PlayerBotRuntime(bot, "schedule-rig") { State = PlayerBotState.Active };
        rig.Manager.Active.Add(runtime);

        if (homeAt is { } home || scheduleJson != null)
        {
            var metadata = rig.Metadata.TryGetValue(botId, out var existing)
                ? existing
                : PlayerBotMetadata.Empty(botId);
            if (homeAt is { } position)
            {
                metadata.HasHome = true;
                metadata.HomeX = position.X;
                metadata.HomeY = position.Y;
                metadata.HomeZ = position.Z;
            }

            if (scheduleJson != null)
                metadata.Schedule = scheduleJson;
            rig.Metadata[botId] = metadata;
        }

        return runtime;
    }

    // ---------------------------------------------------------------- disabled by default

    [Test]
    public async Task DisabledByDefault_StartRefuses_RunTickInert()
    {
        var rig = CreateRig(Options(enabled: false));
        AddActiveBot(rig, 3001, homeAt: new Vector3(10f, 20f, 30f));

        await Assert.That(rig.Service.Start()).IsFalse();
        await Assert.That(rig.Service.IsRunning).IsFalse();
        await Assert.That(rig.Ticker.OnTick.SnapshotMetrics().SubscriberCount).IsEqualTo(0);

        rig.Service.RunTick(); // must not resolve, apply, or persist anything

        await Assert.That(rig.Behavior.Roams).IsEmpty();
        await Assert.That(rig.Behavior.Moves).IsEmpty();
        foreach (var metadata in rig.Metadata.Values)
            await Assert.That(metadata.Schedule).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Start_Enabled_SubscribesGameLoopTick_StopUnsubscribes()
    {
        var rig = CreateRig();

        await Assert.That(rig.Service.Start()).IsTrue();
        await Assert.That(rig.Service.IsRunning).IsTrue();

        rig.Ticker.OnTick.Invoke(); // deferred subscribe applies on next Invoke
        await Assert.That(rig.Ticker.OnTick.SnapshotMetrics().SubscriberCount).IsEqualTo(1);

        await rig.Service.StopAsync();
        rig.Ticker.OnTick.Invoke();
        await Assert.That(rig.Ticker.OnTick.SnapshotMetrics().SubscriberCount).IsEqualTo(0);
    }

    // ---------------------------------------------------------------- phase-driven visible behavior

    [Test]
    public async Task RestPhase_BotWalksHomeAndIdles()
    {
        var rig = CreateRig(hour: 23f); // deep in the rest window (22-06)
        var home = new Vector3(19950f, 20050f, 100f);
        AddActiveBot(rig, 3002, homeAt: home);

        rig.Service.RunTick();

        await Assert.That(rig.Service.TransitionCount).IsEqualTo(1);
        await Assert.That(rig.Service.SnapshotPhases()[3002]).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(rig.Behavior.Moves.Count).IsEqualTo(1);
        await Assert.That(rig.Behavior.Moves[0].BotId).IsEqualTo(3002u);
        await Assert.That(rig.Behavior.Moves[0].Target).IsEqualTo(home);
    }

    [Test]
    public async Task WorkPhase_BotResumesNormalRoam()
    {
        var rig = CreateRig(hour: 12f);
        var runtime = AddActiveBot(rig, 3003, homeAt: new Vector3(1f, 2f, 3f),
            scheduleJson: "{\"kind\":\"roam-loop\",\"radius\":25,\"phase\":3,\"home\":[10,20,5]}");

        rig.Service.RunTick();

        await Assert.That(rig.Service.SnapshotPhases()[3003]).IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(rig.Behavior.Roams.Count).IsEqualTo(1);
        await Assert.That(rig.Behavior.Roams[0].BotId).IsEqualTo(runtime.CharacterId);
        await Assert.That(rig.Behavior.Roams[0].FallbackCenter).IsEqualTo(new Vector3(1f, 2f, 3f));
    }

    [Test]
    public async Task TravelMorningLeg_TargetsTheWorkAnchor()
    {
        var rig = CreateRig(hour: 7.75f); // inside the morning leg (< 30 game-min to work)
        AddActiveBot(rig, 3004, homeAt: new Vector3(1f, 1f, 1f),
            scheduleJson: "{\"kind\":\"roam-loop\",\"home\":[500,600,7]}");

        rig.Service.RunTick();

        await Assert.That(rig.Service.SnapshotPhases()[3004]).IsEqualTo(BotSchedulePhase.Travel);
        await Assert.That(rig.Behavior.Moves.Single().Target).IsEqualTo(new Vector3(500f, 600f, 7f));
    }

    [Test]
    public async Task SteadyState_SecondTickAppliesNothingNew()
    {
        var rig = CreateRig(hour: 23f);
        AddActiveBot(rig, 3005, homeAt: new Vector3(4f, 5f, 6f));

        rig.Service.RunTick();
        rig.Service.RunTick();
        rig.Service.RunTick();

        await Assert.That(rig.Service.TransitionCount).IsEqualTo(1); // logged/applied ONCE
        await Assert.That(rig.Behavior.Moves.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SchedulePhaseModule_DefersToAuthoritativeService()
    {
        var rig = CreateRig(hour: 23f);
        const uint botId = 3009;
        var character = new Character(new UnitCustomModelParams())
        {
            Id = botId,
            Name = "schedule-owner",
            MaxHp = 100,
            Hp = 100
        };
        var runtime = new PlayerBotRuntime(character, "schedule-rig")
        {
            State = PlayerBotState.Active
        };
        rig.Manager.Active.Add(runtime);
        rig.Metadata[botId] = PlayerBotMetadata.Empty(botId) with
        {
            HasHome = true,
            HomeX = 10f,
            HomeY = 20f,
            HomeZ = 30f
        };

        var module = new SchedulePhaseActivityModule(
            rig.Service.Options,
            rig.Behavior,
            gameHourProvider: () => rig.Hour,
            metadataProvider: id => rig.Metadata[id],
            scheduleWriter: (id, json) => rig.Metadata[id] = rig.Metadata[id] with { Schedule = json },
            authoritativeScheduleService: rig.Service);

        var decision = module.CanActivate(new BotActivityContext { Bot = runtime, GameHour = rig.Hour });
        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("BotScheduleService owns");

        rig.Service.RunTick();

        await Assert.That(rig.Behavior.Moves.Count).IsEqualTo(1);
        await Assert.That(rig.Service.TransitionCount).IsEqualTo(1);
    }

    [Test]
    public async Task CombatBots_AreSkipped()
    {
        var rig = CreateRig(hour: 23f);
        var runtime = AddActiveBot(rig, 3006, homeAt: new Vector3(4f, 5f, 6f));
        runtime.Character.IsInBattle = true;

        rig.Service.RunTick();

        await Assert.That(rig.Service.TransitionCount).IsEqualTo(0);
        await Assert.That(rig.Behavior.Moves).IsEmpty();
        await Assert.That(rig.Service.SnapshotPhases()).DoesNotContainKey(3006u);
    }

    // ---------------------------------------------------------------- B4-shape compatibility

    [Test]
    public async Task OldRowWithoutAnchors_RunsTemplateAnchors_AndKeepsBaseKeysOnWrite()
    {
        // A pre-C1 row: plain roam-loop descriptor, no anchors key at all.
        const string legacyJson =
            "{\"kind\":\"roam-loop\",\"waypoints\":8,\"radius\":30,\"phase\":0,\"loop\":true," +
            "\"home\":[19950,20050,100]}";
        var rig = CreateRig(hour: 12f);
        AddActiveBot(rig, 3007, homeAt: new Vector3(9f, 9f, 9f), scheduleJson: legacyJson);

        rig.Service.RunTick(); // must not throw; template anchors → midday Work

        await Assert.That(rig.Service.SnapshotPhases()[3007]).IsEqualTo(BotSchedulePhase.Work);
        var written = rig.Metadata[3007].Schedule;
        await Assert.That(written).Contains("\"kind\":\"roam-loop\""); // base keys preserved verbatim
        await Assert.That(written).Contains("\"anchors\"");           // extensions appended additively
        await Assert.That(written).Contains("\"lastPhase\":\"Work\"");
    }

    [Test]
    public async Task PersistedLastPhase_IsHonoredAcrossRestart_WithoutAFlap()
    {
        // Restart continuity: a row persisted lastPhase=Rest; right after the
        // RestEnd boundary (inside hysteresis) the resolver HOLDS Rest — no
        // transition, no write, no movement spam on boot.
        var storedWithRest = BotSchedulePayload.WithRuntimeState(
            "{\"kind\":\"roam-loop\",\"home\":[1,2,3]}",
            new BotDailyAnchors { RestStart = 22f, RestEnd = 6f }, BotSchedulePhase.Rest);
        var rig = CreateRig(hour: 6.05f);
        AddActiveBot(rig, 3008, homeAt: new Vector3(1f, 1f, 1f), scheduleJson: storedWithRest);

        rig.Service.RunTick();

        await Assert.That(rig.Service.TransitionCount).IsEqualTo(0);
        await Assert.That(rig.Service.SnapshotPhases()[3008]).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(rig.Behavior.Moves).IsEmpty();
    }

    // ---------------------------------------------------------------- persistence (real store, DB-less)

    [Test]
    public async Task PersistenceRoundTrip_AnchorsAndLastPhaseLandInTheMetadataStore()
    {
        const uint botId = 310000001u; // unique per test: process-wide singleton store
        var store = PlayerBotMetadataStore.Instance;
        store.RecordHome(botId, 1u, 283u, 19950f, 20050f, 100f);
        store.RecordSchedule(botId, "{\"kind\":\"roam-loop\",\"radius\":30,\"phase\":1}");

        var manager = new FakeBotManager();
        var bot = HeadlessSession.Create(botId, "CitizenPersist", 10).Character;
        manager.Active.Add(new PlayerBotRuntime(bot, "schedule-rig") { State = PlayerBotState.Active });

        var service = new BotScheduleService(
            manager,
            Options(),
            new FakeTickManager(),
            new RecordingBehavior(),
            gameHourProvider: () => 23f); // rest window → transition on first tick

        service.RunTick();

        var persisted = store.GetForRead(botId).Schedule;
        await Assert.That(persisted).Contains("\"kind\":\"roam-loop\"");   // B4 descriptor intact
        await Assert.That(persisted).Contains("\"anchors\"");              // anchors persisted
        await Assert.That(persisted).Contains("\"lastPhase\":\"Rest\"");   // last phase persisted
        // Write-through failed (hermetic gate has no MySQL) → dirty for SaveManager.
        await Assert.That(store.IsDirty(botId)).IsTrue();

        // Round trip: the persisted payload re-reads as the same state.
        await Assert.That(BotSchedulePayload.TryReadLastPhase(persisted, out var phase)).IsTrue();
        await Assert.That(phase).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(BotSchedulePayload.TryReadAnchors(persisted, out _)).IsTrue();
    }

    // ---------------------------------------------------------------- fixture singletons (HeadlessSession convention)

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
