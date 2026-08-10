using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// M1-5c + M2a + M2c: tier runner - drives every manifest in Manifests/t1
/// (Solzreed golden zone), Manifests/t2 (kill-accept sample + CheckGuard +
/// ItemGroup families), Manifests/t3 (M1-5c stratified act-family census,
/// frozen sample), Manifests/t4 (M2a wave-1: band 1-20 quests carrying the
/// closed act families cinema/etc-item-obtain/accept-item-gain/supply-LP),
/// Manifests/t5 (M2a wave-2: express-fire/aggro/CCC/honor) and the M2a/M2c
/// full-band census tiers t6 (band 1-10) / t7 (band 11-20) / t8 (band
/// 21-30) through the scenario harness and writes
/// scorecard-explorations/runnability.md (per-tier tables, headline number,
/// FAIL rollup by act family, SKIP rollup, recommended fix-card queue, band
/// census acceptance table, zone-coverage rows).
///
/// A quest verdict is evidence, not a test outcome: PASS/FAIL/SKIP per quest
/// lands in the report. The test itself only asserts that every manifest ran
/// and the report was written (FAILs are findings for fix cards, by design).
/// The report header is deterministic (no wall-clock) - census regen must not
/// churn commits (M2a acceptance). The band denominators + signature-zone map
/// come from Manifests/census-meta.json (written by gen-manifests.py).
/// </summary>
[NotInParallel]
public class QuestScenarioTierTests
{
    private static readonly (string Tier, string Label)[] Tiers =
    [
        ("t1", "T1 golden zone (Solzreed)"),
        ("t2", "T2 families (kill-accept/guard/item-group)"),
        ("t3", "T3 stratified act-family census (frozen M1-5c sample)"),
        ("t4", "T4 M2a wave-1 (band 1-20: cinema/etc-obtain/CAIG+LP)"),
        ("t5", "T5 M2a wave-2 (band 1-20: express-fire/aggro/CCC/honor)"),
        ("t6", "T6 M2a census (band 1-10 full sweep)"),
        ("t7", "T7 M2a census (band 11-20 full sweep)"),
        ("t8", "T8 M2c census (band 21-30 full sweep)"),
        ("t9", "T9 M2 WI-2 (CrimePoint supply carriers)"),
        ("t10", "T10 M2 WI-3 (AbilityLevel objective carriers)"),
        ("t11", "T11 M2 WI-4 (MateLevel objective carriers)"),
        ("t12", "T12 M2 WI-5 (CompleteQuest objective carriers)")
    ];

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

    /// <summary>First engine frame in an exception reason ("Foo.cs:line N"),
    /// preferring AAEmu.Game frames; falls back to the harness assertion.</summary>
    private static string ExtractFileLine(string reason)
    {
        var matches = Regex.Matches(reason, @"([A-Za-z0-9_./\\-]+\.cs):line (\d+)");
        Match best = null;
        foreach (Match m in matches)
        {
            if (best == null)
                best = m;
            if (m.Groups[1].Value.Contains("AAEmu.Game"))
            {
                best = m;
                break;
            }
        }
        if (best == null)
            return "harness assertion";
        var leaf = best.Groups[1].Value.Split('\\', '/').Last();
        return $"{leaf}:{best.Groups[2].Value}";
    }

