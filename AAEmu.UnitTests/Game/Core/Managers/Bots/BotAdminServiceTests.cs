using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Bots;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Rig for the GM bot admin service (P1 card t_f216710e) — the testable core
/// behind /bot add|remove|list|here|go. Hand-rolled recording fakes (the
/// suite's convention — TUnit, no Moq) mirroring BotPresenceCoordinatorTests;
/// the provisioner + terrain resolver + name probe + region updater are
/// injected so NO DB, NO MySQL and NO live singletons are touched. The real
/// BotRoamStepExecutor is used for route arming (SetRoamRoute is pure
/// state) and routes are observed through the internal GetRoamRoute seam.
/// </summary>
[ParallelLimiter<BodyPartSequentialParallelLimit>] // t_eb9d8b30: state-neutral rig — serialize + per-test cleanup; limiter class lives in BotBodyPartEquipmentTests.cs (develop's t_743866f9 version)
[NotInParallel]
public class BotAdminServiceTests
{
    /// <summary>In-memory registry fake mirroring PlayerBotManager semantics.</summary>
    private sealed class FakeManager : IPlayerBotManager
    {
        private readonly Dictionary<uint, PlayerBotRuntime> _registry = new();

        public List<(uint Id, string Owner)> Spawns { get; } = [];
        public List<(uint Id, string Owner)> Activations { get; } = [];
        public List<(uint Id, string Reason)> Deactivations { get; } = [];
        public List<uint> Removes { get; } = [];

        public bool Spawn(Character character, string owner)
        {
            Spawns.Add((character.Id, owner));
            if (_registry.ContainsKey(character.Id))
                return false;
            _registry.Add(character.Id, new PlayerBotRuntime(character, owner));
            return true;
        }

        public bool Activate(uint characterId, object? botContext, string owner)
        {
            Activations.Add((characterId, owner));
            if (!_registry.TryGetValue(characterId, out var runtime))
                return false;
            if (runtime.State is not (PlayerBotState.Registered or PlayerBotState.Deactivated))
                return false;
            runtime.State = PlayerBotState.Active;
            runtime.Owner = owner;
            return true;
        }

        public bool Deactivate(uint characterId, string reason)
        {
            Deactivations.Add((characterId, reason));
            if (!_registry.TryGetValue(characterId, out var runtime))
                return false;
            if (runtime.State != PlayerBotState.Active)
                return false;
            runtime.State = PlayerBotState.Deactivated;
            runtime.LastDeactivateReason = reason;
            return true;
        }

        public bool TryGet(uint characterId, out PlayerBotRuntime? runtime)
        {
            runtime = _registry.GetValueOrDefault(characterId);
            return runtime != null;
        }

        public bool Remove(uint characterId)
        {
            Removes.Add(characterId);
            if (!_registry.TryGetValue(characterId, out var runtime))
                return false;
            if (runtime.State == PlayerBotState.Active)
                return false;
            return _registry.Remove(characterId);
        }

        public IReadOnlyList<PlayerBotRuntime> GetAll() => [.. _registry.Values];
        public IReadOnlyList<PlayerBotRuntime> GetActive() => _registry.Values.Where(r => r.State == PlayerBotState.Active).ToList();
        public int Count => _registry.Count;
        public int ActiveCount => _registry.Values.Count(r => r.State == PlayerBotState.Active);
        public PlayerBotDiagnostics GetDiagnostics() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>Seeds a runtime directly (no lifecycle calls recorded).</summary>
        public PlayerBotRuntime Seed(Character character, PlayerBotState state = PlayerBotState.Active, string owner = "rig")
        {
            var runtime = new PlayerBotRuntime(character, owner) { State = state };
            _registry[character.Id] = runtime;
            return runtime;
        }
    }

    private sealed class FakeScheduler : IPlayerBotScheduler
    {
        public int StartCalls;
        public List<uint> Wakes { get; } = [];

        public void Start() => StartCalls++;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool Wake(uint characterId) { Wakes.Add(characterId); return true; }
        public bool WakeAt(uint characterId, DateTime utcDue) => true;
        public bool WakeAfter(uint characterId, TimeSpan delay) => true;
        public bool IsLeased(uint characterId) => false;
        public PlayerBotSchedulerMetrics GetMetrics() => new(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0);
        public int WorkerCount => 4;
        public bool IsRunning => StartCalls > 0;
    }

