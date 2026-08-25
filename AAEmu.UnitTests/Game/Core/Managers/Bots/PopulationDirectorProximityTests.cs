using System.Collections.Concurrent;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// PopulationDirector proximity fidelity rig (G2-A3): the human-proximity
/// tier ladder (Dormant→Reduced→Full near humans, back down without them).
///
/// Same skeleton as PopulationDirectorTests: real PlayerBotManager +
/// real PlayerBotScheduler (FakeTimeProvider, manual pumps), stubbed safety
/// probe and pressure probe. The proximity seam is a settable human snapshot
/// list — humans are ordinary Characters positioned via their Transform, NOT
/// registered as bots. Sweeps are pumped directly (no tick subscription in
/// tests); the driver gate is on for every rig except the inert test.
/// </summary>
public class PopulationDirectorProximityTests
{
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool ActivateHeadless(Character character, object? botContext) => true;

        public bool Deactivate(Character character, string reason) => true;
    }

    private sealed class NullExecutor : IBotStepExecutor
    {
        public ConcurrentQueue<uint> Starts { get; } = [];

        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            Starts.Enqueue(bot.CharacterId);
            return Task.FromResult<TimeSpan?>(null);
        }
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

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(DateTime.UtcNow);
        public NullExecutor Executor { get; } = new();
        public StubSafetyProbe Safety { get; } = new();
        public StubPressureProbe PressureProbe { get; } = new();
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }
        public PopulationDirector Director { get; }

        /// <summary>The current "human" snapshot (settable per test/scenario).</summary>
        public List<Character> Humans { get; } = [];

        /// <summary>Positions a character at (x, 0, 0) through its Transform.</summary>
        public static void Place(Character c, float x) => c.Transform.Local.SetPosition(x, 0f, 0f);

        public Character AddHuman(uint id, float x)
        {
            var human = new Character(new UnitCustomModelParams()) { Id = id, Name = $"human{id}", MaxHp = 100, Hp = 100 };
            Place(human, x);
            Humans.Add(human);
            return human;
        }

        public Rig(Action<PopulationDirectorOptions>? configureOptions = null, bool enabled = true)
        {
            Manager = new PlayerBotManager(new RecordingLifecycle());
            var schedulerOptions = new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1), // loop inert; cycles pumped manually
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false,
            };
            Scheduler = new PlayerBotScheduler(Manager, Executor, schedulerOptions, Time);
            Scheduler.Start();

            var directorOptions = new PopulationDirectorOptions { EnableProximityFidelity = enabled };
            configureOptions?.Invoke(directorOptions);
            Director = new PopulationDirector(
                Manager,
                Scheduler,
                Safety,
                PressureProbe,
                directorOptions,
                humanSnapshotProvider: () => Humans);
        }

        public uint AddActiveBot(uint id, string name = "bot")
        {
            var bot = new Character(new UnitCustomModelParams()) { Id = id, Name = name, MaxHp = 100, Hp = 100 };
            Place(bot, 0f);
            Manager.Spawn(bot, "rig");
            Manager.Activate(id, null, "rig");
            return id;
        }

        public Character Bot(uint id) => Manager.TryGet(id, out var runtime) ? runtime!.Character : throw new InvalidOperationException($"bot {id} not registered");

        public void Sweep() => Director.RefreshProximityFidelity();

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();
    }

    [Test]
    public async Task HumanInFullRadius_EscalatesOneStepPerSweep_DormantToReduced()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.AddHuman(100, x: 10f); // well inside the 75m full radius

        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);

        rig.Sweep(); // hysteresis sweep 1: condition observed, no move yet
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);

        var metrics = rig.Director.GetMetrics();
        await Assert.That(metrics.TotalProximityUpgrades).IsEqualTo(0);

        rig.Sweep(); // streak 2 → one step up only
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Reduced);
        await Assert.That(rig.Director.GetMetrics().TotalProximityUpgrades).IsEqualTo(1);
    }

    [Test]
    public async Task SustainedProximity_ReachesFull_AfterSecondUpgradeSweep()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.AddHuman(100, x: 5f);

        rig.Sweep(); // observe
        rig.Sweep(); // streak ≥2 → Dormant→Reduced (Wake)
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Reduced);
        await Assert.That(rig.Director.GetMetrics().TotalWakes).IsEqualTo(1);

        rig.Sweep(); // next step up → Reduced→Full
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Full);
        await Assert.That(rig.Director.GetMetrics().TotalProximityUpgrades).IsEqualTo(2);
    }

    [Test]
    public async Task NoHumans_DemotesBackDown_WithHysteresis()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Director.TrySetFidelity(bot, BotFidelity.Reduced, "seed");
        rig.Director.TrySetFidelity(bot, BotFidelity.Full, "seed");
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Full);

        // No humans at all: target is Dormant from sweep 1, but the demotion
        // waits for the second consecutive observation.
        rig.Sweep();
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Full);

        rig.Sweep(); // streak 2 → Full→Reduced (one step per sweep)
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Reduced);
        await Assert.That(rig.Director.GetMetrics().TotalProximityDemotions).IsEqualTo(1);

        rig.Sweep(); // still no humans → Reduced→Dormant (Sleep path)
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);
        await Assert.That(rig.Director.GetMetrics().TotalSleeps).IsEqualTo(1);
        await Assert.That(rig.Director.GetMetrics().TotalProximityDemotions).IsEqualTo(2);
    }

    [Test]
    public async Task SafetyGated_InCombatBot_StaysPutOnDemotion()
    {
        using var rig = new Rig();
        var combatBot = rig.AddActiveBot(1);
        var clearBot = rig.AddActiveBot(2);
        rig.Director.TrySetFidelity(combatBot, BotFidelity.Reduced, "seed");
        rig.Director.TrySetFidelity(combatBot, BotFidelity.Full, "seed");
        rig.Director.TrySetFidelity(clearBot, BotFidelity.Reduced, "seed");
        rig.Director.TrySetFidelity(clearBot, BotFidelity.Full, "seed");
        rig.Safety.InCombatBots.Add(combatBot); // ONLY bot 1 is gated

        rig.Sweep(); // observe
        rig.Sweep(); // clear bot steps down; combat bot refused by the gate

        await Assert.That(rig.Director.GetFidelity(combatBot)).IsEqualTo(BotFidelity.Full);
        await Assert.That(rig.Director.GetFidelity(clearBot)).IsEqualTo(BotFidelity.Reduced);
        await Assert.That(rig.Director.GetMetrics().TotalTransitionsRejected)
            .IsGreaterThanOrEqualTo(1);

        // The gate keeps holding on every subsequent sweep.
        rig.Sweep();
        await Assert.That(rig.Director.GetFidelity(combatBot)).IsEqualTo(BotFidelity.Full);
    }

    [Test]
    public async Task Disabled_IsStrictlyInert()
    {
        using var rig = new Rig(enabled: false);
        var bot = rig.AddActiveBot(1);
        rig.AddHuman(100, x: 10f);

        await Assert.That(rig.Director.Start()).IsFalse(); // refuses to arm
        await Assert.That(rig.Director.IsRunning).IsFalse();

        rig.Sweep(); // direct pump must be a no-op too

        var m = rig.Director.GetMetrics();
        await Assert.That(m.TotalProximitySweeps).IsEqualTo(0);
        await Assert.That(m.TotalPressureSweeps).IsEqualTo(0);
        await Assert.That(m.TotalProximityUpgrades).IsEqualTo(0);
        await Assert.That(m.TotalProximityDemotions).IsEqualTo(0);
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);
    }

    [Test]
    public async Task Sweep_RunsPressurePolicy_EveryTime()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.PressureProbe.SampleValue = new PressureSample(0.80d, 120, 0, 0, 800d, 0); // High band

        rig.Sweep();

        await Assert.That(rig.Director.Pressure).IsEqualTo(ServerPressure.High);
        var afterFirst = rig.Director.GetMetrics().TotalPressureSweeps;
        await Assert.That(afterFirst).IsEqualTo(1);

        rig.Sweep();
        await Assert.That(rig.Director.GetMetrics().TotalPressureSweeps).IsEqualTo(afterFirst + 1);

        // High band also means the wake half of the policy is live: the
        // Dormant bot's proximity upgrade to Reduced is refused (RefuseWakeAtOrAbove=High),
        // proving RefreshPressure state actually feeds the transitions.
        rig.AddHuman(100, x: 10f);
        rig.Sweep();
        rig.Sweep();
        await Assert.That(rig.Director.GetFidelity(bot)).IsEqualTo(BotFidelity.Dormant);
    }

    [Test]
    public async Task Bots_NeverEscalateEachOther_OnlyHumansCount()
    {
        using var rig = new Rig();
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        Rig.Place(rig.Bot(2), 3f); // botB embodied right next to botA
        rig.Director.TrySetFidelity(botB, BotFidelity.Reduced, "seed");

        for (var i = 0; i < 4; i++)
            rig.Sweep();

        // botA has zero HUMANS nearby — botB's embodiment must never escalate
        // it (stays Dormant, zero upgrades, zero wakes). botB itself demotes
        // back to Dormant: from the proximity tier's point of view there are
        // no humans at all.
        await Assert.That(rig.Director.GetFidelity(botA)).IsEqualTo(BotFidelity.Dormant);
        await Assert.That(rig.Director.GetFidelity(botB)).IsEqualTo(BotFidelity.Dormant);
        await Assert.That(rig.Director.GetMetrics().TotalProximityUpgrades).IsEqualTo(0);
        await Assert.That(rig.Director.GetMetrics().TotalWakes).IsEqualTo(0);
    }
}
