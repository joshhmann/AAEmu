using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.World;

using Microsoft.Extensions.Time.Testing;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T4 return path, executor half (plan §7, audit L3-b). The step executor is the
/// only thing that sees a leg end, so it reports each leg's terminal outcome
/// exactly once from the one place every wake passes — and reports nothing at
/// all while no sink is wired. The last three tests are the real composition
/// the plan's §8 criterion asks for: a real executor, a real arbiter and a real
/// actor, where a leg really fails and the module that owned it answers
/// differently on the next election.
/// </summary>
[NotInParallel]
public class BotLegOutcomePublishTests
{
    #region Rig

    /// <summary>
    /// A module whose answer depends on its own last leg. <c>RememberFailure</c>
    /// decides whether the module keeps that memory (the plan's "does not
    /// re-activate identically forever") or only differs for the one election
    /// the outcome was delivered on (the arbiter's once-only delivery).
    /// </summary>
    private sealed class LegAwareModule : IBotActivityModule
    {
        public string Name { get; init; } = "LegTest";
        public int Priority { get; init; } = 50;
        public string ActivityName { get; init; } = "legtest.work";
        public bool RememberFailure { get; init; }

        /// <summary>A rival withdraws its offer by flipping this (used to move the bot on).</summary>
        public bool Allows { get; set; } = true;

        /// <summary>The leg outcome handed to each probe (null when there was none).</summary>
        public List<BotLegOutcome?> Seen { get; } = [];

        private bool _failed;

        public BotActivityDecision CanActivate(BotActivityContext context)
        {
            Seen.Add(context.LastLegOutcome);
            if (context.LastLegOutcome is { IsFailure: true } && RememberFailure)
                _failed = true;

            // A failure in hand always refuses this wake; the memory keeps
            // refusing it (that part is the module's own policy).
            if (context.LastLegOutcome is { IsFailure: true } || _failed)
                return BotActivityDecision.Deny("the last leg failed");

            return Allows ? BotActivityDecision.Allow(ActivityName) : BotActivityDecision.Deny("not now");
        }

        public BotActivity Activate(BotActivityContext context) => new(ActivityName, Name);
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock)
        CreateRig(string name, TimeSpan? roamLegTimeout = null, Func<uint, string?>? activeActivityProvider = null)
    {
        // No config JSON is loaded in unit tests: AppConfiguration.Instance.World
        // is null by default and the ground clamp dereferences it.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            RoamLegTimeout = roamLegTimeout ?? TimeSpan.FromSeconds(60),
            ActiveActivityProvider = activeActivityProvider,
        };

