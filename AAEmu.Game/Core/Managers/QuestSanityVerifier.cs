using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units.Static;

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

    /// <summary>
    /// A single defect finding. <see cref="QuestId"/> is the quest template the finding
    /// belongs to (null for SQL-level findings with no quest scope) — used by the
    /// allowlist downgrade and the zone/kind rollup.
    /// </summary>
    public sealed record Finding(Severity Severity, string Code, string Message, uint? QuestId = null);

    /// <summary>Per-zone census rollup (zone = quest_contexts.zone_id → zone_groups.id).</summary>
    public sealed record ZoneRollup(uint ZoneId, int QuestCount, int FailedQuestCount, int WarnedQuestCount);

    /// <summary>Per-kind census rollup (kind = quest_contexts.category_id).</summary>
    public sealed record KindRollup(uint KindId, int QuestCount, int FailedQuestCount, int WarnedQuestCount);

    public sealed record SanityReport(
        IReadOnlyList<Finding> Findings,
        int QuestCount,
        int ComponentCount,
        int ActCount,
        IReadOnlyList<ZoneRollup> ZoneRollups,
        IReadOnlyList<KindRollup> KindRollups)
    {
        public int ErrorCount => Findings.Count(f => f.Severity == Severity.Error);
        public int WarnCount => Findings.Count(f => f.Severity == Severity.Warn);
        public int InfoCount => Findings.Count(f => f.Severity == Severity.Info);
    }

    /// <summary>
    /// Quest ids intentionally allowed to report at INFO instead of WARN/ERROR. Populated
    /// from the M1 data-defect classification (scorecard-explorations/data-defects.md,
    /// t_7416ea48) — every id verified against prod compact.sqlite3 (md5
    /// 78b3bdbf0383db3b927056106efdf91af). The census stays green without deleting data rows.
    /// </summary>
    private static readonly HashSet<uint> s_allowlistedQuestIds = BuildAllowlist();

    /// <summary>
    /// Max ids printed in an orphan-id list before eliding with "… and N more" (the
    /// prod orphan quest_acts set is ~7.6k rows — a full listing would flood the log).
    /// </summary>
    private const int MaxListedIds = 50;

    /// <summary>Read-only view of the allowlist (tests assert the classified shells are present).</summary>
    public static IReadOnlySet<uint> AllowlistedQuestIds => s_allowlistedQuestIds;

    private static HashSet<uint> BuildAllowlist()
    {
        var ids = new HashSet<uint>
        {
            // QUEST_NO_START — legacy 1.0-era tutorial shells (data-defects.md §5): single
            // Reward comp (SupplyCopper+SupplyExp) or empty, no accept path, no live deps.
            // 21 are cat-28 zone-1 Gweonid "튜토리얼" steps; 1830/1831 are 미사용 "UNUSED".
            1533, 1640, 1830, 1831,

            // QUEST_NO_COMPONENTS — reserve/dummy/cutscene shells (data-defects.md §6):
            // 315/1728 carry a "do not delete" label (client-side skill/doodad link hooks),
            // 1576/2046 are dummies, 2148–2229 are the "하다보니(reserve)" block,
            // 3748/3750–3757 are the Hadir-farm instance cutscenes (unreachable in M1).
            // 1391 was dropped 2026-08-05 (dropped-content-register.md §1, t_5a61cee3) —
            // NOT allowlisted anymore, so a regression re-reports at WARN.
            315, 1576, 1728, 2046, 3748,

            // Dead cat-34 crafting chain (data-defects.md §4): the orphan mid-chain contexts
            // (1954–1958, 1961, 2140–2143, 2146 — no quest_contexts row, never loaded) and the
            // two live quests whose Reward comps carry dangling ConAcceptComponent acts
            // (1960→1961, 2145→2146). Whole chain is unreachable (roots gated on orphans).
            1960, 1961, 2145, 2146
        };

        // Ranges (inclusive), each with the group it belongs to.
        ids.UnionWith(Enumerable.Range(1535, 1549 - 1535 + 1).Select(i => (uint)i)); // 1535–1549  tutorial shells
        ids.UnionWith(Enumerable.Range(1551, 1554 - 1551 + 1).Select(i => (uint)i)); // 1551–1554  tutorial shells
        ids.UnionWith(Enumerable.Range(2148, 2229 - 2148 + 1).Select(i => (uint)i)); // 2148–2229  reserve block
        ids.UnionWith(Enumerable.Range(3750, 3757 - 3750 + 1).Select(i => (uint)i)); // 3750–3757  Hadir cutscenes
        ids.UnionWith(Enumerable.Range(1954, 1958 - 1954 + 1).Select(i => (uint)i)); // 1954–1958  cat-34 chain
        ids.UnionWith(Enumerable.Range(2140, 2143 - 2140 + 1).Select(i => (uint)i)); // 2140–2143  cat-34 chain
        return ids;
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
                    $"act_detail_type '{type}' has no handler class — act can never run",
                    baseAct.ParentQuestTemplate.Id));
                continue;
            }

            var instance = actTemplatesByDetailType[type].GetValueOrDefault(baseAct.DetailId);
            if (instance == null)
            {
                findings.Add(new Finding(Severity.Error, "ACT_UNINSTANTIATED",
                    $"Quest {baseAct.ParentQuestTemplate.Id} component {baseAct.ParentComponent.Id} act {baseAct.ActId}: " +
                    $"{type} detail id {baseAct.DetailId} has no quest_act_xxx row — act never instantiated, objective silently missing",
                    baseAct.ParentQuestTemplate.Id));
                continue;
            }

            if (instance.ParentComponent.Id != baseAct.ParentComponent.Id)
            {
                findings.Add(new Finding(Severity.Error, "ACT_DETACHED",
                    $"Quest {baseAct.ParentQuestTemplate.Id} component {baseAct.ParentComponent.Id} act {baseAct.ActId}: " +
                    $"{type} detail id {baseAct.DetailId} is shared with component {instance.ParentComponent.Id} — " +
                    "instance is only wired to the first component, act is missing here",
                    baseAct.ParentQuestTemplate.Id));
            }
        }

        // -- Per loaded quest template --
        foreach (var quest in questTemplates.Values)
        {
            if (quest.Components.Count <= 0)
            {
                findings.Add(new Finding(Severity.Warn, "QUEST_NO_COMPONENTS",
                    $"Quest {quest.Id}: template has no components — can never be accepted or run",
                    quest.Id));
                continue;
            }

            if (!quest.Components.Values.Any(c => c.KindId == QuestComponentKind.Start))
            {
                findings.Add(new Finding(Severity.Warn, "QUEST_NO_START",
                    $"Quest {quest.Id}: has components but no Start component — cannot be accepted via the normal flow",
                    quest.Id));
            }

            foreach (var component in quest.Components.Values)
            {
                // next_component is a deprecated 1.0 field the engine never reads for
                // progression (QuestStep organizes by component kind); a dangling reference
                // is a cosmetic data quirk, so this is a warning, not a runtime defect
                // (data-defects.md §3).
                if (component.NextComponent != 0 && !quest.Components.ContainsKey(component.NextComponent))
                {
                    findings.Add(new Finding(Severity.Warn, "COMPONENT_NEXT_MISSING",
                        $"Quest {quest.Id} component {component.Id}: next_component {component.NextComponent} does not exist in this quest",
                        quest.Id));
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
                                $"QuestActCheckCompleteComponent references missing component {checkComplete.CompleteComponent} — check can never pass",
                                quest.Id));
                            break;
                        case QuestActConAcceptComponent acceptComponent
                            when !questTemplates.ContainsKey(acceptComponent.QuestContextId):
                            findings.Add(new Finding(Severity.Error, "ACT_REF_MISSING_QUEST",
                                $"Quest {quest.Id} component {component.Id} act {acceptComponent.DetailId}: " +
                                $"QuestActConAcceptComponent references missing quest context {acceptComponent.QuestContextId} — self-start target can never be found",
                                quest.Id));
                            break;
                        case QuestActObjCompleteQuest completeQuest
                            when !questTemplates.ContainsKey(completeQuest.QuestId):
                            findings.Add(new Finding(Severity.Error, "ACT_REF_MISSING_COMPLETE_QUEST",
                                $"Quest {quest.Id} component {component.Id} act {completeQuest.DetailId}: " +
                                $"QuestActObjCompleteQuest references missing quest {completeQuest.QuestId}",
                                quest.Id));
                            break;
                        case QuestActCheckTimer timer
                            when timer.NextComponent != 0 && !quest.Components.ContainsKey(timer.NextComponent):
                            findings.Add(new Finding(Severity.Error, "ACT_NEXT_MISSING",
                                $"Quest {quest.Id} component {component.Id} act {timer.DetailId}: " +
                                $"QuestActCheckTimer next_component {timer.NextComponent} does not exist in this quest",
                                quest.Id));
                            break;
                    }

                    // Known stub acts (M1-2 audit catalog)
                    if (s_knownStubActTypes.TryGetValue(act.GetType().Name, out var stubNote))
                    {
                        findings.Add(new Finding(Severity.Warn, "ACT_STUB_KNOWN",
                            $"Quest {quest.Id} component {component.Id} act {act.DetailId}: {act.GetType().Name} is a known stub ({stubNote})",
                            quest.Id));
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
                        $"QuestActObjItemGroupGather references missing item group {gather.ItemGroupId}",
                        gather.ParentQuestTemplate.Id));
                    break;
                case QuestActObjItemGroupUse use when !groupItems.ContainsKey(use.ItemGroupId):
                    findings.Add(new Finding(Severity.Error, "ACT_GROUP_MISSING",
                        $"Quest {use.ParentQuestTemplate.Id} component {use.ParentComponent.Id} act {use.DetailId}: " +
                        $"QuestActObjItemGroupUse references missing item group {use.ItemGroupId}",
                        use.ParentQuestTemplate.Id));
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

        // -- Allowlist (data-defects.md classification): quest ids that are intentionally
        //    dead shells / cosmetic defects report at INFO, keeping the census green
        //    without deleting data rows. --
        for (var i = 0; i < findings.Count; i++)
        {
            var finding = findings[i];
            if (finding.QuestId is uint questId && s_allowlistedQuestIds.Contains(questId))
                findings[i] = finding with { Severity = Severity.Info };
        }

        // -- Zone/kind rollup (post-allowlist severities): which zones/kinds are quest-broken. --
        var rollups = BuildRollups(questTemplates, findings);

        return new SanityReport(findings, questTemplates.Count, componentTemplates.Count, actsBaseByActId.Count,
            rollups.Zones, rollups.Kinds);
    }

    /// <summary>
    /// Aggregates post-allowlist findings per zone (quest_contexts.zone_id) and per kind
    /// (quest_contexts.category_id): total quests, quests with ≥1 Error finding, quests with
    /// ≥1 Warn finding. Feeds the per-zone census lines (M2 world-broadening planning).
    /// </summary>
    private static (List<ZoneRollup> Zones, List<KindRollup> Kinds) BuildRollups(
        IReadOnlyDictionary<uint, QuestTemplate> questTemplates,
        IReadOnlyList<Finding> findings)
    {
        var failed = new Dictionary<uint, bool>();
        var warned = new Dictionary<uint, bool>();
        foreach (var finding in findings)
        {
            if (finding.QuestId is not uint questId)
                continue;
            if (finding.Severity == Severity.Error)
                failed[questId] = true;
            else if (finding.Severity == Severity.Warn)
                warned[questId] = true;
        }

        var questCountByZone = new Dictionary<uint, int>();
        var failedByZone = new Dictionary<uint, int>();
        var warnedByZone = new Dictionary<uint, int>();
        var questCountByKind = new Dictionary<uint, int>();
        var failedByKind = new Dictionary<uint, int>();
        var warnedByKind = new Dictionary<uint, int>();

        foreach (var quest in questTemplates.Values)
        {
            var isFailed = failed.GetValueOrDefault(quest.Id);
            var isWarned = warned.GetValueOrDefault(quest.Id);

            questCountByZone[quest.ZoneId] = questCountByZone.GetValueOrDefault(quest.ZoneId) + 1;
            if (isFailed) failedByZone[quest.ZoneId] = failedByZone.GetValueOrDefault(quest.ZoneId) + 1;
            if (isWarned) warnedByZone[quest.ZoneId] = warnedByZone.GetValueOrDefault(quest.ZoneId) + 1;

            questCountByKind[quest.CategoryId] = questCountByKind.GetValueOrDefault(quest.CategoryId) + 1;
            if (isFailed) failedByKind[quest.CategoryId] = failedByKind.GetValueOrDefault(quest.CategoryId) + 1;
            if (isWarned) warnedByKind[quest.CategoryId] = warnedByKind.GetValueOrDefault(quest.CategoryId) + 1;
        }

        var zones = questCountByZone.Keys.OrderBy(z => z)
            .Select(z => new ZoneRollup(z, questCountByZone[z],
                failedByZone.GetValueOrDefault(z), warnedByZone.GetValueOrDefault(z)))
            .ToList();
        var kinds = questCountByKind.Keys.OrderBy(k => k)
            .Select(k => new KindRollup(k, questCountByKind[k],
                failedByKind.GetValueOrDefault(k), warnedByKind.GetValueOrDefault(k)))
            .ToList();

        return (zones, kinds);
    }

    /// <summary>
    /// SQL-level hygiene checks that need the raw table data (rows skipped by the loaders
    /// are invisible in memory): orphaned act/component rows, act types with no handler
    /// class, and the quest_act_obj_aliases dormancy check (M1-1 verdict).
    /// </summary>
    public static IReadOnlyList<Finding> VerifyData(SqliteConnection connection, IReadOnlyCollection<string> registeredActTypes)
    {
        var findings = new List<Finding>();

        // quest_acts rows referencing a missing quest_component — never instantiated (dead data, no crash).
        // List the orphan act ids (capped — prod has ~7.6k of them) so the census is self-documenting.
        var orphanActIds = new List<long>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT a.id FROM quest_acts a LEFT JOIN quest_components qc ON qc.id = a.quest_component_id " +
                                  "WHERE qc.id IS NULL ORDER BY a.id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                orphanActIds.Add(reader.GetInt64(0));
        }

        findings.Add(new Finding(Severity.Info, "DATA_ORPHAN_ACTS",
            $"{orphanActIds.Count} quest_acts rows reference a missing quest_component — never instantiated (dead data, no crash)" +
            $"(orphan quest_act ids: {FormatIdList(orphanActIds)})"));

        // quest_components rows referencing a missing quest_context — silently skipped by
        // LoadQuestComponents. List the orphan quest_context ids (data-defects.md §7: 28 ids).
        long orphanComponentRows = 0;
        var orphanContextIds = new List<long>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT qc.quest_context_id, COUNT(*) FROM quest_components qc " +
                                  "LEFT JOIN quest_contexts q ON q.id = qc.quest_context_id " +
                                  "WHERE q.id IS NULL GROUP BY qc.quest_context_id ORDER BY qc.quest_context_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                orphanContextIds.Add(reader.GetInt64(0));
                orphanComponentRows += reader.GetInt64(1);
            }
        }

        findings.Add(new Finding(Severity.Info, "DATA_ORPHAN_COMPONENTS",
            $"{orphanComponentRows} quest_components rows reference a missing quest_context — never loaded" +
            $"(orphan quest_context ids: {FormatIdList(orphanContextIds)})"));

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
    /// UNIT_REQS layer check (audit: scorecard-explorations/unit-reqs-layer.md, t_c87c5deb).
    /// QuestComponent-owned unit_reqs rows with a POSITIVE quest-context kind
    /// (CompleteQuestContext / ProgressQuestContext / ReadyQuestContext / PreCompleteQuestContext)
    /// must resolve value1 against quest_contexts.id only. Skipped by design:
    ///   - kind 1 (Level): value1 is a LEVEL, not a quest id (the 45 rows with value1=14
    ///     are level gates, not quest deps)
    ///   - kind 36 (ExceptCompleteQuestContext) + kind 72/73 (ExceptProgress/ExceptReady):
    ///     negative gates — "must NOT have completed/started X" against a missing quest is
    ///     vacuously true, no player impact.
    /// Missing contexts are classified (same discriminator as the audit):
    ///   - surviving quest body (quest_components rows with quest_context_id = value1)
    ///     => orphaned template => WARN (gate can never pass; chain already ruled drop)
    ///   - NO body but value1 owned by another entity table (spheres/npcs/doodad_almighties/
    ///     ai_events/items) => id-space collision => INFO (number reused, not a quest dep)
    ///   - no body, no other-table ownership => genuinely missing context => WARN.
    /// Emits per-row findings plus a UNIT_REQS_SUMMARY rollup (count → missing contexts,
    /// gated quests list, collisions list).
    /// </summary>
    public static IReadOnlyList<Finding> VerifyUnitReqs(SqliteConnection connection)
    {
        var findings = new List<Finding>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.id, r.owner_id, r.kind_id, r.value1, qc.quest_context_id,
                       EXISTS(SELECT 1 FROM quest_components b WHERE b.quest_context_id = r.value1) AS has_body
                FROM unit_reqs r
                LEFT JOIN quest_components qc ON qc.id = r.owner_id
                WHERE r.owner_type = 'QuestComponent'
                  AND r.kind_id IN (31, 32, 33, 37)
                  AND r.value1 NOT IN (SELECT id FROM quest_contexts)
                ORDER BY r.value1, r.id
                """;
            using var reader = command.ExecuteReader();

            var missingContexts = new SortedSet<uint>();
            var collisions = new SortedSet<uint>();
            var gatedQuests = new SortedSet<uint>();
            var orphanCount = 0;
            var collisionCount = 0;

            while (reader.Read())
            {
                var rowId = (uint)reader.GetInt64(0);
                var ownerId = (uint)reader.GetInt64(1);
                var kindId = (uint)reader.GetInt64(2);
                var value1 = (uint)reader.GetInt64(3);
                var gatedQuest = reader.IsDBNull(4) ? 0u : (uint)reader.GetInt64(4);
                var hasBody = reader.GetInt64(5) > 0;

                missingContexts.Add(value1);
                if (gatedQuest != 0)
                    gatedQuests.Add(gatedQuest);

                if (hasBody)
                {
                    // Orphaned template: the quest body survives, the context row is gone.
                    orphanCount++;
                    findings.Add(new Finding(Severity.Warn, "UNIT_REQS_MISSING_CONTEXT",
                        $"unit_reqs {rowId} (QuestComponent {ownerId} of quest {gatedQuest}): kind {(UnitReqsKindType)kindId} " +
                        $"references missing quest context {value1} (quest body survives, context row gone) — gate can never pass"));
                }
                else
                {
                    var collisionTables = CollisionTablesOwn(connection, value1);
                    if (collisionTables.Count > 0)
                    {
                        // Id-space collision: no quest body at all, the number is a live entity of another type.
                        collisionCount++;
                        collisions.Add(value1);
                        findings.Add(new Finding(Severity.Info, "UNIT_REQS_COLLISION",
                            $"unit_reqs {rowId} (QuestComponent {ownerId} of quest {gatedQuest}): kind {(UnitReqsKindType)kindId} value1 {value1} " +
                            $"is an id-space collision — id owned by {string.Join("/", collisionTables)}, not a quest context"));
                    }
                    else
                    {
                        orphanCount++;
                        findings.Add(new Finding(Severity.Warn, "UNIT_REQS_MISSING_CONTEXT",
                            $"unit_reqs {rowId} (QuestComponent {ownerId} of quest {gatedQuest}): kind {(UnitReqsKindType)kindId} " +
                            $"references missing quest context {value1} — gate can never pass"));
                    }
                }
            }

            var gatedList = gatedQuests.Count > 0 ? string.Join(",", gatedQuests) : "(none)";
            var collisionList = collisions.Count > 0 ? string.Join(",", collisions) : "(none)";
            findings.Add(new Finding(Severity.Info, "UNIT_REQS_SUMMARY",
                $"unit_reqs: {missingContexts.Count} missing contexts from {orphanCount + collisionCount} QuestComponent-owned rows " +
                $"({orphanCount} orphans WARN / {collisionCount} collisions INFO); gated quests: {gatedList}; collisions: {collisionList}"));
        }

        return findings;
    }

    /// <summary>Entity tables whose ids share the same number space as quest contexts (collision evidence).</summary>
    private static readonly string[] s_unitReqsCollisionTables =
        ["spheres", "npcs", "doodad_almighties", "ai_events", "items"];

    /// <summary>Which entity tables own the given id (id-space collision evidence).</summary>
    private static List<string> CollisionTablesOwn(SqliteConnection connection, uint id)
    {
        var tables = new List<string>();
        foreach (var table in s_unitReqsCollisionTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = @id";
            command.Parameters.AddWithValue("@id", id);
            if ((long)command.ExecuteScalar() > 0)
                tables.Add(table);
        }

        return tables;
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

        // Zone/kind rollup — one glance answers "which zones are quest-broken" (M2
        // world-broadening planning). Only zones/kinds with failed or warned quests print;
        // a fully green census emits the "none" line.
        var brokenZones = report.ZoneRollups.Where(z => z.FailedQuestCount > 0 || z.WarnedQuestCount > 0).ToList();
        if (brokenZones.Count > 0)
        {
            foreach (var zone in brokenZones)
                logger.Info($"[QuestSanity] ZONE {zone.ZoneId}: {zone.QuestCount} quests, {zone.FailedQuestCount} failed, {zone.WarnedQuestCount} warned");
        }
        else
        {
            logger.Info("[QuestSanity] ZONES: no quests with errors or warnings");
        }

        var brokenKinds = report.KindRollups.Where(k => k.FailedQuestCount > 0 || k.WarnedQuestCount > 0).ToList();
        if (brokenKinds.Count > 0)
        {
            foreach (var kind in brokenKinds)
                logger.Info($"[QuestSanity] KIND {kind.KindId}: {kind.QuestCount} quests, {kind.FailedQuestCount} failed, {kind.WarnedQuestCount} warned");
        }
        else
        {
            logger.Info("[QuestSanity] KINDS: no quests with errors or warnings");
        }
    }

    /// <summary>Joins an id list for a census message, eliding past <see cref="MaxListedIds"/> entries.</summary>
    private static string FormatIdList(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
            return "none";
        var shown = string.Join(", ", ids.Take(MaxListedIds));
        return ids.Count > MaxListedIds ? $"{shown} … and {ids.Count - MaxListedIds} more" : shown;
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
