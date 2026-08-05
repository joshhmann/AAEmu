using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

using Microsoft.Data.Sqlite;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// M1-3: startup sanity cross-check for quest templates loaded from compact.sqlite3.
/// Walks every loaded quest template and collects structural defects — unresolvable act
/// references, acts whose class is a known stub (M1-2 audit), components referencing
/// missing objectives — instead of silently skipping them like the loaders do.
///
/// FAIL POLICY (evidence from sibling loaders):
///   - LoadBaseQuestActs / LoadDetailQuestActTemplates / LoadQuestComponents all skip
///     corrupt rows with Trace/Info logging and never throw.
///   - The runtime act base logs Error + returns false on unimplemented acts
///     (QuestActTemplate.RunAct); Program.cs only hard-fails when the DB file is missing.
///   => The verifier logs findings at Error/Warn/Info and NEVER throws. Defects surface
///      loudly in the server log (Error level) without bricking server start on the
///      current data, which contains thousands of orphaned rows.
/// </summary>
public static class QuestSanityVerifier
{
    public enum Severity
    {
        Info,
        Warn,
        Error
    }

    public sealed record Finding(Severity Severity, string Code, string Message);

    public sealed record SanityReport(
        IReadOnlyList<Finding> Findings,
        int QuestCount,
        int ComponentCount,
        int ActCount)
    {
        public int ErrorCount => Findings.Count(f => f.Severity == Severity.Error);
        public int WarnCount => Findings.Count(f => f.Severity == Severity.Warn);
        public int InfoCount => Findings.Count(f => f.Severity == Severity.Info);
    }

    /// <summary>
    /// Known stub acts from the M1-2 audit (scorecard-explorations/stub-acts.md).
    /// Registry is currently EMPTY — every M1-2 entry was retired when the fix landed:
    ///   - QuestActCheckGuard          retired 2026-08-04 (BUG-008, real guard-alive
    ///     check since 5e8359d2)
    ///   - QuestActObjItemGroupGather  retired 2026-08-04 (BUG-009, real objective
    ///     logic since 6a3c0e20)
    ///   - QuestActObjItemGroupUse     retired 2026-08-04 (BUG-009, same)
    /// Keep the mechanism: if a future audit finds a NEW stub act, add it here and the
    /// ACT_STUB_KNOWN finding fires again automatically.
    /// </summary>
    private static readonly Dictionary<string, string> s_knownStubActTypes =
        new(StringComparer.Ordinal);

    /// <summary>Watch item from the M1-2 audit — returns true, plausibly by design (self-start pattern).</summary>
    private const string WatchActType = nameof(QuestActConAcceptComponent);

