using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace AAEmu.ArchaeologyMcp;

/// <summary>
/// Domain archaeology tools (partial <see cref="ArchaeologyService"/>):
/// cross-table search, bounded reference tracing, quest-objective discovery,
/// and schema-backed wrappers for skills, items, quests, NPCs, doodads,
/// mates, vehicles, crafting, world spawns, physics, and source-data
/// comparison. All SQL is parameterized; table/column names come only from
/// <c>sqlite_master</c> / <c>PRAGMA table_info</c> introspection, never from
/// user input. Evidence labels are honest: <c>exact</c> only for declared
/// foreign keys, <c>heuristic</c> for name-convention links, <c>textual</c>
/// for value matches.
/// </summary>
public sealed partial class ArchaeologyService
{
    private const int MaxSearchTables = 300;
    private const int MaxSearchColumnsPerTable = 8;
    private const int MaxTraceDepth = 3;
    private const int MaxTraceNodes = 200;
    private const int MaxTraceSeedTables = 100;
    private const int MaxFileScan = 1000;

    private sealed record ColumnInfo(string Name, string Type, bool IsPk);

    private sealed record ForeignKey(string FromCol, string ToTable, string ToCol);

    /// <summary>
    /// A deterministic domain-tool failure (SQLite error, timeout, or
    /// malformed data) that must surface as an honest ErrorResult rather
    /// than an uncaught exception.
    /// </summary>
    private sealed class DomainQueryException : Exception
    {
        public DomainQueryException(string message) : base(message) { }
    }

    /// <summary>
    /// Runs a domain-tool body, converting deterministic failures into
    /// ErrorResult. SQLite errors and timeouts are reported verbatim;
    /// anything else is a server bug and rethrown.
    /// </summary>
    private JsonObject RunDomain(string tool, Func<JsonObject> body)
    {
        try
        {
            return body();
        }
        catch (DomainQueryException ex)
        {
            return ErrorResult(tool, ex.Message);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9)
        {
            return ErrorResult(tool, $"query timed out after {QueryTimeoutSecondsOverride ?? QueryTimeoutSeconds}s");
        }
        catch (SqliteException ex)
        {
            return ErrorResult(tool, $"SQLite error: {ex.Message}");
        }
    }

    // ------------------------------------------------------ search_everything

