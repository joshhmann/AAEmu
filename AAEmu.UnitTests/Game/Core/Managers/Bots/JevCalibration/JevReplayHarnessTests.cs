#nullable enable

using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// The offline replay harness: frozen wake records re-decided N times must
/// agree with the frozen verdict every time. One arm per brain, each built
/// from the same synthetic inputs the brain's own frozen unit tests pin —
/// no world, no actor, no engine, no clock.
/// </summary>
[NotInParallel]
public class JevReplayHarnessTests
{
    private const int Replays = 4;
    private static readonly Vector3 Self = new(100f, 100f, 10f);
    private static readonly Vector3 Far = new(400f, 100f, 10f);
    private static readonly DateTime FrozenNow = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Before(Test)]
    public void Reset()
    {
        TravelIntentStore.ClearAll();
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
    }

    [Test]
    public async Task TravelWake_ReplaysToTheSameVerdict()
    {
        var inputs = TravelReady();
        var wake = new JevTravelWake("travel-local", inputs, TravelBrain.Decide(inputs));

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsTrue();
        await Assert.That(result.Brain).IsEqualTo(JevBrain.Travel);
        await Assert.That(result.Replays).IsEqualTo(Replays);
        await Assert.That(result.MismatchDetail).IsNull();
    }

    [Test]
    public async Task TravelRetreatWake_ReplaysToTheSameAnchor()
    {
        // The retreat arm derives its destination from live positions, so the
        // anchor math itself is part of the determinism contract.
        var inputs = TravelReady() with { RetreatRequested = true, ThreatPosition = new Vector3(90f, 100f, 10f) };
        var wake = new JevTravelWake("travel-retreat", inputs, TravelBrain.Decide(inputs));

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsTrue();
        await Assert.That(wake.Expected.Destination).IsNotNull();
    }

    [Test]
    public async Task SurvivalWake_ReplaysToTheSameVerdict()
    {
        var inputs = SurvivalHealthy();
        var wake = new JevSurvivalWake("survival-healthy", inputs, SurvivalBrain.Decide(inputs));

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsTrue();
        await Assert.That(result.Brain).IsEqualTo(JevBrain.Survival);
    }

    [Test]
    public async Task SurvivalFleeWake_ReplaysToTheSameAnchor()
    {
        var inputs = SurvivalHealthy() with
        {
            SelfHpRatio = 0.19f,
            CommittedTargetObjId = 91_001,
            ThreatPosition = new Vector3(90f, 100f, 10f)
        };
        var wake = new JevSurvivalWake("survival-flee", inputs, SurvivalBrain.Decide(inputs));

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsTrue();
        await Assert.That(wake.Expected.Verdict).IsEqualTo(SurvivalVerdict.Flee);
    }

    [Test]
    public async Task NeedsWake_ReplaysToTheSameVerdict()
    {
        var inputs = NeedsSoilSeek() with { SeedCount = 1 };
        var wake = new JevNeedsWake("needs-seek-soil", inputs, NeedsBrain.Decide(inputs));

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsTrue();
        await Assert.That(wake.Expected.Verdict).IsEqualTo(NeedsVerdict.SeekSoil);
    }

    [Test]
    public async Task CombatWake_ReplaysToTheSameVerdict()
    {
        var inputs = CombatMelee(close: true);
        var wake = new JevCombatWake("combat-opener", inputs, CombatBrain.Decide(inputs));

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsTrue();
        await Assert.That(result.Brain).IsEqualTo(JevBrain.Combat);
        await Assert.That(wake.Expected.Arm).IsEqualTo(CombatArm.Opener);
    }

    [Test]
    public async Task CombatWake_WithRivals_ReplaysTheSameCommitment()
    {
        // A ranked competition with two rivals: the scorer's total order must
        // pick the same winner on every replay.
        var inputs = CombatMelee(close: true, rivals: true);
        var wake = new JevCombatWake("combat-rivals", inputs, CombatBrain.Decide(inputs));

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsTrue();
    }

    [Test]
    public async Task TamperedRecord_IsReportedAsADivergence()
    {
        // A record whose frozen verdict was altered after the fact must FAIL
        // the replay: the harness reports the divergence instead of agreeing.
        var inputs = TravelReady();
        var honest = TravelBrain.Decide(inputs);
        var tampered = honest with { Reason = TravelReason.Repathed };
        var wake = new JevTravelWake("travel-tampered", inputs, tampered);

        var result = JevReplayHarness.Replay(wake, Replays);

        await Assert.That(result.Deterministic).IsFalse();
        await Assert.That(result.MismatchDetail).IsNotNull();
        await Assert.That(result.MismatchDetail!).Contains("travel-tampered");
    }

    [Test]
    public async Task ZeroReplays_IsRejectedRatherThanVacuouslyPassing()
    {
        var inputs = TravelReady();
        var wake = new JevTravelWake("travel-zero", inputs, TravelBrain.Decide(inputs));

        await Assert.That(() => JevReplayHarness.Replay(wake, 0)).Throws<ArgumentOutOfRangeException>();
    }

    // ------------------------------------------------------------ fixtures

    private static TravelBrainInputs TravelReady() => new(
        ActorObjId: 4242,
        SelfPosition: Self,
        TargetKind: TravelTargetKind.Position,
        TargetObjId: 0,
        Destination: Far,
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

    private static SurvivalBrainInputs SurvivalHealthy() => new(
        ActorObjId: 4242,
        SelfHpRatio: 1.0f,
        CombatRetreatPublished: false,
        CommittedTargetObjId: 0,
        SelectedTargetObjId: 0,
        SelfPosition: Self,
        ThreatPosition: Vector3.Zero);

    private static NeedsBrainInputs NeedsSoilSeek() => new(
        ActorObjId: 7777,
        SelfPosition: Self,
        SoilReadable: true,
        SeedReadable: true,
        SeedCount: 0,
        OnValidSoil: false,
        CropState: NeedsCropState.None,
        CropMature: false,
        CropDistanceM: float.NaN,
        CropPosition: Vector3.Zero,
        CropEnRoute: false,
        CropObjId: 0,
        CropTemplateId: 0,
        CropApproachAttempts: 0,
        MaxCropApproachAttempts: NeedsBrain.DefaultMaxCropApproachAttempts,
        HarvestJustLanded: false,
        SoilEnRoute: false,
        SoilResolved: false,
        SoilDestination: Vector3.Zero,
        SoilAttempts: 0,
        MaxSoilAttempts: NeedsBrain.DefaultMaxSoilAttempts,
        HarvestRangeM: NeedsBrain.DefaultHarvestRangeM);

    private static CombatBrainInputs CombatMelee(bool close, bool rivals = false)
    {
        const uint boar = 90_001;
        var distance = close ? 2f : 25f;
        IReadOnlyList<CombatCandidate> candidates = rivals
            ? [Rival(boar, 2f), Rival(90_002, 20f)]
            : [];
        return new CombatBrainInputs(
            ActorObjId: 70001,
            Role: CombatRole.Melee,
            Alive: true,
            SelfHpRatio: 1.0f,
            SelfLevel: 10,
            SelfPosition: Vector3.Zero,
            EnemyCount: 1,
            NearestEnemyDistanceM: distance,
            TookDamageThisFrame: false,
            CrowdControlled: false,
            CcAvailable: true,
            IsAutoAttackLive: false,
            HealItemTemplateId: 0,
            SkillMinRangeM: 0f,
            SkillMaxRangeM: 4f,
            SelectedSkillId: 18131,
            LastSkillUsed: 0,
            IncumbentObjId: boar,
            IncumbentValid: true,
            IncumbentUnknown: false,
            IncumbentScore: 0,
            CommitmentInForce: false,
            IncumbentPosition: new Vector3(distance, 0f, 0f),
            IncumbentDistanceM: distance,
            IncumbentLeashDriftM: 0f,
            LeashBudgetM: 50f,
            Candidates: candidates,
            NowUtc: FrozenNow);
    }

    private static CombatCandidate Rival(uint objId, float distance)
        => new(objId,
            CombatTargetScorer.AttentionScoreOf(attackTarget: true, hostileInAggro: true),
            distance, 10, 1.0f, false, 0f);
}