    /// <summary>
    /// Walks every loaded quest template and every base act row, collecting structural
    /// findings. Pure function over the loaded state — no I/O, no logging, fully testable.
    /// </summary>
    public static SanityReport VerifyLoadedState(
        IReadOnlyDictionary<uint, QuestTemplate> questTemplates,
        IReadOnlyDictionary<uint, QuestComponentTemplate> componentTemplates,
        IReadOnlyDictionary<uint, QuestActTemplate> actsBaseByActId,
        IReadOnlyDictionary<string, Dictionary<uint, QuestActTemplate>> actTemplatesByDetailType,
        IReadOnlyDictionary<uint, List<uint>> groupItems)
    {
        var findings = new List<Finding>();

        // -- Base act rows: every quest_acts row (with a valid component) must have been
        //    instantiated by a quest_act_xxx detail row, and the instance must belong to
        //    this act's component. A base act without an instance is a silently missing
        //    objective/reward: the component's runtime act list simply never gets it.
        foreach (var baseAct in actsBaseByActId.Values)
        {
            var type = baseAct.DetailType;
            if (!actTemplatesByDetailType.ContainsKey(type))
            {
                findings.Add(new Finding(Severity.Error, "ACT_UNKNOWN_TYPE",
                    $"Quest {baseAct.ParentQuestTemplate.Id} component {baseAct.ParentComponent.Id} act {baseAct.ActId}: " +
                    $"act_detail_type '{type}' has no handler class — act can never run"));
                continue;
            }

            var instance = actTemplatesByDetailType[type].GetValueOrDefault(baseAct.DetailId);
            if (instance == null)
            {
                findings.Add(new Finding(Severity.Error, "ACT_UNINSTANTIATED",
                    $"Quest {baseAct.ParentQuestTemplate.Id} component {baseAct.ParentComponent.Id} act {baseAct.ActId}: " +
                    $"{type} detail id {baseAct.DetailId} has no quest_act_xxx row — act never instantiated, objective silently missing"));
                continue;
            }

            if (instance.ParentComponent.Id != baseAct.ParentComponent.Id)
            {
                findings.Add(new Finding(Severity.Error, "ACT_DETACHED",
                    $"Quest {baseAct.ParentQuestTemplate.Id} component {baseAct.ParentComponent.Id} act {baseAct.ActId}: " +
                    $"{type} detail id {baseAct.DetailId} is shared with component {instance.ParentComponent.Id} — " +
                    "instance is only wired to the first component, act is missing here"));
            }
        }

        // -- Per loaded quest template --
        foreach (var quest in questTemplates.Values)
        {
            if (quest.Components.Count <= 0)
            {
                findings.Add(new Finding(Severity.Warn, "QUEST_NO_COMPONENTS",
                    $"Quest {quest.Id}: template has no components — can never be accepted or run"));
                continue;
            }

            if (!quest.Components.Values.Any(c => c.KindId == QuestComponentKind.Start))
            {
                findings.Add(new Finding(Severity.Warn, "QUEST_NO_START",
                    $"Quest {quest.Id}: has components but no Start component — cannot be accepted via the normal flow"));
            }

            foreach (var component in quest.Components.Values)
            {
                if (component.NextComponent != 0 && !quest.Components.ContainsKey(component.NextComponent))
                {
                    findings.Add(new Finding(Severity.Error, "COMPONENT_NEXT_MISSING",
                        $"Quest {quest.Id} component {component.Id}: next_component {component.NextComponent} does not exist in this quest"));
                }

                if (component.ActTemplates.Count <= 0)
                    continue;

                foreach (var act in component.ActTemplates)
                {
                    // Components referencing missing objectives / quests
                    switch (act)
                    {
                        case QuestActCheckCompleteComponent checkComplete
                            when !componentTemplates.ContainsKey(checkComplete.CompleteComponent):
                            findings.Add(new Finding(Severity.Error, "ACT_REF_MISSING_COMPONENT",
                                $"Quest {quest.Id} component {component.Id} act {checkComplete.DetailId}: " +
                                $"QuestActCheckCompleteComponent references missing component {checkComplete.CompleteComponent} — check can never pass"));
                            break;
                        case QuestActConAcceptComponent acceptComponent
                            when !questTemplates.ContainsKey(acceptComponent.QuestContextId):
                            findings.Add(new Finding(Severity.Error, "ACT_REF_MISSING_QUEST",
                                $"Quest {quest.Id} component {component.Id} act {acceptComponent.DetailId}: " +
                                $"QuestActConAcceptComponent references missing quest context {acceptComponent.QuestContextId} — self-start target can never be found"));
                            break;
                        case QuestActObjCompleteQuest completeQuest
                            when !questTemplates.ContainsKey(completeQuest.QuestId):
                            findings.Add(new Finding(Severity.Error, "ACT_REF_MISSING_COMPLETE_QUEST",
                                $"Quest {quest.Id} component {component.Id} act {completeQuest.DetailId}: " +
                                $"QuestActObjCompleteQuest references missing quest {completeQuest.QuestId}"));
                            break;
                        case QuestActCheckTimer timer
                            when timer.NextComponent != 0 && !quest.Components.ContainsKey(timer.NextComponent):
                            findings.Add(new Finding(Severity.Error, "ACT_NEXT_MISSING",
                                $"Quest {quest.Id} component {component.Id} act {timer.DetailId}: " +
                                $"QuestActCheckTimer next_component {timer.NextComponent} does not exist in this quest"));
                            break;
                    }

                    // Known stub acts (M1-2 audit catalog)
                    if (s_knownStubActTypes.TryGetValue(act.GetType().Name, out var stubNote))
                    {
                        findings.Add(new Finding(Severity.Warn, "ACT_STUB_KNOWN",
                            $"Quest {quest.Id} component {component.Id} act {act.DetailId}: {act.GetType().Name} is a known stub ({stubNote})"));
                    }
                }
            }
        }

        // -- Item-group references (only meaningful for the group acts) --
        foreach (var act in actTemplatesByDetailType.Values.SelectMany(byType => byType.Values))
        {
            switch (act)
            {
                case QuestActObjItemGroupGather gather when !groupItems.ContainsKey(gather.ItemGroupId):
                    findings.Add(new Finding(Severity.Error, "ACT_GROUP_MISSING",
                        $"Quest {gather.ParentQuestTemplate.Id} component {gather.ParentComponent.Id} act {gather.DetailId}: " +
                        $"QuestActObjItemGroupGather references missing item group {gather.ItemGroupId}"));
                    break;
                case QuestActObjItemGroupUse use when !groupItems.ContainsKey(use.ItemGroupId):
                    findings.Add(new Finding(Severity.Error, "ACT_GROUP_MISSING",
                        $"Quest {use.ParentQuestTemplate.Id} component {use.ParentComponent.Id} act {use.DetailId}: " +
                        $"QuestActObjItemGroupUse references missing item group {use.ItemGroupId}"));
                    break;
            }
        }

        // -- Watch item (M1-2): ConAcceptComponent self-start pattern, informational count --
        if (actTemplatesByDetailType.TryGetValue(WatchActType, out var acceptComponentActs) && acceptComponentActs.Count > 0)
        {
            findings.Add(new Finding(Severity.Info, "ACT_WATCH",
                $"QuestActConAcceptComponent: {acceptComponentActs.Count} acts registered " +
                "(self-referencing starter pattern, returns true by design — spot-check per M1-2)"));
        }

        return new SanityReport(findings, questTemplates.Count, componentTemplates.Count, actsBaseByActId.Count);
    }

