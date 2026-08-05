using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M1 widened backlog (ROADMAP.md §M1, 2026-08-04): QUEST_NO_COMPONENTS quest 1391
/// ("마을을 지켜라", category 27, zone 0, level 0) — template has no components at all.
///
/// GROUND TRUTH (prod compact.sqlite3, md5 78b3bdbf038db3b927056106efdf91af):
///   quest_contexts  id=1391  name='마을을 지켜라'  category_id=27  zone_id=0  LEVEL=0
///   quest_components WHERE quest_context_id=1391  -> 0 rows
///   quest_acts      (via quest_components)        -> 0 rows
/// No accept path exists anywhere: no components -> no Start component -> no
/// QuestActConAccept* act -> no NPC/doodad/sphere/item can offer quest 1391.
///
/// FAIL-BEFORE EVIDENCE (this rig, pre-fix): the engine cannot accept quest 1391 —
/// Quest.CreateQuestSteps() produces an empty QuestSteps map (NewQuestCode.cs:34-35),
/// Quest.StartQuest() returns false (NewQuestCode.cs:44-48, no Start entry), and the
/// scenario harness reports START:FAIL "StartQuest() returned false - quest has no
/// Start component". The verifier flags the shape (QUEST_NO_COMPONENTS, Warn) but the
/// allowlist (data-defects.md §6, "dummy" shell) masks it to INFO (QuestSanityVerifier.cs:93),
/// so the live census stays green while the quest stays permanently dead — the exact
/// silent-defect class this rig exists to expose.
///
/// FIX CONTRACT (for the downstream fix card): restore the template's components from
/// the canonical client data (SQL patch on compact.sqlite3: quest_components +
/// quest_acts + quest_act_* rows for quest 1391) AND remove 1391 from the verifier
/// allowlist so a regression re-reports at WARN. After the fix, this rig's manifest
/// gains the restored component shape and Quest1391_Lifecycle_CannotStart_FailsAtStart
/// flips to assert PASS (fix card updates the test, per the fail-before convention).
/// </summary>
/// <remarks>
/// The driver swaps game singletons (QuestManager/ItemManager/UnitRequirementsGameData)
/// per test run, so this class must not execute in parallel with itself or with
/// other classes that touch the same singletons.
/// </remarks>
[NotInParallel]
public class Quest1391NoComponentsRigTests
{
    /// <summary>
    /// Real 1391 shape from compact.sqlite3: zero components, zero acts. The acceptor
    /// is moot — with no Start component there is no accept act to satisfy, and the
    /// harness never gets past StartQuest(). The START stage documents the healthy
    /// expectation (a startable quest rests at Progress/Progress); the run fails
    /// before any stage evaluation with the engine's own reason.
    /// </summary>
    private const string Quest1391ManifestJson = """
    {
      "questId": 1391,
      "name": "마을을 지켜라 (no-components rig)",
      "zoneId": 0,
      "categoryId": 27,
      "level": 0,
      "letItDone": true,
      "score": 0,
      "family": "no-components",
      "acceptor": { "type": "Npc", "id": 0 },
      "template": { "level": 0, "components": [] },
      "stages": [
        { "name": "START", "events": [], "expect": { "step": "Progress", "status": "Progress" } }
      ]
    }
    """;

    /// <summary>
    /// Data-level proof: the 1391 template built from the real shape carries zero
    /// components (and therefore zero acts) — the template loader has nothing to
    /// instantiate, so QuestSteps comes out empty.
    /// </summary>
    [Test]
    public async Task Quest1391_TemplateShape_ZeroComponentsZeroActs()
    {
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.Load(Quest1391ManifestJson);
        var template = QuestScenarioDriver.BuildTemplate(manifest);

        await Assert.That(template.Id).IsEqualTo(1391u);
        await Assert.That(template.Components.Count).IsEqualTo(0);
    }

    /// <summary>
    /// THE fail-before evidence: driving quest 1391's real shape through the scenario
    /// harness fails at START — Quest.StartQuest() returns false because QuestSteps
    /// has no Start entry (there are no components at all). The quest can never be
    /// accepted, never progress, never reward: permanently dead content that the
    /// allowlist currently masks to INFO in the verifier census.
    /// </summary>
    [Test]
    public async Task Quest1391_Lifecycle_CannotStart_FailsAtStart()
    {
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.Load(Quest1391ManifestJson);
        QuestScenarioDriver.RegisterManifestItems(manifest);
        var verdict = new QuestScenarioDriver().Run(manifest);

        await Assert.That(verdict.Overall).IsEqualTo(StageOutcome.Fail);
        await Assert.That(verdict.Stages.Count).IsEqualTo(1);
        await Assert.That(verdict.Stages[0].Stage).IsEqualTo("START");
        await Assert.That(verdict.Stages[0].Outcome).IsEqualTo(StageOutcome.Fail);
        await Assert.That(verdict.Stages[0].Reason.Contains("StartQuest() returned false")).IsTrue();
        await Assert.That(verdict.Stages[0].Reason.Contains("no Start component")).IsTrue();
    }

    /// <summary>
    /// Verifier cross-check pinned to quest 1391: the QUEST_NO_COMPONENTS finding
    /// fires (data-defects.md §6) but the allowlist downgrades it to INFO — the
    /// census cannot see the defect. The fix card must remove 1391 from
    /// QuestSanityVerifier's allowlist (QuestSanityVerifier.cs:93) so a regression
    /// re-reports at WARN.
    /// </summary>
    [Test]
    public async Task Quest1391_Verifier_NoComponentsFindingMaskedToInfoByAllowlist()
    {
        var quest = new QuestTemplate { Id = 1391, ZoneId = 0, CategoryId = 27 };
        var report = QuestSanityVerifier.VerifyLoadedState(
            new Dictionary<uint, QuestTemplate> { [1391] = quest },
            new Dictionary<uint, QuestComponentTemplate>(),
            new Dictionary<uint, QuestActTemplate>(),
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>(),
            new Dictionary<uint, List<uint>>());

        var finding = report.Findings.FirstOrDefault(f => f.Code == "QUEST_NO_COMPONENTS" && f.QuestId == 1391u);
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding.Severity).IsEqualTo(QuestSanityVerifier.Severity.Info);
        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_COMPONENTS" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsFalse();
    }
}
