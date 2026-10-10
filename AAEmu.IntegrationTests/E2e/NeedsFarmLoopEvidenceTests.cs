using System.Text.Json;
using System.Text;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

// Synthetic consumer inputs, NOT live proof and NOT assertions about production
// default properties or source wording. Required audit stamps must be real in a run.
public class NeedsFarmLoopEvidenceTests
{
    private static readonly DateTime Epoch = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
    private static Guid Trace(int n) => new(n, 0, 0, new byte[8]);
    private static object Crop(uint obj = 501, uint db = 901, uint phase = 4379, double planted = 4.1, uint owner = 7)
        => new { objId = obj, found = true, dbId = db, template = 2259u, phase, ownerId = owner,
            ownerType = "Character", houseId = 0u, farmType = "Farm", plantTime = Epoch.AddSeconds(planted),
            growthTime = Epoch.AddSeconds(planted + 60), x = 10.5, y = 0d, z = 0d };
    private static object Gone(uint obj = 501) => new { objId = obj, found = false };
    private static object Audit(int n, string action, double requested, double completed, uint target = 501,
        long? wake = null, uint actor = 42, string result = "Completed", bool attribution = true, string? cycle = null)
        => new { trace_id = Trace(n), actor_id = actor, action, target_id = target, requested_at = Epoch.AddSeconds(requested),
            completed_at = Epoch.AddSeconds(completed), result, decision_goal = attribution ? action == "Plant" ? "needs.plant" : "needs.harvest" : "needs.farm",
            decision_policy = attribution ? "needs-v1" : "module:NeedsFarm",
            decision_cycle_id = attribution ? cycle ?? Cycle(requested) : null,
            wake_sequence = attribution ? wake : null, move_owner = "OTHER:needs-farm-travel" };
    private static string Cycle(double requested) => "opaque-decision@" + requested.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    // Terminal audit is a retained tail, not just this poll's new rows.
    private static object[] History(double at, object[]? replacements)
    {
        var rows = new List<object>();
        if (at >= 2) rows.Add(Audit(10, "Move", -1, 1.2, target: 0, result: "Interrupted"));
        if (at >= 5) rows.Add(Audit(100, "Plant", 4, 4.3, target: 15659));
        if (at >= 67) rows.Add(Audit(101, "Harvest", 66, 66.3));
        if (at >= 71) rows.Add(Audit(102, "Plant", 70, 70.3, target: 15659));
        if (at >= 133) rows.Add(Audit(103, "Harvest", 132, 132.3, target: 601));
        rows.AddRange(replacements ?? []);
        return rows.GroupBy(r => JsonSerializer.SerializeToElement(r).GetProperty("trace_id").GetGuid())
            .Select(g => g.Last()).ToArray();
    }
    private static JsonElement Snapshot(double at, long seeds = 3, long potatoes = 0, long auxiliary = 0,
        long labor = 100, long wake = 1, object[]? crops = null, object[]? audit = null, uint tracked = 0,
        bool registered = true, bool actorAvailable = true, long generation = 3, uint actor = 42, double x = 1,
        int? live = null, double liveRequested = -1, string owner = "OTHER:needs-farm-travel", double? begin = null,
        bool retainedHistory = true, long experience = 0, long abilityPoint = 0, string liveAction = "Move", uint liveTarget = 0)
        => JsonSerializer.SerializeToElement(new
        {
            snapshotStartedAtUtc = Epoch.AddSeconds(begin ?? at - 0.02), observedAtUtc = Epoch.AddSeconds(at),
            id = 7u, objId = actor, accountId = 9u, actorCharId = actorAvailable ? 7u : (uint?)null,
            actorObjId = actorAvailable ? actor : (uint?)null, enrollmentGeneration = generation,
            enrollmentRegistered = registered, stateAvailable = actorAvailable, actorAvailable,
            enrollmentSchedulerRunning = true, enrollmentIdleWakeEnabled = true, isDead = false,
            level = 1, experience, money = 0, labor, dropRateMul = 0d, growthRate = 10d, lootRate = 1d,
            actabilities = new[] { new { id = 1u, point = abilityPoint, step = 0 } },
            laborConsumptionDisabled = false, arbiterActivity = "needs.farm", wakeSequence = wake,
            x, y = 0d, z = 0d, worldId = 1u, zoneId = 0u, cropObjId = tracked,
            // These intentionally bogus counters must have no verdict power.
            plants = 99, harvests = 99,
            items = new[] { new { template = 15659u, count = seeds }, new { template = 7992u, count = potatoes }, new { template = 19887u, count = auxiliary } },
            doodads = crops ?? [], actorAudit = actorAvailable ? retainedHistory ? History(at, audit) : audit ?? [] : null,
            liveTraceId = live.HasValue ? Trace(live.Value) : (Guid?)null, liveRequestedAtUtc = live.HasValue ? Epoch.AddSeconds(liveRequested) : (DateTime?)null,
            liveAction = live.HasValue ? liveAction : null, liveTargetId = live.HasValue ? liveTarget : 0u,
            liveState = live.HasValue ? "Running" : null, liveMoveOwner = owner,
            liveDestinationX = live.HasValue ? 10d : (double?)null, liveDestinationY = live.HasValue ? 0d : (double?)null,
            liveDestinationZ = live.HasValue ? 0d : (double?)null,
        });
    private static JsonElement Control(bool payload = true, int trace = 11, uint actor = 42,
        double requested = 1.1, double completed = 1.3)
        => JsonSerializer.SerializeToElement(new { trace_id = Trace(trace), actor_id = actor, action = "Interrupt", state = "Completed",
            requested_at = Epoch.AddSeconds(requested), completed_at = Epoch.AddSeconds(completed), result_payload = payload });
    private static NeedsFarmLoopEvidence Initial()
    {
        var proof = new NeedsFarmLoopEvidence(7, Snapshot(-2, registered: false, actorAvailable: false, x: 0), Epoch);
        proof.Observe(1, Snapshot(1, x: 1, live: 10));
        return proof;
    }
    private static NeedsFarmLoopEvidence RecoveredApproach(bool interrupted = true)
    {
        var proof = Initial();
        var candidate = proof.GetInterruptCandidate();
        Assert.NotNull(candidate);
        Assert.True(candidate.RequestedBeforeStart);
        proof.DeclareInterrupt(candidate);
        proof.AcknowledgeInterrupt(Trace(11));
        proof.ObserveControl(Control());
        proof.Observe(2, Snapshot(2, x: 2, live: 20, liveRequested: 1.5,
            audit: [Audit(10, "Move", -1, 1.2, target: 0, wake: 1, result: interrupted ? "Interrupted" : "Completed")]));
        proof.Observe(3, Snapshot(3, x: 3, live: 20, liveRequested: 1.5));
        return proof;
    }
    private static NeedsFarmLoopEvidence Planted()
    {
        var proof = RecoveredApproach();
        proof.Observe(5, Snapshot(5, seeds: 2, wake: 10, tracked: 501, x: 10,
            crops: [Crop()], audit: [Audit(100, "Plant", 4, 4.3, target: 15659)]));
        proof.RecordDatabaseCreation(901, 7, 2259, Epoch.AddSeconds(4), 0, 1);
        Assert.Null(proof.EvidenceFault);
        return proof;
    }
    private static NeedsFarmLoopEvidence FirstMature()
    {
        var proof = Planted();
        proof.Observe(8, Snapshot(8, seeds: 2, wake: 20, tracked: 501, x: 9,
            crops: [Crop(phase: 4456)]));
        proof.Observe(65, Snapshot(65, seeds: 2, wake: 30, tracked: 501, x: 9,
            crops: [Crop(phase: 4457)]));
        Assert.Null(proof.EvidenceFault);
        return proof;
    }
    private static NeedsFarmLoopEvidence FirstHarvest(bool returnedSeed = false, bool auxiliary = false, bool deletion = true)
    {
        var proof = FirstMature();
        proof.Observe(67, Snapshot(67, seeds: returnedSeed ? 3 : 2, potatoes: 3, auxiliary: auxiliary ? 1 : 0,
            labor: 99, wake: 41, x: 9, crops: [Gone()], audit: [Audit(101, "Harvest", 66, 66.3)]));
        if (deletion) proof.RecordDatabaseDeletion(Trace(100), true);
        Assert.Null(proof.EvidenceFault);
        return proof;
    }
    private static void SecondPlant(NeedsFarmLoopEvidence proof, bool recycled = false, bool returnedSeed = false, bool auxiliary = false)
    {
        uint obj = recycled ? 501u : 601u, db = recycled ? 901u : 902u;
        proof.Observe(71, Snapshot(71, seeds: returnedSeed ? 2 : 1, potatoes: 3, auxiliary: auxiliary ? 1 : 0,
            labor: 99, wake: 51, tracked: obj, x: 10, crops: [Crop(obj, db, planted: 70.1)],
            audit: [Audit(102, "Plant", 70, 70.3, target: 15659)]));
        if (proof.EvidenceFault == null) proof.RecordDatabaseCreation(db, 7, 2259, Epoch.AddSeconds(70), 0, 1);
    }
    private static void FinishSecond(NeedsFarmLoopEvidence proof, bool recycled = false, bool returnedSeed = false, bool auxiliary = false,
        string? harvestCycle = null)
    {
        uint obj = recycled ? 501u : 601u, db = recycled ? 901u : 902u;
        var seeds = returnedSeed ? 2 : 1;
        proof.Observe(74, Snapshot(74, seeds: seeds, potatoes: 3, auxiliary: auxiliary ? 1 : 0,
            labor: 99, wake: 60, tracked: obj, x: 9, crops: [Crop(obj, db, 4456, 70.1)]));
        proof.Observe(131, Snapshot(131, seeds: seeds, potatoes: 3, auxiliary: auxiliary ? 1 : 0,
            labor: 99, wake: 70, tracked: obj, x: 9, crops: [Crop(obj, db, 4457, 70.1)]));
        proof.Observe(133, Snapshot(133, seeds: seeds, potatoes: 6, auxiliary: auxiliary ? 1 : 0,
            labor: 98, wake: 81, x: 9, crops: [Gone(obj)], audit: [Audit(103, "Harvest", 132, 132.3, target: obj, cycle: harvestCycle)]));
    }

