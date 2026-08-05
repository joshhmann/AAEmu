using System.Diagnostics;
using System.Text;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M1-5b: tier runner - drives every manifest in Manifests/t1 (Solzreed golden
/// zone) and Manifests/t2 (kill-accept sample + CheckGuard + ItemGroup families)
/// through the scenario harness and writes scorecard-explorations/runnability.md.
///
/// A quest verdict is evidence, not a test outcome: PASS/FAIL/SKIP per quest
/// lands in the report. The test itself only asserts that every manifest ran
/// and the report was written (FAILs are findings for fix cards, by design).
/// </summary>
[NotInParallel]
public class QuestScenarioTierTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    private static List<string> DiscoverManifests(string tier)
    {
        var dir = Path.Combine(RepoRoot(), "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", tier);
        if (!Directory.Exists(dir))
            return [];
        return Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList();
    }

    [Test]
    public async Task TierManifests_DriveAndWriteRunnabilityReport()
    {
        var repoRoot = RepoRoot();
        var reportPath = Path.Combine(repoRoot, "scorecard-explorations", "runnability.md");

        QuestScenarioDriver.SeedSingletons();

        var rows = new List<(string Tier, uint QuestId, string Name, string Family, string Verdict, string Detail)>();
        var totals = new Dictionary<string, (int Pass, int Fail, int Skip)>();

        foreach (var tier in new[] { "t1", "t2" })
        {
            var files = DiscoverManifests(tier);
            await Assert.That(files.Count > 0).IsTrue();
            var stopwatch = Stopwatch.StartNew();

            foreach (var file in files)
            {
                var manifest = QuestScenarioManifest.LoadFromFile(file);
                QuestScenarioDriver.RegisterManifestItems(manifest);
                var verdict = new QuestScenarioDriver().Run(manifest);

                var outcome = verdict.Overall.ToString();
                var detail = verdict.Stages.Count > 0
                    ? string.Join("; ", verdict.Stages.Select(s => $"{s.Stage}:{s.Outcome}" +
                        (s.Outcome == StageOutcome.Fail || s.Outcome == StageOutcome.Skip ? $" ({s.Reason})" : "")))
                    : "no stages";
                rows.Add((tier, manifest.QuestId, manifest.Name, manifest.Family, outcome, detail));

                var t = totals.GetValueOrDefault(tier);
                if (outcome == nameof(StageOutcome.Pass)) t.Pass++;
                else if (outcome == nameof(StageOutcome.Fail)) t.Fail++;
                else t.Skip++;
                totals[tier] = t;
            }

            Console.WriteLine($"{tier}: {files.Count} quests in {stopwatch.Elapsed.TotalSeconds:F1}s");
        }

        // ---- write the report ----
        var sb = new StringBuilder();
        sb.AppendLine("# Quest Runnability — M1-5 scenario harness census");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z by QuestScenarioTierTests (M1-5b)");
        sb.AppendLine();
        sb.AppendLine("Verdict semantics: **PASS** = full lifecycle driven (start→progress→ready→reward→persist); " +
                      "**FAIL** = a stage assertion or engine exception (name the stage + reason); " +
                      "**SKIP** = not driven (broken refs / unsynthesizable shapes), reason in the detail column.");
        sb.AppendLine();
        sb.AppendLine("## Headline");
        sb.AppendLine();
        foreach (var tier in new[] { "t1", "t2" })
        {
            var t = totals.GetValueOrDefault(tier);
            var label = tier == "t1" ? "T1 golden zone (Solzreed)" : "T2 families (kill-accept/guard/item-group)";
            sb.AppendLine($"- **{label}**: {t.Pass} PASS / {t.Fail} FAIL / {t.Skip} SKIP");
        }
        sb.AppendLine();

        foreach (var tier in new[] { "t1", "t2" })
        {
            sb.AppendLine($"## {tier.ToUpperInvariant()} — per-quest verdicts");
            sb.AppendLine();
            sb.AppendLine("| quest | name | family | verdict | detail |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var (t, qid, name, family, verdict, detail) in rows.Where(r => r.Tier == tier))
            {
                var cleanDetail = detail.Replace("|", "\\|").Replace("\n", " ").Trim();
                sb.AppendLine($"| {qid} | {name.Replace("|", "\\|")} | {family} | {verdict} | {cleanDetail} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## FAIL rollup (by stage reason)");
        sb.AppendLine();
        foreach (var group in rows.Where(r => r.Verdict == "Fail")
                     .GroupBy(r => r.Detail.Split('(').First().Trim())
                     .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"- **{group.Key}** — {group.Count()} quests: {string.Join(", ", group.Select(g => g.QuestId))}");
        }
        sb.AppendLine();

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, sb.ToString());

        // Every manifest must have been driven (no silent drops).
        var expected = DiscoverManifests("t1").Count + DiscoverManifests("t2").Count;
        await Assert.That(rows.Count).IsEqualTo(expected);
        await Assert.That(File.Exists(reportPath)).IsTrue();
    }
}
