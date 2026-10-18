using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T2 supported arming — the switch itself. It arms through the one owner and
/// then hands off to the enrollment it was given, so these cases pin the
/// ordering and the honesty of the result, not the enrollment (that is
/// <c>BotPresenceCoordinatorTests</c>).
/// </summary>
[NotInParallel]
public class BotStartupArmingTests
{
    private static BotPresenceCoordinator.BotPresenceConfig Config(int botCount = 2) => new(
        BotCount: botCount,
        ZoneId: 9,
        HomePosition: default,
        RoamRadius: 30f,
        RoamSpeed: 2.5f,
        Level: 5,
        NamePrefix: "Startup",
        AccountPrefix: BotStartupArmingOptions.AccountPrefix,
        MaxPresenceBots: 10);

    private static BotPresenceCoordinator.BotEnrollmentOutcome Enrolled(int count = 2, int adopted = 0)
        => new(count, adopted, AlreadyEmbodied: false);

    [Test]
    public async Task Enroll_ArmsThroughTheOneOwner_ThenRunsTheEnrollment()
    {
        var scheduler = new RigScheduler();
        var calls = 0;
        BotPresenceCoordinator.BotPresenceConfig? seen = null;

        var result = BotStartupArming.Enroll(scheduler, Config(3), config =>
        {
            seen = config;
            calls++;
            return Enrolled(3);
        });

        await Assert.That(scheduler.ArmCalls).IsEqualTo(1);
        await Assert.That(scheduler.ArmedBy).IsEqualTo(BotStartupArming.Owner);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(seen!.BotCount).IsEqualTo(3);
        await Assert.That(result.Armed).IsTrue();
        await Assert.That(result.HostRunning).IsTrue();
        await Assert.That(result.Enrolled).IsEqualTo(3);
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result.Converged).IsTrue();
        await Assert.That(result.Message).Contains("armed the bot host and enrolled 3 bot(s) of a roster of up to 3");
    }

    [Test]
    public async Task Enroll_ReportsTheCountTheEnrollmentDid_NotTheRosterSize()
    {
        // Review D1: the enrollment can come back having enrolled nothing (the
        // coordinator skips when the world already holds enough bots). The
        // switch must report that, not the roster size it was asked for.
        var scheduler = new RigScheduler();

        var result = BotStartupArming.Enroll(scheduler, Config(2),
            _ => new BotPresenceCoordinator.BotEnrollmentOutcome(0, 0, AlreadyEmbodied: true));

        await Assert.That(result.Armed).IsTrue();
        await Assert.That(result.Enrolled).IsEqualTo(0);
        await Assert.That(result.Message).Contains("nothing enrolled (idempotent)");
        await Assert.That(result.Message).DoesNotContain("enrolled 2");
    }

    [Test]
    public async Task Enroll_EnrollmentReportsNoBots_IsReportedNotClaimed()
    {
        var scheduler = new RigScheduler();

        var result = BotStartupArming.Enroll(scheduler, Config(2), _ => default);

        await Assert.That(result.Armed).IsTrue();
        await Assert.That(result.Enrolled).IsEqualTo(0);
        await Assert.That(result.Message).Contains("enrolled no bot");
    }

    [Test]
    public async Task Enroll_ReEnrollmentInProcess_IsNamedInTheReport()
    {
        var scheduler = new RigScheduler();

        var result = BotStartupArming.Enroll(scheduler, Config(2), _ => Enrolled(2, adopted: 2));

        await Assert.That(result.Enrolled).IsEqualTo(2);
        await Assert.That(result.Message).Contains("2 of them re-enrolled in-process (no new provisioning)");
    }

    [Test]
    public async Task Enroll_HostAlreadyArmedByAnotherCaller_ReportsThatOwnerAndStillEnrolls()
    {
        // A deployment that turns the switch on while another caller (presence
        // demo, proximity bootstrap) already armed the host must still enroll
        // its roster — it just does not get to claim it armed anything.
        var scheduler = new RigScheduler { AlreadyArmed = true, ArmedBy = "presence-demo" };
        var calls = 0;

        var result = BotStartupArming.Enroll(scheduler, Config(2), _ =>
        {
            calls++;
            return Enrolled(2);
        });

        await Assert.That(scheduler.ArmCalls).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(result.Armed).IsFalse();
        await Assert.That(result.HostRunning).IsTrue();
        await Assert.That(result.Enrolled).IsEqualTo(2);
        await Assert.That(result.ArmedBy).IsEqualTo("presence-demo");
        await Assert.That(result.Message).Contains("already armed by presence-demo");
    }

    [Test]
    public async Task Enroll_HostDisarmed_RefusesAndSkipsTheEnrollment()
    {
        // Arming after a disarm is terminal for the process: enrolling then
        // would only create characters that never step.
        var scheduler = new RigScheduler { Running = false, ArmRefused = true };
        var calls = 0;

        var result = BotStartupArming.Enroll(scheduler, Config(2), _ =>
        {
            calls++;
            return Enrolled();
        });

        await Assert.That(scheduler.ArmCalls).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(result.Armed).IsFalse();
        await Assert.That(result.HostRunning).IsFalse();
        await Assert.That(result.Enrolled).IsEqualTo(0);
        await Assert.That(result.Message).Contains("not running (arming was refused)");
    }

    [Test]
    public async Task Enroll_ArmSucceededButTheHostStoppedBeforeEnrolling_ReportsTheRaceNotARefusal()
    {
        // Review D3: Arm returned true and a concurrent Disarm landed between
        // the two reads. Skipping the enrollment is right; reporting it as a
        // refusal is not.
        var scheduler = new RigScheduler { Running = false };
        var calls = 0;

        var result = BotStartupArming.Enroll(scheduler, Config(2), _ =>
        {
            calls++;
            return Enrolled();
        });

        await Assert.That(scheduler.ArmCalls).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(result.Armed).IsFalse();
        await Assert.That(result.Message).Contains("stopped while the startup enrollment was starting");
        await Assert.That(result.Message).DoesNotContain("refused");
    }

    [Test]
    public async Task Enroll_HostStoppedWhileEnrolling_DisarmsAgainToConverge()
    {
        // Review D4: the enrollment runs on a detached boot task that nothing
        // awaits, so a shutdown can land while it is still enrolling. Disarm
        // converges on a fresh snapshot, so one more pass after the enrollment
        // cannot leave a bot Active behind a stopped host.
        var scheduler = new RigScheduler();

        var result = BotStartupArming.Enroll(scheduler, Config(2), _ =>
        {
            scheduler.Running = false;
            return Enrolled(1);
        });

        await Assert.That(scheduler.DisarmCalls).IsEqualTo(1);
        await Assert.That(scheduler.DisarmReason).IsEqualTo("startup-arming-after-stop");
        await Assert.That(result.Converged).IsTrue();
        await Assert.That(result.HostRunning).IsFalse();
        await Assert.That(result.Enrolled).IsEqualTo(1);
        await Assert.That(result.Message).Contains("the host stopped while the enrollment was in flight and was disarmed again (converged=True)");
    }

    [Test]
    public async Task Enroll_HostStoppedWhileEnrolling_AndTheDisarmDidNotConverge_IsReported()
    {
        var scheduler = new RigScheduler { DisarmFailures = 1 };

        var result = BotStartupArming.Enroll(scheduler, Config(2), _ =>
        {
            scheduler.Running = false;
            return Enrolled(1);
        });

        await Assert.That(scheduler.DisarmCalls).IsEqualTo(1);
        await Assert.That(result.Converged).IsFalse();
        await Assert.That(result.Message).Contains("disarmed again (converged=False)");
    }

    [Test]
    public async Task Enroll_NullArguments_Throw()
    {
        var scheduler = new RigScheduler();

        await Assert.That(() => BotStartupArming.Enroll(null!, Config(), _ => Enrolled()))
            .Throws<ArgumentNullException>();
        await Assert.That(() => BotStartupArming.Enroll(scheduler, null!, _ => Enrolled()))
            .Throws<ArgumentNullException>();
        await Assert.That(() => BotStartupArming.Enroll(scheduler, Config(), null!))
            .Throws<ArgumentNullException>();
    }

    private sealed class RigScheduler : IPlayerBotScheduler
    {
        public int ArmCalls;
        public string? ArmedBy { get; set; }
        public bool AlreadyArmed;
        public bool ArmRefused;
        public bool Running = true;
        public int DisarmCalls;
        public string? DisarmReason;
        public int DisarmFailures;

        public bool Arm(string reason)
        {
            ArmCalls++;
            if (AlreadyArmed || ArmRefused)
                return false;
            ArmedBy ??= reason;
            return true;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public BotHostTeardownReport Disarm(string reason)
        {
            DisarmCalls++;
            DisarmReason = reason;
            return new BotHostTeardownReport(reason, false, 0, DisarmFailures == 0 ? 1 : 0, DisarmFailures, []);
        }

        public bool Wake(uint characterId) => true;
        public bool WakeAt(uint characterId, DateTime utcDue) => true;
        public bool WakeAfter(uint characterId, TimeSpan delay) => true;
        public bool IsLeased(uint characterId) => false;
        public BotEnrollmentSnapshot GetEnrollmentSnapshot(uint characterId)
            => new(false, "-", null, false, null, null, null, 0, 0, 0, 0, false, 0);
        public PlayerBotSchedulerMetrics GetMetrics() => new(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0);
        public int WorkerCount => 4;
        public bool IsRunning => Running;
    }
}
