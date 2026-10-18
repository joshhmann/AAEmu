using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using System.Reflection;

using TUnit.Core;
using TUnit.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// A1 converging teardown (card player-ai-foundation-correctness): the bot
/// host has ONE stop owner — <see cref="IPlayerBotScheduler.Disarm"/> — and a
/// stopped host leaves no enrolled bot Active, drops the queued steps the
/// stopped execution boundary can no longer run WITH a record (releasing their
/// leases), and reports a refused leave instead of keeping the bot silently.
///
/// Real components throughout: the real registry, the real scheduler (tick
/// subscription off, cycles pumped manually) and the real lease/queue
/// machinery; only the lifecycle seam and the step executor are recording
/// fakes. No step ever executes here — that is the point: the game loop is
/// gone, so queued work must be dropped, not run off-boundary.
/// </summary>
[NotInParallel]
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
public class PlayerBotSchedulerDisarmTests
{
    [Test]
    public async Task Disarm_StopsTheHost_DeactivatesEveryEnrolledBot_AndCountsDroppedSteps()
    {
        using var rig = new Rig();
        var first = rig.AddActiveBot(1, "disarm-one");
        var second = rig.AddActiveBot(2, "disarm-two");
        rig.QueueStep(first);
        rig.QueueStep(second);
        await Assert.That(rig.Scheduler.GetMetrics().InFlight).IsEqualTo(2);

        var report = rig.Scheduler.Disarm("unit-test");

        await Assert.That(report.SchedulerWasRunning).IsTrue();
        await Assert.That(rig.Scheduler.IsRunning).IsFalse();
        await Assert.That(report.Converged, report.ToString()).IsTrue();
        await Assert.That(report.BotsDeactivated).IsEqualTo(2);
        await Assert.That(report.BotsFailed).IsEqualTo(0);
        await Assert.That(report.Failures).IsEmpty();

        // Ownership converged: no bot is left Active, and the records stay for
        // diagnostics (Deactivated, not silently dropped from the registry).
        await Assert.That(rig.Manager.GetActive()).IsEmpty();
        await Assert.That(rig.Manager.Count).IsEqualTo(2);

        // The queued work the stopped boundary could no longer run is dropped
        // WITH a record, its leases are released, and nothing executed.
        await Assert.That(report.QueuedStepsDropped).IsEqualTo(2);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsDropped).IsEqualTo(2);
        await Assert.That(rig.Scheduler.IsLeased(first)).IsFalse();
        await Assert.That(rig.Scheduler.IsLeased(second)).IsFalse();
        await Assert.That(rig.Executor.Steps).IsEqualTo(0);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(0);
    }

    [Test]
    public async Task Disarm_IsIdempotent_AndRefusesLaterWakes()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1, "disarm-once");
        rig.QueueStep(bot);

        var first = rig.Scheduler.Disarm("unit-test");
        var second = rig.Scheduler.Disarm("unit-test-again");

        await Assert.That(first.Converged, first.ToString()).IsTrue();
        await Assert.That(second.Converged, second.ToString()).IsTrue();
        await Assert.That(second.SchedulerWasRunning).IsFalse();
        await Assert.That(second.BotsDeactivated).IsEqualTo(0);
        await Assert.That(second.BotsFailed).IsEqualTo(0);
        await Assert.That(second.QueuedStepsDropped).IsEqualTo(0);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsDropped).IsEqualTo(first.QueuedStepsDropped);

        // A stopped host takes no new work: the wake is refused, not queued.
        await Assert.That(rig.Scheduler.Wake(bot)).IsFalse();
    }

    [Test]
    public async Task Disarm_WhenPhysicalLeaveIsRefused_ReportsTheFailure_AndDoesNotClaimConvergence()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1, "disarm-refused");
        rig.Lifecycle.RefuseDeactivate = true;

        var report = rig.Scheduler.Disarm("unit-test");

        await Assert.That(report.Converged).IsFalse();
        await Assert.That(report.BotsDeactivated).IsEqualTo(0);
        await Assert.That(report.BotsFailed).IsEqualTo(1);
        await Assert.That(report.Failures).HasCount(1);
        await Assert.That(report.Failures[0]).Contains(bot.ToString());
        await Assert.That(rig.Manager.GetDiagnostics().FailedDeactivations).IsEqualTo(1);
    }

    /// <summary>
    /// The leave MUST be asked for while the host is still running: the native
    /// retirement seam refuses outright once the scheduler is not running
    /// (<c>PlayerBotScheduler.cs:321</c>) and its step runs on the execution
    /// boundary the stop tears down. Asking after the stop left every enrolled
    /// bot in <c>DeactivationFailed</c> on the live lane (2026-10-16).
    /// </summary>
    [Test]
    public async Task Disarm_AsksEachBotToLeave_WhileTheHostIsStillRunning()
    {
        using var rig = new Rig();
        rig.AddActiveBot(1, "disarm-order-one");
        rig.AddActiveBot(2, "disarm-order-two");

        var report = rig.Scheduler.Disarm("unit-test");

        await Assert.That(report.Converged, report.ToString()).IsTrue();
        await Assert.That(report.BotsDeactivated).IsEqualTo(2);
        await Assert.That(rig.Lifecycle.HostWasRunningAtDeactivate).IsTrue();
        await Assert.That(rig.Scheduler.IsRunning).IsFalse();
    }

    /// <summary>
    /// A bot already left un-safely-deactivated BEFORE the disarm (a refused
    /// leave the manager recorded as <c>DeactivationFailed</c> — a terminal
    /// state nothing retries) must not be skipped silently: skipping it would
    /// let the report claim convergence over a bot that never left (code
    /// review 2026-10-16, F2).
    /// </summary>
    [Test]
    public async Task Disarm_CountsABotThatWasAlreadyFailed_AndDoesNotClaimConvergence()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1, "disarm-already-failed");
        rig.Lifecycle.RefuseDeactivate = true;
        await Assert.That(rig.Manager.Deactivate(bot, "seed-refused-leave")).IsFalse();
        rig.Lifecycle.RefuseDeactivate = false;

        var report = rig.Scheduler.Disarm("unit-test");

        await Assert.That(report.Converged).IsFalse();
        await Assert.That(report.BotsDeactivated).IsEqualTo(0);
        await Assert.That(report.BotsFailed).IsEqualTo(1);
        await Assert.That(report.Failures[0]).Contains(bot.ToString());
    }

    private sealed class Rig : IDisposable
    {
        public RecordingLifecycle Lifecycle { get; } = new();
        public RecordingExecutor Executor { get; } = new();
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }

        public Rig()
        {
            SeedWorldManager();
            Manager = new PlayerBotManager(Lifecycle);
            Scheduler = new PlayerBotScheduler(Manager, Executor, new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1), // loop inert in tests; cycles pumped manually
                StepTimeout = TimeSpan.FromSeconds(30),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false, // the marshal drain is pumped manually
            });
            Scheduler.Start();
            Lifecycle.HostRunningProbe = () => Scheduler.IsRunning;
        }

        /// <summary>Spawns + activates a bot through the real manager registry.</summary>
        public uint AddActiveBot(uint id, string name)
        {
            Manager.Spawn(new Character(new UnitCustomModelParams()) { Id = id, Name = name, MaxHp = 100, Hp = 100 }, "disarm-rig");
            if (!Manager.Activate(id, null, "disarm-rig"))
                throw new InvalidOperationException($"rig activation failed for character {id}");
            return id;
        }

        /// <summary>
        /// Puts one due step for the bot on the marshal channel by pumping one
        /// scan cycle. The step is NOT drained afterwards: it must still be
        /// queued when the host is disarmed.
        /// </summary>
        public void QueueStep(uint id)
        {
            if (!Scheduler.WakeAt(id, DateTime.UtcNow.AddMilliseconds(-1)))
                throw new InvalidOperationException($"rig wake was refused for character {id}");
            Scheduler.RunScanCycle();
            if (!Scheduler.IsLeased(id))
                throw new InvalidOperationException($"the pumped scan cycle did not lease character {id}");
        }

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();

        /// <summary>
        /// The registry's own deactivate path resolves the live world to prove
        /// the character has no other embodiment (WorldManager.Instance), and
        /// that singleton is DI-only — no parameterless constructor. Seed it
        /// once, never replacing an established instance; an empty world means
        /// "not embodied anywhere else", which is what this rig is.
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
        /// <summary>When set, the physical leave/save refuses (the bot must stay non-Deactivated).</summary>
        public bool RefuseDeactivate { get; set; }

        /// <summary>
        /// Reads the host's own running flag, so the fake can witness WHEN the
        /// leave was asked for. The production retirement seam refuses while
        /// the scheduler is not running (<c>PlayerBotScheduler.cs:321</c>), so
        /// the ordering this records is load-bearing, not cosmetic.
        /// </summary>
        public Func<bool>? HostRunningProbe { get; set; }

        /// <summary>Host running flag observed at the last leave request.</summary>
        public bool? HostWasRunningAtDeactivate { get; private set; }

        public bool ActivateHeadless(Character character, object? botContext) => true;

        /// <summary>
        /// Part of the native-retirement seam in trees that carry it; inert
        /// here — this slice does not depend on retirement behaviour, only on
        /// the stop owner's convergence and its recorded refusals.
        /// </summary>
        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason) => true;

        public bool Deactivate(Character character, string reason)
        {
            HostWasRunningAtDeactivate = HostRunningProbe?.Invoke();
            return !RefuseDeactivate;
        }
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
