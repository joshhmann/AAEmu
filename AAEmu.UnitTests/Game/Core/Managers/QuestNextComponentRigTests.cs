using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M1 rig: COMPONENT_NEXT_MISSING fail-before evidence for quests 776/777 (and 330).
///
/// Models the REAL quest_components topology of quests 330/776/777 exactly as it
/// ships in compact.sqlite3 (md5 78b3bdbf038db3b927056106efdf91af — the same
/// reference verified in scorecard-explorations/data-defects.md §3), then runs the
/// REAL QuestSanityVerifier over it:
///
///   FAIL-BEFORE (raw data):  comp 3480 (quest 776) next_component 4370,
///   comp 3488 (quest 777) next_component 3487, comp 1520 (quest 330)
///   next_component 3543 — all three targets exist in NO quest_component row
///   anywhere, so the verifier's COMPONENT_NEXT_MISSING finding fires for each.
///
///   PASS-AFTER (data fix):  the same three rows after the 3 UPDATEs from
///   SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql
///   (1520→1521, 3480→3482, 3488→11591 — all real sibling components) report
///   zero findings.
///
/// Component kinds come from compact.sqlite3 quest_components.component_kind_id:
///   330: 1520 Start(2) · 1521 Ready(6) · 1522 Reward(8)
///   776: 3480 Start(2) · 3482 Progress(4) · 3483 Ready(6) · 3484 Reward(8)
///   777: 3485 Start(2) · 3488 Progress(4) · 11591 Ready(6) · 11592 Reward(8) · 21238 Progress(4)
/// </summary>
public class QuestNextComponentRigTests
{
    /// <summary>One prod-shaped quest row: quest id + component id + kind + next_component.</summary>
    private sealed record Comp(uint QuestId, uint CompId, QuestComponentKind Kind, uint Next);

    private const uint Quest330 = 330;
    private const uint Quest776 = 776;
    private const uint Quest777 = 777;

    /// <summary>Raw pre-fix rows (compact.sqlite3 md5 78b3bdbf038db3b927056106efdf91af).</summary>
    private static readonly Comp[] s_preFixRows =
    [
        new(Quest330, 1520, QuestComponentKind.Start, 3543), // dangling → 3543 exists in no quest
        new(Quest330, 1521, QuestComponentKind.Ready, 0),
        new(Quest330, 1522, QuestComponentKind.Reward, 0),
        new(Quest776, 3480, QuestComponentKind.Start, 4370), // dangling → 4370 exists in no quest
        new(Quest776, 3482, QuestComponentKind.Progress, 0),
        new(Quest776, 3483, QuestComponentKind.Ready, 0),
        new(Quest776, 3484, QuestComponentKind.Reward, 0),
        new(Quest777, 3485, QuestComponentKind.Start, 0),
        new(Quest777, 3488, QuestComponentKind.Progress, 3487), // dangling → 3487 exists in no quest
        new(Quest777, 11591, QuestComponentKind.Ready, 0),
        new(Quest777, 11592, QuestComponentKind.Reward, 0),
        new(Quest777, 21238, QuestComponentKind.Progress, 0),
    ];

    /// <summary>The 3-row data fix, verbatim from SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql.</summary>
    private static readonly (uint CompId, uint NewNext)[] s_fixUpdates =
    [
        (1520, 1521), // quest 330: 3543 → Ready comp 1521
        (3480, 3482), // quest 776: 4370 → Progress comp 3482
        (3488, 11591), // quest 777: 3487 → Ready comp 11591
    ];

