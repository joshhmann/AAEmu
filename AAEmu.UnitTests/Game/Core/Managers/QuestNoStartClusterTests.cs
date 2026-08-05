using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

using Microsoft.Data.Sqlite;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M1 rig (t_d5e088ed → t_5140fb35): QUEST_NO_START cluster 1533–1548 evidence.
///
/// Ground truth (scorecard-explorations/data-defects.md §5): quests 1533, 1535–1549,
/// 1551–1554, 1640, 1830, 1831 (23 quests) were legacy 1.0-era tutorial shells — every one
/// had components but NO Start component, and NO accept surface referenced them. The engine
/// can therefore never accept them: Quest.CreateQuestSteps() builds steps from component
/// kinds (no Start step exists) and Quest.StartQuest() returns false for a quest without a
/// Start step (AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:42-56). 1534 and 1550 are pure
/// id gaps (no quest_contexts row — nothing is ever loaded for them).
///
/// POST-FIX CONTRACT (2026-08-05, t_5140fb35): the cluster was DROPPED as data per
/// data-defects.md §5 verdict (c) drop + Josh's 2026-08-05 decision (dropped-content-register.md
/// §2). Drop = guarded DELETEs in SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql
/// (23 quest_contexts + 25 quest_components + 42 quest_acts) + REMOVAL of the cluster ids from
/// the verifier allowlist — a regression that re-adds the rows now re-reports QUEST_NO_START at
/// WARN instead of being masked to Info. This rig asserts that post-drop contract:
///   - every cluster quest is either FULLY ABSENT (dropped: 0 contexts/0 comps/0 acts) or,
///     when still present in a reference DB that predates the drop, provably never-acceptable
///     (zero Start comps, zero accept surfaces, real Quest.StartQuest() returns false);
///   - the allowlist no longer contains any cluster id (mask removed);
///   - the drop patch, applied to a COPY of the reference DB, removes the cluster entirely
///     and leaves no orphaned rows behind.
///
/// DB resolution: $AAEMU_COMPACT_SQLITE3 if set, else the repo's AAEmu.Game/Data/compact.sqlite3
/// walked up from the test output dir. When no reference DB is present (e.g. CI checkout
/// without data) the tests are ignored with a reason — they never fake evidence. The drop-patch
/// test copies the reference DB to a temp file and patches the COPY (the reference stays
/// read-only, per the fork's data rules).
/// </summary>
[NotInParallel] // seeds the QuestManager singleton for the engine-level StartQuest proof
public class QuestNoStartClusterTests
{
    /// <summary>Full classified cluster (data-defects.md §5): 1533, 1535–1549, 1551–1554, 1640, 1830, 1831.</summary>
    private static readonly uint[] ClusterQuestIds =
    [
        1533, 1640, 1830, 1831,
        .. Enumerable.Range(1535, 1549 - 1535 + 1).Select(i => (uint)i),
        .. Enumerable.Range(1551, 1554 - 1551 + 1).Select(i => (uint)i),
    ];

    /// <summary>Card headline range 1533–1548 (inclusive).</summary>
    private static readonly uint[] HeadlineRangeIds = Enumerable.Range(1533, 1548 - 1533 + 1).Select(i => (uint)i).ToArray();

    /// <summary>Pure id gaps inside the headline range: no quest_contexts row at all.</summary>
    private static readonly uint[] IdGapIds = [1534, 1550];

    /// <summary>25 quest_components rows owned by the 23 cluster contexts (1831 has 3 comps).</summary>
    private static readonly uint[] ClusterComponentIds =
    [
        .. Enumerable.Range(7738, 7758 - 7738 + 1).Select(i => (uint)i), // 7738–7758: quests 1533–1549, 1551–1554, 1640
        8492, // quest 1830
        8494, 8495, 8496, // quest 1831
    ];

    /// <summary>42 quest_acts rows wired to the cluster components (SupplyCopper + SupplyExp).</summary>
    private static readonly uint[] ClusterActIds =
    [
        10867, 10868, 10869, 10870, 10871, 10872, 10873, 10875, 10876, 10877,
        10878, 10879, 10880, 10881, 10882, 10883, 10884, 10885, 10886, 10887,
        10888, 10889, 10890, 10891, 10892, 10893, 10894, 10895, 10896, 10897,
        10898, 10899, 10900, 10901, 10902, 10903, 10904, 10905, 10906, 10907,
        10910, 10911,
    ];

    private const uint QuestCategoryTutorial = 45; // QuestManager.QuestCategoryTutorial (private)

