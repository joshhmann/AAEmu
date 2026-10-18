using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T2 supported arming — the one start owner itself. <see cref="PlayerBotScheduler.Arm"/>
/// is what every production caller now uses (presence bootstrap, proximity bootstrap,
/// admin roam and lab, the E2E bridge, the startup switch), so these cases pin the
/// contract those callers rely on: the first armer is recorded and the host starts, a
/// second arming is refused and keeps the first armer's name, a blank reason still names
/// the armer, and a host disarmed in this process refuses to arm again — its scan loop
/// and worker pool are torn down and never rebuilt, so a later "arm" would claim a host
/// that can never step. The enrollment half of the switch lives in
/// <c>BotStartupArmingTests</c> and <c>BotPresenceCoordinatorTests</c>.
/// </summary>
[NotInParallel]
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
public class PlayerBotSchedulerArmingTests
{
    [Test]
    public async Task Arm_FirstCaller_RecordsTheReasonAndStartsTheHost()
    {
        var scheduler = NewScheduler();

        try
        {
            await Assert.That(scheduler.IsRunning).IsFalse();
            await Assert.That(scheduler.ArmedBy).IsNull();

            await Assert.That(scheduler.Arm("presence-demo")).IsTrue();

            await Assert.That(scheduler.IsRunning).IsTrue();
            await Assert.That(scheduler.ArmedBy).IsEqualTo("presence-demo");
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Test]
    public async Task Arm_SecondCaller_IsRefusedAndKeepsTheFirstArmer()
    {
        // A lane or a deployment can have two arming callers at once (the E2E bridge
        // arms at module init, the startup switch arms when the world is ready). The
        // second one must not steal the record: it did not arm this host.
        var scheduler = NewScheduler();

        try
        {
            await Assert.That(scheduler.Arm("e2e-bridge")).IsTrue();
            await Assert.That(scheduler.Arm("startup-arming")).IsFalse();

            await Assert.That(scheduler.ArmedBy).IsEqualTo("e2e-bridge");
            await Assert.That(scheduler.IsRunning).IsTrue();
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Test]
    public async Task Arm_BlankReason_StillArmsAndNamesTheHostArm()
    {
        var scheduler = NewScheduler();

        try
        {
            await Assert.That(scheduler.Arm("   ")).IsTrue();

            await Assert.That(scheduler.IsRunning).IsTrue();
            await Assert.That(scheduler.ArmedBy).IsEqualTo("host-arm");
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Test]
    public async Task Arm_AfterDisarm_IsRefusedWithoutRestartingTheHost()
    {
        var scheduler = NewScheduler();

        await Assert.That(scheduler.Arm("first")).IsTrue();
        var report = scheduler.Disarm("unit-test");

        await Assert.That(report.Converged).IsTrue();
        await Assert.That(scheduler.IsRunning).IsFalse();

        await Assert.That(scheduler.Arm("startup-arming")).IsFalse();
        await Assert.That(scheduler.IsRunning).IsFalse();
    }

    private static PlayerBotScheduler NewScheduler() => new(
        new PlayerBotManager(new NoopLifecycle()),
        new NullExecutor(),
        new PlayerBotSchedulerOptions
        {
            WorkerCount = 4,
            ScanInterval = TimeSpan.FromHours(1), // background loop inert; no cycle is pumped here
            StepTimeout = TimeSpan.FromSeconds(30),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            SubscribeToTickManager = false,
        });

    private sealed class NoopLifecycle : IPlayerBotLifecycleService
    {
        public NoopLifecycle() => GameplayActorTestRig.Seed();

        public bool ActivateHeadless(Character character, object? botContext) => true;

        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason)
            => true;

        public bool Deactivate(Character character, string reason) => true;
    }

    private sealed class NullExecutor : IBotStepExecutor
    {
        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
            => Task.FromResult<TimeSpan?>(null);
    }
}