    /// <summary>Builds the loaded-state dictionaries (quest templates + components) for the prod rows.</summary>
    private static (Dictionary<uint, QuestTemplate> Quests, Dictionary<uint, QuestComponentTemplate> Components) BuildState(Comp[] rows)
    {
        var quests = new Dictionary<uint, QuestTemplate>();
        var components = new Dictionary<uint, QuestComponentTemplate>();

        foreach (var (questId, zone, category) in new (uint, uint, uint)[]
                 {
                     (Quest330, 125, 4), // 나를 찾는 사람 (data-defects.md §3)
                     (Quest776, 8, 14), // 해적과 오크 (Pirates and Orcs)
                     (Quest777, 8, 14), // 오크의 그늘 아래 (Under the Orc's shadow)
                 })
        {
            quests[questId] = new QuestTemplate { Id = questId, ZoneId = zone, CategoryId = category };
        }

        foreach (var row in rows)
        {
            var component = new QuestComponentTemplate(quests[row.QuestId])
            {
                Id = row.CompId,
                KindId = row.Kind,
                NextComponent = row.Next,
            };
            quests[row.QuestId].Components[row.CompId] = component;
            components[row.CompId] = component;
        }

        return (quests, components);
    }

    private static QuestSanityVerifier.SanityReport Run(Comp[] rows)
    {
        var (quests, components) = BuildState(rows);
        return QuestSanityVerifier.VerifyLoadedState(
            quests, components,
            new Dictionary<uint, QuestActTemplate>(),
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>(),
            new Dictionary<uint, List<uint>>());
    }

    [Test]
    public async Task VerifyLoadedState_ProdQuests776_777_330_RawData_FailComponentNextMissing()
    {
        // FAIL-BEFORE: the raw prod data (no overlay, no SQL patch applied) must
        // trip COMPONENT_NEXT_MISSING on all three quests.
        var report = Run(s_preFixRows);

        var findings = report.Findings.Where(f => f.Code == "COMPONENT_NEXT_MISSING").ToList();

        await Assert.That(findings.Count == 3).IsTrue();

        var byQuest = findings.ToDictionary(f => f.QuestId!.Value);

        await Assert.That(byQuest.ContainsKey(Quest776)).IsTrue();
        await Assert.That(byQuest[Quest776].Message.Contains("component 3480")).IsTrue();
        await Assert.That(byQuest[Quest776].Message.Contains("next_component 4370")).IsTrue();
        await Assert.That(byQuest[Quest776].Severity == QuestSanityVerifier.Severity.Warn).IsTrue();

        await Assert.That(byQuest.ContainsKey(Quest777)).IsTrue();
        await Assert.That(byQuest[Quest777].Message.Contains("component 3488")).IsTrue();
        await Assert.That(byQuest[Quest777].Message.Contains("next_component 3487")).IsTrue();
        await Assert.That(byQuest[Quest777].Severity == QuestSanityVerifier.Severity.Warn).IsTrue();

        await Assert.That(byQuest.ContainsKey(Quest330)).IsTrue();
        await Assert.That(byQuest[Quest330].Message.Contains("component 1520")).IsTrue();
        await Assert.That(byQuest[Quest330].Message.Contains("next_component 3543")).IsTrue();
        await Assert.That(byQuest[Quest330].Severity == QuestSanityVerifier.Severity.Warn).IsTrue();

        // Not allowlisted → the finding keeps real Warn severity (an allowlisted
        // quest would report at Info; see Allowlist_ContainsClassifiedShells).
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(Quest776)).IsFalse();
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(Quest777)).IsFalse();
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(Quest330)).IsFalse();
    }

    [Test]
    public async Task VerifyLoadedState_ProdQuests776_777_330_DataFixApplied_Pass()
    {
        // PASS-AFTER: apply the exact 3 UPDATEs from
        // SQL/patches/compact/2026-08-04-fix-quest-data-defects.sql (the fix the
        // additive QuestDataOverlay mirrors at runtime) — the census must go green.
        var fixedRows = s_preFixRows
            .Select(row => s_fixUpdates
                .Where(fix => fix.CompId == row.CompId)
                .Select(fix => row with { Next = fix.NewNext })
                .DefaultIfEmpty(row)
                .Single())
            .ToArray();

        var report = Run(fixedRows);

        await Assert.That(report.Findings.Count == 0).IsTrue();
        await Assert.That(report.Findings.Any(f => f.Code == "COMPONENT_NEXT_MISSING")).IsFalse();
    }
}
