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
        public bool LeaveHeadlessCharacter(Character expected, AAEmu.Game.Core.Managers.CharacterLifecycleReason reason,
            out bool savedThisCall, Func<bool>? releaseCachedSession = null) => throw new NotSupportedException("not used by this rig");
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

    /// <summary>
    /// Minimal activatable module so the rig can drive a REAL
    /// <see cref="BotGoalArbiter"/> and produce the route-ownership fact the
    /// service reads (plan T8). <see cref="CanWork"/> flips to false to end the
    /// work leg; the arbiter then reports no active activity.
    /// </summary>
    private sealed class FakeModule(string name, int priority, string activityName) : IBotActivityModule
    {
        public string Name { get; } = name;
        public int Priority { get; } = priority;
        public bool CanWork { get; set; } = true;

        public BotActivityDecision CanActivate(BotActivityContext context) =>
            CanWork ? BotActivityDecision.Allow(activityName) : BotActivityDecision.Deny("work finished");

        public BotActivity Activate(BotActivityContext context) => new(activityName, Name);
    }

    private sealed class Rig
    {
        public required FakeBotManager Manager { get; init; }
        public required FakeTickManager Ticker { get; init; }

        /// <summary>The behavior the service was built with (recording fake unless a test supplied a real one).</summary>
        public required IBotScheduleBehavior Behavior { get; init; }

        /// <summary>The recording fake behind <see cref="Behavior"/> — every rig except the real-behavior one.</summary>
        public RecordingBehavior Recording => (RecordingBehavior)Behavior;

        public required Dictionary<uint, PlayerBotMetadata> Metadata { get; init; }
        public required BotScheduleService Service { get; init; }

        private float _hour = 12f;
        public float Hour
        {
            get => _hour;
            set => _hour = value;
        }
    }

    private static Rig CreateRig(BotScheduleOptions? options = null, float hour = 12f,
        Lazy<IBotGoalArbiter>? arbiter = null, IBotScheduleBehavior? behavior = null)
    {
        SeedFixtureSingletons();
        var manager = new FakeBotManager();
        var ticker = new FakeTickManager();
        var recording = new RecordingBehavior();
        var metadata = new Dictionary<uint, PlayerBotMetadata>();
        // The hour provider closes over the rig being built here — evaluated
        // lazily per tick, never during construction.
        Rig rig = null!;
        rig = new Rig
        {
            Manager = manager,
            Ticker = ticker,
            Behavior = behavior ?? recording,
            Metadata = metadata,
            Service = new BotScheduleService(
                manager,
                options ?? Options(),
                ticker,
                behavior ?? recording,
                gameHourProvider: () => rig.Hour,
                metadataProvider: id => metadata.TryGetValue(id, out var meta)
                    ? meta
                    : PlayerBotMetadata.Empty(id),
                scheduleWriter: (id, json) => metadata[id] = metadata.TryGetValue(id, out var meta)
                    ? meta with { Schedule = json }
                    : PlayerBotMetadata.Empty(id) with { Schedule = json },
                arbiter: arbiter)
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

        await Assert.That(rig.Recording.Roams).IsEmpty();
        await Assert.That(rig.Recording.Moves).IsEmpty();
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
        await Assert.That(rig.Recording.Moves.Count).IsEqualTo(1);
        await Assert.That(rig.Recording.Moves[0].BotId).IsEqualTo(3002u);
        await Assert.That(rig.Recording.Moves[0].Target).IsEqualTo(home);
    }

    [Test]
    public async Task WorkPhase_BotResumesNormalRoam()
    {
        var rig = CreateRig(hour: 12f);
        var runtime = AddActiveBot(rig, 3003, homeAt: new Vector3(1f, 2f, 3f),
            scheduleJson: "{\"kind\":\"roam-loop\",\"radius\":25,\"phase\":3,\"home\":[10,20,5]}");

        rig.Service.RunTick();

        await Assert.That(rig.Service.SnapshotPhases()[3003]).IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(rig.Recording.Roams.Count).IsEqualTo(1);
        await Assert.That(rig.Recording.Roams[0].BotId).IsEqualTo(runtime.CharacterId);
        await Assert.That(rig.Recording.Roams[0].FallbackCenter).IsEqualTo(new Vector3(1f, 2f, 3f));
    }

    [Test]
    public async Task TravelMorningLeg_TargetsTheWorkAnchor()
    {
        var rig = CreateRig(hour: 7.75f); // inside the morning leg (< 30 game-min to work)
        AddActiveBot(rig, 3004, homeAt: new Vector3(1f, 1f, 1f),
            scheduleJson: "{\"kind\":\"roam-loop\",\"home\":[500,600,7]}");

        rig.Service.RunTick();

        await Assert.That(rig.Service.SnapshotPhases()[3004]).IsEqualTo(BotSchedulePhase.Travel);
        await Assert.That(rig.Recording.Moves.Single().Target).IsEqualTo(new Vector3(500f, 600f, 7f));
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
        await Assert.That(rig.Recording.Moves.Count).IsEqualTo(1);
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
            rig.Recording,
            gameHourProvider: () => rig.Hour,
            metadataProvider: id => rig.Metadata[id],
            scheduleWriter: (id, json) => rig.Metadata[id] = rig.Metadata[id] with { Schedule = json },
            authoritativeScheduleService: rig.Service);

        var decision = module.CanActivate(new BotActivityContext { Bot = runtime, GameHour = rig.Hour });
        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("BotScheduleService owns");

        rig.Service.RunTick();

        await Assert.That(rig.Recording.Moves.Count).IsEqualTo(1);
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
        await Assert.That(rig.Recording.Moves).IsEmpty();
        await Assert.That(rig.Service.SnapshotPhases()).DoesNotContainKey(3006u);
    }

    // ---------------------------------------------------------------- one writer per route (plan T8)

    [Test]
    public async Task PhaseChange_WhileElectedWorkOwnsTheRoute_Defers_ThenLandsWhenTheWorkEnds()
    {
        var work = new FakeModule("Farm", 55, "farm.work");
        var arbiter = new BotGoalArbiter([work]);
        var rig = CreateRig(arbiter: new Lazy<IBotGoalArbiter>(() => arbiter));
        var runtime = AddActiveBot(rig, 4101, homeAt: new Vector3(10f, 20f, 30f));

        // The bot is running an elected work leg: the arbiter's live activity
        // owns the route — this is the authority the service reads.
        arbiter.Arbitrate(runtime, gameHour: 12f);
        await Assert.That(arbiter.GetRouteOwner(runtime.CharacterId)).IsEqualTo("farm.work");

        // Night falls (rest 22-06) while that leg is still in force: the
        // transition must not be applied, not be persisted, and not advance the
        // phase memory (so it is retried instead of being lost).
        rig.Hour = 23f;
        rig.Service.RunTick();

        await Assert.That(rig.Recording.Moves, "the elected route must survive the phase change").IsEmpty();
        await Assert.That(rig.Recording.Roams).IsEmpty();
        await Assert.That(rig.Service.TransitionCount).IsEqualTo(0);
        await Assert.That(rig.Metadata[runtime.CharacterId].Schedule, "the deferred phase must not be persisted")
            .IsEqualTo(string.Empty);
        await Assert.That(rig.Service.SnapshotPhases(), "the applied phase must stay where it was").IsEmpty();
        await Assert.That(rig.Service.DeferralCount).IsEqualTo(1);
        await Assert.That(rig.Service.SnapshotDeferrals().TryGetValue(runtime.CharacterId, out var pending)).IsTrue();
        await Assert.That(pending!.Pending).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(pending.Owner).IsEqualTo("farm.work");

        // A second scan with the same work still holding the route: still
        // deferred, but not counted as a new episode.
        rig.Service.RunTick();
        await Assert.That(rig.Service.DeferralCount).IsEqualTo(1);
        await Assert.That(rig.Recording.Moves).IsEmpty();

        // The leg ends: the arbiter drops the activity and the SAME transition
        // lands on the next scan.
        work.CanWork = false;
        arbiter.Arbitrate(runtime, gameHour: 23f);
        await Assert.That(arbiter.GetRouteOwner(runtime.CharacterId)).IsNull();

        rig.Service.RunTick();

        await Assert.That(rig.Recording.Moves.Count).IsEqualTo(1);
        await Assert.That(rig.Recording.Moves[0].Target).IsEqualTo(new Vector3(10f, 20f, 30f));
        await Assert.That(rig.Service.TransitionCount).IsEqualTo(1);
        await Assert.That(rig.Service.DeferralCount, "the episode is not re-counted when it lands").IsEqualTo(1);
        await Assert.That(rig.Service.SnapshotDeferrals()).IsEmpty();
        await Assert.That(rig.Service.SnapshotPhases()[runtime.CharacterId]).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(rig.Metadata[runtime.CharacterId].Schedule).Contains("\"lastPhase\":\"Rest\"");
    }

    [Test]
    public async Task DeferredTransition_WhoseWindowClosesFirst_IsDroppedNotQueued()
    {
        var work = new FakeModule("Farm", 55, "farm.work");
        var arbiter = new BotGoalArbiter([work]);
        var rig = CreateRig(hour: 10f, arbiter: new Lazy<IBotGoalArbiter>(() => arbiter));
        var runtime = AddActiveBot(rig, 4108, homeAt: new Vector3(10f, 20f, 30f));

        // Daytime with nobody elected: the work phase applies, so the bot's
        // phase memory now says Work.
        rig.Service.RunTick();
        await Assert.That(rig.Recording.Roams.Count).IsEqualTo(1);
        await Assert.That(rig.Service.SnapshotPhases()[runtime.CharacterId]).IsEqualTo(BotSchedulePhase.Work);

        // An elected leg owns the route and night falls (rest 22-06): the
        // transition is deferred, not applied.
        arbiter.Arbitrate(runtime, gameHour: 12f);
        rig.Hour = 23f;
        rig.Service.RunTick();
        await Assert.That(rig.Service.SnapshotDeferrals()[runtime.CharacterId].Pending)
            .IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(rig.Service.DeferralCount).IsEqualTo(1);

        // The rest window closes before the leg ends. 06:05 is inside the
        // hysteresis window past RestEnd, so the resolver holds the phase the
        // bot is already in: the deferral resolves back to it and is DROPPED,
        // not queued — a stale Rest at dawn must not run.
        rig.Hour = 6.05f;
        rig.Service.RunTick();

        await Assert.That(rig.Service.SnapshotDeferrals(), "the closed-window deferral is dropped").IsEmpty();
        await Assert.That(rig.Service.DeferralCount, "the dropped episode stays counted").IsEqualTo(1);
        await Assert.That(rig.Service.SnapshotPhases()[runtime.CharacterId], "the applied phase stays Work")
            .IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(rig.Recording.Moves, "no stale rest leg runs").IsEmpty();
        await Assert.That(rig.Recording.Roams.Count, "no new work leg is armed either").IsEqualTo(1);

        // Nothing was queued behind the drop: the work still owns the route.
        await Assert.That(arbiter.GetRouteOwner(runtime.CharacterId)).IsEqualTo("farm.work");
    }

    [Test]
    public async Task PhaseChange_WhileElectedWorkOwnsTheRoute_LeavesTheArmedRouteInPlace()
    {
        var work = new FakeModule("Farm", 55, "farm.work");
        var arbiter = new BotGoalArbiter([work]);
        var executor = new BotRoamStepExecutor();
        var rig = CreateRig(
            arbiter: new Lazy<IBotGoalArbiter>(() => arbiter),
            behavior: new RoamRouteScheduleBehavior(executor));
        var runtime = AddActiveBot(rig, 4102, homeAt: new Vector3(10f, 20f, 30f));

        // The work leg armed its route through the same entry point the farm
        // loop uses.
        var armed = BotPath.PathTo(new Vector3(41f, 42f, 43f));
        executor.SetRoamRoute(runtime.Character, armed);
        arbiter.Arbitrate(runtime, gameHour: 12f);

        rig.Hour = 23f; // Rest would walk home through the same behavior seam
        rig.Service.RunTick();

        await Assert.That(executor.GetRoamRoute(runtime.CharacterId), "the elected route survives the phase change")
            .IsSameReferenceAs(armed);

        // Once the work ends the same transition lands and takes the route.
        work.CanWork = false;
        arbiter.Arbitrate(runtime, gameHour: 23f);
        rig.Service.RunTick();

        var replaced = executor.GetRoamRoute(runtime.CharacterId);
        await Assert.That(replaced).IsNotNull();
        await Assert.That(replaced, "the schedule's route takes over once nothing elected owns the route")
            .IsNotSameReferenceAs(armed);
        await Assert.That(replaced!.CurrentTarget).IsEqualTo(new Vector3(10f, 20f, 30f));
    }

    [Test]
    public async Task PhaseChange_NoElectedWork_AppliesAsBefore()
    {
        // An arbiter that is registered but has nothing in force for this bot.
        var arbiter = new BotGoalArbiter([]);
        var rig = CreateRig(arbiter: new Lazy<IBotGoalArbiter>(() => arbiter));
        AddActiveBot(rig, 4103, homeAt: new Vector3(10f, 20f, 30f));

        rig.Hour = 23f;
        rig.Service.RunTick();

        await Assert.That(rig.Recording.Moves.Count).IsEqualTo(1);
        await Assert.That(rig.Service.TransitionCount).IsEqualTo(1);
        await Assert.That(rig.Service.DeferralCount).IsEqualTo(0);
        await Assert.That(rig.Service.SnapshotDeferrals()).IsEmpty();
    }

    [Test]
    public async Task PhaseChange_WhenIdleOrTheScheduleOwnsTheRoute_Applies()
    {
        // Idle: a route-less bot (idle clears the route instead of owning one).
        var idle = new FakeModule("Idle", 0, IdleActivityModule.ActivityName);
        var idleArbiter = new BotGoalArbiter([idle]);
        var idleRig = CreateRig(arbiter: new Lazy<IBotGoalArbiter>(() => idleArbiter));
        var idleBot = AddActiveBot(idleRig, 4104, homeAt: new Vector3(10f, 20f, 30f));
        idleArbiter.Arbitrate(idleBot, gameHour: 12f);
        await Assert.That(idleArbiter.GetRouteOwner(idleBot.CharacterId)).IsNull();

        idleRig.Hour = 23f;
        idleRig.Service.RunTick();

        await Assert.That(idleRig.Recording.Moves.Count).IsEqualTo(1);
        await Assert.That(idleRig.Service.DeferralCount).IsEqualTo(0);

        // The schedule layer's own phase: that route IS the schedule's.
        var schedule = new FakeModule("Schedules", 100, "schedule.work");
        var scheduleArbiter = new BotGoalArbiter([schedule]);
        var scheduleRig = CreateRig(arbiter: new Lazy<IBotGoalArbiter>(() => scheduleArbiter));
        var scheduleBot = AddActiveBot(scheduleRig, 4105, homeAt: new Vector3(10f, 20f, 30f));
        scheduleArbiter.Arbitrate(scheduleBot, gameHour: 12f);
        await Assert.That(scheduleArbiter.GetRouteOwner(scheduleBot.CharacterId)).IsNull();

        scheduleRig.Hour = 23f;
        scheduleRig.Service.RunTick();

        await Assert.That(scheduleRig.Recording.Moves.Count).IsEqualTo(1);
        await Assert.That(scheduleRig.Service.DeferralCount).IsEqualTo(0);
    }

    [Test]
    public async Task GameLoopTick_WhileElectedWorkOwnsTheRoute_Defers()
    {
        var work = new FakeModule("Farm", 55, "farm.work");
        var arbiter = new BotGoalArbiter([work]);
        var rig = CreateRig(hour: 23f, arbiter: new Lazy<IBotGoalArbiter>(() => arbiter));
        var runtime = AddActiveBot(rig, 4107, homeAt: new Vector3(10f, 20f, 30f));
        arbiter.Arbitrate(runtime, gameHour: 12f);

        // The REAL schedule path: Start() subscribes the scan to the game-loop
        // tick and the tick — not a direct RunTick call — drives the deferral.
        await Assert.That(rig.Service.Start()).IsTrue();
        rig.Ticker.OnTick.Invoke();

        await Assert.That(rig.Recording.Moves, "the elected route survives the tick").IsEmpty();
        await Assert.That(rig.Service.TransitionCount).IsEqualTo(0);
        await Assert.That(rig.Service.DeferralCount).IsEqualTo(1);
        await Assert.That(rig.Service.SnapshotDeferrals()[runtime.CharacterId].Owner).IsEqualTo("farm.work");

        await rig.Service.StopAsync();
    }

    [Test]
    public async Task NoArbiterWired_KeepsTheUnconditionalBehavior()
    {
        // Hermetic rigs (and a host that never registered the arbiter wrapper)
        // have no ownership authority: the phase applies as it did before T8.
        var rig = CreateRig();
        AddActiveBot(rig, 4106, homeAt: new Vector3(10f, 20f, 30f));

        rig.Hour = 23f;
        rig.Service.RunTick();

        await Assert.That(rig.Recording.Moves.Count).IsEqualTo(1);
        await Assert.That(rig.Service.TransitionCount).IsEqualTo(1);
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
        await Assert.That(rig.Recording.Moves).IsEmpty();
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
