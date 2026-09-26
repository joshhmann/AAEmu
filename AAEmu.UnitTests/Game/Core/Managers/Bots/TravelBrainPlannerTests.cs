using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// TravelBrainPlanner: the LIVE ADAPTER against the real headless actor — the
/// moving target's resolve through the actor's own world, the ONE dispatch-time
/// world-key pre-flight, the distance measurement, and the road-network route.
///
/// The pure chain is covered by <c>TravelBrainTests</c>; what is pinned here is the
/// adapter's own honesty contract: an unresolvable unit target reads UNRESOLVED
/// (never a zero destination), an unreadable world key reads UNKNOWN, and a
/// position destination beyond the local threshold walks the road network's
/// junctions.
/// </summary>
[NotInParallel]
public class TravelBrainPlannerTests
{
    private const uint Boar3475 = 3475;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AAEmu.Game.Models.AppConfiguration.Instance.World ??= new AAEmu.Game.Models.Game.WorldConfig();
        AAEmu.UnitTests.Game.Quests.Playerbot.PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        TravelIntentStore.ClearAll();
        ExecutionBoundary.ResetForTest();
    }

    [Test]
    public async Task PositionIntent_LocalMode_WalksTheDestination()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("travel-local");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var destination = here + new Vector3(20f, 0f, 0f); // inside the local threshold

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Position, 0, destination, false);
        var prepared = TravelBrainPlanner.Prepare(actor, Request(actor, TravelTargetKind.Position, 0, destination));

        await Assert.That(prepared.Decision.Arm).IsEqualTo(TravelArm.Local);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(prepared.Decision.Destination).IsEqualTo(destination);
        await Assert.That(prepared.Decision.DistanceM).IsCloseTo(20f, 0.01f);
        await Assert.That(prepared.Inputs.ActorObjId).IsEqualTo(actor.ActorId);
    }

    [Test]
    public async Task UnitIntent_ResolvesThroughTheActorsOwnWorld()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("travel-unit");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(Boar3475);
        var boar = session.World.GetNpc(boarObjId)!;
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(40f, 0f, 0f));

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Unit, boarObjId, Vector3.Zero, true);
        var prepared = TravelBrainPlanner.Prepare(actor, Request(actor, TravelTargetKind.Unit, boarObjId, Vector3.Zero));

        await Assert.That(prepared.Decision.Arm).IsEqualTo(TravelArm.Follow);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(TravelVerb.MoveToUnit);
        await Assert.That(prepared.Decision.TargetObjId).IsEqualTo(boarObjId);
        // The destination is the LIVE resolve, not the caller's placeholder.
        await Assert.That(prepared.Decision.Destination!.Value.X).IsCloseTo(here.X + 40f, 0.01f);
        await Assert.That(prepared.Decision.DistanceM).IsCloseTo(40f, 0.01f);
        // A unit resolved in the actor's own world is never a cross-world mismatch.
        await Assert.That(prepared.Inputs.WorldMismatch).IsFalse();
    }

    [Test]
    public async Task UnitIntent_UnresolvableTarget_NeverReadsAGoneVerdictOnTheFirstMiss()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("travel-unit-gone");
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));
        const uint missingObjId = 999_999;

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Unit, missingObjId, Vector3.Zero, true);
        var first = TravelBrainPlanner.Prepare(actor, Request(actor, TravelTargetKind.Unit, missingObjId, Vector3.Zero));

        await Assert.That(first.Decision.Arm).IsEqualTo(TravelArm.TargetProbe);
        await Assert.That(first.Decision.Reason).IsEqualTo(TravelReason.TargetUnresolved);
        await Assert.That(first.Decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(first.Decision.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(first.Decision.Destination).IsNull();

        // The adapter banks the miss, so the SECOND wake is the terminal one.
        TravelBrainPlanner.Publish(actor.ActorId, first);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var afterFirst)).IsTrue();
        await Assert.That(afterFirst.ResolveAttempts).IsEqualTo(1);

        var second = TravelBrainPlanner.Prepare(actor, Request(actor, TravelTargetKind.Unit, missingObjId, Vector3.Zero));
        await Assert.That(second.Decision.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        TravelBrainPlanner.Publish(actor.ActorId, second);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var settled)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.TargetGone);
    }

    [Test]
    public async Task PositionIntent_UnreadableDestinationWorld_IsUnknownNotAMismatch()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("travel-world-unknown");
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));
        var destination = new Vector3(140f, 100f, 10f);

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Position, 0, destination, false);
        var prepared = TravelBrainPlanner.Prepare(actor, Request(
            actor, TravelTargetKind.Position, 0, destination, destWorldKnown: false));

        await Assert.That(prepared.Inputs.WorldMismatch).IsFalse();
        await Assert.That(prepared.Decision.Arm).IsNotEqualTo(TravelArm.PreFlight);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(TravelVerb.MoveTo);
    }

    [Test]
    public async Task PositionIntent_AnotherWorldInstance_AbandonsWrongWorld()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("travel-world-mismatch");
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));
        var destination = new Vector3(140f, 100f, 10f);

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Position, 0, destination, false);
        var prepared = TravelBrainPlanner.Prepare(actor, Request(
            actor, TravelTargetKind.Position, 0, destination,
            destWorldKnown: true, destWorldId: 99, destInstanceId: 99));

        await Assert.That(prepared.Inputs.WorldMismatch).IsTrue();
        await Assert.That(prepared.Decision.Arm).IsEqualTo(TravelArm.PreFlight);
        await Assert.That(prepared.Decision.Terminal).IsEqualTo(TravelTerminal.WrongWorld);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(prepared.IsTerminal).IsTrue();

        TravelBrainPlanner.Publish(actor.ActorId, prepared);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var settled)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.WrongWorld);
    }

    [Test]
    public async Task PositionIntent_FarDestinationWithTheRealRoadNetwork_WalksTheFirstJunction()
    {
        // The process's own road atlas (136 junctions / 603 edges) is a real data
        // read: a far Arcum Iris hop routes through junctions, and the leg walks the
        // FIRST waypoint of that route rather than straight-lining the whole way.
        var roads = new RoadNetworkService();
        var (actor, _) = GameplayActorTestRig.CreateActor("travel-route");
        var start = new Vector3(21602.44f, 7325.04f, 236.39f); // Arcum Iris J1
        GameplayActorTestRig.SetPosition(actor, start);
        var destination = new Vector3(21602.44f, 7325.04f, 236.39f) + new Vector3(4000f, 1500f, 0f);

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Position, 0, destination, false);
        var prepared = TravelBrainPlanner.Prepare(
            actor, Request(actor, TravelTargetKind.Position, 0, destination, allowRoute: true), roads);

        await Assert.That(prepared.Decision.Mode).IsEqualTo(TravelMode.Route);
        await Assert.That(prepared.Decision.Arm).IsEqualTo(TravelArm.Route);
        await Assert.That(prepared.Decision.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(prepared.Route.Count).IsGreaterThan(0);
        await Assert.That(prepared.Decision.Destination!.Value).IsEqualTo(prepared.Route[0]);
        await Assert.That(prepared.Decision.WaypointCount).IsEqualTo(prepared.Route.Count);

        // The route is not thrown away: the next wake holds the live leg, and the
        // intent store carries the remaining waypoints.
        TravelBrainPlanner.Publish(actor.ActorId, prepared);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var armed)).IsTrue();
        await Assert.That(armed.Route.Count).IsEqualTo(prepared.Route.Count);
    }

    [Test]
    public async Task ARouteLegThatCompletes_AdvancesTheRouteInsteadOfRestarting()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("travel-route-advance");
        var first = new Vector3(200f, 200f, 10f);
        var second = new Vector3(300f, 200f, 10f);
        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Position, 0, new Vector3(900f, 900f, 10f), false);
        TravelIntentStore.SetRoute(actor.ActorId, [first, second]);

        GameplayActorTestRig.SetPosition(actor, first);
        await Assert.That(TravelBrainPlanner.PublishLegCompleted(actor)).IsTrue();

        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var armed)).IsTrue();
        await Assert.That(armed.Route.Count).IsEqualTo(1);
        await Assert.That(armed.Route[0]).IsEqualTo(second);
    }

    /// <summary>
    /// The RETREAT MOVE support the CombatBrain disengage consumes: the safe anchor
    /// is produced with no armed intent and no world read at all, so a disengaging
    /// combat leg can dispatch it through the ordinary Move verb immediately.
    /// </summary>
    [Test]
    public async Task RetreatDecision_NeedsNoArmedIntentAndProducesA25mAnchor()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("travel-retreat");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var threat = new Vector3(70f, 100f, 10f); // 30 m west

        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out _)).IsFalse();
        var retreat = TravelBrainPlanner.RetreatDecision(here, 4242, threat);

        await Assert.That(retreat.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(retreat.IsTerminal).IsFalse();
        await Assert.That(Vector3.Distance(here, retreat.Destination!.Value)).IsCloseTo(25f, 0.001f);
        await Assert.That(retreat.Destination!.Value.X).IsGreaterThan(here.X); // away from the threat
    }

    /// <summary>
    /// <see cref="TravelBrainPlanner.PublishDispatched"/> banks what the caller
    /// actually ISSUED: a repath spends an attempt and drops the route the failed
    /// leg was walking, and an ordinary issue records the mode/destination the next
    /// wake's drift comparison reads.
    /// </summary>
    [Test]
    public async Task PublishDispatched_BanksTheIssuedLeg_AndSpendsTheRepath()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("travel-published");
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));
        TravelIntentStore.ClearAll();
        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Position, 0, new Vector3(900f, 900f, 10f), false);

        // A verb-less decision banks nothing at all.
        var withheld = new TravelDecision(
            TravelArm.LegHold, TravelVerb.Hold, TravelMode.None, TravelTerminal.None,
            TravelReason.LegLive, 0, null, 10f, 0, 0);
        TravelBrainPlanner.PublishDispatched(actor, withheld);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var untouched)).IsTrue();
        await Assert.That(untouched.RepathCount).IsEqualTo(0);
        await Assert.That(untouched.Mode).IsEqualTo(TravelMode.None);

        // An ordinary issue records the mode/destination.
        var issued = new TravelDecision(
            TravelArm.Local, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
            TravelReason.LocalSelected, 0, new Vector3(200f, 200f, 10f), 300f, 0, 0);
        TravelBrainPlanner.PublishDispatched(actor, issued);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var noted)).IsTrue();
        await Assert.That(noted.Mode).IsEqualTo(TravelMode.Local);
        await Assert.That(noted.PriorDestination).IsEqualTo(new Vector3(200f, 200f, 10f));
        await Assert.That(noted.RepathCount).IsEqualTo(0);

        // A repath spends the attempt and drops the route the failed leg walked.
        TravelIntentStore.SetRoute(actor.ActorId, [new Vector3(150f, 100f, 10f)]);
        var repathed = issued with
        {
            Mode = TravelMode.Route,
            Reason = TravelReason.Repathed,
            Destination = new Vector3(250f, 250f, 10f),
            RepathCount = 1
        };
        TravelBrainPlanner.PublishDispatched(actor, repathed);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var spent)).IsTrue();
        await Assert.That(spent.RepathCount).IsEqualTo(1);
        await Assert.That(spent.Route.Count).IsEqualTo(0);
        await Assert.That(spent.Mode).IsEqualTo(TravelMode.Route);
    }

    private static TravelBrainPlanner.Request Request(
        IGameplayActor actor, TravelTargetKind kind, uint targetObjId, Vector3 destination,
        bool destWorldKnown = false, uint destWorldId = 0, uint destInstanceId = 0,
        bool allowRoute = false, TravelLegOutcome legOutcome = TravelLegOutcome.None, bool legLive = false)
        => new(
            Kind: kind,
            TargetObjId: targetObjId,
            Destination: destination,
            FollowRequested: kind == TravelTargetKind.Unit,
            DestWorldKnown: destWorldKnown,
            DestWorldId: destWorldId,
            DestInstanceId: destInstanceId,
            ArrivalRadiusM: TravelBrain.DefaultArrivalRadiusM,
            LocalModeMaxM: TravelBrain.DefaultLocalModeMaxM,
            RepathBudget: TravelBrain.DefaultRepathBudget,
            AllowRoute: allowRoute,
            LegOutcome: legOutcome,
            LegLive: legLive,
            LegTargetObjId: 0,
            LegDestinationKnown: false,
            LegDestination: Vector3.Zero,
            RetreatRequested: false,
            ThreatObjId: 0,
            ThreatPosition: Vector3.Zero);
}
