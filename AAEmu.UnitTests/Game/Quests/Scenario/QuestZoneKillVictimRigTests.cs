using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2c wave-3 (t_1324bc51): QuestActObjZoneKill victim rig verification.
///
/// The act's OnZoneKill rejects Victim=owner (self-kill guard,
/// QuestActObjZoneKill.cs:70-71) AND only credits a victim satisfying its
/// faction/level filters (lines 83-96). The harness rig (FireZoneKillEvents)
/// delivers a NON-OWNER victim built to satisfy the act's filters.
///
/// Two rig tests:
///   1. PK-shaped act (pcFactionId=115, exclusive, level 40-55) - the shape of
///      the 11 real PK ZoneKill quests (5982-5991, 6627). The rig must credit
///      and the quest must PASS the full lifecycle: the victim!=killer guard
///      passes and the faction/level filters are satisfied.
///   2. Faction-0 NPC-kill act (the shape of 95/106 real quests incl. the
///      expedition dailies 5900/5923/5924) - the engine's credit path used to
///      be gated on `if (NpcFactionId > 0)` (QuestActObjZoneKill.cs:83-93), so
///      the objective could NEVER credit (REAL engine defect, upstream-
///      identical, tagged as engine watch item §2.4). Fixed by t_497b51d8:
///      faction 0 = "no filter", 0..0 level bounds = "any level". The fail-
///      before pin (stall at Progress) flipped to a full-lifecycle PASS -
///      this test is now the regression pin for the fix.
/// </summary>
[NotInParallel]
public class QuestZoneKillVictimRigTests
{
    private const string PkShapeManifestJson = """
    {
      "questId": 9001,
      "name": "ZoneKill PK-shape rig",
      "acceptor": { "type": "Npc", "id": 2237 },
      "template": {
        "level": 50,
        "components": [
          { "kind": "Start", "id": 9101, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 2237, "detailId": 1001 } ] },
          { "kind": "Progress", "id": 9102, "acts": [ { "type": "QuestActObjZoneKill", "countPk": 2, "countNpc": 0, "pcFactionId": 115, "pcFactionExclusive": true, "lvMin": 40, "lvMax": 55, "lvMinNpc": 0, "lvMaxNpc": 0, "detailId": 1002 } ] },
          { "kind": "Ready", "id": 9103, "acts": [ { "type": "QuestActConReportNpc", "npcId": 5697, "detailId": 1003 } ] },
          { "kind": "Reward", "id": 9104, "acts": [ { "type": "QuestActSupplyItem", "itemId": 18792, "count": 1, "gradeId": 0, "detailId": 1004 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "ZoneKill", "zoneGroupId": 34, "count": 2, "countNpc": 0, "countPk": 2, "pcFactionId": 115, "pcFactionExclusive": true, "lvMin": 40, "lvMax": 55 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 5697, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "rewardItems": [ { "itemId": 18792, "count": 1 } ], "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string Faction0ShapeManifestJson = """
    {
      "questId": 9002,
      "name": "ZoneKill faction-0 NPC shape rig",
      "acceptor": { "type": "Npc", "id": 2237 },
      "template": {
        "level": 30,
        "components": [
          { "kind": "Start", "id": 9201, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 2237, "detailId": 2001 } ] },
          { "kind": "Progress", "id": 9202, "acts": [ { "type": "QuestActObjZoneKill", "countNpc": 30, "countPk": 0, "npcFactionId": 0, "npcFactionExclusive": false, "lvMinNpc": 0, "lvMaxNpc": 0, "detailId": 2002 } ] },
          { "kind": "Ready", "id": 9203, "acts": [ { "type": "QuestActConReportNpc", "npcId": 5697, "detailId": 2003 } ] },
          { "kind": "Reward", "id": 9204, "acts": [ { "type": "QuestActSupplyItem", "itemId": 18792, "count": 1, "gradeId": 0, "detailId": 2004 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "ZoneKill", "zoneGroupId": 20, "count": 30, "countNpc": 30, "countPk": 0, "npcFactionId": 0, "npcFactionExclusive": false, "lvMinNpc": 0, "lvMaxNpc": 0 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 5697, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "rewardItems": [ { "itemId": 18792, "count": 1 } ], "completed": true } },
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
    /// PASS: PK-shaped ZoneKill (pc_faction=115, exclusive, lv 40-55) drives the
    /// full lifecycle through the victim rig - the rig's non-owner Character
    /// victim satisfies the faction/level filters, the objective credits, and
    /// the quest reaches Ready -> Reward -> complete.
    /// </summary>
    [Test]
    public async Task ZoneKill_PkShape_WithVictimRig_PassesFullLifecycle()
    {
        var verdict = RunManifest(PkShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();
    }

    /// <summary>
    /// PASS (flipped from fail-before pin, t_497b51d8): the faction-0 NPC-kill
    /// shape (95/106 real quests incl. the 20 band-21-30 quests and expedition
    /// dailies 5900/5923/5924) credits through the engine. The fix treats
    /// faction 0 as "no faction filter" (QuestActObjZoneKill.cs:81-93) and
    /// 0..0 level bounds as "any level" (lines 89-92) — a compliant non-owner
    /// victim credits, the objective reaches its count, and the quest passes
    /// the full lifecycle. This test is the regression pin for the fix.
    /// </summary>
    [Test]
    public async Task ZoneKill_Faction0NpcShape_CreditsThroughEngine_PassesFullLifecycle()
    {
        var verdict = RunManifest(Faction0ShapeManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();
    }
}
