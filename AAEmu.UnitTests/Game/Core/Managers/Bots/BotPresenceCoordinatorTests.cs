using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Containers;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Rig for the PRESENCE PROOF coordinator (integration card t_6bad0654):
/// wires the proven pieces into one living loop — provision → spawn →
/// activate → fidelity Full → roam route → wake. Loop pieces are hand-rolled
/// recording fakes (the suite's convention — TUnit.Mocks, no Moq); the
/// provisioner is injected so no DB/MySQL is touched (it uses the E2E-fixture
/// HeadlessSession.Create, which is DB-row-less).
/// </summary>
[NotInParallel]
public class BotPresenceCoordinatorTests
{
    private sealed class RecordingManager : IPlayerBotManager
    {
        public int SpawnCalls;
        public int ActivateCalls;
        public bool SpawnResult = true;
        public bool ActivateResult = true;

        public bool Spawn(Character character, string owner)
        {
            SpawnCalls++;
            return SpawnResult;
        }

        public bool Activate(uint characterId, object? botContext, string owner)
        {
            ActivateCalls++;
            return ActivateResult;
        }

        public bool Deactivate(uint characterId, string reason) => true;
        public bool TryGet(uint characterId, out PlayerBotRuntime? runtime)
        {
            runtime = null;
            return false;
        }

        public bool Remove(uint characterId) => true;
        public IReadOnlyList<PlayerBotRuntime> GetAll() => [];
        public IReadOnlyList<PlayerBotRuntime> GetActive() => [];

        public int Count => 0;
        public int ActiveCount => 0;
        public PlayerBotDiagnostics GetDiagnostics() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
        public IReadOnlyList<PlayerBotRuntime> Runtimes => [];
    }

    private sealed class RecordingScheduler : IPlayerBotScheduler
    {
        public int StartCalls;
        public List<uint> Wakes { get; } = [];

        public void Start() => StartCalls++;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool Wake(uint characterId)
        {
            Wakes.Add(characterId);
            return true;
        }

        public bool WakeAt(uint characterId, DateTime utcDue) => true;
        public bool WakeAfter(uint characterId, TimeSpan delay) => true;
        public bool IsLeased(uint characterId) => false;
        public PlayerBotSchedulerMetrics GetMetrics() => new(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0);
        public int WorkerCount => 4;
        public bool IsRunning => StartCalls > 0;
    }

    private sealed class RecordingDirector : IPopulationDirector
    {
        public int Embodied = 0;
        public int FidelityCalls;
        public FidelityTransitionResult Result = FidelityTransitionResult.Applied;

        public BotFidelity GetFidelity(uint characterId) => BotFidelity.Dormant;
        public FidelityTransitionResult TrySetFidelity(uint characterId, BotFidelity target, string reason)
        {
            FidelityCalls++;
            return Result;
        }

        public ServerPressure RefreshPressure() => ServerPressure.Healthy;
        public ServerPressure Pressure => ServerPressure.Healthy;
        public FidelityTransitionResult Wake(uint characterId, string reason) => FidelityTransitionResult.Applied;
        public FidelityTransitionResult Sleep(uint characterId, string reason) => FidelityTransitionResult.Applied;
        public int EmbodiedCount => Embodied;
        public int EmbodiedInZone(uint zoneId) => Embodied;
        public int EmbodiedOnActivity(string activity) => Embodied;
        public PopulationDirectorMetrics GetMetrics() => new(0, 0, 0, ServerPressure.Healthy, 0, 0, 0, 0, 0);
    }

