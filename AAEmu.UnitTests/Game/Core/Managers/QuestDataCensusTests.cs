using AAEmu.Commons.IO;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M1-3: real-data quest census — boots QuestManager.Load() against the canonical
/// 1.2 compact.sqlite3 (the exact file the game server reads at startup) and
/// asserts the sanity verifier's COMPONENT_NEXT_MISSING findings are gone
/// (data-defects.md §3 overlay, fix/verifier-data-overlay).
///
/// The database file is gitignored (119 MB reference, upstream alignment rule 3 —
/// read-only): the test SKIPS when it is not present. To run the census locally,
/// drop the canonical file at AAEmu.Game/Data/compact.sqlite3 (the layout the game
/// server itself uses) and build — it lands under the test host's Data/ dir.
/// </summary>
[NotInParallel]
public class QuestDataCensusTests
{
    [Test]
    public async Task Census_RealCompactSqlite3_NoComponentNextMissingFindings()
    {
        var dbPath = Path.Combine(FileManager.AppPath, "Data", "compact.sqlite3");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine(
                $"[QuestCensus] SKIPPED — {dbPath} not found (canonical 1.2 compact.sqlite3 is gitignored; " +
                "place it at AAEmu.Game/Data/compact.sqlite3 to run the census)");
            return;
        }

        // Real load path: sqlite reference + overlay + verifier, exactly like GameService startup.
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        questManager.Load();

        var report = questManager.LastSanityReport;
        var nextMissing = report.Findings.Where(f => f.Code == "COMPONENT_NEXT_MISSING").ToList();

        Console.WriteLine(
            $"[QuestCensus] {report.ErrorCount} ERR / {report.WarnCount} WARN / {report.InfoCount} INFO " +
            $"across {report.QuestCount} quests / {report.ComponentCount} components / {report.ActCount} acts");
        foreach (var finding in nextMissing)
            Console.WriteLine($"[QuestCensus] {finding.Severity} {finding.Code}: {finding.Message}");

        await Assert.That(nextMissing.Count == 0).IsTrue();
    }
}
