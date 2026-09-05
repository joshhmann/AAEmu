using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2 WI-4 (t_fe93e2d8): QuestActObjMateLevel harness-closure rig.
///
/// The act's RunAct -> CalculateObjective scans the owner's inventory for a
/// SummonMate item with the act's ItemId whose DetailLevel >= Level
/// (QuestActObjMateLevel.cs:22-58) - a state check at RunAct time, like
/// AbilityLevel. The harness therefore presees a REAL SummonMate (an ItemMock
/// fails the `item is not SummonMate` guard) via the driver's synthetic
/// "MateLevel" event, fired at the Progress stage before RunCurrentStep.
///
/// Engine behavior verified from source: when a valid mate is found,
/// CalculateObjective calls SetObjective(quest, 1) and - when the act's
/// Cleanup flag is set - CONSUMES the mate item from the bag
/// (ConsumeItem QuestRemoveSupplies, QuestActObjMateLevel.cs:42-51). The
/// consume happens when the objective is met (Progress RunAct), NOT at quest
/// completion. The two manifests below pin BOTH flag branches:
///   - CleanupShape (mirrors real carrier 5464: item 8158, level 50,
///     cleanup='t'): full lifecycle PASS and the mate is GONE from the bag
///     after the run (consumed).
///   - NoCleanupShape (same shape, cleanup='f'): full lifecycle PASS and the
///     mate REMAINS in the bag - proving the consume is gated on the flag.
/// Fail-before: without the factory case + ACT_TABLES entry the 6 live
/// carriers (5430/5464 in T3 + 5465/5466/5812/5813 unsampled) are not driven
/// (census SKIP / unsampled). Pass-after: this rig drives both branches
/// through the full lifecycle and the census flips all 6 to PASS.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestMateLevelRigTests
{
    private const string CleanupShapeManifestJson = """
    {
      "questId": 9103,
      "name": "MateLevel cleanup shape rig (5464 mirror)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9331, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1031 } ] },
          { "kind": "Progress", "id": 9332, "acts": [ { "type": "QuestActObjItemGather", "itemId": 28449, "count": 1, "detailId": 1032 } ] },
          { "kind": "Progress", "id": 9333, "acts": [ { "type": "QuestActObjMateLevel", "itemId": 8158, "level": 50, "cleanup": true, "useAlias": true, "questActObjAliasId": 2467, "detailId": 1033 } ] },
          { "kind": "Ready", "id": 9334, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 1034 } ] },
          { "kind": "Reward", "id": 9335, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1035 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [
            { "type": "ItemGather", "itemId": 28449, "count": 1 },
            { "type": "MateLevel", "itemId": 8158, "level": 50 }
          ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ],
      "inventory": [ { "itemId": 28449, "count": 1 } ]
    }
    """;

    private const string NoCleanupShapeManifestJson = """
    {
      "questId": 9104,
      "name": "MateLevel no-cleanup shape rig",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9341, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 1041 } ] },
          { "kind": "Progress", "id": 9342, "acts": [ { "type": "QuestActObjMateLevel", "itemId": 8158, "level": 50, "cleanup": false, "useAlias": true, "questActObjAliasId": 2467, "detailId": 1042 } ] },
          { "kind": "Ready", "id": 9343, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 1043 } ] },
          { "kind": "Reward", "id": 9344, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1044 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "MateLevel", "itemId": 8158, "level": 50 } ], "expect": { "step": "Ready", "status": "Ready" } },
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
    /// PASS: the cleanup shape (real carrier 5464: item 8158, level 50,
    /// cleanup='t') drives the full lifecycle. The PROGRESS stage's MateLevel
    /// event presees a real SummonMate at DetailLevel 50; RunCurrentStep's
    /// RunAct state check passes AND the engine consumes the mate from the
    /// bag (ConsumeItem QuestRemoveSupplies, verified in
    /// QuestActObjMateLevel.cs:42-51) - the bag no longer holds item 8158
    /// after the run. This is the Cleanup-consume path evidence: the mate is
    /// consumed when the objective is met, not at completion.
    /// </summary>
    [Test]
    public async Task MateLevel_CleanupShape_DrivesFullLifecycle_ConsumesMate()
    {
        var verdict = RunManifest(CleanupShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();

        // Cleanup-consume path: the mate (template 8158) must be gone from the
        // bag after the objective was met at the Progress stage.
        var character = (Character)verdict.QuestRef.Owner;
        var remainingMates = character.Inventory.Bag.Items.Count(i => i.TemplateId == 8158);
        await Assert.That(remainingMates).IsEqualTo(0);
    }

    /// <summary>
    /// PASS: the no-cleanup shape drives the full lifecycle and the mate
    /// REMAINS in the bag - proving the consume is gated on the act's Cleanup
    /// flag (cleanup='f' carriers keep their mate, e.g. a "show your grown
    /// mate" objective without hand-in).
    /// </summary>
    [Test]
    public async Task MateLevel_NoCleanupShape_DrivesFullLifecycle_KeepsMate()
    {
        var verdict = RunManifest(NoCleanupShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();

        var character = (Character)verdict.QuestRef.Owner;
        var remainingMates = character.Inventory.Bag.Items.Count(i => i.TemplateId == 8158);
        await Assert.That(remainingMates).IsEqualTo(1);
    }
}
