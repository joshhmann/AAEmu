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
/// M1 rig (t_d5e088ed): fail-before evidence for the QUEST_NO_START cluster 1533–1548.
///
/// Ground truth (scorecard-explorations/data-defects.md §5): quests 1533, 1535–1549,
/// 1551–1554, 1640, 1830, 1831 (23 quests) are legacy 1.0-era tutorial shells — every one
/// has components but NO Start component, and NO accept surface references them. The engine
/// can therefore never accept them: Quest.CreateQuestSteps() builds steps from component
/// kinds (no Start step exists) and Quest.StartQuest() returns false for a quest without a
/// Start step (AAEmu.Game/Models/Game/Quests/NewQuestCode.cs:42-56). 1534 and 1550 are pure
/// id gaps (no quest_contexts row — nothing is ever loaded for them).
///
/// The rig asserts these facts against the READ-ONLY reference compact.sqlite3 (canonical
/// md5 78b3bdbf0383db3b927056106efdf91af — the verifier's documented prod data). It also
/// runs the real QuestSanityVerifier.VerifyLoadedState over a loader-faithful template load
/// and asserts QUEST_NO_START fires for every cluster quest (allowlist masks the severity to
/// Info, so the census stays green — this rig documents that green ≠ runnable).
///
/// This is fail-BEFORE evidence: it pins the current never-acceptable state so the follow-up
/// fix card (drop or repair the cluster) can prove its effect. If the reference data ever
/// changes such that a cluster quest gains a Start component or an accept path, these tests
/// FAIL — the classification is stale and the evidence doc must be regenerated.
///
/// DB resolution: $AAEMU_COMPACT_SQLITE3 if set, else the repo's AAEmu.Game/Data/compact.sqlite3
/// walked up from the test output dir. When no reference DB is present (e.g. CI checkout
/// without data) the tests are ignored with a reason — they never fake evidence.
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

    private const uint QuestCategoryTutorial = 45; // QuestManager.QuestCategoryTutorial (private)

    /// <summary>Verifier allowlist downgrades cluster findings to Info — census green while the defect is real.</summary>
    private static bool IsAllowlisted(uint questId) => QuestSanityVerifier.AllowlistedQuestIds.Contains(questId);

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
    /// Fail-before fact 1: every cluster quest has a quest_contexts row and at least one
    /// component — the template exists and the engine loads it (none are category 45,
    /// which is the only category LoadQuestContexts skips).
    /// </summary>
    [Test]
    public async Task EveryClusterQuest_IsLoadedWithComponents()
    {
        using var db = OpenReferenceDb();

        var failures = new List<string>();
        foreach (var questId in ClusterQuestIds)
        {
            var contextCount = ScalarCount(db, $"SELECT COUNT(*) FROM quest_contexts WHERE id = {questId}");
            if (contextCount != 1)
            {
                failures.Add($"quest {questId}: quest_contexts row count = {contextCount}, expected 1");
                continue;
            }

            var componentCount = ScalarCount(db, $"SELECT COUNT(*) FROM quest_components WHERE quest_context_id = {questId}");
            if (componentCount <= 0)
                failures.Add($"quest {questId}: {componentCount} components — expected at least 1 (QUEST_NO_COMPONENTS shape, not QUEST_NO_START)");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Fail-before fact 2 (THE core assertion): every cluster quest has ZERO Start-kind
    /// components. With no Start component the engine has no Start step
    /// (Quest.CreateQuestSteps) and Quest.StartQuest() returns false — the quest can never
    /// be accepted via the normal flow.
    /// </summary>
    [Test]
    public async Task EveryClusterQuest_HasNoStartComponent()
    {
        using var db = OpenReferenceDb();

        var failures = new List<string>();
        foreach (var questId in ClusterQuestIds)
        {
            var startCount = ScalarCount(db,
                $"SELECT COUNT(*) FROM quest_components WHERE quest_context_id = {questId} AND component_kind_id = {(int)QuestComponentKind.Start}");
            if (startCount != 0)
                failures.Add($"quest {questId}: {startCount} Start-kind component(s) — cluster classification is STALE, evidence doc must be regenerated");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Fail-before fact 3: the real verifier (QuestSanityVerifier.VerifyLoadedState) emits a
    /// QUEST_NO_START finding for EVERY cluster quest when fed a loader-faithful template
    /// load of the reference data — and every cluster quest is allowlisted, which is exactly
    /// why the census stays green (green ≠ runnable).
    /// </summary>
    [Test]
    public async Task EveryClusterQuest_VerifierEmitsQuestNoStart()
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
            var finding = report.Findings.FirstOrDefault(f => f.Code == "QUEST_NO_START" && f.QuestId == questId);
            if (finding == null)
            {
                failures.Add($"quest {questId}: verifier emitted NO QUEST_NO_START finding");
                continue;
            }

            // The allowlist is the ONLY reason these are not Warn/Error: document the mask.
            if (!IsAllowlisted(questId))
                failures.Add($"quest {questId}: QUEST_NO_START fires but quest is NOT allowlisted — severity is {finding.Severity}, expected the documented Info mask");
        }

        await Assert.That(failures).IsEmpty();
    }

    /// <summary>
    /// Fail-before fact 4: no accept surface can reach the cluster — zero item_accept_quests,
    /// zero accept_quest_effects, zero doodad_func_quests, zero QuestActConAcceptComponent
    /// (self-start) rows, and zero completion/in-progress unit_reqs gates from live quest
    /// components. Combined with no Start component: the quests are unreachable by any
    /// engine entry point.
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
    /// Fail-before fact 5 (engine-level): constructing a Quest from each cluster template and
    /// calling the REAL Quest.StartQuest() returns false for every quest — the engine itself
    /// refuses to start a quest whose step map has no Start step.
    /// </summary>
    [Test]
    public async Task EngineStartQuest_ReturnsFalse_ForEveryClusterQuest()
    {
        using var db = OpenReferenceDb();

        var state = LoadTemplates(db);
        SeedQuestManagerSingleton();

        var failures = new List<string>();
        foreach (var questId in ClusterQuestIds)
        {
            var template = state.Quests.GetValueOrDefault(questId);
            if (template == null)
            {
                failures.Add($"quest {questId}: template not loaded");
                continue;
            }

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
    /// Fail-before fact 6: 1534 and 1550 (inside the card's 1533–1548 range) have NO
    /// quest_contexts row — nothing is loaded for them, so they cannot be accepted either
    /// (there is no template to accept). They are id gaps, not quests.
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
    /// The card headline range 1533–1548 contains exactly 15 loadable quests (1534 is the
    /// one id gap): pins the headline claim to the data.
    /// </summary>
    [Test]
    public async Task HeadlineRange_ContainsFifteenQuestsAndOneIdGap()
    {
        using var db = OpenReferenceDb();

        var contextCount = ScalarCount(db,
            $"SELECT COUNT(*) FROM quest_contexts WHERE id BETWEEN 1533 AND 1548");

        await Assert.That(contextCount).IsEqualTo(15);
    }
}
