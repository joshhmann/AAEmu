using System.Numerics;

using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Quests.Playerbot;

/// <summary>
/// M6-light sideload rigs: roam (BotPath) waypoint/pathing primitives.
///
/// The roam region is pure math — no engine singletons. (The former safety +
/// behavior-stack regions covered the dead PlayerBotBehaviorController
/// stack — deleted with G3-B3 goal arbitration, which replaces ad-hoc
/// behavior sequencing with the IBotActivityModule seam.)
/// </summary>
[NotInParallel]
public class PlayerbotM6LightTests
{
    #region Roam — BotPath (waypoint/pathing primitives)

    private static async Task AssertNear(Vector3 expected, Vector3 actual, string label, float tolerance = 0.01f)
    {
        var distance = Vector3.Distance(expected, actual);
        await Assert.That(distance <= tolerance,
            $"{label}: expected {expected}, got {actual} (delta {distance:0.###})").IsTrue();
    }

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
}