    public JsonObject SearchEverything(string term, int? limit)
        => RunDomain("search_everything", () =>
        {
            if (string.IsNullOrWhiteSpace(term))
                return ErrorResult("search_everything", "term is required");

            var dbPath = _catalog.DbPath;
            var resultLimit = Math.Clamp(limit ?? 50, 1, 500);
            var pattern = "%" + EscapeLike(term) + "%";
            var dbHits = new JsonArray();
            var truncated = false;
            var tablesScanned = 0;

            using (var connection = OpenReadOnly(dbPath))
            {
                foreach (var table in ListTableNames(connection))
                {
                    if (tablesScanned >= MaxSearchTables || dbHits.Count >= resultLimit)
                    {
                        truncated = true;
                        break;
                    }

                    tablesScanned++;
                    var columns = TableColumns(connection, table);
                    var textColumns = columns.Where(c => IsTextType(c.Type)).Take(MaxSearchColumnsPerTable).ToList();
                    if (textColumns.Count == 0)
                        continue;

                    var keyColumn = columns.Any(c => c.Name == "id") ? "id" : columns[0].Name;
                    foreach (var column in textColumns)
                    {
                        if (dbHits.Count >= resultLimit)
                        {
                            truncated = true;
                            break;
                        }

                        var remaining = resultLimit - dbHits.Count;
                        var sql = $"SELECT {keyColumn} AS id, {column.Name} AS value FROM {table} " +
                                  $"WHERE {column.Name} LIKE @pattern ESCAPE '\\' ORDER BY {keyColumn} LIMIT @lim";
                        var (rows, rowTruncated) = QueryRowsBounded(dbPath, sql,
                            new JsonObject { ["pattern"] = pattern, ["lim"] = remaining }, remaining);
                        truncated |= rowTruncated;
                        foreach (var row in rows)
                        {
                            dbHits.Add(new JsonObject
                            {
                                ["table"] = table,
                                ["column"] = column.Name,
                                ["id"] = row?["id"]?.DeepClone(),
                                ["value"] = row?["value"]?.DeepClone(),
                            });
                            if (dbHits.Count >= resultLimit)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                }
            }

            var (fileMatches, filesScanned, fileTruncated) = SearchFilesForTerm(term, resultLimit - dbHits.Count);
            truncated |= fileTruncated;

            return Result("search_everything", new JsonObject
            {
                ["term"] = term,
                ["db_hits"] = dbHits,
                ["file_matches"] = fileMatches,
                ["hit_count"] = dbHits.Count + fileMatches.Count,
                ["tables_scanned"] = tablesScanned,
                ["files_scanned"] = filesScanned,
                ["truncated"] = truncated,
                ["limit"] = resultLimit,
            }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion, truncated: truncated);
        });

    // ------------------------------------------------------ trace_references

    public JsonObject TraceReferences(string identifier, string? domain, string? table, int? depth, int? limit)
        => RunDomain("trace_references", () => TraceReferencesCore(identifier, domain, table, depth, limit));

    private JsonObject TraceReferencesCore(string identifier, string? domain, string? table, int? depth, int? limit)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return ErrorResult("trace_references", "identifier is required");

        var dbPath = _catalog.DbPath;
        var maxDepth = Math.Clamp(depth ?? 2, 1, MaxTraceDepth);
        var maxNodes = Math.Clamp(limit ?? MaxTraceNodes, 1, MaxTraceNodes);
        var nodes = new List<JsonObject>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Table, long Id, int Depth, string Evidence, string? Via)>();
        var idValue = long.TryParse(identifier, out var parsed) ? parsed : (long?)null;

        using (var connection = OpenReadOnly(dbPath))
        {
            var seedTables = ListTableNames(connection)
                .Where(t => table is null || t == table)
                .Where(t => domain is null || t.Contains(domain, StringComparison.OrdinalIgnoreCase))
                .Take(MaxTraceSeedTables)
                .ToList();

            // Reverse indexes for incoming edges, built once per call.
            var incomingExact = new Dictionary<string, List<(string FromTable, string FromCol)>>(StringComparer.Ordinal);
            var incomingHeuristic = new Dictionary<string, List<(string FromTable, string FromCol)>>(StringComparer.Ordinal);
            foreach (var candidateTable in ListTableNames(connection))
            {
                var columns = TableColumns(connection, candidateTable);
                foreach (var fk in ForeignKeys(connection, candidateTable))
                {
                    if (!incomingExact.TryGetValue(fk.ToTable, out var list))
                        incomingExact[fk.ToTable] = list = [];
                    list.Add((candidateTable, fk.FromCol));
                }

                foreach (var column in columns.Where(c => c.Name.EndsWith("_id", StringComparison.Ordinal) && c.Name != "id"))
                {
                    var target = FindIdTable(connection, column.Name[..^3]);
                    if (target is null)
                        continue;
                    if (!incomingHeuristic.TryGetValue(target, out var list))
                        incomingHeuristic[target] = list = [];
                    list.Add((candidateTable, column.Name));
                }
            }

            foreach (var seedTable in seedTables)
            {
                var keyColumn = KeyColumn(connection, seedTable);
                if (keyColumn is null)
                    continue; // no single key column — cannot address rows
                var columns = TableColumns(connection, seedTable);
                if (idValue.HasValue)
                {
                    var hit = QueryScalarLong(connection,
                        $"SELECT {keyColumn} FROM {seedTable} WHERE {keyColumn} = @v", idValue.Value);
                    if (hit.HasValue)
                        Enqueue(seedTable, hit.Value, 0, "exact", null);
                }

                foreach (var column in columns.Where(c => IsTextType(c.Type)).Take(4))
                {
                    var hit = QueryScalarLong(connection,
                        $"SELECT {keyColumn} FROM {seedTable} WHERE {column.Name} = @v", identifier);
                    if (hit.HasValue)
                        Enqueue(seedTable, hit.Value, 0, "textual", column.Name);
                }
            }

            while (queue.Count > 0 && nodes.Count < maxNodes)
            {
                var (currentTable, currentId, currentDepth, evidence, via) = queue.Dequeue();
                var key = currentTable + "|" + currentId;
                if (!visited.Add(key))
                    continue;

                nodes.Add(new JsonObject
                {
                    ["table"] = currentTable,
                    ["id"] = currentId,
                    ["depth"] = currentDepth,
                    ["evidence"] = evidence,
                    ["via"] = via,
                });
                if (currentDepth >= maxDepth)
                    continue;

                var currentKey = KeyColumn(connection, currentTable);
                if (currentKey is null)
                    continue; // no single key column — skip edges through it

                // Outgoing exact edges: declared foreign keys only.
                var foreignKeys = ForeignKeys(connection, currentTable);
                foreach (var fk in foreignKeys)
                {
                    var value = QueryScalarLong(connection,
                        $"SELECT {fk.FromCol} FROM {currentTable} WHERE {currentKey} = @v", currentId);
                    if (!value.HasValue || !TableExists(connection, fk.ToTable))
                        continue;
                    Enqueue(fk.ToTable, value.Value, currentDepth + 1, "exact",
                        $"{currentTable}.{fk.FromCol} → {fk.ToTable}.{fk.ToCol}");
                }

                // Outgoing heuristic edges: *_id columns (not the PK, not a
                // declared FK) whose prefix names an existing table with an id.
                var fkColumns = new HashSet<string>(foreignKeys.Select(f => f.FromCol), StringComparer.Ordinal);
                foreach (var column in TableColumns(connection, currentTable)
                             .Where(c => c.Name.EndsWith("_id", StringComparison.Ordinal)
                                         && c.Name != "id"
                                         && !fkColumns.Contains(c.Name)))
                {
                    var value = QueryScalarLong(connection,
                        $"SELECT {column.Name} FROM {currentTable} WHERE {currentKey} = @v", currentId);
                    if (!value.HasValue)
                        continue;
                    var candidate = FindIdTable(connection, column.Name[..^3]);
                    if (candidate is null)
                        continue;
                    Enqueue(candidate, value.Value, currentDepth + 1, "heuristic",
                        $"{currentTable}.{column.Name} → {candidate}.id");
                }

                // Incoming exact edges: rows in other tables whose declared FK
                // points at this row.
                if (incomingExact.TryGetValue(currentTable, out var exactIncoming))
                {
                    foreach (var (fromTable, fromCol) in exactIncoming)
                    {
                        var fromKey = KeyColumn(connection, fromTable);
                        if (fromKey is null)
                            continue; // cannot address rows of the source table
                        var value = QueryScalarLong(connection,
                            $"SELECT {fromKey} FROM {fromTable} WHERE {fromCol} = @v", currentId);
                        if (!value.HasValue)
                            continue;
                        Enqueue(fromTable, value.Value, currentDepth + 1, "exact",
                            $"{fromTable}.{fromCol} → {currentTable}.{currentKey}");
                    }
                }

                // Incoming heuristic edges: *_id columns in other tables whose
                // prefix names this table.
                if (incomingHeuristic.TryGetValue(currentTable, out var heuristicIncoming))
                {
                    foreach (var (fromTable, fromCol) in heuristicIncoming)
                    {
                        var fromKey = KeyColumn(connection, fromTable);
                        if (fromKey is null)
                            continue; // cannot address rows of the source table
                        var value = QueryScalarLong(connection,
                            $"SELECT {fromKey} FROM {fromTable} WHERE {fromCol} = @v", currentId);
                        if (!value.HasValue)
                            continue;
                        Enqueue(fromTable, value.Value, currentDepth + 1, "heuristic",
                            $"{fromTable}.{fromCol} → {currentTable}.{currentKey}");
                    }
                }
            }
        }

        var (fileMatches, filesScanned, fileTruncated) = SearchFilesForTerm(identifier, 20);
        var truncated = nodes.Count >= maxNodes || fileTruncated;

        return Result("trace_references", new JsonObject
        {
            ["identifier"] = identifier,
            ["domain"] = domain,
            ["table"] = table,
            ["nodes"] = new JsonArray(nodes.ToArray()),
            ["node_count"] = nodes.Count,
            ["max_depth"] = maxDepth,
            ["file_matches"] = fileMatches,
            ["files_scanned"] = filesScanned,
            ["truncated"] = truncated,
        }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion, truncated: truncated);

        void Enqueue(string targetTable, long targetId, int targetDepth, string targetEvidence, string? targetVia)
        {
            var key = targetTable + "|" + targetId;
            if (visited.Contains(key) || nodes.Count + queue.Count >= maxNodes)
                return;
            queue.Enqueue((targetTable, targetId, targetDepth, targetEvidence, targetVia));
        }
    }

