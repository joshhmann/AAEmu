using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2 WI-10 (t_abafd918): QuestActCheckTimer driver-fidelity rig (TIMEOUT stage).
///
/// The act's RunAct returns true unconditionally (auto-pass), so the census
/// passes CheckTimer quests without ever exercising the timer. The REAL path:
/// InitializeAction -> QuestManager.AddQuestTimer (registers a QuestTimeoutTask
/// + sets quest.Time) -> QuestTimeoutTask.Execute -> QuestManager.OnTimerExpired
/// -> owner.Events.OnTimerExpired -> QuestActCheckTimer.OnTimerExpired ->
/// QuestManager.FailQuest (sets quest.Step = Fail, QuestManager.cs:182-193).
///
/// Engine facts pinned here:
///   - accepting a CheckTimer quest registers the timer (QuestTimeoutTask entry
///     for the owner + quest.Time in the future) - AddQuestTimer, QuestManager.cs:2013
///   - firing the timeout task's exact body (QuestManager.OnTimerExpired) FAILS
///     the quest (Step == Fail) - the fail path is real, not structural
///   - the census manifest's TIMEOUT stage drives this path on a fresh probe
///     quest and asserts step Fail (driver fidelity, WI-10)
///
/// Fail-before: the TIMEOUT stage is not handled by the driver (stage runs the
/// default branch -> step assertion fails); after the driver change the stage
/// passes. The engine-level pin passes on both (it drives the engine directly).
/// </summary>
[NotInParallel]
public class QuestCheckTimerRigTests
{
    private const string TimerShapeManifestJson = """
    {
      "questId": 9201,
      "name": "CheckTimer shape rig (real carriers: 350/1313/4292)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 5,
        "components": [
          { "kind": "Start", "id": 92011, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9201 } ] },
          { "kind": "Progress", "id": 92012, "acts": [ { "type": "QuestActCheckTimer", "limitTime": 60000, "nextComponent": 0, "detailId": 9202 } ] },
          { "kind": "Ready", "id": 92013, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9203 } ] },
          { "kind": "Reward", "id": 92014, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9204 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true, "failPathWired": true } },
        { "name": "TIMEOUT", "events": [], "expect": { "step": "Fail" } }
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
    /// Engine-level pin (passes with or without the driver stage): accepting a
    /// CheckTimer quest registers the quest timer (QuestTimeoutTask entry +
    /// quest.Time in the future), and firing the timeout task's exact execution
    /// body (QuestManager.OnTimerExpired - QuestTimeoutTask.cs:24) routes
    /// through owner.Events.OnTimerExpired to QuestActCheckTimer.OnTimerExpired
    /// and FAILS the quest (QuestManager.FailQuest sets Step = Fail).
    /// </summary>
    [Test]
    public async Task CheckTimer_Accept_RegistersTimer_ExpiryFiresFailQuest()
    {
        var manifest = LoadManifest(TimerShapeManifestJson);
        var quest = QuestScenarioDriver.BuildQuest(manifest);
        var character = (Character)quest.Owner;
        quest.StartQuest();
        character.Quests.ActiveQuests.Add(quest.TemplateId, quest);
        quest.RunCurrentStep();

        // Timer registered at quest construction (InitializeAction -> AddQuestTimer)
        await Assert.That(QuestManager.Instance.QuestTimeoutTask.ContainsKey(character.Id)).IsTrue();
        await Assert.That(QuestManager.Instance.QuestTimeoutTask[character.Id].ContainsKey(quest.TemplateId)).IsTrue();
        await Assert.That(quest.Time > DateTime.UtcNow).IsTrue();

        // Fire the timeout task's execution body: OnTimerExpired -> FailQuest
        QuestManager.Instance.OnTimerExpired(quest.Owner, quest.TemplateId);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Fail);
    }

    /// <summary>
    /// Driver-fidelity pin (fail-before on the pre-WI-10 driver): the census
    /// manifest's TIMEOUT stage drives the real expiry path on a fresh probe
    /// quest and asserts the quest FAILS (step Fail). The main lifecycle
    /// (START -> READY -> REWARD -> PERSIST) still completes normally - the
    /// timer never expires in the happy path.
    /// </summary>
    [Test]
    public async Task CheckTimer_ManifestWithTimeoutStage_DrivesExpiryPath_StagePasses()
    {
        var manifest = LoadManifest(TimerShapeManifestJson);
        var verdict = new QuestScenarioDriver().Run(manifest);

        var timeoutStage = verdict.Stages.Single(s => s.Stage == "TIMEOUT");
        if (timeoutStage.Outcome != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(timeoutStage.Outcome).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
    }
}
