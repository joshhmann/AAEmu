using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// G2-A5 true-dormancy rig: <see cref="DormantBotRegistry"/> discovery /
/// materialize / dematerialize plus the PopulationDirector additive passes.
///
/// Same skeleton as PopulationDirectorProximityTests: real PlayerBotManager +
/// real PlayerBotScheduler (manual pumps), recording lifecycle seam, stubbed
/// safety/pressure probes. The DB seams are stubbed per the ownership rules:
/// IDormantBotSource (SQL discovery), Func&lt;uint, Character?&gt; (the
/// Character.Load adoption-path loader) and IDormantBotHomeSource
/// (playerbot_metadata home positions).
/// </summary>
public class DormantBotRegistryTests
{
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public List<uint> Activated { get; } = [];
        public List<uint> Deactivated { get; } = [];
        public HashSet<uint> RefuseDeactivate { get; } = [];

        public bool ActivateHeadless(Character character, object? botContext)
        {
            Activated.Add(character.Id);
            return true;
        }

        public bool Deactivate(Character character, string reason)
        {
            if (RefuseDeactivate.Contains(character.Id))
                return false;
            Deactivated.Add(character.Id);
            return true;
        }
    }

    private sealed class NullExecutor : IBotStepExecutor
    {
        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
            => Task.FromResult<TimeSpan?>(null);
    }

    private sealed class StubSafetyProbe : IBotTransitionSafetyProbe
    {
        public HashSet<uint> InCombatBots { get; } = [];

        public bool IsInCombat(Character character) => InCombatBots.Contains(character.Id);
        public bool IsAttachedToSlave(Character character) => false;
        public bool IsCarryingTradePack(Character character) => false;
        public bool IsInTrial(Character character) => false;
        public bool IsGroupedWithHuman(Character character) => false;
        public bool IsSaving(Character character) => false;
    }

    private sealed class StubPressureProbe : IPressureProbe
    {
        public PressureSample SampleValue { get; set; } = PressureSample.Empty;

        public PressureSample Sample() => SampleValue;
    }

    private sealed class StubDormantBotSource(List<DormantBotSpec> specs) : IDormantBotSource
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<DormantBotSpec> ListSpecs()
        {
            CallCount++;
            return specs;
        }
    }

    private sealed class StubHomeSource : IDormantBotHomeSource
    {
        public Dictionary<uint, (uint WorldId, Vector3 Position)> Homes { get; } = [];

        public bool TryGetHome(uint characterId, out uint worldId, out Vector3 position)
        {
            if (Homes.TryGetValue(characterId, out var home))
            {
                worldId = home.WorldId;
                position = home.Position;
                return true;
            }

            worldId = 0;
            position = default;
            return false;
        }
    }

    private sealed class Rig : IDisposable
    {
        public RecordingLifecycle Lifecycle { get; } = new();
        public StubSafetyProbe Safety { get; } = new();
        public StubPressureProbe PressureProbe { get; } = new();
        public StubHomeSource HomeSource { get; } = new();
        public StubDormantBotSource Source { get; }
        public Dictionary<uint, Character> Rows { get; } = [];
        public List<uint> LoadedIds { get; } = [];
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }
        public DormantBotRegistry Registry { get; }
        public PopulationDirector Director { get; }

        /// <summary>The current "human" snapshot (settable per test/scenario).</summary>
        public List<Character> Humans { get; } = [];

        public Rig(Action<PopulationDirectorOptions>? configureOptions = null,
            bool enableTrueDormancy = true,
            int materializePerSweepMax = 3)
        {
            var knownSpecs = new List<DormantBotSpec>
            {
                new(101, "DormantOne"),
                new(102, "DormantTwo"),
                new(103, "DormantThree"),
                new(104, "DormantFour"),
                new(105, "DormantFive"),
            };
            Source = new StubDormantBotSource(knownSpecs);
            foreach (var spec in knownSpecs)
                Rows[spec.CharacterId] = NewRow(spec.CharacterId, spec.Name);

            Manager = new PlayerBotManager(Lifecycle);
            var schedulerOptions = new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false,
            };
            Scheduler = new PlayerBotScheduler(Manager, new NullExecutor(), schedulerOptions,
                new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTime.UtcNow));
            Scheduler.Start();

            Registry = new DormantBotRegistry(
                Manager,
                Source,
                characterLoader: id =>
                {
                    LoadedIds.Add(id);
                    return Rows.TryGetValue(id, out var row) ? row : null;
                },
                homeSource: HomeSource);

            var directorOptions = new PopulationDirectorOptions
            {
                EnableProximityFidelity = true,
                EnableTrueDormancy = enableTrueDormancy,
                TrueDormancyMaterializePerSweepMax = materializePerSweepMax,
            };
            configureOptions?.Invoke(directorOptions);
            Director = new PopulationDirector(
                Manager,
                Scheduler,
                Safety,
                PressureProbe,
                directorOptions,
                humanSnapshotProvider: () => Humans,
                dormantBots: enableTrueDormancy ? Registry : null);
        }

        private static Character NewRow(uint id, string name)
            => new(new UnitCustomModelParams()) { Id = id, Name = name, MaxHp = 100, Hp = 100 };

        public Character AddHuman(uint id, float x)
        {
            var human = new Character(new UnitCustomModelParams()) { Id = id, Name = $"human{id}", MaxHp = 100, Hp = 100 };
            human.Transform.Local.SetPosition(x, 0f, 0f);
            Humans.Add(human);
            return human;
        }

        public Character AddEmbodiedFullBot(uint id, string name)
        {
            var bot = NewRow(id, name);
            bot.Transform.Local.SetPosition(5000f, 0f, 0f); // far from everything
            Manager.Spawn(bot, "rig");
            Manager.Activate(id, null, "rig");
            Director.TrySetFidelity(id, BotFidelity.Reduced, "seed");
            Director.TrySetFidelity(id, BotFidelity.Full, "seed");
            return bot;
        }

        public void Sweep() => Director.RefreshProximityFidelity();

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------ discovery

    [Test]
    public async Task ListSpecs_DiscoversFromStubSource_Lazily()
    {
        using var rig = new Rig();

        await Assert.That(rig.Source.CallCount).IsEqualTo(0); // lazy: no discovery before first use

        var specs = rig.Registry.ListSpecs();
        await Assert.That(specs.Count).IsEqualTo(5);
        await Assert.That(specs.Any(s => s.CharacterId == 101 && s.Name == "DormantOne")).IsTrue();
        await Assert.That(rig.Source.CallCount).IsEqualTo(1); // discovered once, cached afterwards

        rig.Registry.ListSpecs();
        await Assert.That(rig.Source.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task ListSpecs_FiltersOutEmbodiedBots()
    {
        using var rig = new Rig();
        // Embodied bot 101 directly through the manager (e.g. provisioned at boot).
        var row = rig.Rows[101];
        row.Transform.Local.SetPosition(0f, 0f, 0f);
        rig.Manager.Spawn(row, "boot");
        rig.Manager.Activate(101, null, "boot");

        var specs = rig.Registry.ListSpecs();
        await Assert.That(specs.Any(s => s.CharacterId == 101)).IsFalse();
        await Assert.That(specs.Count).IsEqualTo(4);
    }

    // ------------------------------------------------------ round trip

    [Test]
    public async Task Materialize_Dematerialize_RoundTrip()
    {
        using var rig = new Rig();
        rig.HomeSource.Homes[102] = (1u, new Vector3(30f, 40f, 50f));

        // Materialize: adoption-path loader used, home restored, bot embodied.
        await Assert.That(rig.Registry.Materialize(new DormantBotSpec(102, "DormantTwo"))).IsTrue();
        await Assert.That(rig.LoadedIds).Contains(102u);
        await Assert.That(rig.Lifecycle.Activated).Contains(102u);
        await Assert.That(rig.Manager.TryGet(102, out var runtime)).IsTrue();
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.Active);

        var row = rig.Rows[102];
        var pos = row.Transform.ComputeWorldPosition();
        await Assert.That(pos.X).IsEqualTo(30f);
        await Assert.That(pos.Y).IsEqualTo(40f);
        await Assert.That(pos.Z).IsEqualTo(50f);
        await Assert.That(rig.Registry.IsDormant(102)).IsFalse();

        // Dematerialize: deactivated through the manager, spec returned.
        await Assert.That(rig.Registry.Dematerialize(row)).IsTrue();
        await Assert.That(rig.Lifecycle.Deactivated).Contains(102u);
        await Assert.That(rig.Manager.TryGet(102, out runtime)).IsTrue();
        await Assert.That(runtime.State).IsEqualTo(PlayerBotState.Deactivated);
        await Assert.That(rig.Registry.IsDormant(102)).IsTrue();
        await Assert.That(rig.Registry.ListSpecs().Any(s => s.CharacterId == 102)).IsTrue();

        // Rematerialize works again (reuses the retained registry record).
        await Assert.That(rig.Registry.Materialize(new DormantBotSpec(102, "DormantTwo"))).IsTrue();
        await Assert.That(rig.Manager.TryGet(102, out runtime)).IsTrue();
        await Assert.That(runtime.State).IsEqualTo(PlayerBotState.Active);
        await Assert.That(rig.Registry.IsDormant(102)).IsFalse();
    }

    [Test]
    public async Task Materialize_MissingRow_Refused()
    {
        using var rig = new Rig();
        await Assert.That(rig.Registry.Materialize(new DormantBotSpec(999, "Ghost"))).IsFalse();
        await Assert.That(rig.Lifecycle.Activated).DoesNotContain(999u);
    }

    // --------------------------------------- PopulationDirector integration

    [Test]
    public async Task TrueDormancy_ProximityMaterialization_HonorsPerSweepBudget()
    {
        using var rig = new Rig(materializePerSweepMax: 3);
        var human = rig.AddHuman(900, x: 10f);
        // All five dormant homes sit right next to the human.
        for (var i = 0; i < 5; i++)
        {
            var id = 101u + (uint)i;
            rig.HomeSource.Homes[id] = (0u, new Vector3(10f + i, 0f, 0f));
        }

        rig.Sweep(); // budget 3 of 5

        var afterFirst = rig.Director.GetMetrics();
        await Assert.That(afterFirst.TotalMaterializations).IsEqualTo(3);
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(3);

        rig.Sweep(); // remaining 2

        var afterSecond = rig.Director.GetMetrics();
        await Assert.That(afterSecond.TotalMaterializations).IsEqualTo(5);
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(5);

        // Materialized bots enter the ladder at Reduced (sweep 1) and are
        // already escalating toward Full here — they sit inside the human's
        // FULL radius. Either way: embodied, never Dormant-labeled.
        var fidelityAfter = rig.Director.GetFidelity(101);
        await Assert.That(fidelityAfter).IsNotEqualTo(BotFidelity.Dormant);
    }

    [Test]
    public async Task TrueDormancy_SpecWithoutHome_StaysDormant()
    {
        using var rig = new Rig();
        rig.AddHuman(900, x: 10f); // no homes recorded at all

        rig.Sweep();
        rig.Sweep();

        await Assert.That(rig.Director.GetMetrics().TotalMaterializations).IsEqualTo(0);
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(0);
        await Assert.That(rig.Registry.ListSpecs().Count).IsEqualTo(5);
    }

    [Test]
    public async Task TrueDormancy_FullBotThreeNoHumanSweeps_Dematerializes()
    {
        using var rig = new Rig();
        rig.AddEmbodiedFullBot(201, "FarBot");
        await Assert.That(rig.Manager.TryGet(201, out var runtime)).IsTrue();
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.Active);

        rig.Sweep(); // streak 1: observed only
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(1);

        rig.Sweep(); // streak 2: ladder steps down…
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(1); // …but still embodied

        rig.Sweep(); // streak 3: DEMATERIALIZED instead of a Dormant label
        await Assert.That(rig.Manager.TryGet(201, out runtime)).IsTrue();
        await Assert.That(runtime.State).IsEqualTo(PlayerBotState.Deactivated);
        await Assert.That(rig.Lifecycle.Deactivated).Contains(201u);

        var m = rig.Director.GetMetrics();
        await Assert.That(m.TotalDematerializations).IsEqualTo(1);
        await Assert.That(m.TotalMaterializations).IsEqualTo(0);
        await Assert.That(rig.Registry.IsDormant(201)).IsTrue(); // spec returned to the registry
    }

    [Test]
    public async Task TrueDormancy_SafetyGatedBot_NotDematerialized()
    {
        using var rig = new Rig();
        var bot = rig.AddEmbodiedFullBot(202, "CombatBot");
        rig.Safety.InCombatBots.Add(bot.Id);

        for (var i = 0; i < 4; i++)
            rig.Sweep();

        await Assert.That(rig.Manager.TryGet(202, out var r) && r!.State == PlayerBotState.Active).IsTrue();
        await Assert.That(rig.Director.GetMetrics().TotalDematerializations).IsEqualTo(0);
        await Assert.That(rig.Registry.IsDormant(202)).IsFalse();
    }

    [Test]
    public async Task TrueDormancy_Disabled_IsStrictlyInert()
    {
        using var rig = new Rig(configureOptions: null, enableTrueDormancy: false);
        rig.AddHuman(900, x: 10f);
        for (var i = 0; i < 5; i++)
        {
            var id = 101u + (uint)i;
            rig.HomeSource.Homes[id] = (0u, new Vector3(10f + i, 0f, 0f));
        }

        rig.AddEmbodiedFullBot(203, "StaysEmbodied");

        for (var i = 0; i < 4; i++)
            rig.Sweep();

        var m = rig.Director.GetMetrics();
        await Assert.That(m.TotalMaterializations).IsEqualTo(0);
        await Assert.That(m.TotalDematerializations).IsEqualTo(0);
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(1); // the Full bot never dissolves
        await Assert.That(rig.Lifecycle.Deactivated).DoesNotContain(203u);
        await Assert.That(rig.Registry.ListSpecs().Count).IsEqualTo(5); // untouched dormant pool
    }
}