    // --------------------------------------------------- find_quest_objectives

    public JsonObject FindQuestObjectives(int? questId, int? objectiveId, string? family, int? limit)
        => RunDomain("find_quest_objectives", () => FindQuestObjectivesCore(questId, objectiveId, family, limit));

    private JsonObject FindQuestObjectivesCore(int? questId, int? objectiveId, string? family, int? limit)
    {
        var dbPath = _catalog.DbPath;
        var resultLimit = Math.Clamp(limit ?? 50, 1, 500);
        var typeByTable = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> familyTables;

        using (var connection = OpenReadOnly(dbPath))
        {
            var tables = ListTableNames(connection);
            familyTables = tables
                .Where(t => t.StartsWith("quest_act_obj_", StringComparison.Ordinal)
                            && t != "quest_act_obj_aliases")
                .ToList();
            if (familyTables.Count == 0 || !tables.Contains("quest_acts"))
            {
                return Result("find_quest_objectives", new JsonObject
                {
                    ["supported"] = false,
                    ["reason"] = "no quest_act_obj_* tables in this database",
                }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion);
            }

            // Map each act_detail_type (QuestActObjXxx) to its family table by
            // snake_case convention (table = quest_act_obj_<snake>[s]).
            var tableSet = new HashSet<string>(familyTables, StringComparer.Ordinal);
            var types = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT act_detail_type FROM quest_acts";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    types.Add(reader.GetString(0));
            }

            foreach (var type in types.Where(t => t.StartsWith("QuestActObj", StringComparison.Ordinal)))
            {
                var snake = SnakeCase(type["QuestActObj".Length..]);
                foreach (var candidate in new[] { "quest_act_obj_" + snake, "quest_act_obj_" + snake + "s" })
                {
                    if (tableSet.Contains(candidate))
                    {
                        typeByTable[candidate] = type;
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(family))
        {
            var wanted = family.StartsWith("quest_act_obj_", StringComparison.OrdinalIgnoreCase)
                ? family.ToLowerInvariant()
                : "quest_act_obj_" + family.ToLowerInvariant();
            familyTables = familyTables.Where(t => t == wanted).ToList();
            if (familyTables.Count == 0)
            {
                return Result("find_quest_objectives", new JsonObject
                {
                    ["supported"] = true,
                    ["family"] = family,
                    ["rows"] = new JsonArray(),
                    ["row_count"] = 0,
                    ["reason"] = $"no quest_act_obj_* table matches family '{family}'",
                }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion);
            }
        }

        var rows = new JsonArray();
        var truncated = false;
        foreach (var familyTable in familyTables)
        {
            if (rows.Count >= resultLimit)
            {
                truncated = true;
                break;
            }

            if (!typeByTable.TryGetValue(familyTable, out var typeName))
                continue; // no quest_acts row references this family

            var columns = SelectColumns(dbPath, familyTable, 8);
            if (columns.Count == 0)
                continue;

            var conditions = new List<string> { "qa.act_detail_type = @type" };
            var parameters = new JsonObject { ["type"] = typeName };
            if (questId.HasValue)
            {
                conditions.Add("qc.quest_context_id = @qid");
                parameters["qid"] = questId.Value;
            }

            if (objectiveId.HasValue)
            {
                conditions.Add("f.id = @oid");
                parameters["oid"] = objectiveId.Value;
            }

            var remaining = resultLimit - rows.Count;
            var sql = $"SELECT qa.id AS act_id, qa.act_detail_type, qa.act_detail_id, " +
                      $"qc.id AS component_id, qc.quest_context_id, qctx.name AS quest_name, " +
                      $"{string.Join(", ", columns.Select(c => "f." + c))} " +
                      $"FROM {familyTable} f " +
                      $"JOIN quest_acts qa ON qa.act_detail_type = @type AND qa.act_detail_id = f.id " +
                      $"JOIN quest_components qc ON qc.id = qa.quest_component_id " +
                      $"JOIN quest_contexts qctx ON qctx.id = qc.quest_context_id " +
                      $"WHERE {string.Join(" AND ", conditions)} ORDER BY qa.id LIMIT @lim";
            parameters["lim"] = remaining;
            var (familyRows, familyTruncated) = QueryRowsBounded(dbPath, sql, parameters, remaining);
            truncated |= familyTruncated;
            foreach (var row in familyRows)
            {
                var clone = row?.DeepClone()?.AsObject();
                if (clone is null)
                    continue;
                clone["family"] = familyTable;
                rows.Add(clone);
                if (rows.Count >= resultLimit)
                {
                    truncated = true;
                    break;
                }
            }
        }

        return Result("find_quest_objectives", new JsonObject
        {
            ["supported"] = true,
            ["quest_id"] = questId.HasValue ? JsonValue.Create(questId.Value) : null,
            ["objective_id"] = objectiveId.HasValue ? JsonValue.Create(objectiveId.Value) : null,
            ["family"] = family,
            ["rows"] = rows,
            ["row_count"] = rows.Count,
            ["evidence"] = "heuristic",
            ["evidence_note"] = "objective linkage via quest_acts.act_detail_type/act_detail_id convention (no declared FK)",
            ["truncated"] = truncated,
            ["limit"] = resultLimit,
        }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion, truncated: truncated);
    }

    // ------------------------------------------------------------- wrappers

    public JsonObject TraceSkill(int? id, string? name, int? limit)
        => RunDomain("trace_skill", () => TraceEntity("trace_skill", "skills", "name", id, name, limit));

    public JsonObject TraceItem(int? id, string? name, int? limit)
        => RunDomain("trace_item", () => TraceEntity("trace_item", "items", "name", id, name, limit));

    public JsonObject TraceNpc(int? id, string? name, int? limit)
        => RunDomain("trace_npc", () => TraceEntity("trace_npc", "npcs", "name", id, name, limit));

    public JsonObject TraceDoodad(int? id, string? name, int? limit)
        => RunDomain("trace_doodad", () => TraceEntity("trace_doodad", "doodad_almighties", "name", id, name, limit));

    public JsonObject TraceVehicle(int? id, int? limit)
        => RunDomain("trace_vehicle", () => TraceEntity("trace_vehicle", "vehicle_models", "normal", id, null, limit));

    public JsonObject TraceCrafting(int? id, string? title, int? limit)
        => RunDomain("trace_crafting", () => TraceEntity("trace_crafting", "crafts", "title", id, title, limit));

    public JsonObject TraceQuest(int? id, string? name, int? limit)
        => RunDomain("trace_quest", () => TraceQuestCore(id, name, limit));

    private JsonObject TraceQuestCore(int? id, string? name, int? limit)
    {
        var dbPath = _catalog.DbPath;
        if (!TableExists(dbPath, "quest_contexts"))
        {
            return Result("trace_quest", new JsonObject
            {
                ["table"] = "quest_contexts",
                ["supported"] = false,
                ["reason"] = "no quest tables in this database",
            }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion);
        }

        var result = TraceEntity("trace_quest", "quest_contexts", "name", id, name, limit);
        if (result["ok"]?.GetValue<bool>() != true)
            return result;

        var data = result["data"]!.AsObject();
        if (id.HasValue)
        {
            var componentColumns = SelectColumns(dbPath, "quest_components", 8);
            var (components, componentsTruncated) = QueryRowsBounded(dbPath,
                $"SELECT {string.Join(", ", componentColumns)} FROM quest_components " +
                "WHERE quest_context_id = @qid ORDER BY id LIMIT @lim",
                new JsonObject { ["qid"] = id.Value, ["lim"] = 20 }, 20);
            data["components"] = components;
            data["components_truncated"] = componentsTruncated;

            var (acts, _) = QueryRowsBounded(dbPath,
                "SELECT COUNT(*) AS act_count FROM quest_acts qa " +
                "JOIN quest_components qc ON qc.id = qa.quest_component_id " +
                "WHERE qc.quest_context_id = @qid",
                new JsonObject { ["qid"] = id.Value }, 1);
            data["act_count"] = acts.Count > 0 ? acts[0]!["act_count"]?.DeepClone() : JsonValue.Create(0);
        }

        return result;
    }

    public JsonObject TraceMate(int? id, int? itemId, int? limit)
        => RunDomain("trace_mate", () => TraceMateCore(id, itemId, limit));

    private JsonObject TraceMateCore(int? id, int? itemId, int? limit)
    {
        var dbPath = _catalog.DbPath;
        if (!TableExists(dbPath, "item_summon_mates"))
        {
            return Result("trace_mate", new JsonObject
            {
                ["table"] = "item_summon_mates",
                ["supported"] = false,
                ["reason"] = "table 'item_summon_mates' is not present in this database",
            }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion);
        }

        if (id is null && itemId is null)
            return ErrorResult("trace_mate", "id or item_id is required");

        var conditions = new List<string>();
        var parameters = new JsonObject();
        if (id.HasValue)
        {
            conditions.Add("m.id = @id");
            parameters["id"] = id.Value;
        }

        if (itemId.HasValue)
        {
            conditions.Add("m.item_id = @item_id");
            parameters["item_id"] = itemId.Value;
        }

        var resultLimit = Math.Clamp(limit ?? 20, 1, 100);
        var sql = "SELECT m.id, m.item_id, m.npc_id, n.name AS npc_name FROM item_summon_mates m " +
                  "LEFT JOIN npcs n ON n.id = m.npc_id " +
                  $"WHERE {string.Join(" AND ", conditions)} ORDER BY m.id LIMIT @lim";
        parameters["lim"] = resultLimit;
        var (rows, truncated) = QueryRowsBounded(dbPath, sql, parameters, resultLimit);

        return Result("trace_mate", new JsonObject
        {
            ["table"] = "item_summon_mates",
            ["supported"] = true,
            ["rows"] = rows,
            ["row_count"] = rows.Count,
            ["evidence"] = "exact",
            ["truncated"] = truncated,
            ["limit"] = resultLimit,
        }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion, truncated: truncated);
    }

    public JsonObject TraceWorldSpawn(string? name, int? zoneId, int? limit)
        => RunDomain("trace_world_spawn", () => TraceWorldSpawnCore(name, zoneId, limit));

    private JsonObject TraceWorldSpawnCore(string? name, int? zoneId, int? limit)
    {
        var dbPath = _catalog.DbPath;
        var resultLimit = Math.Clamp(limit ?? 20, 1, 100);
        var spawns = new JsonArray();
        var filesMatched = new List<string>();
        var filesScanned = 0;
        var worldsDir = Path.Combine(_catalog.DataRoot, "Worlds");
        var nameFilter = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var nameLower = nameFilter?.ToLowerInvariant();

        if (Directory.Exists(worldsDir))
        {
            // The aggregate named-spawn list is the primary source; scan it
            // first, then the per-world spawn files.
            var aggregate = Path.Combine(worldsDir, "world_spawns.json");
            var files = new List<string>();
            if (File.Exists(aggregate))
                files.Add(aggregate);
            files.AddRange(EnumerateFilesSafe(worldsDir)
                .Where(f => !string.Equals(f, aggregate, StringComparison.Ordinal)));

            foreach (var file in files)
            {
                if (spawns.Count >= resultLimit)
                    break;
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (filesScanned >= MaxFileScan)
                    break; // deterministic scan cap

                filesScanned++;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxReadBytes)
                        continue; // skip oversized spawn files

                    var array = ParseJsonTolerant(File.ReadAllText(file))?.AsArray();
                    if (array is null)
                        continue; // not an array document — skip

                    var fileMatched = false;
                    foreach (var item in array)
                    {
                        if (spawns.Count >= resultLimit)
                            break;

                        // Schema-tolerant: aggregate entries carry Name +
                        // SpawnPosition; per-world entries carry UnitId +
                        // Position. Extract whatever is present.
                        var itemName = item?["Name"]?.GetValue<string>()
                                       ?? item?["name"]?.GetValue<string>()
                                       ?? string.Empty;
                        var unitId = item?["UnitId"]?.GetValue<long>()
                                     ?? item?["unit_id"]?.GetValue<long>();
                        var position = item?["SpawnPosition"] ?? item?["spawn_position"]
                                       ?? item?["Position"] ?? item?["position"];
                        var itemZone = position?["ZoneId"]?.GetValue<int>()
                                       ?? position?["zone_id"]?.GetValue<int>();

                        // Skip entries with no identifier at all.
                        if (itemName.Length == 0 && !unitId.HasValue)
                            continue;

                        if (nameLower is not null)
                        {
                            if (itemName.Length == 0)
                                continue; // unnamed entry cannot match a name
                            if (!MatchesNameFilter(itemName, nameLower))
                                continue;
                        }

                        if (zoneId.HasValue && itemZone != zoneId)
                            continue;

                        var entry = new JsonObject
                        {
                            ["name"] = itemName,
                            ["file"] = file,
                        };
                        if (unitId.HasValue)
                            entry["unit_id"] = unitId.Value;
                        if (itemZone.HasValue)
                            entry["zone_id"] = itemZone.Value;
                        if (position is not null)
                            entry["position"] = position?.DeepClone();
                        spawns.Add(entry);
                        fileMatched = true;
                    }

                    if (fileMatched)
                        filesMatched.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Malformed/unreadable spawn file: skip it, keep scanning.
                }
            }
        }

        var dbSpawns = new JsonArray();
        var dbTruncated = false;
        if (TableExists(dbPath, "npc_spawners"))
        {
            // Fetch a bounded candidate set (all rows when no name filter,
            // else a broad LIKE superset) and apply the same exact /
            // whole-word name matching in C# so numeric/name filters never
            // overmatch (e.g. "3554" must not match "13554").
            var candidateLimit = Math.Clamp(resultLimit * 10, 1, 1000);
            var sql = "SELECT id, name, npc_spawner_category_id, maxPopulation FROM npc_spawners " +
                      (nameFilter is not null
                          ? "WHERE name LIKE @pattern ESCAPE '\\' "
                          : string.Empty) +
                      "ORDER BY id LIMIT @lim";
            var parameters = new JsonObject { ["lim"] = candidateLimit };
            if (nameFilter is not null)
                parameters["pattern"] = "%" + EscapeLike(nameFilter) + "%";

            var (candidates, candidatesTruncated) = QueryRowsBounded(dbPath, sql, parameters, candidateLimit);
            foreach (var candidate in candidates)
            {
                if (dbSpawns.Count >= resultLimit)
                {
                    dbTruncated = true;
                    break;
                }

                if (nameFilter is not null)
                {
                    var candidateName = candidate?["name"]?.GetValue<string>() ?? string.Empty;
                    if (!MatchesNameFilter(candidateName, nameLower!))
                        continue;
                }

                dbSpawns.Add(candidate?.DeepClone());
            }

            dbTruncated |= candidatesTruncated;
        }

        var truncated = spawns.Count >= resultLimit || dbTruncated || filesScanned >= MaxFileScan;
        return Result("trace_world_spawn", new JsonObject
        {
            ["worlds_dir"] = Directory.Exists(worldsDir) ? worldsDir : null,
            ["spawns"] = spawns,
            ["files_matched"] = new JsonArray(filesMatched.Select(f => JsonValue.Create(f)).ToArray()),
            ["files_scanned"] = filesScanned,
            ["npc_spawners"] = dbSpawns,
            ["npc_spawners_truncated"] = dbTruncated,
            ["row_count"] = spawns.Count + dbSpawns.Count,
            ["no_match"] = spawns.Count == 0 && dbSpawns.Count == 0,
            ["truncated"] = truncated,
            ["limit"] = resultLimit,
        }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion, truncated: truncated);
    }

