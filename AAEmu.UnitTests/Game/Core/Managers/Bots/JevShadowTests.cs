using System.Numerics;
using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Jev teacher/critic Stage 1 (Shadow): the frozen shadow-log record type,
/// the disagreement-dataset JSONL writer, and the sampling-gated emission at
/// the already-logged quest point. The shadow surface never decides, never
/// reads live state, and never throws into the tick.
/// </summary>
[NotInParallel]
public class JevShadowTests
{
    [Before(Test)]
    public void SetUp()
    {
        _prevEnabled = JevShadow.Enabled;
        _prevSample = JevShadow.SampleEvery;
        _prevPath = JevShadow.FilePath;
        _prevMaxQueued = JevShadow.MaxQueuedLines;
        JevShadow.Enabled = true;
        JevShadow.SampleEvery = 1;
        JevShadow.MaxQueuedLines = 1024;
        JevShadow.FilePath = Path.Combine(Path.GetTempPath(), "jev-shadow-tests-" + Guid.NewGuid().ToString("N"), "shadow.jsonl");
        JevShadow.ResetSamplingForTest();
        JevShadow.ResetQueueForTest();
    }

    [After(Test)]
    public void TearDown()
    {
        JevShadow.SetWriterPausedForTest(false);
        var testPath = JevShadow.FilePath;
        JevShadow.ResetQueueForTest();
        JevShadow.MaxQueuedLines = _prevMaxQueued;
        JevShadow.Enabled = _prevEnabled;
        JevShadow.SampleEvery = _prevSample;
        JevShadow.FilePath = _prevPath;
        JevShadow.ResetSamplingForTest();
        JevShadow.ClearWakeInputsForTest();
        try
        {
            if (testPath.EndsWith("shadow.jsonl", StringComparison.Ordinal))
            {
                var dir = Path.GetDirectoryName(testPath);
                if (dir != null && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }
        catch (Exception) { }
    }

    private bool _prevEnabled;
    private int _prevSample;
    private int _prevMaxQueued;
    private string _prevPath = "";

    private static BotDecisionProposal Proposal(
        string goal, int priority, int weight, string rejectName) => new(
            goal: goal,
            action: ActorActionType.Observe,
            targetId: 0,
            expectedPostcondition: new BotProposalPostcondition("observation remains available", _ => true),
            idempotencyKey: $"shadow-{goal}",
            timeout: TimeSpan.FromSeconds(1),
            rationale: $"proposal {goal}",
            policyVersion: "quest-v1",
            priority: priority,
            personalityWeight: weight,
            tieBreakKey: rejectName,
            hardPreconditions:
            [
                new BotProposalPrecondition(rejectName, _ => false)
            ]);

    /// <summary>
    /// A proposal that satisfies every hard precondition, so it reaches the
    /// preference order. <see cref="Proposal"/> hard-codes a failing gate and
    /// therefore cannot express a LEGAL contender.
    /// </summary>
    private static BotDecisionProposal LegalProposal(
        string goal, int priority, int weight, string tieBreakKey) => new(
            goal: goal,
            action: ActorActionType.Observe,
            targetId: 0,
            expectedPostcondition: new BotProposalPostcondition("observation remains available", _ => true),
            idempotencyKey: $"shadow-{goal}",
            timeout: TimeSpan.FromSeconds(1),
            rationale: $"proposal {goal}",
            policyVersion: "quest-v1",
            priority: priority,
            personalityWeight: weight,
            tieBreakKey: tieBreakKey,
            hardPreconditions:
            [
                new BotProposalPrecondition(tieBreakKey, _ => true)
            ]);

    private static QuestDecisionScenario.QuestRunResult FailResult(params BotProposalRejection[] rejections)
        => new()
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = false,
            SelectedAction = null,
            Request = null,
            Rejections = rejections,
            Explanation = "no legal proposal",
            FailStage = "DECIDE",
            Failure = ActorFailureReason.WrongDecision,
            FailReason = "no legal quest proposal: empty [swept=0 offerings=0 inBand=0 legal=0 firstZero=sweep-empty]",
            CompletedQuestIds = [251],
            TraceRecords = []
        };

    /// <summary>
    /// The record carries the full shadow contract from frozen buffers only:
    /// scenario/wake ids, the inputs hash, brain candidates + scores, the
    /// decision + verdict/reason, and outcome-delta refs.
    /// </summary>
    [Test]
    public async Task BuildRecord_DecideFail_CarriesShadowContract()
    {
        var a = Proposal("quest.accept", 10, 0, "not-in-band");
        var b = Proposal("quest.advance", 20, 5, "quest-not-active");
        var result = FailResult(
            new BotProposalRejection(a, "precondition 'not-in-band' was not satisfied"),
            new BotProposalRejection(b, "precondition 'quest-not-active' was not satisfied"));

        var record = JevShadow.BuildRecord(42, "quest-42-7", "DECIDE: no legal quest proposal", result);

        await Assert.That(record.Schema).IsEqualTo(JevShadow.SchemaVersion);
        await Assert.That(record.Scenario).IsEqualTo(QuestDecisionScenario.ScenarioName);
        await Assert.That(record.CycleId).IsEqualTo("quest-42-7");
        await Assert.That(record.CharacterId).IsEqualTo(42u);
        await Assert.That(record.InputsHash).IsNotEmpty();
        await Assert.That(record.DecideDetail).IsEqualTo("DECIDE: no legal quest proposal");
        await Assert.That(record.Candidates.Count).IsEqualTo(2);
        await Assert.That(record.Candidates[1].Goal).IsEqualTo("quest.advance");
        await Assert.That(record.Candidates[1].Priority).IsEqualTo(20);
        await Assert.That(record.Candidates[1].Weight).IsEqualTo(5);
        await Assert.That(record.Candidates[0].Reason.Contains("not-in-band")).IsTrue();
        await Assert.That(record.CandidateCount).IsEqualTo(2);
        await Assert.That(record.RejectionCount).IsEqualTo(2);
        await Assert.That(record.Verdict).IsEqualTo("DECIDE");
        await Assert.That(record.Reason.Contains("no legal proposal")).IsTrue();
        await Assert.That(record.CompletedQuestIds).IsEquivalentTo(new[] { 251u });
        await Assert.That(record.AuditRowCount).IsEqualTo(0);
        await Assert.That(record.Request).IsNull();
    }

    /// <summary>
    /// The writer emits one snake_case JSONL row per wake to the lane-side
    /// path (the disagreement dataset schema as code).
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_Enabled_WritesJsonlRow()
    {
        JevShadow.MaybeEmitQuest(7, "quest-7-1", "DECIDE: empty",
            FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("schema").GetString()).IsEqualTo(JevShadow.SchemaVersion);
        await Assert.That(root.GetProperty("cycle_id").GetString()).IsEqualTo("quest-7-1");
        await Assert.That(root.GetProperty("character_id").GetUInt32()).IsEqualTo(7u);
        await Assert.That(root.GetProperty("candidates").GetArrayLength()).IsEqualTo(1);
        await Assert.That(root.GetProperty("verdict").GetString()).IsEqualTo("DECIDE");
    }

    /// <summary>
    /// Flag-off wakes write nothing: no lines, no file.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_Disabled_WritesNothing()
    {
        JevShadow.Enabled = false;

        JevShadow.MaybeEmitQuest(7, "quest-7-1", "DECIDE: empty",
            FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));

        await Assert.That(File.Exists(JevShadow.FilePath)).IsFalse();
    }

