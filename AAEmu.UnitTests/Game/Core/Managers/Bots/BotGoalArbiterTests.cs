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
        public int Probes { get; private set; }
        public int Retirements { get; private set; }

        /// <summary>The context handed to the most recent activation.</summary>
        public BotActivityContext? LastContext { get; private set; }

        /// <summary>Whether this module releases its work when asked to retire.</summary>
        public bool Retire { get; init; } = true;

        public FakeModule(string name, int priority, BotActivityDecision decision)
        {
            Name = name;
            Priority = priority;
            Decision = decision;
        }

        public BotActivityDecision CanActivate(BotActivityContext context)
        {
            Probes++;
            return Decision;
        }

        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            LastContext = context;
            return new BotActivity(Decision.ActivityName!, Name);
        }

        public bool Deactivate(BotActivityContext context)
        {
            Retirements++;
            return Retire;
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
        await Assert.That(module.Retirements, "an owner handing over to itself is not a supersede").IsEqualTo(0);
    }

    [Test]
    public async Task Arbitrate_OwnerChanges_RetiresTheOutgoingModule()
    {
        var first = new FakeModule("First", 100, BotActivityDecision.Allow("first.act"));
        var second = new FakeModule("Second", 50, BotActivityDecision.Allow("second.act"));
        var arbiter = new BotGoalArbiter([first, second]);
        var bot = NewBot("owner-change-bot");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        first.Decision = BotActivityDecision.Deny("done");
        var changed = arbiter.Arbitrate(bot, 12f);

        await Assert.That(changed.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(changed.Activity!.ModuleName).IsEqualTo("Second");
        await Assert.That(first.Retirements, "the outgoing owner is asked to release").IsEqualTo(1);
        await Assert.That(second.Retirements, "the incoming owner keeps its own work").IsEqualTo(0);
        await Assert.That(second.LastContext!.Retirement!.Retired).IsTrue();
        await Assert.That(second.LastContext!.Retirement!.ModuleName).IsEqualTo("First");
    }

    [Test]
    public async Task Arbitrate_OutgoingRefusesToRetire_TheRefusalReachesTheIncomingModule()
    {
        var first = new FakeModule("First", 100, BotActivityDecision.Allow("first.act")) { Retire = false };
        var second = new FakeModule("Second", 50, BotActivityDecision.Allow("second.act"));
        var arbiter = new BotGoalArbiter([first, second]);
        var bot = NewBot("retire-refusal-bot");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        first.Decision = BotActivityDecision.Deny("done");
        var changed = arbiter.Arbitrate(bot, 12f);

        await Assert.That(changed.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(first.Retirements).IsEqualTo(1);
        var retirement = second.LastContext!.Retirement;
        await Assert.That(retirement!.Retired).IsFalse();
        await Assert.That(retirement.ActivityName).IsEqualTo("first.act");
        await Assert.That(retirement.Reason).Contains("refused");
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

    [Test]
    public async Task Arbitrate_WeightRanksInsideBand_HeavierBandMateWins()
    {
        var idle = new FakeModule("Idle", 50, BotActivityDecision.Allow("idle.act", BotActivityMotivation.Satisfied));
        var driven = new FakeModule("Driven", 50, BotActivityDecision.Allow("driven.act", BotActivityMotivation.Needs));
        var arbiter = new BotGoalArbiter();
        arbiter.Register(idle);
        arbiter.Register(driven);
        var bot = NewBot("weight-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.ModuleName, "the heavier band-mate outranks registration order")
            .IsEqualTo("Driven");
        await Assert.That(driven.Activations).IsEqualTo(1);
        await Assert.That(idle.Activations).IsEqualTo(0);
        await Assert.That(result.Candidates).IsEqualTo(2);
    }

    [Test]
    public async Task Arbitrate_WeightNeverPromotesAcrossBands_LowerBandsNeverAsked()
    {
        var high = new FakeModule("High", 100, BotActivityDecision.Allow("high.act", BotActivityMotivation.Satisfied));
        var low = new FakeModule("Low", 10, BotActivityDecision.Allow("low.act", int.MaxValue));
        var arbiter = new BotGoalArbiter([low, high]);
        var bot = NewBot("band-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Activity!.ModuleName, "priority stays the safety ordering").IsEqualTo("High");
        await Assert.That(low.Activations).IsEqualTo(0);
        await Assert.That(low.Probes, "a band below the winner is never even probed").IsEqualTo(0);
        await Assert.That(result.Candidates, "only the deciding band's allowances are candidates").IsEqualTo(1);
    }

    [Test]
    public async Task Arbitrate_IncumbentHeld_WithinBandAgainstMarginalChallenger()
    {
        var incumbent = new FakeModule("Incumbent", 50, BotActivityDecision.Allow("inc.act", BotActivityMotivation.Needs));
        var challenger = new FakeModule("Challenger", 50, BotActivityDecision.Deny("not yet"));
        // The challenger is registered FIRST on purpose: it is the allowance the
        // old first-allowance-wins scan would have taken, so this test fails
        // without the commitment branch instead of passing by registration order.
        var arbiter = new BotGoalArbiter([challenger, incumbent]);
        var bot = NewBot("commit-bot");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        // 110 < 100 + 25% margin → the incumbent keeps the activity.
        challenger.Decision = BotActivityDecision.Allow("chal.act", BotActivityMotivation.Needs + 10);
        var held = arbiter.Arbitrate(bot, 12f);

        await Assert.That(held.Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(held.Activity!.Name).IsEqualTo("inc.act");
        await Assert.That(challenger.Activations, "a marginal challenger never activates").IsEqualTo(0);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("inc.act");
    }

    [Test]
    public async Task Arbitrate_ChallengerAboveMargin_TakesActivity()
    {
        var incumbent = new FakeModule("Incumbent", 50, BotActivityDecision.Allow("inc.act", BotActivityMotivation.Needs));
        var challenger = new FakeModule("Challenger", 50, BotActivityDecision.Deny("not yet"));
        var arbiter = new BotGoalArbiter([incumbent, challenger]);
        var bot = NewBot("preempt-bot");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        // 125 >= 100 + 25 → the band-mate outbids the incumbent.
        challenger.Decision = BotActivityDecision.Allow("chal.act", BotActivityMotivation.Needs + 25);
        var taken = arbiter.Arbitrate(bot, 12f);

        await Assert.That(taken.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(taken.Activity!.ModuleName).IsEqualTo("Challenger");
        await Assert.That(challenger.Activations).IsEqualTo(1);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("chal.act");
    }

    [Test]
    public async Task Arbitrate_RefusalsSurfaced_WhyNotFromHighestPriorityDenial()
    {
        var denied = new FakeModule("Denied", 100, BotActivityDecision.Deny("gate off"));
        var allowed = new FakeModule("Allowed", 50, BotActivityDecision.Allow("fallback.act"));
        var arbiter = new BotGoalArbiter([denied, allowed]);
        var bot = NewBot("whynot-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.WhyNot, "the highest-priority refusal is reported").IsEqualTo("gate off");
        await Assert.That(result.Rejections).IsEqualTo(1);
        await Assert.That(result.Candidates).IsEqualTo(1);
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
    public async Task SchedulePhaseModule_ElectedWorkOwnsTheRoute_Declines()
    {
        var module = new SchedulePhaseActivityModule(
            new BotScheduleOptions { Enabled = true },
            new RecordingBehavior(),
            metadataProvider: id => PlayerBotMetadata.Empty(id));
        var bot = NewBot("sched-owned-bot");

        // Plan T8 (audit L1-g): the schedule layer must not overwrite a route an
        // elected work leg owns — ActiveActivity is the arbiter's authority.
        var owned = module.CanActivate(ContextFor(bot, active: "farm.work", hour: 23f));

        await Assert.That(owned.CanActivate).IsFalse();
        await Assert.That(owned.WhyNot).Contains("farm.work");

        // Idle and the schedule layer's own phases own no route: the phase applies.
        await Assert.That(module.CanActivate(ContextFor(bot, active: "idle", hour: 23f)).CanActivate).IsTrue();
        await Assert.That(module.CanActivate(ContextFor(bot, active: "schedule.work", hour: 23f)).CanActivate).IsTrue();
    }

    [Test]
    public async Task RouteOwner_ReportsLiveWork_AndNothingForIdleOrTheSchedule()
    {
        var bot = NewBot("route-owner-bot");
        var arbiter = new BotGoalArbiter([new FakeModule("Farm", 55, BotActivityDecision.Allow("farm.work"))]);

        await Assert.That(arbiter.GetRouteOwner(bot.CharacterId), "no election yet").IsNull();

        arbiter.Arbitrate(bot, gameHour: 12f);
        await Assert.That(arbiter.GetRouteOwner(bot.CharacterId)).IsEqualTo("farm.work");

        arbiter.Forget(bot.CharacterId);
        await Assert.That(arbiter.GetRouteOwner(bot.CharacterId)).IsNull();

        var idleArbiter = new BotGoalArbiter([new FakeModule("Idle", 0, BotActivityDecision.Allow("idle"))]);
        idleArbiter.Arbitrate(bot, gameHour: 12f);
        await Assert.That(idleArbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("idle");
        await Assert.That(idleArbiter.GetRouteOwner(bot.CharacterId), "idle clears the route instead of owning it")
            .IsNull();

        var scheduleArbiter = new BotGoalArbiter([new FakeModule("Schedules", 100, BotActivityDecision.Allow("schedule.work"))]);
        scheduleArbiter.Arbitrate(bot, gameHour: 12f);
        await Assert.That(scheduleArbiter.GetRouteOwner(bot.CharacterId), "the schedule's own phase is its own route")
            .IsNull();
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

    #region Dispatcher-or-deny (T9)

    [Test]
    public async Task Arbitrate_ActivityWithNoDispatchOwner_IsRefusedWithTheMissingOwner()
    {
        var module = new FakeModule("Fishing", 60, BotActivityDecision.Allow("fishing.contest"));
        var arbiter = new BotGoalArbiter([module]) { ActivityCarrier = BotActivityDispatchSurface.IsCarried };
        var bot = NewBot("ownerless-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(result.WhyNot).IsEqualTo("no dispatch owner for activity 'fishing.contest'");
        await Assert.That(module.Activations, "an ownerless allowance must never activate").IsEqualTo(0);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsNull();
        await Assert.That(arbiter.OwnerlessRefusalCount).IsEqualTo(1);
    }

    [Test]
    public async Task Arbitrate_OwnerlessRefusal_LetsTheLowerBandTakeOver()
    {
        var ownerless = new FakeModule("Fishing", 60, BotActivityDecision.Allow("fishing.contest"));
        var carried = new FakeModule("Presence", 50, BotActivityDecision.Allow(PresenceRoamActivityModule.ActivityName));
        var arbiter = new BotGoalArbiter([ownerless, carried]) { ActivityCarrier = BotActivityDispatchSurface.IsCarried };
        var bot = NewBot("ownerless-lower-band-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.ModuleName).IsEqualTo("Presence");
        await Assert.That(carried.Activations).IsEqualTo(1);
        await Assert.That(ownerless.Activations).IsEqualTo(0);
        await Assert.That(arbiter.OwnerlessRefusalCount).IsEqualTo(1);
        await Assert.That(result.WhyNot, "the operator read-back still shows the ownerless module")
            .IsEqualTo("no dispatch owner for activity 'fishing.contest'");
    }

    [Test]
    public async Task Arbitrate_WithoutTheCarrierCheck_KeepsThePreT9Behaviour()
    {
        var module = new FakeModule("Fishing", 60, BotActivityDecision.Allow("fishing.contest"));
        var arbiter = new BotGoalArbiter([module]);
        var bot = NewBot("no-carrier-check-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.Name).IsEqualTo("fishing.contest");
        await Assert.That(arbiter.OwnerlessRefusalCount).IsEqualTo(0);
    }

    [Test]
    public async Task Arbitrate_CarriedActivity_IsNotRefused()
    {
        var module = new FakeModule("Quest", 58, BotActivityDecision.Allow("quest.progress"));
        var arbiter = new BotGoalArbiter([module]) { ActivityCarrier = BotActivityDispatchSurface.IsCarried };
        var bot = NewBot("carried-bot");

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.Name).IsEqualTo("quest.progress");
        await Assert.That(arbiter.OwnerlessRefusalCount).IsEqualTo(0);
    }

    [Test]
    public async Task Arbitrate_RealFishingModule_IsRefusedByTheRealSurface()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var module = new FishingContestActivityModule(
            new FishingContestModuleOptions { Enabled = true, OpensAt = now.AddHours(-1), ClosesAt = now.AddHours(1) },
            utcNow: () => now);
        var presence = new FakeModule("Presence", 50, BotActivityDecision.Allow(PresenceRoamActivityModule.ActivityName));
        var arbiter = new BotGoalArbiter([module, presence]) { ActivityCarrier = BotActivityDispatchSurface.IsCarried };
        var bot = NewBot("real-fishing-bot");

        // The real module really does allow its activity while the window is open...
        await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        // ...and the real surface refuses it, so the lower band runs instead.
        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.ModuleName).IsEqualTo("Presence");
        await Assert.That(arbiter.OwnerlessRefusalCount).IsEqualTo(1);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo(PresenceRoamActivityModule.ActivityName);
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