    public JsonObject SearchPhysics(string? term, int? limit)
        => RunDomain("search_physics", () => SearchPhysicsCore(term, limit));

    private JsonObject SearchPhysicsCore(string? term, int? limit)
    {
        var dbPath = _catalog.DbPath;
        List<string> tables;
        using (var connection = OpenReadOnly(dbPath))
        {
            tables = ListTableNames(connection)
                .Where(t => t.StartsWith("physical_", StringComparison.Ordinal))
                .ToList();
        }

        if (tables.Count == 0)
        {
            return Result("search_physics", new JsonObject
            {
                ["supported"] = false,
                ["reason"] = "no physical_* tables in this database; no collision/geometry data exists",
                ["tables"] = new JsonArray(),
            }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion);
        }

        var resultLimit = Math.Clamp(limit ?? 20, 1, 100);
        var perTable = new JsonObject();
        var truncated = false;
        foreach (var table in tables)
        {
            var columns = SelectColumns(dbPath, table, 8);
            if (columns.Count == 0)
                continue;

            var conditions = new List<string>();
            var parameters = new JsonObject();
            if (!string.IsNullOrWhiteSpace(term))
            {
                foreach (var column in columns.Where(c => c != "id").Take(4))
                    conditions.Add($"{column} LIKE @pattern ESCAPE '\\'");
                parameters["pattern"] = "%" + EscapeLike(term) + "%";
            }

            if (conditions.Count == 0)
                conditions.Add("1 = 1");
            var sql = $"SELECT {string.Join(", ", columns)} FROM {table} " +
                      $"WHERE {string.Join(" OR ", conditions)} ORDER BY {columns[0]} LIMIT @lim";
            parameters["lim"] = resultLimit;
            var (rows, rowTruncated) = QueryRowsBounded(dbPath, sql, parameters, resultLimit);
            perTable[table] = rows;
            truncated |= rowTruncated;
        }

        return Result("search_physics", new JsonObject
        {
            ["supported"] = true,
            ["tables"] = new JsonArray(tables.Select(t => JsonValue.Create(t)).ToArray()),
            ["rows"] = perTable,
            ["note"] = "no collision/geometry tables exist in this database; only physical_* effect tables are available",
            ["truncated"] = truncated,
            ["limit"] = resultLimit,
        }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion, truncated: truncated);
    }

