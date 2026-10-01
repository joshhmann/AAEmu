#nullable enable

using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// The sampling trigger set: pure predicates over already-frozen state. Each
/// test freezes inputs/a decision (or a token history) and asserts the
/// trigger fires — or stays silent — without any world, actor, or clock.
/// </summary>
[NotInParallel]
public class JevSamplingTriggerTests
{
    private static readonly Vector3 Self = new(100f, 100f, 10f);
    private static readonly DateTime FrozenNow = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ tight spread

    [Test]
    public async Task TightSpread_FiresWhenRivalsScoreWithinTheGap()
    {
        // Two rivals at nearly the same distance: their ranked scores differ
        // by less than one proximity step, so the commitment was ambiguous.
        var inputs = CombatInputs([Rival(90_001, 10f), Rival(90_002, 11f)]);

        await Assert.That(JevSamplingTriggers.TightScoreSpread(inputs)).IsTrue();
        await Assert.That(JevSamplingTriggers.CollectCombat(inputs, CombatBrain.Decide(inputs)))
            .Contains(JevSamplingTriggers.TightSpreadTrigger);
    }

    [Test]
    public async Task TightSpread_StaysSilentWhenOneRivalDominates()
    {
        // A close rival against a far one: the gap dwarfs the threshold, so
        // there is nothing ambiguous to sample.
        var inputs = CombatInputs([Rival(90_001, 2f), Rival(90_002, 24f)]);

        await Assert.That(JevSamplingTriggers.TightScoreSpread(inputs)).IsFalse();
    }

    [Test]
    public async Task TightSpread_NeedsTwoMeasuredRows()
    {
        // A lone candidate — or a set with only unmeasured rows — is not a
        // competition, so the trigger stays silent.
        await Assert.That(JevSamplingTriggers.TightScoreSpread(CombatInputs([]))).IsFalse();
        await Assert.That(JevSamplingTriggers.TightScoreSpread(CombatInputs([Rival(90_001, 5f)]))).IsFalse();
        var unmeasured = new CombatCandidate(90_003,
            CombatTargetScorer.AttentionScoreOf(true, true), float.NaN, 10, 1.0f, false, 0f);
        await Assert.That(JevSamplingTriggers.TightScoreSpread(CombatInputs([Rival(90_001, 5f), unmeasured]))).IsFalse();
    }

    // ------------------------------------------------------------ retries

    [Test]
    public async Task TravelRetried_FiresOnlyWhenARetryWasSpent()
    {
        await Assert.That(JevSamplingTriggers.TravelRetried(TravelReady())).IsFalse();
        await Assert.That(JevSamplingTriggers.TravelRetried(TravelReady() with { PriorRepathCount = 1 })).IsTrue();
        await Assert.That(JevSamplingTriggers.TravelRetried(TravelReady() with { PriorResolveAttempts = 1 })).IsTrue();
    }

    [Test]
    public async Task NeedsRetried_FiresOnlyWhenDiscardsWereSpent()
    {
        await Assert.That(JevSamplingTriggers.NeedsRetried(NeedsBlank())).IsFalse();
        await Assert.That(JevSamplingTriggers.NeedsRetried(NeedsBlank() with { SoilAttempts = 2 })).IsTrue();
        await Assert.That(JevSamplingTriggers.NeedsRetried(NeedsBlank() with { CropApproachAttempts = 1 })).IsTrue();
    }

    [Test]
    public async Task CombatRecommitted_FiresOnlyWhenTheCompetitionActuallyRan()
    {
        // A live incumbent past its window with rivals present: the ranked
        // competition ran against it this wake.
        await Assert.That(JevSamplingTriggers.CombatRecommitted(CombatInputs([Rival(90_002, 20f)]))).IsTrue();
        // Inside the window the incumbent is kept unconditionally: no retry.
        var inForce = CombatInputs([Rival(90_002, 20f)]) with { CommitmentInForce = true };
        await Assert.That(JevSamplingTriggers.CombatRecommitted(inForce)).IsFalse();
        // No rivals, no incumbent: nothing was re-decided.
        var noRivals = CombatInputs([]) with { IncumbentObjId = 0u, IncumbentValid = false };
        await Assert.That(JevSamplingTriggers.CombatRecommitted(noRivals)).IsFalse();
    }

    // ------------------------------------------------------------ recovery paths