    private static (BotPresenceCoordinator Coordinator,
        RecordingManager Manager,
        RecordingScheduler Scheduler,
        RecordingDirector Director,
        List<string> Provisioned) CreateRig()
    {
        // HeadlessSession.Create builds an ordinary Character (Inventory ctor
        // resolves ItemManager.Instance + ContainerIdManager.Instance) — seed
        // missing-only, exactly like HeadlessSessionProvisioningTests
        // (t_302b67bf); NEVER call PlayerbotPilotRig.SeedPilotSingletons()
        // (one-shot s_seeded flag flips full-suite ordering, t_4f11a519).
        SeedFixtureSingletons();
        var manager = new RecordingManager();
        var scheduler = new RecordingScheduler();
        var director = new RecordingDirector();
        var stepExecutor = new BotRoamStepExecutor(); // real — SetRoamRoute is a plain recorder

        var provisioned = new List<string>();
        var provisioner = (string username, string name, Race race, Gender gender, byte level) =>
        {
            provisioned.Add(name);
            var id = (uint)(100 + provisioned.Count);
            return HeadlessSession.Create(id, name, level, race);
        };

        var coordinator = new BotPresenceCoordinator(
            manager, scheduler, director, stepExecutor,
            _ => new Vector3(15578f, 15382f, 126f),
            provisioner);

        return (coordinator, manager, scheduler, director, provisioned);
    }

    private static BotPresenceCoordinator.BotPresenceConfig Config(int botCount = 3)
        => new(
            BotCount: botCount, ZoneId: 9, HomePosition: default, RoamRadius: 30f,
            RoamSpeed: 2.5f, Level: 5, NamePrefix: "Citizen", AccountPrefix: "presence");