    public JsonObject CompareSourceData(string table, string? dbId, int? limit)
        => RunDomain("compare_source_data", () => CompareSourceDataCore(table, dbId, limit));

    private JsonObject CompareSourceDataCore(string table, string? dbId, int? limit)
    {
        if (!IsValidIdentifier(table))
            return ErrorResult("compare_source_data", $"invalid table name: {table}");
        if (string.IsNullOrWhiteSpace(dbId) || dbId == "compact.sqlite3")
            return ErrorResult("compare_source_data", "db_id must be a file:<name> copy to compare against");

        var canonicalPath = _catalog.DbPath;
        if (!TableExists(canonicalPath, table))
        {
            return Result("compare_source_data", new JsonObject
            {
                ["table"] = table,
                ["supported"] = false,
                ["reason"] = $"table '{table}' is not present in the canonical database",
            }, sourceId: "compact.sqlite3", path: canonicalPath, version: _catalog.DbVersion);
        }

        string otherPath;
        try
        {
            otherPath = ResolveDb(dbId);
        }
        catch (ArgumentException ex)
        {
            return ErrorResult("compare_source_data", ex.Message);
        }

        if (!TableExists(otherPath, table))
        {
            return Result("compare_source_data", new JsonObject
            {
                ["table"] = table,
                ["supported"] = true,
                ["present_in_other"] = false,
                ["reason"] = $"table '{table}' is not present in {dbId}",
            }, sourceId: dbId, path: otherPath, version: null);
        }

        var columns = SelectColumns(canonicalPath, table, 50);
        if (columns.Count == 0)
            return ErrorResult("compare_source_data", $"table '{table}' has no columns");

        var (countRows1, _) = QueryRowsBounded(canonicalPath, $"SELECT COUNT(*) AS c FROM {table}", null, 1);
        var (countRows2, _) = QueryRowsBounded(otherPath, $"SELECT COUNT(*) AS c FROM {table}", null, 1);
        var count1 = countRows1.Count > 0 ? countRows1[0]!["c"]!.GetValue<long>() : 0;
        var count2 = countRows2.Count > 0 ? countRows2[0]!["c"]!.GetValue<long>() : 0;

        var sampleLimit = Math.Clamp(limit ?? 100, 1, 500);
        var orderColumn = columns[0];
        var select = $"SELECT {string.Join(", ", columns)} FROM {table} ORDER BY {orderColumn} LIMIT @lim";
        var (sample1, truncated1) = QueryRowsBounded(canonicalPath, select,
            new JsonObject { ["lim"] = sampleLimit }, sampleLimit);
        var (sample2, truncated2) = QueryRowsBounded(otherPath, select,
            new JsonObject { ["lim"] = sampleLimit }, sampleLimit);
        var sampleIdentical = sample1.ToJsonString() == sample2.ToJsonString();
        var truncated = truncated1 || truncated2;

        return Result("compare_source_data", new JsonObject
        {
            ["table"] = table,
            ["supported"] = true,
            ["canonical_row_count"] = count1,
            ["other_row_count"] = count2,
            ["row_counts_match"] = count1 == count2,
            ["sample_identical"] = sampleIdentical,
            ["sample_rows"] = sampleLimit,
            ["sample_truncated"] = truncated,
            ["note"] = "row counts and an ordered sample comparison only; not a full diff",
        }, sourceId: dbId, path: otherPath, version: null, truncated: truncated);
    }

