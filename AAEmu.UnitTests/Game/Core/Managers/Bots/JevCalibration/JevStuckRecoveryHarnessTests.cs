#nullable enable

using System.Numerics;

using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// Experiment 1 (offline, test-side only): given a corpus of frozen
/// stuck/recovery wakes synthesized from the travel brain's own input shapes
/// (no live data, no model calls — the expected labels are test fixtures),
/// the comparison harness runs the current classifier logic vs the labels.
/// This is the proof that frozen-state disagreement mining CAN separate
/// classes the per-wake telemetry conflates.
/// </summary>
[NotInParallel]
public class JevStuckRecoveryHarnessTests
{
    private static readonly Vector3 Self = new(100f, 100f, 10f);

    [Test]
    public async Task Corpus_SeparatesThreeConflatedClasses()
    {
        // The corpus: every frozen decision is the travel brain's OWN verdict
        // over the frozen inputs (never hand-built), the label is the fixture.
        var corpus = new List<JevLabeledStuckWake>
        {
            // Stale-perception pursuit: two shapes of the same cause.
            Label("stale-probe", TravelUnitUnresolved(attempts: 0), silentWakes: 1,
                JevStuckRootCause.StalePerceptionPursuit),
            Label("stale-gone", TravelUnitUnresolved(attempts: TravelBrain.TargetResolveAllowance), silentWakes: 1,
                JevStuckRootCause.StalePerceptionPursuit),
            Label("stale-drift", TravelFollowDrifted(), silentWakes: 0,
                JevStuckRootCause.StalePerceptionPursuit),
            // Path-invalid: the recovery shape and the abandonment shape.
            Label("path-repathed", TravelReady() with
            {
                LegOutcome = TravelLegOutcome.TimedOut,
                PriorRepathCount = 0
            }, silentWakes: 0, JevStuckRootCause.PathInvalid),
            Label("path-unreachable", TravelReady() with
            {
                LegOutcome = TravelLegOutcome.TimedOut,
                PriorRepathCount = TravelBrain.DefaultRepathBudget
            }, silentWakes: 0, JevStuckRootCause.PathInvalid),
            // Scheduler-silence: an armed intent asking for nothing, over a streak.
            Label("silent-a", TravelSilentBlip(), silentWakes: 2, JevStuckRootCause.SchedulerSilence),
            Label("silent-b", TravelSilentBlip(), silentWakes: 3, JevStuckRootCause.SchedulerSilence),
        };

        var report = JevStuckRecoveryHarness.Run(corpus);

        // The offline proof: every row classified as labelled, every class covered.
        await Assert.That(report.Accuracy).IsEqualTo(1.0);
        await Assert.That(report.TotalFor(JevStuckRootCause.StalePerceptionPursuit)).IsEqualTo(3);
        await Assert.That(report.TotalFor(JevStuckRootCause.PathInvalid)).IsEqualTo(2);
        await Assert.That(report.TotalFor(JevStuckRootCause.SchedulerSilence)).IsEqualTo(2);
        await Assert.That(report.CorrectFor(JevStuckRootCause.StalePerceptionPursuit)).IsEqualTo(3);
        await Assert.That(report.CorrectFor(JevStuckRootCause.PathInvalid)).IsEqualTo(2);
        await Assert.That(report.CorrectFor(JevStuckRootCause.SchedulerSilence)).IsEqualTo(2);
    }

