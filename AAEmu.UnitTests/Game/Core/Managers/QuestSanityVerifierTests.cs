using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M1-3: QuestSanityVerifier detection logic. Every defect class the verifier
/// reports must have a test here (fail-before would be a broken verifier; these
/// assert the detector itself fires on each defect shape).
/// </summary>
public class QuestSanityVerifierTests
{
    private sealed record State(
        QuestTemplate Quest,
        QuestComponentTemplate Component,
        Dictionary<uint, QuestTemplate> Quests,
        Dictionary<uint, QuestComponentTemplate> Components,
        Dictionary<uint, QuestActTemplate> BaseActs,
        Dictionary<string, Dictionary<uint, QuestActTemplate>> ByType);

    private static State BuildCleanState()
    {
        var quest = new QuestTemplate { Id = 1 };
        var component = new QuestComponentTemplate(quest) { Id = 100, KindId = QuestComponentKind.Start };
        quest.Components[100] = component;

        var instance = new QuestActObjTalk(component) { DetailId = 500 };
        component.ActTemplates.Add(instance);

        var baseAct = new QuestActTemplate(component) { ActId = 900, DetailId = 500, DetailType = nameof(QuestActObjTalk) };

        return new State(
            quest, component,
            new Dictionary<uint, QuestTemplate> { [1] = quest },
            new Dictionary<uint, QuestComponentTemplate> { [100] = component },
            new Dictionary<uint, QuestActTemplate> { [900] = baseAct },
            new Dictionary<string, Dictionary<uint, QuestActTemplate>>
            {
                [nameof(QuestActObjTalk)] = new() { [500] = instance }
            });
    }

    private static QuestSanityVerifier.SanityReport Run(State state, Dictionary<uint, List<uint>> groupItems = null)
    {
        return QuestSanityVerifier.VerifyLoadedState(
            state.Quests, state.Components, state.BaseActs, state.ByType, groupItems ?? []);
    }