    [Test]
    public async Task TierManifests_DriveAndWriteRunnabilityReport()
    {
        var repoRoot = RepoRoot();
        var reportPath = Path.Combine(repoRoot, "scorecard-explorations", "runnability.md");

        QuestScenarioDriver.SeedSingletons();

        var rows = new List<(string Tier, uint QuestId, string Name, string Family, string Verdict, string Detail)>();
        var totals = new Dictionary<string, (int Pass, int Fail, int Skip)>();
        var manifestsByQuest = new Dictionary<uint, QuestScenarioManifest>();

        foreach (var (tier, _) in Tiers)
        {
            var files = DiscoverManifests(tier);
            await Assert.That(files.Count > 0).IsTrue();
            var stopwatch = Stopwatch.StartNew();

            foreach (var file in files)
            {
                var manifest = QuestScenarioManifest.LoadFromFile(file);
                manifestsByQuest[manifest.QuestId] = manifest;
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

        var all = totals.Values.Aggregate((Pass: 0, Fail: 0, Skip: 0),
            (acc, t) => (acc.Pass + t.Pass, acc.Fail + t.Fail, acc.Skip + t.Skip));
        var driven = all.Pass + all.Fail;

        // ---- FAIL rollup by act family: tally every act type carried by the
        // failing quests' manifests (a quest with several act families counts
        // for each) - the top blockers are the families most often implicated.
        var failingActTally = new Dictionary<string, int>();
        foreach (var (tier, qid, _, _, verdict, _) in rows.Where(r => r.Verdict == "Fail"))
        {
            if (!manifestsByQuest.TryGetValue(qid, out var manifest))
                continue;
            foreach (var act in manifest.Template.Components.SelectMany(c => c.Acts))
            {
                if (act.TryGetProperty("type", out var typeElement) && typeElement.GetString() is { } actType)
                    failingActTally[actType] = failingActTally.GetValueOrDefault(actType) + 1;
            }
        }

        // ---- write the report ----
        var sb = new StringBuilder();
        sb.AppendLine("# Quest Runnability — M1-5 + M2a/M2c (wave closures + band 1-30 census)");
        sb.AppendLine();
        sb.AppendLine("Generated by QuestScenarioTierTests (deterministic — no wall-clock)");
        sb.AppendLine();
        sb.AppendLine("Verdict semantics: **PASS** = full lifecycle driven (start→progress→ready→reward→persist); " +
                      "**FAIL** = a stage assertion or engine exception (name the stage + reason); " +
                      "**SKIP** = not driven (broken refs / unsynthesizable shapes), reason in the detail column.");
        sb.AppendLine();
        sb.AppendLine("## Headline");
        sb.AppendLine();
        foreach (var (tier, label) in Tiers)
        {
            var t = totals.GetValueOrDefault(tier);
            sb.AppendLine($"- **{label}**: {t.Pass} PASS / {t.Fail} FAIL / {t.Skip} SKIP");
        }
        sb.AppendLine($"- **ALL TIERS (census)**: {all.Pass} PASS / {all.Fail} FAIL / {all.Skip} SKIP over " +
                      $"{all.Pass + all.Fail + all.Skip} quests — **{all.Pass}/{driven} quests runnable** " +
                      $"({all.Skip} SKIP not driven, reasons below)");
        sb.AppendLine();

        // ---- Band census (acceptance): denominators from census-meta.json
        // (written by gen-manifests.py), verdicts from every tier - each quest
        // is driven exactly once, so band membership by manifest level is the
        // full non-dropped band coverage. PASS-or-doc-SKIP % is the acceptance
        // metric (M2_PLAN.md §4, bar >= 95%). ----
        var metaPath = Path.Combine(repoRoot, "AAEmu.UnitTests", "Game", "Quests", "Scenario",
            "Manifests", "census-meta.json");
        if (File.Exists(metaPath))
        {
            using var metaDoc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var bands = metaDoc.RootElement.GetProperty("bands");
            sb.AppendLine("## Band census (acceptance)");
            sb.AppendLine();
            sb.AppendLine("| band | tier | total | dropped | non-dropped | driven | PASS | FAIL | SKIP | PASS-or-doc-SKIP |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var bandProp in bands.EnumerateObject())
            {
                var band = bandProp.Value;
                var bounds = bandProp.Name.Split('-').Select(int.Parse).ToArray();
                var nonDropped = band.GetProperty("nonDropped").GetInt32();
                var bandRows = rows.Where(r =>
                    manifestsByQuest.TryGetValue(r.QuestId, out var m) &&
                    m.Template.Level >= bounds[0] && m.Template.Level <= bounds[1]).ToList();
                var bPass = bandRows.Count(r => r.Verdict == "Pass");
                var bFail = bandRows.Count(r => r.Verdict == "Fail");
                var bSkip = bandRows.Count(r => r.Verdict == "Skip");
                var pct = nonDropped > 0 ? 100.0 * (bPass + bSkip) / nonDropped : 100.0;
                sb.AppendLine($"| {bandProp.Name} | {band.GetProperty("tier").GetString()} | " +
                              $"{band.GetProperty("total").GetInt32()} | {band.GetProperty("dropped").GetArrayLength()} | " +
                              $"{nonDropped} | {bandRows.Count} | {bPass} | {bFail} | {bSkip} | {pct:F1}% |");
            }
            sb.AppendLine();

            // ---- Zone coverage: signature zones (real ids only, from
            // census-meta.json) x band - driven quests + verdict split. ----
            sb.AppendLine("## Zone coverage (signature zones)");
            sb.AppendLine();
            sb.AppendLine("| zone | band | quests | PASS | FAIL | SKIP |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var zone in metaDoc.RootElement.GetProperty("signatureZones").EnumerateArray())
            {
                var zoneName = zone.GetProperty("name").GetString();
                var zoneIds = zone.GetProperty("zoneIds").EnumerateArray().Select(z => z.GetUInt32()).ToHashSet();
                foreach (var bandProp in bands.EnumerateObject())
                {
                    var bounds = bandProp.Name.Split('-').Select(int.Parse).ToArray();
                    var zoneRows = rows.Where(r =>
                        manifestsByQuest.TryGetValue(r.QuestId, out var m) &&
                        zoneIds.Contains(m.ZoneId) &&
                        m.Template.Level >= bounds[0] && m.Template.Level <= bounds[1]).ToList();
                    if (zoneRows.Count == 0)
                        continue;
                    sb.AppendLine($"| {zoneName} | {bandProp.Name} | {zoneRows.Count} | " +
                                  $"{zoneRows.Count(r => r.Verdict == "Pass")} | " +
                                  $"{zoneRows.Count(r => r.Verdict == "Fail")} | " +
                                  $"{zoneRows.Count(r => r.Verdict == "Skip")} |");
                }
            }
            sb.AppendLine();
        }

        foreach (var (tier, _) in Tiers)
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

        sb.AppendLine("## FAIL rollup (by act family — top blockers)");
        sb.AppendLine();
        if (failingActTally.Count == 0)
        {
            sb.AppendLine("_none — every driven quest passed._");
        }
        else
        {
            foreach (var (actType, count) in failingActTally.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
                sb.AppendLine($"- **{actType}** — {count} failing quest occurrence(s)");
        }
        sb.AppendLine();

        sb.AppendLine("## FAIL rollup (by stage reason)");
        sb.AppendLine();
        foreach (var group in rows.Where(r => r.Verdict == "Fail")
                     .GroupBy(r => r.Detail.Split('(').First().Trim())
                     .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"- **{group.Key}** — {group.Count()} quests: {string.Join(", ", group.Select(g => g.QuestId))}");
        }
        sb.AppendLine();

        sb.AppendLine("## SKIP rollup (by reason)");
        sb.AppendLine();
        foreach (var group in rows.Where(r => r.Verdict == "Skip")
                     .GroupBy(r =>
                     {
                         var s = r.Detail.Split(';').First().Replace("SKIP:Skip (", "").Trim();
                         return s.EndsWith(")") ? s[..^1] : s;
                     })
                     .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"- **{group.Key}** — {group.Count()} quests: {string.Join(", ", group.Select(g => g.QuestId))}");
        }
        sb.AppendLine();

        sb.AppendLine("## Recommended fix-card queue");
        sb.AppendLine();
        sb.AppendLine("Each row = one FAILed quest with the first engine frame from its failure reason " +
                      "(file:line) for the fix card. SKIP rows are harness/data gaps, not engine defects.");
        sb.AppendLine();
        sb.AppendLine("| quest | name | family | failing stage | act families | file:line | reason |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (tier, qid, name, family, _, detail) in rows.Where(r => r.Verdict == "Fail").OrderBy(r => r.QuestId))
        {
            var actTypes = manifestsByQuest.TryGetValue(qid, out var manifest)
                ? manifest.Template.Components.SelectMany(c => c.Acts)
                    .Where(a => a.TryGetProperty("type", out var t) && t.GetString() is not null)
                    .Select(a => a.GetProperty("type").GetString())
                    .Distinct().Take(4)
                : [];
            var stageParts = detail.Split(';').Select(p => p.Trim()).Where(p => p.Contains("Fail")).ToArray();
            var failingStage = stageParts.Length > 0 ? stageParts[0].Split(':').First() : "?";
            var reason = stageParts.Length > 0 ? stageParts[0] : detail;
            var fileLine = ExtractFileLine(reason);
            var cleanReason = reason.Replace("|", "\\|").Replace("\n", " ").Trim();
            if (cleanReason.Length > 160)
                cleanReason = cleanReason[..160] + "…";
            sb.AppendLine($"| {qid} | {name.Replace("|", "\\|")} | {family} | {failingStage} | " +
                          $"{string.Join("+", actTypes)} | {fileLine} | {cleanReason} |");
        }
        sb.AppendLine();

        // SKIP-driven harness gaps: act families the generator cannot yet shape
        // (unsupported act types / unsynthesizable events) are the next harness
        // extension cards - they need a generator shape + driver event, not an
        // engine fix. Orphaned-context SKIPs are data gaps (no quest_contexts row).
        var gapByAct = new Dictionary<string, (string Kind, List<uint> Quests)>();
        foreach (var (_, qid, _, _, _, detail) in rows.Where(r => r.Verdict == "Skip"))
        {
            foreach (Match m in Regex.Matches(detail,
                         @"unsupported act type (QuestAct\w+)|unsynthesizable event shape for (QuestAct\w+)"))
            {
                var actType = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                var kind = m.Groups[1].Success ? "unsupported act type" : "unsynthesizable event shape";
                if (!gapByAct.TryGetValue(actType, out var entry))
                {
                    entry = (kind, []);
                    gapByAct[actType] = entry;
                }
                if (!entry.Quests.Contains(qid))
                    entry.Quests.Add(qid);
            }
        }
        if (gapByAct.Count > 0)
        {
            sb.AppendLine("### Harness-gap queue (SKIP-driven — extend the harness, not the engine)");
            sb.AppendLine();
            sb.AppendLine("| act family | gap kind | example quests | fix target |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (actType, (kind, quests)) in gapByAct
                         .OrderByDescending(kv => kv.Value.Quests.Count).ThenBy(kv => kv.Key))
            {
                var examples = string.Join(", ", quests.OrderBy(q => q).Take(4)) + (quests.Count > 4 ? " …" : "");
                sb.AppendLine($"| {actType} | {kind} | {examples} | tools/quest-scenario/gen-manifests.py (ACT_TABLES + event_shape) |");
            }
            sb.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, sb.ToString());

        // Every manifest must have been driven (no silent drops).
        var expected = Tiers.Sum(t => DiscoverManifests(t.Tier).Count);
        await Assert.That(rows.Count).IsEqualTo(expected);
        await Assert.That(File.Exists(reportPath)).IsTrue();
    }
}