        return (executor, actor, runtime, clock);
    }

    #endregion

    #region Publish once per leg

    /// <summary>
    /// A route leg that exhausts its walk budget is reported once, with the
    /// leg's own identity and failure detail, and never again — the same leg
    /// stays visible to the executor across wakes, so the report must be keyed
    /// on the leg, not on the wake.
    /// </summary>
    [Test]
    public async Task Step_TimedOutRouteLeg_IsReportedOnce()
    {
        var (executor, actor, runtime, clock) = CreateRig("leg-publish-1", TimeSpan.FromMilliseconds(150));
        var outcomes = new List<BotLegOutcome>();
        executor.LegOutcomeSink = outcomes.Add;
        executor.SetRoamRoute(runtime.Character, new BotPath([new Vector3(10, 0, 0)], BotPath.LoopMode.Once));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var leg = actor.ActiveRequest!;
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(outcomes).IsEmpty();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.TimedOut);

        // The wake after the leg died is the one that reports it.
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(outcomes).Count().IsEqualTo(1);

        var outcome = outcomes[0];
        await Assert.That(outcome.CharacterId).IsEqualTo(runtime.CharacterId);
        await Assert.That(outcome.TraceId).IsEqualTo(leg.TraceId);
        await Assert.That(outcome.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(outcome.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(outcome.Kind).IsEqualTo(BotLegOutcomeKind.Failed);
        await Assert.That(outcome.MoveOwner).IsEqualTo(leg.MoveOwner);
        await Assert.That(outcome.Detail).IsEqualTo(leg.Detail);

        // Later wakes report nothing more: one leg, one outcome.
        await executor.StepAsync(runtime, CancellationToken.None);
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(outcomes).Count().IsEqualTo(1);
    }

    /// <summary>
    /// The zero-behaviour-change default made observable: with no sink wired
    /// nothing is reported at all, and a sink enrolled afterwards hears the leg
    /// that was in flight exactly once — never a second copy on later wakes.
    /// </summary>
    [Test]
    public async Task Step_WithoutASink_ReportsNothingUntilOneIsEnrolled()
    {
        var (executor, actor, runtime, clock) = CreateRig("leg-publish-2", TimeSpan.FromMilliseconds(150));
        executor.SetRoamRoute(runtime.Character, new BotPath([new Vector3(10, 0, 0)], BotPath.LoopMode.Once));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var leg = actor.ActiveRequest!;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.TimedOut);

        // Nobody was listening while the leg died, so the next wake — with the
        // sink now wired — is the first thing the sink hears.
        var outcomes = new List<BotLegOutcome>();
        executor.LegOutcomeSink = outcomes.Add;

        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(outcomes).Count().IsEqualTo(1);
        await Assert.That(outcomes[0].TraceId).IsEqualTo(leg.TraceId);
        await Assert.That(outcomes[0].State).IsEqualTo(ActorLifecycleState.TimedOut);

        // One leg, one outcome: the later wakes add nothing.
        await executor.StepAsync(runtime, CancellationToken.None);
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(outcomes).Count().IsEqualTo(1);
    }

    /// <summary>
    /// The case neither wake-top slot can see: a leg that is issued and dropped
    /// inside a single wake. The route layer does exactly that when a move is
    /// rejected and it advances past the waypoint ("already advanced here"), so
    /// the slot itself reports the leg it drops — otherwise a rejected leg's
    /// outcome would be lost entirely, and the activity that issued it would
    /// never learn why its leg failed.
    /// </summary>
    [Test]
    public async Task Step_LegIssuedAndDroppedInsideOneWake_IsReported()
    {
        var (executor, actor, runtime, clock) =
            CreateRig("leg-publish-3", activeActivityProvider: _ => "legtest.work");
        var outcomes = new List<BotLegOutcome>();
        executor.LegOutcomeSink = outcomes.Add;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var state = executor.GetBotState(runtime.CharacterId);
        await Assert.That(state).IsNotNull();

        // Exactly what the route layer does to a rejected leg: issue it, see it
        // terminal, and clear it again before the wake ends.
        var leg = actor.MoveTo(new Vector3(10, 0, 0), 2f);
        leg.Reject(ActorFailureReason.Navigation, "no path to the waypoint");
        state!.PendingLeg = leg;
        state.PendingLeg = null;

        await Assert.That(outcomes).Count().IsEqualTo(1);
        var outcome = outcomes[0];
        await Assert.That(outcome.CharacterId).IsEqualTo(runtime.CharacterId);
        await Assert.That(outcome.TraceId).IsEqualTo(leg.TraceId);
        await Assert.That(outcome.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(outcome.Kind).IsEqualTo(BotLegOutcomeKind.Failed);
        await Assert.That(outcome.Detail).IsEqualTo("Navigation: no path to the waypoint");
        await Assert.That(outcome.ActivityName).IsEqualTo("legtest.work");

        // The wake top does not report the same leg again.
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(outcomes).Count().IsEqualTo(1);
    }

    /// <summary>
    /// A leg that was already reported at a wake top is not offered again from
    /// the previous-leg slot. The slot keeps the last wake's leg for one more
    /// wake, so a wake that reports a different leg in its body (the route
    /// layer dropping a rejected leg while the previous one is still in the
    /// slot) must not make the older leg look unreported: one leg, one outcome,
    /// and no stale fact clobbering the newer one at the consumer.
    /// </summary>
    [Test]
    public async Task Step_LegReportedAtAWakeTop_IsNotReOfferedFromThePreviousSlot()
    {
        var (executor, actor, runtime, clock) =
            CreateRig("leg-publish-4", activeActivityProvider: _ => "legtest.work");
        var outcomes = new List<BotLegOutcome>();
        executor.LegOutcomeSink = outcomes.Add;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var state = executor.GetBotState(runtime.CharacterId);
        await Assert.That(state).IsNotNull();

        // A leg already dead in the slot: the next wake top reports it.
        var first = actor.MoveTo(new Vector3(10, 0, 0), 2f);
        first.Reject(ActorFailureReason.Navigation, "no path to the first waypoint");
        state!.PendingLeg = first;

        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(outcomes).Count().IsEqualTo(1);
        await Assert.That(outcomes[0].TraceId).IsEqualTo(first.TraceId);

        // The same wake's body drops that leg and ends a second one, which the
        // slot reports — so the last report no longer names the leg sitting in
        // the previous slot.
        var second = actor.MoveTo(new Vector3(20, 0, 0), 2f);
        second.Reject(ActorFailureReason.Navigation, "no path to the second waypoint");
        state.PendingLeg = second;
        state.PendingLeg = null;
        await Assert.That(outcomes).Count().IsEqualTo(2);

        // The next wake top still sees the first leg in the previous slot. It
        // was reported once already, so it must not be reported again.
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(outcomes).Count().IsEqualTo(2);
        await Assert.That(outcomes.Select(outcome => outcome.TraceId).Distinct().Count()).IsEqualTo(2);
        await Assert.That(outcomes.Select(outcome => outcome.TraceId)).Contains(first.TraceId);
        await Assert.That(outcomes.Select(outcome => outcome.TraceId)).Contains(second.TraceId);
    }

    #endregion

    #region Composition: the real executor, the real arbiter, a real failing leg

    /// <summary>
    /// The plan's §8 unit/rig criterion, end to end: a real executor and a real
    /// arbiter around a real actor. The elected activity issues a route leg, the
    /// leg really times out, and the module that owned it is handed its own
    /// failure on the next election — where it answers differently, so the
    /// failed activity is not re-elected identically.
    /// </summary>
    [Test]
    public async Task Composition_FailedLegReachesTheOwningModule_AndTheElectionDiffers()
    {
        var module = new LegAwareModule();
        var arbiter = new BotGoalArbiter([module]);
        var (executor, actor, runtime, clock) = CreateRig("leg-composition-1", TimeSpan.FromMilliseconds(150),
            activeActivityProvider: arbiter.GetActiveActivity);
        executor.LegOutcomeSink = arbiter.ReportLegOutcome;
        executor.SetRoamRoute(runtime.Character, new BotPath([new Vector3(10, 0, 0)], BotPath.LoopMode.Once));

        // Wake 1: the election arms the activity and the executor issues the leg.
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var leg = actor.ActiveRequest!;
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);

        // Wake 2: the leg runs out of budget.
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(module.Seen[^1]).IsNull();

        // Wake 3: the executor reports the leg at the top of the wake; the
        // election of this wake ran before it, so it is not delivered yet.
        await executor.StepAsync(runtime, CancellationToken.None);

        // Wake 4: the election hears about the failure and the module refuses.
        var afterFailure = arbiter.Arbitrate(runtime, 12f);
        var delivered = module.Seen[^1];
        await Assert.That(delivered).IsNotNull();
        await Assert.That(delivered!.TraceId).IsEqualTo(leg.TraceId);
        await Assert.That(delivered.IsFailure).IsTrue();
        await Assert.That(afterFailure.Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(afterFailure.WhyNot).IsEqualTo("the last leg failed");
        await Assert.That(arbiter.GetActiveActivity(runtime.CharacterId)).IsNull();

        // The delivery was the once-only kind: this module keeps no memory, so
        // the next election is back to its plain answer.
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(module.Seen[^1]).IsNull();
    }

    /// <summary>
    /// The plan's §8 integration criterion with a module that keeps the fact: a
    /// failed activity does not re-activate identically, wake after wake. The
    /// memory belongs to the module (the arbiter delivers a fact, not a policy);
    /// the return path is what makes that answer possible.
    /// </summary>
    [Test]
    public async Task Composition_ModuleThatRemembersItsFailedLeg_DoesNotReActivate()
    {
        var module = new LegAwareModule { RememberFailure = true };
        var arbiter = new BotGoalArbiter([module]);
        var (executor, actor, runtime, clock) = CreateRig("leg-composition-2", TimeSpan.FromMilliseconds(150),
            activeActivityProvider: arbiter.GetActiveActivity);
        executor.LegOutcomeSink = arbiter.ReportLegOutcome;
        executor.SetRoamRoute(runtime.Character, new BotPath([new Vector3(10, 0, 0)], BotPath.LoopMode.Once));

        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var leg = actor.ActiveRequest!;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(arbiter.GetActiveActivity(runtime.CharacterId)).IsNull();
    }

    /// <summary>
    /// A leg issued under an activity that a handover replaced is not handed to
    /// the module that took the bot over. The executor stamps the fact with the
    /// activity that issued the leg — read one wake late, i.e. before the current
    /// wake's handover — and the arbiter refuses a fact about work that is over.
    /// The rival here denies at first, so the owner really issues the leg.
    /// </summary>
    [Test]
    public async Task Composition_LegOfASupersededActivity_IsNotHandedToTheNewOwner()
    {
        var owner = new LegAwareModule();
        var rival = new LegAwareModule
        {
            Name = "Rival",
            Priority = 60,
            ActivityName = "rival.work",
            Allows = false,
        };
        var arbiter = new BotGoalArbiter([rival, owner]);
        string? currentActivity = null;
        var (executor, actor, runtime, clock) = CreateRig("leg-composition-3", TimeSpan.FromMilliseconds(150),
            activeActivityProvider: _ => currentActivity);
        executor.LegOutcomeSink = arbiter.ReportLegOutcome;
        executor.SetRoamRoute(runtime.Character, new BotPath([new Vector3(10, 0, 0)], BotPath.LoopMode.Once));

        // Wake 1: the owner's activity is elected and issues the leg.
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(runtime.CharacterId)).IsEqualTo("legtest.work");
        currentActivity = "legtest.work";
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var leg = actor.ActiveRequest!;
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);

        // Wake 2: the rival takes the bot over while the leg is still running.
        rival.Allows = true;
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(runtime.CharacterId)).IsEqualTo("rival.work");
        currentActivity = "rival.work";
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.TimedOut);

        // Wake 3: the executor reports the leg at the top of the wake, stamped
        // with the activity that issued it; the arbiter refuses it because that
        // activity is over, so the rival never hears about somebody else's leg.
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(arbiter.Arbitrate(runtime, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(rival.Seen[^1]).IsNull();
        await Assert.That(owner.Seen[^1]).IsNull();
    }

    #endregion
}
