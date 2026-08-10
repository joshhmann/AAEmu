using System.Numerics;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Quests.Playerbot;

/// <summary>
/// M6-light sideload rigs: roam (BotPath), safety (BotSafetyMonitor) and
/// behaviors (BotBehaviorStack + PlayerBotBehaviorController).
///
/// The roam/safety regions are pure math — no engine singletons. The
/// behavior region drives a real headless bot (ordinary Character, no
/// Connection) through the pilot rig so movement exercises the real
/// Transform path. Quest-drive is exercised through the QuestDriveStep
/// seam — the behavior layer sequences; it never touches quest internals.
/// </summary>
[NotInParallel]
public class PlayerbotM6LightTests
{
    #region Helpers

    private static async Task AssertNear(Vector3 expected, Vector3 actual, string label, float tolerance = 0.01f)
    {
        var distance = Vector3.Distance(expected, actual);
        await Assert.That(distance <= tolerance,
            $"{label}: expected {expected}, got {actual} (delta {distance:0.###})").IsTrue();
    }

    /// <summary>Fresh headless bot + behavior controller parked at home.</summary>
    private static PlayerBotBehaviorController NewController(string name, Vector3 home, byte level = 1)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, level, Race.Nuian);
        var bot = new PlayerBotController(session.Character);
        bot.Character.Transform.Local.SetPosition(home);
        return new PlayerBotBehaviorController(bot, home);
    }

    #endregion

    #region Roam — BotPath (waypoint/pathing primitives)

    [Test]
    public async Task Roam_StraightLine_BoundedSteps_ArrivesAndFinishes()
    {
        var path = new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 2f);

        var position = new Vector3(0, 0, 0);
        var previous = position;
        for (var i = 0; i < 5; i++)
        {
            position = path.Move(position);
            await Assert.That(Vector3.Distance(previous, position) <= 2.01f,
                $"tick {i}: step exceeded MaxStepPerTick").IsTrue();
            previous = position;
        }

        await AssertNear(new Vector3(10, 0, 0), position, "final position");
        await Assert.That(path.IsFinished, "route must finish after the last waypoint").IsTrue();
        await Assert.That(path.CurrentTarget, "target stays at the final waypoint").IsEqualTo(new Vector3(10, 0, 0));
    }

    [Test]
    public async Task Roam_StepClamp_NeverExceedsMaxStep()
    {
        var path = new BotPath([new Vector3(100, 0, 0)], maxStepPerTick: 5f);
        var position = path.Move(new Vector3(0, 0, 0));

        await AssertNear(new Vector3(5, 0, 0), position, "single bounded step");
        await Assert.That(path.IsFinished, "route not finished after one step").IsFalse();
    }

    [Test]
    public async Task Roam_ArrivalRadius_ArrivesEarly_AndSnapsToWaypoint()
    {
        var path = new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 3f, arrivalRadius: 1.5f);

        var position = path.Move(new Vector3(0, 0, 0)); // (3,0,0)
        position = path.Move(position);                  // (6,0,0)
        await AssertNear(new Vector3(6, 0, 0), position, "before arrival radius");
        await Assert.That(path.IsFinished, "must not finish before the radius").IsFalse();

        position = path.Move(position); // 1.0 <= 1.5 -> arrive, snap to waypoint
        await AssertNear(new Vector3(10, 0, 0), position, "arrival snaps to waypoint");
        await Assert.That(path.IsFinished, "finished inside arrival radius").IsTrue();
    }

    [Test]
    public async Task Roam_ZInterpolation_VerticalAndDiagonal()
    {
        // Pure vertical leg (flat distance zero -> vertical branch).
        var vertical = new BotPath([new Vector3(0, 0, 10)], maxStepPerTick: 3f);
        var position = new Vector3(0, 0, 0);
        for (var i = 0; i < 4; i++)
            position = vertical.Move(position);
        await AssertNear(new Vector3(0, 0, 10), position, "vertical leg");
        await Assert.That(vertical.IsFinished, "vertical leg finished").IsTrue();

        // Diagonal leg: Z interpolates with the horizontal fraction.
        var diagonal = new BotPath([new Vector3(10, 0, 10)], maxStepPerTick: 5f);
        var first = diagonal.Move(new Vector3(0, 0, 0));
        await AssertNear(new Vector3(5, 0, 5), first, "diagonal midpoint (Z interpolated)");
        var second = diagonal.Move(first);
        await AssertNear(new Vector3(10, 0, 10), second, "diagonal end");
        await Assert.That(diagonal.IsFinished, "diagonal leg finished").IsTrue();
    }

    [Test]
    public async Task Roam_Loop_WrapsToStartForever()
    {
        var path = new BotPath([new Vector3(0, 0, 0), new Vector3(10, 0, 0)],
            BotPath.LoopMode.Loop, maxStepPerTick: 10f);

        var position = new Vector3(0, 0, 0);
        position = path.Move(position); // already at start -> wrap, target becomes (10,0,0)
        await Assert.That(path.CurrentTarget, "loop wraps to the second waypoint").IsEqualTo(new Vector3(10, 0, 0));

        position = path.Move(position); // (10,0,0) -> wrap, target back to start
        await AssertNear(new Vector3(10, 0, 0), position, "loop leg");
        await Assert.That(path.CurrentTarget, "loop wraps back to start").IsEqualTo(new Vector3(0, 0, 0));

        position = path.Move(position);
        await AssertNear(new Vector3(0, 0, 0), position, "loop full circle");
        await Assert.That(path.IsFinished, "loop mode never finishes").IsFalse();
    }

    [Test]
    public async Task Roam_PingPong_ReversesAtBothEnds()
    {
        var path = new BotPath([new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(20, 0, 0)],
            BotPath.LoopMode.PingPong, maxStepPerTick: 10f);

        var position = new Vector3(0, 0, 0);
        var targets = new List<Vector3>();
        for (var i = 0; i < 5; i++)
        {
            position = path.Move(position);
            targets.Add(path.CurrentTarget);
        }

        // Outbound: 10 -> 20, then reversed: 10 -> 0, then forward again: 10.
        await AssertNear(new Vector3(10, 0, 0), targets[0], "outbound 1");
        await AssertNear(new Vector3(20, 0, 0), targets[1], "outbound end");
        await AssertNear(new Vector3(10, 0, 0), targets[2], "return 1");
        await AssertNear(new Vector3(0, 0, 0), targets[3], "return end");
        await AssertNear(new Vector3(10, 0, 0), targets[4], "outbound again");
        await Assert.That(path.IsFinished, "ping-pong never finishes").IsFalse();
    }

    [Test]
    public async Task Roam_AllWaypointsWithin_BoundsGuard()
    {
        var home = new Vector3(0, 0, 0);
        var inside = new BotPath([new Vector3(5, 0, 0), new Vector3(-5, 0, 0)]);
        await Assert.That(inside.AllWaypointsWithin(home, 10f), "route inside the safe zone").IsTrue();

        var outside = new BotPath([new Vector3(5, 0, 0), new Vector3(15, 0, 0)]);
        await Assert.That(outside.AllWaypointsWithin(home, 10f),
            "route with a waypoint outside the safe zone must be rejected").IsFalse();
    }

    [Test]
    public async Task Roam_PathTo_SingleLeg_TargetsAndSteps()
    {
        var path = BotPath.PathTo(new Vector3(5, 5, 0), maxStepPerTick: 5f);
        await Assert.That(path.CurrentTarget, "single-leg target").IsEqualTo(new Vector3(5, 5, 0));

        var position = path.Move(new Vector3(0, 0, 0));
        await Assert.That(Math.Abs(Vector3.Distance(new Vector3(0, 0, 0), position) - 5f) <= 0.01f,
            "single leg travels exactly MaxStepPerTick").IsTrue();
        await AssertNear(new Vector3(3.5355f, 3.5355f, 0), position, "45-degree step");
    }

    #endregion

    #region Safety — BotSafetyMonitor (stop/abort + world-state guards)

    [Test]
    public async Task Safety_Stuck_AfterThresholdConsecutiveTicks()
    {
        var monitor = new BotSafetyMonitor(new Vector3(0, 0, 0));

        for (var i = 0; i < 5; i++)
            monitor.ObserveWorkTick(new Vector3(0, 0, 0), new Vector3(10, 0, 0), 100);
        await Assert.That(monitor.StopReason, "below threshold -> no stop").IsEqualTo(BotStopReason.None);

        monitor.ObserveWorkTick(new Vector3(0, 0, 0), new Vector3(10, 0, 0), 100);
        await Assert.That(monitor.StopReason, "threshold reached -> Stuck").IsEqualTo(BotStopReason.Stuck);
    }

    [Test]
    public async Task Safety_NavigationTimeout_AfterLegBudget()
    {
        var monitor = new BotSafetyMonitor(new Vector3(0, 0, 0)) { NavigationTimeoutTicks = 3 };

        // Bot advances every tick (not stuck) on the same leg.
        monitor.ObserveWorkTick(new Vector3(1, 0, 0), new Vector3(10, 0, 0), 100);
        monitor.ObserveWorkTick(new Vector3(2, 0, 0), new Vector3(10, 0, 0), 100);
        monitor.ObserveWorkTick(new Vector3(3, 0, 0), new Vector3(10, 0, 0), 100);
        await Assert.That(monitor.StopReason, "inside leg budget -> no stop").IsEqualTo(BotStopReason.None);

        monitor.ObserveWorkTick(new Vector3(4, 0, 0), new Vector3(10, 0, 0), 100);
        await Assert.That(monitor.StopReason, "leg budget exceeded -> NavigationTimeout")
            .IsEqualTo(BotStopReason.NavigationTimeout);
    }

    [Test]
    public async Task Safety_OutOfBounds_OutsideSafeZone()
    {
        var inside = new BotSafetyMonitor(new Vector3(0, 0, 0)) { SafeRadius = 10f };
        inside.ObserveWorkTick(new Vector3(9.9f, 0, 0), null, 100);
        await Assert.That(inside.StopReason, "inside safe zone -> no stop").IsEqualTo(BotStopReason.None);

        var outside = new BotSafetyMonitor(new Vector3(0, 0, 0)) { SafeRadius = 10f };
        outside.ObserveWorkTick(new Vector3(11, 0, 0), null, 100);
        await Assert.That(outside.StopReason, "outside safe zone -> OutOfBounds").IsEqualTo(BotStopReason.OutOfBounds);
    }

    [Test]
    public async Task Safety_InventoryFull_AtFreeSlotThreshold()
    {
        var roomy = new BotSafetyMonitor(new Vector3(0, 0, 0)) { InventoryFreeSlotsThreshold = 0 };
        roomy.ObserveWorkTick(new Vector3(0, 0, 0), null, 1);
        await Assert.That(roomy.StopReason, "slots above threshold -> no stop").IsEqualTo(BotStopReason.None);

        var full = new BotSafetyMonitor(new Vector3(0, 0, 0)) { InventoryFreeSlotsThreshold = 0 };
        full.ObserveWorkTick(new Vector3(0, 0, 0), null, 0);
        await Assert.That(full.StopReason, "slots at threshold -> InventoryFull").IsEqualTo(BotStopReason.InventoryFull);
    }

    [Test]
    public async Task Safety_TickBudget_Exceeded()
    {
        var monitor = new BotSafetyMonitor(new Vector3(0, 0, 0)) { TickBudget = 5 };

        for (var i = 0; i < 5; i++)
            monitor.ObserveWorkTick(new Vector3(0, 0, 0), null, 100);
        await Assert.That(monitor.StopReason, "inside tick budget -> no stop").IsEqualTo(BotStopReason.None);

        monitor.ObserveWorkTick(new Vector3(0, 0, 0), null, 100);
        await Assert.That(monitor.StopReason, "tick budget exceeded -> TickBudgetExceeded")
            .IsEqualTo(BotStopReason.TickBudgetExceeded);
    }

    [Test]
    public async Task Safety_CombatGate_DefaultOff_Grantable_BlockedWhenStopped()
    {
        var monitor = new BotSafetyMonitor(new Vector3(0, 0, 0));
        await Assert.That(monitor.CanEngageCombat, "combat gate off by default (no combat until quest-drive needs it)").IsFalse();

        monitor.GrantCombat();
        await Assert.That(monitor.CanEngageCombat, "granted -> combat legal").IsTrue();

        monitor.RequestStop(BotStopReason.ManualStop);
        await Assert.That(monitor.CanEngageCombat, "stop revokes combat legality").IsFalse();

        monitor.Reset();
        await Assert.That(monitor.CanEngageCombat, "reset restores granted combat").IsTrue();
        monitor.RevokeCombat();
        await Assert.That(monitor.CanEngageCombat, "revoke closes the gate").IsFalse();
    }

    [Test]
    public async Task Safety_FirstReasonWins_ResetClears()
    {
        var monitor = new BotSafetyMonitor(new Vector3(0, 0, 0)) { SafeRadius = 10f };
        monitor.RequestStop(BotStopReason.ManualStop);

        monitor.ObserveWorkTick(new Vector3(11, 0, 0), null, 100); // would be OutOfBounds
        await Assert.That(monitor.StopReason, "first reason wins (ManualStop latched)").IsEqualTo(BotStopReason.ManualStop);

        monitor.Reset();
        await Assert.That(monitor.StopReason, "reset clears the latch").IsEqualTo(BotStopReason.None);
        monitor.ObserveWorkTick(new Vector3(0, 0, 0), null, 100);
        await Assert.That(monitor.StopReason, "clean tick after reset stays clean").IsEqualTo(BotStopReason.None);
    }

    #endregion

    #region Behaviors — stack + controller (quest-drive primary)

    [Test]
    public async Task Behavior_Default_IdleNoMovement()
    {
        var controller = NewController("idle-bot", new Vector3(0, 0, 0));

        await Assert.That(controller.CurrentState, "default state is Idle").IsEqualTo(BotBehaviorState.Idle);

        controller.Tick();
        controller.Tick();
        controller.Tick();

        await Assert.That(controller.CurrentState, "stays Idle with no work").IsEqualTo(BotBehaviorState.Idle);
        await AssertNear(new Vector3(0, 0, 0), controller.Position, "idle bot never moves");
        await Assert.That(controller.IsStopped, "idle bot not stopped").IsFalse();
    }

    [Test]
    public async Task Behavior_Roam_WalksRoute_ThenIdles()
    {
        var controller = NewController("roam-bot", new Vector3(0, 0, 0));
        var started = controller.TryStartRoam(new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 2f));

        await Assert.That(started, "route inside safe zone -> accepted").IsTrue();
        await Assert.That(controller.CurrentState, "roam after start").IsEqualTo(BotBehaviorState.Roam);

        for (var i = 0; i < 5; i++)
            controller.Tick();

        await AssertNear(new Vector3(10, 0, 0), controller.Position, "route walked to the end");
        await Assert.That(controller.CurrentState, "route done -> Idle").IsEqualTo(BotBehaviorState.Idle);
    }

    [Test]
    public async Task Behavior_Roam_RejectsOutOfBoundsRoute()
    {
        var controller = NewController("bounded-bot", new Vector3(0, 0, 0));
        var rejected = controller.TryStartRoam(new BotPath([new Vector3(500, 0, 0)]));

        await Assert.That(rejected, "waypoint outside safe zone -> rejected").IsFalse();
        await Assert.That(controller.CurrentState, "stays Idle after rejection").IsEqualTo(BotBehaviorState.Idle);

        controller.Tick();
        await AssertNear(new Vector3(0, 0, 0), controller.Position, "bot never moved");
    }

    [Test]
    public async Task Behavior_QuestDrive_PreemptsRoam_ThenResumesRoam()
    {
        var controller = NewController("preempt-bot", new Vector3(0, 0, 0));
        controller.TryStartRoam(new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 2f));

        controller.Tick();
        controller.Tick();
        await AssertNear(new Vector3(4, 0, 0), controller.Position, "roam progressed");

        // Quest work arrives -> quest-drive preempts roam.
        var steps = 0;
        controller.QuestDriveStep = () => { steps++; return false; };
        controller.SetQuestWork(true);

        controller.Tick();
        await Assert.That(steps, "one quest step per tick").IsEqualTo(1);
        await Assert.That(controller.CurrentState, "quest-drive preempted roam").IsEqualTo(BotBehaviorState.Roam);
        await AssertNear(new Vector3(4, 0, 0), controller.Position, "quest tick consumed the turn (no movement)");

        // Work done -> next tick roams again.
        controller.Tick();
        await AssertNear(new Vector3(6, 0, 0), controller.Position, "roam resumed after quest work");
        await Assert.That(steps, "no extra quest steps").IsEqualTo(1);
    }

    [Test]
    public async Task Behavior_QuestDrive_Primary_BlocksRoamWhilePending()
    {
        var controller = NewController("primary-bot", new Vector3(0, 0, 0));
        controller.TryStartRoam(new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 2f));
        controller.Tick();
        controller.Tick();
        await AssertNear(new Vector3(4, 0, 0), controller.Position, "roam progressed");

        var steps = 0;
        controller.QuestDriveStep = () => { steps++; return true; }; // work never ends
        controller.SetQuestWork(true);

        for (var i = 0; i < 3; i++)
            controller.Tick();

        await Assert.That(controller.CurrentState, "quest-drive holds while work pending").IsEqualTo(BotBehaviorState.QuestDrive);
        await AssertNear(new Vector3(4, 0, 0), controller.Position, "roam fully blocked while quest work pending");
        await Assert.That(steps, "one quest step per tick").IsEqualTo(3);
    }

    [Test]
    public async Task Behavior_StuckStop_SafeReturnsHome_ThenIdlesWithReason()
    {
        var controller = NewController("stuck-bot", new Vector3(0, 0, 0));
        controller.TryStartRoam(new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 2f));
        for (var i = 0; i < 4; i++)
            controller.Tick();
        await AssertNear(new Vector3(8, 0, 0), controller.Position, "roamed away from home");

        // External abort latches a stop (stuck detection class).
        controller.Stop(BotStopReason.Stuck);
        controller.Tick(); // stop handling: safe-return starts
        await Assert.That(controller.CurrentState, "safe return after stop").IsEqualTo(BotBehaviorState.Return);

        for (var i = 0; i < 6; i++)
            controller.Tick(); // walk home (step 2: 8->6->4->2->0) + finalize

        await AssertNear(new Vector3(0, 0, 0), controller.Position, "returned home");
        await Assert.That(controller.CurrentState, "home again -> Idle").IsEqualTo(BotBehaviorState.Idle);
        await Assert.That(controller.StopReason, "stop reason preserved for evidence").IsEqualTo(BotStopReason.Stuck);
        await Assert.That(controller.IsStopped, "bot stays stopped (work paused)").IsTrue();
    }

    [Test]
    public async Task Behavior_ManualStop_IdlesInPlace()
    {
        var controller = NewController("manual-bot", new Vector3(0, 0, 0));
        controller.TryStartRoam(new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 2f));
        for (var i = 0; i < 4; i++)
            controller.Tick();
        await AssertNear(new Vector3(8, 0, 0), controller.Position, "roamed away from home");

        controller.Stop(BotStopReason.ManualStop);
        controller.Tick();
        controller.Tick();

        await Assert.That(controller.CurrentState, "manual stop idles in place").IsEqualTo(BotBehaviorState.Idle);
        await AssertNear(new Vector3(8, 0, 0), controller.Position, "manual stop does not move the bot");
        await Assert.That(controller.StopReason, "manual reason recorded").IsEqualTo(BotStopReason.ManualStop);
    }

    [Test]
    public async Task Behavior_CombatGrant_OnlyDuringQuestDrive()
    {
        var controller = NewController("combat-bot", new Vector3(0, 0, 0));

        await Assert.That(controller.TryGrantCombat(), "idle bot cannot grant combat").IsFalse();

        var steps = 0;
        controller.QuestDriveStep = () => { steps++; return true; }; // work remains
        controller.SetQuestWork(true);
        controller.Tick(); // quest-drive active
        await Assert.That(controller.CurrentState, "quest-drive active").IsEqualTo(BotBehaviorState.QuestDrive);

        await Assert.That(controller.TryGrantCombat(), "quest-drive with work pending may grant combat").IsTrue();
        await Assert.That(controller.Safety.CanEngageCombat, "gate open").IsTrue();

        // Stop revokes combat legality.
        controller.Stop(BotStopReason.ManualStop);
        await Assert.That(controller.Safety.CanEngageCombat, "stopped bot cannot fight").IsFalse();
    }

    [Test]
    public async Task Behavior_Stopped_QuestWorkPaused()
    {
        var controller = NewController("paused-bot", new Vector3(0, 0, 0));
        controller.TryStartRoam(new BotPath([new Vector3(10, 0, 0)], maxStepPerTick: 2f));

        var steps = 0;
        controller.QuestDriveStep = () => { steps++; return true; };
        controller.SetQuestWork(true);

        // Stop latches BEFORE the quest work runs.
        controller.Stop(BotStopReason.Stuck);
        for (var i = 0; i < 8; i++)
            controller.Tick(); // return home + finalize

        await Assert.That(steps, "quest steps must not run while stopped").IsEqualTo(0);
        await AssertNear(new Vector3(0, 0, 0), controller.Position, "returned home");
        await Assert.That(controller.CurrentState, "idle after safe return").IsEqualTo(BotBehaviorState.Idle);

        // Resume -> quest work runs again (quest-drive primary restored).
        controller.Resume();
        controller.Tick();
        await Assert.That(steps, "quest work resumes after Resume()").IsEqualTo(1);
        await Assert.That(controller.CurrentState, "quest-drive primary after resume").IsEqualTo(BotBehaviorState.QuestDrive);
    }

    #endregion
}
