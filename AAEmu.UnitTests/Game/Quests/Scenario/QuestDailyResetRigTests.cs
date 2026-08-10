using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M2 WI-10 (t_abafd918): daily/repeatable driver-fidelity rig (RESET stage).
///
/// Real fidelity path for the ~533 daily + 79 repeatable corpus contexts:
///   - a completed quest is refused on re-accept when the template is NOT
///     repeatable: CharacterQuests.AddQuest -> HasQuestCompleted &&
///     !template.Repeatable -> QuestDailyLimit refusal (CharacterQuests.cs:107-120)
///   - QuestDailyResetTask.Execute -> character.Quests.ResetDailyQuests(true)
///     clears the completed flags for detail Daily(7)/DailyHunt(10)/
///     DailyLivelihood(11)/DailyGroup(12) (CharacterQuests.cs:637-645,
///     ResetQuests clears the completed-block bits, :463-501)
///   - repeatable quests (REPEATABLE='t') re-accept immediately - the daily
///     limit does not apply (AddQuest only refuses when Repeatable == false)
///   - the census manifest's RESET stage drives ResetDailyQuests + the engine's
///     AddQuest re-accept on the main-run character (synthetic-block pattern:
///     the main run's completion is the block that reset must clear)
///
/// Fail-before: (1) QuestTemplateShape/BuildTemplate do not yet carry DetailId/
/// Repeatable, so ResetDailyQuests never clears the rigged template's flag and
/// the re-accept stays refused; (2) the 2-arg Quest ctor (AddQuest) resolves
/// SkillManager/ExpressTextManager/WorldManager singletons that SeedSingletons
/// does not yet seed; (3) the RESET stage is not handled by the driver. After
/// the WI-10 driver change all three resolve.
/// </summary>
[NotInParallel]
public class QuestDailyResetRigTests
{
    private const string DailyManifestJson = """
    {
      "questId": 9202,
      "name": "Daily reset shape rig (detail 7, non-repeatable)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 5,
        "detailId": 7,
        "repeatable": false,
        "components": [
          { "kind": "Start", "id": 92021, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9211 } ] },
          { "kind": "Progress", "id": 92022, "acts": [ { "type": "QuestActObjTalk", "npcId": 13453, "count": 1, "detailId": 9212 } ] },
          { "kind": "Ready", "id": 92023, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9213 } ] },
          { "kind": "Reward", "id": 92024, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9214 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "Talk", "npcId": 13453 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string DailyManifestWithResetJson = """
    {
      "questId": 9202,
      "name": "Daily reset shape rig (detail 7, non-repeatable)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 5,
        "detailId": 7,
        "repeatable": false,
        "components": [
          { "kind": "Start", "id": 92021, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9211 } ] },
          { "kind": "Progress", "id": 92022, "acts": [ { "type": "QuestActObjTalk", "npcId": 13453, "count": 1, "detailId": 9212 } ] },
          { "kind": "Ready", "id": 92023, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9213 } ] },
          { "kind": "Reward", "id": 92024, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9214 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "Talk", "npcId": 13453 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } },
        { "name": "RESET", "events": [], "expect": { "reAccepted": true } }
      ]
    }
    """;

    private const string RepeatableManifestJson = """
    {
      "questId": 9203,
      "name": "Repeatable reset shape rig (REPEATABLE=t)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 5,
        "detailId": 1,
        "repeatable": true,
        "components": [
          { "kind": "Start", "id": 92031, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9221 } ] },
          { "kind": "Progress", "id": 92032, "acts": [ { "type": "QuestActObjTalk", "npcId": 13453, "count": 1, "detailId": 9222 } ] },
          { "kind": "Ready", "id": 92033, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9223 } ] },
          { "kind": "Reward", "id": 92034, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9224 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "Talk", "npcId": 13453 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } }
      ]
    }
    """;

    private const string RepeatableManifestWithResetJson = """
    {
      "questId": 9203,
      "name": "Repeatable reset shape rig (REPEATABLE=t)",
      "acceptor": { "type": "Npc", "id": 13453 },
      "template": {
        "level": 5,
        "detailId": 1,
        "repeatable": true,
        "components": [
          { "kind": "Start", "id": 92031, "acts": [ { "type": "QuestActConAcceptNpc", "npcId": 13453, "detailId": 9221 } ] },
          { "kind": "Progress", "id": 92032, "acts": [ { "type": "QuestActObjTalk", "npcId": 13453, "count": 1, "detailId": 9222 } ] },
          { "kind": "Ready", "id": 92033, "acts": [ { "type": "QuestActConReportNpc", "npcId": 13453, "detailId": 9223 } ] },
          { "kind": "Reward", "id": 92034, "acts": [ { "type": "QuestActSupplyItem", "itemId": 30012, "gradeId": 0, "count": 1, "detailId": 9224 } ] }
        ]
      },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } },
        { "name": "PROGRESS", "events": [ { "type": "Talk", "npcId": 13453 } ], "expect": { "step": "Ready", "status": "Ready" } },
        { "name": "READY", "events": [ { "type": "ReportNpc", "npcId": 13453, "selected": 0 } ], "expect": { "step": "Reward", "status": "Completed" } },
        { "name": "REWARD", "events": [], "expect": { "completed": true } },
        { "name": "PERSIST", "events": [], "expect": { "persistRoundTrip": true } },
        { "name": "RESET", "events": [], "expect": { "reAccepted": true } }
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

    /// <summary>Registers the built template so the engine's GetTemplate path
    /// (CharacterQuests.AddQuest, CharacterQuests.cs:85) resolves it.</summary>
    private static void RegisterTemplate(QuestScenarioManifest manifest)
    {
        var template = QuestScenarioDriver.BuildTemplate(manifest);
        var field = typeof(QuestManager).GetField("_questTemplates", BindingFlags.NonPublic | BindingFlags.Instance);
        var dict = (Dictionary<uint, QuestTemplate>)field.GetValue(QuestManager.Instance);
        dict[template.Id] = template;
    }

    /// <summary>
    /// Engine-level pin (daily): the completed daily's re-accept is REFUSED by
    /// the engine (QuestDailyLimit, CharacterQuests.cs:107-120) until
    /// ResetDailyQuests(true) clears the completed flag (ResetQuests clears the
    /// detail Daily(7) block bit, CharacterQuests.cs:463-501); after the reset
    /// the engine AddQuest re-accepts and the quest is active again. This is
    /// the exact QuestDailyResetTask path the RESET stage drives.
    /// </summary>
    [Test]
    public async Task DailyQuest_Completed_AddQuestRefused_UntilResetDailyQuests()
    {
        var manifest = LoadManifest(DailyManifestJson);
        var verdict = new QuestScenarioDriver().Run(manifest);
        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT (main run):\n" + verdict);

        var character = (Character)verdict.QuestRef.Owner;
        RegisterTemplate(manifest);

        // Completed flag set by the main run; re-accept refused (daily limit).
        await Assert.That(character.Quests.HasQuestCompleted(9202)).IsTrue();
        var refused = character.Quests.AddQuest(9202);
        await Assert.That(refused).IsFalse();

        // The daily reset task's body: ResetDailyQuests(true) -> flag cleared.
        character.Quests.ResetDailyQuests(true);
        await Assert.That(character.Quests.HasQuestCompleted(9202)).IsFalse();

        // Re-accept through the engine: AddQuest -> StartQuest -> first step.
        var accepted = character.Quests.AddQuest(9202);
        await Assert.That(accepted).IsTrue();
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(9202)).IsTrue();
    }

    /// <summary>
    /// Engine-level pin (repeatable): a completed REPEATABLE='t' quest
    /// re-accepts IMMEDIATELY - the daily limit only refuses non-repeatable
    /// templates (CharacterQuests.cs:114). No reset needed.
    /// </summary>
    [Test]
    public async Task RepeatableQuest_Completed_AddQuestAccepted_WithoutReset()
    {
        var manifest = LoadManifest(RepeatableManifestJson);
        var verdict = new QuestScenarioDriver().Run(manifest);
        if (verdict.Overall != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT (main run):\n" + verdict);

        var character = (Character)verdict.QuestRef.Owner;
        RegisterTemplate(manifest);

        await Assert.That(character.Quests.HasQuestCompleted(9203)).IsTrue();
        var accepted = character.Quests.AddQuest(9203);
        await Assert.That(accepted).IsTrue();
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(9203)).IsTrue();
    }

    /// <summary>
    /// Driver-fidelity pin (repeatable): the RESET stage on a REPEATABLE='t'
    /// quest skips the reset (the daily limit does not apply) and re-accepts
    /// through the engine's AddQuest - the quest is active again (reAccepted).
    /// </summary>
    [Test]
    public async Task RepeatableQuest_ManifestWithResetStage_StagePasses()
    {
        var manifest = LoadManifest(RepeatableManifestWithResetJson);
        var verdict = new QuestScenarioDriver().Run(manifest);

        var resetStage = verdict.Stages.Single(s => s.Stage == "RESET");
        if (resetStage.Outcome != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(resetStage.Outcome).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
    }

    /// <summary>
    /// Driver-fidelity pin (fail-before on the pre-WI-10 driver): the RESET
    /// stage drives ResetDailyQuests + engine re-accept on the main-run
    /// character and asserts the quest is active again (reAccepted).
    /// </summary>
    [Test]
    public async Task DailyQuest_ManifestWithResetStage_StagePasses()
    {
        var manifest = LoadManifest(DailyManifestWithResetJson);
        var verdict = new QuestScenarioDriver().Run(manifest);

        var resetStage = verdict.Stages.Single(s => s.Stage == "RESET");
        if (resetStage.Outcome != StageOutcome.Pass)
            throw new Exception("DIAGNOSTIC VERDICT:\n" + verdict);

        await Assert.That(resetStage.Outcome).IsEqualTo(StageOutcome.Pass);
        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Pass);
    }
}