    // ------------------------------------------------------------- lookup_row

    public JsonObject LookupRow(string dbId, string table, long id)
    {
        if (!IsValidIdentifier(table))
            return ErrorResult("lookup_row", $"invalid table name: {table}");

        string dbPath;
        try
        {
            dbPath = ResolveDb(dbId);
        }
        catch (ArgumentException ex)
        {
            return ErrorResult("lookup_row", ex.Message);
        }

        return RunDomain("lookup_row", () =>
        {
            if (!TableExists(dbPath, table))
            {
                return Result("lookup_row", new JsonObject
                {
                    ["table"] = table,
                    ["supported"] = false,
                    ["reason"] = $"table '{table}' is not present in this database",
                }, sourceId: dbId, path: dbPath, version: dbId == "compact.sqlite3" ? _catalog.DbVersion : null);
            }

            var columns = SelectColumns(dbPath, table, MaxColumns);
            if (columns.Count == 0)
                return ErrorResult("lookup_row", $"table '{table}' has no columns");
            if (!columns.Contains("id"))
                return ErrorResult("lookup_row", $"table '{table}' has no id column");

            var sql = $"SELECT {string.Join(", ", columns)} FROM {table} WHERE id = @id LIMIT @lim";
            var (rows, truncated) = QueryRowsBounded(dbPath, sql,
                new JsonObject { ["id"] = id, ["lim"] = 1 }, 1);

            return Result("lookup_row", new JsonObject
            {
                ["table"] = table,
                ["id"] = id,
                ["supported"] = true,
                ["columns"] = new JsonArray(columns.Select(c => JsonValue.Create(c)).ToArray()),
                ["rows"] = rows,
                ["row_count"] = rows.Count,
                ["truncated"] = truncated,
            }, sourceId: dbId, path: dbPath, version: dbId == "compact.sqlite3" ? _catalog.DbVersion : null, truncated: truncated);
        });
    }