    [Test]
    public async Task VerifyLoadedState_CleanState_NoFindings()
    {
        var state = BuildCleanState();
        var report = Run(state);
        await Assert.That(report.Findings.Count == 0).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_UnknownActType_ReportsError()
    {
        var state = BuildCleanState();
        state.BaseActs[900].DetailType = "QuestActObjBogus";
        state.ByType.Remove(nameof(QuestActObjTalk));

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_UNKNOWN_TYPE" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_UninstantiatedAct_ReportsError()
    {
        var state = BuildCleanState();
        state.ByType[nameof(QuestActObjTalk)] = new(); // type known, no detail row loaded

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_UNINSTANTIATED" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_DetachedAct_ReportsError()
    {
        var state = BuildCleanState();
        var other = new QuestComponentTemplate(state.Quest) { Id = 101, KindId = QuestComponentKind.Progress };
        state.Quest.Components[101] = other;
        state.Components[101] = other;
        // The detail row got wired to the OTHER component, while the base act row
        // belongs to the first component — the act is missing from its own component.
        var instance = new QuestActObjTalk(other) { DetailId = 500 };
        other.ActTemplates.Add(instance);
        state.ByType[nameof(QuestActObjTalk)][500] = instance;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_DETACHED" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_QuestWithoutComponents_ReportsWarning()
    {
        var state = BuildCleanState();
        state.Quest.Components.Clear();

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_COMPONENTS" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_QuestWithoutStartComponent_ReportsWarning()
    {
        var state = BuildCleanState();
        state.Component.KindId = QuestComponentKind.Progress;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_START" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_MissingNextComponent_ReportsWarning()
    {
        var state = BuildCleanState();
        state.Component.NextComponent = 999;

        var report = Run(state);

        // next_component is a deprecated 1.0 field the engine never reads for progression —
        // a dangling reference is cosmetic (data-defects.md §3), so Warn, not Error.
        await Assert.That(report.Findings.Any(f => f.Code == "COMPONENT_NEXT_MISSING" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_FixedActs_AreNotFlaggedAsStubs()
    {
        // BUG-008 (QuestActCheckGuard, real check since 5e8359d2) and BUG-009
        // (QuestActObjItemGroupGather/Use, real objectives since 6a3c0e20) retired the
        // M1-2 stub-registry entries on 2026-08-04 — these acts must NOT produce
        // ACT_STUB_KNOWN anymore (they were the false positives on every boot).
        var state = BuildCleanState();

        var guard = new QuestActCheckGuard(state.Component) { DetailId = 501 };
        state.Component.ActTemplates.Add(guard);

        var gather = new QuestActObjItemGroupGather(state.Component) { DetailId = 507, ItemGroupId = 700 };
        state.Component.ActTemplates.Add(gather);
        state.ByType[nameof(QuestActObjItemGroupGather)] = new() { [507] = gather };

        var use = new QuestActObjItemGroupUse(state.Component) { DetailId = 508, ItemGroupId = 701 };
        state.Component.ActTemplates.Add(use);
        state.ByType[nameof(QuestActObjItemGroupUse)] = new() { [508] = use };

        var report = Run(state, new Dictionary<uint, List<uint>> { [700] = [], [701] = [] });

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_STUB_KNOWN")).IsFalse();
    }

    [Test]
    public async Task VerifyLoadedState_CheckCompleteComponentMissingTarget_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActCheckCompleteComponent(state.Component) { DetailId = 502, CompleteComponent = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_COMPONENT" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_ConAcceptComponentMissingQuest_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActConAcceptComponent(state.Component) { DetailId = 503, QuestContextId = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_QUEST" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_CompleteQuestMissingQuest_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActObjCompleteQuest(state.Component) { DetailId = 504, QuestId = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_COMPLETE_QUEST" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_CheckTimerMissingNextComponent_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActCheckTimer(state.Component) { DetailId = 505, NextComponent = 999 };
        state.Component.ActTemplates.Add(act);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_NEXT_MISSING" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_ConAcceptComponentPresent_ReportsWatchInfo()
    {
        var state = BuildCleanState();
        var act = new QuestActConAcceptComponent(state.Component) { DetailId = 506, QuestContextId = 1 };
        state.Component.ActTemplates.Add(act);
        state.ByType[nameof(QuestActConAcceptComponent)] = new() { [506] = act };

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_WATCH" && f.Severity == QuestSanityVerifier.Severity.Info)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_ItemGroupGatherMissingGroup_ReportsError()
    {
        var state = BuildCleanState();
        var act = new QuestActObjItemGroupGather(state.Component) { DetailId = 507, ItemGroupId = 999 };
        state.Component.ActTemplates.Add(act);
        state.ByType[nameof(QuestActObjItemGroupGather)] = new() { [507] = act };

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_GROUP_MISSING" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_AllowlistedQuestWithoutComponents_ReportsInfo()
    {
        var state = BuildCleanState();
        state.Quest.Id = 2148; // "하다보니(reserve)" block shell (data-defects.md §6)
        state.Quest.Components.Clear();

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_COMPONENTS" && f.Severity == QuestSanityVerifier.Severity.Info)).IsTrue();
        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_COMPONENTS" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsFalse();
    }

    [Test]
    public async Task VerifyLoadedState_AllowlistedQuestWithoutStart_ReportsInfo()
    {
        var state = BuildCleanState();
        state.Quest.Id = 2148; // "하다보니(reserve)" block shell (data-defects.md §6)
        state.Component.KindId = QuestComponentKind.Progress;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_START" && f.Severity == QuestSanityVerifier.Severity.Info)).IsTrue();
        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_START" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsFalse();
    }

    [Test]
    public async Task VerifyLoadedState_AllowlistedQuestDanglingAcceptTarget_ReportsInfo()
    {
        var state = BuildCleanState();
        state.Quest.Id = 1960; // cat-34 chain quest with dangling ConAcceptComponent (data-defects.md §4)
        var act = new QuestActConAcceptComponent(state.Component) { DetailId = 508, QuestContextId = 999 };
        state.Component.ActTemplates.Add(act);
        state.ByType[nameof(QuestActConAcceptComponent)] = new() { [508] = act };

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_QUEST" && f.Severity == QuestSanityVerifier.Severity.Info)).IsTrue();
        // Non-allowlisted quests with the same shape still report Error (see
        // VerifyLoadedState_ConAcceptComponentMissingQuest_ReportsError).
        await Assert.That(report.Findings.Any(f => f.Code == "ACT_REF_MISSING_QUEST" && f.Severity == QuestSanityVerifier.Severity.Error)).IsFalse();
    }

    [Test]
    public async Task Allowlist_ContainsClassifiedShells()
    {
        // Spot-check every allowlist group from data-defects.md (108 ids total — was 109
        // before 1391 was dropped on 2026-08-05, dropped-content-register.md §1).
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(315u)).IsTrue();  // do-not-delete shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1728u)).IsTrue(); // do-not-delete shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1391u)).IsFalse(); // dummy shell — DROPPED 2026-08-05 (dropped-content-register.md §1)
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1576u)).IsTrue(); // dummy shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(2046u)).IsTrue(); // dummy shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(2148u)).IsTrue(); // reserve block start
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(2229u)).IsTrue(); // reserve block end
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(3748u)).IsTrue(); // Hadir cutscene
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(3757u)).IsTrue(); // Hadir cutscene end
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1954u)).IsTrue(); // cat-34 orphan context
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1960u)).IsTrue(); // cat-34 dangling accept
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(2145u)).IsTrue(); // cat-34 dangling accept
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(2146u)).IsTrue(); // cat-34 orphan context

        // The QUEST_NO_START cluster 1533–1548 (data-defects.md §5) was DROPPED
        // 2026-08-05 (dropped-content-register.md §2) — its ids are REMOVED from the
        // allowlist so a regression re-reports at WARN instead of being masked.
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1533u)).IsFalse(); // dropped tutorial shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1535u)).IsFalse(); // dropped tutorial shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1548u)).IsFalse(); // dropped tutorial shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1640u)).IsFalse(); // dropped tutorial shell
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1830u)).IsFalse(); // dropped tutorial "UNUSED"
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1831u)).IsFalse(); // dropped tutorial "UNUSED"

        // Non-shell quests stay Warn/Error — 330/776/777 keep COMPONENT_NEXT_MISSING Warn
        // pending the 3-row data overlay (data-defects.md §3).
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(330u)).IsFalse();
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(776u)).IsFalse();
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(777u)).IsFalse();
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Count == 108).IsTrue();
    }

    // -- Zone/kind rollup (scope add: which zones/kinds are quest-broken). --

    [Test]
    public async Task VerifyLoadedState_ZoneAndKindRollup_CountsFailedAndWarned()
    {
        // zone 125: quest 1 clean + quest 2 failed (Error); zone 8: quest 3 warned (Warn)
        // + quest 2148 allowlisted (no-start downgraded to Info → NOT counted as warned).
        var q1 = new QuestTemplate { Id = 1, ZoneId = 125, CategoryId = 4 };
        var c1 = new QuestComponentTemplate(q1) { Id = 100, KindId = QuestComponentKind.Start };
        q1.Components[100] = c1;

        var q2 = new QuestTemplate { Id = 2, ZoneId = 125, CategoryId = 4 };
        var c2 = new QuestComponentTemplate(q2) { Id = 200, KindId = QuestComponentKind.Start };
        q2.Components[200] = c2;
        c2.ActTemplates.Add(new QuestActObjCompleteQuest(c2) { DetailId = 600, QuestId = 999 });

        var q3 = new QuestTemplate { Id = 3, ZoneId = 8, CategoryId = 14 };
        var c3 = new QuestComponentTemplate(q3) { Id = 300, KindId = QuestComponentKind.Progress };
        q3.Components[300] = c3;
        c3.ActTemplates.Add(new QuestActObjTalk(c3) { DetailId = 700 });

        var q4 = new QuestTemplate { Id = 2148, ZoneId = 8, CategoryId = 28 };
        var c4 = new QuestComponentTemplate(q4) { Id = 400, KindId = QuestComponentKind.Progress };
        q4.Components[400] = c4;

        var quests = new Dictionary<uint, QuestTemplate> { [1] = q1, [2] = q2, [3] = q3, [2148] = q4 };
        var components = new Dictionary<uint, QuestComponentTemplate> { [100] = c1, [200] = c2, [300] = c3, [400] = c4 };

        var report = QuestSanityVerifier.VerifyLoadedState(quests, components,
            new Dictionary<uint, QuestActTemplate>(), new Dictionary<string, Dictionary<uint, QuestActTemplate>>(),
            new Dictionary<uint, List<uint>>());

        var zone125 = report.ZoneRollups.First(z => z.ZoneId == 125);
        var zone8 = report.ZoneRollups.First(z => z.ZoneId == 8);
        await Assert.That(zone125.QuestCount == 2 && zone125.FailedQuestCount == 1 && zone125.WarnedQuestCount == 0).IsTrue();
        await Assert.That(zone8.QuestCount == 2 && zone8.FailedQuestCount == 0 && zone8.WarnedQuestCount == 1).IsTrue();

        var kind4 = report.KindRollups.First(k => k.KindId == 4);
        var kind28 = report.KindRollups.First(k => k.KindId == 28);
        await Assert.That(kind4.QuestCount == 2 && kind4.FailedQuestCount == 1 && kind4.WarnedQuestCount == 0).IsTrue();
        // Allowlisted quest's no-start finding is Info → kind 28 is not "warned".
        await Assert.That(kind28.QuestCount == 1 && kind28.WarnedQuestCount == 0).IsTrue();
    }

    /// <summary>
    /// The QUEST_NO_START cluster 1533–1548 was DROPPED from the data layer 2026-08-05
    /// (data-defects.md §5 verdict (c) drop; dropped-content-register.md §2; t_5140fb35).
    /// Its ids are REMOVED from the allowlist, so if the rows ever return (regression),
    /// the verifier re-reports QUEST_NO_START at WARN — the mask is gone.
    /// </summary>
    [Test]
    public async Task VerifyLoadedState_DroppedClusterQuestWithoutStart_ReportsWarn()
    {
        var state = BuildCleanState();
        state.Quest.Id = 1533; // dropped tutorial shell — must NOT be masked anymore
        state.Component.KindId = QuestComponentKind.Progress;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_START" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
        await Assert.That(report.Findings.Any(f => f.Code == "QUEST_NO_START" && f.Severity == QuestSanityVerifier.Severity.Info)).IsFalse();
        await Assert.That(QuestSanityVerifier.AllowlistedQuestIds.Contains(1533u)).IsFalse();
    }

    // -- SQL-level orphan listing (data-defects.md §7: census must name the orphans). --

    [Test]
    public async Task VerifyData_OrphanRows_MessageListsOrphanIds()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE quest_contexts (id INTEGER PRIMARY KEY);" +
                "CREATE TABLE quest_components (id INTEGER PRIMARY KEY, quest_context_id INTEGER);" +
                "CREATE TABLE quest_acts (id INTEGER PRIMARY KEY, quest_component_id INTEGER, act_detail_type TEXT, act_detail_id INTEGER);" +
                "INSERT INTO quest_contexts (id) VALUES (5);" +
                "INSERT INTO quest_components (id, quest_context_id) VALUES (1, 745), (2, 1421), (3, 5);" +
                "INSERT INTO quest_acts (id, quest_component_id, act_detail_type, act_detail_id) VALUES (10, 999, 'QuestActObjTalk', 1), (11, 3, 'QuestActObjTalk', 2);";
            setup.ExecuteNonQuery();
        }

        var findings = QuestSanityVerifier.VerifyData(connection, new HashSet<string> { "QuestActObjTalk" });

        var orphans = findings.First(f => f.Code == "DATA_ORPHAN_COMPONENTS");
        await Assert.That(orphans.Message.Contains("2 quest_components rows")).IsTrue();
        await Assert.That(orphans.Message.Contains("745") && orphans.Message.Contains("1421")).IsTrue();

        var orphanActs = findings.First(f => f.Code == "DATA_ORPHAN_ACTS");
        await Assert.That(orphanActs.Message.Contains("1 quest_acts row")).IsTrue();
        await Assert.That(orphanActs.Message.Contains("orphan quest_act ids: 10")).IsTrue();

        // Valid rows must not be flagged as orphans.
        await Assert.That(orphans.Message.Contains("11")).IsFalse();
        await Assert.That(orphanActs.Message.Contains("11")).IsFalse();
    }

    // ------------------------------------------------------------------
    // UNIT_REQS layer (t_333300e2, audit: scorecard-explorations/unit-reqs-layer.md)
    // ------------------------------------------------------------------

    /// <summary>In-memory DB with the tables VerifyUnitReqs reads: unit_reqs, quest_contexts,
    /// quest_components, and the five id-space collision tables.</summary>
    private static SqliteConnection BuildUnitReqsDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE unit_reqs (
                id INTEGER PRIMARY KEY, owner_id INTEGER NOT NULL, owner_type TEXT NOT NULL,
                kind_id INTEGER NOT NULL, value1 INTEGER NOT NULL, value2 INTEGER NOT NULL);
            CREATE TABLE quest_contexts (id INTEGER PRIMARY KEY);
            CREATE TABLE quest_components (id INTEGER PRIMARY KEY, quest_context_id INTEGER NOT NULL);
            CREATE TABLE spheres (id INTEGER PRIMARY KEY);
            CREATE TABLE npcs (id INTEGER PRIMARY KEY);
            CREATE TABLE doodad_almighties (id INTEGER PRIMARY KEY);
            CREATE TABLE ai_events (id INTEGER PRIMARY KEY);
            CREATE TABLE items (id INTEGER PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertUnitReq(SqliteConnection connection, uint id, uint ownerId, uint kindId, uint value1)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO unit_reqs (id, owner_id, owner_type, kind_id, value1, value2)
            VALUES (@id, @ownerId, 'QuestComponent', @kindId, @value1, 0)
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@ownerId", ownerId);
        command.Parameters.AddWithValue("@kindId", kindId);
        command.Parameters.AddWithValue("@value1", value1);
        command.ExecuteNonQuery();
    }

    private static void InsertQuestComponent(SqliteConnection connection, uint id, uint questContextId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO quest_components (id, quest_context_id) VALUES (@id, @qc)";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@qc", questContextId);
        command.ExecuteNonQuery();
    }

    [Test]
    public async Task VerifyUnitReqs_LevelGateKind1Value1_NotFlagged()
    {
        // 45 kind-1 rows with value1=14 are LEVEL gates (min-level 14), not quest deps.
        using var connection = BuildUnitReqsDb();
        InsertUnitReq(connection, 1, 100, 1, 14); // kind 1 (Level), owner QuestComponent
        InsertQuestComponent(connection, 100, 500);

        var findings = QuestSanityVerifier.VerifyUnitReqs(connection);

        await Assert.That(findings.Any(f => f.Code is "UNIT_REQS_MISSING_CONTEXT" or "UNIT_REQS_COLLISION")).IsFalse();
    }

    [Test]
    public async Task VerifyUnitReqs_ExceptCompleteKind36_NotFlagged()
    {
        // ExceptComplete (kind 36): "must NOT have completed quest X" against a missing
        // quest is vacuously true — the row must not be flagged.
        using var connection = BuildUnitReqsDb();
        InsertUnitReq(connection, 2, 100, 36, 14); // kind 36 (ExceptCompleteQuestContext)
        InsertQuestComponent(connection, 100, 500);

        var findings = QuestSanityVerifier.VerifyUnitReqs(connection);

        await Assert.That(findings.Any(f => f.Code is "UNIT_REQS_MISSING_CONTEXT" or "UNIT_REQS_COLLISION")).IsFalse();
    }

    [Test]
    public async Task VerifyUnitReqs_MissingContextNoBodyNoCollision_ReportsWarning()
    {
        // Genuinely missing context: no quest body, no other-table ownership → WARN.
        using var connection = BuildUnitReqsDb();
        InsertUnitReq(connection, 3, 100, 31, 7777); // CompleteQuestContext → missing 7777
        InsertQuestComponent(connection, 100, 500);

        var findings = QuestSanityVerifier.VerifyUnitReqs(connection);

        await Assert.That(findings.Any(f => f.Code == "UNIT_REQS_MISSING_CONTEXT" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyUnitReqs_OrphanBodySurvives_ReportsWarning()
    {
        // Orphaned template (audit class b): quest_components rows survive under the missing
        // context id — chain already ruled drop, but the gate can never pass → WARN.
        using var connection = BuildUnitReqsDb();
        InsertUnitReq(connection, 4, 100, 31, 1955); // CompleteQuestContext → missing 1955
        InsertQuestComponent(connection, 100, 500);
        InsertQuestComponent(connection, 101, 1955); // surviving body of quest 1955

        var findings = QuestSanityVerifier.VerifyUnitReqs(connection);

        await Assert.That(findings.Any(f => f.Code == "UNIT_REQS_MISSING_CONTEXT" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
    }

    [Test]
    public async Task VerifyUnitReqs_CollisionIdOwnedByOtherTable_ReportsInfo()
    {
        // Id-space collision (audit class c): no quest body, but the id is a live sphere/npc/
        // doodad/ai_event/item — INFO, not WARN/ERR (the number is reused, not a quest dep).
        using var connection = BuildUnitReqsDb();
        InsertUnitReq(connection, 5, 100, 31, 1882); // CompleteQuestContext → missing 1882
        InsertQuestComponent(connection, 100, 500);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO spheres (id) VALUES (1882)";
            command.ExecuteNonQuery();
        }

        var findings = QuestSanityVerifier.VerifyUnitReqs(connection);

        await Assert.That(findings.Any(f => f.Code == "UNIT_REQS_COLLISION" && f.Severity == QuestSanityVerifier.Severity.Info)).IsTrue();
        await Assert.That(findings.Any(f => f.Code == "UNIT_REQS_MISSING_CONTEXT")).IsFalse();
    }

    [Test]
    public async Task VerifyUnitReqs_ValidContext_NoFinding()
    {
        // value1 resolves against quest_contexts.id → no finding.
        using var connection = BuildUnitReqsDb();
        InsertUnitReq(connection, 6, 100, 31, 42);
        InsertQuestComponent(connection, 100, 500);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO quest_contexts (id) VALUES (42)";
            command.ExecuteNonQuery();
        }

        var findings = QuestSanityVerifier.VerifyUnitReqs(connection);

        await Assert.That(findings.Any(f => f.Code is "UNIT_REQS_MISSING_CONTEXT" or "UNIT_REQS_COLLISION")).IsFalse();
    }

    [Test]
    public async Task VerifyUnitReqs_SummaryRollup_ListsGatedQuestsAndCollisions()
    {
        // Summary must name the gated quests and the collision ids.
        using var connection = BuildUnitReqsDb();
        InsertUnitReq(connection, 7, 100, 31, 1955);   // orphan → gated quest 500
        InsertUnitReq(connection, 8, 102, 31, 1882);   // collision → gated quest 501
        InsertQuestComponent(connection, 100, 500);
        InsertQuestComponent(connection, 102, 501);
        InsertQuestComponent(connection, 101, 1955);   // surviving body → orphan
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO spheres (id) VALUES (1882)";
            command.ExecuteNonQuery();
        }

        var findings = QuestSanityVerifier.VerifyUnitReqs(connection);
        var summary = findings.FirstOrDefault(f => f.Code == "UNIT_REQS_SUMMARY");

        await Assert.That(summary != null).IsTrue();
        await Assert.That(summary!.Message.Contains("gated quests: 500,501")).IsTrue();
        await Assert.That(summary.Message.Contains("collisions: 1882")).IsTrue();
        await Assert.That(summary.Message.Contains("2 missing contexts")).IsTrue();
    }
}
