using System.Collections.Concurrent;
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
/// G2-A6 manifest-driven provisioning: loader parsing (valid / invalid /
/// per-entry isolation), the coordinator's roster path via fakes, the legacy
/// fallback when no manifest is wired, and the configurable bot-count clamp.
/// </summary>
[NotInParallel]
public class BotPresenceManifestTests
{
    // ---------------------------------------------------------------- loader

    [Test]
    public async Task TryLoad_ValidManifest_ParsesAllEntries()
    {
        var path = WriteTempManifest("""
            [
              { "name": "Citizen01", "race": "Nuian", "gender": "Male", "level": 5 },
              {
                "name": "Mira",
                "race": "elf",
                "gender": "Female",
                "level": 7,
                "classAbility": "Occultism",
                "personality": "chatty",
                "home": { "x": 15578.0, "y": 15382.0, "z": 126.5, "zoneId": 179 }
              }
            ]
            """);
        try
        {
            var ok = PresenceManifestLoader.TryLoad(path, out var entries);

            await Assert.That(ok).IsTrue();
            await Assert.That(entries.Count).IsEqualTo(2);

            var first = entries[0];
            await Assert.That(first.Name).IsEqualTo("Citizen01");
            await Assert.That(first.Race).IsEqualTo(Race.Nuian);
            await Assert.That(first.Gender).IsEqualTo(Gender.Male);
            await Assert.That(first.Level).IsEqualTo((byte)5);
            await Assert.That(first.ClassAbility).IsNull();
            await Assert.That(first.Home).IsNull();

            var second = entries[1];
            await Assert.That(second.Name).IsEqualTo("Mira");
            await Assert.That(second.Race).IsEqualTo(Race.Elf); // case-insensitive enum
            await Assert.That(second.Gender).IsEqualTo(Gender.Female);
            await Assert.That(second.Level).IsEqualTo((byte)7);
            await Assert.That(second.ClassAbility).IsEqualTo("Occultism");
            await Assert.That(second.Personality).IsEqualTo("chatty");
            await Assert.That(second.Home!.Value).IsEqualTo(new Vector3(15578f, 15382f, 126.5f));
            await Assert.That(second.HomeZoneId).IsEqualTo((uint)179);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoad_IsolatedBadEntries_GoodOnesSurvive()
    {
        var path = WriteTempManifest("""
            [
              { "race": "Nuian", "gender": "Male", "level": 5 },
              { "name": "BadRace", "race": "Vampire", "gender": "Male", "level": 5 },
              { "name": "BadLevel", "race": "Nuian", "gender": "Male", "level": 0 },
              { "name": "KeepMe", "race": "Ferre", "gender": "Female", "level": 3 },
              { "name": "PartialHome", "race": "Nuian", "gender": "Male", "level": 4, "home": { "x": 1.0 } },
              "not-an-object"
            ]
            """);
        try
        {
            var ok = PresenceManifestLoader.TryLoad(path, out var entries);

            // Per-entry isolation (G2-A6): missing name, unknown race and a
            // bad level are skipped; the good entries must survive.
            await Assert.That(ok).IsTrue();
            await Assert.That(entries.Count).IsEqualTo(2);
            await Assert.That(entries[0].Name).IsEqualTo("KeepMe");
            await Assert.That(entries[0].Home).IsNull(); // partial home dropped, entry kept
            await Assert.That(entries[1].Name).IsEqualTo("PartialHome");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoad_MalformedJson_Fails()
    {
        var path = WriteTempManifest("{ this is not json ]");
        try
        {
            var ok = PresenceManifestLoader.TryLoad(path, out var entries);

            await Assert.That(ok).IsFalse();
            await Assert.That(entries).IsEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task TryLoad_MissingFile_Fails()
    {
        var ok = PresenceManifestLoader.TryLoad(
            Path.Combine(Path.GetTempPath(), $"aaemu-no-such-{Guid.NewGuid():N}.json"), out var entries);

        await Assert.That(ok).IsFalse();
        await Assert.That(entries).IsEmpty();
    }

    [Test]
    public async Task TryLoad_EmptyArray_Succeeds_WithNoEntries()
    {
        var path = WriteTempManifest("[]");
        try
        {
            var ok = PresenceManifestLoader.TryLoad(path, out var entries);

            await Assert.That(ok).IsTrue();
            await Assert.That(entries).IsEmpty(); // empty roster → legacy loop in Start
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------- roster-driven Start

    [Test]
    public async Task Start_ManifestRoster_ProvisionsNamedBotsThroughFakeFlow()
    {
        SeedFixtureSingletons();
        var rig = ManifestRig();
        rig.Coordinator = withProvider(rig, () => new List<PresenceManifestEntry>
        {
            new("Mira", Race.Elf, Gender.Female, 7),
            new("Torren", Race.Nuian, Gender.Male, 4),
        });

        var result = rig.Coordinator.Start(Config());

        await Assert.That(result).IsTrue();

        // The provisioner received EXACTLY the manifest identity (G2-A6):
        // per-entry name / race / gender / level — not the Citizen prefix.
        await Assert.That(rig.Provisioned.Select(p => p.Name).ToArray())
            .IsEquivalentTo(["Mira", "Torren"]);
        var mira = rig.Provisioned[0];
        await Assert.That(mira.Race).IsEqualTo(Race.Elf);
        await Assert.That(mira.Gender).IsEqualTo(Gender.Female);
        await Assert.That(mira.Level).IsEqualTo((byte)7);
        var torren = rig.Provisioned[1];
        await Assert.That(torren.Race).IsEqualTo(Race.Nuian);
        await Assert.That(torren.Gender).IsEqualTo(Gender.Male);
        await Assert.That(torren.Level).IsEqualTo((byte)4);

        // Same production flow as the legacy path: spawn → activate →
        // fidelity Reduced+Full → wake.
        await Assert.That(rig.Manager.SpawnCalls).IsEqualTo(2);
        await Assert.That(rig.Manager.ActivateCalls).IsEqualTo(2);
        await Assert.That(rig.Director.FidelityCalls).IsEqualTo(4);
        await Assert.That(rig.Scheduler.StartCalls).IsEqualTo(1);
        await Assert.That(rig.Scheduler.Wakes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Start_ManifestEntryWithHome_PlacesBotAtHome_AndSteersRouteZoneProbe()
    {
        SeedFixtureSingletons();
        var home = new Vector3(15572f, 15364f, 126.5f);
        uint? probedZone = null;
        Character? mira = null;

        var rig = ManifestRig(probe: (_, zoneId) => { probedZone ??= zoneId; return 0f; });
        rig.Coordinator = withProvider(rig, () =>
        [
            new PresenceManifestEntry("Mira", Race.Elf, Gender.Female, 7,
                Home: home, HomeZoneId: 179)
        ]);
        rig.OnProvisionedCharacter = c => mira ??= c;

        var result = rig.Coordinator.Start(Config());

        await Assert.That(result).IsTrue();
        await Assert.That(mira).IsNotNull();

        // The bot embodies AT the manifest home (explicit-home precedence).
        var pos = mira!.Transform.World.Position;
        await Assert.That(Math.Abs(pos.X - home.X)).IsLessThanOrEqualTo(0.5f);
        await Assert.That(Math.Abs(pos.Y - home.Y)).IsLessThanOrEqualTo(0.5f);

        // The route's terrain probes used the entry's zoneId (179), not a
        // default — the manifest steers the patrol terrain source.
        await Assert.That(probedZone).IsEqualTo((uint)179);
    }

    [Test]
    public async Task Start_ManifestEntryWithoutHome_RouteAnchoredAtSpawn_NotDefaultHome()
    {
        // Soak stage-1 finding (a) regression: a roster entry without an
        // explicit home used to spawn at its race-template start position
        // while the patrol route anchored on the DEFAULT demo home — the bot
        // then walked kilometers toward an unreachable route (the drowning
        // elves). Home precedence must end at the ACTUAL SPAWN POSITION.
        SeedFixtureSingletons();
        Character? noHome = null;
        var rig = ManifestRig(probe: (_, _) => 0f);
        rig.Coordinator = withProvider(rig, () =>
        [
            new PresenceManifestEntry("NoHomeElf", Race.Elf, Gender.Female, 7)
        ]);
        rig.OnProvisionedCharacter = c => noHome ??= c;

        var result = rig.Coordinator.Start(Config());
        await Assert.That(result).IsTrue();
        await Assert.That(noHome).IsNotNull();

        var spawn = noHome!.Transform.World.Position;
        var route = rig.StepExecutor.GetRoamRoute(noHome.Id);
        await Assert.That(route).IsNotNull();

        // Every waypoint circles the SPAWN position, not the default home.
        foreach (var wp in route!.Waypoints)
        {
            await Assert.That(Math.Abs(wp.X - spawn.X)).IsLessThanOrEqualTo(30f);
            await Assert.That(Math.Abs(wp.Y - spawn.Y)).IsLessThanOrEqualTo(30f);
        }
    }

    [Test]
    public async Task Start_NoManifest_LegacyHardcodedCitizenPath()
    {
        SeedFixtureSingletons();

        // Legacy path (G2-A6 contract): provider yields null/empty → the
        // hardcoded 3-citizen loop runs exactly as before.
        var rig = ManifestRig();
        rig.Coordinator = withProvider(rig, () => null);

        var result = rig.Coordinator.Start(Config(botCount: 3));

        await Assert.That(result).IsTrue();
        await Assert.That(rig.Provisioned.Count).IsEqualTo(3);
        for (var i = 0; i < 3; i++)
        {
            var bot = rig.Provisioned[i];
            await Assert.That(bot.Name).IsEqualTo($"Citizen{i + 1:D2}"); // NamePrefix + index, not manifest names
            await Assert.That(bot.Race).IsEqualTo(Race.Nuian);           // hardcoded demo race/gender
            await Assert.That(bot.Gender).IsEqualTo(Gender.Male);
            await Assert.That(bot.Level).IsEqualTo((byte)5);
        }
        await Assert.That(rig.Manager.SpawnCalls).IsEqualTo(3);
        await Assert.That(rig.Scheduler.Wakes.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Start_ManifestRoster_ClampedToMaxPresenceBots()
    {
        SeedFixtureSingletons();
        var rig = ManifestRig();
        rig.Coordinator = withProvider(rig, () =>
        [
            new PresenceManifestEntry("A1", Race.Elf, Gender.Male, 2),
            new PresenceManifestEntry("A2", Race.Elf, Gender.Male, 2),
            new PresenceManifestEntry("A3", Race.Elf, Gender.Male, 2),
            new PresenceManifestEntry("A4", Race.Elf, Gender.Male, 2),
        ]);

        // MaxPresenceBots=2 clamps the 4-entry roster (safety bound).
        var result = rig.Coordinator.Start(Config(maxPresenceBots: 2));

        await Assert.That(result).IsTrue();
        await Assert.That(rig.Provisioned.Count).IsEqualTo(2);
        await Assert.That(rig.Manager.SpawnCalls).IsEqualTo(2);
    }

    // ---------------------------------------------------------------- clamp

    [Test]
    public async Task ClampBotCount_ConfigurableMax_OverridesLegacyTen()
    {
        // The old hardcoded clamp was Math.Clamp(count, 1, 10); the max is
        // now injectable (config/env) while defaulting to the same shape.
        await Assert.That(BotPresenceCoordinator.ClampBotCount(3, 10)).IsEqualTo(3);
        await Assert.That(BotPresenceCoordinator.ClampBotCount(25, 10)).IsEqualTo(10);
        await Assert.That(BotPresenceCoordinator.ClampBotCount(25, 20)).IsEqualTo(20); // raised bound honored
        await Assert.That(BotPresenceCoordinator.ClampBotCount(0, 10)).IsEqualTo(1);   // lower bound stays 1
        await Assert.That(BotPresenceCoordinator.ClampBotCount(5, 0)).IsEqualTo(1);    // degenerate max floored
    }

    [Test]
    public async Task ReadMaxPresenceBots_EnvOverride_Wins()
    {
        const string envVar = "AAEMU_PRESENCE_MAX_BOTS";
        var previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "42");

            await Assert.That(BotPresenceCoordinator.ReadMaxPresenceBots()).IsEqualTo(42);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Test]
    public async Task BotPresenceConfig_MaxPresenceBots_DefaultsToLegacyTen()
    {
        // Unconfigured deployments keep the historical clamp shape.
        await Assert.That(Config().MaxPresenceBots).IsEqualTo(10);
    }

    // ---------------------------------------------------------------- rig

    private sealed record ProvisionedBot(string Name, Race Race, Gender Gender, byte Level);

    private sealed class RecordingManager : IPlayerBotManager
    {
        public int SpawnCalls;
        public int ActivateCalls;

        public bool Spawn(Character character, string owner) { SpawnCalls++; return true; }
        public bool Activate(uint characterId, object? botContext, string owner) { ActivateCalls++; return true; }
        public bool Deactivate(uint characterId, string reason) => true;
        public bool TryGet(uint characterId, out PlayerBotRuntime? runtime) { runtime = null; return false; }
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
        public bool Wake(uint characterId) { Wakes.Add(characterId); return true; }
        public bool WakeAt(uint characterId, DateTime utcDue) => true;
        public bool WakeAfter(uint characterId, TimeSpan delay) => true;
        public bool IsLeased(uint characterId) => false;
        public PlayerBotSchedulerMetrics GetMetrics() => new(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0);
        public int WorkerCount => 4;
        public bool IsRunning => StartCalls > 0;
    }

    private sealed class RecordingDirector : IPopulationDirector
    {
        public int FidelityCalls;

        public BotFidelity GetFidelity(uint characterId) => BotFidelity.Dormant;
        public FidelityTransitionResult TrySetFidelity(uint characterId, BotFidelity target, string reason)
        {
            FidelityCalls++;
            return FidelityTransitionResult.Applied;
        }

        public ServerPressure RefreshPressure() => ServerPressure.Healthy;
        public ServerPressure Pressure => ServerPressure.Healthy;
        public FidelityTransitionResult Wake(uint characterId, string reason) => FidelityTransitionResult.Applied;
        public FidelityTransitionResult Sleep(uint characterId, string reason) => FidelityTransitionResult.Applied;
        public int EmbodiedCount => 0; // never idempotent-skip in this rig
        public int EmbodiedInZone(uint zoneId) => 0;
        public int EmbodiedOnActivity(string activity) => 0;
        public PopulationDirectorMetrics GetMetrics() => new(0, 0, 0, ServerPressure.Healthy, 0, 0, 0, 0, 0);
    }

    /// <summary>Mutable rig handle — tests assign the manifest provider after creation.</summary>
    private sealed class Rig
    {
        public required RecordingManager Manager { get; init; }
        public required RecordingScheduler Scheduler { get; init; }
        public required RecordingDirector Director { get; init; }
        public required List<ProvisionedBot> Provisioned { get; init; }
        public required List<uint> NextIds { get; init; }
        public Action<Character>? OnProvisionedCharacter { get; set; }
        public required Func<IReadOnlyList<PresenceManifestEntry>?> ProviderSlot { get; set; }
        public BotPresenceCoordinator Coordinator { get; set; } = null!;
        public BotRoamStepExecutor StepExecutor { get; set; } = null!;
    }

    private static Rig ManifestRig(Func<Vector3, uint, float>? probe = null)
    {
        SeedFixtureSingletons();
        var manager = new RecordingManager();
        var scheduler = new RecordingScheduler();
        var director = new RecordingDirector();
        var stepExecutor = new BotRoamStepExecutor(); // real — SetRoamRoute is a plain recorder

        var provisioned = new List<ProvisionedBot>();
        var nextIds = new List<uint>();

        var rig = new Rig
        {
            Manager = manager,
            Scheduler = scheduler,
            Director = director,
            Provisioned = provisioned,
            NextIds = nextIds,
            ProviderSlot = () => null,
            StepExecutor = stepExecutor,
        };

        var provisioner = (string username, string name, Race race, Gender gender, byte level) =>
        {
            provisioned.Add(new ProvisionedBot(name, race, gender, level));
            var id = (uint)(300 + nextIds.Count + 1);
            nextIds.Add(id);
            var session = HeadlessSession.Create(id, name, level, race);
            rig.OnProvisionedCharacter?.Invoke(session.Character);
            return session;
        };

        rig.Coordinator = new BotPresenceCoordinator(
            manager, scheduler, director, stepExecutor,
            _ => new Vector3(15578f, 15382f, 126f),
            provisioner,
            probe ?? ((_, _) => 0f), // flat route: waypoints keep home.Z
            manifestProvider: () => rig.ProviderSlot());

        return rig;
    }

    /// <summary>Swaps the rig's manifest provider and returns the coordinator.</summary>
    private static BotPresenceCoordinator withProvider(Rig rig,
        Func<IReadOnlyList<PresenceManifestEntry>?> provider)
    {
        rig.ProviderSlot = provider;
        return rig.Coordinator;
    }

    private static BotPresenceCoordinator.BotPresenceConfig Config(int botCount = 3, int maxPresenceBots = 10)
        => new(
            BotCount: botCount, ZoneId: 9, HomePosition: default, RoamRadius: 30f,
            RoamSpeed: 2.5f, Level: 5, NamePrefix: "Citizen", AccountPrefix: "presence",
            MaxPresenceBots: maxPresenceBots);

    private static string WriteTempManifest(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aaemu-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    // ---------------------------------------------------- singleton seeding

    /// <summary>
    /// Seeds exactly the singletons HeadlessSession.Create resolves — the
    /// BotPresenceCoordinatorTests convention (missing-only guards; never
    /// replaces an established singleton, t_4f11a519).
    /// </summary>
    private static void SeedFixtureSingletons()
    {
        SetSingletonIfMissing(typeof(Singleton<ItemManager>), BuildFixtureItemManager());
        ContainerIdManager.Instance.Initialize(false); // no-op when already initialized
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
            BindingFlags.NonPublic | BindingFlags.Instance);
        if ((containerField?.GetValue(itemManager) as ConcurrentDictionary<ulong, ItemContainer>) == null)
            containerField?.SetValue(itemManager, new ConcurrentDictionary<ulong, ItemContainer>());

        return itemManager;
    }

    private static void SetSingletonIfMissing(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        if (field.GetValue(null) != null)
            return; // never replace an established singleton (t_4f11a519)
        field.SetValue(null, instance);
    }
}