    // ------------------------------------------------------------- helpers

    private JsonObject TraceEntity(string tool, string table, string nameColumn, int? id, string? name, int? limit)
    {
        var dbPath = _catalog.DbPath;
        if (!TableExists(dbPath, table))
        {
            return Result(tool, new JsonObject
            {
                ["table"] = table,
                ["supported"] = false,
                ["reason"] = $"table '{table}' is not present in this database",
            }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion);
        }

        if (id is null && string.IsNullOrWhiteSpace(name))
            return ErrorResult(tool, "id or name is required");

        var columns = SelectColumns(dbPath, table, 8);
        if (columns.Count == 0)
            return ErrorResult(tool, $"table '{table}' has no columns");
        if (id.HasValue && !columns.Contains("id"))
            return ErrorResult(tool, $"table '{table}' has no id column");
        if (!string.IsNullOrWhiteSpace(name) && !columns.Contains(nameColumn))
            return ErrorResult(tool, $"table '{table}' has no {nameColumn} column");

        var conditions = new List<string>();
        var parameters = new JsonObject();
        if (id.HasValue)
        {
            conditions.Add("id = @id");
            parameters["id"] = id.Value;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            conditions.Add($"{nameColumn} LIKE @pattern ESCAPE '\\'");
            parameters["pattern"] = "%" + EscapeLike(name) + "%";
        }

        var resultLimit = Math.Clamp(limit ?? 20, 1, 100);
        var sql = $"SELECT {string.Join(", ", columns)} FROM {table} " +
                  $"WHERE {string.Join(" AND ", conditions)} ORDER BY {columns[0]} LIMIT @lim";
        parameters["lim"] = resultLimit;
        var (rows, truncated) = QueryRowsBounded(dbPath, sql, parameters, resultLimit);

        return Result(tool, new JsonObject
        {
            ["table"] = table,
            ["supported"] = true,
            ["columns"] = new JsonArray(columns.Select(c => JsonValue.Create(c)).ToArray()),
            ["rows"] = rows,
            ["row_count"] = rows.Count,
            ["evidence"] = id.HasValue ? "exact" : "textual",
            ["truncated"] = truncated,
            ["limit"] = resultLimit,
        }, sourceId: "compact.sqlite3", path: dbPath, version: _catalog.DbVersion, truncated: truncated);
    }

