using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// WI-8 (t_fc85a317): QuestActConReportJournal subscription-timing regression pin.
///
/// REAL engine defect found by the T14 band 41-50 census (quest 3630, 모래늪의
/// 무법자들 처치): QuestActConReportJournal subscribed the OnReportJournal
/// handler in InitializeAction (step-ENTRY time), while its siblings
/// QuestActConReportNpc / QuestActConReportDoodad subscribe in InitializeQuest
/// (quest-CONSTRUCTION time, Quest.cs:488 InitializeQuestActs). For
/// let-it-done quests the Progress step is force-blocked (QuestStep.cs:128-129),
/// so the quest NEVER enters Ready — the journal handler was never subscribed,
/// the report event was a no-op, and the quest could never complete.
///
/// Fix: moved the subscription to InitializeQuest/FinalizeQuest (sibling
/// pattern). This pin drives the exact 3630 shape (ltd, MGH objective,
/// journal-only report, copper reward) and asserts the full lifecycle PASS —
/// regression would stall the quest at Progress forever.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestReportJournalLtdRigTests
{
    private const string LtdJournalManifestJson = """
    {
      "questId": 9003,
      "name": "LTD journal-report rig (3630 shape)",
      "letItDone": true,
      "score": 0,
      "acceptor": { "type": "Kill", "id": 1055 },
      "template": {
        "level": 42,
        "components": [
          { "kind": "Start", "id": 9101, "acts": [ { "type": "QuestActConAcceptNpcKill", "npcId": 1055, "detailId": 1001 } ] },
          { "kind": "Progress", "id": 9102, "acts": [ { "type": "QuestActObjMonsterGroupHunt", "monsterGroupId": 328, "count": 32, "detailId": 1002 } ] },
          { "kind": "Ready", "id": 9103, "acts": [ { "type": "QuestActConReportJournal", "detailId": 1003 } ] },
          { "kind": "Reward", "id": 9104, "acts": [ { "type": "QuestActSupplyCopper", "amount": 100, "detailId": 1004 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "MonsterGroupHunt", "npcId": 328, "count": 32 } ], "expect": { "step": "Progress", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportJournal" } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    [Test]
    public async Task LtdJournalQuest_JournalReport_CompletesFullLifecycle()
    {
        var manifest = QuestScenarioManifest.Load(LtdJournalManifestJson);
        QuestScenarioDriver.SeedSingletons();
        QuestScenarioDriver.RegisterManifestItems(manifest);

        var verdict = new QuestScenarioDriver().Run(manifest);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        foreach (var stage in verdict.Stages)
            await Assert.That(stage.Outcome, $"stage {stage.Stage}: {stage.Reason}").IsEqualTo(StageOutcome.Pass);
    }
}
