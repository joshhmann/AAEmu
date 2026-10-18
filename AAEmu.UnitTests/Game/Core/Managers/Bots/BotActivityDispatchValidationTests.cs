using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Plan T9 (audit L3-a) startup half: the registered module set is checked
/// against the real dispatch surface, so a module that can elect work no
/// dispatch owner performs is reported instead of discovered in the field.
/// The real-set rig mirrors the Program.cs registrations with the real module
/// types (nothing boots the real host in this repo) — it is the test that fails
/// when a module's elected name drifts away from the surface.
/// </summary>
[NotInParallel]
public class BotActivityDispatchValidationTests
{
    private sealed class StubModule(string name, params string[] declared) : IBotActivityModule
    {
        public string Name { get; } = name;
        public int Priority => 0;
        public IReadOnlyList<string> DeclaredActivities { get; } = declared;
        public BotActivityDecision CanActivate(BotActivityContext context) => BotActivityDecision.Deny("stub");
        public BotActivity Activate(BotActivityContext context) => new(Name, Name);
    }

    /// <summary>The ten modules Program.cs registers, built with their real types.</summary>
    private static List<IBotActivityModule> RealModuleSet()
    {
        var stepExecutor = new BotRoamStepExecutor();
        var behavior = new RoamRouteScheduleBehavior(stepExecutor);
        var scheduleOptions = new BotScheduleOptions { Enabled = false };
        return
        [
            new SchedulePhaseActivityModule(scheduleOptions, behavior),
            new OutOfCombatRecoveryModule(),
            new PresenceRoamActivityModule(scheduleOptions, behavior, stepExecutor),
            new ConflictJoinActivityModule(),
            new FishingContestActivityModule(new FishingContestModuleOptions()),
            new HomesteadActivityModule(),
            new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions()),
            new NeedsFarmActivityModule(new NeedsFarmModuleOptions()),
            new AssignedTravelActivityModule(new BotAssignmentRegistry(), stepExecutor),
            new IdleActivityModule(stepExecutor),
        ];
    }

    [Test]
    public async Task Validate_RealModuleSet_ReportsOnlyTheKnownFishingGap()
    {
        var report = BotActivityDispatchValidation.Validate(RealModuleSet());

        await Assert.That(report.ModuleCount).IsEqualTo(10);
        await Assert.That(report.DeclaredActivityCount).IsEqualTo(11);
        await Assert.That(report.IsClean).IsFalse();
        await Assert.That(report.Gaps.Count).IsEqualTo(1);
        await Assert.That(report.Gaps[0].ModuleName).IsEqualTo("FishingContest");
        await Assert.That(report.Gaps[0].Activity).IsEqualTo(FishingContestActivityModule.ActivityName);
        await Assert.That(report.Gaps[0].Reason).Contains("no dispatch owner");
    }

    [Test]
    public async Task Describe_NamesTheModuleAndTheActivityItCannotCarry()
    {
        var lines = BotActivityDispatchValidation
            .Describe(BotActivityDispatchValidation.Validate(RealModuleSet()))
            .ToList();

        await Assert.That(lines.Count).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("FishingContest");
        await Assert.That(lines[0]).Contains(FishingContestActivityModule.ActivityName);
    }

    [Test]
    public async Task Validate_ModuleDeclaringAnUncarriedActivity_ReportsIt()
    {
        var report = BotActivityDispatchValidation.Validate([new StubModule("Adventurer", "adventure.explore")]);

        await Assert.That(report.Gaps.Count).IsEqualTo(1);
        await Assert.That(report.Gaps[0].ModuleName).IsEqualTo("Adventurer");
        await Assert.That(report.Gaps[0].Activity).IsEqualTo("adventure.explore");
    }

    [Test]
    public async Task Validate_ModuleDeclaringNothing_IsReported()
    {
        var report = BotActivityDispatchValidation.Validate([new StubModule("Mystery")]);

        await Assert.That(report.Gaps.Count).IsEqualTo(1);
        await Assert.That(report.Gaps[0].Activity, "a module that declares nothing names no activity").IsNull();
        await Assert.That(report.Gaps[0].Reason).Contains("declares no activity");
    }

    [Test]
    public async Task Validate_CarriedDeclarations_AreClean()
    {
        var report = BotActivityDispatchValidation.Validate(
        [
            new StubModule("Farm", "needs.farm"),
            new StubModule("Schedules", SchedulePhaseActivityModule.ActivityPrefix),
        ]);

        await Assert.That(report.IsClean).IsTrue();
        await Assert.That(report.ModuleCount).IsEqualTo(2);
        await Assert.That(report.DeclaredActivityCount).IsEqualTo(2);
    }

    [Test]
    public async Task Validate_NoModules_IsCleanAndEmpty()
    {
        var report = BotActivityDispatchValidation.Validate(null);

        await Assert.That(report.IsClean).IsTrue();
        await Assert.That(report.ModuleCount).IsEqualTo(0);
        await Assert.That(report.DeclaredActivityCount).IsEqualTo(0);
    }
}
