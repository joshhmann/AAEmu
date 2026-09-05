using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M1 Lane-B sweep rigs: the two act defects surfaced by the 2026-08-24
/// stub audit (QuestActEtcItemObtain pattern).
///
/// 1. QuestActConReportJournal wired-noop'd (`|| true` short-circuit) —
///    all 466 live quests gated on it auto-passed the journal gate; the 59
///    without a Progress step were instantly completable on accept. Fixed to
///    mirror QuestActConReportDoodad: passes only after OnReportJournal sets
///    OverrideObjectiveCompleted.
/// 2. QuestActConReportDoodad.FinalizeQuest double-subscribed OnReportDoodad
///    (`+=` in Finalize) — after N quest cycles the handler fired N times
///    per event.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestActReportJournalRigTests
{
    private const string JournalGateShapeManifestJson = """
    {
      "questId": 9311,
      "name": "ConReportJournal gate rig (journal report required)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 30,
        "components": [
          { "kind": "Start", "id": 9531, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1231 } ] },
          { "kind": "Progress", "id": 9532, "acts": [ { "type": "QuestActEtcItemObtain", "itemId": 3950, "count": 2, "cleanup": false, "detailId": 1232 } ] },
          { "kind": "Ready", "id": 9533, "acts": [ { "type": "QuestActConReportJournal", "detailId": 1233 } ] },
          { "kind": "Reward", "id": 9534, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1234 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [
            { "type": "ItemGather", "itemId": 3950, "count": 1 },
            { "type": "ItemGather", "itemId": 3950, "count": 1 }
          ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportJournal" } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    [Test]
    public async Task JournalGate_ObjectivesDone_GatePassesAfterJournalReport_LifecycleCompletes()
    {
        var verdict = RunManifest(JournalGateShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);
        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
    }

    /// <summary>
    /// The gate must actually GATE: objectives fully done (quest reaches
    /// Ready) but NO journal report fired → the step machine holds at Ready
    /// and the quest cannot complete (the pre-fix `|| true` auto-passed this
    /// shape instantly).
    /// </summary>
    [Test]
    public async Task JournalGate_ProgressDoneButNoJournalReport_StallsAtReady()
    {
        var stalled = """
        {
          "questId": 9312,
          "name": "ConReportJournal gate rig (no report -> holds at Ready)",
          "acceptor": { "type": "Npc", "id": 13453 },
          "template": {
            "level": 30,
            "components": [
              { "kind": "Start", "id": 9541, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1241 } ] },
              { "kind": "Progress", "id": 9542, "acts": [ { "type": "QuestActEtcItemObtain", "itemId": 3950, "count": 1, "cleanup": false, "detailId": 1242 } ] },
              { "kind": "Ready", "id": 9543, "acts": [ { "type": "QuestActConReportJournal", "detailId": 1243 } ] }
            ]
          },
          "stages": [
            { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
            { "name": "PROGRESS", "events": [ { "type": "ItemGather", "itemId": 3950, "count": 1 } ], "expect": { "step": "Ready", "status": "Ready" } },
            { "name": "READY-NO-REPORT", "events": [], "expect": { "step": "Ready", "status": "Ready" } }
          ]
        }
        """;
        var verdict = RunManifest(stalled);

        // All declared stage EXPECTATIONS hold (the quest genuinely holds at
        // Ready without the journal report) — that IS the pass condition.
        var readyStage = verdict.Stages.FirstOrDefault(s => s.Stage.Equals("READY-NO-REPORT", StringComparison.OrdinalIgnoreCase));
        await Assert.That(readyStage, "READY-NO-REPORT stage must exist").IsNotNull();
        await Assert.That(readyStage!.Outcome).IsEqualTo(StageOutcome.Pass);
    }

    private static QuestScenarioVerdict RunManifest(string json)
    {
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.Load(json);
        QuestScenarioDriver.RegisterManifestItems(manifest);
        return new QuestScenarioDriver().Run(manifest);
    }
}
