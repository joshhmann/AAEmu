using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M1 rig: ACT_REF_MISSING_QUEST fail-before evidence for quest 2145 → 2146.
///
/// Models the REAL quest 2145 topology exactly as it ships in compact.sqlite3
/// (md5 78b3bdbf0383db3b927056106efdf91af — the same reference verified in
/// scorecard-explorations/data-defects.md §4), then runs the REAL
/// QuestSanityVerifier over it:
///
///   FAIL-BEFORE (raw data):  quest 2145 (다용도 옷감을 만들어보세요, "make
///   versatile fabric") loads with 3 components — 9925 Start(2), 9926
///   Progress(4), 9927 Reward(8). The Reward comp carries act 89
///   (QuestActConAcceptComponent, quest_act_con_accept_components id 89 →
///   quest_context_id 2146). Quest context 2146 has NO quest_contexts row
///   (count 0) and is therefore never loaded into the template dictionary —
///   the verifier's ACT_REF_MISSING_QUEST finding fires for quest 2145 and
///   GetTemplate(2146) can never return a template: the self-start target can
///   never be found.
///
///   Contrast: the same quest's Start comp act 88 (ConAcceptComponent → 2145,
///   self-reference) resolves fine because 2145 IS loaded — proving the defect
///   is specifically the missing 2146 context, not the self-start pattern
///   itself (M1-2 watch item).
///
///   PASS-AFTER (data fix):  delete the dangling act (quest_act_con_accept_components
///   id 89 + quest_acts row 14121 — the documented minimal action from
///   data-defects.md §4) and the finding disappears; the self-start act 88
///   stays untouched and still resolves.
///
/// Note on severity: quest 2145 is in the verifier allowlist (the whole cat-34
/// chain was classified dead in data-defects.md §4), so the finding reports at
/// Info — but the finding CODE still fires, which is the fail-before proof.
/// Non-allowlisted quests with the same shape report Error (see
/// VerifyLoadedState_ConAcceptComponentMissingQuest_ReportsError).
/// </summary>
public class QuestActRefMissingQuestRigTests
{
    private const uint Quest2145 = 2145;
    private const uint Quest2146 = 2146;

    /// <summary>Reward component of quest 2145 (kind 8) — carries the dangling act.</summary>
    private const uint RewardComp = 9927;

    /// <summary>Start component of quest 2145 (kind 2) — carries the self-start act.</summary>
    private const uint StartComp = 9925;

    /// <summary>quest_act_con_accept_components id 89 → quest_context_id 2146 (dangling).</summary>
    private const uint DanglingActDetailId = 89;

    /// <summary>quest_act_con_accept_components id 88 → quest_context_id 2145 (self-start, valid).</summary>
    private const uint SelfStartActDetailId = 88;

    /// <summary>
    /// Builds the loaded-state dictionaries for quest 2145 exactly as the loaders
    /// would against prod data: 2145 present with its 3 components (kinds from
    /// quest_components.component_kind_id), act instances wired into their
    /// components' ActTemplates, and — crucially — NO entry for 2146 (it has no
    /// quest_contexts row, so LoadQuestContexts never creates its template).
    /// </summary>
    private static (Dictionary<uint, QuestTemplate> Quests, Dictionary<uint, QuestComponentTemplate> Components) BuildState(bool danglingActPresent)
    {
        var quest = new QuestTemplate { Id = Quest2145, ZoneId = 1, CategoryId = 34 };

        var start = new QuestComponentTemplate(quest) { Id = StartComp, KindId = QuestComponentKind.Start };
        // Self-start act 88 → 2145: the same pattern, but the target IS loaded → resolves.
        start.ActTemplates.Add(new QuestActConAcceptComponent(start) { DetailId = SelfStartActDetailId, QuestContextId = Quest2145 });

        var progress = new QuestComponentTemplate(quest) { Id = 9926, KindId = QuestComponentKind.Progress };

        var reward = new QuestComponentTemplate(quest) { Id = RewardComp, KindId = QuestComponentKind.Reward };
        if (danglingActPresent)
        {
            // The prod defect: ConAcceptComponent act 89 targets quest context 2146,
            // which has no quest_contexts row → never loaded → can never be found.
            reward.ActTemplates.Add(new QuestActConAcceptComponent(reward) { DetailId = DanglingActDetailId, QuestContextId = Quest2146 });
        }

        quest.Components[StartComp] = start;
        quest.Components[9926] = progress;
        quest.Components[RewardComp] = reward;

        var components = new Dictionary<uint, QuestComponentTemplate>
        {
            [StartComp] = start,
            [9926] = progress,
            [RewardComp] = reward,
        };

        // 2146 is deliberately absent — no quest_contexts row, never loaded.
        return (new Dictionary<uint, QuestTemplate> { [Quest2145] = quest }, components);
    }

    private static QuestSanityVerifier.SanityReport Run(bool danglingActPresent)
    {
        var (quests, components) = BuildState(danglingActPresent);
        return QuestSanityVerifier.VerifyLoadedState(
            quests, components,
            new Dictionary<uint, QuestActTemplate>(),
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>(),
            new Dictionary<uint, List<uint>>());
    }

    [Test]
    public async Task VerifyLoadedState_Quest2145_RawData_FailActRefMissingQuest()
    {
        // FAIL-BEFORE: raw prod topology (dangling act 89 present, 2146 never
        // loaded) must trip ACT_REF_MISSING_QUEST for quest 2145.
        var report = Run(danglingActPresent: true);

        var findings = report.Findings.Where(f => f.Code == "ACT_REF_MISSING_QUEST").ToList();

        await Assert.That(findings.Count == 1).IsTrue();

        var finding = findings[0];
        await Assert.That(finding.QuestId == Quest2145).IsTrue();
        await Assert.That(finding.Message.Contains("component 9927")).IsTrue();
        await Assert.That(finding.Message.Contains("act 89")).IsTrue();
        await Assert.That(finding.Message.Contains("quest context 2146")).IsTrue();
        await Assert.That(finding.Message.Contains("self-start target can never be found")).IsTrue();

        // 2145 is allowlisted (cat-34 chain classified dead, data-defects.md §4) →
        // the finding reports at Info but still fires. Pre-allowlist severity is
        // Error (proven for non-allowlisted quests by
        // VerifyLoadedState_ConAcceptComponentMissingQuest_ReportsError).
        await Assert.That(finding.Severity == QuestSanityVerifier.Severity.Info).IsTrue();
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(Quest2145)).IsTrue();

        // The runtime truth: 2146 has no quest_contexts row → no template entry →
        // QuestManager.GetTemplate(2146) can never find it.
        var (quests, _) = BuildState(danglingActPresent: true);
        await Assert.That(quests.ContainsKey(Quest2146)).IsFalse();
    }

    [Test]
    public async Task VerifyLoadedState_Quest2145_DanglingActRemoved_Pass()
    {
        // PASS-AFTER: apply the documented minimal fix from data-defects.md §4 —
        // delete the dangling act (quest_act_con_accept_components id 89 + its
        // quest_acts row 14121). The self-start act 88 on the Start comp stays
        // and still resolves (its target 2145 is loaded).
        var report = Run(danglingActPresent: false);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_QUEST")).IsFalse();
    }
}
