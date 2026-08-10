using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2 WI-5 (t_d6516324): QuestActObjCompleteQuest harness-closure rig.
///
/// The act's RunAct checks quest.Owner.Quests.HasQuestCompleted(QuestId)
/// (QuestActObjCompleteQuest.cs:26) - a state check at RunAct time, like
/// AbilityLevel/MateLevel: if the referenced quest's completed flag is set
/// AND the objective has not credited yet, SetObjective(quest, 1); the act
/// passes when GetObjective > 0. The harness therefore pre-marks the
/// referenced quest as completed via the driver's synthetic "CompleteQuest"
/// event (fired at the Progress stage before RunCurrentStep), which calls
/// the engine's OWN flag API (CharacterQuests.SetCompletedQuestFlag - the
/// same method the completion path calls; synthetic-block pattern from
/// PlayerbotPilotTests).
///
/// The manifests below mirror the REAL carrier shapes (5814-5821/5862/5868/
/// 5911: Progress complete-quest objectives + report at Ready + item at
/// Reward). Each real carrier references OTHER quests (5814 requires
/// 5815-5819; 5815 requires 5822-5826; ... 5911 requires 5918/5920/5921) -
/// the event carries the act's questId so each reference is pre-marked.
/// Fail-before: without the factory case + ACT_TABLES entry the 11 live
/// carriers are not driven (census SKIP / unsampled). Pass-after: the rig
/// drives both shapes below and the census flips all 11 to PASS.
///
/// Two cases pinned:
///   - AlreadyCompletedShape (mirrors real carrier 5815: two complete-quest
///     Progress objectives referencing 5822/5823): the PROGRESS stage's
///     CompleteQuest events pre-mark both references -> RunAct state checks
///     pass -> full lifecycle PASS.
///   - NotYetShape (same template, NO CompleteQuest events in PROGRESS):
///     the references are never completed -> RunAct returns false -> the
///     quest cannot leave Progress (objective never credits) -> the drive
///     FAILS at the Progress stage. This pins the engine semantics: the act
///     genuinely gates on the completed flag - the rig preseed is what
///     makes the objective count, it is not a harness auto-pass.
/// </summary>
[NotInParallel]
public class QuestCompleteQuestRigTests
{
    private const string AlreadyCompletedShapeManifestJson = """
    {
      "questId": 9201,
      "name": "CompleteQuest already-completed shape rig (5815 mirror)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9401, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1101 } ] },
          { "kind": "Progress", "id": 9402, "acts": [ { "type": "QuestActObjCompleteQuest", "questId": 5822, "acceptWith": false, "useAlias": true, "questActObjAliasId": 2467, "detailId": 1102 } ] },
          { "kind": "Progress", "id": 9403, "acts": [ { "type": "QuestActObjCompleteQuest", "questId": 5823, "acceptWith": false, "useAlias": true, "questActObjAliasId": 2467, "detailId": 1103 } ] },
          { "kind": "Ready", "id": 9404, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 1104 } ] },
          { "kind": "Reward", "id": 9405, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1105 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [
            { "type": "CompleteQuest", "questId": 5822 },
            { "type": "CompleteQuest", "questId": 5823 }
          ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string NotYetShapeManifestJson = """
    {
      "questId": 9202,
      "name": "CompleteQuest not-yet shape rig (reference never completed)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9411, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1111 } ] },
          { "kind": "Progress", "id": 9412, "acts": [ { "type": "QuestActObjCompleteQuest", "questId": 5822, "acceptWith": false, "useAlias": true, "questActObjAliasId": 2467, "detailId": 1112 } ] },
          { "kind": "Ready", "id": 9413, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 1113 } ] },
          { "kind": "Reward", "id": 9414, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1114 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private static QuestScenarioVerdict RunManifest(string json)
    {
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.Load(json);
        QuestScenarioDriver.RegisterManifestItems(manifest);
        return new QuestScenarioDriver().Run(manifest);
    }

    /// <summary>
    /// PASS: the already-completed shape (real carrier 5815 pattern: Progress
    /// complete-quest objectives referencing 5822/5823) drives the full
    /// lifecycle. The PROGRESS stage's CompleteQuest events pre-mark both
    /// references through the engine's own SetCompletedQuestFlag; then
    /// RunCurrentStep's RunAct state checks pass (HasQuestCompleted true ->
    /// SetObjective(1)) and the quest advances to Ready. This is the
    /// regression pin for the WI-5 closure: before the factory case the
    /// manifest build threw NotSupportedException (verdict Fail), and the
    /// real carriers were SKIP/unsampled in the census.
    /// </summary>
    [Test]
    public async Task CompleteQuest_AlreadyCompletedShape_DrivesFullLifecycle_Passes()
    {
        var verdict = RunManifest(AlreadyCompletedShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();

        // The rig pre-marked BOTH references through the engine's flag API -
        // the completed flags must be observable on the owner after the run.
        var character = (Character)verdict.QuestRef.Owner;
        await Assert.That(character.Quests.HasQuestCompleted(5822), "reference 5822 must be pre-marked completed").IsTrue();
        await Assert.That(character.Quests.HasQuestCompleted(5823), "reference 5823 must be pre-marked completed").IsTrue();
    }

    /// <summary>
    /// FAIL (engine semantics pin): the not-yet shape fires NO CompleteQuest
    /// events, so the referenced quest 5822 is never completed. RunAct's
    /// HasQuestCompleted check returns false, the objective never credits,
    /// and the quest CANNOT leave Progress - the drive fails at the PROGRESS
    /// stage (expected step Ready, observed Progress). This proves the
    /// objective genuinely gates on the completed flag: the harness's
    /// pre-mark is the rig, not an auto-pass.
    /// </summary>
    [Test]
    public async Task CompleteQuest_NotYetShape_ObjectiveNeverCredits_StallsAtProgress()
    {
        var verdict = RunManifest(NotYetShapeManifestJson);

        // The quest must be stuck at Progress with the objective uncredited.
        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        var progressStage = verdict.Stages.FirstOrDefault(s => s.Stage.Equals("PROGRESS", StringComparison.OrdinalIgnoreCase));
        await Assert.That(progressStage, "PROGRESS stage must exist").IsNotNull();
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Fail);
        await Assert.That(progressStage.Reason.Contains("expected step Ready"), "stall reason must mention the step mismatch").IsTrue();
    }
}