    [Fact]
    public void RecoverySamplesBeforeControlPollRemainActualEvidenceWhenWindowLaterQualifies()
    {
        var proof = Initial();
        proof.DeclareInterrupt(proof.GetInterruptCandidate()!);
        proof.AcknowledgeInterrupt(Trace(11));
        proof.Observe(2, Snapshot(2, x: 2, live: 20, liveRequested: 1.5));
        Assert.False(proof.ControlCompleted);
        proof.ObserveControl(Control());
        proof.Observe(3, Snapshot(3, x: 3, live: 20, liveRequested: 1.5));
        proof.Observe(5, Snapshot(5, seeds: 2, wake: 10, tracked: 501, x: 10, crops: [Crop()]));
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.RecoveryProved);
        Assert.Single(proof.Lifetimes);
    }

    [Theory]
    [InlineData(1.05)]
    [InlineData(1.35)]
    public void ExactVictimTerminalOutsideTheTrueControlWindowCannotQualifyRecovery(double terminal)
    {
        var proof = Initial();
        proof.DeclareInterrupt(proof.GetInterruptCandidate()!);
        proof.AcknowledgeInterrupt(Trace(11));
        proof.Observe(2, Snapshot(2, x: 2, live: 20, liveRequested: 1.5,
            audit: [Audit(10, "Move", -1, terminal, target: 0, result: "Interrupted")]));
        proof.ObserveControl(Control());
        Assert.Contains("OUTSIDE_CONTROL_WINDOW", proof.EvidenceFault);
        Assert.False(proof.ControlCompleted);
        Assert.False(proof.RecoveryProved);
    }

    [Fact]
    public void ReusedSelectedDecisionCycleCannotLabelAnotherAppliedHarvest()
    {
        var proof = FirstMature();
        proof.Observe(67, Snapshot(67, seeds: 2, potatoes: 3, labor: 99, wake: 41, crops: [Gone()],
            audit: [Audit(101, "Harvest", 66, 66.3, cycle: Cycle(4))]));
        Assert.Contains("REUSED_DECISION_CYCLE", proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }


    [Fact]
    public void ADecreasingDecoratorWakeCannotSupplyTheSecondImmatureWitness()
    {
        var proof = Planted();
        proof.Observe(8, Snapshot(8, seeds: 2, wake: 9, tracked: 501, crops: [Crop(phase: 4456)]));
        Assert.Contains("WAKE_REGRESSED", proof.EvidenceFault);
        Assert.Single(proof.Lifetimes[0].Immature);
    }

    [Fact]
    public void ConflictingPresentAndMissingCropRowsCannotProveAnAppliedHarvest()
    {
        var proof = FirstMature();
        proof.Observe(67, Snapshot(67, seeds: 2, potatoes: 3, labor: 99, wake: 41,
            crops: [Crop(phase: 4457), Gone()]));
        Assert.Contains("conflicting crop disposition", proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }

    [Fact]
    public void MissingPreviousAuditTailRejectsAResourceReceiptWithHiddenRequestHistory()
    {
        var proof = FirstMature();
        proof.Observe(67, Snapshot(67, seeds: 2, potatoes: 3, labor: 99, wake: 41,
            crops: [Gone()], retainedHistory: false, audit: [Audit(101, "Harvest", 66, 66.3)]));
        Assert.Contains("PREVIOUS_TAIL_MISSING", proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }

    [Fact]
    public async Task BridgeIngressCountsUtf8BytesAndRejectsBeforeUnboundedParsing()
    {
        var bytes = Encoding.UTF8.GetBytes("\U00020000a\n");
        using var accepted = new MemoryStream(bytes);
        Assert.Equal("\U00020000a", await BotDriveClient.ReadBoundedLineAsync(accepted, 6, CancellationToken.None));
        using var rejected = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => BotDriveClient.ReadBoundedLineAsync(rejected, 5, CancellationToken.None));
    }

    [Fact]
    public void SyntheticInFlightHarvestKeepsEarlierMaturityAndRequiresExactObservedRunningRequest()
    {
        var proof = FirstMature();
        // Synthetic4000ms interval uses canonical authored duration. It is NOT
        // evidence of real casting; current actor doodad.Use is synchronous.
        proof.Observe(68, Snapshot(68, seeds: 2, wake: 40, tracked: 501, crops: [Crop(phase: 4457)],
            live: 101, liveRequested: 66, liveAction: "Harvest", liveTarget: 501, retainedHistory: false,
            audit: [Audit(10, "Move", -1, 1.2, target: 0, result: "Interrupted"), Audit(100, "Plant", 4, 4.3, target: 15659)]));
        proof.Observe(70.1, Snapshot(70.1, seeds: 2, potatoes: 3, labor: 99, wake: 41, crops: [Gone()],
            audit: [Audit(101, "Harvest", 66, 70)]));
        Assert.Null(proof.EvidenceFault);
        Assert.Equal(1, proof.HarvestCount);
        Assert.Equal(Epoch.AddSeconds(65), proof.Lifetimes[0].MatureWitness!.ObservedAtUtc);
    }

    [Fact]
    public void ARunningRequestWithAnotherTargetCannotAuthorizeAnOlderTerminalEffectJoin()
    {
        var proof = FirstMature();
        proof.Observe(68, Snapshot(68, seeds: 2, wake: 40, tracked: 501, crops: [Crop(phase: 4457)],
            live: 101, liveRequested: 66, liveAction: "Harvest", liveTarget: 999, retainedHistory: false,
            audit: [Audit(10, "Move", -1, 1.2, target: 0, result: "Interrupted"), Audit(100, "Plant", 4, 4.3, target: 15659)]));
        proof.Observe(70.1, Snapshot(70.1, seeds: 2, potatoes: 3, labor: 99, wake: 41, crops: [Gone()],
            audit: [Audit(101, "Harvest", 66, 70)]));
        Assert.Contains("AMBIGUOUS", proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }

    [Fact]
    public void FourDistinctOpaqueCyclesCanCompleteTwoCropLifetimes()
    {
        var proof = CompletedProof();
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.LiveAccepted);
        Assert.Equal(2, proof.HarvestCount);
    }

    [Theory]
    [InlineData(66, 70, 70.08)]
    [InlineData(67.98, 70, 70)]
    [InlineData(67.98, 68, 70.08)]
    public void EqualityAtEitherCarriedProjectionBoundaryCannotProveAnEffectJoin(double priorBegin, double terminal, double nextBegin)
    {
        var proof = FirstMature();
        proof.Observe(68, Snapshot(68, seeds: 2, wake: 40, tracked: 501, begin: priorBegin, crops: [Crop(phase: 4457)],
            live: 101, liveRequested: 66, liveAction: "Harvest", liveTarget: 501, retainedHistory: false,
            audit: [Audit(10, "Move", -1, 1.2, target: 0, result: "Interrupted"), Audit(100, "Plant", 4, 4.3, target: 15659)]));
        proof.Observe(70.1, Snapshot(70.1, seeds: 2, potatoes: 3, labor: 99, wake: 41, begin: nextBegin, crops: [Gone()],
            audit: [Audit(101, "Harvest", 66, terminal)]));
        Assert.Contains("AMBIGUOUS", proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }

    [Fact]
    public void ADistinctOpaqueCycleCannotExcuseARequestStartedBeforeThePriorFarmCompletion()
    {
        var proof = FirstMature();
        proof.Observe(68, Snapshot(68, seeds: 2, wake: 40, tracked: 501, crops: [Crop(phase: 4457)],
            live: 101, liveRequested: 4.2, liveAction: "Harvest", liveTarget: 501, retainedHistory: false,
            audit: [Audit(10, "Move", -1, 1.2, target: 0, result: "Interrupted"), Audit(100, "Plant", 4, 4.3, target: 15659)]));
        proof.Observe(70.1, Snapshot(70.1, seeds: 2, potatoes: 3, labor: 99, wake: 41, crops: [Gone()],
            audit: [Audit(101, "Harvest", 4.2, 70, cycle: "different opaque cycle")]));
        Assert.Contains("STALE_FARM_REQUEST_ORDER", proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }

    private static NeedsFarmLoopE2eTests.FarmDurable Durable(long seeds = 3, long potatoes = 0, long labor = 100,
        long experience = 0, long abilityPoint = 0, double x = 0)
    {
        var items = new List<NeedsFarmLoopE2eTests.FarmItem>();
        if (seeds > 0) items.Add(new(1, 15659, 1, 0, seeds, null, 0, 0, "{\"made_unit_id\":0}"));
        if (potatoes > 0) items.Add(new(2, 7992, 1, 1, potatoes, null, 0, 0, "{\"made_unit_id\":0}"));
        return new(7, 9, labor, 1, experience, 0, x, 0, 0, 1, 0, items, [],
            [new(1, abilityPoint, 0)], [], [], []);
    }
    private static NeedsFarmLoopEvidence CompletedProof()
    {
        var proof = FirstHarvest();
        SecondPlant(proof);
        FinishSecond(proof);
        Assert.True(proof.LiveAccepted);
        return proof;
    }

    [Fact]
    public void CorrectAfterFlushCannotExcuseAReleaseSnapshotThatDisagreesWithFrozenEnd()
    {
        var proof = CompletedProof();
        var baseline = Durable();
        var correct = Durable(seeds: 1, potatoes: 6, labor: 98, x: 9);
        NeedsFarmLoopE2eTests.RequireFarmDurableStage(baseline, correct, proof.LastObservation!.Value, proof);
        var beforeAppliedGain = correct with { Labor = 99, Items = Durable(seeds: 1, potatoes: 3).Items };
        Assert.Throws<InvalidOperationException>(() =>
            NeedsFarmLoopE2eTests.RequireFarmDurableStage(baseline, beforeAppliedGain, proof.LastObservation.Value, proof));
    }

    [Theory]
    [InlineData("quest")]
    [InlineData("completed")]
    [InlineData("house")]
    public void CorrectFarmGainsCannotHideOwnedQuestOrHouseStateChanges(string changed)
    {
        var proof = CompletedProof();
        var after = Durable(seeds: 1, potatoes: 6, labor: 98, x: 9);
        after = changed switch
        {
            "quest" => after with { Quests = ["{\"id\":1,\"template_id\":10,\"data\":\"AA==\",\"status\":1,\"owner\":7}"] },
            "completed" => after with { CompletedQuests = ["{\"id\":1,\"data\":\"AQ==\",\"owner\":7}"] },
            _ => after with { Houses = ["{\"id\":1,\"owner\":7,\"name\":\"changed\"}"] },
        };
        Assert.Throws<InvalidOperationException>(() =>
            NeedsFarmLoopE2eTests.RequireFarmDurableStage(Durable(), after, proof.LastObservation!.Value, proof));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedXpAndActabilityMustAgreeWithDurableValuesNotPredictedFormulas(bool xp)
    {
        var observed = Snapshot(1, registered: false, actorAvailable: false, x: 0, experience: 23, abilityPoint: 17);
        var correct = Durable(experience: 23, abilityPoint: 17);
        Assert.True(NeedsFarmLoopE2eTests.MatchesFarmSnapshot(correct, observed));
        var stale = xp ? correct with { Experience = 0 } : correct with { Actabilities = [new(1, 0, 0)] };
        Assert.False(NeedsFarmLoopE2eTests.MatchesFarmSnapshot(stale, observed));
    }

    [Fact]
    public void SeedingCannotSilentlyRewriteUnrelatedItemInstanceMetadata()
    {
        var body = Durable(seeds: 0);
        var item = new NeedsFarmLoopE2eTests.FarmItem(77, 9000, 1, 8, 1, null, 0, 0, "{\"made_unit_id\":0}");
        body.Items.Add(item);
        var seeded = Durable();
        seeded.Items.Add(item with { Metadata = "{\"made_unit_id\":7}" });
        Assert.Throws<InvalidOperationException>(() => NeedsFarmLoopE2eTests.RequireFarmSetupBody(body, seeded));
    }

    [Fact]
    public void SecondAppliedHarvestCannotReuseAnEarlierHarvestDecisionCycle()
    {
        var proof = FirstHarvest();
        SecondPlant(proof);
        FinishSecond(proof, harvestCycle: Cycle(66));
        Assert.Contains("DECISION_CYCLE", proof.EvidenceFault);
        Assert.Equal(1, proof.HarvestCount);
        Assert.False(proof.LiveAccepted);
    }

    [Fact]
    public void SharedEvidencePrewriteFailurePreservesReservedFinalBytesWithoutChargingARejectedWrite()
    {
        var budget = new NeedsFarmLoopE2eTests.FarmEvidenceBudget(16, 8);
        budget.ReserveRaw("ab");       // observations:3UTF8 bytes with newline
        budget.ReserveRaw("cde");      // audit:4 more from the SAME allowance
        Assert.Throws<InvalidDataException>(() => budget.ReserveRaw("x"));
        Assert.Equal(7, budget.RawBytes);
        budget.ReserveFinal("failed"); // cleanup/final reserve remains available
        Assert.Equal(6, budget.ReservedBytes);
        Assert.Throws<InvalidDataException>(() => budget.ReserveFinal("xxx"));
        Assert.Equal(6, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TwoAppliedHarvestsAcceptDistinctLifetimesIncludingRecycledNumericIdsAndOptionalLoot(bool recycled, bool optionalLoot)
    {
        var proof = FirstHarvest(optionalLoot, optionalLoot);
        SecondPlant(proof, recycled, optionalLoot, optionalLoot);
        Assert.False(proof.LiveAccepted);
        FinishSecond(proof, recycled, optionalLoot, optionalLoot);
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.LiveAccepted);
        Assert.Equal("TWO_APPLIED_HARVESTS", proof.StopReason(133));
        Assert.Equal(2, proof.HarvestCount);
        Assert.All(proof.Lifetimes, life => Assert.Null(life.RequestWakeSequence));
        Assert.All(proof.Lifetimes, life => Assert.Null(life.Harvest!.RequestWakeSequence));
        Assert.All(proof.Lifetimes, life => Assert.Equal(2, life.Immature.Count));
        Assert.NotEqual(proof.Lifetimes[0].PlantTraceId, proof.Lifetimes[1].PlantTraceId);
        Assert.Equal(optionalLoot ? 1 : 0, proof.Lifetimes.Sum(l => l.Harvest!.ReturnedSeeds));
        Assert.Equal(1.5, proof.Lifetimes[0].MatureWitness!.DistanceMetres);
        Assert.Equal(1.5, proof.Lifetimes[0].Harvest!.DistanceToLastCropPositionMetres);
    }
    [Fact]
    public void SecondPlantWithoutSecondHarvestCannotAccept()
    {
        var proof = FirstHarvest();
        SecondPlant(proof);
        Assert.False(proof.LiveAccepted);
        Assert.Equal(1, proof.HarvestCount);
    }
    [Fact]
    public void CompletedPlantWithoutSeedCostCannotBecomeACropLifetime()
    {
        var proof = RecoveredApproach();
        proof.Observe(5, Snapshot(5, wake: 10, tracked: 501, crops: [Crop()],
            audit: [Audit(100, "Plant", 4, 4.3, target: 15659, wake: 9)]));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.Lifetimes);
    }
    [Fact]
    public void CompletedHarvestWithoutPotatoGainCannotAccept()
    {
        var proof = FirstMature();
        proof.Observe(67, Snapshot(67, seeds: 2, labor: 99, wake: 41, crops: [Gone()], audit: [Audit(101, "Harvest", 66, 66.3)]));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }
    [Fact]
    public void TwoImmaturePollsOnOneWakeDoNotProveAutonomousWaiting()
    {
        var proof = Planted();
        proof.Observe(8, Snapshot(8, seeds: 2, wake: 10, tracked: 501, crops: [Crop(phase: 4456)]));
        proof.Observe(65, Snapshot(65, seeds: 2, wake: 30, tracked: 501, crops: [Crop(phase: 4457)]));
        Assert.Contains("WAIT_NOT_WITNESSED", proof.EvidenceFault);
    }
    [Fact]
    public void SnapshotStartBeforeHarvestIsInsufficientWhenEndOfCopyWatermarkIsAfterRequest()
    {
        var proof = FirstMature();
        // Phase read started before the request, but its copy finished after it.
        proof.Observe(66, Snapshot(66, seeds: 2, wake: 40, tracked: 501, begin: 65.1, crops: [Crop(phase: 4457)]));
        proof.Observe(67, Snapshot(67, seeds: 2, potatoes: 3, labor: 99, wake: 41, crops: [Gone()],
            audit: [Audit(101, "Harvest", 65.5, 66.3)]));
        Assert.Contains("AMBIGUOUS", proof.EvidenceFault);
        Assert.Equal(0, proof.HarvestCount);
    }
    [Fact]
    public void TerminalEffectDuringProjectionCannotBeJoinedAsAnAtomicSnapshot()
    {
        var proof = RecoveredApproach();
        proof.Observe(5, Snapshot(5, seeds: 2, wake: 10, tracked: 501, begin: 4.2, crops: [Crop()],
            audit: [Audit(100, "Plant", 4, 4.3, target: 15659, wake: 9)]));
        Assert.Contains("AMBIGUOUS", proof.EvidenceFault);
    }
    [Fact]
    public void CurrentActivityOrIdempotencyNamespaceDoesNotReplaceCopiedDecisionAuditFields()
    {
        var proof = RecoveredApproach();
        proof.Observe(5, Snapshot(5, seeds: 2, wake: 10, tracked: 501, crops: [Crop()],
            audit: [Audit(100, "Plant", 4, 4.3, target: 15659, wake: 9, attribution: false)]));
        Assert.Equal("FRESH_NEEDS_DECISION_JOIN", proof.EvidenceFault);
    }
    [Fact]
    public void TrueControlAcknowledgmentWithoutExactVictimInterruptedDoesNotProveRecovery()
    {
        var proof = RecoveredApproach(interrupted: false);
        proof.Observe(5, Snapshot(5, seeds: 2, wake: 10, tracked: 501, crops: [Crop()],
            audit: [Audit(100, "Plant", 4, 4.3, target: 15659, wake: 9)]));
        Assert.False(proof.RecoveryProved);
        Assert.Contains("TRUE_INTERRUPTION_RECOVERY", proof.EvidenceFault);
    }
    [Theory]
    [InlineData(false, 11, 42)]
    [InlineData(true, 12, 42)]
    [InlineData(true, 11, 43)]
    public void FalsePayloadOrForeignControlCannotProveAnInterruption(bool payload, int trace, int actor)
    {
        var proof = Initial();
        proof.DeclareInterrupt(proof.GetInterruptCandidate()!);
        proof.AcknowledgeInterrupt(Trace(11));
        proof.ObserveControl(Control(payload, trace, (uint)actor));
        Assert.False(proof.ControlCompleted);
        Assert.NotNull(proof.EvidenceFault);
    }
    [Fact]
    public void NonFarmMoveCannotBeAnInterruptionVictim()
    {
        var proof = Initial();
        proof.Observe(2, Snapshot(2, x: 2, live: 10, owner: "ROAM"));
        Assert.Null(proof.GetInterruptCandidate());
    }
    [Theory]
    [InlineData(43, 3)]
    [InlineData(42, 4)]
    public void AnotherActorOrEnrollmentGenerationCannotInheritTheProof(int actor, int generation)
    {
        var proof = Initial();
        proof.Observe(2, Snapshot(2, actor: (uint)actor, generation: generation));
        Assert.NotNull(proof.EvidenceFault);
    }
    [Fact]
    public void AbsentPassiveStateAndBareActionCountersSupplyNoActorOrProgress()
    {
        var proof = new NeedsFarmLoopEvidence(7, Snapshot(-2, registered: false, actorAvailable: false), Epoch);
        proof.Observe(1, Snapshot(1, actorAvailable: false));
        Assert.Equal(0u, proof.ActorId);
        Assert.False(proof.LiveAccepted);
        Assert.Equal("NO_AUTHORITATIVE_PROGRESS", proof.StopReason(300));
    }
    [Fact]
    public void RegistryAbsenceRemainsAbsenceRatherThanSuccessfulEnrollment()
    {
        var proof = new NeedsFarmLoopEvidence(7, Snapshot(-2, registered: false, actorAvailable: false), Epoch);
        proof.Observe(1, Snapshot(1, registered: false, actorAvailable: false));
        Assert.Contains("ENROLLMENT_ABSENT", proof.EvidenceFault);
    }
    [Fact]
    public void RecycledIdWithoutPriorDatabaseDeletionIsAmbiguousNotAnotherLifetime()
    {
        var proof = FirstHarvest(deletion: false);
        SecondPlant(proof, recycled: true);
        Assert.Contains("FRESH_LIFETIME", proof.EvidenceFault);
        Assert.Single(proof.Lifetimes);
    }
    [Fact]
    public void NoProgressBoundaryCannotPromoteLateSuppliedEffects()
    {
        var proof = Initial();
        proof.Observe(300, Snapshot(300, seeds: 2, wake: 100, tracked: 501, crops: [Crop(planted: 299.1)],
            audit: [Audit(100, "Plant", 299, 299.3, target: 15659, wake: 99)]));
        Assert.Empty(proof.Lifetimes);
        Assert.Equal("NO_AUTHORITATIVE_PROGRESS", proof.StopReason(300));
    }
    [Fact]
    public void AcceptanceIsFrozenBeforeAnyThirdChoiceOrCleanupWindowObservation()
    {
        var proof = FirstHarvest();
        SecondPlant(proof);
        FinishSecond(proof);
        Assert.True(proof.LiveAccepted);
        proof.Observe(140, Snapshot(140, seeds: 0, potatoes: 6, labor: 98, wake: 100, tracked: 701,
            crops: [Crop(701, 903, planted: 139.1)], audit: [Audit(104, "Plant", 139, 139.3, target: 15659, wake: 99)]));
        Assert.Equal(2, proof.Lifetimes.Count);
        Assert.Equal(2, proof.HarvestCount);
        Assert.Null(proof.EvidenceFault);
    }
}