    // -- DB resolution ---------------------------------------------------------

    private static string ResolveReferenceDatabase()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_COMPACT_SQLITE3");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "AAEmu.Game", "Data", "compact.sqlite3");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Opens the reference DB read-only, or skips the test when no reference data exists.</summary>
    private static SqliteConnection OpenReferenceDb()
    {
        var path = ResolveReferenceDatabase();
        Skip.Unless(path != null && new FileInfo(path).Length > 0,
            "No non-empty reference compact.sqlite3 available (set AAEMU_COMPACT_SQLITE3 or restore AAEmu.Game/Data/compact.sqlite3) — rig evidence requires the canonical read-only reference data.");

        var connection = new SqliteConnection($"Data Source=file:{path}; Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static long ScalarCount(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar();
    }

    // -- Loader-faithful template load (mirrors QuestManager.LoadQuestContexts /
    //    LoadQuestComponents incl. the category-45 skip; acts are not needed to prove
    //    the no-Start shape, and the verifier's act walks are empty-dictionary no-ops). --

    private sealed record LoadedState(
        Dictionary<uint, QuestTemplate> Quests,
        Dictionary<uint, QuestComponentTemplate> Components);

    private static LoadedState LoadTemplates(SqliteConnection connection)
    {
        var quests = new Dictionary<uint, QuestTemplate>();
        var components = new Dictionary<uint, QuestComponentTemplate>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, category_id, zone_id, COALESCE(level, 0) FROM quest_contexts ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var template = new QuestTemplate
                {
                    Id = (uint)reader.GetInt64(0),
                    CategoryId = (uint)reader.GetInt64(1),
                    ZoneId = (uint)reader.GetInt64(2),
                    Level = (byte)reader.GetInt64(3),
                };
                if (template.CategoryId != QuestCategoryTutorial)
                    quests.Add(template.Id, template);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, quest_context_id, component_kind_id, COALESCE(next_component, 0) " +
                                  "FROM quest_components ORDER BY quest_context_id, component_kind_id, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var questId = (uint)reader.GetInt64(1);
                if (!quests.TryGetValue(questId, out var questTemplate))
                    continue; // mirror LoadQuestComponents: rows for skipped contexts are dropped

                var template = new QuestComponentTemplate(questTemplate)
                {
                    Id = (uint)reader.GetInt64(0),
                    KindId = (QuestComponentKind)reader.GetInt64(2),
                    NextComponent = (uint)reader.GetInt64(3),
                };
                components.Add(template.Id, template);
                questTemplate.Components.Add(template.Id, template);
            }
        }

