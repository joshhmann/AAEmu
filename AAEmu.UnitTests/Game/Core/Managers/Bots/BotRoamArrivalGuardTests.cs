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
/// Route-cursor honesty (card player-ai-foundation-correctness, L1-j): a roam
/// route advances to its next waypoint only when the Move leg that carried the
/// bot there actually ARRIVED. A leg preempted by another owner left the
/// waypoint unvisited, so the cursor stays on it and the next step re-issues the
/// same target instead of silently skipping ground the bot never walked. A leg
/// that could not be walked at all — its walk budget expired — is the other
/// case: the cursor steps past that waypoint (with a warning), because
/// re-issuing an unreachable target every wake would pin the route forever
/// (code review 2026-10-16, F1).
/// </summary>
[NotInParallel]
public class BotRoamArrivalGuardTests
{
    /// <summary>
    /// The classification the route guard branches on. Only the two arrival
    /// endings count: a completed leg, or the route layer's own halt at the
    /// arrival radius (<c>Stop()</c> writes the detail "stop requested"). Every
    /// other terminal Move — a foreign preemption, a navigation timeout — is a
    /// leg that stopped short.
    /// </summary>
    [Test]
    public async Task IsArrivedMoveLeg_ClassifiesLegTermination()
    {
        static ActorRequest Leg(Action<ActorRequest> terminate)
        {
            var leg = new ActorRequest(ActorActionType.Move, 0, new Vector3(10, 0, 0), 0, TimeSpan.FromSeconds(30));
            leg.Accept("test");
            leg.Start("test");
            terminate(leg);
            return leg;
        }

        var arrived = Leg(leg => leg.Complete("arrived"));
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(arrived)).IsTrue();