    private sealed class FakeDirector : IPopulationDirector
    {
        public Dictionary<uint, BotFidelity> Fidelity { get; } = new();
        public List<(uint Id, BotFidelity Target)> SetCalls { get; } = [];

        public BotFidelity GetFidelity(uint characterId) => Fidelity.GetValueOrDefault(characterId, BotFidelity.Dormant);

        public FidelityTransitionResult TrySetFidelity(uint characterId, BotFidelity target, string reason)
        {
            SetCalls.Add((characterId, target));
            var current = GetFidelity(characterId);
            if (current == target)
                return FidelityTransitionResult.NoChange;
            Fidelity[characterId] = target;
            return FidelityTransitionResult.Applied;
        }

        public ServerPressure RefreshPressure() => ServerPressure.Healthy;
        public ServerPressure Pressure => ServerPressure.Healthy;
        public FidelityTransitionResult Wake(uint characterId, string reason) => FidelityTransitionResult.Applied;
        public FidelityTransitionResult Sleep(uint characterId, string reason) => FidelityTransitionResult.Applied;
        public int EmbodiedCount => Fidelity.Count;
        public int EmbodiedInZone(uint zoneId) => Fidelity.Count;
        public int EmbodiedOnActivity(string activity) => Fidelity.Count;
        public PopulationDirectorMetrics GetMetrics() => new(0, 0, 0, ServerPressure.Healthy, 0, 0, 0, 0, 0);
    }

    private sealed class Rig
    {
        public FakeManager Manager { get; } = new();
        public FakeScheduler Scheduler { get; } = new();
        public FakeDirector Director { get; } = new();
        public BotRoamStepExecutor Executor { get; } = new();
        public List<(string Account, string Name, Race Race, Gender Gender, byte Level)> Provisions { get; } = [];
        public List<Vector3> TerrainRequests { get; } = [];
        public List<Character> RegionUpdates { get; } = [];
        public HashSet<string> TakenNames { get; } = [];

        private uint _nextId = 9000;

        public BotAdminService CreateService()
        {
            SeedFixtureSingletons();
            return new BotAdminService(
                Manager,
                Scheduler,
                Director,
                Executor,
                provisioner: (account, name, race, gender, level) =>
                {
                    Provisions.Add((account, name, race, gender, level));
                    var session = MakeHeadlessSession(_nextId++, name, level, race);
                    session.Character.Transform.Local.SetPosition(100, 200, 30);
                    return session;
                },
                terrainResolver: (pos, zoneId) =>
                {
                    TerrainRequests.Add(pos);
                    return pos; // identity — no heightmap in the rig
                },
                groundHeightProvider: (pos, zoneId) => 0f, // no heightmap data → route keeps home.Z
                nameIsTaken: n => TakenNames.Contains(n),
                regionUpdater: c => RegionUpdates.Add(c));
        }

        /// <summary>Provisioner that throws (squatting / DB failure simulation).</summary>
        public BotAdminService CreateServiceWithFailingProvisioner()
        {
            SeedFixtureSingletons();
            return new BotAdminService(
                Manager, Scheduler, Director, Executor,
                provisioner: (account, name, race, gender, level) =>
                    throw new ArgumentException($"character name '{name}' already exists and is owned by another account"),
                terrainResolver: (pos, zoneId) => pos,
                groundHeightProvider: (pos, zoneId) => 0f,
                nameIsTaken: n => TakenNames.Contains(n),
                regionUpdater: c => RegionUpdates.Add(c));
        }
    }

    // ---------------------------------------------------------------- list