        return new LoadedState(quests, components);
    }

    // -- Singleton seeding for the engine-level proof (same mechanism as QuestScenarioDriver) --

    private static void SetSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static void SeedQuestManagerSingleton()
    {
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        SetField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
        SetSingleton(typeof(Singleton<QuestManager>), questManager);
    }

    // -- Tests ----------------------------------------------------------------

    /// <summary>
    /// Post-drop contract, data-state-aware: every cluster quest is either FULLY ABSENT
    /// (the dropped state — 0 context rows AND 0 component rows) or, on a reference DB that
    /// still predates the drop, present with ≥1 component (the pre-drop shape). A context
    /// with 0 components would be the QUEST_NO_COMPONENTS shape, not the cluster's — that
    /// would mean the drop was applied half-way and must be re-verified.
    /// </summary>
    [Test]
    public async Task EveryClusterQuest_IsDroppedOrLoadedWithComponents()
    {
        using var db = OpenReferenceDb();

        var failures = new List<string>();
        foreach (var questId in ClusterQuestIds)
        {
            var contextCount = ScalarCount(db, $"SELECT COUNT(*) FROM quest_contexts WHERE id = {questId}");
            var componentCount = ScalarCount(db, $"SELECT COUNT(*) FROM quest_components WHERE quest_context_id = {questId}");
            if (contextCount == 0 && componentCount == 0)
                continue; // dropped state — clean absence

            if (contextCount != 1)
                failures.Add($"quest {questId}: quest_contexts row count = {contextCount}, expected 1 (pre-drop) or 0 (dropped)");
            else if (componentCount <= 0)
                failures.Add($"quest {questId}: {componentCount} components — expected at least 1 (QUEST_NO_COMPONENTS shape, not QUEST_NO_START)");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Post-drop contract: any cluster quest STILL PRESENT in the reference data must have
    /// ZERO Start-kind components. This is the regression tripwire: if the rows ever return
    /// WITH a Start component (or the drop is reverted against a re-created quest), this
    /// fails — the classification is stale and the evidence doc must be regenerated.
    /// </summary>
    [Test]
    public async Task EveryPresentClusterQuest_HasNoStartComponent()
    {
        using var db = OpenReferenceDb();

        var failures = new List<string>();
        foreach (var questId in ClusterQuestIds)
        {
            var contextCount = ScalarCount(db, $"SELECT COUNT(*) FROM quest_contexts WHERE id = {questId}");
            if (contextCount == 0)
                continue; // dropped — nothing to check

            var startCount = ScalarCount(db,
                $"SELECT COUNT(*) FROM quest_components WHERE quest_context_id = {questId} AND component_kind_id = {(int)QuestComponentKind.Start}");
            if (startCount != 0)
                failures.Add($"quest {questId}: {startCount} Start-kind component(s) — cluster classification is STALE, evidence doc must be regenerated");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Post-drop contract, verifier interplay: the cluster ids are NO LONGER allowlisted
    /// (dropped-content-register.md §2 — mask removed). Any cluster quest still present in
    /// the reference data therefore re-reports QUEST_NO_START at WARN (not Info): a
    /// regression that re-adds the rows surfaces in the census instead of being hidden.
    /// </summary>
    [Test]
    public async Task EveryPresentClusterQuest_VerifierEmitsQuestNoStart_Unmasked()
    {
        using var db = OpenReferenceDb();

        var state = LoadTemplates(db);
        var report = QuestSanityVerifier.VerifyLoadedState(
            state.Quests, state.Components,
            new Dictionary<uint, QuestActTemplate>(),
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>(),
            new Dictionary<uint, List<uint>>());

        var failures = new List<string>();
        foreach (var questId in ClusterQuestIds)
        {
            // Allowlist mask removed: a regression re-reports at WARN.
            if (QuestSanityVerifier.AllowlistedQuestIds.Contains(questId))
                failures.Add($"quest {questId}: STILL allowlisted — drop contract violated, mask must be removed (dropped-content-register.md §2)");

            var finding = report.Findings.FirstOrDefault(f => f.Code == "QUEST_NO_START" && f.QuestId == questId);
            if (finding == null)
                continue; // dropped from this DB — no template, no finding (post-drop state)

            // Present (pre-drop reference): the finding must now fire UNMASKED at Warn.
            if (finding.Severity != QuestSanityVerifier.Severity.Warn)
                failures.Add($"quest {questId}: QUEST_NO_START severity is {finding.Severity}, expected Warn (allowlist mask removed)");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Post-drop contract: no accept surface can reach the cluster — zero item_accept_quests,
    /// zero accept_quest_effects, zero doodad_func_quests, zero QuestActConAcceptComponent
    /// (self-start) rows, and zero completion/in-progress unit_reqs gates from live quest
    /// components. Holds in both the pre-drop (unreachable = why it was dropped) and the
    /// dropped (rows gone) states.
    /// </summary>
    [Test]
    public async Task EveryClusterQuest_HasNoAcceptPath()
    {
        using var db = OpenReferenceDb();

        var inList = string.Join(",", ClusterQuestIds);
        var checks = new (string Label, string Sql)[]
        {
            ("item_accept_quests", $"SELECT COUNT(*) FROM item_accept_quests WHERE quest_id IN ({inList})"),
            ("accept_quest_effects", $"SELECT COUNT(*) FROM accept_quest_effects WHERE quest_id IN ({inList})"),
            ("doodad_func_quests", $"SELECT COUNT(*) FROM doodad_func_quests WHERE quest_id IN ({inList})"),
            ("quest_act_con_accept_components (self-start refs)", $"SELECT COUNT(*) FROM quest_act_con_accept_components WHERE quest_context_id IN ({inList})"),
            ("unit_reqs gates from live quest components", $"SELECT COUNT(*) FROM unit_reqs WHERE kind_id IN (31,32,33,37) AND value1 IN ({inList}) AND owner_type = 'QuestComponent' AND owner_id IN (SELECT id FROM quest_components)"),
        };

        var failures = new List<string>();
        foreach (var (label, sql) in checks)
        {
            var count = ScalarCount(db, sql);
            if (count != 0)
                failures.Add($"{label}: {count} row(s) reference the cluster — an accept path EXISTS, classification is STALE");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Post-drop contract, engine-level: constructing a Quest from each STILL-PRESENT cluster
    /// template and calling the REAL Quest.StartQuest() returns false — the engine itself
    /// refuses to start a quest whose step map has no Start step. Absent (dropped) quests
    /// have no template to construct.
    /// </summary>
    [Test]
    public async Task EngineStartQuest_ReturnsFalse_ForEveryPresentClusterQuest()
    {
        using var db = OpenReferenceDb();

        var state = LoadTemplates(db);
        SeedQuestManagerSingleton();

        var failures = new List<string>();
        foreach (var questId in ClusterQuestIds)
        {
            var template = state.Quests.GetValueOrDefault(questId);
            if (template == null)
                continue; // dropped from this DB — no template (post-drop state)

            var quest = new Quest(template,
                Mock.Of<ICharacter>().Object,
                Mock.Of<IQuestManager>().Object,
                Mock.Of<ITaskManager>().Object,
                Mock.Of<ISkillManager>().Object,
                Mock.Of<IExpressTextManager>().Object,
                Mock.Of<IWorldManager>().Object);

            if (quest.QuestSteps.ContainsKey(QuestComponentKind.Start))
                failures.Add($"quest {questId}: CreateQuestSteps produced a Start step — classification is STALE");
            else if (quest.StartQuest())
                failures.Add($"quest {questId}: Quest.StartQuest() returned true — the quest CAN be accepted, classification is STALE");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Post-drop contract: 1534 and 1550 (inside the card's 1533–1548 range) remain pure id
    /// gaps — no quest_contexts row, no components. The drop patch must not invent rows for
    /// them (nothing to delete, and nothing may be re-added).
    /// </summary>
    [Test]
    public async Task IdGaps1534And1550_HaveNoTemplate()
    {
        using var db = OpenReferenceDb();

        var failures = new List<string>();
        foreach (var questId in IdGapIds)
        {
            var contextCount = ScalarCount(db, $"SELECT COUNT(*) FROM quest_contexts WHERE id = {questId}");
            var componentCount = ScalarCount(db, $"SELECT COUNT(*) FROM quest_components WHERE quest_context_id = {questId}");
            if (contextCount != 0 || componentCount != 0)
                failures.Add($"id {questId}: context rows = {contextCount}, component rows = {componentCount} — expected a pure id gap (0/0)");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// PASS-AFTER (the drop itself): applying SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql
    /// to a COPY of the reference DB removes the cluster entirely — 0 contexts, 0 components,
    /// 0 acts, no orphaned rows — while leaving the shared act DETAIL rows (quest_act_supply_coppers
    /// / quest_act_supply_exps) and the non-cluster unit_reqs id-collision rows untouched.
    /// The reference DB itself is never modified (copy-on-write, per the fork's data rules).
    /// </summary>
    [Test]
    public async Task DropPatch_WhenAppliedToReferenceCopy_RemovesClusterEntirely()
    {
        var referencePath = ResolveReferenceDatabase();
        Skip.Unless(referencePath != null && new FileInfo(referencePath).Length > 0,
            "No non-empty reference compact.sqlite3 available — drop-patch evidence requires the canonical read-only reference data.");

        // Locate the drop patch next to the repo root (same walk-up as ResolveReferenceDatabase).
        var patchPath = ResolveRepoFile("SQL", "patches", "compact", "2026-08-05-drop-no-start-cluster.sql");
        Skip.Unless(patchPath != null, "Drop patch SQL/patches/compact/2026-08-05-drop-no-start-cluster.sql not found in repo checkout.");

        var copyPath = Path.Combine(Path.GetTempPath(), $"no-start-drop-{Guid.NewGuid():N}.sqlite3");
        try
        {
            File.Copy(referencePath, copyPath);
            using (var copy = new SqliteConnection($"Data Source={copyPath}"))
            {
                copy.Open();
                // The patch is guarded DELETEs only — strip the '--' comment lines (some
                // header comments contain semicolons) and split on statement boundaries, so
                // the exact shipped file (guards included) is what gets executed.
                var statements = File.ReadAllLines(patchPath)
                    .Where(l => !l.TrimStart().StartsWith("--"))
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Aggregate(new List<string>(), (acc, line) =>
                    {
                        if (acc.Count == 0)
                            acc.Add(line);
                        else
                            acc[^1] += " " + line;
                        if (acc[^1].TrimEnd().EndsWith(";"))
                        {
                            acc[^1] = acc[^1].TrimEnd().TrimEnd(';').Trim();
                            acc.Add(string.Empty);
                        }
                        return acc;
                    })
                    .Where(s => s.Length > 0)
                    .ToArray();
                await Assert.That(statements.Length).IsEqualTo(3); // 3 DELETEs: acts, components, contexts

                foreach (var statement in statements)
                {
                    using var command = copy.CreateCommand();
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                var failures = new List<string>();
                var inContexts = string.Join(",", ClusterQuestIds);
                var inComponents = string.Join(",", ClusterComponentIds);
                var inActs = string.Join(",", ClusterActIds);

                var contextCount = ScalarCount(copy, $"SELECT COUNT(*) FROM quest_contexts WHERE id IN ({inContexts})");
                if (contextCount != 0)
                    failures.Add($"quest_contexts: {contextCount} cluster row(s) remain after drop patch");

                var componentCount = ScalarCount(copy, $"SELECT COUNT(*) FROM quest_components WHERE id IN ({inComponents})");
                if (componentCount != 0)
                    failures.Add($"quest_components: {componentCount} cluster row(s) remain after drop patch");

                var actCount = ScalarCount(copy, $"SELECT COUNT(*) FROM quest_acts WHERE id IN ({inActs})");
                if (actCount != 0)
                    failures.Add($"quest_acts: {actCount} cluster row(s) remain after drop patch");

                // No orphans left behind: no quest_acts rows now dangling on deleted components.
                var orphanActs = ScalarCount(copy,
                    $"SELECT COUNT(*) FROM quest_acts WHERE quest_component_id IN ({inComponents})");
                if (orphanActs != 0)
                    failures.Add($"quest_acts: {orphanActs} row(s) still reference deleted cluster components");

                // Id gaps stay gaps (drop must not invent rows).
                foreach (var questId in IdGapIds)
                {
                    var gapContexts = ScalarCount(copy, $"SELECT COUNT(*) FROM quest_contexts WHERE id = {questId}");
                    var gapComponents = ScalarCount(copy, $"SELECT COUNT(*) FROM quest_components WHERE quest_context_id = {questId}");
                    if (gapContexts != 0 || gapComponents != 0)
                        failures.Add($"id gap {questId}: context rows = {gapContexts}, component rows = {gapComponents} — expected 0/0 after drop");
                }

                // Shared act DETAIL rows survive (they serve other quests): supply copper/exp
                // totals must still match the pre-patch reference (the patch only unwires acts).
                var detailCoppers = ScalarCount(copy, "SELECT COUNT(*) FROM quest_act_supply_coppers");
                var detailExps = ScalarCount(copy, "SELECT COUNT(*) FROM quest_act_supply_exps");
                using (var reference = OpenReferenceDb())
                {
                    var refCoppers = ScalarCount(reference, "SELECT COUNT(*) FROM quest_act_supply_coppers");
                    var refExps = ScalarCount(reference, "SELECT COUNT(*) FROM quest_act_supply_exps");
                    if (detailCoppers != refCoppers || detailExps != refExps)
                        failures.Add($"shared detail rows changed: coppers {refCoppers}→{detailCoppers}, exps {refExps}→{detailExps} — patch must not touch detail tables");
                }

                // Non-cluster unit_reqs rows (Skill/AiEvent id collisions, kinds 30/23/35) survive.
                var collisionReqs = ScalarCount(copy,
                    "SELECT COUNT(*) FROM unit_reqs WHERE id IN (36410, 45719, 45720, 45721, 45722, 45723, 45868, 45923, 45951)");
                if (collisionReqs != 9)
                    failures.Add($"unit_reqs id-collision rows: {collisionReqs}/9 remain — patch must not touch non-quest unit_reqs");

                await Assert.That(failures).IsEmpty();
            }
        }
        finally
        {
            if (File.Exists(copyPath))
                File.Delete(copyPath);
        }
    }

    /// <summary>
    /// The card headline range 1533–1548 contains exactly 15 loadable quests in the pre-drop
    /// reference (1534 is the one id gap), or 0 after the drop is applied to the data. Pins
    /// the headline claim either way.
    /// </summary>
    [Test]
    public async Task HeadlineRange_IsEitherFifteenQuestsOrFullyDropped()
    {
        using var db = OpenReferenceDb();

        var contextCount = ScalarCount(db,
            $"SELECT COUNT(*) FROM quest_contexts WHERE id BETWEEN 1533 AND 1548");

        await Assert.That(contextCount is 0 or 15).IsTrue();
    }

    /// <summary>Resolves a file path inside the repo checkout (same walk-up as the DB resolution).</summary>
    private static string ResolveRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
