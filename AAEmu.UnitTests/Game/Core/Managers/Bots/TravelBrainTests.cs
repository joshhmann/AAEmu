using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// TravelBrain: the decision chain, the NAVIGATE RESULT VOCABULARY (the three
/// distinct abandonment terminals), the one-allowed re-resolve of a moving target,
/// the repath/abandon rule, the mode select, and the shared retreat anchor.
///
/// These tests drive the PURE decision surface (<see cref="TravelBrain.Decide"/>,
/// <see cref="TravelBrain.SafeAnchor"/>, <see cref="TravelBrain.SelectMode"/>,
/// <see cref="TravelIntentStore"/>) from synthetic
/// <see cref="TravelBrainInputs"/> — no world, no actor, no engine. The live
/// adapter's own reads (the target resolve, the world pre-flight, the road route)
/// are exercised by <c>TravelBrainPlannerTests</c> against the real headless actor;
/// what is pinned here is the contract a consumer observes: which arm fires, which
/// verb it asks for, which terminal it names, and the intent state it leaves.
/// </summary>
[NotInParallel]
public class TravelBrainTests
{
    private const uint BotObjId = 4242;
    private const uint TargetObjId = 91_001;
    private const uint ThreatObjId = 91_777;
    private static readonly Vector3 Self = new(100f, 100f, 10f);
    private static readonly Vector3 FarDestination = new(400f, 100f, 10f);

    [Before(Test)]
    public void Reset() => TravelIntentStore.ClearAll();

    /// <summary>
    /// A wake that reaches the local mode select: an armed position intent 300 m
    /// out, a readable distance, no live leg, no route. Every row below starts here
    /// and flips exactly ONE verdict.
    /// </summary>
    private static TravelBrainInputs LocalReady() => new(
        ActorObjId: BotObjId,
        SelfPosition: Self,
        TargetKind: TravelTargetKind.Position,
        TargetObjId: 0,
        Destination: FarDestination,
        DistanceM: 300f,
        ArrivalRadiusM: TravelBrain.DefaultArrivalRadiusM,
        LocalModeMaxM: TravelBrain.DefaultLocalModeMaxM,
        FollowRequested: false,
        DestWorldKnown: true,
        ActorWorldId: 1,
        ActorInstanceId: 1,
        DestWorldId: 1,
        DestInstanceId: 1,
        TargetResolved: true,
        PriorResolveAttempts: 0,
        LegOutcome: TravelLegOutcome.None,
        LegLive: false,
        LegTargetObjId: 0,
        LegDestinationKnown: false,
        LegDestination: Vector3.Zero,
        PriorRepathCount: 0,
        RepathBudget: TravelBrain.DefaultRepathBudget,
        RouteAvailable: false,
        RouteWaypoint: Vector3.Zero,
        RouteWaypointCount: 0,
        PriorMode: TravelMode.None,
        RetreatRequested: false,
        ThreatObjId: 0,
        ThreatPosition: Vector3.Zero);

    // ------------------------------------------------------------ terminals

