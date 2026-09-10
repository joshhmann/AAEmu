using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.UnitTests.Game.Housing;
namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Fishing-contest module rig (M9.5 B3 activity): CanActivate gate matrix.
/// Fail-pre discipline: each deny case FAILS if the module allows, each
/// allow case FAILS if it denies or names the activity unstably.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class FishingContestActivityModuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static FishingContestModuleOptions OpenOptions() => new()
    {
        Enabled = true,
        OpensAt = Now.AddHours(-1),
        ClosesAt = Now.AddHours(1),
    };

    private static FishingContestActivityModule OpenModule(Func<ServerPressure>? pressureProbe = null)
        => new(OpenOptions(), utcNow: () => Now, pressureProbe: pressureProbe);

    private static PlayerBotRuntime NewBot(string name)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.JoinActorWorld(session, actor);
        return new PlayerBotRuntime(actor.Character, "contest-rig");
    }

    private static BotActivityContext ContextFor(PlayerBotRuntime bot)
        => new() { Bot = bot, GameHour = 12f, ActiveActivity = null };

    [Test]
    public async Task Priority_Is60_BetweenRoamAndConflict()
    {
        // PresenceRoam 50 < contest 60 < ConflictJoin 75: an open contest
        // draws bots off patrol, war still preempts play, schedules own all.
        var priority = new FishingContestActivityModule(OpenOptions()).Priority;
        await Assert.That(priority).IsEqualTo(60);
        await Assert.That(priority > 50 && priority < 75).IsTrue();
    }

    [Test]
    public async Task CanActivate_DisabledByDefault_Denies()
    {
        var bot = NewBot("m95m-off");
        var module = new FishingContestActivityModule(new FishingContestModuleOptions());

        var decision = module.CanActivate(ContextFor(bot));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot!.Contains("disabled")).IsTrue();
    }

    [Test]
    public async Task CanActivate_InBattle_Denies()
    {
        var bot = NewBot("m95m-battle");
        bot.Character.IsInBattle = true;
        var module = OpenModule();

        var decision = module.CanActivate(ContextFor(bot));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot!.Contains("battle")).IsTrue();
    }

    [Test]
    public async Task CanActivate_OutsideWindow_Denies()
    {
        var bot = NewBot("m95m-closed");
        var module = new FishingContestActivityModule(
            OpenOptions() with { OpensAt = Now.AddHours(1), ClosesAt = Now.AddHours(2) },
            utcNow: () => Now);

        var decision = module.CanActivate(ContextFor(bot));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot!.Contains("not open")).IsTrue();
    }

    [Test]
    public async Task CanActivate_OpenWindow_AllowsStableContestActivity()
    {
        var bot = NewBot("m95m-open");
        var module = OpenModule();

        var first = module.CanActivate(ContextFor(bot));
        var second = module.CanActivate(ContextFor(bot));

        await Assert.That(first.CanActivate).IsTrue();
        await Assert.That(first.ActivityName).IsEqualTo(FishingContestActivityModule.ActivityName);
        await Assert.That(first.ActivityName).IsEqualTo("fishing.contest");
        await Assert.That(second.ActivityName).IsEqualTo(first.ActivityName);
    }

    [Test]
    public async Task CanActivate_HighPressure_Denies()
    {
        var bot = NewBot("m95m-hot");
        var module = OpenModule(pressureProbe: () => ServerPressure.High);

        var decision = module.CanActivate(ContextFor(bot));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot!.Contains("pressure")).IsTrue();
    }

    [Test]
    public async Task CanActivate_PressureBand_Allows()
    {
        // The hold sits at High: a merely warm world still fishes.
        var bot = NewBot("m95m-warm");
        var module = OpenModule(pressureProbe: () => ServerPressure.Pressure);

        var decision = module.CanActivate(ContextFor(bot));

        await Assert.That(decision.CanActivate).IsTrue();
    }

    [Test]
    public async Task Activate_ReturnsContestActivity()
    {
        var bot = NewBot("m95m-act");
        var module = OpenModule();

        var activity = module.Activate(ContextFor(bot));

        await Assert.That(activity.Name).IsEqualTo("fishing.contest");
        await Assert.That(activity.ModuleName).IsEqualTo("FishingContest");
    }

    [Test]
    public async Task FromEnvironment_DefaultOff_OptInWithFlag()
    {
        var previous = Environment.GetEnvironmentVariable("AAEMU_FISHING_CONTEST_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("AAEMU_FISHING_CONTEST_ENABLED", null);
            await Assert.That(FishingContestModuleOptions.FromEnvironment().Enabled).IsFalse();

            Environment.SetEnvironmentVariable("AAEMU_FISHING_CONTEST_ENABLED", "1");
            await Assert.That(FishingContestModuleOptions.FromEnvironment().Enabled).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_FISHING_CONTEST_ENABLED", previous);
        }
    }
}