    /// <summary>
    /// The sampling gate writes only every Nth eligible wake (default 1 =
    /// every wake; 2 = every second wake starting with the first).
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_SampleEveryTwo_WritesHalf()
    {
        JevShadow.SampleEvery = 2;

        for (var wake = 1; wake <= 4; wake++)
            JevShadow.MaybeEmitQuest(7, $"quest-7-{wake}", "DECIDE: empty",
                FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0].Contains("quest-7-1")).IsTrue();
        await Assert.That(lines[1].Contains("quest-7-3")).IsTrue();
    }

    /// <summary>
    /// LANE-SINK FAULT CONTAINMENT. A wake whose output path cannot be written
    /// (here a directory standing where the file goes) must lose only its own
    /// row — and the tick must never have touched the filesystem to find that
    /// out.
    ///
    /// Two things are pinned, and both are real contracts rather than
    /// "it did not throw":
    ///
    ///  1. NO I/O ON THE TICK. The row is handed to the background writer and
    ///     sits queued while the writer is paused. If anyone ever inlines the
    ///     write back onto the tick, this row is gone instead — an unwritable
    ///     path then raises out of the wake, which is the exact failure this
    ///     lane-side sink exists to prevent. The paused writer makes that
    ///     deterministic: nothing can drain the queue while it is asserted.
    ///  2. THE WRITER ABSORBS THE FAULT AND SURVIVES. The row drains, is
    ///     accounted for (not stranded), creates no file at the blocked path,
    ///     and a subsequent row on a good path still lands.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_UnwritablePath_QueuesRowAndWriterAbsorbsTheFault()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "jev-shadow-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(blocker);
        JevShadow.MaxQueuedLines = 16;
        JevShadow.SetWriterPausedForTest(true);
        try
        {
            JevShadow.FilePath = blocker;

            JevShadow.MaybeEmitQuest(7, "quest-7-1", "DECIDE: empty",
                FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));

            // A row whose path cannot be written is still ACCEPTED by the sink:
            // the tick's only job is to hand the built line over. Anything less
            // (a row refused or dropped because of the future path) would mean
            // the tick is inspecting the filesystem, which is the thing the
            // background writer exists to keep off the wake.
            await Assert.That(JevShadow.QueuedLineCount).IsEqualTo(1)
                .Because("the tick must only ENQUEUE — an unwritable path cannot be " +
                         "detected without doing the write on the wake");

            JevShadow.SetWriterPausedForTest(false);

            // The row is fully accounted for: written or fault-swallowed, never
            // stranded, and never miscounted as a bounded-queue capacity drop.
            await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
            await Assert.That(JevShadow.PendingLineCount).IsEqualTo(0);
            await Assert.That(JevShadow.DroppedLineCount).IsEqualTo(0)
                .Because("the fault is on the WRITE, not on queue admission, so this row " +
                         "must not be miscounted as a capacity drop");
            await Assert.That(File.Exists(blocker)).IsFalse()
                .Because("no file may be created at a path that is a directory");

            // THE CONTRACT: the writer survives the OSError. A writer thread that
            // died on the first failed write takes the whole harvest down
            // silently — nothing throws anywhere, there are simply no more rows.
            JevShadow.FilePath = Path.Combine(Path.GetTempPath(),
                "jev-shadow-tests-" + Guid.NewGuid().ToString("N"), "shadow.jsonl");
            JevShadow.MaybeEmitQuest(7, "quest-7-2", "DECIDE: empty",
                FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));
            await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
            var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
            await Assert.That(lines.Length).IsEqualTo(1)
                .Because("the writer must still be serving rows after an unwritable-path fault");
        }
        finally
        {
            JevShadow.SetWriterPausedForTest(false);
            try { Directory.Delete(blocker, true); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// THE BYTE-IDENTICAL-WAKE CONTRACT, end to end. The shadow switch is wired
    /// into the executor's already-logged quest point
    /// (BotRoamStepExecutor.StepQuestLeg), so the byte-identical-wake promise is
    /// a claim about a REAL wake: the same bot, driven through the real
    /// executor, with the flag off and then with the flag on, must produce the
    /// same observe string.
    ///
    /// The previous version of this test compared two strings that both came
    /// from a local test helper — it never invoked JevShadow or the executor at
    /// all, so it could not fail on any change to either. What is pinned here
    /// is observable: BotRoamState.QuestDecideDetail as the executor itself
    /// wrote it, and the row the wake then emitted.
    ///
    /// The asserts are layered on purpose. Comparing off-vs-on alone would pass
    /// even if the quest leg produced "" on both sides, which is no contract at
    /// all — so the flag-off wake is pinned to the real, non-empty DECIDE string
    /// the executor derives from the run result, and the flag-on wake is pinned
    /// to having actually emitted a row carrying that same string.
    /// </summary>
    [Test]
    public async Task ExecutorQuestWake_FlagOn_LeavesDecideDetailByteIdentical()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor("jev-shadow-flag");
        GameplayActorTestRig.SetPosition(actor, Vector3.Zero);
        var runtime = new PlayerBotRuntime(actor.Character, "jev-shadow");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            EnableWildlifeHunt = false,
            ActiveActivityProvider = _ => "quest.progress",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
        };

        // Flag OFF: the pre-shadow baseline wake.
        JevShadow.Enabled = false;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var off = executor.GetBotState(runtime.CharacterId)!.QuestDecideDetail;

        // The leg really ran and really failed DECIDE — an empty detail would
        // make the byte-identity below a comparison of two empty strings.
        await Assert.That(off).IsNotEmpty()
            .Because("the quest leg must have produced a real decide detail, " +
                     "otherwise flag-off-vs-on compares nothing");
        await Assert.That(off.StartsWith("DECIDE: ", StringComparison.Ordinal)).IsTrue()
            .Because("a bareness rig has no legal quest work, so the wake is the " +
                     "frozen DECIDE-fail path this test is byte-stability of");

        // Flag ON: same bot, same executor, same clock position — the row is
        // built from the frozen evidence and handed to the writer.
        JevShadow.Enabled = true;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var on = executor.GetBotState(runtime.CharacterId)!.QuestDecideDetail;

        // The flag-on wake must ALSO have actually reached the shadow writer,
        // with the executor's own detail string on the row. Without this the
        // identity below would hold even if StepQuestLeg had stopped calling
        // JevShadow altogether — the exact regression the wiring exists to catch.
        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(File.Exists(JevShadow.FilePath)).IsTrue()
            .Because("the executor's flag-on quest wake must emit its shadow row");
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1)
            .Because("the executor's quest wake must emit exactly one shadow row when the flag is on");
        using var doc = JsonDocument.Parse(lines[0]);
        await Assert.That(doc.RootElement.GetProperty("decide_detail").GetString()).IsEqualTo(off)
            .Because("the row must carry the very decide detail the executor observed");
        await Assert.That(doc.RootElement.GetProperty("character_id").GetUInt32())
            .IsEqualTo(runtime.CharacterId);

        await Assert.That(on).IsEqualTo(off)
            .Because("a flag-on wake must not perturb the observe string");
    }

    /// <summary>
    /// Absent brains stay null: a quest-only wake carries no per-brain rows,
    /// and the v1 contract fields are unchanged.
    /// </summary>
    [Test]
    public async Task BuildRecord_NoBrains_AllBrainRowsNull()
    {
        var record = JevShadow.BuildRecord(42, "quest-42-7", "DECIDE: empty", FailResult());

        await Assert.That(record.Travel).IsNull();
        await Assert.That(record.Survival).IsNull();
        await Assert.That(record.Needs).IsNull();
        await Assert.That(record.Combat).IsNull();
        await Assert.That(record.Schema).IsEqualTo(JevShadow.SchemaVersion);
        await Assert.That(record.Verdict).IsEqualTo("DECIDE");
    }

    /// <summary>
    /// Present brains are value-copied into the row: travel + survival frozen
    /// inputs land with their scalar facts intact.
    /// </summary>
    [Test]
    public async Task BuildRecord_TravelAndSurvivalPresent_CarriesFrozenInputs()
    {
        var travel = new TravelBrainInputs(
            ActorObjId: 9,
            SelfPosition: new Vector3(1, 2, 3),
            TargetKind: TravelTargetKind.Position,
            TargetObjId: 0,
            Destination: new Vector3(10, 20, 0),
            DistanceM: 25.5f,
            ArrivalRadiusM: 3,
            LocalModeMaxM: 50,
            FollowRequested: false,
            DestWorldKnown: true,
            ActorWorldId: 1,
            ActorInstanceId: 0,
            DestWorldId: 1,
            DestInstanceId: 0,
            TargetResolved: true,
            PriorResolveAttempts: 0,
            LegOutcome: TravelLegOutcome.Running,
            LegLive: true,
            LegTargetObjId: 0,
            LegDestinationKnown: true,
            LegDestination: new Vector3(10, 20, 0),
            PriorRepathCount: 1,
            RepathBudget: 3,
            RouteAvailable: false,
            RouteWaypoint: new Vector3(10, 20, 0),
            RouteWaypointCount: 0,
            PriorMode: TravelMode.Local,
            RetreatRequested: false,
            ThreatObjId: 0,
            ThreatPosition: Vector3.Zero);
        var survival = new SurvivalBrainInputs(
            ActorObjId: 9,
            SelfHpRatio: 0.2f,
            CombatRetreatPublished: true,
            CommittedTargetObjId: 77,
            SelectedTargetObjId: 77,
            SelfPosition: new Vector3(1, 2, 3),
            ThreatPosition: new Vector3(4, 5, 6));
        var brains = new JevShadowBrains(Travel: travel, Survival: survival);

        var record = JevShadow.BuildRecord(9, "quest-9-1", "DECIDE: empty", FailResult(), brains);

        await Assert.That(record.Travel).IsNotNull();
        await Assert.That(record.Travel!.TargetKind).IsEqualTo("Position");
        await Assert.That(record.Travel!.DistanceM).IsEqualTo(25.5f);
        await Assert.That(record.Travel!.PriorRepathCount).IsEqualTo(1);
        await Assert.That(record.Travel!.Destination.X).IsEqualTo(10f);
        await Assert.That(record.Survival).IsNotNull();
        await Assert.That(record.Survival!.SelfHpRatio).IsEqualTo(0.2f);
        await Assert.That(record.Survival!.CommittedTargetObjId).IsEqualTo(77u);
        await Assert.That(record.Needs).IsNull();
        await Assert.That(record.Combat).IsNull();
    }

    /// <summary>
    /// Present brains are value-copied into the row: needs + combat frozen
    /// inputs land with their scalar and census facts intact.
    /// </summary>
    [Test]
    public async Task BuildRecord_NeedsAndCombatPresent_CarriesFrozenInputs()
    {
        var needs = new NeedsBrainInputs(
            ActorObjId: 9,
            SelfPosition: new Vector3(1, 2, 3),
            SoilReadable: true,
            SeedReadable: true,
            SeedCount: 5,
            OnValidSoil: false,
            CropState: NeedsCropState.Live,
            CropMature: true,
            CropDistanceM: 2.5f,
            CropPosition: new Vector3(7, 8, 0),
            CropEnRoute: false,
            CropObjId: 123,
            CropTemplateId: 456,
            CropApproachAttempts: 1,
            MaxCropApproachAttempts: 3,
            HarvestJustLanded: false,
            SoilEnRoute: false,
            SoilResolved: true,
            SoilDestination: new Vector3(11, 12, 0),
            SoilAttempts: 0,
            MaxSoilAttempts: 3,
            HarvestRangeM: 3);
        var combatInputs = new CombatBrainInputs(
            ActorObjId: 9,
            Role: CombatRole.Melee,
            Alive: true,
            SelfHpRatio: 0.9f,
            SelfLevel: 10,
            SelfPosition: new Vector3(1, 2, 3),
            EnemyCount: 1,
            NearestEnemyDistanceM: 5,
            TookDamageThisFrame: false,
            CrowdControlled: false,
            CcAvailable: false,
            IsAutoAttackLive: true,
            HealItemTemplateId: 0,
            SkillMinRangeM: 0,
            SkillMaxRangeM: 3.5f,
            SelectedSkillId: 0,
            LastSkillUsed: 0,
            IncumbentObjId: 55,
            IncumbentValid: true,
            IncumbentUnknown: false,
            IncumbentScore: 42,
            CommitmentInForce: true,
            IncumbentPosition: new Vector3(6, 6, 0),
            IncumbentDistanceM: 5,
            IncumbentLeashDriftM: 1,
            LeashBudgetM: 50,
            Candidates:
            [
                new CombatCandidate(55, 10, 5, 10, 0.8f, true, 1)
            ],
            NowUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var combat = new CombatBrainPlanner.Prepared(combatInputs, 55, 1, 0, 0);
        var brains = new JevShadowBrains(Needs: needs, Combat: combat);

        var record = JevShadow.BuildRecord(9, "quest-9-2", "DECIDE: empty", FailResult(), brains);

        await Assert.That(record.Needs).IsNotNull();
        await Assert.That(record.Needs!.SeedCount).IsEqualTo(5);
        await Assert.That(record.Needs!.CropState).IsEqualTo("Live");
        await Assert.That(record.Needs!.CropObjId).IsEqualTo(123u);
        await Assert.That(record.Combat).IsNotNull();
        await Assert.That(record.Combat!.IncumbentObjId).IsEqualTo(55u);
        await Assert.That(record.Combat!.CandidateCount).IsEqualTo(1);
        await Assert.That(record.Combat!.CommittedQuestTarget).IsEqualTo(55u);
        await Assert.That(record.Combat!.Candidates.Count).IsEqualTo(1);
        await Assert.That(record.Combat!.Candidates[0].ObjId).IsEqualTo(55u);
        await Assert.That(record.Travel).IsNull();
        await Assert.That(record.Survival).IsNull();
    }

    /// <summary>
    /// The writer emits present brain rows as snake_case JSONL (the lane replay
    /// feed) and omits absent ones as nulls.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_BrainsPresent_WritesBrainRows()
    {
        var survival = new SurvivalBrainInputs(
            ActorObjId: 7,
            SelfHpRatio: 0.5f,
            CombatRetreatPublished: false,
            CommittedTargetObjId: 0,
            SelectedTargetObjId: 0,
            SelfPosition: Vector3.Zero,
            ThreatPosition: Vector3.Zero);

        JevShadow.MaybeEmitQuest(7, "quest-7-9", "DECIDE: empty",
            FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")),
            new JevShadowBrains(Survival: survival));

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("survival").ValueKind).IsNotEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("survival").GetProperty("committed_target_obj_id").GetUInt32()).IsEqualTo(0u);
        await Assert.That(root.GetProperty("travel").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("combat").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    /// <summary>
    /// The bounded queue drops the NEWEST line when full (never blocks the
    /// tick, never evicts): with a one-slot queue and the writer paused, the
    /// first wake is kept and the burst tail is dropped and counted.
    /// </summary>
    [Test]
    public async Task Enqueue_PausedQueueFull_DropsNewestCountsDrops()
    {
        JevShadow.MaxQueuedLines = 1;
        JevShadow.SetWriterPausedForTest(true);
        try
        {
            JevShadow.MaybeEmitQuest(7, "quest-7-1", "DECIDE: empty",
                FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));
            for (var wake = 2; wake <= 4; wake++)
                JevShadow.MaybeEmitQuest(7, $"quest-7-{wake}", "DECIDE: empty",
                    FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));

            await Assert.That(JevShadow.QueuedLineCount).IsEqualTo(1);
            await Assert.That(JevShadow.DroppedLineCount).IsEqualTo(3);
        }
        finally
        {
            JevShadow.SetWriterPausedForTest(false);
        }
        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0].Contains("quest-7-1")).IsTrue();
    }

    /// <summary>
    /// The background writer drains FIFO without tick-side I/O: a burst of
    /// sampled wakes lands in emission order once the writer resumes.
    /// </summary>
    [Test]
    public async Task BackgroundWriter_DrainsQueuedLinesInOrder()
    {
        JevShadow.MaxQueuedLines = 16;
        JevShadow.SetWriterPausedForTest(true);
        try
        {
            for (var wake = 1; wake <= 3; wake++)
                JevShadow.MaybeEmitQuest(7, $"quest-7-{wake}", "DECIDE: empty",
                    FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));
            await Assert.That(JevShadow.QueuedLineCount).IsEqualTo(3);
            await Assert.That(File.Exists(JevShadow.FilePath)).IsFalse();
        }
        finally
        {
            JevShadow.SetWriterPausedForTest(false);
        }

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[0].Contains("quest-7-1")).IsTrue();
        await Assert.That(lines[1].Contains("quest-7-2")).IsTrue();
        await Assert.That(lines[2].Contains("quest-7-3")).IsTrue();
    }

    /// <summary>
    /// Flag-off stays zero-alloc: a disabled wake performs a static flag
    /// read + return without touching the record, JSON, or queue seam.
    ///
    /// <para>
    /// MEASUREMENT NOTE (this revision). This asserted
    /// <c>allocated IsEqualTo(0)</c> measured inline on the TUnit test
    /// thread. GC.GetAllocatedBytesForCurrentThread is per-thread, so ANY
    /// unrelated allocation the runner performed on that thread during the
    /// 10k-iteration window landed in the delta — a zero-tolerance budget on
    /// a shared thread is a flake generator, not a pin. The measurement now
    /// runs on a dedicated thread whose only work is the loop under test.
    /// </para>
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_Disabled_ZeroAlloc()
    {
        JevShadow.Enabled = false;
        // Built ONCE outside the probe: constructing the run result allocates,
        // and measuring the fixture would report a regression that is not one.
        var result = FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope"));

        var allocated = MeasureOnDedicatedThread(() =>
        {
            // The first call on a cold thread carries a FIXED one-time cost
            // (JIT stub hoisting: measured 568 bytes on the very first
            // invocation, 0 on every subsequent one). That is not the contract
            // under test — the contract is the STEADY-STATE per-wake cost of a
            // disabled wake, which the warm loop below establishes. Sampling
            // before the settle would fold the one-time cost into the delta and
            // make the pin unmeetable, which is exactly what the previous
            // inline reading did (192 bytes attributed to 10k wakes).
            for (var i = 0; i < 1_000; i++)
                JevShadow.MaybeEmitQuest(7, "quest-7-1", "DECIDE: empty", result);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
                JevShadow.MaybeEmitQuest(7, "quest-7-1", "DECIDE: empty", result);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        await Assert.That(allocated).IsEqualTo(0)
            .Because($"10k disabled wakes must allocate nothing at all; saw {allocated} bytes");
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh dedicated thread and returns
    /// its value. <paramref name="body"/> is responsible for sampling
    /// GC.GetAllocatedBytesForCurrentThread itself — the body runs entirely on
    /// that thread, so the counter it reads covers only the loop under test. A
    /// dedicated thread (rather than a pool thread) guarantees nothing else can
    /// allocate inside the window.
    /// </summary>
    private static long MeasureOnDedicatedThread(Func<long> body)
    {
        long allocated = -1;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                allocated = body();
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "jev-shadow-alloc-probe",
        };
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("allocation probe did not finish within 2 minutes");
        failure?.Throw();
        return allocated;
    }

    // ------------------------------------------------------------------
    // L2 harvest repairs: the four brain blocks must reach the dataset, the
    // incumbent must carry its own score row, the needs class must emit, and
    // the disabled path must stay free.
    // ------------------------------------------------------------------

    private static SurvivalBrainInputs SurvivalInputs(uint actorObjId) => new(
        ActorObjId: actorObjId,
        SelfHpRatio: 0.42f,
        CombatRetreatPublished: false,
        CommittedTargetObjId: 0,
        SelectedTargetObjId: 0,
        SelfPosition: Vector3.Zero,
        ThreatPosition: Vector3.Zero);

    private static NeedsBrainInputs NeedsInputs(uint actorObjId) => new(
        ActorObjId: actorObjId,
        SelfPosition: Vector3.Zero,
        SoilReadable: true,
        SeedReadable: true,
        SeedCount: 5,
        OnValidSoil: false,
        CropState: NeedsCropState.Live,
        CropMature: true,
        CropDistanceM: 2.5f,
        CropPosition: new Vector3(7, 8, 0),
        CropEnRoute: false,
        CropObjId: 123,
        CropTemplateId: 456,
        CropApproachAttempts: 1,
        MaxCropApproachAttempts: 3,
        HarvestJustLanded: false,
        SoilEnRoute: false,
        SoilResolved: true,
        SoilDestination: new Vector3(11, 12, 0),
        SoilAttempts: 0,
        MaxSoilAttempts: 3,
        HarvestRangeM: 3);

    private static CombatBrainPlanner.Prepared CombatPrepared(uint actorObjId)
    {
        var inputs = new CombatBrainInputs(
            ActorObjId: actorObjId,
            Role: CombatRole.Melee,
            Alive: true,
            SelfHpRatio: 0.9f,
            SelfLevel: 10,
            SelfPosition: Vector3.Zero,
            EnemyCount: 1,
            NearestEnemyDistanceM: 5,
            TookDamageThisFrame: false,
            CrowdControlled: false,
            CcAvailable: false,
            IsAutoAttackLive: true,
            HealItemTemplateId: 0,
            SkillMinRangeM: 0,
            SkillMaxRangeM: 3.5f,
            SelectedSkillId: 0,
            LastSkillUsed: 0,
            IncumbentObjId: 55,
            IncumbentValid: true,
            IncumbentUnknown: false,
            IncumbentScore: 42,
            CommitmentInForce: true,
            IncumbentPosition: new Vector3(6, 6, 0),
            IncumbentDistanceM: 5,
            IncumbentLeashDriftM: 1,
            LeashBudgetM: 50,
            Candidates: [new CombatCandidate(55, 10, 5, 10, 0.8f, true, 1)],
            NowUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return new CombatBrainPlanner.Prepared(inputs, 55, 1, 0, 0);
    }

    private static TravelBrainInputs TravelInputs(uint actorObjId) => new(
        ActorObjId: actorObjId,
        TargetKind: TravelTargetKind.Position,
        TargetObjId: 0,
        SelfPosition: Vector3.Zero,
        Destination: new Vector3(50, 0, 0),
        DistanceM: 50f,
        ArrivalRadiusM: 3f,
        LocalModeMaxM: 60f,
        FollowRequested: false,
        DestWorldKnown: true,
        ActorWorldId: 100,
        ActorInstanceId: 0,
        DestWorldId: 100,
        DestInstanceId: 0,
        TargetResolved: true,
        PriorResolveAttempts: 0,
        LegOutcome: TravelLegOutcome.None,
        LegLive: false,
        LegTargetObjId: 0,
        LegDestinationKnown: false,
        LegDestination: Vector3.Zero,
        PriorRepathCount: 0,
        RepathBudget: 2,
        RouteAvailable: false,
        RouteWaypoint: new Vector3(50, 0, 0),
        RouteWaypointCount: 0,
        PriorMode: TravelMode.None,
        RetreatRequested: false,
        ThreatObjId: 0,
        ThreatPosition: Vector3.Zero);

    /// <summary>
    /// DEFECT 1 regression: the wake-scoped capture must deliver all four brain
    /// blocks into the emitted row. Before the repair the live call site passed
    /// the 4-arg overload (brains = null), so every harvested row serialized all
    /// four blocks as null and no brain outside quest arbitration was
    /// harvestable at all.
    /// </summary>
    [Test]
    public async Task CaptureThenEmit_CarriesAllFourBrainBlocks()
    {
        const uint actor = 77;
        JevShadow.BeginWake(actor);
        JevShadow.CaptureTravel(actor, TravelInputs(actor), default);
        JevShadow.CaptureSurvival(actor, SurvivalInputs(actor), default);
        JevShadow.CaptureNeeds(actor, NeedsInputs(actor), default);
        JevShadow.CaptureCombat(actor, CombatPrepared(actor));

        var brains = JevShadow.TakeBrains(actor);
        await Assert.That(brains).IsNotNull();

        var record = JevShadow.BuildRecord(actor, "quest-77-1", "landed Observe", FailResult(), brains);

        await Assert.That(record.Travel).IsNotNull();
        await Assert.That(record.Travel!.DistanceM).IsEqualTo(50f);
        await Assert.That(record.Survival).IsNotNull();
        await Assert.That(record.Survival!.SelfHpRatio).IsEqualTo(0.42f);
        await Assert.That(record.Needs).IsNotNull();
        await Assert.That(record.Needs!.SeedCount).IsEqualTo(5);
        await Assert.That(record.Combat).IsNotNull();
        await Assert.That(record.Combat!.IncumbentObjId).IsEqualTo(55u);
    }

    /// <summary>
    /// The wake-scoped slots are per-wake, not per-process: a brain that did not
    /// run this wake must read absent rather than inherit the previous wake's
    /// inputs. Without the reset a stale row would silently feed the replay.
    /// </summary>
    [Test]
    public async Task BeginWake_ClearsThePreviousWakesBrainSlots()
    {
        const uint actor = 78;
        JevShadow.BeginWake(actor);
        JevShadow.CaptureSurvival(actor, SurvivalInputs(actor), default);
        await Assert.That(JevShadow.TakeBrains(actor)?.Survival).IsNotNull();

        // Next wake: the survival gate found nothing to veto, so it captured
        // nothing and the row must not inherit wake 1's inputs.
        JevShadow.BeginWake(actor);
        await Assert.That(JevShadow.TakeBrains(actor)).IsNull();
    }

    /// <summary>
    /// DEFECT 2 regression: the incumbent's OWN score row must appear on the row.
    /// Before the repair only the rejected candidates carried priority/weight
    /// (they were built from the rejection list), so a challenger re-ranking the
    /// set could not score the option the game brain actually took — the
    /// comparison was structurally impossible.
    /// </summary>
    [Test]
    public async Task BuildRecord_SelectedProposal_CarriesTheIncumbentsScoreRow()
    {
        var rejected = Proposal("quest.accept", 10, 2, "not-in-band");
        var winner = Proposal("quest.advance", 30, 7, "never-fires");
        var result = new QuestDecisionScenario.QuestRunResult
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = true,
            SelectedAction = ActorActionType.Observe,
            SelectedProposal = winner,
            Request = null,
            Rejections = [new BotProposalRejection(rejected, "precondition 'not-in-band' was not satisfied")],
            Explanation = "selected quest.advance/Observe",
            TraceRecords = []
        };

        var record = JevShadow.BuildRecord(11, "quest-11-1", "landed Observe", result);

        await Assert.That(record.SelectedCandidate).IsNotNull();
        await Assert.That(record.SelectedCandidate!.Goal).IsEqualTo("quest.advance");
        await Assert.That(record.SelectedCandidate!.Action).IsEqualTo(ActorActionType.Observe.ToString());
        await Assert.That(record.SelectedCandidate!.Priority).IsEqualTo(30);
        await Assert.That(record.SelectedCandidate!.Weight).IsEqualTo(7);
        // Same candidate set, same scoring fields: the incumbent is now
        // scoreable against the rejected rows on identical terms.
        await Assert.That(record.Candidates.Count).IsEqualTo(1);
        await Assert.That(record.SelectedCandidate!.Priority > record.Candidates[0].Priority).IsTrue();
    }

    /// <summary>
    /// DEFECT 2 regression at the wire: the selected row must serialize, so an
    /// offline replay sees it. The schema stays additive — every pre-existing
    /// key keeps its exact name and shape.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_SelectedProposal_SerializesTheIncumbentRow()
    {
        var rejected = Proposal("quest.accept", 10, 2, "not-in-band");
        var winner = Proposal("quest.advance", 30, 7, "never-fires");
        var result = new QuestDecisionScenario.QuestRunResult
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = true,
            SelectedAction = ActorActionType.Observe,
            SelectedProposal = winner,
            Rejections = [new BotProposalRejection(rejected, "gate")],
            Explanation = "selected quest.advance",
            TraceRecords = []
        };

        JevShadow.MaybeEmitQuest(12, "quest-12-1", "landed Observe", result);

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var selected = doc.RootElement.GetProperty("selected_candidate");
        await Assert.That(selected.GetProperty("goal").GetString()).IsEqualTo("quest.advance");
        await Assert.That(selected.GetProperty("priority").GetInt32()).IsEqualTo(30);
        await Assert.That(selected.GetProperty("weight").GetInt32()).IsEqualTo(7);
        // The v1 keys are untouched.
        await Assert.That(doc.RootElement.GetProperty("candidates").GetArrayLength()).IsEqualTo(1);
        await Assert.That(doc.RootElement.GetProperty("schema").GetString()).IsEqualTo(JevShadow.SchemaVersion);
    }

    /// <summary>
    /// DEFECT 3 regression: the needs class runs its OWN decision cycle, which the
    /// quest emit point never observes. Without a needs emit point it contributes
    /// zero rows and the whole class is unmeasurable.
    /// </summary>
    [Test]
    public async Task MaybeEmitNeeds_WritesTheNeedsArbitrationRow()
    {
        const uint actor = 21;
        JevShadow.BeginWake(actor);
        JevShadow.CaptureNeeds(actor, NeedsInputs(actor), default);

        var winner = Proposal("needs.plant", 30, 0, "never-fires");
        var rejected = Proposal("needs.rest", 40, 0, "seed-unreadable");
        var needsResult = new NeedsDecisionScenario.NeedsRunResult
        {
            Scenario = NeedsDecisionScenario.ScenarioName,
            WorkSelected = true,
            DominantNeed = BotNeedKind.Food,
            Urgency = 0.8,
            SelectedAction = ActorActionType.Plant,
            SelectedProposal = winner,
            Rejections = [new BotProposalRejection(rejected, "seed unreadable")],
            Explanation = "selected needs.plant/Plant",
            TraceRecords = []
        };

        JevShadow.MaybeEmitNeeds(actor, needsResult);

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("scenario").GetString()).IsEqualTo(NeedsDecisionScenario.ScenarioName);
        await Assert.That(root.GetProperty("selected_action").GetString()).IsEqualTo(ActorActionType.Plant.ToString());
        await Assert.That(root.GetProperty("selected_candidate").GetProperty("goal").GetString()).IsEqualTo("needs.plant");
        // The captured needs-brain inputs travel with the row.
        await Assert.That(root.GetProperty("needs").ValueKind).IsNotEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("needs").GetProperty("seed_count").GetInt32()).IsEqualTo(5);
    }

    /// <summary>
    /// The disabled flag must produce NO row for the new needs entry point either
    /// — the flag-off early return is the whole allocation contract.
    /// </summary>
    [Test]
    public async Task MaybeEmitNeeds_Disabled_WritesNothing()
    {
        JevShadow.Enabled = false;
        var needsResult = new NeedsDecisionScenario.NeedsRunResult
        {
            Scenario = NeedsDecisionScenario.ScenarioName,
            WorkSelected = false,
            Rejections = [],
            TraceRecords = []
        };

        JevShadow.MaybeEmitNeeds(31, needsResult);

        await Assert.That(File.Exists(JevShadow.FilePath)).IsFalse();
    }

    /// <summary>
    /// THE ALLOCATION CONTRACT. Every new harvest entry point and every new
    /// capture call must be inert while the flag is off: a disabled capture is a
    /// static flag read and a return, so a caller that forgets its own guard
    /// cannot put a dictionary write or a record allocation on the calm wake.
    /// Measured the same way BotRoamAllocationTests measures the roam step.
    /// </summary>
    [Test]
    public async Task Disabled_CaptureAndEmit_AllocateNothing()
    {
        JevShadow.Enabled = false;
        const uint actor = 88;

        // Every fixture is built ONCE, outside the measured loop: constructing a
        // TravelBrainInputs/NeedsBrainInputs/CombatBrainPlanner.Prepared allocates
        // (the combat candidate array, the result's TraceRecords list and
        // CompletedQuestIds array), and measuring the FIXTURE instead of the code
        // under test would report a regression that is not one.
        var travel = TravelInputs(actor);
        var survival = SurvivalInputs(actor);
        var needs = NeedsInputs(actor);
        var combat = CombatPrepared(actor);
        var questResult = FailResult();

        // Same dedicated-thread discipline as MaybeEmitQuest_Disabled_ZeroAlloc:
        // an inline GetAllocatedBytesForCurrentThread reading on the shared TUnit
        // test thread attributes any unrelated runner allocation to the calm-wake
        // path, so a zero-tolerance budget there is a flake, not a pin.
        var allocated = MeasureOnDedicatedThread(() =>
        {
            void OneWake()
            {
                JevShadow.BeginWake(actor);
                JevShadow.CaptureTravel(actor, travel, default);
                JevShadow.CaptureSurvival(actor, survival, default);
                JevShadow.CaptureNeeds(actor, needs, default);
                JevShadow.CaptureCombat(actor, combat);
                _ = JevShadow.TakeBrains(actor);
                JevShadow.MaybeEmitNeeds(actor, null);
                JevShadow.MaybeEmitQuest(actor, "quest-88-1", "", questResult);
            }

            // Warm the JIT so the measurement is steady-state, not first-call cost.
            for (var i = 0; i < 2_000; i++)
                OneWake();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 2_000; i++)
                OneWake();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        await Assert.That(allocated).IsEqualTo(0)
            .Because($"a disabled wake must be a static flag read and a return (saw {allocated} bytes over 10k wakes)");
    }

    /// <summary>
    /// BUG-1 REGRESSION: the wake-slot bound must not destroy another actor's
    /// in-flight capture.
    ///
    /// The original bound called <c>WakeInputs.Clear()</c> the moment the table
    /// reached its cap, which wiped EVERY actor's captured slots — including bots
    /// that were mid-wake. The symptom was silent: rows with null
    /// travel/survival/needs/combat blocks and nothing to explain it. At 512 bots
    /// (an ordinary soak size) that cap is reached in normal operation, not at
    /// the edge.
    ///
    /// This drives well past the former 256 cap and asserts every actor's row
    /// still carries ITS OWN inputs, with each actor's own marker value so a
    /// cross-actor bleed would be visible rather than merely a null.
    /// </summary>
    [Test]
    public async Task WakeSlotBound_PastFormerCap_EveryActorKeepsItsOwnBrainInputs()
    {
        // Distinct actors driven past the REAL cap. WakeSlotMemoryBound is not
        // exposed to tests, so this deliberately overshoots it by a wide margin:
        // the old (broken) bound was 256, the current one is 4096, and an
        // earlier draft of this test used 600 — which was silently VACUOUS,
        // because 600 never reaches 4096 so the cap branch never executed and
        // the test passed even with the destructive Clear() reinstated. 6000 is
        // far beyond any cap this code will plausibly carry, so the cap path is
        // guaranteed to be exercised for real.
        //
        // Each actor is captured then emitted and released, so a correct
        // per-wake release keeps the population flat no matter how many actors
        // pass through; a table-wide clear would destroy the slots of every
        // actor currently mid-wake.
        const int actors = 6000;
        // The cap can ONLY be reached by actors held MID-WAKE SIMULTANEOUSLY.
        // A sequential loop that emits and releases each actor keeps the table
        // near-empty, so it never touches the cap branch at all (measured: 0
        // executions) and would pass even with the destructive Clear() back. So
        // the test now parks MORE concurrent mid-wake actors than the cap allows
        // and drives the last ones over it. That is the only shape in which the
        // cap is reachable — and therefore the only shape that can catch Bug 1.
        //
        // 5000 concurrent mid-wake actors > WakeSlotMemoryBound (4096).
        const uint concurrent = 5000;
        for (var held = 1u; held <= concurrent; held++)
        {
            JevShadow.BeginWake(held);
            JevShadow.CaptureNeeds(held, NeedsInputsWithSeedCount(held, (int)(held % 4093)), default);
        }

        // Every actor parked BEFORE the cap was reached must still hold its own
        // inputs. Under the old table-wide Clear() the crossing wipes them all.
        for (var held = 1u; held <= 8u; held++)
        {
            await Assert.That(JevShadow.TakeBrains(held)?.Needs).IsNotNull()
                .Because($"actor {held} was mid-wake when the wake-slot cap was crossed by " +
                         $"actor {concurrent}; a table-wide Clear() would have destroyed its " +
                         $"in-flight capture (this is the silent-corruption bug)");
            await Assert.That(JevShadow.TakeBrains(held)!.Needs!.Value.SeedCount).IsEqualTo((int)(held % 4093))
                .Because($"actor {held} must still carry ITS OWN inputs, not another's");
        }
        for (var held = 1u; held <= concurrent; held++)
            JevShadow.ReleaseWake(held);

        // The writer queue is BOUNDED (MaxQueuedLines=1024, drop-newest by
        // design), so a 5 900-row burst in one tick would legitimately drop most
        // rows. That is the queue's contract, not a wake-slot bug, so the loop
        // drains the writer at the batch boundary instead of racing it. The
        // invariant under test is the WAKE SLOT, not the writer.
        const int batch = 500;
        for (var start = 100u; start <= actors; start += batch)
        {
            for (var i = start; i < start + batch && i <= actors; i++)
            {
                JevShadow.BeginWake(i);
                // A per-actor marker: the needs SeedCount is actor-specific, so a
                // row carrying the WRONG actor's inputs is detectable, not just
                // non-null.
                JevShadow.CaptureNeeds(i, NeedsInputsWithSeedCount(i, (int)(i % 97)), default);
                JevShadow.MaybeEmitQuest(i, $"quest-{i}-1", "landed Observe", FailResult(),
                    JevShadow.TakeBrains(i));
                JevShadow.ReleaseWake(i);
            }
            await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(30))).IsTrue();
        }


        // The emitted file must hold one row per looping actor, each with its own
        // inputs. (The 8 mid-wake actors emit no row — they never emitted.)
        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(60))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        var expected = actors - 99;   // ids 100..6000 inclusive
        await Assert.That(lines.Length).IsEqualTo(expected);

        var byActor = new Dictionary<uint, int>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var actor = root.GetProperty("character_id").GetUInt32();
            // Non-null is the core assertion: the old clear-all produced nulls.
            await Assert.That(root.GetProperty("needs").ValueKind).IsNotEqualTo(JsonValueKind.Null)
                .Because($"actor {actor} lost its captured needs inputs (silent clear-all regression)");
            byActor[actor] = root.GetProperty("needs").GetProperty("seed_count").GetInt32();
        }
        await Assert.That(byActor.Count).IsEqualTo(expected);
        // No cross-actor bleed: each row's marker matches its own actor id.
        foreach (var (actor, seedCount) in byActor)
        {
            await Assert.That(seedCount).IsEqualTo((int)(actor % 97))
                .Because($"actor {actor} carried another actor's frozen inputs");
        }
    }

    /// <summary>
    /// The cap is a backstop, not a silent corruption: with more simultaneous
    /// MID-WAKE actors than the cap, the new actor's capture is refused and
    /// COUNTED, while every already-capturing actor keeps its inputs. This is the
    /// correct-by-construction half of the fix.
    /// </summary>
    [Test]
    public async Task WakeSlotBound_OverCapMidWake_RefusesNewActorAndCountsIt()
    {
        // Fill past the cap with actors that never finish their wake, so no
        // finished wake exists to evict.
        const uint cap = 4096;
        for (var i = 1u; i <= cap; i++)
        {
            JevShadow.BeginWake(i);
            // Give EVERY filling actor real captured inputs, so the survival check
            // at the end proves those in-flight captures are intact.
            JevShadow.CaptureNeeds(i, NeedsInputsWithSeedCount(i, (int)(i % 97)), default);
        }
        await Assert.That(JevShadow.WakeSlotCount).IsEqualTo((int)cap);

        // One more actor, mid-wake, with no evictable entry: every attempt to bind
        // a slot past the cap is refused AND counted. The counter is process-wide
        // (Interlocked), so assert the delta this actor contributes: its BeginWake
        // and its CaptureNeeds are two refused binds, and neither is silent.
        var before = JevShadow.ForcedEvictionCount;
        JevShadow.BeginWake(cap + 1);
        JevShadow.CaptureNeeds(cap + 1, NeedsInputsWithSeedCount(cap + 1, 7), default);
        var refusals = JevShadow.ForcedEvictionCount - before;
        await Assert.That(refusals).IsEqualTo(2);
        await Assert.That(JevShadow.TakeBrains(cap + 1)).IsNull();

        // The already-capturing actors are untouched — the bug this replaced
        // would have cleared all of them.
        await Assert.That(JevShadow.WakeSlotCount).IsEqualTo((int)cap);
        await Assert.That(JevShadow.TakeBrains(1)?.Needs).IsNotNull();
        await Assert.That(JevShadow.TakeBrains(cap)?.Needs).IsNotNull();
    }

    /// <summary>
    /// EXCEPTION-PATH RECLAIM: a wake whose leg THREW never reaches the
    /// end-of-wake release (it is the last statement of <c>StepAsync</c>, and the
    /// scheduler's catch swallows the fault upstream). The slot must therefore be
    /// reclaimed by the actor's NEXT wake, not merely reset — otherwise a bot
    /// whose last wake crashed keeps its entry forever AND keeps it permanently
    /// un-evictable, because only a sealed wake may be evicted.
    ///
    /// This is the bug this test was written for: with reset-only semantics, the
    /// second assertion (the slot is GONE, not reset) fails.
    /// </summary>
    [Test]
    public async Task BeginWake_ReclaimsTheSlotOfAPreviousWakeThatNeverEmitted()
    {
        const uint actor = 501;
        JevShadow.BeginWake(actor);
        JevShadow.CaptureNeeds(actor, NeedsInputsWithSeedCount(actor, 11), default);
        var record = JevShadow.BuildNeedsRecord(actor,
            new NeedsDecisionScenario.NeedsRunResult
            {
                Scenario = NeedsDecisionScenario.ScenarioName,
                WorkSelected = false,
                Rejections = [],
                TraceRecords = []
            },
            JevShadow.TakeBrains(actor));
        // The crashed wake's own inputs are intact for the row it was building.
        await Assert.That(record.Needs!.SeedCount).IsEqualTo(11);
        // ...and it never sealed, so the end-of-wake release would NOT have run.
        await Assert.That(JevShadow.WakeSlotCount).IsEqualTo(1);

        // The observable difference between "reset in place" and "drop, then
        // rebind" is the SEALED set, not the slot count — both leave one entry.
        // A reset-in-place wake whose id was never sealed is PERMANENTLY
        // un-evictable, so it would pin the table forever once the cap is hit.
        // Dropping clears the seal bookkeeping, which is what makes the entry
        // evictable again. So: seal the crashed wake, then assert the seal is
        // gone after the next BeginWake.
        JevShadow.EndWake(actor);   // as an emit path would have done
        var sealedBefore = JevShadow.SealedWakeCount;
        await Assert.That(sealedBefore).IsEqualTo(1);

        JevShadow.BeginWake(actor);
        await Assert.That(JevShadow.SealedWakeCount).IsEqualTo(0)
            .Because("a new wake must drop the previous wake's seal, or that entry " +
                     "stays un-evictable and pins the table once the cap is reached");
        await Assert.That(JevShadow.TakeBrains(actor)).IsNull();
    }

    /// <summary>
    /// The recording gap this repairs is a fact the wire has to carry, not a
    /// code-shape detail: a proposal that satisfied EVERY hard precondition and
    /// still lost the preference order must reach the dataset with its score,
    /// in an array that cannot be confused with a precondition rejection or
    /// with the incumbent. Driven through the REAL selector, so the test fails
    /// if the alternatives are not collected at the source, and through the
    /// real JSONL writer, so it fails if the field does not survive the wire.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_LegalLoser_SurvivesSerializationDistinctFromRejectionAndIncumbent()
    {
        var illegal = Proposal("quest.accept", 99, 0, "quest-not-active");
        var winner = LegalProposal("quest.turn-in", 30, 7, "reporter-range");
        var legalLoser = LegalProposal("quest.accept", 17, 1, "accept-band");
        // A real (empty) observation: the selector reads recorded state, so the
        // illegal proposal's precondition fails and the other two pass. Nothing
        // about the context is fabricated to produce the alternatives.
        var context = new BotObservedContext { ActorId = 77 };

        var selection = BotDecisionSelector.Select(
            context,
            [illegal, winner, legalLoser],
            includeLegalAlternatives: true);

        await Assert.That(selection.Rejections.Count).IsEqualTo(1)
            .Because("only the hard-precondition failure is a rejection");
        await Assert.That(selection.LegalAlternatives).IsNotNull();
        await Assert.That(selection.LegalAlternatives!.Count).IsEqualTo(1)
            .Because("the legal loser is not a rejection and must be carried separately");

        var result = new QuestDecisionScenario.QuestRunResult
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = true,
            SelectedAction = ActorActionType.Observe,
            SelectedProposal = winner,
            Request = null,
            Rejections = selection.Rejections,
            LegalAlternatives = selection.LegalAlternatives,
            Explanation = selection.Explanation,
            TraceRecords = []
        };

        JevShadow.MaybeEmitQuest(77, "quest-77-1", "landed Observe", result);

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;

        // The three kinds are told apart structurally, not inferred.
        var rejections = root.GetProperty("candidates");
        await Assert.That(rejections.GetArrayLength()).IsEqualTo(1);
        await Assert.That(rejections[0].GetProperty("goal").GetString()).IsEqualTo("quest.accept");
        await Assert.That(rejections[0].GetProperty("reason").GetString()!).Contains("quest-not-active");

        var alternatives = root.GetProperty("legal_alternatives");
        await Assert.That(alternatives.GetArrayLength()).IsEqualTo(1);
        await Assert.That(alternatives[0].GetProperty("priority").GetInt32()).IsEqualTo(17)
            .Because("a legal loser must carry its real score, or a challenger cannot rank it");
        await Assert.That(alternatives[0].GetProperty("weight").GetInt32()).IsEqualTo(1);

        var incumbent = root.GetProperty("selected_candidate");
        await Assert.That(incumbent.GetProperty("priority").GetInt32()).IsEqualTo(30);

        // Truthful count: illegal + legal loser + incumbent.
        await Assert.That(root.GetProperty("candidate_count").GetInt32()).IsEqualTo(3)
            .Because("candidate_count is how many proposals the selector actually saw");
        await Assert.That(root.GetProperty("rejection_count").GetInt32()).IsEqualTo(1);
    }

    /// <summary>
    /// "Collected and there were none" must not serialize the same as "not
    /// collected". A lane that saw the alternatives and found the winner was
    /// the only legal proposal has to say so with an empty array; only a
    /// flag-off wake (never asked) may report null. Collapsing the two would
    /// make a consumer read absence as a claim about the decision.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_CollectedWithNoLegalLoser_SerializesEmptyArrayNotNull()
    {
        var only = LegalProposal("quest.accept", 17, 1, "accept-band");
        var selection = BotDecisionSelector.Select(
            new BotObservedContext { ActorId = 78 },
            [only],
            includeLegalAlternatives: true);

        await Assert.That(selection.LegalAlternatives).IsNotNull();
        await Assert.That(selection.LegalAlternatives!.Count).IsEqualTo(0);

        var result = new QuestDecisionScenario.QuestRunResult
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = true,
            SelectedAction = ActorActionType.Observe,
            SelectedProposal = only,
            Request = null,
            Rejections = selection.Rejections,
            LegalAlternatives = selection.LegalAlternatives,
            Explanation = selection.Explanation,
            TraceRecords = []
        };

        JevShadow.MaybeEmitQuest(78, "quest-78-1", "landed Observe", result);

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);

        await Assert.That(doc.RootElement.GetProperty("legal_alternatives").ValueKind)
            .IsEqualTo(JsonValueKind.Array)
            .Because("collected-with-none is an empty array, not null");
        await Assert.That(doc.RootElement.GetProperty("legal_alternatives").GetArrayLength()).IsEqualTo(0);
        await Assert.That(doc.RootElement.GetProperty("candidate_count").GetInt32()).IsEqualTo(1);
    }

    /// <summary>
    /// REGRESSION — found by a live lane run, not by review. A real harvest row
    /// reported candidate_count 1 while carrying TWO legal proposals (a selected
    /// turn-in and an unselected accept), because the quest leg stamps
    /// ActorRequest.DecisionCandidates as rejections + 1 and the recorder used
    /// to prefer that stamp over the selector's own tally. The count must be
    /// selector truth even when the dispatched request says 1, and the selected
    /// vs legal-but-unselected distinction must survive alongside it.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_RequestStampSaysOne_TwoLegalChoices_StillReportsTwo()
    {
        var winner = LegalProposal("quest.turn-in", 30, 0, "reporter-range");
        var legalLoser = LegalProposal("quest.accept", 17, 1, "accept-band");
        var selection = BotDecisionSelector.Select(
            new BotObservedContext { ActorId = 79 },
            [winner, legalLoser],
            includeLegalAlternatives: true);

        await Assert.That(selection.SelectionCandidateCount).IsEqualTo(2);

        // The exact shape the live run produced: a dispatched request whose
        // stamp is the pre-existing rejections+1 (1), plus a selected proposal.
        var request = new ActorRequest(ActorActionType.Observe, 0, null, 0, null);
        // Exactly what GameplayActor.SetPendingDecision forwards: rejections + 1.
        request.AnnotateDecision("quest.turn-in", "quest-v1", 1, 0, "quest-79-1");
        await Assert.That(request.DecisionCandidates).IsEqualTo(1);

        var result = new QuestDecisionScenario.QuestRunResult
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = true,
            SelectedAction = ActorActionType.Observe,
            SelectedProposal = winner,
            Request = request,
            Rejections = selection.Rejections,
            LegalAlternatives = selection.LegalAlternatives,
            SelectionCandidateCount = selection.SelectionCandidateCount,
            Explanation = selection.Explanation,
            TraceRecords = []
        };

        JevShadow.MaybeEmitQuest(79, "quest-79-1", "landed TurnInQuest", result);

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("candidate_count").GetInt32()).IsEqualTo(2)
            .Because("the selector weighed two legal proposals; the request stamp cannot see the loser");
        // The distinction that makes the number meaningful.
        await Assert.That(root.GetProperty("rejection_count").GetInt32()).IsEqualTo(0)
            .Because("neither choice was hard-rejected — both were legal");
        await Assert.That(root.GetProperty("candidates").GetArrayLength()).IsEqualTo(0);
        await Assert.That(root.GetProperty("legal_alternatives").GetArrayLength()).IsEqualTo(1);
        await Assert.That(root.GetProperty("legal_alternatives")[0].GetProperty("priority").GetInt32())
            .IsEqualTo(17);
        await Assert.That(root.GetProperty("selected_candidate").GetProperty("priority").GetInt32())
            .IsEqualTo(30);
    }

    /// <summary>
    /// The schema cutover is behavioural, so the version string is the contract
    /// and must not drift back. v2 exists because candidate_count changed meaning
    /// (selector truth: rejections + legal losers + winner) and legal_alternatives
    /// was added; a v1 reader would silently misread both. Rows already written as
    /// v1 stay v1 and must never be relabelled.
    /// </summary>
    [Test]
    public async Task SchemaVersion_IsV2_NotTheV1WhoseCandidateCountMeantRejectionsPlusOne()
    {
        await Assert.That(JevShadow.SchemaVersion).IsEqualTo("jev-shadow/v2");

        // The v2-only field must actually be emitted, or the bump would be a lie.
        var winner = LegalProposal("quest.turn-in", 30, 0, "reporter-range");
        var loser = LegalProposal("quest.accept", 17, 1, "accept-band");
        var selection = BotDecisionSelector.Select(
            new BotObservedContext { ActorId = 80 },
            [winner, loser],
            includeLegalAlternatives: true);
        var result = new QuestDecisionScenario.QuestRunResult
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = true,
            SelectedAction = ActorActionType.Observe,
            SelectedProposal = winner,
            Request = null,
            Rejections = selection.Rejections,
            LegalAlternatives = selection.LegalAlternatives,
            SelectionCandidateCount = selection.SelectionCandidateCount,
            Explanation = selection.Explanation,
            TraceRecords = []
        };

        JevShadow.MaybeEmitQuest(80, "quest-80-1", "landed Observe", result);

        await Assert.That(JevShadow.WaitForDrainForTest(TimeSpan.FromSeconds(10))).IsTrue();
        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(1);
        using var doc = JsonDocument.Parse(lines[0]);
        await Assert.That(doc.RootElement.GetProperty("schema").GetString()).IsEqualTo("jev-shadow/v2");
        await Assert.That(doc.RootElement.TryGetProperty("legal_alternatives", out _)).IsTrue();
        await Assert.That(doc.RootElement.GetProperty("candidate_count").GetInt32()).IsEqualTo(2);
    }

    private static NeedsBrainInputs NeedsInputsWithSeedCount(uint actorObjId, int seedCount)
    {
        var baseInputs = NeedsInputs(actorObjId);
        return baseInputs with { SeedCount = seedCount };
    }
}
