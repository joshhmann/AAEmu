using System.Text.Json;

using Xunit;

using static AAEmu.IntegrationTests.E2e.BotShortRunTests;

namespace AAEmu.IntegrationTests.E2e;

// Consumer regressions using supplied passive observations, not gameplay proof.
// No E2eStack fixture, DB, server, ambient flags, shared files or mutable globals.
public class BotShortRunEvidenceTests
{
    private static readonly DateTime Start = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static Guid Trace(int id) => new(id, 0, 0, new byte[8]);
    private static object Quest(uint id = 1, string step = "Ready")
        => new { questId = id, status = step, step, objectives = new[] { 0 } };
    private static object Flag(uint quest = 1, bool completed = false) => new { questId = quest, completed };

    private static object Audit(int trace, uint quest = 1, string action = "AcceptQuest",
        string result = "Completed", double requested = 1, double completed = 2, uint actor = 42,
        string? goal = null, bool metadata = true, string owner = "QUEST_TRAVEL", string? cycle = null,
        string policy = "quest-v1")
        => new
        {
            trace_id = Trace(trace), actor_id = actor, action, target_id = quest, decision_quest_id = quest == 0 ? (uint?)1 : quest,
            requested_at = Start.AddSeconds(requested), completed_at = Start.AddSeconds(completed), result,
            decision_goal = metadata ? goal ?? (action == "AcceptQuest" ? "quest.accept"
                : action == "AdvanceQuest" ? "quest.advance" : action == "Move" ? "quest.progress" : "quest.turn-in") : null,
            decision_policy = metadata ? policy : null,
            decision_cycle_id = metadata ? cycle ?? $"opaque-decision-{trace}" : null,
            move_owner = owner, detail = "arbitrary presentation, not a verdict input",
        };

    private static JsonElement Observation(double at = 0, uint chosen = 0, object[]? quests = null,
        object[]? flags = null, object[]? audit = null, uint actor = 42, uint actorCharacter = 7,
        long generation = 3, bool dead = false, int? live = null, double liveRequested = 4,
        double? routeX = 100, double? routeY = 0, int completedCount = 0, double x = 0,
        string activity = "quest.progress", string liveAction = "Move", uint liveTarget = 0)
        => JsonSerializer.SerializeToElement(new
        {
            id = 7u, actorCharId = actorCharacter, actorObjId = actor, enrollmentGeneration = generation,
            observedAtUtc = Start.AddSeconds(at), chosenQuestId = chosen, isDead = dead,
            snapshotStartedAtUtc = Start.AddSeconds(at - 0.001),
            enrollmentRegistered = true, enrollmentSchedulerRunning = true, enrollmentIdleWakeEnabled = true,
            activeQuests = quests ?? [], questCompletion = flags ?? new[] { Flag(), Flag(2) },
            actorAudit = audit ?? [], completedCount,
            questTravelTargetX = routeX, questTravelTargetY = routeY, arbiterActivity = activity,
            x, y = 0d, liveTraceId = live.HasValue ? Trace(live.Value) : (Guid?)null,
            liveRequestedAtUtc = live.HasValue ? Start.AddSeconds(liveRequested) : (DateTime?)null,
            liveAction = live.HasValue ? liveAction : null, liveState = live.HasValue ? "Running" : null,
            liveTargetId = live.HasValue ? liveTarget : (uint?)null,
            liveDecisionQuestId = live.HasValue ? (liveTarget == 0 ? chosen : liveTarget) : (uint?)null,
            liveDecisionGoal = live.HasValue ? liveAction == "Move" ? "quest.progress" : "quest.turn-in" : null,
            liveDecisionPolicy = live.HasValue ? "quest-v1" : null,
            liveDecisionCycleId = live.HasValue ? $"opaque-decision-{live}" : null,
            liveMoveOwner = live.HasValue ? "QUEST_TRAVEL" : null,
            liveDestinationX = routeX, liveDestinationY = routeY, liveDestinationZ = 0d,
            liveDetail = "irrelevant", currentTargetObjId = 99u, npcAlive = false, npcObjId = 0u,
        });
    // Supplied-data audit tails retain their previous rows, like the actual
    // bounded actor projection. This is fixture assembly, not an oracle.
    private static void Feed(BotShortRunEvidence proof, double t, JsonElement observation)
    {
        var prior = proof.LastObservation.HasValue
            ? BotShortRunEvidence.Array(proof.LastObservation.Value, "actorAudit") : [];
        var rows = prior.Concat(BotShortRunEvidence.Array(observation, "actorAudit"))
            .GroupBy(r => BotShortRunEvidence.Id(r, "trace_id")).Select(g => g.Last()).ToArray();
        var values = observation.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        values["actorAudit"] = JsonSerializer.SerializeToElement(rows);
        proof.Observe(t, JsonSerializer.SerializeToElement(values));
    }