    [Test]
    public async Task Start_ProvisionsBots_SpawnsActivates_AndSetsFullFidelity()
    {
        var (coordinator, manager, scheduler, director, provisioned) = CreateRig();

        var result = coordinator.Start(Config(3));

        await Assert.That(result).IsTrue();
        await Assert.That(provisioned.Count).IsEqualTo(3);
        await Assert.That(manager.SpawnCalls).IsEqualTo(3);
        await Assert.That(manager.ActivateCalls).IsEqualTo(3);
        await Assert.That(director.FidelityCalls).IsEqualTo(6); // Reduced + Full per bot
        await Assert.That(scheduler.StartCalls).IsEqualTo(1);
        await Assert.That(scheduler.Wakes.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Start_AlreadyEmbodied_IsIdempotent_NoReprovision()
    {
        var (coordinator, manager, scheduler, director, provisioned) = CreateRig();
        director.Embodied = 3; // bots already up

        var result = coordinator.Start(Config(3));

        await Assert.That(result).IsTrue();
        await Assert.That(provisioned.Count).IsEqualTo(0);
        await Assert.That(manager.SpawnCalls).IsEqualTo(0);
        await Assert.That(scheduler.StartCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Start_SecondBoot_WithAdoptingProvisioner_EmbodiesAllBotsAgain()
    {
        // Restart semantics (t_db5b2be7): a game-container restart brings a
        // FRESH coordinator (EmbodiedCount=0) back against character rows the
        // first boot created. The provisioner simulates the production adopt
        // path — same name → SAME character id (reloaded row), never a new
        // id — and the coordinator must come up 3/3 again, exactly like the
        // first boot, with no duplicate-row provisioning.
        SeedFixtureSingletons();
        var manager = new RecordingManager();
        var scheduler = new RecordingScheduler();
        var director = new RecordingDirector();
        var stepExecutor = new BotRoamStepExecutor(); // real — SetRoamRoute is a plain recorder
        var ids = new Dictionary<string, uint>();
        var provisioned = new List<string>();
        var provisioner = (string username, string name, Race race, Gender gender, byte level) =>
        {
            if (!ids.TryGetValue(name, out var id))
                ids[name] = id = (uint)(500 + ids.Count + 1); // fresh rows on first boot only
            provisioned.Add(name);
            return HeadlessSession.Create(id, name, level, race);
        };
        var coordinator = new BotPresenceCoordinator(
            manager, scheduler, director, stepExecutor,
            _ => new Vector3(15578f, 15382f, 126f),
            provisioner);

        // First boot: fresh provision — 3 distinct character rows.
        await Assert.That(coordinator.Start(Config(3))).IsTrue();
        await Assert.That(ids.Count).IsEqualTo(3);

        // Restart: fresh process state (Embodied=0), rows still exist → the
        // provisioner adopts (same ids). The coordinator still embodies 3/3.
        director.Embodied = 0;
        await Assert.That(coordinator.Start(Config(3))).IsTrue();

        await Assert.That(provisioned.Count).IsEqualTo(6); // 3 fresh + 3 adopted
        await Assert.That(ids.Count).IsEqualTo(3);         // adoption never allocates new rows
        await Assert.That(manager.SpawnCalls).IsEqualTo(6);
        await Assert.That(manager.ActivateCalls).IsEqualTo(6);
    }

    [Test]
    public async Task Start_ActivationFailure_SkipsBot_ContinuesOthers()
    {
        var (coordinator, manager, _, _, provisioned) = CreateRig();
        manager.ActivateResult = false; // all fail

        var result = coordinator.Start(Config(3));

        // All three were provisioned (rows created), none activated → the
        // demo reports failure (no embodied citizens).
        await Assert.That(result).IsFalse();
        await Assert.That(provisioned.Count).IsEqualTo(3);
        await Assert.That(manager.SpawnCalls).IsEqualTo(3);
        await Assert.That(manager.ActivateCalls).IsEqualTo(3);
    }

    [Test]
    public async Task BuildRoamRoute_AllWaypointsWithinRadius()
    {
        var home = new Vector3(15578f, 15382f, 126f);
        var radius = 30f;

        var route = BotPresenceCoordinator.BuildRoamRoute(home, radius, seed: 1);

        await Assert.That(route.Waypoints.Count).IsEqualTo(8);
        await Assert.That(route.Mode).IsEqualTo(BotPath.LoopMode.Loop);
        await Assert.That(route.AllWaypointsWithin(home, radius)).IsTrue();
    }

    [Test]
    public async Task BuildRoamRoute_SeedsDiffer_SoBotsDontSync()
    {
        var home = new Vector3(100f, 100f, 10f);

        var routeA = BotPresenceCoordinator.BuildRoamRoute(home, 30f, seed: 0);
        var routeB = BotPresenceCoordinator.BuildRoamRoute(home, 30f, seed: 1);

        await Assert.That(routeA.Waypoints[0]).IsNotEqualTo(routeB.Waypoints[0]);
    }

    // ---------------------------------------------------------------- singleton seeding

    /// <summary>
    /// Seeds exactly the singletons HeadlessSession.Create resolves
    /// (Inventory ctor → ContainerIdManager.Instance.GetNextId +
    /// ItemManager.GetItemContainerForCharacter) with missing-only guards —
    /// the HeadlessSessionProvisioningTests convention (t_302b67bf). Never
    /// replaces an established singleton (t_4f11a519).
    /// </summary>
    private static void SeedFixtureSingletons()
    {
        SetSingletonIfMissing(typeof(Singleton<ItemManager>), BuildFixtureItemManager());
        // Fail-closed on missing MySQL (logged, empty used ids), then serves
        // incrementing ids from its range — same call the pilot rig makes.
        // NO forceReset: re-initializing an established ContainerIdManager
        // resets the id counter while _allPersistentContainers still holds
        // keys from earlier tests → duplicate-key 65536 (full-suite failure,
        // t_6bad0654). Initialize(false) is a no-op when already initialized.
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

        // The Inventory ctor resolves ItemManager.GetItemContainerForCharacter,
        // which iterates _allPersistentContainers. Scenario-rig ItemManagers
        // never seed it — a null registry would NRE the Character ctor.
        var containerField = typeof(ItemManager).GetField("_allPersistentContainers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var existing = containerField?.GetValue(itemManager) as Dictionary<ulong, ItemContainer>;
        if (existing == null)
            containerField?.SetValue(itemManager, new Dictionary<ulong, ItemContainer>());

        return itemManager;
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) != null)
            return; // never replace an established singleton (t_4f11a519)
        field.SetValue(null, instance);
    }
}