    [Test]
    public async Task TelemetryConflation_IdenticalHoldSurface_SeparatesByHistory()
    {
        // The conflation this experiment breaks: a first-miss unit probe and a
        // first silent blip are byte-identical at the verb level (both Hold,
        // both non-terminal) — the per-wake telemetry cannot tell them apart,
        // but the frozen history can.
        var probe = TravelBrain.Decide(TravelUnitUnresolved(attempts: 0));
        var blip = TravelBrain.Decide(TravelSilentBlip());
        await Assert.That(probe.Verb).IsEqualTo(blip.Verb);
        await Assert.That(probe.Terminal).IsEqualTo(blip.Terminal);

        var probeWake = new JevStuckWake("probe", TravelUnitUnresolved(attempts: 0), probe, 1);
        var blipWake = new JevStuckWake("blip", TravelSilentBlip(), blip, 1);
        await Assert.That(JevStuckClassifier.Classify(probeWake))
            .IsEqualTo(JevStuckRootCause.StalePerceptionPursuit);
        await Assert.That(JevStuckClassifier.Classify(blipWake))
            .IsEqualTo(JevStuckRootCause.Unknown);

        // The same blip shape over a streak IS scheduler silence.
        var streakWake = blipWake with { ConsecutiveSilentWakes = 2 };
        await Assert.That(JevStuckClassifier.Classify(streakWake))
            .IsEqualTo(JevStuckRootCause.SchedulerSilence);
    }

    [Test]
    public async Task Precedence_RepathedUnitLeg_NamesPathNotTarget()
    {
        // A failed leg on a UNIT target carries both signals (a moving target
        // AND a failed leg): the failed-leg evidence outranks the target kind.
        var inputs = TravelUnitResolved() with
        {
            LegOutcome = TravelLegOutcome.TimedOut,
            PriorRepathCount = 0
        };
        var decision = TravelBrain.Decide(inputs);
        await Assert.That(decision.Reason).IsEqualTo(TravelReason.Repathed);

        var wake = new JevStuckWake("unit-repathed", inputs, decision, 0);
        await Assert.That(JevStuckClassifier.Classify(wake)).IsEqualTo(JevStuckRootCause.PathInvalid);
    }

    [Test]
    public async Task Unknown_ForHealthyUnarmedAndSingleBlip()
    {
        // A healthy local leg, an unarmed wake, and a single silent blip are
        // all out of scope for this experiment — never mislabelled.
        var healthy = TravelReady();
        await Assert.That(JevStuckClassifier.Classify(
            new JevStuckWake("healthy", healthy, TravelBrain.Decide(healthy), 0)))
            .IsEqualTo(JevStuckRootCause.Unknown);

        var unarmed = TravelReady() with { TargetKind = TravelTargetKind.None };
        await Assert.That(JevStuckClassifier.Classify(
            new JevStuckWake("unarmed", unarmed, TravelBrain.Decide(unarmed), 5)))
            .IsEqualTo(JevStuckRootCause.Unknown);

        var blip = TravelSilentBlip();
        await Assert.That(JevStuckClassifier.Classify(
            new JevStuckWake("blip", blip, TravelBrain.Decide(blip), 1)))
            .IsEqualTo(JevStuckRootCause.Unknown);
    }

    // ------------------------------------------------------------ fixtures

    private static JevLabeledStuckWake Label(
        string wakeId, TravelBrainInputs inputs, int silentWakes, JevStuckRootCause expected)
        => new(new JevStuckWake(wakeId, inputs, TravelBrain.Decide(inputs), silentWakes), expected);

    private static TravelBrainInputs TravelReady() => new(
        ActorObjId: 4242,
        SelfPosition: Self,
        TargetKind: TravelTargetKind.Position,
        TargetObjId: 0,
        Destination: new Vector3(400f, 100f, 10f),
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

    private static TravelBrainInputs TravelUnitUnresolved(int attempts) => TravelReady() with
    {
        TargetKind = TravelTargetKind.Unit,
        TargetObjId = 500,
        Destination = new Vector3(410f, 100f, 10f),
        DistanceM = 300f,
        TargetResolved = false,
        PriorResolveAttempts = attempts
    };

    private static TravelBrainInputs TravelUnitResolved() => TravelReady() with
    {
        TargetKind = TravelTargetKind.Unit,
        TargetObjId = 500,
        Destination = new Vector3(410f, 100f, 10f),
        DistanceM = 100f,
        FollowRequested = true,
        TargetResolved = true
    };

    private static TravelBrainInputs TravelFollowDrifted() => TravelUnitResolved() with
    {
        LegLive = true,
        LegTargetObjId = 501
    };

    private static TravelBrainInputs TravelSilentBlip() => TravelReady() with
    {
        DistanceM = float.NaN
    };
}
