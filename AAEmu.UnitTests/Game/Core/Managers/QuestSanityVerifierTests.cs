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
    public async Task VerifyLoadedState_MissingNextComponent_ReportsError()
    {
        var state = BuildCleanState();
        state.Component.NextComponent = 999;

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "COMPONENT_NEXT_MISSING" && f.Severity == QuestSanityVerifier.Severity.Error)).IsTrue();
    }

    [Test]
    public async Task VerifyLoadedState_KnownStubAct_ReportsWarning()
    {
        var state = BuildCleanState();
        var guard = new QuestActCheckGuard(state.Component) { DetailId = 501 };
        state.Component.ActTemplates.Add(guard);

        var report = Run(state);

        await Assert.That(report.Findings.Any(f => f.Code == "ACT_STUB_KNOWN" && f.Severity == QuestSanityVerifier.Severity.Warn)).IsTrue();
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
