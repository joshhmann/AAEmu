using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// QuestActEtcItemObtain credit-path closure rig.
///
/// The act previously wired-no-op'd: RunAct returned true unconditionally and
/// OnItemGather was an empty body, so the ~51 live quests gated on it never
/// credited from real item acquisition. The fix mirrors sibling
/// QuestActObjItemGather's credit path (QuestActEtcItemObtain.cs): the act is
/// now CountsAsAnObjective (takes an Objectives slot), subscribes
/// OnItemGather via InitializeAction (unchanged wiring), credits
/// AddObjective(questAct, args.Count) per matching acquire event, and RunAct
/// gates on GetObjective(quest) >= Count.
///
/// Event surface: OnItemGather is raised by the real acquisition path
/// (Inventory.OnAcquiredItem -> QuestManager.DoItemsAcquiredEvents ->
/// Character.Events.OnItemGather); the harness's synthetic "ItemGather" event
/// raises exactly that UnitEvents surface (same one ObjItemGather manifests
/// drive). Unlike ObjItemGather (inventory-count snapshot), EtcItemObtain
/// accumulates acquisitions - items may be consumed afterwards ("does not
/// require the item in the inventory").
///
/// Three cases pinned:
///   - HappyPathShape (count 3): three matching ItemGather events credit the
///     objective -> full lifecycle PASS (start->progress->ready->reward->persist).
///   - InsufficientShape (count 3, only 2 matching events + a WRONG-ITEM event
///     that must NOT credit): the objective cannot reach Count -> the quest
///     stalls at Progress (drive fails at the PROGRESS stage).
///   - ZeroCountShape (count 0, degenerate template row): no crash, no credit
///     needed - RunAct's >= comparison is trivially satisfied, matching
///     sibling ObjItemGather semantics for Count == 0.
/// </summary>
[NotInParallel]
public class QuestEtcItemObtainRigTests
{
    private const string HappyPathShapeManifestJson = """
    {
      "questId": 9301,
      "name": "EtcItemObtain happy path rig (gather credits objective)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 30,
        "components": [
          { "kind": "Start", "id": 9501, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1201 } ] },
          { "kind": "Progress", "id": 9502, "acts": [ { "type": "QuestActEtcItemObtain", "itemId": 3950, "count": 3, "cleanup": false, "detailId": 1202 } ] },
          { "kind": "Ready", "id": 9503, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 1203 } ] },
          { "kind": "Reward", "id": 9504, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1204 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [
            { "type": "ItemGather", "itemId": 3950, "count": 1 },
            { "type": "ItemGather", "itemId": 3950, "count": 1 },
            { "type": "ItemGather", "itemId": 3950, "count": 1 }
          ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string InsufficientShapeManifestJson = """
    {
      "questId": 9302,
      "name": "EtcItemObtain insufficient count rig (wrong item + partial quota)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 30,
        "components": [
          { "kind": "Start", "id": 9511, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1211 } ] },
          { "kind": "Progress", "id": 9512, "acts": [ { "type": "QuestActEtcItemObtain", "itemId": 3950, "count": 3, "cleanup": false, "detailId": 1212 } ] },
          { "kind": "Ready", "id": 9513, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 1213 } ] },
          { "kind": "Reward", "id": 9514, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1214 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [
            { "type": "ItemGather", "itemId": 4031, "count": 5 },
            { "type": "ItemGather", "itemId": 3950, "count": 1 },
            { "type": "ItemGather", "itemId": 3950, "count": 1 }
          ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string ZeroCountShapeManifestJson = """
    {
      "questId": 9303,
      "name": "EtcItemObtain zero-count degenerate template rig",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 30,
        "components": [
          { "kind": "Start", "id": 9521, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1221 } ] },
          { "kind": "Progress", "id": 9522, "acts": [ { "type": "QuestActEtcItemObtain", "itemId": 3950, "count": 0, "cleanup": false, "detailId": 1222 } ] },
          { "kind": "Ready", "id": 9523, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 1223 } ] },
          { "kind": "Reward", "id": 9524, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1224 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Ready", "status": "Ready" } },
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
    /// PASS: three matching ItemGather events (the OnItemGather surface raised by
    /// the real acquisition path) credit the EtcItemObtain objective to its
    /// quota of 3 and the quest advances through the full lifecycle. Regression
    /// pin for the wired-no-op closure: before the fix this drive stalled at
    /// Progress forever because OnItemGather discarded every event.
    /// </summary>
    [Test]
    public async Task EtcItemObtain_MatchingGathersCreditObjective_DrivesFullLifecycle_Passes()
    {
        var verdict = RunManifest(HappyPathShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();

        // Credit proof lives in the PROGRESS stage itself: Ready is unreachable
        // without three matching AddObjective calls (the insufficient-shape pin
        // below proves partial credit stalls). Post-completion GetObjective is
        // always 0 — Quest.Objectives is reallocated when the quest completes
        // (Quest.cs Objectives = new int[MaxObjectiveCount]) — so a quota
        // assertion here would be meaningless.
    }

    /// <summary>
    /// FAIL (engine semantics pin): two matching gathers + one WRONG-ITEM gather
    /// (item 4031 vs act itemId 3950 - must not credit) leave the objective at
    /// 2 of 3, RunAct returns false, and the quest CANNOT leave Progress. This
    /// proves the act genuinely gates on the gathered count - it is not a
    /// pass-through anymore.
    /// </summary>
    [Test]
    public async Task EtcItemObtain_InsufficientCount_ObjectiveNeverCompletes_StallsAtProgress()
    {
        var verdict = RunManifest(InsufficientShapeManifestJson);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        var progressStage = verdict.Stages.FirstOrDefault(s => s.Stage.Equals("PROGRESS", StringComparison.OrdinalIgnoreCase));
        await Assert.That(progressStage, "PROGRESS stage must exist").IsNotNull();
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Fail);
        await Assert.That(progressStage.Reason.Contains("expected step Ready"), "stall reason must mention the step mismatch").IsTrue();

        // Partial credit landed (2 of 3) but did not complete the objective.
        var obtainAct = verdict.QuestRef.Template.Components[9512].ActTemplates
            .OfType<AAEmu.Game.Models.Game.Quests.Acts.QuestActEtcItemObtain>().Single();
        await Assert.That(obtainAct.GetObjective(verdict.QuestRef)).IsEqualTo(2);
    }

    /// <summary>
    /// Degenerate-template edge (count 0 DB rows): the act must not crash and
    /// its trivially-satisfied comparison lets the step advance with no gather
    /// events at all - identical semantics to sibling QuestActObjItemGather at
    /// Count == 0 (GetObjective >= 0).
    /// </summary>
    [Test]
    public async Task EtcItemObtain_ZeroCount_Tolerated_NoCrash_AdvancesWithoutEvents()
    {
        var verdict = RunManifest(ZeroCountShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();
    }
}
