using AAEmu.Game.Models.Game.Quests.Acts;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M1-5a: quest scenario harness fixture + fail-before demonstration.
///
/// Fixture: quest 1119 (Arcum Iris) - clean single-starter shape, verified against
/// the canonical 1.2 compact.sqlite3 on the aaemu box (2026-08-04):
///   Start  comp 5734: QuestActConAcceptNpc npc 2237
///   Ready  comp 5736: QuestActConReportNpc  npc 5697
///   Reward comp 5737: QuestActSupplyItem    item 18792 x1
/// (no Supply/Progress components - the quest goes Start -> Ready -> Reward directly)
///
/// Fail-before: the same manifest with a broken acceptor npc (9999) must FAIL the
/// START stage, and a broken report npc (9999) must FAIL the READY stage - proving
/// the harness detects template breakage instead of silently passing.
/// </summary>
/// <remarks>
/// The driver swaps game singletons (QuestManager/ItemManager/UnitRequirementsGameData)
/// per test run, so this class must not execute in parallel with itself or with
/// other classes that touch the same singletons.
/// </remarks>
[NotInParallel]
public class QuestScenarioTests
{
    private const string Quest1119ManifestJson = """
    {
      "questId": 1119,
      "name": "Arcum Iris (fixture)",
      "acceptor": { "type": "Npc", "id": 2237 },
      "template": {
        "level": 3,
        "components": [
          { "kind": "Start", "id": 5734, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 2237, "detailId": 1001 } ] },
          { "kind": "Ready", "id": 5736, "acts": [ { "type": "QuestActConReportNpc", "npcId": 5697, "detailId": 1149 } ] },
          { "kind": "Reward", "id": 5737, "acts": [ { "type": "QuestActSupplyItem", "itemId": 18792, "count": 1, "gradeId": 0, "detailId": 4326 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Ready", "status": "Ready" } },
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
    /// Fixture: quest 1119 runs the full lifecycle end-to-end and every stage passes.
    /// </summary>
    [Test]
    public async Task Quest1119_ArcumIris_Fixture_PassesEndToEnd()
    {
        var verdict = RunManifest(Quest1119ManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Stages.Count).IsEqualTo(4);
        await Assert.That(verdict.Stages.All(s => s.Outcome == StageOutcome.Pass)).IsTrue();
    }

    /// <summary>
    /// Fail-before: a broken acceptor npc in the Start component must fail the START
    /// stage (the quest never leaves the Start step), while the rest of the flow is
    /// unaffected - the harness reports the breakage instead of passing silently.
    /// </summary>
    [Test]
    public async Task Quest1119_BrokenAcceptorNpc_FailsStartStage()
    {
        var brokenJson = Quest1119ManifestJson.Replace(
            "\"type\": \"QuestActConAcceptNpc\", \"npcId\": 2237",
            "\"type\": \"QuestActConAcceptNpc\", \"npcId\": 9999");

        var verdict = RunManifest(brokenJson);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        await Assert.That(verdict.Stages[0].Outcome).IsEqualTo(StageOutcome.Fail);
        await Assert.That(verdict.Stages[0].Reason.Contains("expected step Ready")).IsTrue();
    }

    /// <summary>
    /// Fail-before: a broken report npc in the Ready component must fail the READY
    /// stage (turn-in never goes through), with the START stage still passing - the
    /// per-stage verdict isolates exactly where the template broke.
    /// </summary>
    [Test]
    public async Task Quest1119_BrokenReportNpc_FailsReadyStage()
    {
        var brokenJson = Quest1119ManifestJson.Replace(
            "\"type\": \"QuestActConReportNpc\", \"npcId\": 5697",
            "\"type\": \"QuestActConReportNpc\", \"npcId\": 9999");

        var verdict = RunManifest(brokenJson);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        await Assert.That(verdict.Stages[0].Outcome).IsEqualTo(StageOutcome.Pass); // START still fine
        await Assert.That(verdict.Stages[1].Outcome).IsEqualTo(StageOutcome.Fail); // READY broke
        await Assert.That(verdict.Stages[1].Reason.Contains("expected step Reward")).IsTrue();
    }

    /// <summary>
    /// Skip-with-reason: a stage without expectations that runs without error is
    /// reported SKIP (observational), not PASS - tier manifests can mark stages as
    /// observation-only without faking assertions.
    /// </summary>
    [Test]
    public async Task StageWithoutExpectations_ReportsSkipWithReason()
    {
        var observeJson = Quest1119ManifestJson.Replace(
            "{ \"name\": \"PERSIST\", \"events\": [], \"expect\": { \"persistRoundTrip\": true } }",
            "{ \"name\": \"PERSIST\", \"events\": [], \"expect\": { \"persistRoundTrip\": true } },\n        { \"name\": \"OBSERVE\", \"events\": [], \"expect\": {} }");

        var verdict = RunManifest(observeJson);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        var observeStage = verdict.Stages.FirstOrDefault(s => s.Stage == "OBSERVE");
        await Assert.That(observeStage).IsNotNull();
        await Assert.That(observeStage.Outcome).IsEqualTo(StageOutcome.Skip);
        await Assert.That(observeStage.Reason.Contains("no expectations")).IsTrue();
    }

    /// <summary>
    /// Fail-path wiring check: a manifest that demands a fail path (CheckTimer/Fail)
    /// on a template that has none must FAIL, proving the wiring check works.
    /// </summary>
    [Test]
    public async Task Quest1119_NoFailPathWired_FailsWiringCheck()
    {
        var failPathJson = Quest1119ManifestJson.Replace(
            "{ \"name\": \"PERSIST\", \"events\": [], \"expect\": { \"persistRoundTrip\": true } }",
            "{ \"name\": \"PERSIST\", \"events\": [], \"expect\": { \"persistRoundTrip\": true, \"failPathWired\": true } }");

        var verdict = RunManifest(failPathJson);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        var persistStage = verdict.Stages.FirstOrDefault(s => s.Stage == "PERSIST");
        await Assert.That(persistStage).IsNotNull();
        await Assert.That(persistStage.Outcome).IsEqualTo(StageOutcome.Fail);
        await Assert.That(persistStage.Reason.Contains("fail path")).IsTrue();
    }

    /// <summary>
    /// The harness must reject manifest shapes it cannot drive (unknown act type)
    /// with a clear NotSupportedException surfaced as a FAIL, not a silent skip.
    /// </summary>
    [Test]
    public async Task UnknownActType_SurfacesAsStageFailure()
    {
        var brokenJson = Quest1119ManifestJson.Replace(
            "\"type\": \"QuestActConAcceptNpc\"",
            "\"type\": \"QuestActObjBogus\"");

        var verdict = RunManifest(brokenJson);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        await Assert.That(verdict.Stages[0].Outcome).IsEqualTo(StageOutcome.Fail);
        await Assert.That(verdict.Stages[0].Reason.Contains("Unsupported act type")).IsTrue();
    }

    /// <summary>
    /// Sanity: the fixture's reward item template must be registered so the supply act
    /// can actually add it to the rigged inventory (otherwise the REWARD stage could
    /// never observe the item).
    /// </summary>
    [Test]
    public async Task RegisterManifestItems_RegistersRewardItemTemplate()
    {
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.Load(Quest1119ManifestJson);
        QuestScenarioDriver.RegisterManifestItems(manifest);

        var template = AAEmu.Game.Core.Managers.ItemManager.Instance.GetTemplate(18792);
        await Assert.That(template).IsNotNull();
        await Assert.That(template.MaxCount > 0).IsTrue();
    }

    // ------------------------------------------------------------------
    // RC-3: guard NPC rig for QuestActCheckGuard in ANY component
    // (quest 1033 shape: guard lives in the Progress component, not Start)
    // ------------------------------------------------------------------

    private const string GuardInProgressManifestJson = """
    {
      "questId": 9033,
      "name": "Guard-in-Progress fixture (RC-3)",
      "acceptor": { "type": "Npc", "id": 4795 },
      "template": {
        "level": 20,
        "components": [
          { "kind": "Start", "id": 9001, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 4795 } ] },
          { "kind": "Progress", "id": 9002, "acts": [ { "type": "QuestActObjTalk", "npcId": 4617 }, { "type": "QuestActCheckGuard", "npcId": 4617 } ] },
          { "kind": "Ready", "id": 9003, "acts": [ { "type": "QuestActConReportNpc", "npcId": 4618 } ] },
          { "kind": "Reward", "id": 9004, "acts": [ { "type": "QuestActSupplyCopper", "amount": 100 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "Talk", "npcId": 4617 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 4618, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } }
      ]
    }
    """;

    /// <summary>
    /// RC-3: a QuestActCheckGuard act in a NON-Start component must pass - the
    /// driver spawns a guard NPC for every CheckGuard act in the template (no
    /// manifest guard block needed). Fail-before: CheckGuard.RunAct returned
    /// false for an unresolvable NPC, so the Progress step could never advance
    /// (quests 1033/3656/1897).
    /// </summary>
    [Test]
    public async Task GuardInProgressComponent_SpawnedFromTemplateActs_PassesProgress()
    {
        var verdict = RunManifest(GuardInProgressManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        var progressStage = verdict.Stages.FirstOrDefault(s => s.Stage == "PROGRESS");
        await Assert.That(progressStage).IsNotNull();
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Pass);
    }

    /// <summary>
    /// RC-3 fail-before companion: when the manifest pins the same guard as
    /// dead (alive:false), the check must NOT pass - the rig must not turn the
    /// guard check into an always-pass. The Progress step stays stuck.
    /// </summary>
    [Test]
    public async Task DeadGuardFromManifest_FailsProgress()
    {
        var deadGuardJson = GuardInProgressManifestJson.Replace(
            "\"questId\": 9033",
            "\"questId\": 9033,\n      \"guard\": { \"npcId\": 4617, \"alive\": false }");

        var verdict = RunManifest(deadGuardJson);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        var progressStage = verdict.Stages.FirstOrDefault(s => s.Stage == "PROGRESS");
        await Assert.That(progressStage).IsNotNull();
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Fail);
        await Assert.That(progressStage.Reason.Contains("expected step Ready")).IsTrue();
    }

    // ------------------------------------------------------------------
    // RC-3 companion: QuestActCheckSphere rig (quest 1033 shape - BUG-011
    // live-position RunAct needs the component's quest sphere in the world)
    // ------------------------------------------------------------------

    private const string CheckSphereInProgressManifestJson = """
    {
      "questId": 9035,
      "name": "CheckSphere-in-Progress fixture (RC-3/1033)",
      "acceptor": { "type": "Npc", "id": 4795 },
      "template": {
        "level": 20,
        "components": [
          { "kind": "Start", "id": 9201, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 4795 } ] },
          { "kind": "Progress", "id": 9202, "acts": [ { "type": "QuestActObjTalk", "npcId": 4617 }, { "type": "QuestActCheckGuard", "npcId": 4617 } ] },
          { "kind": "Progress", "id": 9203, "acts": [ { "type": "QuestActCheckSphere", "sphereId": 945 } ] },
          { "kind": "Ready", "id": 9204, "acts": [ { "type": "QuestActConReportNpc", "npcId": 4618 } ] },
          { "kind": "Reward", "id": 9205, "acts": [ { "type": "QuestActSupplyCopper", "amount": 100 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "Talk", "npcId": 4617 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 4618, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } }
      ]
    }
    """;

    /// <summary>
    /// RC-3/quest 1033 exact shape: a second Progress component carries a
    /// QuestActCheckSphere (no objective counter, ThisComponentObjectiveIndex =
    /// 0xFF). Since BUG-011 its RunAct evaluates the owner's LIVE position
    /// against the component's quest spheres - the harness must rig one
    /// origin-centered sphere or the check can never pass. Fail-before: the
    /// sphere rig was missing and 1033's Progress stayed stuck on the check.
    /// </summary>
    [Test]
    public async Task CheckSphereInProgressComponent_RiggedSphere_PassesProgress()
    {
        var verdict = RunManifest(CheckSphereInProgressManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        var progressStage = verdict.Stages.FirstOrDefault(s => s.Stage == "PROGRESS");
        await Assert.That(progressStage).IsNotNull();
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Pass);
    }

    // ------------------------------------------------------------------
    // RC-7: objective index reset per KIND (mirror QuestManager.cs:207-211)
    // ------------------------------------------------------------------

    private const string MultiComponentProgressManifestJson = """
    {
      "questId": 9034,
      "name": "Per-kind objective index fixture (RC-7)",
      "acceptor": { "type": "Npc", "id": 1001 },
      "template": {
        "level": 10,
        "components": [
          { "kind": "Start", "id": 9101, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 1001 } ] },
          { "kind": "Progress", "id": 9102, "acts": [ { "type": "QuestActObjTalk", "npcId": 2001 } ] },
          { "kind": "Progress", "id": 9103, "acts": [ { "type": "QuestActObjSphere", "sphereId": 777 } ] },
          { "kind": "Ready", "id": 9104, "acts": [ { "type": "QuestActConReportNpc", "npcId": 3001 } ] },
          { "kind": "Reward", "id": 9105, "acts": [ { "type": "QuestActSupplyCopper", "amount": 100 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress", "objectives": [0, 0, 0, 0, 0] } },
        { "name": "PROGRESS", "events": [ { "type": "Talk", "npcId": 2001 }, { "type": "EnterSphere", "componentId": 9103 } ], "expect": { "step": "Ready", "status": "Ready", "objectives": [1, 1, 0, 0, 0] } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 3001, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } }
      ]
    }
    """;

    /// <summary>
    /// RC-7: BuildTemplate must mirror the loader's per-KIND objective counter
    /// (QuestManager.cs:207-211) - objective acts across two Progress components
    /// land in distinct Objectives slots (0 and 1), and non-objective acts get
    /// 0xFF exactly like the loader (QuestManager.cs:220).
    /// </summary>
    [Test]
    public async Task BuildTemplate_ObjectiveIndices_MirrorLoaderPerKindReset()
    {
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.Load(MultiComponentProgressManifestJson);
        var template = QuestScenarioDriver.BuildTemplate(manifest);

        var progressActs = template.Components[9102].ActTemplates
            .Concat(template.Components[9103].ActTemplates)
            .ToList();
        await Assert.That(progressActs[0] is QuestActObjTalk).IsTrue();
        await Assert.That(progressActs[0].ThisComponentObjectiveIndex).IsEqualTo((byte)0);
        await Assert.That(progressActs[1] is QuestActObjSphere).IsTrue();
        await Assert.That(progressActs[1].ThisComponentObjectiveIndex).IsEqualTo((byte)1);
        // non-objective acts are 0xFF, never an Objectives slot
        await Assert.That(template.Components[9101].ActTemplates[0].ThisComponentObjectiveIndex).IsEqualTo((byte)0xFF);
    }

    /// <summary>
    /// RC-7 pass-after: two Progress components share the per-kind counter, so
    /// the second component's objective event credits Objectives[1] - not slot 0
    /// (which would collide with the first component's act and leave the census
    /// objective columns wrong). The quest rests at Ready (quest alive, objectives
    /// intact - completed quests get dropped and cleared, Quest.cs:441), so the
    /// objective counters are observable. Fail-before: both acts took index 0 and
    /// the sphere credit overwrote the talk credit.
    /// </summary>
    [Test]
    public async Task MultiComponentProgress_ObjectivesInDistinctSlots_Passes()
    {
        var verdict = RunManifest(MultiComponentProgressManifestJson);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
        var progressStage = verdict.Stages.FirstOrDefault(s => s.Stage == "PROGRESS");
        await Assert.That(progressStage).IsNotNull();
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Pass);
    }
}
