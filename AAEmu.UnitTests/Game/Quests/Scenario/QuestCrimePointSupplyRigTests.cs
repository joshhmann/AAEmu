using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2 WI-2 (t_f42b9ae3): QuestActSupplyCrimePoint harness-closure rig.
///
/// The act's RunAct calls Character.AddCrime(Point) and returns true
/// (QuestActSupplyCrimePoint.cs:17-23) - the same shape as the JuryPoint /
/// LivingPoint supply closures. It needs no synthetic event: the Reward stage
/// drives it via RunCurrentStep. The harness factory case (BuildAct) builds it
/// from the manifest row {"type": "QuestActSupplyCrimePoint", "point": N}.
///
/// The manifest below mirrors the REAL carrier shape (quest 2916): interaction
/// objective at Progress, report at Ready, crime-point + copper + exp at
/// Reward. Fail-before: without the factory case + ACT_TABLES entry the 7 live
/// carriers (2916/2926/2935/2936/5197/5198/5494) SKIP as unsupported-act-type
/// (census: t3 2916/2926; the level-41-50 five were not sampled at all - t9
/// adds them). Pass-after: this rig drives the family through the full
/// lifecycle, and the census flips all 7 to PASS.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestCrimePointSupplyRigTests
{
    private const string CrimePointShapeManifestJson = """
    {
      "questId": 9100,
      "name": "CrimePoint supply shape rig",
      "acceptor": { "type": "Npc", "id": 8822 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9301, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 8822, "detailId": 1001 } ] },
          { "kind": "Progress", "id": 9302, "acts": [ { "type": "QuestActObjInteraction", "doodadId": 3407, "count": 15, "wiId": 19, "phase": 0, "detailId": 1002 } ] },
          { "kind": "Ready", "id": 9303, "acts": [ { "type": "QuestActConReportNpc", "npcId": 8822, "detailId": 1003 } ] },
          { "kind": "Reward", "id": 9304, "acts": [
              { "type": "QuestActSupplyCrimePoint", "point": -20, "detailId": 1004 },
              { "type": "QuestActSupplyCopper", "amount": 10, "detailId": 1005 },
              { "type": "QuestActSupplyExp", "exp": 0, "detailId": 1006 }
          ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "Interaction", "doodadId": 3407, "count": 15 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 8822, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
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
    /// PASS: the CrimePoint supply act (production shape: point-supply at
    /// Reward alongside copper/exp) drives the full lifecycle. The Reward
    /// stage's RunCurrentStep executes QuestActSupplyCrimePoint.RunAct ->
    /// Character.AddCrime(-20) (null-safe SendPacket) and returns true, so the
    /// quest completes and the persist round-trip stays byte-equal. This is the
    /// regression pin for the WI-2 closure: before the factory case the
    /// manifest build threw NotSupportedException (verdict Fail), and the real
    /// carriers were SKIP/unsampled in the census.
    /// </summary>
    [Test]
    public async Task CrimePoint_SupplyShape_DrivesFullLifecycle_Passes()
    {
        var verdict = RunManifest(CrimePointShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();
    }
}