    /// <summary>
    /// SQL-level hygiene checks that need the raw table data (rows skipped by the loaders
    /// are invisible in memory): orphaned act/component rows, act types with no handler
    /// class, and the quest_act_obj_aliases dormancy check (M1-1 verdict).
    /// </summary>
    public static IReadOnlyList<Finding> VerifyData(SqliteConnection connection, IReadOnlyCollection<string> registeredActTypes)
    {
        var findings = new List<Finding>();

        // quest_acts rows referencing a missing quest_component — never instantiated (dead data, no crash)
        var orphanActs = ScalarCount(connection,
            "SELECT COUNT(*) FROM quest_acts a LEFT JOIN quest_components qc ON qc.id = a.quest_component_id WHERE qc.id IS NULL");
        findings.Add(new Finding(Severity.Info, "DATA_ORPHAN_ACTS",
            $"{orphanActs} quest_acts rows reference a missing quest_component — never instantiated (dead data, no crash)"));

        // quest_components rows referencing a missing quest_context — silently skipped by LoadQuestComponents
        var orphanComponents = ScalarCount(connection,
            "SELECT COUNT(*) FROM quest_components qc LEFT JOIN quest_contexts q ON q.id = qc.quest_context_id WHERE q.id IS NULL");
        findings.Add(new Finding(Severity.Info, "DATA_ORPHAN_COMPONENTS",
            $"{orphanComponents} quest_components rows reference a missing quest_context — never loaded"));

        // act_detail_type values with no handler class — every row of such a type stalls
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT act_detail_type, COUNT(*) FROM quest_acts GROUP BY act_detail_type";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var count = reader.GetInt64(1);
                if (!registeredActTypes.Contains(type))
                {
                    findings.Add(new Finding(Severity.Error, "DATA_UNKNOWN_TYPE",
                        $"{count} quest_acts rows have act_detail_type '{type}' which has no handler class — these acts can never run"));
                }
            }
        }

        // quest_act_obj_aliases dormancy (M1-1 verdict): the alias dictionary is dormant when
        // no quest_act_xxx row has use_alias=1. If any appear, alias resolution is NOT implemented
        // and those objectives silently never resolve.
        var aliasUseCount = 0L;
        using (var tablesCommand = connection.CreateCommand())
        {
            tablesCommand.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'quest_act_%' AND name NOT LIKE '%aliases' ORDER BY name";
            using var tablesReader = tablesCommand.ExecuteReader();
            while (tablesReader.Read())
            {
                var table = tablesReader.GetString(0);
                if (!HasColumn(connection, table, "use_alias"))
                    continue;
                aliasUseCount += ScalarCount(connection, $"SELECT COUNT(*) FROM {table} WHERE use_alias = 1");
            }
        }

        findings.Add(aliasUseCount > 0
            ? new Finding(Severity.Warn, "DATA_ALIAS_USE",
                $"{aliasUseCount} quest_act_xxx rows have use_alias=1 but quest_act_obj_aliases resolution is NOT implemented — those objectives silently never resolve")
            : new Finding(Severity.Info, "DATA_ALIAS_USE",
                "quest_act_obj_aliases is DORMANT (0 use_alias=1 rows across all quest_act_xxx tables) — M1-1 verdict confirmed, no alias resolution needed"));

        return findings;
    }

    /// <summary>
    /// Logs every finding at its severity and prints a loud summary line. Never throws.
    /// </summary>
    public static void LogReport(SanityReport report)
    {
        var logger = LogManager.GetCurrentClassLogger();
        foreach (var finding in report.Findings)
        {
            switch (finding.Severity)
            {
                case Severity.Error:
                    logger.Error($"[QuestSanity] {finding.Code}: {finding.Message}");
                    break;
                case Severity.Warn:
                    logger.Warn($"[QuestSanity] {finding.Code}: {finding.Message}");
                    break;
                default:
                    logger.Info($"[QuestSanity] {finding.Code}: {finding.Message}");
                    break;
            }
        }

        if (report.ErrorCount > 0)
        {
            logger.Error(
                $"[QuestSanity] SUMMARY: {report.ErrorCount} ERRORS, {report.WarnCount} warnings, {report.InfoCount} info " +
                $"across {report.QuestCount} quests / {report.ComponentCount} components / {report.ActCount} acts — " +
                "quest data has structural defects, see findings above (server continues to start, defects are logged loudly)");
        }
        else
        {
            logger.Info(
                $"[QuestSanity] SUMMARY: OK — {report.ErrorCount} errors, {report.WarnCount} warnings, {report.InfoCount} info " +
                $"across {report.QuestCount} quests / {report.ComponentCount} components / {report.ActCount} acts");
        }
    }

    private static long ScalarCount(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
