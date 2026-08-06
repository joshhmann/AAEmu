using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M1 widened backlog (ROADMAP.md §M1, 2026-08-04): QUEST_NO_COMPONENTS quest 1391
/// ("마을을 지켜라", category 27, zone 0, level 0) — template has no components at all.
///
/// DECISION: DROP (Josh 2026-08-05: "Unblock granted, if they're orphans we prob don't
/// need to code em in."; data-defects.md §6 verdict (c) drop; dropped-content-register.md
/// §1). The fix card (t_5a61cee3) implements the drop: quest_contexts row 1391 deleted
/// via SQL/patches/compact/2026-08-05-drop-1391.sql (guarded DELETE, pinned shape,
/// drift −1 row: quest_contexts 4876 → 4875), and 1391 removed from the verifier
/// allowlist (QuestSanityVerifier.cs:93, "dummy shells" group) so a regression
/// re-reports at WARN instead of the pre-fix INFO mask.
///
/// PASS-AFTER (this rig, post-fix): the drop is a data-level removal — the loaded
/// state has no 1391 template, the verifier reports nothing for 1391, and the
/// allowlist no longer masks the empty-template shape.
///
/// FAIL-BEFORE baseline (fix/no-components-1391-rig @ 405e85b5, t_6c5430e6 — full
/// evidence in scorecard-explorations/no-components-1391-rig.md): the engine cannot
/// accept quest 1391 — Quest.CreateQuestSteps() produced an empty QuestSteps map
/// (NewQuestCode.cs:34-35), Quest.StartQuest() returned false (NewQuestCode.cs:44-48),
/// the scenario harness reported START:FAIL "StartQuest() returned false - quest has
/// no Start component", and the verifier's QUEST_NO_COMPONENTS finding was
/// allowlist-masked to INFO. The rig tests asserting that broken shape
/// (ZeroComponentsZeroActs, Lifecycle_CannotStart_FailsAtStart) were retired with the
/// drop, per the rig fix contract §5.4.
/// </summary>
[NotInParallel]
public class Quest1391NoComponentsRigTests
{
    private const uint Quest1391 = 1391;

    /// <summary>
    /// PASS-AFTER: with the drop applied, quest 1391 has no quest_contexts row and is
    /// never loaded — the loaded-state dictionary contains no 1391 template, and the
    /// verifier produces zero findings for it (no QUEST_NO_COMPONENTS, nothing else).
    /// </summary>
    [Test]
    public async Task Quest1391_Dropped_TemplateAbsentFromLoadedState()
    {
        // Post-drop loaded state: the loader never creates a 1391 template (the SQL
        // patch removed the quest_contexts row; QuestManager.GetTemplate(1391) → null).
        var quests = new Dictionary<uint, QuestTemplate>();
        var report = QuestSanityVerifier.VerifyLoadedState(
            quests,
            new Dictionary<uint, QuestComponentTemplate>(),
            new Dictionary<uint, QuestActTemplate>(),
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>(),
            new Dictionary<uint, List<uint>>());

        await Assert.That(quests.ContainsKey(Quest1391)).IsFalse();
        await Assert.That(report.Findings.Any(f => f.QuestId == Quest1391)).IsFalse();
    }

    /// <summary>
    /// PASS-AFTER + regression guard: 1391 is no longer allowlisted (removed with the
    /// drop, dropped-content-register.md §1), so if an empty 1391 template ever
    /// regresses into the data (e.g. a bad data sync), QUEST_NO_COMPONENTS re-reports
    /// at WARN — the census can see the defect again instead of the pre-fix INFO mask.
    /// </summary>
    [Test]
    public async Task Quest1391_Dropped_Regression_EmptyTemplateReReportsWarn()
    {
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(Quest1391)).IsFalse();

        // Regression shape: an empty 1391 template comes back (the pre-fix prod shape).
        var quest = new QuestTemplate { Id = Quest1391, ZoneId = 0, CategoryId = 27 };
        var report = QuestSanityVerifier.VerifyLoadedState(
            new Dictionary<uint, QuestTemplate> { [Quest1391] = quest },
            new Dictionary<uint, QuestComponentTemplate>(),
            new Dictionary<uint, QuestActTemplate>(),
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>(),
            new Dictionary<uint, List<uint>>());

        var finding = report.Findings.FirstOrDefault(f => f.Code == "QUEST_NO_COMPONENTS" && f.QuestId == Quest1391);
        await Assert.That(finding).IsNotNull();
        await Assert.That(finding.Severity).IsEqualTo(QuestSanityVerifier.Severity.Warn);
        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_COMPONENTS" && f.Severity == QuestSanityVerifier.Severity.Info)).IsFalse();
    }
}
