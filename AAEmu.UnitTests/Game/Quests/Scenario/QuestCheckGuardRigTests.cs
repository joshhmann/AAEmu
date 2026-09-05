using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2 WI-10 (t_abafd918): QuestActCheckGuard negative-path rig (GUARD_DIED
/// stage). BUG-008 semantics pinned (QuestActCheckGuard.cs:19-34):
///   - guard present + alive -> RunAct TRUE (the guard check passes)
///   - guard present + dead (Hp &lt;= 0, Unit.IsDead, Unit.cs:873) -> RunAct FALSE
///   - guard despawned / unresolvable (GetNpcByTemplateId null) -> RunAct FALSE
/// A false guard check blocks the quest AT the guard-checking step (the
/// component can never pass), so the escort objective fails/passes correctly.
///
/// The census manifests spawn every CheckGuard NPC alive (RC-3); the GUARD_DIED
/// stage builds a probe with the rigged guards KILLED before accept, so the
/// engine can never pass the guard component and the quest stalls at the guard
/// step (expect step = guard component kind + guardBlocked).
///
/// Fail-before: the GUARD_DIED stage is not handled by the driver (default
/// branch evaluates the expect against the completed/dropped main quest ->
/// step mismatch). After the WI-10 driver change the stage passes.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestCheckGuardRigTests
{
    private const string AliveGuardManifestJson = """
    {
      "questId": 9301,
      "name": "CheckGuard alive shape rig (real carriers: 1033/1313/1897/3656)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "guard": { "npcId": 6059, "alive": true },
      "guards": [ { "npcId": 6059, "alive": true } ],
      "template": {
        "level": 5,
        "components": [
          { "kind": "Start", "id": 93011, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9301 } ] },
          { "kind": "Progress", "id": 93012, "acts": [ { "type": "QuestActCheckGuard", "npcId": 6059, "detailId": 9302 } ] },
          { "kind": "Ready", "id": 93013, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9303 } ] },
          { "kind": "Reward", "id": 93014, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9304 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } },
        { "name": "GUARD_DIED", "events": [], "expect": { "step": "Progress", "guardBlocked": true } }
      ]
    }
    """;

    private const string DeadGuardManifestJson = """
    {
      "questId": 9302,
      "name": "CheckGuard dead shape rig",
      "acceptor": { "type": "Npc", "id": 13453 },
      "guard": { "npcId": 6059, "alive": false },
      "guards": [ { "npcId": 6059, "alive": false } ],
      "template": {
        "level": 5,
        "components": [
          { "kind": "Start", "id": 93021, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9311 } ] },
          { "kind": "Progress", "id": 93022, "acts": [ { "type": "QuestActCheckGuard", "npcId": 6059, "detailId": 9312 } ] },
          { "kind": "Ready", "id": 93023, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9313 } ] },
          { "kind": "Reward", "id": 93024, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9314 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string NoGuardSpawnManifestJson = """
    {
      "questId": 9303,
      "name": "CheckGuard unresolvable shape rig (npcId 0 - no spawn)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 5,
        "components": [
          { "kind": "Start", "id": 93031, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9321 } ] },
          { "kind": "Progress", "id": 93032, "acts": [ { "type": "QuestActCheckGuard", "npcId": 0, "detailId": 9322 } ] },
          { "kind": "Ready", "id": 93033, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9323 } ] },
          { "kind": "Reward", "id": 93034, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9324 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private static QuestScenarioManifest LoadManifest(string json)
    {
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.Load(json);
        QuestScenarioDriver.RegisterManifestItems(manifest);
        return manifest;
    }

    /// <summary>
    /// BUG-008 positive + negative semantics through the engine:
    ///  - alive guard: the guard component passes and the quest advances past
    ///    the guard step (main run completes START -> READY -> REWARD).
    ///  - GUARD_DIED stage: a probe with the rigged guard killed cannot pass
    ///    the guard component - the quest stalls at Progress (step Progress +
    ///    guardBlocked), the engine never advances past the dead guard.
    /// </summary>
    [Test]
    public async Task CheckGuard_AliveGuard_ManifestWithGuardDiedStage_StagePasses()
    {
        var manifest = LoadManifest(AliveGuardManifestJson);
        var verdict = new QuestScenarioDriver().Run(manifest);

        // Main lifecycle: alive guard -> guard check passes -> full completion.
        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);

        var guardStage = verdict.Stages.Single(s => s.Stage == "GUARD_DIED");
        if (guardStage.Outcome != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(guardStage.Outcome).IsEqualTo(StageOutcome.Pass);
        await Assert.That(guardStage.StepObserved).IsEqualTo(QuestComponentKind.Progress);
    }

    /// <summary>
    /// BUG-008 negative: guard spawned DEAD (Hp 0 -> IsDead) -> RunAct false ->
    /// the quest stalls at the guard-checking step and never completes. The
    /// PROGRESS stage's evaluation observes the quest still resting at Progress.
    /// </summary>
    [Test]
    public async Task CheckGuard_DeadGuard_StallsAtGuardStep_NeverCompletes()
    {
        var manifest = LoadManifest(DeadGuardManifestJson);
        var verdict = new QuestScenarioDriver().Run(manifest);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        var progressStage = verdict.Stages.Single(s => s.Stage == "PROGRESS");
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Pass);
        await Assert.That(progressStage.StepObserved).IsEqualTo(QuestComponentKind.Progress);
    }

    /// <summary>
    /// BUG-008 negative: guard despawned / unresolvable (no NPC in the world ->
    /// GetNpcByTemplateId null) -> RunAct false -> the quest stalls at the
    /// guard-checking step. Same stall as the dead guard - a missing guard must
    /// never let the escort objective silently pass.
    /// </summary>
    [Test]
    public async Task CheckGuard_NoGuardSpawned_StallsAtGuardStep_NeverCompletes()
    {
        var manifest = LoadManifest(NoGuardSpawnManifestJson);
        var verdict = new QuestScenarioDriver().Run(manifest);

        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        var progressStage = verdict.Stages.Single(s => s.Stage == "PROGRESS");
        await Assert.That(progressStage.Outcome).IsEqualTo(StageOutcome.Pass);
        await Assert.That(progressStage.StepObserved).IsEqualTo(QuestComponentKind.Progress);
    }
}