    private static BotShortRunEvidence Accepted()
    {
        var proof = new BotShortRunEvidence(7, Observation(), protocolReady: true);
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()], audit: [Audit(1)]));
        return proof;
    }

    private static JsonElement Control(bool queued = true, int trace = 11, uint actor = 42)
        => JsonSerializer.SerializeToElement(new
        {
            trace_id = Trace(trace), actor_id = actor, action = "Interrupt", state = "Completed",
            requested_at = Start.AddSeconds(5.2), started_at = Start.AddSeconds(5.3002),
            completed_at = Start.AddSeconds(5.3003), result_payload = queued,
            audit = new { action = "Stop", detail = "presentation can change" },
        });

    private static void Declare(BotShortRunEvidence proof)
    {
        var observation = Observation(5, chosen: 1, quests: [Quest(1, "Progress")], live: 10);
        observation = Changed(observation, "objectiveItems", new[] { new { questId = 1u, actId = 501u,
            componentId = 401u, objectiveIndex = 0, itemId = 600u, required = 3 } });
        Feed(proof, 5, observation);
        var candidate = proof.GetInterruptCandidate(observation);
        Assert.NotNull(candidate);
        proof.DeclareInterrupt(candidate, Start.AddSeconds(5.1));
        proof.AcknowledgeInterrupt(Trace(11));
    }

    private static void Continue(BotShortRunEvidence proof, int victim = 10,
        string victimOwner = "QUEST_TRAVEL", string freshOwner = "QUEST_TRAVEL", bool freshMove = true)
    {
        Feed(proof, 6, Observation(6, chosen: 1, quests: [Quest()],
            audit: [Audit(victim, quest: 0, action: "Move", result: "Interrupted", requested: 4, completed: 5.3, owner: victimOwner)]));
        if (freshMove)
            Feed(proof, 8, Observation(8, chosen: 1, quests: [Quest()], live: 12, liveRequested: 7));
        Feed(proof, 10, Observation(10, flags: [Flag(completed: true), Flag(2)], audit:
        [
            Audit(12, quest: 0, action: "Move", result: "Interrupted", requested: 7, completed: 9, owner: freshOwner),
            Audit(2, action: "TurnInQuest", requested: 9.2, completed: 9.3),
        ]));
    }

    private static void NextChoice(BotShortRunEvidence proof)
        => Feed(proof, 12, Observation(12, chosen: 2, quests: [Quest(2)],
            flags: [Flag(completed: true), Flag(2)], audit: [Audit(3, quest: 2, requested: 10.5, completed: 11)]));

    [Fact]
    public void PendingActorJoinsWithoutRestartingDeadlineOrCreditingPreJoinWork()
    {
        var proof = new BotShortRunEvidence(7, Observation(actor: 0, actorCharacter: 0), protocolReady: true);
        Feed(proof, 2, Observation(2, chosen: 1, quests: [Quest()], audit: [Audit(1, requested: 1, completed: 1.5)]));
        Assert.Equal(42u, proof.ActorId);
        Assert.Equal(Start.AddSeconds(2), proof.JoinedAt);
        Assert.Empty(proof.QuestWork);
        Assert.Equal(0, proof.LastProvedProgressSeconds);
        Assert.Equal("NO_PROVED_PROGRESS", proof.StopReason(300));
        Feed(proof, 4, Observation(4, chosen: 2, quests: [Quest(), Quest(2)],
            audit: [Audit(2, quest: 2, requested: 2.5, completed: 3)]));
        Assert.Single(proof.QuestWork);
    }

    [Fact]
    public void PreJoinRequestedOwnTerminalInsideJoinCopyCannotBeHiddenByOwnershipFilter()
    {
        // Supplied records, not gameplay: request1 precedes JoinedAt2, but its
        // terminal2 intersects the own JOIN copy (BEGIN1.999, END2].
        var proof = new BotShortRunEvidence(7, Observation(actor: 0, actorCharacter: 0), protocolReady: true);
        Feed(proof, 2, Observation(2, chosen: 1, quests: [Quest()],
            audit: [Audit(1, requested: 1, completed: 2)]));
        Assert.Equal(42u, proof.ActorId);
        Assert.Equal(Start.AddSeconds(2), proof.JoinedAt);
        Assert.Empty(proof.QuestWork);
        Feed(proof, 4, Observation(4, chosen: 2, quests: [Quest(), Quest(2)],
            audit: [Audit(2, quest: 2, requested: 2.5, completed: 3)]));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.QuestWork);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void InstantAcceptanceBeforeFirstChosenPollUsesImmutableDecisionAndOwnStateEffect()
    {
        var proof = new BotShortRunEvidence(7, Observation(), protocolReady: true);
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()], audit: [Audit(1)]));
        Assert.Single(proof.QuestWork);
        Assert.False(proof.PurposeProved);
    }

    [Theory]
    [InlineData(false, "quest.accept")]
    [InlineData(true, "quest.turn-in")]
    public void MissingDecisionOrWrongGoalCannotProveAcceptance(bool metadata, string goal)
    {
        var proof = new BotShortRunEvidence(7, Observation());
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()],
            audit: [Audit(1, metadata: metadata, goal: goal)]));
        Assert.Empty(proof.QuestWork);
    }

    [Fact]
    public void ForeignPolicyCannotEarnTheBotsActualAcceptanceEffect()
    {
        var proof = new BotShortRunEvidence(7, Observation(), protocolReady: true);
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()],
            audit: [Audit(1, policy: "foreign-controller-policy")]));
        Assert.Empty(proof.QuestWork);
        Assert.Equal(0, proof.LastProvedProgressSeconds);
    }

    [Theory]
    [InlineData(99u, 1)]
    [InlineData(42u, -2)]
    public void ForeignOrPreStartRequestCannotEarnAStateChange(uint actor, double requested)
    {
        var proof = new BotShortRunEvidence(7, Observation());
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()], audit: [Audit(1, actor: actor, requested: requested)]));
        Assert.Empty(proof.QuestWork);
    }

    [Fact]
    public void OldCompletedRequestIsConsumedWithoutWaitingForLaterUnrelatedAcceptance()
    {
        var proof = new BotShortRunEvidence(7, Observation());
        var old = Audit(1);
        Feed(proof, 3, Observation(3, audit: [old]));
        Feed(proof, 6, Observation(6, chosen: 1, quests: [Quest()], audit: [old]));
        Assert.Empty(proof.QuestWork);
        Assert.Equal(0, proof.LastProvedProgressSeconds);
    }

    [Fact]
    public void OldTurnInCannotAcquireLaterUnrelatedCompletion()
    {
        var proof = Accepted();
        var old = Audit(2, action: "TurnInQuest", requested: 4, completed: 5);
        Feed(proof, 6, Observation(6, chosen: 1, quests: [Quest()], audit: [old]));
        Feed(proof, 9, Observation(9, flags: [Flag(completed: true)], audit: [old]));
        Assert.Empty(proof.CompletedGoals);
    }

    [Fact]
    public void TwoRequestsCompetingForOneIntervalEffectRemainUnattributed()
    {
        var proof = new BotShortRunEvidence(7, Observation());
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()],
            audit: [Audit(1), Audit(2, requested: 1.1, completed: 2.1)]));
        Assert.Empty(proof.QuestWork);
    }

    [Fact]
    public void LongerRequestUsesCapturedOwnBeforeState()
    {
        var proof = new BotShortRunEvidence(7, Observation(chosen: 1, quests: [Quest()]));
        Feed(proof, 2, Observation(2, chosen: 1, quests: [Quest()], live: 2, liveRequested: 1, liveAction: "TurnInQuest", liveTarget: 1));
        Feed(proof, 4, Observation(4, chosen: 1, quests: [Quest()], live: 2, liveRequested: 1, liveAction: "TurnInQuest", liveTarget: 1));
        Feed(proof, 6, Observation(6, flags: [Flag(completed: true)],
            audit: [Audit(2, action: "TurnInQuest", requested: 1, completed: 5)]));
        Assert.Single(proof.CompletedGoals);
    }

    [Fact]
    public void MissingPollCannotSupplyLongRequestBeforeState()
    {
        var proof = new BotShortRunEvidence(7, Observation(chosen: 1, quests: [Quest()]));
        Feed(proof, 2, Observation(2, chosen: 1, quests: [Quest()], live: 2, liveRequested: 1, liveAction: "TurnInQuest", liveTarget: 1));
        proof.BreakContinuity();
        Feed(proof, 4, Observation(4, chosen: 1, quests: [Quest()], live: 2, liveRequested: 1, liveAction: "TurnInQuest", liveTarget: 1));
        Feed(proof, 6, Observation(6, flags: [Flag(completed: true)],
            audit: [Audit(2, action: "TurnInQuest", requested: 1, completed: 5)]));
        Assert.Empty(proof.CompletedGoals);
    }

    [Theory]
    [InlineData(43u, 3)]
    [InlineData(42u, 4)]
    public void ActorOrGenerationChangeFailsClosed(uint actor, long generation)
    {
        var proof = Accepted();
        Feed(proof, 6, Observation(6, actor: actor, generation: generation, flags: [Flag(completed: true)],
            audit: [Audit(2, action: "TurnInQuest", requested: 4, completed: 5)]));
        Assert.Equal(1, proof.IdentityMismatches);
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.CompletedGoals);
    }

    [Fact]
    public void MissingCompletionFlagIsNotKnownFalse()
    {
        var proof = new BotShortRunEvidence(7, Observation(chosen: 1, quests: [Quest()], flags: []));
        Feed(proof, 3, Observation(3, flags: [Flag(completed: true)],
            audit: [Audit(2, action: "TurnInQuest")]));
        Assert.Empty(proof.CompletedGoals);
    }

    [Fact]
    public void HistoricalCompletionAndGlobalCountIncreaseRemainDiagnostic()
    {
        var proof = new BotShortRunEvidence(7, Observation(chosen: 1, quests: [Quest()],
            flags: [Flag(completed: true)], completedCount: 12));
        Feed(proof, 3, Observation(3, flags: [Flag(completed: true)], completedCount: 13,
            audit: [Audit(2, action: "TurnInQuest")]));
        Assert.Equal(12, proof.BaselineCompletedCount);
        Assert.Equal(13, proof.LastCompletedCount);
        Assert.Empty(proof.CompletedGoals);
    }

    [Fact]
    public void MovementDiagnosticCountsAndHeldRequestsDoNotExtendNoProgressBoundary()
    {
        var proof = Accepted();
        Feed(proof, 100, Observation(100, chosen: 1, quests: [Quest()], live: 10, liveRequested: 4,
            completedCount: 50, x: 20));
        Feed(proof, 200, Observation(200, chosen: 1, quests: [Quest()], live: 10, liveRequested: 4,
            completedCount: 100, x: 50));
        Assert.Equal(3, proof.LastProvedProgressSeconds);
        Assert.Null(proof.StopReason(302.999));
        Assert.Equal("NO_PROVED_PROGRESS", proof.StopReason(303));
        Assert.Equal(0, proof.RemainingSeconds(303));
        Assert.Equal("MAXIMUM_WINDOW", proof.StopReason(900));
    }

    [Fact]
    public void RoutineArrivalInterruptedAndLaterTurnInDoNotProveHandledFault()
    {
        var proof = Accepted();
        Feed(proof, 5, Observation(5, chosen: 1, quests: [Quest()], live: 10));
        Continue(proof);
        Assert.Single(proof.CompletedGoals);
        Assert.Empty(proof.Recoveries);
    }

    [Fact]
    public void CompletionFlagWithoutQuestRemovalDoesNotProveTerminalGoal()
    {
        var proof = Accepted();
        Feed(proof, 6, Observation(6, chosen: 1, quests: [Quest()], flags: [Flag(completed: true)],
            audit: [Audit(2, action: "TurnInQuest", requested: 4, completed: 5)]));
        Assert.Empty(proof.CompletedGoals);
    }

    [Fact]
    public void ALongRequestCannotChangeItsActionIdentityAtCompletion()
    {
        var proof = new BotShortRunEvidence(7, Observation(chosen: 1, quests: [Quest()]));
        Feed(proof, 2, Observation(2, chosen: 1, quests: [Quest()], live: 2, liveRequested: 1));
        Feed(proof, 4, Observation(4, chosen: 1, quests: [Quest()], live: 2, liveRequested: 1));
        Feed(proof, 6, Observation(6, flags: [Flag(completed: true)],
            audit: [Audit(2, action: "TurnInQuest", requested: 1, completed: 5)]));
        Assert.Empty(proof.CompletedGoals);
    }

    [Theory]
    [InlineData(99, "QUEST_TRAVEL", "QUEST_TRAVEL", true)]
    [InlineData(10, "ROAM", "QUEST_TRAVEL", true)]
    [InlineData(10, "QUEST_TRAVEL", "ROAM", true)]
    [InlineData(10, "QUEST_TRAVEL", "QUEST_TRAVEL", false)]
    public void WrongVictimOwnerOrMissingFreshRunningMoveCannotJoin(int victim, string victimOwner, string freshOwner, bool freshMove)
    {
        var proof = Accepted();
        Declare(proof);
        proof.ObserveControl(Control());
        Continue(proof, victim, victimOwner, freshOwner, freshMove);
        NextChoice(proof);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Theory]
    [InlineData(false, 11, 42u)]
    [InlineData(true, 99, 42u)]
    [InlineData(true, 11, 99u)]
    public void FalseOrForeignControlCannotEarnRecovery(bool queued, int trace, uint actor)
    {
        var proof = Accepted();
        Declare(proof);
        proof.ObserveControl(Control(queued, trace, actor));
        Continue(proof);
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.Recoveries);
    }

    [Fact]
    public void AcknowledgmentWithoutTrueTerminalQueueResultCannotEarnRecovery()
    {
        var proof = Accepted();
        Declare(proof);
        Continue(proof);
        Assert.Empty(proof.Recoveries);
    }


    [Fact]
    public void OneDeclarationCannotBeRetriedForASecondVictim()
    {
        var proof = Accepted();
        Declare(proof);
        var current = Observation(6, chosen: 1, quests: [Quest()], live: 99, liveRequested: 5.5);
        Feed(proof, 6, current);
        Assert.Null(proof.GetInterruptCandidate(current));
        Assert.Throws<InvalidOperationException>(() => proof.DeclareInterrupt(
            new(1, Trace(99), Start.AddSeconds(5.5), Start.AddSeconds(6), 100, 0), Start.AddSeconds(6.1)));
    }

    [Fact]
    public void OwnDeathRequiresFreshMeaningfulSamePurposeConsequence()
    {
        var proof = Accepted();
        Feed(proof, 4, Observation(4, chosen: 1, quests: [Quest()], dead: true));
        Feed(proof, 6, Observation(6, chosen: 1, quests: [Quest()],
            audit: [Audit(90, quest: 0, action: "DiscoverSelfQuests", result: "Rejected", requested: 5, completed: 5.1)]));
        Assert.Empty(proof.Recoveries);
        Feed(proof, 9, Observation(9, flags: [Flag(completed: true)],
            audit: [Audit(2, action: "TurnInQuest", requested: 7, completed: 8)]));
        Assert.Single(proof.Recoveries);
    }

    [Fact]
    public void EarlyDifferentWorkCannotBecomeSubsequentChoiceWhenACompletionLaterArrives()
    {
        var proof = Accepted();
        Feed(proof, 4, Observation(4, chosen: 2, quests: [Quest(), Quest(2)],
            audit: [Audit(3, quest: 2, requested: 3.1, completed: 3.2)]));
        Feed(proof, 6, Observation(6, chosen: 1, quests: [Quest(), Quest(2)], dead: true));
        Feed(proof, 9, Observation(9, chosen: 2, quests: [Quest(2)], flags: [Flag(completed: true), Flag(2)],
            audit: [Audit(2, action: "TurnInQuest", requested: 7, completed: 8)]));
        Assert.Empty(proof.SubsequentChoices);
    }

    [Fact]
    public void AuditResourceOverflowCannotBecomeACompletionPass()
    {
        var proof = Accepted();
        Feed(proof, 6, Observation(6, flags: [Flag(completed: true)], audit:
            Enumerable.Range(1, BotShortRunEvidence.MaximumAuditRows + 1)
                .Select(id => Audit(id + 100, action: "TurnInQuest", requested: 4, completed: 5)).ToArray()));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.CompletedGoals);
    }

    [Fact]
    public void WitnessMemoryIsBoundedWithoutSilentlyDiscardingCausalProof()
    {
        var proof = new BotShortRunEvidence(7, Observation());
        for (var id = 1; id <= BotShortRunEvidence.MaximumWorkWitnesses + 1; id++)
        {
            var at = id * 3d;
            Feed(proof, at, Observation(at, chosen: (uint)id, quests: [Quest((uint)id)],
                audit: [Audit(id, quest: (uint)id, requested: at - 2, completed: at - 1)]));
        }
        Assert.Equal(BotShortRunEvidence.MaximumWorkWitnesses, proof.QuestWork.Count);
        Assert.NotNull(proof.EvidenceFault);
    }

    // Explicitly synthetic passive payloads. These test the consumer, not
    // quest choice, grant mechanics, natural opportunity or live gameplay.
    private static JsonElement PurposeAudit(int trace, string action = "Loot", uint target = 99,
        uint? parent = 251, string cycle = "opaque-loot-decision", double requested = 1,
        double completed = 2, bool includeParent = true)
    {
        var values = JsonSerializer.SerializeToElement(Audit(trace, quest: target, action: action,
            goal: action switch { "Loot" => "quest.objective-loot", "Move" => "quest.progress",
                "AdvanceQuest" => "quest.advance", "TurnInQuest" or "TurnInDoodad" or "AutoTurnIn" => "quest.turn-in", _ => "quest.accept" },
            cycle: cycle, requested: requested, completed: completed))
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        if (includeParent) values["decision_quest_id"] = JsonSerializer.SerializeToElement(parent);
        else values.Remove("decision_quest_id");
        return JsonSerializer.SerializeToElement(values);
    }

    private static JsonElement PurposeObservation(double at = 0, int objective = 0, long items = 0,
        object[]? audit = null, uint chosen = 251, int? live = null, double liveRequested = 4)
    {
        var values = Observation(at, chosen: chosen,
            quests: [new { questId = 251u, status = "Progress", step = "Progress", objectives = new[] { objective, 0, 0, 0, 0 } }],
            flags: [Flag(251)], audit: audit, live: live, liveRequested: liveRequested).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        values["snapshotStartedAtUtc"] = JsonSerializer.SerializeToElement(Start.AddSeconds(at - 0.001));
        values["items"] = JsonSerializer.SerializeToElement(new[] { new { template = 4058u, count = items } });
        values["objectiveItems"] = JsonSerializer.SerializeToElement(new[]
        {
            new { questId = 251u, actId = 10473u, componentId = 633u, objectiveIndex = 0, itemId = 4058u, required = 3 }
        });
        return JsonSerializer.SerializeToElement(values);
    }

    [Fact]
    public void QuestLootNumericAndItemProgressUsesRequestParentNotCorpseTargetOrCycleFormat()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, PurposeObservation(3, objective: 1, items: 1,
            audit: [PurposeAudit(101)]));
        var work = Assert.Single(proof.QuestWork);
        Assert.Equal(251u, work.QuestId);
        Assert.Equal("Loot", work.Action);
        Assert.Equal(Trace(101), work.TraceId);
        Assert.Equal("opaque-loot-decision", work.Cycle);
        Assert.Empty(proof.CompletedGoals);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData(2u, true)]
    public void ActualAcceptanceEffectCannotInventMissingOrMismatchedRequestParent(uint? parent, bool includeParent)
    {
        var proof = new BotShortRunEvidence(7, Observation());
        var acceptedCycle = JsonSerializer.SerializeToElement(Audit(102))
            .GetProperty("decision_cycle_id").GetString()!;
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()],
            audit: [PurposeAudit(102, action: "AcceptQuest", target: 1, parent: parent,
                cycle: acceptedCycle, includeParent: includeParent)]));
        Assert.Empty(proof.QuestWork);
        Assert.Equal(0, proof.ProvedProgressVersion);
    }

    [Fact]
    public void SelectedAcceptanceCycleIsOpaqueRatherThanParsedAsCharacterAndTicks()
    {
        var proof = new BotShortRunEvidence(7, Observation());
        Feed(proof, 3, Observation(3, chosen: 1, quests: [Quest()],
            audit: [PurposeAudit(103, action: "AcceptQuest", target: 1, parent: 1,
                cycle: "independent-decision-A")]));
        Assert.Equal("independent-decision-A", Assert.Single(proof.QuestWork).Cycle);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 0)]
    public void QuestLootRequiresBothOwnNumericObjectiveAndItemIncrease(int objective, long itemCount)
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, PurposeObservation(3, objective: objective, items: itemCount,
            audit: [PurposeAudit(104)]));
        Assert.Empty(proof.QuestWork);
        Assert.Equal(0, proof.ProvedProgressVersion);
    }

    [Fact]
    public void AnOldLootRequestCannotWaitForLaterUnrelatedProgress()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        var row = PurposeAudit(105);
        Feed(proof, 3, PurposeObservation(3, audit: [row]));
        Feed(proof, 6, PurposeObservation(6, objective: 1, items: 1, audit: [row]));
        Assert.Empty(proof.QuestWork);
    }

    [Fact]
    public void CompetingLootRequestsCannotShareOneObservedObjectiveAndItemGain()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, PurposeObservation(3, objective: 1, items: 1, audit:
        [
            PurposeAudit(106, target: 99),
            PurposeAudit(107, target: 100, cycle: "different-loot-decision", requested: 1.2, completed: 2.2),
        ]));
        Assert.Empty(proof.QuestWork);
    }

    [Fact]
    public void ChangedMutableChoiceDoesNotRelabelTheLootRequestsOwnObjective()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, PurposeObservation(3, objective: 1, items: 1, chosen: 2,
            audit: [PurposeAudit(108)]));
        Assert.Equal(251u, Assert.Single(proof.QuestWork).QuestId);
    }
    [Fact]
    public void LongRunningLootUsesItsCapturedBeforeStateAndImmutableTerminalTuple()
    {
        JsonElement Running(double at)
        {
            var row = PurposeObservation(at, live: 220, liveRequested: 2);
            row = Changed(Changed(row, "liveAction", "Loot"), "liveTargetId", 99u);
            row = Changed(Changed(row, "liveDecisionGoal", "quest.objective-loot"), "liveDecisionQuestId", 251u);
            return Changed(row, "liveDecisionCycleId", "long-opaque-loot-context");
        }
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, Running(3));
        Feed(proof, 6, Running(6));
        Feed(proof, 9, PurposeObservation(9, objective: 1, items: 1,
            audit: [PurposeAudit(220, cycle: "long-opaque-loot-context", requested: 2, completed: 8)]));
        Assert.Equal(251u, Assert.Single(proof.QuestWork).QuestId);
        Assert.Null(proof.EvidenceFault);
    }
    [Theory]
    [InlineData(251u, "Ready", false, true)]
    [InlineData(601u, "Progress", true, true)]
    [InlineData(601u, "Progress", false, false)]
    [InlineData(6198u, "Ready", false, false)]
    public void InterruptRequiresActiveProgressPhaseAndValidatedOwnMapping(uint quest, string phase, bool eligible, bool hasMapping)
    {
        // Explicit supplied mappings/records, not deployed template or natural
        // opportunity claims. A numeric Ready counter is not a Progress objective.
        var item = quest == 251 ? 4058u : 8123u;
        var act = quest == 251 ? 10473u : 9024u;
        var component = quest == 251 ? 633u : 1204u;
        JsonElement Frame(double at, int objective = 0, long items = 0, int? live = null,
            double requested = 4, object[]? audit = null, bool loot = false)
        {
            var frame = PurposeObservation(at, objective: objective, items: items, chosen: quest,
                live: live, liveRequested: requested, audit: audit);
            frame = Changed(frame, "activeQuests", new[] { new { questId = quest, status = phase, step = phase,
                objectives = new[] { objective, 0, 0, 0, 0 } } });
            frame = Changed(frame, "questCompletion", new[] { Flag(quest) });
            frame = Changed(frame, "items", new[] { new { template = item, count = items } });
            object[] mappings = hasMapping
                ? [new { questId = quest, actId = act, componentId = component, objectiveIndex = 0, itemId = item, required = 3 }]
                : [];
            frame = Changed(frame, "objectiveItems", mappings);
            if (loot)
            {
                frame = Changed(Changed(frame, "liveAction", "Loot"), "liveTargetId", 99u);
                frame = Changed(frame, "liveDecisionGoal", "quest.objective-loot");
            }
            return frame;
        }
        var proof = new BotShortRunEvidence(7, Frame(0), protocolReady: true);
        var victim = Frame(5, live: 10);
        Feed(proof, 5, victim);
        var candidate = proof.GetInterruptCandidate(victim);
        if (!eligible)
        {
            Assert.Null(candidate);
            Assert.Empty(proof.QuestWork);
            Assert.Empty(proof.Recoveries);
            Assert.False(proof.PurposeProved);
            return;
        }
        Assert.NotNull(candidate);
        proof.DeclareInterrupt(candidate, Start.AddSeconds(5.1));
        proof.AcknowledgeInterrupt(Trace(11));
        proof.ObserveControl(Control());
        var interrupted = Changed(PurposeAudit(10, action: "Move", target: 0, parent: quest,
            cycle: "opaque-decision-10", requested: 4, completed: 5.3), "result", "Interrupted");
        Feed(proof, 6, Frame(6, audit: [interrupted]));
        Feed(proof, 8, Frame(8, live: 12, requested: 7));
        var route = PurposeAudit(12, action: "Move", target: 0, parent: quest,
            cycle: "opaque-decision-12", requested: 7, completed: 9);
        Feed(proof, 10, Frame(10, live: 232, requested: 9.2, audit: [route], loot: true));
        Feed(proof, 12, Frame(12, objective: 1, items: 1,
            audit: [PurposeAudit(232, parent: quest, cycle: "opaque-decision-232", requested: 9.2, completed: 11.5)]));
        Assert.True(proof.PurposeProved);
        Assert.Equal(quest, Assert.Single(proof.QuestWork).QuestId);
        Assert.Equal("Loot", proof.QuestWork[0].Action);
        Assert.Equal(Trace(11), Assert.Single(proof.Recoveries).ControlTraceId);
        Assert.Equal(Trace(10), proof.Recoveries[0].VictimTraceId);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ProgressInterruptEligibilityDoesNotInventAnUnfilledCounterRequirement(int counter)
    {
        // Supplied Progress state/mapping only; not natural-opportunity evidence.
        JsonElement Frame(double at, int? live = null)
        {
            var frame = PurposeObservation(at, chosen: 601, live: live);
            frame = Changed(frame, "activeQuests", new[] { new { questId = 601u, status = "Progress",
                step = "Progress", objectives = new[] { counter, 0, 0, 0, 0 } } });
            frame = Changed(frame, "questCompletion", new[] { Flag(601) });
            frame = Changed(frame, "items", new[] { new { template = 8123u, count = counter } });
            return Changed(frame, "objectiveItems", new[] { new { questId = 601u, actId = 9024u,
                componentId = 1204u, objectiveIndex = 0, itemId = 8123u, required = 3 } });
        }
        var proof = new BotShortRunEvidence(7, Frame(0), protocolReady: true);
        var running = Frame(5, live: 10);
        Feed(proof, 5, running);
        Assert.NotNull(proof.GetInterruptCandidate(running));
    }


    [Theory]
    [InlineData(0, 633, 3)]
    [InlineData(10473, 0, 3)]
    [InlineData(10473, 633, 0)]
    [InlineData(10474, 633, 3)]
    public void CanonicalObjectiveMappingMustBeActualNumericAndUnambiguous(int act, int component, int required)
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        var after = Changed(PurposeObservation(3, objective: 1, items: 1, audit: [PurposeAudit(221)]),
            "objectiveItems", new[] { new { questId = 251u, actId = act, componentId = component,
                objectiveIndex = 0, itemId = 4058u, required } });
        Feed(proof, 3, after);
        Assert.Empty(proof.QuestWork);
    }


    private static JsonElement Changed(JsonElement value, string field, object? replacement)
    {
        var values = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        values[field] = JsonSerializer.SerializeToElement(replacement);
        return JsonSerializer.SerializeToElement(values);
    }

    private static JsonElement PursuitObservation(double seconds, int live = 10, double requested = 4,
        int objective = 0, int items = 0, object[]? audit = null, uint target = 44293)
    {
        var value = PurposeObservation(seconds, objective: objective, items: items,
            live: live, liveRequested: requested, audit: audit ?? []);
        value = Changed(value, "liveMoveOwner", "PURSUIT_MOVE_TO_UNIT");
        value = Changed(value, "liveDecisionGoal", "quest.objective-pursuit");
        value = Changed(value, "liveTargetId", target);
        foreach (var field in new[] { "liveDestinationX", "liveDestinationY", "liveDestinationZ" })
            value = Changed(value, field, null);
        return value;
    }

    [Fact]
    public void ActualShapedNpcPursuitCanBeInterruptedWithoutInventingDestinationCoordinates()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        var running = PursuitObservation(5);
        Feed(proof, 5, running);
        var candidate = proof.GetInterruptCandidate(running);
        Assert.NotNull(candidate);
        Assert.Equal(251u, candidate.QuestId);
        Assert.Equal(Trace(10), candidate.VictimTraceId);
    }

    private static JsonElement PursuitAudit(int trace, double requested, double completed,
        string result = "Completed", uint target = 44293)
    {
        var row = PurposeAudit(trace, action: "Move", target: target,
            cycle: $"opaque-decision-{trace}", requested: requested, completed: completed);
        row = Changed(row, "move_owner", "PURSUIT_MOVE_TO_UNIT");
        row = Changed(row, "decision_goal", "quest.objective-pursuit");
        return Changed(row, "result", result);
    }

    [Fact]
    public void ExactPursuitInterruptionFreshPursuitAndOrdinaryLootProveRecovery()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        var running = PursuitObservation(5);
        Feed(proof, 5, running);
        var candidate = Assert.IsType<BotShortRunEvidence.InterruptCandidate>(proof.GetInterruptCandidate(running));
        Assert.Null(candidate.RouteX);
        Assert.Null(candidate.RouteY);
        Assert.Equal(44293u, candidate.TargetId);
        proof.DeclareInterrupt(candidate, Start.AddSeconds(5.1));
        proof.AcknowledgeInterrupt(Trace(11));
        proof.ObserveControl(Control());
        Feed(proof, 6, PurposeObservation(6, audit: [PursuitAudit(10, 4, 5.3, "Interrupted")]));
        Feed(proof, 8, PursuitObservation(8, live: 12, requested: 7));
        Assert.False(proof.PurposeProved); // Recovery movement gives no quest credit.
        Feed(proof, 10, PurposeObservation(10, objective: 1, items: 1,
            audit: [PursuitAudit(12, 7, 9), PurposeAudit(201, requested: 9.2, completed: 9.3)]));
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.PurposeProved);
        Assert.Equal(251u, Assert.Single(proof.Recoveries).QuestId);
        Assert.Equal("Loot", Assert.Single(proof.QuestWork).Action);
    }

    private static BotShortRunEvidence InterruptedPursuit()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        var running = PursuitObservation(5);
        Feed(proof, 5, running);
        var candidate = Assert.IsType<BotShortRunEvidence.InterruptCandidate>(proof.GetInterruptCandidate(running));
        proof.DeclareInterrupt(candidate, Start.AddSeconds(5.1));
        proof.AcknowledgeInterrupt(Trace(11));
        proof.ObserveControl(Control());
        Feed(proof, 6, PurposeObservation(6, audit: [PursuitAudit(10, 4, 5.3, "Interrupted")]));
        return proof;
    }

    private static void TimedOutResponse(BotShortRunEvidence proof, int trace, double requested,
        string? failure = "Navigation")
    {
        Feed(proof, requested + 1, PursuitObservation(requested + 1, live: trace, requested: requested));
        var terminal = Changed(PursuitAudit(trace, requested, requested + 2, "TimedOut"), "failure", failure);
        Feed(proof, requested + 3, PurposeObservation(requested + 3, audit: [terminal]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedResumedPursuitNeedsDistinctResponseAndOrdinaryLoot(int failedResponses)
    {
        var proof = InterruptedPursuit();
        for (var n = 0; n < failedResponses; n++)
            TimedOutResponse(proof, 12 + n, 7 + n * 4);
        Assert.False(proof.PurposeProved);
        Assert.Empty(proof.Recoveries); // Failed movement is not progress.
        var requested = 7 + failedResponses * 4;
        var response = 12 + failedResponses;
        Feed(proof, requested + 1, PursuitObservation(requested + 1, live: response, requested: requested, target: 44294));
        Feed(proof, requested + 3, PurposeObservation(requested + 3, objective: 1, items: 1,
            audit: [PursuitAudit(response, requested, requested + 2, target: 44294),
                PurposeAudit(301, requested: requested + 2.2, completed: requested + 2.3)]));
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.PurposeProved);
        Assert.Equal(Trace(response), Assert.Single(proof.Recoveries).ResponseTraceId);
        Assert.Equal("Loot", Assert.Single(proof.QuestWork).Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Starvation")]
    public void FailedResponseRequiresAuthoritativeNavigationFailure(string? failure)
    {
        var proof = InterruptedPursuit();
        TimedOutResponse(proof, 12, 7, failure);
        Feed(proof, 12, PursuitObservation(12, live: 13, requested: 11));
        Feed(proof, 14, PurposeObservation(14, objective: 1, items: 1,
            audit: [PursuitAudit(13, 11, 13), PurposeAudit(302, requested: 13.2, completed: 13.3)]));
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void FailedResponseCannotBorrowUnobservedLaterLootAsItsReplacement()
    {
        var proof = InterruptedPursuit();
        TimedOutResponse(proof, 12, 7);
        var after = PurposeObservation(12, objective: 1, items: 1,
            audit: [PurposeAudit(303, requested: 11, completed: 11.3)]);
        after = Changed(Changed(after, "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 12, after);
        Assert.Single(proof.QuestWork); // Real progress, no verified replacement response.
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("cycle")]
    [InlineData("before-retirement")]
    [InlineData("other-quest")]
    [InlineData("other-owner")]
    public void FailedResponseReplacementCannotReuseOrBorrowPurpose(string invalid)
    {
        var proof = InterruptedPursuit();
        TimedOutResponse(proof, 12, 7);
        var trace = invalid == "guid" ? 12 : 13;
        var requested = invalid == "before-retirement" ? 8.5 : 11;
        var replacement = PursuitObservation(12, live: trace, requested: requested);
        replacement = invalid switch
        {
            "cycle" => Changed(replacement, "liveDecisionCycleId", "opaque-decision-12"),
            "other-quest" => Changed(Changed(replacement, "liveDecisionQuestId", 2u), "chosenQuestId", 2u),
            "other-owner" => Changed(replacement, "liveMoveOwner", "OTHER_MOVE"),
            _ => replacement,
        };
        Feed(proof, 12, replacement);
        Feed(proof, 14, PurposeObservation(14, objective: 1, items: 1,
            audit: [PurposeAudit(304, requested: 13, completed: 13.3)]));
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void FailedResponseChainStopsAtItsDeclaredBound()
    {
        var proof = InterruptedPursuit();
        for (var n = 0; n < BotShortRunEvidence.MaximumRecoveryResponses; n++)
            TimedOutResponse(proof, 12 + n, 7 + n * 4);
        Feed(proof, 20, PursuitObservation(20, live: 15, requested: 19));
        Feed(proof, 22, PurposeObservation(22, objective: 1, items: 1,
            audit: [PursuitAudit(15, 19, 21), PurposeAudit(305, requested: 21.2, completed: 21.3)]));
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void FailedResponseChainLosesProofOnSnapshotDiscontinuity()
    {
        var proof = InterruptedPursuit();
        TimedOutResponse(proof, 12, 7);
        proof.BreakContinuity();
        Feed(proof, 12, PursuitObservation(12, live: 13, requested: 11));
        Feed(proof, 14, PurposeObservation(14, objective: 1, items: 1,
            audit: [PursuitAudit(13, 11, 13), PurposeAudit(306, requested: 13.2, completed: 13.3)]));
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void RetiredResponseCannotResumeItsOriginalRunningTupleBeforeLaterRecovery()
    {
        var proof = InterruptedPursuit();
        TimedOutResponse(proof, 12, 7);
        Feed(proof, 11, PursuitObservation(11, live: 12, requested: 7));
        Feed(proof, 13, PursuitObservation(13, live: 13, requested: 12));
        Feed(proof, 15, PurposeObservation(15, objective: 1, items: 1,
            audit: [PursuitAudit(13, 12, 14), PurposeAudit(307, requested: 14.2, completed: 14.3)]));
        Assert.Equal("Retired recovery response resumed", proof.EvidenceFault);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Theory]
    [InlineData("completed_at", null)]
    [InlineData("actor_id", 99u)]
    [InlineData("result", "Running")]
    [InlineData("requested_at", null)]
    public void MalformedRecoveryTerminalCannotRemainARunningWitness(string field, object? value)
    {
        var proof = InterruptedPursuit();
        Feed(proof, 8, PursuitObservation(8, live: 12, requested: 7));
        var terminal = Changed(Changed(PursuitAudit(12, 7, 9, "TimedOut"), "failure", "Navigation"), field, value);
        Feed(proof, 10, PurposeObservation(10, audit: [terminal]));
        Feed(proof, 12, PurposeObservation(12, objective: 1, items: 1,
            audit: [PurposeAudit(308, requested: 11, completed: 11.3)]));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void DuplicateRecoveryTerminalCannotRemainARunningWitness()
    {
        var proof = InterruptedPursuit();
        Feed(proof, 8, PursuitObservation(8, live: 12, requested: 7));
        var terminal = Changed(PursuitAudit(12, 7, 9, "TimedOut"), "failure", "Navigation");
        // Feed deliberately deduplicates fixture tails; preserve these two rows
        // explicitly to exercise the malformed server projection instead.
        var rows = BotShortRunEvidence.Array(proof.LastObservation!.Value, "actorAudit")
            .Concat(new[] { terminal, terminal }).ToArray();
        proof.Observe(10, Changed(PurposeObservation(10), "actorAudit", rows));
        Feed(proof, 12, PurposeObservation(12, objective: 1, items: 1,
            audit: [PurposeAudit(309, requested: 11, completed: 11.3)]));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Interrupted")]
    public void SuccessfulRecoveryTerminalCannotAlsoCarryAFailure(string result)
    {
        var proof = InterruptedPursuit();
        Feed(proof, 8, PursuitObservation(8, live: 12, requested: 7));
        var terminal = Changed(PursuitAudit(12, 7, 9, result), "failure", "Navigation");
        Feed(proof, 10, PurposeObservation(10, audit: [terminal]));
        Feed(proof, 12, PurposeObservation(12, objective: 1, items: 1,
            audit: [PurposeAudit(310, requested: 11, completed: 11.3)]));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void CapturedRecoveryChainReplayUsesOnlyPersistedObservations()
    {
        // Explicit offline opt-in: no provisioning, HTTP, scheduler or gameplay.
        var directory = Environment.GetEnvironmentVariable("BOT_RECOVERY_REPLAY_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
            Assert.Skip("BOT_RECOVERY_REPLAY_DIRECTORY is required for captured offline recovery replay.");
        var rawPath = Path.Combine(directory!, "live-run", "observations.jsonl");
        Assert.InRange(new FileInfo(rawPath).Length, 1, 64 * 1024 * 1024);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!, "live-report.json")));
        using var chain = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!, "actual-chain.json")));
        var start = report.RootElement.GetProperty("start");
        var controlEvents = chain.RootElement.GetProperty("control");
        var victim = Guid.Parse(controlEvents[0].GetProperty("request").GetProperty("body").GetProperty("traceId").GetString()!);
        var acknowledgment = controlEvents[0].GetProperty("output");
        var control = controlEvents[1].GetProperty("output");
        var controlRequested = BotShortRunEvidence.Time(control, "requested_at")!.Value;
        var controlObserved = BotShortRunEvidence.Time(controlEvents[1], "recordedAtUtc")!.Value;
        var started = BotShortRunEvidence.Time(start, "observedAtUtc")!.Value;
        var proof = new BotShortRunEvidence(BotShortRunEvidence.UInt(start, "id"), start, protocolReady: true);
        var declared = false;
        var controlDelivered = false;
        var observations = 0;
        foreach (var line in File.ReadLines(rawPath))
        {
            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            if (BotShortRunEvidence.String(entry, "phase") != "OBSERVE") continue;
            var request = entry.GetProperty("request");
            if (BotShortRunEvidence.String(request, "cmd") != "quest"
                || BotShortRunEvidence.String(request, "sub") != "observe") continue;
            Assert.InRange(++observations, 1, 2000);
            var observation = entry.GetProperty("output");
            var observed = BotShortRunEvidence.Time(observation, "observedAtUtc")!.Value;
            proof.Observe((observed - started).TotalSeconds, observation);
            if (!declared && BotShortRunEvidence.Id(observation, "liveTraceId") == victim)
            {
                var candidate = Assert.IsType<BotShortRunEvidence.InterruptCandidate>(proof.GetInterruptCandidate(observation));
                proof.DeclareInterrupt(candidate, controlRequested);
                proof.AcknowledgeInterrupt(BotShortRunEvidence.Id(acknowledgment, "trace_id")!.Value);
                declared = true;
            }
            if (declared && !controlDelivered && BotShortRunEvidence.Time(entry, "recordedAtUtc") >= controlObserved)
            {
                proof.ObserveControl(control);
                controlDelivered = true;
            }
        }
        Assert.True(declared && controlDelivered);
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.PurposeProved);
        var recovery = Assert.Single(proof.Recoveries);
        Assert.Equal(251u, recovery.QuestId);
        Assert.NotEqual(Guid.Parse("ff041409-2f17-4572-9d5f-6477e1e0e2af"), recovery.ResponseTraceId);
        Assert.Contains(proof.QuestWork, w => w.Action == "Loot" && w.QuestId == 251);
        var completed = Assert.Single(proof.CompletedGoals.Where(w => w.QuestId == 251));
        Assert.Contains(proof.QuestWork, w => w.Action == "AcceptQuest" && w.QuestId == 324
            && w.RequestedAt > completed.CompletedAt);
    }

    [Theory]
    [InlineData("liveMoveOwner", "OTHER_MOVE")]
    [InlineData("liveMoveOwner", "QUEST_TRAVEL")]
    [InlineData("liveDecisionGoal", "quest.progress")]
    [InlineData("liveDecisionPolicy", "other")]
    public void PursuitCannotBorrowAnotherMoveOwnerGoalOrPolicy(string field, string value)
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        var running = Changed(PursuitObservation(5), field, value);
        Feed(proof, 5, running);
        Assert.Null(proof.GetInterruptCandidate(running));
    }

    [Fact]
    public void PursuitRequiresObservedNonzeroTarget()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        var running = Changed(PursuitObservation(5), "liveTargetId", 0u);
        Feed(proof, 5, running);
        Assert.Null(proof.GetInterruptCandidate(running));
    }

    [Theory]
    [InlineData("liveMoveOwner", "OTHER_MOVE")]
    [InlineData("liveTargetId", 44294u)]
    [InlineData("liveDecisionGoal", "quest.progress")]
    public void RunningPursuitPurposeCannotChangeUnderSameRequest(string field, object value)
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        Feed(proof, 5, PursuitObservation(5));
        Feed(proof, 6, Changed(PursuitObservation(6), field, value));
        Assert.Equal("Running request purpose changed", proof.EvidenceFault);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void PursuitRecoveryCannotBorrowAnUnrelatedQuestResponse()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        var running = PursuitObservation(5);
        Feed(proof, 5, running);
        proof.DeclareInterrupt(proof.GetInterruptCandidate(running)!, Start.AddSeconds(5.1));
        proof.AcknowledgeInterrupt(Trace(11));
        proof.ObserveControl(Control());
        Feed(proof, 6, PurposeObservation(6, audit: [PursuitAudit(10, 4, 5.3, "Interrupted")]));
        var other = PurposeObservation(8, chosen: 2,
            audit: [PurposeAudit(214, action: "AcceptQuest", target: 2, parent: 2, requested: 7, completed: 7.5)]);
        other = Changed(other, "activeQuests", new object[]
        {
            new { questId = 251u, status = "Progress", step = "Progress", objectives = new[] { 0, 0, 0, 0, 0 } },
            Quest(2, "Progress")
        });
        other = Changed(Changed(other, "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 8, other);
        // A later root effect cannot use the other quest's response as its own.
        Feed(proof, 10, PurposeObservation(10, objective: 1, items: 1,
            audit: [PurposeAudit(201, requested: 9.2, completed: 9.3)]));
        Assert.Null(proof.EvidenceFault);
        Assert.Equal(2, proof.QuestWork.Count);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    private static BotShortRunEvidence InterruptedPurpose(string? terminalCycle = null, uint terminalParent = 251,
        bool rootLootBefore = false)
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation(), protocolReady: true);
        if (rootLootBefore)
            Feed(proof, 3, PurposeObservation(3, objective: 1, items: 1, audit: [PurposeAudit(213)]));
        var running = PurposeObservation(5, objective: rootLootBefore ? 1 : 0, items: rootLootBefore ? 1 : 0, live: 10);
        Feed(proof, 5, running);
        proof.DeclareInterrupt(proof.GetInterruptCandidate(running)!, Start.AddSeconds(5.1));
        proof.AcknowledgeInterrupt(Trace(11));
        proof.ObserveControl(Control());
        var terminal = PurposeAudit(10, action: "Move", target: 0, parent: terminalParent,
            cycle: terminalCycle ?? "opaque-decision-10", requested: 4, completed: 5.3);
        terminal = Changed(terminal, "result", "Interrupted");
        Feed(proof, 6, PurposeObservation(6, objective: rootLootBefore ? 1 : 0, items: rootLootBefore ? 1 : 0,
            audit: [terminal]));
        return proof;
    }

    [Fact]
    public void ImmutableFallbackInterruptFreshReselectionAndIndependentLootCycleProvePurpose()
    {
        var proof = InterruptedPurpose();
        Feed(proof, 8, PurposeObservation(8, live: 12, liveRequested: 7));
        var route = PurposeAudit(12, action: "Move", target: 0, cycle: "opaque-decision-12", requested: 7, completed: 9);
        Feed(proof, 10, PurposeObservation(10, objective: 1, items: 1,
            audit: [route, PurposeAudit(201, requested: 9.2, completed: 9.3)]));
        Assert.True(proof.PurposeProved);
        Assert.Equal("QUEST_PURPOSE_COMPLETE", proof.StopReason(10));
        var recovery = Assert.Single(proof.Recoveries);
        Assert.Equal(Trace(11), recovery.ControlTraceId);
        Assert.Equal(Trace(10), recovery.VictimTraceId);
        Assert.NotEqual(recovery.ControlTraceId, recovery.VictimTraceId);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreshRealQuestEffectRequiresExplicitRouteRetirementNotMissingFields(bool retired)
    {
        var proof = InterruptedPurpose();
        var observed = PurposeObservation(8, objective: 1, items: 1,
            audit: [PurposeAudit(211, requested: 7, completed: 7.5)]);
        observed = Changed(Changed(observed, "questTravelTargetX", retired ? null : 100d),
            "questTravelTargetY", retired ? null : 0d);
        Feed(proof, 8, observed);
        Assert.Equal(retired, proof.PurposeProved);
        if (retired) Assert.Equal(Trace(211), Assert.Single(proof.Recoveries).ResponseTraceId);
        var missing = JsonSerializer.SerializeToElement(observed.EnumerateObject()
            .Where(p => p.Name is not ("questTravelTargetX" or "questTravelTargetY")).ToDictionary(p => p.Name, p => p.Value));
        var other = InterruptedPurpose();
        Feed(other, 8, missing);
        Assert.False(other.PurposeProved);
    }

    [Fact]
    public void FreshMoveMayChooseAnotherQuestWithoutRelabelingRootLoot()
    {
        var proof = InterruptedPurpose();
        Feed(proof, 8, PurposeObservation(8, chosen: 2, live: 12, liveRequested: 7));
        var route = PurposeAudit(12, action: "Move", target: 0, parent: 2, cycle: "opaque-decision-12", requested: 7, completed: 9);
        Feed(proof, 10, PurposeObservation(10, chosen: 2, objective: 1, items: 1,
            audit: [route, PurposeAudit(212, requested: 9.2, completed: 9.3)]));
        Assert.True(proof.PurposeProved);
        Assert.Equal(251u, Assert.Single(proof.QuestWork).QuestId);
    }

    [Theory]
    [InlineData("AcceptQuest", true)]
    [InlineData("AcceptQuest", false)]
    [InlineData("AdvanceQuest", true)]
    [InlineData("AdvanceQuest", false)]
    public void EarlierRootLootAndLaterRequestRequireAppliedOrdinaryStateEffect(string action, bool applied)
    {
        var proof = InterruptedPurpose(rootLootBefore: true);
        // The opaque token may legitimately be shared by direct requests;
        // only fresh rearmed Move contexts have the arm-retirement discriminator.
        var target = action == "AcceptQuest" ? 2u : 251u;
        var response = PurposeAudit(214, action: action, target: target, parent: target,
            cycle: "opaque-decision-10", requested: 7, completed: 7.5);
        var after = PurposeObservation(8, objective: 1, items: 1, audit: [response], chosen: target);
        if (applied)
            after = Changed(after, "activeQuests", action == "AcceptQuest"
                ? new object[] { new { questId = 251u, status = "Progress", step = "Progress", objectives = new[] { 1, 0, 0, 0, 0 } }, Quest(2, "Progress") }
                : new object[] { new { questId = 251u, status = "Ready", step = "Ready", objectives = new[] { 1, 0, 0, 0, 0 } } });
        after = Changed(Changed(after, "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 8, after);
        Assert.Equal(applied, proof.PurposeProved);
        Assert.Equal(applied ? 2 : 1, proof.QuestWork.Count);
        Assert.Equal("Loot", proof.QuestWork[0].Action);
        if (applied) Assert.Equal(Trace(214), Assert.Single(proof.Recoveries).ResponseTraceId);
        else Assert.Empty(proof.Recoveries); // Completed/accepted no-op is not applied progress.
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherOwnedEffectCannotRelabelNoopAdvanceAsRecovery(bool carriedInterval)
    {
        var proof = InterruptedPurpose(rootLootBefore: true);
        var rootLoot = Assert.Single(proof.QuestWork);
        Assert.Equal("Loot", rootLoot.Action);
        Assert.Equal(251u, rootLoot.QuestId);
        Assert.Equal(Trace(11), proof.ControlTraceId);
        Assert.True(proof.ControlCompleted);
        Assert.Null(proof.EvidenceFault);

        // Supplied counterexample, not observed gameplay: ordinary Interact
        // changes the phase before the fresh Advance is requested. Completed
        // Advance is then a no-op; the aggregate phase delta is not its effect.
        var interact = Changed(JsonSerializer.SerializeToElement(Audit(230, quest: 900, action: "Interact",
            goal: "quest.objective-gather", cycle: "ordinary-effect-context", requested: 7, completed: 7.2)),
            "decision_quest_id", 251u);
        var advance = PurposeAudit(231, action: "AdvanceQuest", target: 251, parent: 251,
            cycle: "fresh-noop-advance-context", requested: 7.3, completed: carriedInterval ? 9.5 : 7.5);
        var changedPhase = new[] { new { questId = 251u, status = "Ready", step = "Ready",
            objectives = new[] { 1, 0, 0, 0, 0 } } };
        if (carriedInterval)
        {
            var running = PurposeObservation(8, objective: 1, items: 1, audit: [interact], live: 231, liveRequested: 7.3);
            running = Changed(Changed(running, "liveAction", "AdvanceQuest"), "liveTargetId", 251u);
            running = Changed(Changed(running, "liveDecisionGoal", "quest.advance"), "liveDecisionQuestId", 251u);
            running = Changed(running, "liveDecisionCycleId", "fresh-noop-advance-context");
            running = Changed(running, "activeQuests", changedPhase);
            running = Changed(Changed(running, "questTravelTargetX", null), "questTravelTargetY", null);
            Feed(proof, 8, running);
        }
        var at = carriedInterval ? 10d : 8d;
        var after = PurposeObservation(at, objective: 1, items: 1,
            audit: carriedInterval ? new object[] { advance } : new object[] { interact, advance });
        after = Changed(after, "activeQuests", changedPhase);
        after = Changed(Changed(after, "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, at, after);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Fact]
    public void LateFirstVisibleLootWithinPreviousCopyCannotCreditLaterNoopLoot()
    {
        // Supplied copy-order counterexample, not gameplay or producer evidence:
        // A copies audit/items/counters at 8.0, L then runs 8.01→8.05 before
        // A's END8.2. B first exposes L and later no-effect N; L caused the gain.
        var proof = InterruptedPurpose();
        Feed(proof, 7, PurposeObservation(7, live: 12, liveRequested: 6.8));
        var route = PurposeAudit(12, action: "Move", target: 0, cycle: "opaque-decision-12",
            requested: 6.8, completed: 7.5);
        var a = Changed(PurposeObservation(8.2, audit: [route]), "snapshotStartedAtUtc", Start.AddSeconds(7.9));
        a = Changed(Changed(Changed(a, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 8.2, a);
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.ControlCompleted);
        Assert.Equal(Trace(11), proof.ControlTraceId);
        Assert.Empty(proof.QuestWork);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
        var copiedA = proof.LastObservation!.Value;
        Assert.Equal(Start.AddSeconds(7.9), BotShortRunEvidence.Time(copiedA, "snapshotStartedAtUtc"));
        Assert.Equal(Start.AddSeconds(8.2), BotShortRunEvidence.Time(copiedA, "observedAtUtc"));
        Assert.Equal(Trace(12), BotShortRunEvidence.Id(BotShortRunEvidence.Array(copiedA, "actorAudit").Last(), "trace_id"));
        Assert.Equal(0, copiedA.GetProperty("activeQuests")[0].GetProperty("objectives")[0].GetInt32());
        Assert.Equal(0, copiedA.GetProperty("items")[0].GetProperty("count").GetInt64());

        var late = PurposeAudit(240, cycle: "late-own-loot-context", requested: 8.01, completed: 8.05);
        var noop = PurposeAudit(241, cycle: "later-no-effect-loot-context", requested: 8.3, completed: 9.5);
        var b = Changed(PurposeObservation(10, objective: 1, items: 1, audit: [route, late, noop]),
            "snapshotStartedAtUtc", Start.AddSeconds(9.9));
        b = Changed(Changed(Changed(b, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        // Inclusive actual previous tail12, not a fabricated replacement tail.
        proof.Observe(10, b);
        Assert.Empty(proof.QuestWork);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
        Assert.NotNull(proof.EvidenceFault);
    }

    [Fact]
    public void TiedDistinctTerminalGuidsCreditOnlyTheWhollyBetweenCopiesLoot()
    {
        // Supplied serial actor timeline: Move12 has already completed; actual
        // effect Loot L completes9.5, then readonly Target T starts/completes9.5.
        // Equal completion timestamps are not ambiguity by themselves.
        var proof = InterruptedPurpose();
        Feed(proof, 7, PurposeObservation(7, live: 12, liveRequested: 6.8));
        var route = PurposeAudit(12, action: "Move", target: 0, cycle: "opaque-decision-12",
            requested: 6.8, completed: 7.5);
        var a = Changed(PurposeObservation(8.2, audit: [route]), "snapshotStartedAtUtc", Start.AddSeconds(7.9));
        a = Changed(Changed(Changed(a, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 8.2, a);
        Assert.Null(proof.EvidenceFault);
        Assert.True(proof.ControlCompleted);
        Assert.Equal(Trace(11), proof.ControlTraceId);
        Assert.Empty(proof.QuestWork);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
        Assert.Equal(Trace(12), BotShortRunEvidence.Id(BotShortRunEvidence.Array(proof.LastObservation!.Value, "actorAudit").Last(), "trace_id"));

        var loot = PurposeAudit(240, cycle: "valid-own-loot-context", requested: 8.3, completed: 9.5);
        var target = Changed(JsonSerializer.SerializeToElement(Audit(242, quest: 99, action: "Target",
            metadata: false, owner: "NONE", requested: 9.5, completed: 9.5)), "decision_quest_id", null);
        Assert.NotEqual(BotShortRunEvidence.Id(loot, "trace_id"), BotShortRunEvidence.Id(target, "trace_id"));
        Assert.Equal(BotShortRunEvidence.Time(loot, "completed_at"), BotShortRunEvidence.Time(target, "completed_at"));
        var b = Changed(PurposeObservation(10, objective: 1, items: 1, audit: [route, loot, target]),
            "snapshotStartedAtUtc", Start.AddSeconds(9.9));
        b = Changed(Changed(Changed(b, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        proof.Observe(10, b);
        Assert.Null(proof.EvidenceFault);
        var work = Assert.Single(proof.QuestWork);
        Assert.Equal(Trace(240), work.TraceId);
        Assert.Equal(251u, work.QuestId);
        Assert.Equal("Loot", work.Action);
        var recovery = Assert.Single(proof.Recoveries);
        Assert.Equal(Trace(11), recovery.ControlTraceId);
        Assert.Equal(Trace(10), recovery.VictimTraceId);
        Assert.Equal(Trace(12), recovery.ResponseTraceId);
        Assert.Equal(Trace(240), recovery.ConsequenceTraceId);
        Assert.True(proof.PurposeProved);
    }

    [Theory]
    [InlineData(true, "DiscoverQuests", "Rejected")]
    [InlineData(false, "Target", "Completed")]
    public void ReadonlyTerminalInsideCopiedIntervalDoesNotExcludeIndependentMappedLootPurpose(
        bool priorCopy, string action, string result)
    {
        // Supplied copy-order fixtures, not gameplay: A arrays are copied8.0.
        // Prior readonly discovery appears first in B; current readonly Target
        // follows the already-completed effect Loot. Neither can cause its gain.
        var proof = InterruptedPurpose();
        Feed(proof, 7, PurposeObservation(7, live: 12, liveRequested: 6.8));
        var route = PurposeAudit(12, action: "Move", target: 0, cycle: "opaque-decision-12",
            requested: 6.8, completed: 7.5);
        var a = Changed(PurposeObservation(8.2, audit: [route]), "snapshotStartedAtUtc", Start.AddSeconds(7.9));
        a = Changed(Changed(Changed(a, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 8.2, a);
        Assert.Null(proof.EvidenceFault);
        Assert.Equal(Trace(11), proof.ControlTraceId);
        Assert.True(proof.ControlCompleted);
        Assert.Empty(proof.QuestWork);
        Assert.Empty(proof.Recoveries);
        Assert.Equal(Trace(12), BotShortRunEvidence.Id(BotShortRunEvidence.Array(proof.LastObservation!.Value, "actorAudit").Last(), "trace_id"));

        var loot = PurposeAudit(240, cycle: "valid-own-loot-context", requested: 8.3, completed: 9.5);
        var readonlyRequest = Changed(JsonSerializer.SerializeToElement(Audit(243, quest: 0,
            action: action, result: result, metadata: false, owner: "NONE",
            requested: priorCopy ? 8.01 : 10.01, completed: priorCopy ? 8.05 : 10.05)), "decision_quest_id", null);
        object[] audit = priorCopy ? [route, readonlyRequest, loot] : [route, loot, readonlyRequest];
        var end = priorCopy ? 10d : 10.2d;
        var b = Changed(PurposeObservation(end, objective: 1, items: 1, audit: audit),
            "snapshotStartedAtUtc", Start.AddSeconds(9.9));
        b = Changed(Changed(Changed(b, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        proof.Observe(end, b);
        Assert.Null(proof.EvidenceFault);
        var work = Assert.Single(proof.QuestWork);
        Assert.Equal(Trace(240), work.TraceId);
        Assert.Equal(251u, work.QuestId);
        Assert.Equal("Loot", work.Action);
        var recovery = Assert.Single(proof.Recoveries);
        Assert.Equal(Trace(11), recovery.ControlTraceId);
        Assert.Equal(Trace(10), recovery.VictimTraceId);
        Assert.Equal(Trace(12), recovery.ResponseTraceId);
        Assert.Equal(Trace(240), recovery.ConsequenceTraceId);
        Assert.True(proof.PurposeProved);
    }

    [Theory]
    [InlineData(true, "Failed")]
    [InlineData(false, "Rejected")]
    public void PotentiallyMutableTerminalInsideEitherCopyFaultsRegardlessOfResultBeforeLootCredit(
        bool priorCopy, string result)
    {
        // Supplied ambiguity, not gameplay: a Failed/Rejected mutable request
        // is not proof that nothing changed while the copied arrays were read.
        var proof = InterruptedPurpose();
        Feed(proof, 7, PurposeObservation(7, live: 12, liveRequested: 6.8));
        var route = PurposeAudit(12, action: "Move", target: 0, cycle: "opaque-decision-12",
            requested: 6.8, completed: 7.5);
        var a = Changed(PurposeObservation(8.2, audit: [route]), "snapshotStartedAtUtc", Start.AddSeconds(7.9));
        a = Changed(Changed(Changed(a, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 8.2, a);
        Assert.Null(proof.EvidenceFault);
        Assert.Equal(Trace(11), proof.ControlTraceId);
        Assert.True(proof.ControlCompleted);
        Assert.Empty(proof.QuestWork);
        Assert.Empty(proof.Recoveries);
        Assert.Equal(Trace(12), BotShortRunEvidence.Id(BotShortRunEvidence.Array(proof.LastObservation!.Value, "actorAudit").Last(), "trace_id"));

        var loot = PurposeAudit(240, cycle: "valid-own-loot-context", requested: 8.3, completed: 9.5);
        var mutable = Changed(PurposeAudit(244, cycle: "mutable-copy-intersection-context",
            requested: priorCopy ? 8.01 : 10.01, completed: priorCopy ? 8.05 : 10.05), "result", result);
        object[] audit = priorCopy ? [route, mutable, loot] : [route, loot, mutable];
        var end = priorCopy ? 10d : 10.2d;
        var b = Changed(PurposeObservation(end, objective: 1, items: 1, audit: audit),
            "snapshotStartedAtUtc", Start.AddSeconds(9.9));
        b = Changed(Changed(Changed(b, "x", 100d), "questTravelTargetX", null), "questTravelTargetY", null);
        proof.Observe(end, b);
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.QuestWork);
        Assert.Empty(proof.Recoveries);
        Assert.False(proof.PurposeProved);
    }

    [Theory]
    [InlineData("TurnInDoodad", true, true)]
    [InlineData("TurnInDoodad", false, true)]
    [InlineData("TurnInDoodad", true, false)]
    [InlineData("TurnInQuest", true, true)]
    [InlineData("AutoTurnIn", true, true)]
    public void OrdinaryTurnInRecoveryNeedsFreshCompletedBitAndExactOwnControl(string action, bool completed, bool ownControl)
    {
        var proof = InterruptedPurpose(rootLootBefore: true);
        if (!ownControl) proof.ObserveControl(Control(trace: 12));
        // Target is never the purpose oracle: a doodad target may be a world
        // object. These supplied effects use the immutable captured quest251.
        var target = action == "TurnInDoodad" ? 900u : 251u;
        var row = PurposeAudit(222, action: action, target: target, parent: 251,
            cycle: "opaque-decision-10", requested: 7, completed: 7.5);
        var after = PurposeObservation(8, objective: 1, items: 1, audit: [row]);
        if (completed) after = Changed(after, "activeQuests", System.Array.Empty<object>());
        after = Changed(after, "questCompletion", new[] { Flag(251, completed) });
        after = Changed(Changed(after, "questTravelTargetX", null), "questTravelTargetY", null);
        Feed(proof, 8, after);
        Assert.Equal(completed && ownControl, proof.PurposeProved);
        if (completed && ownControl)
        {
            Assert.Equal(Trace(222), Assert.Single(proof.Recoveries).ConsequenceTraceId);
            Assert.Equal(251u, Assert.Single(proof.CompletedGoals).QuestId);
        }
        else Assert.Empty(proof.Recoveries);
    }



    [Theory]
    [InlineData("changed-terminal-cycle", 251u)]
    [InlineData(null, 2u)]
    public void RunningVictimCannotChangeImmutableParentOrCycleAtTerminal(string? cycle, uint parent)
    {
        var proof = InterruptedPurpose(cycle, parent);
        Feed(proof, 8, PurposeObservation(8, live: 12, liveRequested: 7));
        Feed(proof, 10, PurposeObservation(10, objective: 1, items: 1,
            audit: [PurposeAudit(202, requested: 9, completed: 9.3)]));
        Assert.False(proof.PurposeProved);
        Assert.Empty(proof.Recoveries);
    }

    [Fact]
    public void FreshRearmCannotReuseVictimArmCycle()
    {
        var proof = InterruptedPurpose();
        Feed(proof, 8, Changed(PurposeObservation(8, live: 12, liveRequested: 7),
            "liveDecisionCycleId", "opaque-decision-10"));
        Feed(proof, 10, PurposeObservation(10, objective: 1, items: 1,
            audit: [PurposeAudit(203, requested: 9, completed: 9.3)]));
        Assert.False(proof.PurposeProved);
        Assert.Empty(proof.Recoveries);
    }

    [Fact]
    public void MissingCopiedWatermarkFailsClosed()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, Changed(PurposeObservation(3, objective: 1, items: 1,
            audit: [PurposeAudit(204)]), "snapshotStartedAtUtc", null));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.QuestWork);
    }

    [Fact]
    public void LostAuditTailOverlapFailsClosedRatherThanAdoptingLaterProgress()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, PurposeObservation(3, audit: [PurposeAudit(205)]));
        proof.Observe(6, PurposeObservation(6, objective: 1, items: 1,
            audit: [PurposeAudit(206, requested: 4, completed: 5)]));
        Assert.NotNull(proof.EvidenceFault);
        Assert.Empty(proof.QuestWork);
    }

    [Theory]
    [InlineData(99u, 99u)]
    [InlineData(42u, 0u)]
    public void ForeignActorOrZeroCorpseCannotEarnQuestLoot(uint actor, uint corpse)
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        var row = Changed(PurposeAudit(207, target: corpse), "actor_id", actor);
        Feed(proof, 3, PurposeObservation(3, objective: 1, items: 1, audit: [row]));
        Assert.Empty(proof.QuestWork);
    }

    // Explicit supplied schema/row/codec fixtures, NOT deployed SQL corroboration
    // or live persistence proof. The live harness obtains these through bounded SELECTs.
    private static QuestSqlCodec SuppliedCodec(QuestSqlColumn[] columns, bool round = false)
        => new(columns, round ? "" : "TIME_TRUNCATE_FRACTIONAL", "+00:00", "UTC", round,
            JsonSerializer.SerializeToElement(new { source = "synthetic supplied codec fixture" }), [],
            JsonSerializer.SerializeToElement(new { timing = "BEFORE", operation = "UPDATE",
                statement = "SET NEW.last_updated = UTC_TIMESTAMP()" }));
    [Theory]
    [InlineData(541.3692288, 541)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 3)]
    [InlineData(0, 0)]
    [InlineData(2147483647, 2147483647)]
    public void OnlineTimeSignedIntInsertRoundsFractionalSecondsHalfAwayFromZero(double seconds, int stored)
    {
        // Supplied signed-INT schema, not provider corroboration. Integer INSERT
        // conversion is distinct from ROUND() on an approximate argument.
        var column = new QuestSqlColumn("characters", "online_time", "int", "int", null);
        Assert.True(SameQuestColumn(JsonSerializer.SerializeToElement(stored), JsonSerializer.SerializeToElement(seconds),
            column, SuppliedCodec([column])));
    }

    [Theory]
    [InlineData(541.3692288, 542)]
    [InlineData(1.5, 1)]
    [InlineData(2.5, 2)]
    [InlineData(-0.1, 0)]
    [InlineData(-1, -1)]
    [InlineData(2147483647.5, 2147483647)]
    [InlineData(2147483648, 2147483648)]
    [InlineData(541.3692288, 541.1)]
    public void OnlineTimeSignedIntRejectsWrongStoredValueNegativeSecondsAndOverflow(double seconds, double stored)
    {
        var column = new QuestSqlColumn("characters", "online_time", "int", "int", null);
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(stored), JsonSerializer.SerializeToElement(seconds),
            column, SuppliedCodec([column])));
    }

    [Theory]
    [InlineData("characters", "hp")]
    [InlineData("items", "count")]
    [InlineData("accounts", "labor")]
    public void OtherIntegerColumnsDoNotRoundFractionalValues(string table, string name)
    {
        var column = new QuestSqlColumn(table, name, "int", "int", null);
        var codec = SuppliedCodec([column]);
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(2), JsonSerializer.SerializeToElement(1.5), column, codec));
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(1.5), JsonSerializer.SerializeToElement(1.5), column, codec));
        Assert.True(SameQuestColumn(JsonSerializer.SerializeToElement(2), JsonSerializer.SerializeToElement(2), column, codec));
    }


    [Theory]
    [InlineData("items", "grade", 3)]
    [InlineData("characters", "gender", 2)]
    public void DeclaredTinyIntWidthOneGradeAndGenderRemainNumeric(string table, string name, int value)
    {
        var column = new QuestSqlColumn(table, name, "tinyint", "tinyint(1)", null);
        var codec = SuppliedCodec([column]);
        Assert.True(SameQuestColumn(JsonSerializer.SerializeToElement(value), JsonSerializer.SerializeToElement(value), column, codec));
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(true), JsonSerializer.SerializeToElement(value), column, codec));
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(1), JsonSerializer.SerializeToElement(true), column, codec));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void OnlyDeclaredActualCharacterBooleanColumnUsesBooleanDomain(int number, bool value)
    {
        var column = new QuestSqlColumn("characters", "died_in_pvp", "tinyint", "tinyint(1)", null);
        Assert.True(SameQuestColumn(JsonSerializer.SerializeToElement(number), JsonSerializer.SerializeToElement(value),
            column, SuppliedCodec([column])));
    }

    [Fact]
    public void FloatSaveDomainUsesExactBinary32AndDoubleUsesExactBinary64NotEpsilon()
    {
        var single = new QuestSqlColumn("characters", "x", "float", "float", null);
        var doubleColumn = new QuestSqlColumn("items", "expire_online_minutes", "double", "double", null);
        var codec = SuppliedCodec([single, doubleColumn]);
        Assert.True(SameQuestColumn(JsonSerializer.SerializeToElement(10f), JsonSerializer.SerializeToElement(10.00000001d), single, codec));
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(10f), JsonSerializer.SerializeToElement(float.BitIncrement(10f)), single, codec));
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(10d), JsonSerializer.SerializeToElement(double.BitIncrement(10d)), doubleColumn, codec));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void SuppliedDatetimePrecisionCodecDoesNotGuessNullOrMinValue(bool round, int expectedSeconds)
    {
        var column = new QuestSqlColumn("characters", "created_at", "datetime", "datetime", 0);
        var codec = SuppliedCodec([column], round);
        Assert.True(SameQuestColumn(JsonSerializer.SerializeToElement(Start.AddSeconds(expectedSeconds)),
            JsonSerializer.SerializeToElement(Start.AddTicks(5000001)), column, codec));
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement((DateTime?)null),
            JsonSerializer.SerializeToElement(DateTime.MinValue), column, codec));
        Assert.False(SameQuestColumn(JsonSerializer.SerializeToElement(Start), JsonSerializer.SerializeToElement(Start),
            column with { DatePrecision = null }, codec));
    }

    [Fact]
    public void FullRowsRequireBidirectionalDeclaredColumnsAndUniqueKeys()
    {
        var codec = SuppliedCodec([new("abilities", "id", "int", "int", null),
            new("abilities", "exp", "int", "int", null), new("abilities", "owner", "int", "int", null)]);
        var row = JsonSerializer.SerializeToElement(new { id = 1, exp = 3, owner = 7 });
        var extra = Changed(row, "unobserved_column", 0);
        Assert.True(SameQuestRows([row], [row], "abilities", "id", codec));
        Assert.False(SameQuestRows([row], [extra], "abilities", "id", codec));
        Assert.False(SameQuestRows([extra], [row], "abilities", "id", codec));
        Assert.False(SameQuestRows([row, row], [row, row], "abilities", "id", codec));
        Assert.False(SameQuestRows([row], [], "abilities", "id", codec));
    }

    [Fact]
    public void FullQuestAndCompletedRowsRequireOwnerIdentityAndAllBlobBytes()
    {
        var codec = SuppliedCodec([new("quests","id","int","int",null), new("quests","template_id","int","int",null),
            new("quests","owner","int","int",null), new("quests","status","tinyint","tinyint",null),
            new("quests","data","tinyblob","tinyblob",null), new("completed_quests","id","int","int",null),
            new("completed_quests","owner","int","int",null), new("completed_quests","data","blob","blob",null)]);
        var quest = JsonSerializer.SerializeToElement(new { id = 123u, template_id = 251u, owner = 7u, status = 1, data = "AQID" });
        var block = JsonSerializer.SerializeToElement(new { id = 3u, owner = 7u, data = "AAAAAAAAAAA=" });
        Assert.True(SameQuestRows([quest], [quest], "quests", "id", codec));
        Assert.False(SameQuestRows([quest], [Changed(quest, "owner", 8u)], "quests", "id", codec));
        Assert.False(SameQuestRows([quest], [Changed(quest, "id", 251u)], "quests", "id", codec));
        Assert.False(SameQuestRows([quest], [Changed(quest, "data", "AQIE")], "quests", "id", codec));
        Assert.True(SameQuestRows([block], [block], "completed_quests", "id", codec));
        Assert.False(SameQuestRows([block], [Changed(block, "owner", 8u)], "completed_quests", "id", codec));
        Assert.False(SameQuestRows([block], [Changed(block, "data", "AQAAAAAAAAA=")], "completed_quests", "id", codec));
    }


    [Fact]
    public void FirstConsumerFaultFreezesBeforeAnyLaterSuppliedLootGain()
    {
        var proof = new BotShortRunEvidence(7, PurposeObservation());
        Feed(proof, 3, Changed(PurposeObservation(3), "snapshotStartedAtUtc", null));
        var frozen = proof.LastObservation;
        Feed(proof, 6, PurposeObservation(6, objective: 1, items: 1,
            audit: [PurposeAudit(208, requested: 4, completed: 5)]));
        Assert.Empty(proof.QuestWork);
        Assert.Equal(frozen, proof.LastObservation);
    }
    private static (NeedsFarmLoopE2eTests.FarmDurable Saved, QuestBodyDurable Body, JsonElement End, QuestSqlCodec Codec) SuppliedItemRows()
    {
        var live = JsonSerializer.SerializeToElement(new
        {
            id = 9001ul, type = "AAEmu.Game.Models.Game.Items.Item", template = 4058u, count = 1L,
            containerId = 20u, slotType = 1, slot = 0, persistedDetails = "AQID", lifespanMins = 0,
            madeUnitId = 7u, unsecureTime = Start, unpackTime = Start, owner = 7u,
            createdAt = Start, grade = 3, flags = 0u, ucc = 0u, expireTime = Start,
            expireOnlineMinutes = 0d, chargeTime = Start, chargeCount = 0
        });
        var map = new (string Db, string Live)[] { ("id","id"), ("type","type"), ("template_id","template"),
            ("container_id","containerId"), ("slot_type","slotType"), ("slot","slot"), ("count","count"),
            ("details","persistedDetails"), ("lifespan_mins","lifespanMins"), ("made_unit_id","madeUnitId"),
            ("unsecure_time","unsecureTime"), ("unpack_time","unpackTime"), ("owner","owner"), ("grade","grade"),
            ("flags","flags"), ("created_at","createdAt"), ("ucc","ucc"), ("expire_time","expireTime"),
            ("expire_online_minutes","expireOnlineMinutes"), ("charge_time","chargeTime"), ("charge_count","chargeCount") };
        var database = JsonSerializer.SerializeToElement(map.ToDictionary(p => p.Db, p => live.GetProperty(p.Live)));
        var dates = new HashSet<string> { "unsecure_time", "unpack_time", "created_at", "expire_time", "charge_time" };
        var columns = map.Select(p => new QuestSqlColumn("items", p.Db,
            dates.Contains(p.Db) ? "datetime" : p.Db == "details" ? "blob" : p.Db == "type" ? "varchar"
                : p.Db == "expire_online_minutes" ? "double" : p.Db == "id" ? "bigint" : p.Db == "grade" ? "tinyint" : "int",
            p.Db == "grade" ? "tinyint(1)" : dates.Contains(p.Db) ? "datetime" : "supplied declared domain",
            dates.Contains(p.Db) ? 0 : null)).Concat(new QuestSqlColumn[]
        {
            new("quests","id","int","int",null), new("quests","template_id","int","int",null),
            new("quests","owner","int","int",null), new("quests","status","tinyint","tinyint",null),
            new("quests","data","tinyblob","tinyblob",null), new("completed_quests","id","int","int",null),
            new("completed_quests","owner","int","int",null), new("completed_quests","data","blob","blob",null),
            new("characters","id","int","int",null), new("characters","account_id","int","int",null),
            new("characters","x","float","float",null), new("characters","y","float","float",null),
            new("characters","z","float","float",null), new("characters","roll","float","float",null),
            new("characters","pitch","float","float",null), new("characters","yaw","float","float",null)
        }).ToArray();
        var item = new NeedsFarmLoopE2eTests.FarmItem(9001, 4058, 1, 0, 1, "AQID", 1, 0,
            Changed(database, "grade", true).GetRawText()); // Explicit lossy old-provider diagnostic.
        var saved = new NeedsFarmLoopE2eTests.FarmDurable(7, 11, 100, 10, 42000, 0,
            1.25, 2.5, 3.75, 1, 99, [item], [], [], [], [], []);
        var body = new QuestBodyDurable(Start.AddSeconds(12), Start.AddSeconds(12.1),
            JsonSerializer.SerializeToElement(new { id = 7u, account_id = 11u,
                x = 1.25f, y = 2.5f, z = 3.75f, roll = 0f, pitch = 0f, yaw = 0f }),
            JsonSerializer.SerializeToElement(new { x = 1.25d, y = 2.5d, z = 3.75d, roll = 0d, pitch = 0d, yaw = 0d }),
            JsonSerializer.SerializeToElement(new { account_id = 11u }), [], [database], "TIME_TRUNCATE_FRACTIONAL", "+00:00");
        var end = JsonSerializer.SerializeToElement(new
        {
            id = 7u, accountId = 11u, level = 10, experience = 42000, money = 0, labor = 100,
            saveDomain = new { worldId = 1u, zoneId = 99u, x = 1.25f, y = 2.5f, z = 3.75f },
            ownItems = new[] { live }, activeQuests = System.Array.Empty<object>(),
            completedQuests = System.Array.Empty<object>(), actabilities = System.Array.Empty<object>()
        });
        return (saved, body, end, SuppliedCodec(columns));
    }

    [Fact]
    public void AllTwentyOneItemColumnsUseLosslessOwnRowsAndExactPersistedWriterBytes()
    {
        var fixture = SuppliedItemRows();
        Assert.True(MatchesQuestDurable(fixture.Saved, fixture.End, fixture.Codec, fixture.Body));
        var own = BotShortRunEvidence.Array(fixture.End, "ownItems").Single();
        Assert.Equal(21, own.EnumerateObject().Count());
        foreach (var field in own.EnumerateObject())
        {
            object changed = field.Value.ValueKind == JsonValueKind.Number ? field.Value.GetDouble() + 1
                : field.Name == "persistedDetails" ? "AQIE"
                : field.Value.TryGetDateTime(out var date) ? date.AddSeconds(1) : field.Value.GetString() + "-changed";
            Assert.False(MatchesQuestDurable(fixture.Saved,
                Changed(fixture.End, "ownItems", new[] { Changed(own, field.Name, changed) }), fixture.Codec, fixture.Body));
        }
        foreach (var mutation in new[] { Changed(own, "grade", 2), Changed(own, "containerId", 0u),
            Changed(own, "persistedDetails", "AQIE"), Changed(own, "owner", 8u), Changed(own, "unknownColumn", 0) })
            Assert.False(MatchesQuestDurable(fixture.Saved, Changed(fixture.End, "ownItems", new[] { mutation }),
                fixture.Codec, fixture.Body));
        Assert.False(MatchesQuestDurable(fixture.Saved, Changed(fixture.End, "ownItems", System.Array.Empty<object>()),
            fixture.Codec, fixture.Body));
        Assert.False(MatchesQuestDurable(fixture.Saved, fixture.End, fixture.Codec, fixture.Body with { Items = [] }));
        var wrongDomain = Changed(fixture.End.GetProperty("saveDomain"), "x", float.BitIncrement(1.25f));
        Assert.False(MatchesQuestDurable(fixture.Saved, Changed(fixture.End, "saveDomain", wrongDomain), fixture.Codec, fixture.Body));
    }

    private static (QuestBodyDurable After, JsonElement End, QuestSqlCodec Codec, DateTime FrozenAt) SuppliedBodyRows()
    {
        var beforeCharacter = JsonSerializer.SerializeToElement(new { id = 7u, account_id = 11u,
            hp = 100, unit_model_params = "AQID", updated_at = Start.AddSeconds(9), deleted = 0, reported_as_bot_count = 0,
            x = 12530.2f, y = 2.5f, z = 3.75f, roll = 0f, pitch = 0f, yaw = 0f });
        var beforeAccount = JsonSerializer.SerializeToElement(new { account_id = 11u, labor = 100,
            last_updated = Start.AddSeconds(9), last_labor_tick = Start.AddSeconds(9) });
        var ability = JsonSerializer.SerializeToElement(new { id = 1, exp = 1000, owner = 7u });
        var mirrors = JsonSerializer.SerializeToElement(new { x = 12530.1875d, y = 2.5d, z = 3.75d, roll = 0d, pitch = 0d, yaw = 0d });
        var sqlEnd = new QuestBodyDurable(Start.AddSeconds(10.001), Start.AddSeconds(10.002),
            beforeCharacter, mirrors, beforeAccount, [ability], [], "TIME_TRUNCATE_FRACTIONAL", "+00:00");
        var liveCharacter = JsonSerializer.SerializeToElement(beforeCharacter.EnumerateObject()
            .Where(p => p.Name is not ("deleted" or "reported_as_bot_count")).ToDictionary(p => p.Name, p => p.Value));
        liveCharacter = Changed(liveCharacter, "x", 12530.1875f);
        var end = JsonSerializer.SerializeToElement(new
        {
            id = 7u, labor = 100, observedAtUtc = Start.AddSeconds(10), fullCharacter = liveCharacter,
            fullAccount = beforeAccount, abilities = new[] { ability }, ownedSqlCopy = sqlEnd,
            fullAccountSqlStartedAtUtc = sqlEnd.SnapshotStartedAtUtc, fullAccountSqlObservedAtUtc = sqlEnd.ObservedAtUtc
        });
        var after = new QuestBodyDurable(Start.AddSeconds(12), Start.AddSeconds(12.1),
            Changed(beforeCharacter, "updated_at", Start.AddSeconds(11)), mirrors, Changed(beforeAccount, "last_updated", Start.AddSeconds(11)),
            [ability], [], "TIME_TRUNCATE_FRACTIONAL", "+00:00");
        var codec = SuppliedCodec([
            new("characters","id","int","int",null), new("characters","account_id","int","int",null),
            new("characters","hp","int","int",null), new("characters","unit_model_params","blob","blob",null),
            new("characters","updated_at","datetime","datetime",0), new("characters","deleted","int","int",null),
            new("characters","reported_as_bot_count","int","int",null),
            new("characters","x","float","float",null), new("characters","y","float","float",null),
            new("characters","z","float","float",null), new("characters","roll","float","float",null),
            new("characters","pitch","float","float",null), new("characters","yaw","float","float",null),
            new("accounts","account_id","int","int",null), new("accounts","labor","int","int",null),
            new("accounts","last_updated","datetime","datetime",0), new("accounts","last_labor_tick","datetime","datetime",0),
            new("abilities","id","int","int",null), new("abilities","exp","int","int",null), new("abilities","owner","int","int",null)]);
        return (after, end, codec, Start.AddSeconds(10.1));
    }

    [Fact]
    public void OnlyTwoRawBookkeepingColumnsCanUseProvedOwnedWriteBounds()
    {
        var fixture = SuppliedBodyRows();
        Assert.True(MatchesQuestBody(fixture.After, fixture.End, fixture.Codec, fixture.FrozenAt));
        var bookkeeping = QuestBookkeepingEvidence(fixture.After, fixture.End, fixture.Codec, fixture.FrozenAt);
        Assert.Equal(2, bookkeeping.Length);
        Assert.All(bookkeeping, b => Assert.True(b.Proved));
        Assert.All(bookkeeping, b => Assert.NotEqual(b.Before, b.After));
        Assert.False(MatchesQuestBody(fixture.After with { Character = Changed(fixture.After.Character, "updated_at", Start.AddSeconds(13)) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Account = Changed(fixture.After.Account, "last_labor_tick", Start.AddSeconds(11)) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Account = Changed(fixture.After.Account, "labor", 101) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Character = Changed(fixture.After.Character, "hp", 101) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Character = Changed(fixture.After.Character, "unit_model_params", "AQIE") },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Character = Changed(fixture.After.Character, "deleted", 1) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After, Changed(fixture.End, "ownedSqlCopy", null), fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { SqlMode = "UNOBSERVED" }, fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { SessionTimeZone = "+01:00" }, fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { SnapshotStartedAtUtc = Start.AddSeconds(9) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Abilities = [Changed(fixture.After.Abilities[0], "owner", 8u)] },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Character = Changed(fixture.After.Character, "reported_as_bot_count", 1) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { Character = Changed(fixture.After.Character, "unknownSavedColumn", 0) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
    }

    [Fact]
    public void OnlineCountersGrowOnlyWithinTheExactEndToReleaseInterval()
    {
        // T10 runs 7 and 9: the counters advanced by one 5 s tracking jump
        // between END and the leave-save (run 9: within 0.17 s of wall time).
        var codec = SuppliedCodec([new("characters", "online_time", "int", "int", null),
            new("accounts", "divine_clock_time", "int", "int", null)]);
        var end = JsonSerializer.SerializeToElement(new
        {
            id = 7u,
            fullCharacter = new { online_time = 306.6879143d },
            fullAccount = new { divine_clock_time = 306 },
        });
        QuestBodyDurable After(int onlineTime, int divineClock, double readAt = 10.15) => new(Start.AddSeconds(readAt), Start.AddSeconds(readAt + 0.02),
            JsonSerializer.SerializeToElement(new { id = 7u, account_id = 11u, online_time = onlineTime }),
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { account_id = 11u, divine_clock_time = divineClock }),
            [], [], "TIME_TRUNCATE_FRACTIONAL", "+00:00");
        var frozenAt = Start.AddSeconds(10);

        QuestBookkeeping[] Counters(QuestBodyDurable after, DateTime? leftWorld = null)
            => QuestBookkeepingEvidence(after, end, codec, frozenAt, leftWorld)
                .Where(b => b.Column is "online_time" or "divine_clock_time").ToArray();
        var grown = Counters(After(312, 311));
        Assert.Equal(2, grown.Length);
        Assert.All(grown, b => Assert.True(b.Proved));
        // Just past one tracking jump + the interval + 1 s rounding.
        Assert.All(Counters(After(314, 314)), b => Assert.False(b.Proved));
        Assert.All(Counters(After(320, 320)), b => Assert.False(b.Proved));
        Assert.All(Counters(After(300, 300)), b => Assert.False(b.Proved));

        // After-flush read 325 s later: growth stops at the leave (the release
        // read). Unchanged release values pass; counters that kept ticking
        // after the leave (a bot that never really left) fail.
        var released = Start.AddSeconds(10.17);
        Assert.All(Counters(After(312, 311, readAt: 335), released), b => Assert.True(b.Proved));
        Assert.All(Counters(After(631, 631, readAt: 335), released), b => Assert.False(b.Proved));
    }

    [Fact]
    public void FullBodyUsesExactTypedFloatMirrorsInsteadOfLossyDirectReaderValues()
    {
        // Supplied provider-shaped rows, not a provider round-trip or gameplay proof.
        var fixture = SuppliedBodyRows();
        Assert.NotEqual(fixture.After.Character.GetProperty("x").GetDouble(),
            fixture.After.CharacterFloatMirrors.GetProperty("x").GetDouble());
        Assert.True(MatchesQuestBody(fixture.After, fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { CharacterFloatMirrors =
                Changed(fixture.After.CharacterFloatMirrors, "x", (double)float.BitIncrement(12530.1875f)) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        // This double rounds to the same Single, but is not an exact Single→Double mirror.
        Assert.False(MatchesQuestBody(fixture.After with { CharacterFloatMirrors =
                Changed(fixture.After.CharacterFloatMirrors, "x", double.BitIncrement(12530.1875d)) },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After with { CharacterFloatMirrors = default },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        var incomplete = JsonSerializer.SerializeToElement(fixture.After.CharacterFloatMirrors.EnumerateObject()
            .Where(p => p.Name != "yaw").ToDictionary(p => p.Name, p => p.Value));
        Assert.False(MatchesQuestBody(fixture.After with { CharacterFloatMirrors = incomplete },
            fixture.End, fixture.Codec, fixture.FrozenAt));
        Assert.False(MatchesQuestBody(fixture.After,
            Changed(fixture.End, "ownedSqlCopy", Changed(fixture.End.GetProperty("ownedSqlCopy"), "CharacterFloatMirrors", null)),
            fixture.Codec, fixture.FrozenAt));
    }

    [Theory]
    [InlineData("missing_float")]
    [InlineData("wrong_type")]
    [InlineData("unexpected_float")]
    [InlineData("alias_collision")]
    public void FullBodyRejectsFloatSchemaDefectsEvenWhenEveryDeclaredRawColumnMatches(string defect)
    {
        var fixture = SuppliedBodyRows();
        var columns = fixture.Codec.Columns;
        var after = fixture.After;
        var sql = fixture.End.GetProperty("ownedSqlCopy");
        var sqlCharacter = sql.GetProperty("Character");
        var live = fixture.End.GetProperty("fullCharacter");
        static JsonElement Without(JsonElement row, string column) => JsonSerializer.SerializeToElement(
            row.EnumerateObject().Where(p => p.Name != column).ToDictionary(p => p.Name, p => p.Value));
        switch (defect)
        {
            case "missing_float":
                columns = columns.Where(c => c.Table != "characters" || c.Name != "yaw").ToArray();
                after = after with { Character = Without(after.Character, "yaw") };
                sqlCharacter = Without(sqlCharacter, "yaw");
                live = Without(live, "yaw");
                break;
            case "wrong_type":
                columns = columns.Select(c => c.Table == "characters" && c.Name == "y"
                    ? c with { DataType = "double", ColumnType = "double" } : c).ToArray();
                break;
            default:
                var name = defect == "unexpected_float" ? "extra_float" : "__quest_purpose_float_x";
                var dataType = defect == "unexpected_float" ? "float" : "int";
                columns = [.. columns, new("characters", name, dataType, dataType, null)];
                after = after with { Character = Changed(after.Character, name, 0) };
                sqlCharacter = Changed(sqlCharacter, name, 0);
                live = Changed(live, name, 0);
                break;
        }
        var end = Changed(Changed(fixture.End, "fullCharacter", live), "ownedSqlCopy", Changed(sql, "Character", sqlCharacter));
        Assert.False(MatchesQuestBody(after, end, fixture.Codec with { Columns = columns }, fixture.FrozenAt));
    }

    [Fact]
    public void MandatoryDurablePositionUsesFullOwnedFloatMirrorsAndNotFarmReaderDiagnostics()
    {
        var fixture = SuppliedItemRows();
        var body = fixture.Body with
        {
            Character = Changed(fixture.Body.Character, "x", 12530.2f),
            CharacterFloatMirrors = Changed(fixture.Body.CharacterFloatMirrors, "x", 12530.1875d)
        };
        var end = Changed(fixture.End, "saveDomain", Changed(fixture.End.GetProperty("saveDomain"), "x", 12530.1875f));
        var saved = fixture.Saved with { X = 12530.2d };
        Assert.True(MatchesQuestDurable(saved, end, fixture.Codec, body));
        Assert.False(MatchesQuestDurable(saved, end, fixture.Codec, body with { CharacterFloatMirrors =
            Changed(body.CharacterFloatMirrors, "x", (double)float.BitIncrement(12530.1875f)) }));
        Assert.False(MatchesQuestDurable(saved, end, fixture.Codec, body with { CharacterFloatMirrors =
            Changed(body.CharacterFloatMirrors, "x", double.BitIncrement(12530.1875d)) }));
        Assert.False(MatchesQuestDurable(saved, end, fixture.Codec, body with { CharacterFloatMirrors = default }));
    }


    private static DurableSnapshot Baseline()
        => new(7, 10, 42000, 0, new() { [10] = 1 }, [], 0);
    private static Reward CanonicalReward()
        => new(1, 420, 33, new() { [20] = 1 }, true, null);
    private static DurableSnapshot Saved(long xp = 42420, long money = 33, bool completion = true, long starterCount = 1)
        => new(7, 10, xp, money, new() { [10] = starterCount, [20] = 1 }, completion ? [1] : [], 0);

    [Fact]
    public void DurableCompletionAndExactRewardsPreserveStarterConservation()
    {
        var result = VerifyDurable(Baseline(), Saved(), [CanonicalReward()], [1]);
        Assert.True(result.Proved);
    }

    [Theory]
    [InlineData(42420, 33, false, 1)]
    [InlineData(42000, 33, true, 1)]
    [InlineData(42420, 0, true, 1)]
    [InlineData(42420, 33, true, 0)]
    public void MissingDurableBitRewardOrStarterCannotPass(long xp, long money, bool completion, long starterCount)
    {
        var proof = Accepted();
        Declare(proof);
        proof.ObserveControl(Control());
        Continue(proof);
        NextChoice(proof);
        var result = VerifyDurable(Baseline(), Saved(xp, money, completion, starterCount), [CanonicalReward()], [1]);
        Assert.False(result.Proved);
        Assert.False(OverallProved(true, result.Proved, cleanup: true, noInfrastructureFault: true));
    }

    [Fact]
    public void UnsupportedCanonicalRewardIsUnknownRatherThanAnInventedGrant()
    {
        var result = VerifyDurable(Baseline(), Saved(), [CanonicalReward() with { Supported = false }], [1]);
        Assert.False(result.Proved);
        Assert.Equal("UNKNOWN", result.Scope);
    }

    [Fact]
    public void ExtraUnobservedCompletionPreventsExactRewardAttribution()
    {
        var saved = Saved() with { CompletedQuests = [1, 2] };
        var result = VerifyDurable(Baseline(), saved, [CanonicalReward()], [1]);
        Assert.False(result.Proved);
        Assert.Equal("UNKNOWN", result.Scope);
    }
}