    [Test]
    public async Task RecoveryPath_FiresOnTheUnseenArms()
    {
        // Travel: the retreat anchor and every abandonment terminal.
        var retreat = TravelBrain.Decide(TravelReady() with
        {
            RetreatRequested = true,
            ThreatPosition = new Vector3(90f, 100f, 10f)
        });
        await Assert.That(JevSamplingTriggers.TravelRecoveryPath(retreat)).IsTrue();
        var unreachable = TravelBrain.Decide(TravelReady() with
        {
            LegOutcome = TravelLegOutcome.TimedOut,
            PriorRepathCount = TravelBrain.DefaultRepathBudget
        });
        await Assert.That(unreachable.Terminal).IsEqualTo(TravelTerminal.Unreachable);
        await Assert.That(JevSamplingTriggers.TravelRecoveryPath(unreachable)).IsTrue();
        // A plain local leg is not a recovery path.
        await Assert.That(JevSamplingTriggers.TravelRecoveryPath(TravelBrain.Decide(TravelReady()))).IsFalse();

        // Survival: the recovery demand (never a veto, never a dispatch).
        var recover = SurvivalBrain.Decide(SurvivalHealthy() with { SelfHpRatio = 0.5f });
        await Assert.That(recover.Verdict).IsEqualTo(SurvivalVerdict.Recover);
        await Assert.That(JevSamplingTriggers.SurvivalRecoveryPath(recover)).IsTrue();
        await Assert.That(JevSamplingTriggers.SurvivalRecoveryPath(SurvivalBrain.Decide(SurvivalHealthy()))).IsFalse();

        // Needs: the replant re-evaluation and the spent-budget hold.
        var replant = NeedsBrain.Decide(NeedsBlank() with { HarvestJustLanded = true });
        await Assert.That(JevSamplingTriggers.NeedsRecoveryPath(replant)).IsTrue();
        var spent = NeedsBrain.Decide(NeedsBlank() with
        {
            SeedCount = 1,
            SoilAttempts = NeedsBrain.DefaultMaxSoilAttempts + 1
        });
        await Assert.That(JevSamplingTriggers.NeedsRecoveryPath(spent)).IsTrue();
        await Assert.That(JevSamplingTriggers.NeedsRecoveryPath(NeedsBrain.Decide(NeedsBlank() with { SeedCount = 1 }))).IsFalse();

        // Combat: the disengage and the heal.
        var disengage = CombatBrain.Decide(CombatInputs([]) with { SelfHpRatio = 0.1f });
        await Assert.That(disengage.Arm).IsEqualTo(CombatArm.Disengage);
        await Assert.That(JevSamplingTriggers.CombatRecoveryPath(disengage)).IsTrue();
        var heal = CombatBrain.Decide(CombatInputs([]) with { SelfHpRatio = 0.3f, HealItemTemplateId = 3100 });
        await Assert.That(heal.Arm).IsEqualTo(CombatArm.Heal);
        await Assert.That(JevSamplingTriggers.CombatRecoveryPath(heal)).IsTrue();
        await Assert.That(JevSamplingTriggers.CombatRecoveryPath(CombatBrain.Decide(CombatInputs([])))).IsFalse();
    }

    // ------------------------------------------------------------ oscillation

    [Test]
    public async Task Oscillation_FiresOnlyOnAnAlternation()
    {
        await Assert.That(JevSamplingTriggers.Oscillating(["seek-soil", "travel", "seek-soil"])).IsTrue();
        await Assert.That(JevSamplingTriggers.Oscillating(["a", "b", "a", "b"])).IsTrue();
        await Assert.That(JevSamplingTriggers.Oscillating(["settled", "settled", "settled"])).IsFalse();
        await Assert.That(JevSamplingTriggers.Oscillating(["a", "b", "c"])).IsFalse();
    }

    [Test]
    public async Task Oscillation_JudgesTheTrailingWindow()
    {
        // Five tokens, the last four strictly alternate: settling history
        // before a flip-flop still samples.
        await Assert.That(JevSamplingTriggers.Oscillating(["x", "a", "b", "a", "b"])).IsTrue();
        await Assert.That(JevSamplingTriggers.Oscillating(["a", "b", "a", "b", "c"])).IsFalse();
    }

    // ------------------------------------------------------------ collectors

    [Test]
    public async Task Collectors_CombineTriggersForOneFrozenWake()
    {
        var travel = JevSamplingTriggers.CollectTravel(
            TravelReady() with { PriorRepathCount = 1 },
            TravelBrain.Decide(TravelReady() with { PriorRepathCount = 1 }),
            ["travel", "hold", "travel"]);
        await Assert.That(travel).Contains(JevSamplingTriggers.RetriedTrigger);
        await Assert.That(travel).Contains(JevSamplingTriggers.OscillationTrigger);

        var survival = JevSamplingTriggers.CollectSurvival(
            SurvivalBrain.Decide(SurvivalHealthy()), ["hold", "hold", "hold"]);
        await Assert.That(survival).IsEmpty();

        var combat = JevSamplingTriggers.CollectCombat(
            CombatInputs([Rival(90_001, 10f), Rival(90_002, 11f)]),
            CombatBrain.Decide(CombatInputs([Rival(90_001, 10f), Rival(90_002, 11f)])));
        await Assert.That(combat).Contains(JevSamplingTriggers.TightSpreadTrigger);
    }

    // ------------------------------------------------------------ fixtures

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

    private static SurvivalBrainInputs SurvivalHealthy() => new(
        ActorObjId: 4242,
        SelfHpRatio: 1.0f,
        CombatRetreatPublished: false,
        CommittedTargetObjId: 0,
        SelectedTargetObjId: 0,
        SelfPosition: Self,
        ThreatPosition: Vector3.Zero);

    private static NeedsBrainInputs NeedsBlank() => new(
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

    private static CombatBrainInputs CombatInputs(IReadOnlyList<CombatCandidate> candidates) => new(
        ActorObjId: 70001,
        Role: CombatRole.Melee,
        Alive: true,
        SelfHpRatio: 1.0f,
        SelfLevel: 10,
        SelfPosition: Vector3.Zero,
        EnemyCount: 1,
        NearestEnemyDistanceM: 2f,
        TookDamageThisFrame: false,
        CrowdControlled: false,
        CcAvailable: true,
        IsAutoAttackLive: false,
        HealItemTemplateId: 0,
        SkillMinRangeM: 0f,
        SkillMaxRangeM: 4f,
        SelectedSkillId: 18131,
        LastSkillUsed: 0,
        IncumbentObjId: 90_001,
        IncumbentValid: true,
        IncumbentUnknown: false,
        IncumbentScore: 0,
        CommitmentInForce: false,
        IncumbentPosition: new Vector3(2f, 0f, 0f),
        IncumbentDistanceM: 2f,
        IncumbentLeashDriftM: 0f,
        LeashBudgetM: 50f,
        Candidates: candidates,
        NowUtc: FrozenNow);

    private static CombatCandidate Rival(uint objId, float distance)
        => new(objId,
            CombatTargetScorer.AttentionScoreOf(attackTarget: true, hostileInAggro: true),
            distance, 10, 1.0f, false, 0f);
}
