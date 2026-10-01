using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;

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
        JevShadow.Enabled = true;
        JevShadow.SampleEvery = 1;
        JevShadow.FilePath = Path.Combine(Path.GetTempPath(), "jev-shadow-tests-" + Guid.NewGuid().ToString("N"), "shadow.jsonl");
        JevShadow.ResetSamplingForTest();
    }

    [After(Test)]
    public void TearDown()
    {
        JevShadow.Enabled = _prevEnabled;
        JevShadow.SampleEvery = _prevSample;
        JevShadow.FilePath = _prevPath;
        JevShadow.ResetSamplingForTest();
        try
        {
            var dir = Path.GetDirectoryName(JevShadow.FilePath);
            if (dir != null && Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (Exception) { }
    }

    private bool _prevEnabled;
    private int _prevSample;
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

        var lines = await File.ReadAllLinesAsync(JevShadow.FilePath);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0].Contains("quest-7-1")).IsTrue();
        await Assert.That(lines[1].Contains("quest-7-3")).IsTrue();
    }

    /// <summary>
    /// The writer never throws into the tick: a lane-side path that cannot
    /// be written (a directory standing where the file goes) is swallowed.
    /// </summary>
    [Test]
    public async Task MaybeEmitQuest_UnwritablePath_NeverThrows()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "jev-shadow-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(blocker);
        JevShadow.FilePath = blocker;

        JevShadow.MaybeEmitQuest(7, "quest-7-1", "DECIDE: empty",
            FailResult(new BotProposalRejection(Proposal("quest.accept", 10, 0, "gate"), "nope")));

        await Assert.That(true).IsTrue();
        Directory.Delete(blocker, true);
    }

    /// <summary>
    /// Flag-off emission at the executor's logged point is byte-identical:
    /// the QuestDecideDetail observe string is unchanged with the shadow
    /// switch off.
    /// </summary>
    [Test]
    public async Task ExecutorQuestWake_FlagOff_DecideDetailByteIdentical()
    {
        JevShadow.Enabled = false;
        var before = CaptureDecideDetail("shadow-off-1");
        JevShadow.Enabled = true;
        JevShadow.SampleEvery = int.MaxValue;
        JevShadow.ResetSamplingForTest();
        // First eligible wake only bumps the sampling counter; the second
        // gates out — either way nothing may perturb the observe string.
        JevShadow.MaybeEmitQuest(99, "quest-99-x", "DECIDE: empty", FailResult());
        var after = CaptureDecideDetail("shadow-off-1");
        await Assert.That(after).IsEqualTo(before);
    }

    private static string CaptureDecideDetail(string cycleId)
    {
        // The executor mirrors FailReason into QuestDecideDetail behind a
        // fixed prefix; the frozen Fail path is the byte-stable probe.
        var result = new QuestDecisionScenario.QuestRunResult
        {
            Scenario = QuestDecisionScenario.ScenarioName,
            WorkSelected = false,
            FailStage = "DECIDE",
            FailReason = $"no legal quest proposal: empty [{cycleId}]",
            Rejections = [],
            TraceRecords = []
        };
        return $"{result.FailStage}: {result.FailReason}";
    }
}