    [Test]
    public async Task NoIntent_HoldsWithoutClaimingAFailure()
    {
        var decision = TravelBrain.Decide(LocalReady() with { TargetKind = TravelTargetKind.None });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.NoIntent);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.IsTerminal).IsFalse();
        await Assert.That(decision.IsAbandon).IsFalse();
    }

    [Test]
    public async Task Arrival_StopsAtTheDestinationAndNamesArrived()
    {
        var decision = TravelBrain.Decide(LocalReady() with { DistanceM = 2.0f });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Arrival);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Stop);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.Arrived);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.Arrived);
        await Assert.That(decision.IsTerminal).IsTrue();
        await Assert.That(decision.IsAbandon).IsFalse();
        await Assert.That(TravelBrain.Token(decision.Terminal)).IsEqualTo("arrived");
    }

    [Test]
    public async Task WrongWorld_IsADistinctTerminal_NotABareFailure()
    {
        var decision = TravelBrain.Decide(LocalReady() with
        {
            DestWorldKnown = true,
            DestInstanceId = 7 // another world instance
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.PreFlight);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.WrongWorld);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.WrongWorld);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(decision.IsAbandon).IsTrue();
        await Assert.That(TravelBrain.Token(decision.Terminal)).IsEqualTo("wrong-world");
    }

    [Test]
    public async Task DifferentWorldTemplateId_IsAlsoWrongWorld()
    {
        var decision = TravelBrain.Decide(LocalReady() with { DestWorldId = 2, ActorWorldId = 1 });

        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.WrongWorld);
    }

    [Test]
    public async Task UnknownDestinationWorldKey_NeverReadsAsWrongWorldOrAsSameWorld()
    {
        // Unknown stays unknown: no verdict is claimed in either direction, so the
        // wake proceeds to the ordinary chain rather than fabricating a mismatch.
        var decision = TravelBrain.Decide(LocalReady() with
        {
            DestWorldKnown = false,
            DestWorldId = 0,
            DestInstanceId = 0
        });

        await Assert.That(decision.Arm).IsNotEqualTo(TravelArm.PreFlight);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveTo);
    }

    [Test]
    public async Task TargetGone_OnlyAfterTheOneAllowedResolve()
    {
        // First miss: the probe withholds and spends the allowance.
        var firstMiss = LocalReady() with
        {
            TargetKind = TravelTargetKind.Unit,
            TargetObjId = TargetObjId,
            TargetResolved = false,
            PriorResolveAttempts = 0
        };
        var probing = TravelBrain.Decide(firstMiss);

        await Assert.That(probing.Arm).IsEqualTo(TravelArm.TargetProbe);
        await Assert.That(probing.Reason).IsEqualTo(TravelReason.TargetUnresolved);
        await Assert.That(probing.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(probing.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(probing.IsAbandon).IsFalse();

        // Second consecutive miss: the terminal, and it is the GONE terminal.
        var gone = TravelBrain.Decide(firstMiss with { PriorResolveAttempts = 1 });

        await Assert.That(gone.Arm).IsEqualTo(TravelArm.TargetProbe);
        await Assert.That(gone.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        await Assert.That(gone.Reason).IsEqualTo(TravelReason.TargetGone);
        await Assert.That(gone.IsAbandon).IsTrue();
        await Assert.That(TravelBrain.Token(gone.Terminal)).IsEqualTo("target-gone");
    }

    [Test]
    public async Task RepathBudgetSpent_AbandonsUnreachableWithThatName()
    {
        var failed = LocalReady() with
        {
            LegOutcome = TravelLegOutcome.TimedOut,
            PriorRepathCount = TravelBrain.DefaultRepathBudget
        };
        var decision = TravelBrain.Decide(failed);

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Repath);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.Unreachable);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.RepathExhausted);
        await Assert.That(decision.IsAbandon).IsTrue();
        await Assert.That(TravelBrain.Token(decision.Terminal)).IsEqualTo("unreachable");
    }

    [Test]
    public async Task TheThreeAbandonTerminals_AreDistinctFacts()
    {
        await Assert.That(TravelBrain.Token(TravelTerminal.WrongWorld))
            .IsNotEqualTo(TravelBrain.Token(TravelTerminal.TargetGone));
        await Assert.That(TravelBrain.Token(TravelTerminal.TargetGone))
            .IsNotEqualTo(TravelBrain.Token(TravelTerminal.Unreachable));
        await Assert.That(TravelBrain.Token(TravelTerminal.WrongWorld))
            .IsNotEqualTo(TravelBrain.Token(TravelTerminal.Unreachable));
        await Assert.That(TravelBrain.IsAbandon(TravelTerminal.Arrived)).IsFalse();
        await Assert.That(TravelBrain.IsAbandon(TravelTerminal.None)).IsFalse();
    }

    // ------------------------------------------------------------ re-resolve once

    [Test]
    public async Task AnUnresolvedMovingTarget_NeverReadsItsStalePositionAsArrival()
    {
        // The caller's snapshot distance reads as inside the arrival radius, but the
        // target did not resolve this wake — so the "arrival" is against a
        // last-known point the target may have walked away from. The probe owns the
        // wake instead.
        var decision = TravelBrain.Decide(LocalReady() with
        {
            TargetKind = TravelTargetKind.Unit,
            TargetObjId = TargetObjId,
            TargetResolved = false,
            DistanceM = 1.0f,
            PriorResolveAttempts = 0
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.TargetProbe);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.None);
    }

    [Test]
    public async Task MovingTarget_ReResolvesExactlyOnce_ThenAbandons()
    {
        TravelIntentStore.ClearAll();
        await Assert.That(TravelBrainPlanner.Arm(
            BotObjId, TravelTargetKind.Unit, TargetObjId, Vector3.Zero, followRequested: true)).IsTrue();

        // Miss #1: withhold, and the store banks the miss.
        var inputs = LocalReady() with
        {
            TargetKind = TravelTargetKind.Unit,
            TargetObjId = TargetObjId,
            TargetResolved = false,
            PriorResolveAttempts = 0
        };
        var first = new TravelBrainPlanner.Prepared(inputs, TravelBrain.Decide(inputs), []);
        TravelBrainPlanner.Publish(BotObjId, first);
        await Assert.That(first.IsTerminal).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var afterFirst)).IsTrue();
        await Assert.That(afterFirst.ResolveAttempts).IsEqualTo(1);
        await Assert.That(afterFirst.IsLive).IsTrue();

        // Miss #2 (the re-resolve also failed): terminal, banked stickily.
        var secondInputs = inputs with { PriorResolveAttempts = 1 };
        var second = new TravelBrainPlanner.Prepared(secondInputs, TravelBrain.Decide(secondInputs), []);
        TravelBrainPlanner.Publish(BotObjId, second);
        await Assert.That(second.Decision.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var settled)).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.TargetGone);
        await Assert.That(settled.IsSettled).IsTrue();

        // A later wake re-reads the SAME named verdict — never a bare failure.
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var reRead)).IsTrue();
        await Assert.That(reRead.Terminal).IsEqualTo(TravelTerminal.TargetGone);
    }

    [Test]
    public async Task ResolvedTarget_ClearsTheMissCounter()
    {
        TravelIntentStore.ClearAll();
        TravelBrainPlanner.Arm(BotObjId, TravelTargetKind.Unit, TargetObjId, Vector3.Zero, true);

        var miss = LocalReady() with
        {
            TargetKind = TravelTargetKind.Unit, TargetResolved = false, PriorResolveAttempts = 0
        };
        TravelBrainPlanner.Publish(BotObjId,
            new TravelBrainPlanner.Prepared(miss, TravelBrain.Decide(miss), []));
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var missed)).IsTrue();
        await Assert.That(missed.ResolveAttempts).IsEqualTo(1);

        // A live resolve clears it: the next absence is a FIRST miss again.
        var live = LocalReady() with { TargetKind = TravelTargetKind.Unit, TargetResolved = true };
        TravelBrainPlanner.Publish(BotObjId,
            new TravelBrainPlanner.Prepared(live, TravelBrain.Decide(live), []));
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var resolved)).IsTrue();
        await Assert.That(resolved.ResolveAttempts).IsEqualTo(0);
    }

    // ------------------------------------------------------------ repath

    [Test]
    public async Task AFailedLeg_RepathsWhileTheBudgetLasts_ThenAbandons()
    {
        // First failure: the mode select runs again, naming the repath and spending
        // one attempt.
        var firstRepath = TravelBrain.Decide(LocalReady() with
        {
            LegOutcome = TravelLegOutcome.TimedOut,
            PriorRepathCount = 0
        });

        await Assert.That(firstRepath.Arm).IsEqualTo(TravelArm.Local);
        await Assert.That(firstRepath.Reason).IsEqualTo(TravelReason.Repathed);
        await Assert.That(firstRepath.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(firstRepath.RepathCount).IsEqualTo(1);
        await Assert.That(firstRepath.Terminal).IsEqualTo(TravelTerminal.None);

        // Budget exhausted: the destination is abandoned with the named terminal.
        var exhausted = TravelBrain.Decide(LocalReady() with
        {
            LegOutcome = TravelLegOutcome.TimedOut,
            PriorRepathCount = TravelBrain.DefaultRepathBudget
        });
        await Assert.That(exhausted.Terminal).IsEqualTo(TravelTerminal.Unreachable);
    }

    [Test]
    public async Task AZeroBudget_AbandonsOnTheFirstFailure()
    {
        var decision = TravelBrain.Decide(LocalReady() with
        {
            LegOutcome = TravelLegOutcome.TimedOut,
            RepathBudget = 0
        });

        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.Unreachable);
    }

    [Test]
    public async Task AForeignInterruption_IsALegFailure_NotAnArrival()
    {
        var decision = TravelBrain.Decide(LocalReady() with
        {
            LegOutcome = TravelLegOutcome.Interrupted,
            PriorRepathCount = 0
        });

        await Assert.That(decision.Reason).IsEqualTo(TravelReason.Repathed);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveTo);
    }

    // ------------------------------------------------------------ leg hold + drift

    [Test]
    public async Task ALiveLegTrackingTheDestination_HoldsInsteadOfRestarting()
    {
        var decision = TravelBrain.Decide(LocalReady() with
        {
            LegLive = true,
            LegDestinationKnown = true,
            LegDestination = FarDestination,
            LegOutcome = TravelLegOutcome.Running
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.LegHold);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.LegLive);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.None);
    }

    [Test]
    public async Task ALiveLegWhoseDestinationDrifted_IsRetrackedNotHeld()
    {
        var decision = TravelBrain.Decide(LocalReady() with
        {
            LegLive = true,
            LegDestinationKnown = true,
            LegDestination = FarDestination + new Vector3(50f, 0f, 0f), // drifted past the tolerance
            LegOutcome = TravelLegOutcome.Running
        });

        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.DriftRetrack);
        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Local);
    }

    [Test]
    public async Task ALiveLegWithAnUnreadableDestination_IsRetrackedNotHeld()
    {
        // Fail-closed: an unreadable leg destination is not proof of tracking.
        var decision = TravelBrain.Decide(LocalReady() with
        {
            LegLive = true,
            LegDestinationKnown = false,
            LegOutcome = TravelLegOutcome.Running
        });

        await Assert.That(decision.Reason).IsEqualTo(TravelReason.DriftRetrack);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveTo);
    }

    [Test]
    public async Task ALiveRouteLegOnItsWaypoint_IsHeld_NotRestarted()
    {
        // A route leg walks the WAYPOINT, not the final destination: comparing the
        // live leg against the destination would read it as drifted every wake and
        // restart the route forever.
        var waypoint = new Vector3(220f, 130f, 12f);
        var decision = TravelBrain.Decide(LocalReady() with
        {
            LegLive = true,
            LegDestinationKnown = true,
            LegDestination = waypoint,
            RouteAvailable = true,
            RouteWaypoint = waypoint,
            RouteWaypointCount = 4,
            LegOutcome = TravelLegOutcome.Running
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.LegHold);
        await Assert.That(decision.HasVerb).IsFalse();
    }

    // ------------------------------------------------------------ mode select

    [Test]
    public async Task ModeSelect_LocalForNear_RouteForFarWithARoute_FollowForAUnit()
    {
        await Assert.That(TravelBrain.SelectMode(TravelTargetKind.Position, 30f, false, 60f))
            .IsEqualTo(TravelMode.Local);
        await Assert.That(TravelBrain.SelectMode(TravelTargetKind.Position, 300f, true, 60f))
            .IsEqualTo(TravelMode.Route);
        // Far but NO route resolved: local. A missing route is never unreachability.
        await Assert.That(TravelBrain.SelectMode(TravelTargetKind.Position, 300f, false, 60f))
            .IsEqualTo(TravelMode.Local);
        // Near with a route resolved is still local: routing a short hop is waste.
        await Assert.That(TravelBrain.SelectMode(TravelTargetKind.Position, 30f, true, 60f))
            .IsEqualTo(TravelMode.Local);
        await Assert.That(TravelBrain.SelectMode(TravelTargetKind.Unit, 5f, false, 60f))
            .IsEqualTo(TravelMode.Follow);
        // Unreadable distance never fabricates a route choice.
        await Assert.That(TravelBrain.SelectMode(TravelTargetKind.Position, float.NaN, true, 60f))
            .IsEqualTo(TravelMode.Local);
    }

    [Test]
    public async Task RouteMode_WalksTheRouteWaypoint_NotTheDestination()
    {
        var waypoint = new Vector3(220f, 130f, 12f);
        var decision = TravelBrain.Decide(LocalReady() with
        {
            RouteAvailable = true,
            RouteWaypoint = waypoint,
            RouteWaypointCount = 4
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Route);
        await Assert.That(decision.Mode).IsEqualTo(TravelMode.Route);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(decision.Destination).IsEqualTo(waypoint);
        await Assert.That(decision.WaypointCount).IsEqualTo(4);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.RouteSelected);
    }

    [Test]
    public async Task FollowMode_WalksTheUnit_NotAPositionSnapshot()
    {
        var targetPosition = new Vector3(140f, 100f, 10f);
        var decision = TravelBrain.Decide(LocalReady() with
        {
            TargetKind = TravelTargetKind.Unit,
            TargetObjId = TargetObjId,
            Destination = targetPosition,
            DistanceM = 40f,
            FollowRequested = true
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Follow);
        await Assert.That(decision.Mode).IsEqualTo(TravelMode.Follow);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveToUnit);
        await Assert.That(decision.TargetObjId).IsEqualTo(TargetObjId);
        await Assert.That(decision.Destination).IsEqualTo(targetPosition);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.FollowSelected);
    }

    [Test]
    public async Task FollowInsideItsStation_StopsButStaysLive()
    {
        // A follower that reached its station halts, but the intent is NOT finished
        // (the target can move again) — so no arrival is banked.
        var decision = TravelBrain.Decide(LocalReady() with
        {
            TargetKind = TravelTargetKind.Unit,
            TargetObjId = TargetObjId,
            DistanceM = 2.0f,
            FollowRequested = true
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Arrival);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Stop);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.FollowInPosition);
        await Assert.That(decision.IsTerminal).IsFalse();
    }

    // ------------------------------------------------------------ fail-closed

    [Test]
    public async Task UnreadableDistance_NeverReadsAsArrivalOrIssuesALeg()
    {
        var decision = TravelBrain.Decide(LocalReady() with { DistanceM = float.NaN });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Arrival);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.DistanceUnreadable);
        await Assert.That(decision.IsTerminal).IsFalse();
    }

    [Test]
    public async Task UnusableDestination_NeverIssuesALegToANaN()
    {
        var decision = TravelBrain.Decide(LocalReady() with
        {
            Destination = new Vector3(float.NaN, 0f, 0f),
            DistanceM = 300f
        });

        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.DistanceUnreadable);
        await Assert.That(decision.Destination).IsNull();
    }

    // ------------------------------------------------------------ retreat anchor

    [Test]
    public async Task SafeAnchor_IsTwentyFiveMetresDirectlyAwayFromTheThreat()
    {
        var threat = new Vector3(80f, 100f, 10f); // 20 m west of the actor
        var anchor = TravelBrain.SafeAnchor(Self, threat, TravelBrain.RetreatAnchorDistanceM);

        // Straight away from the threat: due east, 25 m out.
        await Assert.That(MathF.Abs(anchor.X - (Self.X + 25f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(anchor.Y - Self.Y)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(anchor.Z - Self.Z)).IsLessThan(0.001f);
        await Assert.That(Vector3.Distance(Self, anchor)).IsCloseTo(25f, 0.001f);
    }

    [Test]
    public async Task SafeAnchor_FollowsTheThreatsBearing()
    {
        var threat = new Vector3(100f, 60f, 10f); // 40 m south of the actor
        var anchor = TravelBrain.SafeAnchor(Self, threat, 25f);

        // Away from a southern threat is north.
        await Assert.That(anchor.Y).IsGreaterThan(Self.Y);
        await Assert.That(MathF.Abs(Vector3.Distance(Self, anchor) - 25f)).IsLessThan(0.001f);
    }

    [Test]
    public async Task SafeAnchor_DegradesSafelyOnUnreadableInput()
    {
        // Unset threat position: the escape direction is still defined (west→east).
        var noThreat = TravelBrain.SafeAnchor(Self, Vector3.Zero, 25f);
        await Assert.That(TravelBrainInputs.IsFinite(noThreat)).IsTrue();
        await Assert.That(noThreat.X).IsGreaterThan(Self.X);

        // Actor standing ON the threat: a degenerate (zero) direction never becomes
        // a NaN destination.
        var degenerate = TravelBrain.SafeAnchor(Self, Self, 25f);
        await Assert.That(TravelBrainInputs.IsFinite(degenerate)).IsTrue();
        await Assert.That(Vector3.Distance(Self, degenerate)).IsCloseTo(25f, 0.001f);

        // A non-finite or non-positive distance falls back to the 25 m step; it
        // never lands the actor on the threat.
        foreach (var bad in (float[])[float.NaN, 0f, -5f, float.PositiveInfinity])
        {
            var anchor = TravelBrain.SafeAnchor(Self, new Vector3(80f, 100f, 10f), bad);
            await Assert.That(Vector3.Distance(Self, anchor)).IsCloseTo(25f, 0.001f);
        }
    }

    [Test]
    public async Task RetreatArm_WalksTheSafeAnchor_BeforeTheDistanceRead()
    {
        // The retreat does not depend on a destination distance: it is derived from
        // live positions in the actor's own world, so an unmeasurable distance cannot
        // withhold it.
        var decision = TravelBrain.Decide(LocalReady() with
        {
            DistanceM = float.NaN,
            RetreatRequested = true,
            ThreatObjId = ThreatObjId,
            ThreatPosition = new Vector3(80f, 100f, 10f)
        });

        await Assert.That(decision.Arm).IsEqualTo(TravelArm.Retreat);
        await Assert.That(decision.Verb).IsEqualTo(TravelVerb.MoveTo);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.Retreat);
        await Assert.That(decision.TargetObjId).IsEqualTo(ThreatObjId);
        await Assert.That(decision.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(decision.Destination).IsNotNull();
        await Assert.That(Vector3.Distance(Self, decision.Destination!.Value)).IsCloseTo(25f, 0.001f);
        await Assert.That(decision.Destination!.Value.X).IsGreaterThan(Self.X);
    }

    /// <summary>
    /// The TWO retreat legs are the same SHAPE: <c>CombatBrain</c>'s disengage
    /// destination and this brain's retreat decision come from the same helper, so a
    /// combat flee and a travel retreat can never drift apart.
    /// </summary>
    [Test]
    public async Task RetreatAnchor_IsTheSameShapeAsTheCombatBrainsFleeLeg()
    {
        var threat = new Vector3(80f, 100f, 10f);

        var retreat = TravelBrainPlanner.RetreatDecision(Self, ThreatObjId, threat);
        var sharedAnchor = TravelBrain.SafeAnchor(Self, threat, TravelBrain.RetreatAnchorDistanceM);

        await Assert.That(retreat.Destination).IsEqualTo(sharedAnchor);
        await Assert.That(TravelBrain.RetreatAnchorDistanceM).IsCloseTo(25f, 0.001f);
    }

    // ------------------------------------------------------------ pure + store

    [Test]
    public async Task Decide_IdenticalInputs_ProduceIdenticalDecisions()
    {
        foreach (var inputs in new[]
                 {
                     LocalReady(),
                     LocalReady() with { TargetKind = TravelTargetKind.None },
                     LocalReady() with { DestInstanceId = 9 },
                     LocalReady() with { DistanceM = 1f },
                     LocalReady() with { DistanceM = float.NaN },
                     LocalReady() with { LegLive = true, LegDestinationKnown = true, LegDestination = FarDestination },
                     LocalReady() with { LegOutcome = TravelLegOutcome.TimedOut },
                     LocalReady() with { LegOutcome = TravelLegOutcome.TimedOut, PriorRepathCount = 2 },
                     LocalReady() with { RouteAvailable = true, RouteWaypointCount = 3 },
                     LocalReady() with { TargetKind = TravelTargetKind.Unit, TargetResolved = false },
                     LocalReady() with { RetreatRequested = true }
                 })
        {
            var first = TravelBrain.Decide(inputs);
            var second = TravelBrain.Decide(inputs);
            await Assert.That(second).IsEqualTo(first);
            await Assert.That(second.Describe()).IsEqualTo(first.Describe());
        }
    }

    [Test]
    public async Task IntentStore_ArmingTheSameDestination_KeepsTheCounters()
    {
        TravelIntentStore.ClearAll();

        await Assert.That(TravelBrainPlanner.Arm(
            BotObjId, TravelTargetKind.Position, 0, FarDestination, false)).IsTrue();
        await Assert.That(TravelIntentStore.NoteResolveMiss(BotObjId)).IsEqualTo(1);

        // Re-arming the same destination REFRESHES: the miss counter survives, so a
        // caller refreshing a moved target cannot hand itself a fresh budget.
        await Assert.That(TravelBrainPlanner.Arm(
            BotObjId, TravelTargetKind.Position, 0, FarDestination, false)).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var refreshed)).IsTrue();
        await Assert.That(refreshed.ResolveAttempts).IsEqualTo(1);

        // A DIFFERENT destination starts clean.
        await Assert.That(TravelBrainPlanner.Arm(
            BotObjId, TravelTargetKind.Position, 0, new Vector3(900f, 900f, 10f), false)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var fresh)).IsTrue();
        await Assert.That(fresh.ResolveAttempts).IsEqualTo(0);
        await Assert.That(fresh.Terminal).IsEqualTo(TravelTerminal.None);
    }

    [Test]
    public async Task IntentStore_ADifferentTargetIdentity_StartsClean()
    {
        TravelIntentStore.ClearAll();
        TravelBrainPlanner.Arm(BotObjId, TravelTargetKind.Unit, TargetObjId, FarDestination, true);
        TravelIntentStore.NoteResolveMiss(BotObjId);

        // Same coordinates, another unit: another journey, a fresh budget.
        await Assert.That(TravelBrainPlanner.Arm(
            BotObjId, TravelTargetKind.Unit, TargetObjId + 1, FarDestination, true)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var fresh)).IsTrue();
        await Assert.That(fresh.ResolveAttempts).IsEqualTo(0);
    }

    [Test]
    public async Task IntentStore_Disarm_DropsTheIntentEntirely()
    {
        TravelIntentStore.ClearAll();
        TravelBrainPlanner.Arm(BotObjId, TravelTargetKind.Position, 0, FarDestination, false);
        await Assert.That(TravelIntentStore.Count).IsEqualTo(1);

        await Assert.That(TravelIntentStore.Disarm(BotObjId)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out _)).IsFalse();
        await Assert.That(TravelIntentStore.Count).IsEqualTo(0);
        // Disarming an intent that is not armed is a no-op, not an error.
        await Assert.That(TravelIntentStore.Disarm(BotObjId)).IsFalse();
    }

    [Test]
    public async Task IntentStore_BankedTerminal_IsStickyUntilANewArm()
    {
        TravelIntentStore.ClearAll();
        TravelBrainPlanner.Arm(BotObjId, TravelTargetKind.Position, 0, FarDestination, false);
        TravelIntentStore.BankTerminal(BotObjId, TravelTerminal.Unreachable, TravelReason.RepathExhausted);

        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var settled)).IsTrue();
        await Assert.That(settled.IsSettled).IsTrue();
        await Assert.That(settled.Terminal).IsEqualTo(TravelTerminal.Unreachable);
        await Assert.That(settled.Reason).IsEqualTo(TravelReason.RepathExhausted);

        // A later bank of None is refused — an intent never loses its verdict.
        TravelIntentStore.BankTerminal(BotObjId, TravelTerminal.None, TravelReason.None);
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var stillSettled)).IsTrue();
        await Assert.That(stillSettled.Terminal).IsEqualTo(TravelTerminal.Unreachable);

        // A fresh arm clears it.
        TravelBrainPlanner.Arm(BotObjId, TravelTargetKind.Position, 0, new Vector3(900f, 900f, 10f), false);
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var rearmed)).IsTrue();
        await Assert.That(rearmed.Terminal).IsEqualTo(TravelTerminal.None);
    }

    [Test]
    public async Task IntentStore_RouteProgressesInsteadOfRestarting()
    {
        TravelIntentStore.ClearAll();
        TravelBrainPlanner.Arm(BotObjId, TravelTargetKind.Position, 0, FarDestination, false);
        TravelIntentStore.SetRoute(BotObjId,
            [new Vector3(150f, 100f, 10f), new Vector3(250f, 100f, 10f), new Vector3(350f, 100f, 10f)]);

        // Walking to the first junction consumes it; the head is now the SECOND.
        await Assert.That(TravelIntentStore.AdvanceRoute(
            BotObjId, new Vector3(150f, 100f, 10f), TravelBrain.DefaultArrivalRadiusM)).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var after)).IsTrue();
        await Assert.That(after.Route.Count).IsEqualTo(2);
        await Assert.That(after.Route[0].X).IsCloseTo(250f, 0.001f);

        // Standing short of the head consumes nothing.
        await Assert.That(TravelIntentStore.AdvanceRoute(
            BotObjId, new Vector3(200f, 100f, 10f), TravelBrain.DefaultArrivalRadiusM)).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var unchanged)).IsTrue();
        await Assert.That(unchanged.Route.Count).IsEqualTo(2);
    }

    [Test]
    public async Task IntentStore_RepathDropsTheStaleRoute()
    {
        TravelIntentStore.ClearAll();
        TravelBrainPlanner.Arm(BotObjId, TravelTargetKind.Position, 0, FarDestination, false);
        TravelIntentStore.SetRoute(BotObjId, [new Vector3(150f, 100f, 10f)]);
        TravelIntentStore.NoteRepath(BotObjId, 1, TravelMode.Route, new Vector3(150f, 100f, 10f));

        await Assert.That(TravelIntentStore.TryGet(BotObjId, out var repathed)).IsTrue();
        await Assert.That(repathed.Route.Count).IsEqualTo(0);
        await Assert.That(repathed.RepathCount).IsEqualTo(1);
    }

    // ------------------------------------------------------------ purity

    /// <summary>
    /// NO WORLD ACCESS: <see cref="TravelBrain"/> and <see cref="TravelIntentStore"/>
    /// are the pure decision surface, and their IL must not reference a single
    /// engine type — no world, no manager singleton, no Character/Npc/Item, and
    /// (per <c>PerceptionStackSurfaceTests</c>) nothing from the perception stack.
    /// Everything a wake needs arrives on <see cref="TravelBrainInputs"/>.
    ///
    /// <see cref="TravelBrainPlanner"/> is deliberately NOT covered — it is the live
    /// adapter and its live reads are its whole job (proved by the planner rigs
    /// against the real headless actor).
    /// </summary>
    [Test]
    public async Task Decide_ReferencesNoEngineType()
    {
        var offenders = new List<string>();
        var scanned = 0;
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in (Type[])[typeof(TravelBrain), typeof(TravelIntentStore)])
        {
            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetMethodBody() == null)
                    continue;
                scanned++;
                foreach (var referenced in ReferencedTypes(method))
                {
                    if (referenced == null)
                        continue;
                    if (IsEngineType(referenced))
                        offenders.Add($"{type.FullName}.{method.Name} → {referenced.FullName}");
                }
            }
        }

        await Assert.That(scanned).IsGreaterThan(4)
            .Because("the IL scan must actually walk the travel decision surface");
        await Assert.That(offenders).IsEmpty()
            .Because("the travel decision surface is pure: every live read belongs to TravelBrainPlanner — offenders: "
                     + string.Join("; ", offenders.Take(20)));
    }

    /// <summary>True when a type lives outside the pure decision surface (engine models, managers, or the perception/belief stack).</summary>
    private static bool IsEngineType(Type type)
    {
        var name = type.FullName ?? "";
        if (name.StartsWith("AAEmu.Game.Models.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Core.Managers.", StringComparison.Ordinal)
            && !name.StartsWith("AAEmu.Game.Core.Managers.Bots.Travel.Travel", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.GameData.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Utils.", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static IEnumerable<Type?> ReferencedTypes(MethodBase method)
        => IlScan.ReferencedTypes(method);
}