        var haltedAtRadius = Leg(leg => leg.Interrupt("stop requested"));
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(haltedAtRadius)).IsTrue();

        var foreignPreemption = Leg(leg => leg.Interrupt("quest pursuit retrack"));
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(foreignPreemption)).IsFalse();

        var controllerPreemption = Leg(leg => leg.Interrupt("interrupted by controller"));
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(controllerPreemption)).IsFalse();

        var navigationTimeout = Leg(leg => leg.Expire(ActorFailureReason.Navigation, "move timed out"));
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(navigationTimeout)).IsFalse();

        var running = new ActorRequest(ActorActionType.Move, 0, new Vector3(10, 0, 0), 0, TimeSpan.FromSeconds(30));
        running.Accept("test");
        running.Start("test");
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(running)).IsFalse();
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(null)).IsFalse();

        // Only Move legs carry a route waypoint.
        var cast = new ActorRequest(ActorActionType.Cast, 0, null, 0, TimeSpan.FromSeconds(30));
        cast.Accept("test");
        cast.Start("test");
        cast.Complete("done");
        await Assert.That(BotRoamStepExecutor.IsArrivedMoveLeg(cast)).IsFalse();
    }

    /// <summary>
    /// Behavioural guard: a route leg preempted mid-walk (not an arrival) must
    /// not advance the cursor. Before the fix the guard tested only "terminal
    /// Move leg", so this exact wake stepped the cursor to waypoint 2 and
    /// re-issued a leg there. The reach matters: `BotPath.Move` arrives only
    /// inside the 0.5 m radius (`BotPath.cs:83-96`), so the pre-fix guard
    /// treated a bot standing inside that radius as arrived even when the leg
    /// itself was interrupted. This test therefore places the bot ON waypoint 1
    /// before preempting the leg — the state where the old reading advances.
    /// </summary>
    [Test]
    public async Task Step_NonArrivalTerminalLeg_DoesNotSkipWaypoint()
    {
        var (executor, actor, runtime, clock) = CreateRig("arrival-guard-1");
        var first = new Vector3(10, 0, 0);
        var second = new Vector3(20, 0, 0);
        executor.SetRoamRoute(runtime.Character, new BotPath([first, second], BotPath.LoopMode.Once));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var path = executor.GetRoamRoute(runtime.CharacterId)!;
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(path.CurrentIndex).IsEqualTo(0);

        // The leg dies for a reason that is NOT an arrival, while the bot
        // already stands on the waypoint it was walking to. Hold the request
        // itself: the actor may drop its own reference when the leg is
        // interrupted, but the route's PendingLeg still points at it.
        var leg = actor.ActiveRequest!;
        GameplayActorTestRig.SetPosition(actor, first);
        await Assert.That(actor.PreemptCurrent("preempted by another owner")).IsTrue();
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(leg.Detail).IsEqualTo("preempted by another owner");

        // Keep the actor busy with a live leg the route does not own, so the
        // "issue the next leg when idle" step stays out of the way and the
        // route still carries its terminal leg into the turnover check. That is
        // the state the defect needs: a leg that ended without arriving while
        // something else holds the actor.
        var holder = actor.MoveTo(new Vector3(99, 0, 0), 2f, TimeSpan.FromSeconds(30));
        await Assert.That(holder.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.PendingLeg).IsSameReferenceAs(leg);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        // Standing on the waypoint is not the same thing as having walked
        // there: the leg ended without arriving, so the cursor stays and the
        // next leg walks the same target instead of the route moving on.
        await Assert.That(path.CurrentIndex).IsEqualTo(0);
        await Assert.That(path.CurrentTarget).IsEqualTo(first);
    }

    /// <summary>
    /// The control case for the guard above: the same route DOES advance when
    /// the leg ends as an arrival, so the fix narrows the condition rather than
    /// freezing the cursor.
    /// </summary>
    [Test]
    public async Task Step_ArrivalAtWaypoint_StillAdvancesTheCursor()
    {
        var (executor, actor, runtime, clock) = CreateRig("arrival-guard-2");
        var first = new Vector3(10, 0, 0);
        var second = new Vector3(20, 0, 0);
        executor.SetRoamRoute(runtime.Character, new BotPath([first, second], BotPath.LoopMode.Once));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        // Walk the bot onto the first waypoint, then halt there the way the
        // route layer does at the arrival radius.
        GameplayActorTestRig.SetPosition(actor, first);
        actor.Stop();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var path = executor.GetRoamRoute(runtime.CharacterId)!;
        await Assert.That(path.CurrentIndex).IsEqualTo(1);
        await Assert.That(path.CurrentTarget).IsEqualTo(second);
    }

    /// <summary>
    /// The escape hatch for a waypoint the bot cannot walk (code review
    /// 2026-10-16, F1): when the route's own leg exhausts its walk budget, the
    /// cursor must step past that waypoint. Pinning it would make the next wake
    /// re-issue the same unreachable target forever, so the ring would never
    /// advance — the opposite failure of the skip the guard above prevents.
    /// </summary>
    [Test]
    public async Task Step_TimedOutRouteLeg_AdvancesPastTheUnreachableWaypoint()
    {
        var (executor, actor, runtime, clock) = CreateRig("arrival-guard-3", roamLegTimeout: TimeSpan.FromMilliseconds(150));
        var first = new Vector3(10, 0, 0);
        var second = new Vector3(20, 0, 0);
        executor.SetRoamRoute(runtime.Character, new BotPath([first, second], BotPath.LoopMode.Once));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var path = executor.GetRoamRoute(runtime.CharacterId)!;
        var leg = actor.ActiveRequest!;
        await Assert.That(leg.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(leg.Destination).IsEqualTo(first);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(path.CurrentIndex).IsEqualTo(0);

        // The walk budget runs out while the bot is still 10 m short of
        // waypoint 1 (it stands at the origin), so this leg ends without
        // arriving and the cursor must step past the unreachable waypoint.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(path.CurrentIndex).IsEqualTo(1);
        await Assert.That(path.CurrentTarget).IsEqualTo(second);
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateRig(
        string name = "arrival-guard", TimeSpan? roamLegTimeout = null)
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
        };

        return (executor, actor, runtime, clock);
    }
}
