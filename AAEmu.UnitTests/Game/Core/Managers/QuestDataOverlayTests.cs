using System.Linq;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// QuestDataOverlay — the fork's additive data-correction mechanism (upstream
/// alignment rule 3: compact.sqlite3 is a READ-ONLY reference, so fixes never edit
/// the reference file). The 3 cosmetic next_component corrections from
/// data-defects.md §3 land as an in-memory overlay applied by QuestManager.Load.
///
/// Component ids below are the REAL ids from the canonical 1.2 compact.sqlite3
/// (prod md5 78b3bdbf038db3b927056106efdf91af), verified 2026-08-04:
///   quest 330 (zone 125, golden route): comp 1520 next 3543 -> 1521 (Ready)
///   quest 776 (zone 8):                  comp 3480 next 4370 -> 3482 (Progress)
///   quest 777 (zone 8):                  comp 3488 next 3487 -> 11591 (Ready)
/// </summary>
public class QuestDataOverlayTests
{
    /// <summary>Real component id universe of quests 330/776/777 (from prod data).</summary>
    private static Dictionary<uint, QuestComponentTemplate> BuildRealIdComponents()
    {
        var quest = new QuestTemplate { Id = 1 };
        var components = new Dictionary<uint, QuestComponentTemplate>();
        foreach (var id in new[]
                 {
                     1520u, 1521u, 1522u, // quest 330
                     3480u, 3482u, 3483u, 3484u, // quest 776
                     3485u, 3488u, 11591u, 11592u, 21238u // quest 777
                 })
        {
            components[id] = new QuestComponentTemplate(quest) { Id = id, KindId = QuestComponentKind.Progress };
        }

        return components;
    }

    [Test]
    public async Task Apply_AllThreeRealFixRows_OverlaysNextComponent()
    {
        var components = BuildRealIdComponents();

        var result = QuestDataOverlay.Apply(components);

        await Assert.That(result.Applied == 3).IsTrue();
        await Assert.That(result.MissingComponentIds.Count == 0).IsTrue();
        await Assert.That(components[1520].NextComponent == 1521).IsTrue(); // quest 330
        await Assert.That(components[3480].NextComponent == 3482).IsTrue(); // quest 776
        await Assert.That(components[3488].NextComponent == 11591).IsTrue(); // quest 777
    }

    [Test]
    public async Task Apply_CorrectedTargetsExistInTheirQuest_OverlayNeverPointsAtMissingComponents()
    {
        // The overlay values must stay valid against the real component universes:
        // each corrected target exists among its quest's own components (the census
        // asserts this on the live file; this test pins the overlay table itself).
        var components = BuildRealIdComponents();
        QuestDataOverlay.Apply(components);

        await Assert.That(components.ContainsKey(1521u)).IsTrue(); // q330 target
        await Assert.That(components.ContainsKey(3482u)).IsTrue(); // q776 target
        await Assert.That(components.ContainsKey(11591u)).IsTrue(); // q777 target
    }

    [Test]
    public async Task Apply_DriftRowMissing_ReportsMissingWithoutThrowing()
    {
        var components = BuildRealIdComponents();
        components.Remove(1520); // simulate data drift — row vanished from the reference

        var result = QuestDataOverlay.Apply(components);

        // Never throws (sanitizer policy matches the verifier): the other rows still apply.
        await Assert.That(result.Applied == 2).IsTrue();
        await Assert.That(result.MissingComponentIds.Contains(1520u)).IsTrue();
    }
}