    private (JsonArray Rows, bool Truncated) QueryRowsBounded(string dbPath, string sql, JsonObject? parameters, int limit)
    {
        var rows = new JsonArray();
        var truncated = false;
        var started = DateTimeOffset.UtcNow;
        var timeoutSeconds = QueryTimeoutSecondsOverride ?? QueryTimeoutSeconds;

        using var connection = OpenReadOnly(dbPath);
        // Same native wall-clock deadline as query_sql: the progress handler
        // interrupts the VM once the deadline passes. The delegate is a
        // per-query local (closure over the local deadline) so overlapping
        // requests never share mutable query state; the local roots the
        // delegate for the connection's lifetime.
        var deadline = started.AddSeconds(timeoutSeconds);
        var progress = new delegate_progress(_ => DateTimeOffset.UtcNow >= deadline ? 1 : 0);
        raw.sqlite3_progress_handler(connection.Handle, 1000, progress, null);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            BindParameters(command, parameters);

            using var reader = command.ExecuteReader();
            var columnCount = reader.FieldCount;
            if (columnCount > MaxColumns)
                throw new DomainQueryException($"query returns {columnCount} columns; maximum is {MaxColumns}");

            var columnNames = new string[columnCount];
            for (var i = 0; i < columnCount; i++)
                columnNames[i] = reader.GetName(i);

            while (reader.Read())
            {
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                var row = new JsonObject();
                for (var i = 0; i < columnCount; i++)
                    row[columnNames[i]] = ReadValue(reader, i);
                rows.Add(row);
            }

            return (rows, truncated);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }
    }

    private bool TableExists(string dbPath, string table)
    {
        using var connection = OpenReadOnly(dbPath);
        return TableExists(connection, table);
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = @name";
        command.Parameters.AddWithValue("@name", table);
        return command.ExecuteScalar() is not null;
    }

    private static List<string> ListTableNames(SqliteConnection connection)
    {
        var tables = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static List<ColumnInfo> TableColumns(SqliteConnection connection, string table)
    {
        var columns = new List<ColumnInfo>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(new ColumnInfo(reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2), reader.GetInt32(5) != 0));
        return columns;
    }

    private static List<string> SelectColumns(string dbPath, string table, int maxCount)
    {
        using var connection = OpenReadOnly(dbPath);
        return TableColumns(connection, table).Take(maxCount).Select(c => c.Name).ToList();
    }

    private static List<ForeignKey> ForeignKeys(SqliteConnection connection, string table)
    {
        var foreignKeys = new List<ForeignKey>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            foreignKeys.Add(new ForeignKey(reader.GetString(3), reader.GetString(2), reader.GetString(4)));
        return foreignKeys;
    }

    private static string? FindIdTable(SqliteConnection connection, string prefix)
    {
        foreach (var candidate in new[] { prefix, prefix + "s", prefix + "es" })
        {
            if (TableExists(connection, candidate) && KeyColumn(connection, candidate) is not null)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The key column used to address rows of a table: the declared primary
    /// key column when exactly one exists, else the conventional "id" column
    /// when present, else null (the table cannot be addressed by a single
    /// value and trace edges through it are skipped, never crashed on).
    /// </summary>
    private static string? KeyColumn(SqliteConnection connection, string table)
    {
        var columns = TableColumns(connection, table);
        var pkColumns = columns.Where(c => c.IsPk).ToList();
        if (pkColumns.Count == 1)
            return pkColumns[0].Name;
        return columns.Any(c => c.Name == "id") ? "id" : null;
    }

    private long? QueryScalarLong(SqliteConnection connection, string sql, object? value)
    {
        // Same native wall-clock deadline as QueryRowsBounded/query_sql: the
        // progress handler interrupts the VM once the deadline passes. The
        // delegate is a per-call local (closure over the local deadline) so
        // overlapping requests never share mutable query state; the local
        // roots the delegate for the connection's lifetime. SQLITE_INTERRUPT
        // surfaces as SqliteException code 9 and is mapped to a deterministic
        // timeout error by RunDomain.
        var started = DateTimeOffset.UtcNow;
        var timeoutSeconds = QueryTimeoutSecondsOverride ?? QueryTimeoutSeconds;
        var deadline = started.AddSeconds(timeoutSeconds);
        var progress = new delegate_progress(_ => DateTimeOffset.UtcNow >= deadline ? 1 : 0);
        raw.sqlite3_progress_handler(connection.Handle, 1000, progress, null);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@v", value ?? DBNull.Value);
            var result = command.ExecuteScalar();
            return result is null || result is DBNull ? null : Convert.ToInt64(result);
        }
        finally
        {
            raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
        }
    }

    private static bool IsTextType(string type)
        => type.Contains("TEXT", StringComparison.OrdinalIgnoreCase)
           || type.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
           || type.Contains("CLOB", StringComparison.OrdinalIgnoreCase);

    private static string EscapeLike(string term)
        => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Exact (case-insensitive) match preferred; falls back to whole-word
    /// containment so numeric/name filters never overmatch via raw
    /// substring (e.g. "3554" must not match "13554"). A word is any run
    /// of alphanumeric characters; any other character (space, underscore,
    /// period, …) is a boundary. A name also matches when it starts with
    /// the filter followed by a boundary (e.g. "arche_mall" matches
    /// "arche_mall_world").
    /// </summary>
    private static bool MatchesNameFilter(string candidate, string nameLower)
    {
        var candidateLower = candidate.ToLowerInvariant();
        if (candidateLower == nameLower)
            return true;

        var token = new StringBuilder();
        foreach (var c in candidateLower)
        {
            if (char.IsLetterOrDigit(c))
            {
                token.Append(c);
                continue;
            }

            if (token.Length > 0)
            {
                if (token.ToString() == nameLower)
                    return true;
                token.Clear();
            }
        }

        if (token.Length > 0 && token.ToString() == nameLower)
            return true;

        // Prefix + boundary: the name starts with the filter followed by a
        // non-alphanumeric character (e.g. "arche_mall" → "arche_mall_world").
        return candidateLower.StartsWith(nameLower, StringComparison.Ordinal)
               && candidateLower.Length > nameLower.Length
               && !char.IsLetterOrDigit(candidateLower[nameLower.Length]);
    }

    private static string SnakeCase(string pascal)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a JSON document tolerantly: strips "//" line comments that
    /// appear outside string literals (the canonical world_spawns.json
    /// contains such annotations) before delegating to JsonNode.Parse.
    /// Returns null when the document is not valid JSON even after
    /// comment stripping.
    /// </summary>
    private static JsonNode? ParseJsonTolerant(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                builder.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                builder.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // Skip to end of line.
                while (i < text.Length && text[i] != '\n')
                    i++;
                builder.Append('\n');
                continue;
            }

            builder.Append(c);
        }

        try
        {
            return JsonNode.Parse(builder.ToString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private (JsonArray Matches, int FilesScanned, bool Truncated) SearchFilesForTerm(string term, int limit)
    {
        var matches = new JsonArray();
        var filesScanned = 0;
        var truncated = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (limit <= 0)
            return (matches, 0, false);

        foreach (var rootId in _catalog.RootIds)
        {
            var root = _catalog.ResolveRoot(rootId);
            if (root is null || !Directory.Exists(root))
                continue;

            foreach (var file in EnumerateFilesSafe(root))
            {
                if (filesScanned >= MaxFileScan || matches.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                filesScanned++;
                if (IsBuildOutputPath(file) || !_catalog.IsAllowed(file))
                    continue;
                var real = ResolveRealPath(file);
                if (real is null || !_catalog.IsAllowed(real))
                    continue;
                // The canonical DB (and any *.sqlite3 copy) is searched via
                // db_hits, not as a raw file; overlapping roots must not
                // double-report the same file.
                if (file.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    || !seen.Add(real))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxReadBytes)
                        continue;

                    var lineNumber = 0;
                    using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    while (reader.ReadLine() is { } line)
                    {
                        lineNumber++;
                        if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(new JsonObject
                            {
                                ["path"] = file,
                                ["line"] = lineNumber,
                                ["text"] = line.Length > 200 ? line[..200] + "…" : line,
                            });
                            if (matches.Count >= limit)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    // Skip unreadable/binary files; search is best-effort.
                }
            }

            if (matches.Count >= limit)
            {
                truncated = true;
                break;
            }
        }

        return (matches, filesScanned, truncated);
    }

    /// <summary>
    /// Bounded, symlink-safe recursive file enumeration: never descends into
    /// a directory that is a symlink (or whose real path escapes the root),
    /// so a symlinked subdirectory cannot pull the scan outside the requested
    /// root. Files are yielded in ordinal order for determinism.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var files = new List<string>();

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable directory — skip
            }

            foreach (var entry in entries.OrderBy(e => e, StringComparer.Ordinal))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue; // symlink/junction — never follow

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var real = ResolveRealPath(entry);
                        if (real is null || !IsWithinRoot(root, real))
                            continue; // escaping directory — skip
                        pending.Push(entry);
                    }
                    else
                    {
                        files.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Entry vanished or unreadable — skip.
                }
            }
        }

        return files.OrderBy(f => f, StringComparer.Ordinal);
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var pathFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return pathFull.Equals(rootFull, StringComparison.Ordinal)
            || pathFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
