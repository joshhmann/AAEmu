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
/// The assigned-travel leg (plan T1): what the executor does with an operator
/// assignment. The walk itself is the ordinary route layer — this leg only owns
/// the assignment's route, judges it from the LEG's terminal state (never from
/// the bot merely standing somewhere) and reports the outcome back into the one
/// registry. The claims pinned here: the route is armed under its own owner tag
/// and a live foreign leg is preempted by owner with a recorded reason; arrival
/// completes the assignment; an exhausted walk budget fails it with the leg's
/// own reason instead of retrying silently; expiry and death end it with a
/// recorded reason; and the retirement seam releases the route when the band is
/// superseded.
/// </summary>
[NotInParallel]
public class BotRoamAssignedTravelTests
{
    [Test]
    public async Task Step_WithoutAssignment_ArmsNothing()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-idle");

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.Path).IsNull();
        await Assert.That(registry.TryGet(runtime.CharacterId, out _)).IsFalse();
    }

    [Test]
    public async Task Step_Assignment_ArmsTheRouteUnderItsOwnOwnerTag()
    {
        var (executor, actor, runtime, clock, _, _) = CreateRig("assigned-travel-arm");
        var destination = new Vector3(8, 0, 0);
        Assign(executor, runtime, destination);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.AssignedTravelArmed).IsTrue();
        await Assert.That(state.PendingMoveOwner).IsEqualTo(BotRoamStepExecutor.AssignedTravelMoveOwner);
        await Assert.That(state.Path).IsNotNull();
        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(actor.ActiveRequest!.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(actor.ActiveRequest.MoveOwner).IsEqualTo(BotRoamStepExecutor.AssignedTravelMoveOwner);
        await Assert.That(actor.ActiveRequest.Destination).IsEqualTo(destination);
    }

    [Test]
    public async Task Step_ArrivalAtTheDestination_CompletesTheAssignment()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-arrive");
        var destination = new Vector3(8, 0, 0);
        Assign(executor, runtime, destination);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        // Walk the bot onto the destination and halt it the way the route layer
        // does at the arrival radius.
        GameplayActorTestRig.SetPosition(actor, destination);
        actor.Stop();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(registry.TryGet(runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Completed);
        await Assert.That(recorded.LastReason).Contains("arrived");

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.Path).IsNull();
        await Assert.That(state.PendingMoveOwner).IsNull();
        await Assert.That(state.AssignedTravelArmed).IsFalse();
        await Assert.That(registry.TryGetLive(runtime.CharacterId, out _)).IsFalse();
    }

    [Test]
    public async Task Step_LiveAssignment_KeepsTheBotWakingUntilTheVerdictLands()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-cadence");

        // Control: an idle bot with nothing to move is parked (the idle-wake gate
        // is off by default) — the exact state an operator instruction has to
        // break, because a parked bot never wakes to elect the assignment band.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.That(await executor.StepAsync(runtime, CancellationToken.None)).IsNull();

        var destination = new Vector3(3, 0, 4);
        Assign(executor, runtime, destination);

        // Live assignment, nothing moving yet: the wake still has to happen.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.That(await executor.StepAsync(runtime, CancellationToken.None))
            .IsEqualTo((TimeSpan?)executor.ActiveCadence);

        // The route layer halts the leg at the arrival radius inside this wake, so
        // the verdict is only readable on the NEXT one — it must happen too.
        GameplayActorTestRig.SetPosition(actor, destination);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.That(await executor.StepAsync(runtime, CancellationToken.None))
            .IsEqualTo((TimeSpan?)executor.ActiveCadence);

        await Assert.That(registry.TryGet(runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Completed);

        // Verdict delivered: the bot is free to park again.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.That(await executor.StepAsync(runtime, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Step_WalkBudgetExhausted_FailsTheAssignmentWithTheLegReason()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig(
            "assigned-travel-timeout", roamLegTimeout: TimeSpan.FromMilliseconds(150));
        Assign(executor, runtime, new Vector3(10, 0, 0));

        // The bot never walks (its position is held at the origin), so the leg
        // runs out of budget and the assignment must end with that reason.
        for (var wake = 0; wake < 6 && registry.TryGetLive(runtime.CharacterId, out _); wake++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);
        }

        await Assert.That(registry.TryGet(runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Failed);
        await Assert.That(recorded.LastReason).Contains("timed out");
        await Assert.That(recorded.LastReason).Contains("Navigation");
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.Path).IsNull();
    }

    [Test]
    public async Task Step_ExpiredAssignment_ReleasesTheRouteWithTheReason()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-expiry");
        registry.Accept(runtime.CharacterId, BotAssignmentActivities.Travel, 6f, 0f, 0f, TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.AssignedTravelArmed).IsTrue();

        // The release preempts the leg, which clears the actor's active request —
        // hold the article first so its terminal verdict can still be read.
        var leg = actor.ActiveRequest!;
        clock.Advance(TimeSpan.FromSeconds(2));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(registry.TryGetLive(runtime.CharacterId, out _)).IsFalse();
        await Assert.That(registry.TryGet(runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Expired);
        await Assert.That(recorded.LastReason).Contains("expired after");

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.Path).IsNull();
        await Assert.That(state.AssignedTravelArmed).IsFalse();
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(leg.Detail).IsEqualTo("assignment no longer live");
    }

    [Test]
    public async Task Step_DeadBot_FailsTheAssignmentWithTheReason()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-death");
        Assign(executor, runtime, new Vector3(6, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.AssignedTravelArmed).IsTrue();

        runtime.Character.Hp = 0;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(registry.TryGet(runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Failed);
        await Assert.That(recorded.LastReason).Contains("died or was incapacitated");
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.Path).IsNull();
    }

    /// <summary>
    /// The orphan window the assignment path closes (gap L2-c): the bot was
    /// already walking someone else's leg when the operator arrived. That leg is
    /// preempted BY OWNER with a reason its audit row carries, instead of being
    /// abandoned to run to its own terminal while the assignment walks away.
    /// </summary>
    [Test]
    public async Task Step_LiveForeignLeg_IsPreemptedByOwnerWithAReason()
    {
        var (executor, actor, runtime, clock, _, _) = CreateRig("assigned-travel-preempt");
        var foreign = actor.MoveTo(new Vector3(99, 0, 0), 2f, TimeSpan.FromSeconds(30));
        await Assert.That(foreign.State).IsEqualTo(ActorLifecycleState.Running);

        Assign(executor, runtime, new Vector3(7, 0, 0));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(foreign.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(foreign.Detail).IsEqualTo("operator assignment supersedes OTHER:move-to");
        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo(BotRoamStepExecutor.AssignedTravelMoveOwner);
    }

    /// <summary>
    /// The retirement seam the module calls when the assignment band is
    /// superseded: the armed route and its live leg go with the band, while the
    /// assignment itself stays live so the operator's instruction is not
    /// silently dropped.
    /// </summary>
    [Test]
    public async Task ModuleDeactivate_ReleasesTheArmedRouteAndKeepsTheAssignmentLive()
    {
        var (executor, actor, runtime, clock, registry, module) = CreateRig("assigned-travel-retire");
        Assign(executor, runtime, new Vector3(7, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var leg = actor.ActiveRequest!;
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);

        var retired = module.Deactivate(new BotActivityContext
        {
            Bot = runtime,
            GameHour = 12f,
            ActiveActivity = BotAssignmentActivities.Travel,
        });

        await Assert.That(retired).IsTrue();
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(leg.Detail).IsEqualTo("the assignment band was superseded");
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.Path).IsNull();
        await Assert.That(state.AssignedTravelArmed).IsFalse();
        await Assert.That(registry.TryGetLive(runtime.CharacterId, out _)).IsTrue();
    }

    /// <summary>
    /// An operator replacing the instruction mid-walk: the bot must walk to the
    /// NEW destination rather than finish the superseded route and report a
    /// failure nobody asked for.
    /// </summary>
    [Test]
    public async Task Step_DestinationReplacedMidWalk_ReArmsToTheNewDestination()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-reassign");
        Assign(executor, runtime, new Vector3(8, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var firstLeg = actor.ActiveRequest!;
        await Assert.That(firstLeg.State).IsEqualTo(ActorLifecycleState.Running);

        var replacement = new Vector3(0, 9, 0);
        Assign(executor, runtime, replacement);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(firstLeg.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(firstLeg.Detail).IsEqualTo("assignment destination changed");
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.AssignedTravelArmed).IsTrue();
        await Assert.That(state.Path!.CurrentTarget).IsEqualTo(replacement);
        await Assert.That(actor.ActiveRequest!.Destination).IsEqualTo(replacement);
        await Assert.That(registry.TryGetLive(runtime.CharacterId, out _)).IsTrue();
    }

    /// <summary>
    /// A destination further than ONE leg can honestly walk in this rig
    /// (RoamSpeed 2 m/s x RoamLegTimeout 60 s, halved by the chunk margin = 60 m).
    /// Armed as a single leg it ended TimedOut on its first leg and the operator's
    /// walk never happened (code review 2026-10-16, P1-1): the route is chunked,
    /// an intermediate chunk is never a verdict, and the last chunk is the
    /// destination itself.
    /// </summary>
    [Test]
    public async Task Step_FarDestination_ArmsAChunkedRouteAndWalksEveryChunkWithoutAFalseVerdict()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-chunks");
        var destination = new Vector3(150, 0, 0);
        Assign(executor, runtime, destination);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.Path!.Waypoints.Count).IsEqualTo(3);
        await Assert.That(state.Path.Waypoints[^1]).IsEqualTo(destination);
        await Assert.That(actor.ActiveRequest!.Destination).IsEqualTo(state.Path.CurrentTarget);
        await Assert.That(actor.ActiveRequest.Destination).IsNotEqualTo(destination);

        // Walk chunk by chunk: the bot is placed on each waypoint and the wake's own
        // arrival halt ends that leg, exactly as the route layer does it live.
        for (var chunk = 1; chunk <= 2; chunk++)
        {
            GameplayActorTestRig.SetPosition(actor, state.Path.CurrentTarget);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);

            await Assert.That(registry.TryGetLive(runtime.CharacterId, out _)).IsTrue();
            await Assert.That(state.Path!.CurrentIndex).IsEqualTo(chunk);
            await Assert.That(actor.ActiveRequest!.Destination).IsEqualTo(state.Path.CurrentTarget);
            await Assert.That(actor.ActiveRequest.MoveOwner).IsEqualTo(BotRoamStepExecutor.AssignedTravelMoveOwner);
        }

        GameplayActorTestRig.SetPosition(actor, destination);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        // The route layer's own arrival halt ends the leg inside the wake, so the
        // verdict is readable on the NEXT one (same as the single-leg arrival).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(registry.TryGet(runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Completed);
        await Assert.That(recorded.LastReason).Contains("arrived");
        await Assert.That(state.Path).IsNull();
    }

    /// <summary>
    /// The preempting owner the review found (P1-2): a party follow, a survival
    /// flee retrack or a pursuit ends the assignment's leg mid-walk and leaves the
    /// article where it is. That is a suspension, not a verdict about the
    /// operator's instruction — the record stays live, the reason names the
    /// interruption, and the walk resumes on the same route.
    /// </summary>
    [Test]
    public async Task Step_ForeignStopOfTheAssignedLeg_SuspendsInsteadOfFailing()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-foreign-stop");
        var destination = new Vector3(40, 0, 0);
        Assign(executor, runtime, destination);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.ActiveRequest!.State).IsEqualTo(ActorLifecycleState.Running);

        // The bot is stopped 28 m short of the destination — well outside the
        // arrival radius, so the same halt detail the route layer uses for arrival
        // cannot mean arrival here.
        GameplayActorTestRig.SetPosition(actor, new Vector3(12, 0, 0));
        actor.Stop();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(registry.TryGetLive(runtime.CharacterId, out var live)).IsTrue();
        await Assert.That(live!.State).IsEqualTo(BotAssignmentState.Active);
        await Assert.That(live.LastReason).Contains("interrupted (stop requested)");

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.AssignedTravelArmed).IsTrue();
        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(actor.ActiveRequest!.Destination).IsEqualTo(destination);
        await Assert.That(actor.ActiveRequest.MoveOwner).IsEqualTo(BotRoamStepExecutor.AssignedTravelMoveOwner);
    }

    /// <summary>
    /// A foreign halt SHORT of an intermediate chunk must not skip the waypoint the
    /// bot never walked: the cursor stays and the chunk is re-issued.
    /// </summary>
    [Test]
    public async Task Step_ForeignStopShortOfAChunk_KeepsTheWaypointAndResumesIt()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-chunk-stop");
        Assign(executor, runtime, new Vector3(150, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var state = executor.GetBotState(runtime.CharacterId)!;
        var firstWaypoint = state.Path!.CurrentTarget;

        GameplayActorTestRig.SetPosition(actor, new Vector3(30, 0, 0));
        actor.Stop();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(registry.TryGetLive(runtime.CharacterId, out _)).IsTrue();
        await Assert.That(state.Path!.CurrentIndex).IsEqualTo(0);
        await Assert.That(state.Path.CurrentTarget).IsEqualTo(firstWaypoint);
        await Assert.That(actor.ActiveRequest!.Destination).IsEqualTo(firstWaypoint);
    }

    /// <summary>
    /// The operator's read-back must not keep reporting "Accepted — nothing has run
    /// yet" for the whole walk (code review 2026-10-16, P3-3): arming the route is
    /// what makes the assignment active, not only the arbiter's election.
    /// </summary>
    [Test]
    public async Task Step_ArmedRoute_MarksTheAssignmentActiveForTheOperator()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-mark-active");
        Assign(executor, runtime, new Vector3(8, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(registry.TryGetLive(runtime.CharacterId, out var live)).IsTrue();
        await Assert.That(live!.State).IsEqualTo(BotAssignmentState.Active);
        await Assert.That(live.LastReason).Contains("armed");
    }

    /// <summary>
    /// Retiring native work retires the assignment's execution ownership too: a
    /// state reused afterwards must not claim an armed route nobody issued (code
    /// review 2026-10-16, note 1). The instruction itself stays live.
    /// </summary>
    [Test]
    public async Task RetireNativeWork_ReleasesTheAssignmentOwnershipAndKeepsTheInstructionLive()
    {
        var (executor, actor, runtime, clock, registry, _) = CreateRig("assigned-travel-retire-native");
        Assign(executor, runtime, new Vector3(8, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.AssignedTravelArmed).IsTrue();

        // The real retirement entry: the scheduler calls it on its execution
        // boundary, holding the runtime's Sync, for a deactivating runtime.
        bool retired;
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        try
        {
            lock (runtime.Sync)
            {
                runtime.State = PlayerBotState.Deactivating;
                retired = executor.TryRetireNativeWork(runtime, runtime.Generation, "rig-retire");
            }
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }

        await Assert.That(retired).IsTrue();
        await Assert.That(state.AssignedTravelArmed).IsFalse();
        await Assert.That(state.AssignedLeg).IsNull();
        await Assert.That(state.Path).IsNull();
        await Assert.That(registry.TryGetLive(runtime.CharacterId, out _)).IsTrue();
    }

    private static void Assign(BotRoamStepExecutor executor, PlayerBotRuntime runtime, Vector3 destination)
    {
        var registry = executor.Assignments!;
        var accepted = registry.Accept(
            runtime.CharacterId, BotAssignmentActivities.Travel,
            destination.X, destination.Y, destination.Z, TimeSpan.FromSeconds(60), "rig");
        if (accepted == null)
            throw new InvalidOperationException("the rig assignment was refused");
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock,
        BotAssignmentRegistry Registry, AssignedTravelActivityModule Module) CreateRig(
        string name = "assigned-travel", TimeSpan? roamLegTimeout = null)
    {
        // No config JSON is loaded in unit tests: AppConfiguration.Instance.World
        // is null by default and the ground clamp dereferences it.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();
        var registry = new BotAssignmentRegistry { UtcNow = () => clock.GetUtcNow().UtcDateTime };

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            RoamLegTimeout = roamLegTimeout ?? TimeSpan.FromSeconds(60),
            Assignments = registry,
        };

        var module = new AssignedTravelActivityModule(registry, executor);
        return (executor, actor, runtime, clock, registry, module);
    }
}
