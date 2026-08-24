using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// G3-B3 goal-arbiter rigs: priority ordering, CanActivate fall-through,
/// single-active enforcement per bot, zero-module inertness, and the
/// decorator executor seam. Recording fakes throughout (the
/// BotScheduleServiceTests convention); the schedule-phase module round-trip
/// uses a recording IBotScheduleBehavior + in-memory metadata so the C1
/// phase semantics are locked without engine singletons.
/// </summary>
[NotInParallel]
public class BotGoalArbiterTests
{
    #region Fakes

    /// <summary>Priority-ordered module fake with an activatable decision.</summary>
    private sealed class FakeModule : IBotActivityModule
    {
        public string Name { get; }
        public int Priority { get; }
        public BotActivityDecision Decision { get; set; }
        public int Activations { get; private set; }

        public FakeModule(string name, int priority, BotActivityDecision decision)
        {
            Name = name;
            Priority = priority;
            Decision = decision;
        }

        public BotActivityDecision CanActivate(BotActivityContext context) => Decision;

        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity(Decision.ActivityName!, Name);
        }
    }

    private sealed class RecordingBehavior : IBotScheduleBehavior
    {
        public List<uint> Roams { get; } = [];
        public List<(uint BotId, float X, float Y)> Moves { get; } = [];

        public void ResumeRoam(PlayerBotRuntime bot, string scheduleJson, System.Numerics.Vector3 fallbackCenter) =>
            Roams.Add(bot.CharacterId);

        public void MoveToAnchor(PlayerBotRuntime bot, System.Numerics.Vector3 target) =>
            Moves.Add((bot.CharacterId, target.X, target.Y));
    }

    /// <summary>Headless bot runtime over the ordinary Character record.</summary>
    private static PlayerBotRuntime NewBot(string name)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, 1, Race.Nuian);
        return new PlayerBotRuntime(session.Character, "arbiter-tests");
    }

    private static BotActivityContext ContextFor(PlayerBotRuntime bot, string? active = null, float hour = 12f) =>
        new() { Bot = bot, GameHour = hour, ActiveActivity = active };

    #endregion

    #region Arbiter core

    [Test]
    public async Task Arbitrate_HighestPriorityWins()
    {
        var low = new FakeModule("Low", 10, BotActivityDecision.Allow("low.act"));
        var high = new FakeModule("High", 100, BotActivityDecision.Allow("high.act"));
        var arbiter = new BotGoalArbiter([low, high]);
        var bot = NewBot("priority-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.ModuleName).IsEqualTo("High");
        await Assert.That(high.Activations).IsEqualTo(1);
        await Assert.That(low.Activations, "lower-priority module must never activate").IsEqualTo(0);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("high.act");
    }

    [Test]
    public async Task Arbitrate_CanActivateFalse_FallsThroughToNext()
    {
        var denied = new FakeModule("Denied", 100, BotActivityDecision.Deny("gate off"));
        var fallback = new FakeModule("Fallback", 50, BotActivityDecision.Allow("fallback.act"));
        var arbiter = new BotGoalArbiter([denied, fallback]);
        var bot = NewBot("fallthrough-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.ModuleName).IsEqualTo("Fallback");
        await Assert.That(denied.Activations, "denied module must not be activated").IsEqualTo(0);
    }

    [Test]
    public async Task Arbitrate_SingleActive_SteadyStateNeverReactivates()
    {
        var module = new FakeModule("Only", 50, BotActivityDecision.Allow("only.act"));
        var arbiter = new BotGoalArbiter([module]);
        var bot = NewBot("steady-bot");

        var first = arbiter.Arbitrate(bot, gameHour: 12f);
        var second = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(first.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(second.Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(module.Activations, "activation side effects run ONCE").IsEqualTo(1);
        await Assert.That(arbiter.TransitionCount).IsEqualTo(1);
    }

    [Test]
    public async Task Arbitrate_ActivityChangeWithinModule_RearmsOnce()
    {
        // Same winning MODULE, changing ACTIVITY name (e.g. schedule.work ->
        // schedule.rest): the arbiter's unit of change is the activity name,
        // so the module re-arms exactly once per change.
        var module = new FakeModule("Schedules", 100, BotActivityDecision.Allow("schedule.work"));
        var arbiter = new BotGoalArbiter([module]);
        var bot = NewBot("phase-bot");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        module.Decision = BotActivityDecision.Allow("schedule.rest");
        var changed = arbiter.Arbitrate(bot, 23f);

        await Assert.That(changed.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(changed.Activity!.Name).IsEqualTo("schedule.rest");
        await Assert.That(module.Activations).IsEqualTo(2);
        await Assert.That(arbiter.TransitionCount).IsEqualTo(2);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("schedule.rest");
    }

    [Test]
    public async Task Arbitrate_ZeroModules_IsInert()
    {
        var arbiter = new BotGoalArbiter();
        var bot = NewBot("inert-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.None);
        await Assert.That(arbiter.ModuleCount).IsEqualTo(0);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId), "no state may be touched").IsNull();
        await Assert.That(arbiter.TransitionCount).IsEqualTo(0);
    }

    [Test]
    public async Task Arbitrate_NoCandidate_ClearsMemory_WorldUntouched()
    {
        var module = new FakeModule("Flaky", 50, BotActivityDecision.Allow("flaky.act"));
        var arbiter = new BotGoalArbiter([module]);
        var bot = NewBot("nocandidate-bot");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        module.Decision = BotActivityDecision.Deny("combat");
        var none = arbiter.Arbitrate(bot, 12f);

        await Assert.That(none.Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId), "stale memory must drop").IsNull();

        // Re-allowed later -> clean re-activation (not Unchanged against the dropped name).
        module.Decision = BotActivityDecision.Allow("flaky.act");
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(module.Activations).IsEqualTo(2);
    }

    [Test]
    public async Task Arbitrate_EqualPriorities_FirstRegisteredWins()
    {
        var first = new FakeModule("First", 50, BotActivityDecision.Allow("first.act"));
        var second = new FakeModule("Second", 50, BotActivityDecision.Allow("second.act"));
        var arbiter = new BotGoalArbiter();
        arbiter.Register(first);
        arbiter.Register(second);
        var bot = NewBot("tie-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Activity!.ModuleName).IsEqualTo("First");
        await Assert.That(first.Activations).IsEqualTo(1);
    }

    [Test]
    public async Task Arbitrate_PerBotSingleActive_IndependentKeys()
    {
        var module = new FakeModule("Shared", 50, BotActivityDecision.Allow("shared.act"));
        var arbiter = new BotGoalArbiter([module]);
        var botA = NewBot("iso-a");
        var botB = NewBot("iso-b");

        await Assert.That(arbiter.Arbitrate(botA, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(module.Activations).IsEqualTo(1);

        // Bot B has its own memory: its FIRST pass activates even though the
        // module+activity already won for bot A.
        var bResult = arbiter.Arbitrate(botB, 12f);

        await Assert.That(bResult.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(module.Activations).IsEqualTo(2);
        await Assert.That(arbiter.GetActiveActivity(botA.CharacterId)).IsEqualTo("shared.act");
        await Assert.That(arbiter.GetActiveActivity(botB.CharacterId)).IsEqualTo("shared.act");
    }

    #endregion

    #region First-party modules

    [Test]
    public async Task SchedulePhaseModule_DisabledGate_Declines()
    {
        var module = new SchedulePhaseActivityModule(
            new BotScheduleOptions { Enabled = false },
            new RecordingBehavior());
        var bot = NewBot("sched-off-bot");

        var decision = module.CanActivate(ContextFor(bot));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).IsNotNull();
    }

    [Test]
    public async Task SchedulePhaseModule_WorkThenRest_AppliesExistingBehaviorSeam()
    {
        var behavior = new RecordingBehavior();
        var metadata = new Dictionary<uint, PlayerBotMetadata>();
        var written = new Dictionary<uint, string>();
        var module = new SchedulePhaseActivityModule(
            new BotScheduleOptions { Enabled = true },
            behavior,
            metadataProvider: id => metadata.TryGetValue(id, out var m) ? m : PlayerBotMetadata.Empty(id),
            scheduleWriter: (id, json) => written[id] = json);
        var bot = NewBot("sched-bot");

        // Midday (template: work 08-18) -> Work = the ordinary roam loop.
        var work = module.CanActivate(ContextFor(bot, hour: 12f));
        await Assert.That(work.ActivityName).IsEqualTo("schedule.work");
        module.Activate(ContextFor(bot, hour: 12f));
        await Assert.That(behavior.Roams.Count).IsEqualTo(1);
        await Assert.That(behavior.Moves, "work must NOT walk home").IsEmpty();

        // Night (rest 22-06, hysteresis long passed) -> Rest = MoveToAnchor(home).
        module.Activate(ContextFor(bot, hour: 23f, active: "schedule.work"));

        await Assert.That(behavior.Moves.Count).IsEqualTo(1);
        await Assert.That(written.ContainsKey(bot.CharacterId), "phase persists through the B4 writer").IsTrue();
    }

    [Test]
    public async Task PresenceRoamModule_LiveRoute_NeverRears()
    {
        var behavior = new RecordingBehavior();
        var stepExecutor = new BotRoamStepExecutor();
        var module = new PresenceRoamActivityModule(
            new BotScheduleOptions { Enabled = false }, behavior, stepExecutor);
        var bot = NewBot("roam-bot");

        // A live route IS the current presence behavior — keep it untouched.
        stepExecutor.SetRoamRoute(bot.Character, BotPath.PathTo(new System.Numerics.Vector3(5, 0, 0)));
        var decision = module.CanActivate(ContextFor(bot));
        var activity = module.Activate(ContextFor(bot));

        await Assert.That(decision.CanActivate).IsTrue();
        await Assert.That(activity.Name).IsEqualTo("presence.roam");
        await Assert.That(behavior.Roams, "live patrol must not be reset mid-walk").IsEmpty();
    }

    [Test]
    public async Task IdleModule_ActivatesByClearingRoute()
    {
        var stepExecutor = new BotRoamStepExecutor();
        var module = new IdleActivityModule(stepExecutor);
        var bot = NewBot("idle-bot");

        stepExecutor.SetRoamRoute(bot.Character, BotPath.PathTo(new System.Numerics.Vector3(5, 0, 0)));
        var activity = module.Activate(ContextFor(bot));

        await Assert.That(activity.Name).IsEqualTo("idle");
        await Assert.That(stepExecutor.GetRoamRoute(bot.CharacterId), "idle clears the route").IsNull();
    }

    #endregion

    #region Executor decorator seam

    private sealed class PassthroughProbeExecutor : IBotStepExecutor
    {
        public int Calls { get; private set; }

        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(3));
        }
    }

    [Test]
    public async Task DecoratorExecutor_ArbitratesThenDelegates()
    {
        var module = new FakeModule("Decorated", 50, BotActivityDecision.Allow("decorated.act"));
        var arbiter = new BotGoalArbiter([module]);
        var inner = new PassthroughProbeExecutor();
        var executor = new BotGoalArbiterStepExecutor(arbiter, inner, gameHourProvider: () => 12f);
        var bot = NewBot("decorator-bot");

        var first = await executor.StepAsync(bot, CancellationToken.None);
        var second = await executor.StepAsync(bot, CancellationToken.None);

        await Assert.That(first).IsEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(inner.Calls, "inner step runs on every wake").IsEqualTo(2);
        await Assert.That(module.Activations, "arbitration activates once, steady state after").IsEqualTo(1);
    }

    [Test]
    public async Task DecoratorExecutor_ZeroModules_PurePassThrough()
    {
        var inner = new PassthroughProbeExecutor();
        var executor = new BotGoalArbiterStepExecutor(new BotGoalArbiter(), inner, gameHourProvider: () => 12f);
        var bot = NewBot("passthrough-bot");

        var delay = await executor.StepAsync(bot, CancellationToken.None);

        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(inner.Calls).IsEqualTo(1);
    }

    #endregion
}
