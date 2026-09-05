using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2 WI-3 (t_d5e802f5): QuestActObjAbilityLevel harness-closure rig.
///
/// The act's RunAct is a pure state check on the owner's ability exp
/// (QuestActObjAbilityLevel.cs:22-45) - it has no event subscription and
/// never calls SetObjective; QuestComponent.RunComponent passes on the
/// RunAct RETURN value. The harness therefore presees ability exp via the
/// driver's synthetic "AbilityLevel" event (fired at the Progress stage
/// before RunCurrentStep), mirroring how "LevelUp" makes QuestActObjLevel
/// reachable. abilityId 0 = the all-abilities branch (every ability 1..10
/// must meet the level) - the rig saturates ALL seeded abilities; the
/// single-ability branch seeds just the act's ability. The preseed goes
/// through CharacterAbilities.AddExp, whose TryGetValue guard skips the
/// unseeded General(0)/None(11) keys (BUG-012 semantics).
///
/// The manifests below mirror the REAL carrier shapes: 5967 (all-abilities,
/// abilityId 0, level 50) and 6070/6075-6082 (single-ability, level 50) -
/// Progress objective + report at Ready + item at Reward. Fail-before:
/// without the factory case + ACT_TABLES entry the 10 live carriers SKIP
/// as unsupported-act-type (census t3: 5967/6069; the other nine were not
/// sampled at all - t10 adds them). Pass-after: this rig drives both
/// branches through the full lifecycle, and the census flips the drivable
/// carriers to PASS (6069 was DROPPED 2026-08-09, register §8 t_6810ebd4 -
/// unreachable ltd with zero accept surfaces; it is no longer a carrier).
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestAbilityLevelRigTests
{
    private const string SingleAbilityShapeManifestJson = """
    {
      "questId": 9101,
      "name": "AbilityLevel single-ability shape rig",
      "acceptor": { "type": "Npc", "id": 8822 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9311, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 8822, "detailId": 1011 } ] },
          { "kind": "Progress", "id": 9312, "acts": [ { "type": "QuestActObjAbilityLevel", "abilityId": 1, "level": 50, "detailId": 1012 } ] },
          { "kind": "Ready", "id": 9313, "acts": [ { "type": "QuestActConReportNpc", "npcId": 8822, "detailId": 1013 } ] },
          { "kind": "Reward", "id": 9314, "acts": [
              { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1014 },
              { "type": "QuestActSupplyExp", "exp": 0, "detailId": 1015 }
          ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "AbilityLevel", "abilityId": 1, "level": 50 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 8822, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string AllAbilitiesShapeManifestJson = """
    {
      "questId": 9102,
      "name": "AbilityLevel all-abilities shape rig",
      "acceptor": { "type": "Npc", "id": 879 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9321, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 879, "detailId": 1021 } ] },
          { "kind": "Progress", "id": 9322, "acts": [ { "type": "QuestActObjAbilityLevel", "abilityId": 0, "level": 50, "detailId": 1022 } ] },
          { "kind": "Ready", "id": 9323, "acts": [ { "type": "QuestActConReportNpc", "npcId": 879, "detailId": 1023 } ] },
          { "kind": "Reward", "id": 9324, "acts": [
              { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 1024 },
              { "type": "QuestActSupplyAppellation", "appellationId": 191, "detailId": 1025 }
          ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "AbilityLevel", "abilityId": 0, "level": 50 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 879, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
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
    /// PASS: the single-ability shape (real carriers 6070/6075-6082:
    /// one ability must reach level 50) drives the full lifecycle. The
    /// PROGRESS stage's AbilityLevel event presees Fight(1) exp via
    /// CharacterAbilities.AddExp, then RunCurrentStep's RunAct state check
    /// passes and the quest advances to Ready. This is the regression pin
    /// for the WI-3 closure: before the factory case the manifest build
    /// threw NotSupportedException (verdict Fail), and the real carriers
    /// were SKIP/unsampled in the census.
    /// </summary>
    [Test]
    public async Task AbilityLevel_SingleAbilityShape_DrivesFullLifecycle_Passes()
    {
        var verdict = RunManifest(SingleAbilityShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();
    }

    /// <summary>
    /// PASS: the all-abilities branch (real carrier 5967: abilityId 0,
    /// EVERY ability 1..10 must reach level 50 - QuestActObjAbilityLevel.cs:35
    /// loops General+1..None). The event presees ALL seeded abilities so the
    /// loop's state check passes. Also pins the AbilityType.General(0) trap:
    /// the rig never touches the unseeded General/None keys (AddExp's
    /// TryGetValue guard, BUG-012 semantics).
    /// </summary>
    [Test]
    public async Task AbilityLevel_AllAbilitiesShape_DrivesFullLifecycle_Passes()
    {
        var verdict = RunManifest(AllAbilitiesShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();
    }
}