    [Test]
    public async Task List_WhenEmpty_ReturnsNoBotsMessage()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var result = service.List();

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message).Contains("No player bots registered");
    }

    [Test]
    public async Task List_ReturnsNameIdStateFidelityAndPosition()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var active = rig.Manager.Seed(MakeBot(1, "Citizen01"), PlayerBotState.Active);
        active.Character.Transform.Local.SetPosition(15597.1f, 15363.4f, 135.2f);
        rig.Director.Fidelity[active.CharacterId] = BotFidelity.Full;

        var deactivated = rig.Manager.Seed(MakeBot(2, "Citizen02"), PlayerBotState.Deactivated);
        deactivated.Character.Transform.Local.SetPosition(10, 20, 30);

        var result = service.List();

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message).Contains("2 registered");
        await Assert.That(result.Message).Contains("1 active");
        await Assert.That(result.Message).Contains("Citizen01 (id 1) [Active] fidelity=Full @ 15597.1/15363.4/135.2");
        await Assert.That(result.Message).Contains("Citizen02 (id 2) [Deactivated] fidelity=Dormant");
    }

    // ----------------------------------------------------------------- add

    [Test]
    public async Task Add_ProvisionsSpawnsActivatesFullFidelityAndArmsRoamRoute()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var result = service.Add("Bob");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message).Contains("Bob");
        await Assert.That(result.Message).Contains("Full fidelity");

        // Production path: GM bot account + Nuian/Male/5.
        await Assert.That(rig.Provisions).HasCount().EqualTo(1);
        var (account, name, race, gender, level) = rig.Provisions[0];
        await Assert.That(account).IsEqualTo(BotAdminService.GmBotAccountName);
        await Assert.That(name).IsEqualTo("Bob");
        await Assert.That(race).IsEqualTo(Race.Nuian);
        await Assert.That(gender).IsEqualTo(Gender.Male);
        await Assert.That(level).IsEqualTo(BotAdminService.GmBotLevel);

        // Registry flow: spawn → activate.
        await Assert.That(rig.Manager.Spawns).HasCount().EqualTo(1);
        await Assert.That(rig.Manager.Activations).HasCount().EqualTo(1);
        await Assert.That(rig.Manager.Spawns[0].Owner).IsEqualTo("gm-command");
        await Assert.That(rig.Manager.Activations[0].Owner).IsEqualTo("gm-command");

        // Fidelity ladder: Reduced → Full (single steps only).
        await Assert.That(rig.Director.SetCalls.Select(c => c.Target))
            .IsEquivalentTo(new[] { BotFidelity.Reduced, BotFidelity.Full });

        // Roam route armed around the bot's spawn position + scheduler started/woken.
        var botId = rig.Manager.GetAll()[0].CharacterId;
        await Assert.That(rig.Executor.GetRoamRoute(botId)).IsNotNull();
        await Assert.That(rig.Executor.GetRoamRoute(botId)!.Mode).IsEqualTo(BotPath.LoopMode.Loop);
        await Assert.That(rig.Scheduler.StartCalls).IsEqualTo(1);
        await Assert.That(rig.Scheduler.Wakes).IsEquivalentTo(new[] { botId });
    }

    [Test]
    public async Task Add_WhenAlreadyActive_IsIdempotent_NoSecondProvision()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        rig.Manager.Seed(MakeBot(1, "Bob"), PlayerBotState.Active);

        var result = service.Add("Bob");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message).Contains("already present and active");
        await Assert.That(rig.Provisions).IsEmpty();
        await Assert.That(rig.Manager.Spawns).IsEmpty();
        await Assert.That(rig.Manager.Activations).IsEmpty();
    }

    [Test]
    public async Task Add_WhenRegisteredButDeactivated_ReactivatesWithoutProvision()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        var bot = rig.Manager.Seed(MakeBot(1, "Bob"), PlayerBotState.Deactivated);

        var result = service.Add("Bob");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message).Contains("re-activated");
        await Assert.That(rig.Provisions).IsEmpty();
        await Assert.That(rig.Manager.Activations).HasCount().EqualTo(1);
        await Assert.That(rig.Manager.Activations[0].Id).IsEqualTo(bot.CharacterId);
        await Assert.That(rig.Executor.GetRoamRoute(bot.CharacterId)).IsNotNull();
    }

    [Test]
    public async Task Add_WhenProvisionThrows_ReturnsFailureMessage()
    {
        var rig = new Rig();
        var service = rig.CreateServiceWithFailingProvisioner();

        var result = service.Add("Squatter");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Message).Contains("Provision failed");
        await Assert.That(result.Message).Contains("owned by another account");
        await Assert.That(rig.Manager.Spawns).IsEmpty();
    }

    [Test]
    public async Task Add_WhenNameIsEmpty_ReturnsFailure()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var result = service.Add("   ");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(rig.Provisions).IsEmpty();
    }

    // ---------------------------------------------------------------- here

    [Test]
    public async Task Here_WithGivenName_SpawnsAtGmPosition()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        var gmPosition = new Vector3(500, 600, 70);

        var result = service.Here(gmPosition, "Guard");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(rig.Provisions).HasCount().EqualTo(1);
        await Assert.That(rig.Provisions[0].Name).IsEqualTo("Guard");

        // Roam home = GM position (terrain resolver identity → unchanged).
        var botId = rig.Manager.GetAll()[0].CharacterId;
        var route = rig.Executor.GetRoamRoute(botId);
        await Assert.That(route).IsNotNull();
        await Assert.That(route!.AllWaypointsWithin(gmPosition, BotAdminService.GmRoamRadius)).IsTrue();
    }

    [Test]
    public async Task Here_WithoutName_AutoGeneratesFreeName()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        rig.TakenNames.Add("Bot01");

        var result = service.Here(new Vector3(1, 2, 3));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(rig.Provisions).HasCount().EqualTo(1);
        await Assert.That(rig.Provisions[0].Name).IsEqualTo("Bot02");
    }

    [Test]
    public async Task Here_WhenAllAutoNamesTaken_ReturnsFailure()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        for (var i = 1; i <= 99; i++)
            rig.TakenNames.Add($"Bot{i:D2}");

        var result = service.Here(new Vector3(1, 2, 3));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Message).Contains("auto-generate");
        await Assert.That(rig.Provisions).IsEmpty();
    }

    // -------------------------------------------------------------- remove

    [Test]
    public async Task Remove_ByName_DeactivatesLeaveSavesAndDropsRegistry()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        var bot = rig.Manager.Seed(MakeBot(1, "Bob"), PlayerBotState.Active);

        var result = service.Remove("bob"); // case-insensitive

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message).Contains("removed");
        await Assert.That(rig.Manager.Deactivations).HasCount().EqualTo(1);
        await Assert.That(rig.Manager.Deactivations[0].Id).IsEqualTo(bot.CharacterId);
        await Assert.That(rig.Manager.Deactivations[0].Reason).IsEqualTo("gm-command-remove");
        await Assert.That(rig.Manager.Removes).IsEquivalentTo(new[] { bot.CharacterId });
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
        // Patrol route cleared before teardown.
        await Assert.That(rig.Executor.GetRoamRoute(bot.CharacterId)).IsNull();
    }

    [Test]
    public async Task Remove_ById_DeactivatesAndDropsRegistry()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        var bot = rig.Manager.Seed(MakeBot(42, "Alice"), PlayerBotState.Active);

        var result = service.Remove("42");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(rig.Manager.Deactivations).HasCount().EqualTo(1);
        await Assert.That(rig.Manager.Deactivations[0].Id).IsEqualTo(42u);
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Remove_WhenNotActive_StillDropsRegistry()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        rig.Manager.Seed(MakeBot(1, "Bob"), PlayerBotState.Registered);

        var result = service.Remove("Bob");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(rig.Manager.Deactivations).IsEmpty();
        await Assert.That(rig.Manager.Removes).IsEquivalentTo(new[] { 1u });
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Remove_Unknown_ReturnsFriendlyError_NoThrow()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var result = service.Remove("Nobody");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Message).Contains("No bot found");
        await Assert.That(rig.Manager.Deactivations).IsEmpty();
        await Assert.That(rig.Manager.Removes).IsEmpty();
    }

    // ------------------------------------------------------------------ go

    [Test]
    public async Task Go_RelocatesPatrolHome_TeleportsAndRearmsRoute()
    {
        var rig = new Rig();
        var service = rig.CreateService();
        var bot = rig.Manager.Seed(MakeBot(1, "Bob"), PlayerBotState.Active);
        bot.Character.Transform.Local.SetPosition(100, 200, 30);

        var target = new Vector3(1234, 5678, 90);
        var result = service.Go("Bob", target);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Message).Contains("relocated");
        await Assert.That(result.Message).Contains("1234/5678/90");

        // Teleported: transform now at the target.
        var pos = bot.Character.Transform.World.Position;
        await Assert.That(pos.X).IsEqualTo(1234f);
        await Assert.That(pos.Y).IsEqualTo(5678f);
        await Assert.That(pos.Z).IsEqualTo(90f);

        // Region graph updated so clients in the new area see the bot.
        await Assert.That(rig.RegionUpdates).HasCount().EqualTo(1);
        await Assert.That(rig.RegionUpdates[0].Id).IsEqualTo(bot.CharacterId);

        // Terrain resolver consulted (post-hotfix coords).
        await Assert.That(rig.TerrainRequests).HasCount().EqualTo(1);
        await Assert.That(rig.TerrainRequests[0]).IsEqualTo(target);

        // Route re-armed around the new home + wake.
        var route = rig.Executor.GetRoamRoute(bot.CharacterId);
        await Assert.That(route).IsNotNull();
        await Assert.That(route!.AllWaypointsWithin(target, BotAdminService.GmRoamRadius)).IsTrue();
        await Assert.That(rig.Scheduler.Wakes).IsEquivalentTo(new[] { bot.CharacterId });
    }

    [Test]
    public async Task Go_UnknownBot_ReturnsFriendlyError()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var result = service.Go("Ghost", new Vector3(1, 2, 3));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Message).Contains("No bot found");
        await Assert.That(rig.TerrainRequests).IsEmpty();
    }

    [Test]
    public async Task Go_TerrainClampedTarget_IsUsedForTransformAndRoute()
    {
        var rig = new Rig();
        // Clamp: snap Z to 135.161 (terrain) — the Z-wedge scenario shape.
        var service = new BotAdminService(
            rig.Manager, rig.Scheduler, rig.Director, rig.Executor,
            provisioner: (account, name, race, gender, level) =>
            {
                SeedFixtureSingletons();
                var session = MakeHeadlessSession(9000, name, level, race);
                session.Character.Transform.Local.SetPosition(100, 200, 30);
                return session;
            },
            terrainResolver: (pos, zoneId) => new Vector3(pos.X, pos.Y, 135.161f),
            groundHeightProvider: (pos, zoneId) => 135.161f,
            nameIsTaken: n => rig.TakenNames.Contains(n),
            regionUpdater: c => rig.RegionUpdates.Add(c));
        var bot = rig.Manager.Seed(MakeBot(1, "Bob"), PlayerBotState.Active);

        var result = service.Go("Bob", new Vector3(15597.1f, 15363.4f, 126.484f));

        await Assert.That(result.Success).IsTrue();
        var pos = bot.Character.Transform.World.Position;
        await Assert.That(pos.Z).IsEqualTo(135.161f);
        var route = rig.Executor.GetRoamRoute(bot.CharacterId);
        await Assert.That(route).IsNotNull();
        // Waypoints built around the CLAMPED home (never the raw input Z).
        await Assert.That(route!.AllWaypointsWithin(new Vector3(15597.1f, 15363.4f, 135.161f),
            BotAdminService.GmRoamRadius)).IsTrue();
    }

    // --------------------------------------------------------- liststatus

    [Test]
    public async Task ListStatus_WhenEmpty_ReturnsEmptyList()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var result = service.ListStatus();

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task ListStatus_ReturnsStructuredSnapshot_SortedByName()
    {
        var rig = new Rig();
        var service = rig.CreateService();

        var zed = rig.Manager.Seed(MakeBot(2, "Zed"), PlayerBotState.Active);
        zed.Character.Transform.Local.SetPosition(15597.1f, 15363.4f, 135.2f);
        rig.Director.Fidelity[zed.CharacterId] = BotFidelity.Full;

        var alice = rig.Manager.Seed(MakeBot(1, "Alice"), PlayerBotState.Deactivated);
        alice.Character.Transform.Local.SetPosition(10, 20, 30);

        var result = service.ListStatus();

        await Assert.That(result).HasCount().EqualTo(2);

        await Assert.That(result[0].Name).IsEqualTo("Alice");
        await Assert.That(result[0].Id).IsEqualTo(1u);
        await Assert.That(result[0].State).IsEqualTo("Deactivated");
        await Assert.That(result[0].Fidelity).IsEqualTo(BotFidelity.Dormant.ToString());
        await Assert.That(result[0].X).IsEqualTo(10f);
        await Assert.That(result[0].Y).IsEqualTo(20f);
        await Assert.That(result[0].Z).IsEqualTo(30f);

        await Assert.That(result[1].Name).IsEqualTo("Zed");
        await Assert.That(result[1].Id).IsEqualTo(2u);
        await Assert.That(result[1].State).IsEqualTo("Active");
        await Assert.That(result[1].Fidelity).IsEqualTo(BotFidelity.Full.ToString());
        await Assert.That(result[1].X).IsEqualTo(15597.1f);
        await Assert.That(result[1].Y).IsEqualTo(15363.4f);
        await Assert.That(result[1].Z).IsEqualTo(135.2f);
    }

    // ------------------------------------------------------------- helpers

    private static Character MakeBot(uint id, string name)
    {
        SeedFixtureSingletons();
        var session = MakeHeadlessSession(id, name, BotAdminService.GmBotLevel);
        return session.Character;
    }

    /// <summary>
    /// HeadlessSession.Create wrapper that TRACKS the exact persistent
    /// container keys the rig registered (Inventory ctor →
    /// GetItemContainerForCharacter, keyed by ContainerId) so the per-test
    /// cleanup ([After(Test)]) can remove precisely this rig's state from the
    /// shared ItemManager singleton — the rig is state-neutral (t_eb9d8b30
    /// ListStatus isolation rework). The tracker is cumulative and cleared by
    /// every cleanup pass, so even a test that fails mid-run has its
    /// containers removed by the next test's cleanup.
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, byte> TrackedContainerKeys = new();

    private static HeadlessSession MakeHeadlessSession(uint id, string name, byte level, Race race = Race.Nuian)
    {
        var session = HeadlessSession.Create(id, name, level, race);
        if (session.Character.Inventory is { } inventory)
        {
            // Every container Inventory resolved for this character is keyed
            // by ContainerId in _allPersistentContainers. Tracking keys — not
            // owner ids — keeps the cleanup blast radius to THIS rig's exact
            // containers (low character ids like 1/2/42 are not rig-exclusive).
            foreach (var container in new[]
                     {
                         inventory.Equipment, inventory.Bag, inventory.Warehouse,
                         inventory.MailAttachments, inventory.AuctionAttachments, inventory.SystemContainer
                     })
            {
                if (container != null)
                    TrackedContainerKeys.TryAdd(container.ContainerId, 0);
            }
        }
        return session;
    }

    [After(Test)]
    public void AfterTest_CleanupTrackedContainers()
    {
        // State-neutral rig: HeadlessSession.Create → Character → Inventory
        // registers persistent containers in the SHARED ItemManager singleton
        // (GetItemContainerForCharacter). Leaving them behind perturbs other
        // suite classes (t_eb9d8b30). Remove exactly the containers this rig
        // registered (tracked by ContainerId key), plus their items.
        // The singleton is shared process-wide — guard before touching
        // Instance: unseeded it THROWS (ItemManager has no parameterless
        // ctor, Singleton<T>.OnInit), and other classes' seeds must never be
        // replaced (t_4f11a519).
        var singletonField = typeof(Singleton<ItemManager>).GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (singletonField?.GetValue(null) is not ItemManager itemManager)
            return;

        // Production field type is ConcurrentDictionary (ItemManager.cs:77-79).
        var containersField = typeof(ItemManager).GetField("_allPersistentContainers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var itemsField = typeof(ItemManager).GetField("_allItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (containersField?.GetValue(itemManager) is not ConcurrentDictionary<ulong, ItemContainer> containers)
            return;
        var allItems = itemsField?.GetValue(itemManager) as ConcurrentDictionary<ulong, Item>;

        foreach (var key in TrackedContainerKeys.Keys.ToList())
        {
            if (!containers.TryGetValue(key, out var container))
                continue;
            if (allItems != null)
            {
                foreach (var item in container.Items.ToList())
                    allItems.TryRemove(item.Id, out _);
            }
            containers.TryRemove(key, out _);
        }
        TrackedContainerKeys.Clear();
    }

    /// <summary>
    /// Missing-only singleton seeding for the Character ctor path (Inventory
    /// resolves ItemManager.Instance + ContainerIdManager.Instance). Mirrors
    /// BotPresenceCoordinatorTests (t_302b67bf / t_4f11a519 discipline):
    /// NEVER replace an established singleton, NEVER forceReset
    /// ContainerIdManager (duplicate-key 65536 in full-suite, t_6bad0654).
    /// </summary>
    private static void SeedFixtureSingletons()
    {
        var field = typeof(Singleton<ItemManager>).GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (field?.GetValue(null) == null)
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
            var existing = containerField?.GetValue(itemManager) as ConcurrentDictionary<ulong, ItemContainer>;
            if (existing == null)
                containerField?.SetValue(itemManager, new ConcurrentDictionary<ulong, ItemContainer>());
            field?.SetValue(null, itemManager);
        }
        ContainerIdManager.Instance.Initialize(false);
    }
}
