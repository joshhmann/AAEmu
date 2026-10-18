using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using System.Diagnostics;
using System.Reflection;

using TUnit.Core;
using TUnit.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Per-tick execution budget (card player-ai-foundation-correctness, L2-b):
/// one game-loop drain runs at most <see cref="PlayerBotSchedulerOptions.MaxStepsPerTick"/>
/// bot steps. Steps over budget stay queued for a later tick — deferred, never
/// dropped, and their leases stay held so the bot is still accounted as
/// in-flight. Before the budget, a crowded tick ran EVERY queued step inline on
/// the game-loop thread.
///
/// Real registry, real scheduler, real marshal queue and lease machinery; only
/// the step executor is a recording fake. Cycles and the drain are pumped
/// manually (tick subscription off), so the arithmetic is deterministic.
/// </summary>
[NotInParallel]
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
public class PlayerBotSchedulerTickBudgetTests
{
    [Test]
    public async Task TickDrain_RunsAtMostMaxStepsPerTick_AndDefersTheRest()
    {
        using var rig = new Rig(maxStepsPerTick: 2);
        var bots = new[] { 1u, 2u, 3u, 4u, 5u };
        foreach (var id in bots)
            rig.AddActiveBot(id, $"budget-{id}");
        foreach (var id in bots)
            rig.WakeDue(id);
        rig.Scheduler.RunScanCycle();

        // Worker threads marshal asynchronously: wait for the batch to land in
        // the tick queue before draining, without draining it ourselves.
        await rig.WaitForQueueAsync(bots.Length);

        rig.Scheduler.DrainTickQueue();
        await Assert.That(rig.Executor.Steps).IsEqualTo(2);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsDeferred).IsEqualTo(3);
        await Assert.That(rig.Scheduler.TickQueueDepth).IsEqualTo(3);

        // Deferred is not lost: the steps are still queued and still leased.
        await Assert.That(rig.Scheduler.GetMetrics().InFlight).IsEqualTo(3);

        rig.Scheduler.DrainTickQueue();
        await Assert.That(rig.Executor.Steps).IsEqualTo(4);
        // The metric counts deferral EVENTS: three steps waited out the first
        // tick, one waited out the second (3 + 1), so a step that waits twice
        // is counted twice.
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsDeferred).IsEqualTo(4);

        rig.Scheduler.DrainTickQueue();
        await Assert.That(rig.Executor.Steps).IsEqualTo(5);

        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(metrics.TotalStepsRun).IsEqualTo(5);
        await Assert.That(metrics.TotalStepsDropped).IsEqualTo(0);
        await Assert.That(metrics.TotalStepsDeferred).IsEqualTo(4);
        await Assert.That(metrics.InFlight).IsEqualTo(0);
        await Assert.That(rig.Scheduler.TickQueueDepth).IsEqualTo(0);
    }

    [Test]
    public async Task TickDrain_ZeroBudget_RunsEveryQueuedStep()
    {
        // 0 (or negative) keeps the pre-budget behaviour: no deferral.
        using var rig = new Rig(maxStepsPerTick: 0);
        var bots = new[] { 1u, 2u, 3u };
        foreach (var id in bots)
            rig.AddActiveBot(id, $"unbounded-{id}");
        foreach (var id in bots)
            rig.WakeDue(id);
        rig.Scheduler.RunScanCycle();
        await rig.WaitForQueueAsync(bots.Length);

        rig.Scheduler.DrainTickQueue();

        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(rig.Executor.Steps).IsEqualTo(3);
        await Assert.That(metrics.TotalStepsRun).IsEqualTo(3);
        await Assert.That(metrics.TotalStepsDeferred).IsEqualTo(0);
        await Assert.That(metrics.InFlight).IsEqualTo(0);
    }

    private sealed class Rig : IDisposable
    {
        public RecordingExecutor Executor { get; } = new();
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }

        public Rig(int maxStepsPerTick)
        {
            SeedWorldManager();
            Manager = new PlayerBotManager(new RecordingLifecycle());
            Scheduler = new PlayerBotScheduler(Manager, Executor, new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1), // loop inert in tests; cycles pumped manually
                StepTimeout = TimeSpan.FromSeconds(30),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false, // the marshal drain is pumped manually
                MaxStepsPerTick = maxStepsPerTick,
            });
            Scheduler.Start();
        }

        /// <summary>Spawns + activates a bot through the real manager registry.</summary>
        public void AddActiveBot(uint id, string name)
        {
            Manager.Spawn(new Character(new UnitCustomModelParams()) { Id = id, Name = name, MaxHp = 100, Hp = 100 }, "budget-rig");
            if (!Manager.Activate(id, null, "budget-rig"))
                throw new InvalidOperationException($"rig activation failed for character {id}");
        }

        /// <summary>Marks the bot's wake due; the scan cycle (pumped by the test) leases it.</summary>
        public void WakeDue(uint id)
        {
            if (!Scheduler.WakeAt(id, DateTime.UtcNow.AddMilliseconds(-1)))
                throw new InvalidOperationException($"rig wake was refused for character {id}");
        }

        /// <summary>
        /// Waits until the worker threads have marshalled a batch into the tick
        /// queue. Deliberately does NOT pump the drain — draining is the
        /// behaviour under test.
        /// </summary>
        public async Task WaitForQueueAsync(int depth)
        {
            var deadline = Stopwatch.StartNew();
            while (Scheduler.TickQueueDepth < depth)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException(
                        $"rig wait failed: tick queue depth {Scheduler.TickQueueDepth} < {depth}");
                await Task.Delay(5);
            }
        }

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();

        /// <summary>
        /// The registry resolves the live world on its deactivate path
        /// (WorldManager.Instance), and that singleton is DI-only — no
        /// parameterless constructor. Seed it once, never replacing an
        /// established instance.
        /// </summary>
        private static void SeedWorldManager()
        {
            if (Singleton<WorldManager>.PeekInstance is not null)
                return;

            var worldManager = new WorldManager(
                Mock.Of<ITickManager>().Object,
                Mock.Of<IWorldIdManager>().Object,
                new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
                new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
                new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

            var field = typeof(Singleton<WorldManager>)
                .GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Cannot locate Singleton<WorldManager>.s_instance");
            field.SetValue(null, worldManager);
        }
    }

    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool ActivateHeadless(Character character, object? botContext) => true;

        /// <summary>Part of the native-retirement seam in trees that carry it; inert here.</summary>
        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason) => true;

        public bool Deactivate(Character character, string reason) => true;
    }

    private sealed class RecordingExecutor : IBotStepExecutor
    {
        public int Steps { get; private set; }

        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            Steps++;
            return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(1));
        }
    }
}
