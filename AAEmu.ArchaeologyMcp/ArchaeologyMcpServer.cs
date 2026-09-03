using System.Text.Json;
using System.Text.Json.Nodes;

namespace AAEmu.ArchaeologyMcp;

/// <summary>
/// MCP stdio server exposing the read-only archaeology service as MCP tools.
/// Speaks newline-delimited JSON-RPC 2.0 (the MCP stdio transport), matching
/// the AAEmu.BotControl / AAEmu.BotControlMcp convention. Tools: the raw
/// read-only surface (list_sources, list_databases, list_tables,
/// describe_table, query_sql, read_file, search_files) plus domain helpers
/// (search_everything, trace_references, find_quest_objectives, trace_*,
/// search_physics, compare_source_data) and the AAPak archive surface
/// (list_pak_entries, read_pak_entry) — all read-only, all returning the
/// standard text-content envelope with deterministic provenance metadata.
/// </summary>
public sealed class ArchaeologyMcpServer
{
    public const string ProtocolVersion = "2025-03-26";
    public const string ServerName = "aaemu-archaeology";
    public const string ServerVersion = "1.1.0";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ArchaeologyService _service;
    private readonly PakArchiveService _pakService;

    public ArchaeologyMcpServer(ArchaeologyService service, PakArchiveService pakService)
    {
        _service = service;
        _pakService = pakService;
    }

    /// <summary>
    /// Handles one newline-delimited JSON-RPC line. Returns the response line
    /// to write back, or null for notifications (no response).
    /// </summary>
    public async Task<string?> HandleAsync(string jsonLine)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(jsonLine);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        var id = message?["id"]?.DeepClone();
        var method = message?["method"]?.GetValue<string>();
        if (id is null)
            return null; // notification (e.g. notifications/initialized) — never respond

        try
        {
            return method switch
            {
                "initialize" => Response(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                    },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
                }),
                "ping" => Response(id, new JsonObject()),
                "tools/list" => Response(id, ToolsList()),
                "tools/call" => await CallToolAsync(id, message?["params"]),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ tools

    private async Task<string> CallToolAsync(JsonNode? id, JsonNode? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
        var arguments = parameters?["arguments"] as JsonObject ?? [];

        JsonObject result;
        try
        {
            result = name switch
            {
                "list_sources" => _service.ListSources(),
                "list_databases" => _service.ListDatabases(),
                "list_tables" => _service.ListTables(Arg(arguments, "db_id", "compact.sqlite3")),
                "describe_table" => _service.DescribeTable(Arg(arguments, "db_id", "compact.sqlite3"), Arg(arguments, "table")),
                "query_sql" => _service.QuerySql(
                    Arg(arguments, "db_id", "compact.sqlite3"),
                    Arg(arguments, "sql"),
                    arguments["parameters"] as JsonObject,
                    arguments["limit"]?.GetValue<int>()),
                "read_file" => _service.ReadFile(
                    Arg(arguments, "path"),
                    arguments["offset"]?.GetValue<int>(),
                    arguments["limit"]?.GetValue<int>()),
                "search_files" => _service.SearchFiles(
                    Arg(arguments, "pattern"),
                    arguments["root"]?.GetValue<string>(),
                    arguments["glob"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "list_pak_entries" => _pakService.ListEntries(
                    arguments["pattern"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "read_pak_entry" => _pakService.ReadEntry(
                    Arg(arguments, "name"),
                    arguments["max_bytes"]?.GetValue<int>()),
                "lookup_row" => _service.LookupRow(
                    Arg(arguments, "db_id", "compact.sqlite3"),
                    Arg(arguments, "table"),
                    arguments["id"]?.GetValue<long>() ?? 0),
                "search_everything" => _service.SearchEverything(
                    Arg(arguments, "term"),
                    arguments["limit"]?.GetValue<int>()),
                "trace_references" => _service.TraceReferences(
                    Arg(arguments, "identifier"),
                    arguments["domain"]?.GetValue<string>(),
                    arguments["table"]?.GetValue<string>(),
                    arguments["depth"]?.GetValue<int>(),
                    arguments["limit"]?.GetValue<int>()),
                "find_quest_objectives" => _service.FindQuestObjectives(
                    arguments["quest_id"]?.GetValue<int>(),
                    arguments["objective_id"]?.GetValue<int>(),
                    arguments["family"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_skill" => _service.TraceSkill(
                    arguments["id"]?.GetValue<int>(),
                    arguments["name"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_item" => _service.TraceItem(
                    arguments["id"]?.GetValue<int>(),
                    arguments["name"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_quest" => _service.TraceQuest(
                    arguments["id"]?.GetValue<int>(),
                    arguments["name"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_npc" => _service.TraceNpc(
                    arguments["id"]?.GetValue<int>(),
                    arguments["name"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_doodad" => _service.TraceDoodad(
                    arguments["id"]?.GetValue<int>(),
                    arguments["name"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_mate" => _service.TraceMate(
                    arguments["id"]?.GetValue<int>(),
                    arguments["item_id"]?.GetValue<int>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_vehicle" => _service.TraceVehicle(
                    arguments["id"]?.GetValue<int>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_crafting" => _service.TraceCrafting(
                    arguments["id"]?.GetValue<int>(),
                    arguments["title"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "trace_world_spawn" => _service.TraceWorldSpawn(
                    arguments["name"]?.GetValue<string>(),
                    arguments["zone_id"]?.GetValue<int>(),
                    arguments["limit"]?.GetValue<int>()),
                "search_physics" => _service.SearchPhysics(
                    arguments["term"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                "compare_source_data" => _service.CompareSourceData(
                    Arg(arguments, "table"),
                    arguments["db_id"]?.GetValue<string>(),
                    arguments["limit"]?.GetValue<int>()),
                _ => throw new InvalidOperationException($"Unknown tool: {name}"),
            };
        }
        catch (ArgumentException ex)
        {
            result = new JsonObject
            {
                ["ok"] = false,
                ["error"] = ex.Message,
            };
        }

        var text = result.ToJsonString();
        var response = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            }),
            ["isError"] = result["ok"]?.GetValue<bool>() == false,
        };
        return Response(id, response);
    }

    private static JsonObject ToolsList()
        => new()
        {
            ["tools"] = new JsonArray(
                Tool("list_sources",
                    "List all allowlisted read-only data sources with metadata (source_id, source_type, path, logical_domain, version, encoding, size, searchable, notes).",
                    EmptyObjectSchema()),
                Tool("list_databases",
                    "List available SQLite databases (canonical compact.sqlite3 plus any *.sqlite3 copies in the data root).",
                    EmptyObjectSchema()),
                Tool("list_tables",
                    "List tables/views in a database (default compact.sqlite3).",
                    ObjectSchema(("db_id", false, "string"))),
                Tool("describe_table",
                    "Describe a table's columns (name, type, notnull, pk).",
                    ObjectSchema(("db_id", false, "string"), ("table", true, "string"))),
                Tool("query_sql",
                    "Run a read-only SQL query (SELECT/WITH/EXPLAIN/schema PRAGMA only; parameterized; bounded rows/columns/timeout).",
                    ObjectSchema(("db_id", false, "string"), ("sql", true, "string"),
                        ("parameters", false, "object"), ("limit", false, "number"))),
                Tool("read_file",
                    "Read a text file from an allowlisted root (bounded to 1 MiB; optional byte offset/limit).",
                    ObjectSchema(("path", true, "string"), ("offset", false, "number"), ("limit", false, "number"))),
                Tool("search_files",
                    "Regex-search files under an allowlisted root (bounded results; optional glob filter).",
                    ObjectSchema(("pattern", true, "string"), ("root", false, "string"),
                        ("glob", false, "string"), ("limit", false, "number"))),
                Tool("list_pak_entries",
                    "List AAPak (game_pak) entry metadata (name, size, offset, md5, timestamps) matching a regex, bounded to a result cap (default 5000). Only the file table is read; no file contents are streamed. Requires ARCHEAGE_PAK_PATH.",
                    ObjectSchema(("pattern", false, "string"), ("limit", false, "number"))),
                Tool("read_pak_entry",
                    "Read one named AAPak (game_pak) entry, bounded to max_bytes (default 1 MiB); returns metadata plus base64 content. Rejects missing entries and traversal/absolute/backslash names. Requires ARCHEAGE_PAK_PATH.",
                    ObjectSchema(("name", true, "string"), ("max_bytes", false, "number"))),
                Tool("lookup_row",
                    "Fetch one row by primary key id from a table (default compact.sqlite3); table/columns validated via introspection, id parameterized, bounded to one row.",
                    ObjectSchema(("db_id", false, "string"), ("table", true, "string"), ("id", true, "number"))),
                Tool("search_everything",
                    "Search every text-bearing column of real tables plus allowlisted source files for a term (bounded; per-hit table/column/id provenance).",
                    ObjectSchema(("term", true, "string"), ("limit", false, "number"))),
                Tool("trace_references",
                    "Bounded reference trace of an identifier across tables (declared FKs = exact, name-convention = heuristic, value matches = textual) plus source-file matches.",
                    ObjectSchema(("identifier", true, "string"), ("domain", false, "string"),
                        ("table", false, "string"), ("depth", false, "number"), ("limit", false, "number"))),
                Tool("find_quest_objectives",
                    "Discover quest objective rows across quest_act_obj_* families, joined to quest_acts/quest_components/quest_contexts (optional quest_id/objective_id/family filters).",
                    ObjectSchema(("quest_id", false, "number"), ("objective_id", false, "number"),
                        ("family", false, "string"), ("limit", false, "number"))),
                Tool("trace_skill",
                    "Look up skills by id or name (exact id / textual name match).",
                    ObjectSchema(("id", false, "number"), ("name", false, "string"), ("limit", false, "number"))),
                Tool("trace_item",
                    "Look up items by id or name (exact id / textual name match).",
                    ObjectSchema(("id", false, "number"), ("name", false, "string"), ("limit", false, "number"))),
                Tool("trace_quest",
                    "Look up quest_contexts by id or name, with linked quest_components and act counts.",
                    ObjectSchema(("id", false, "number"), ("name", false, "string"), ("limit", false, "number"))),
                Tool("trace_npc",
                    "Look up npcs by id or name (exact id / textual name match).",
                    ObjectSchema(("id", false, "number"), ("name", false, "string"), ("limit", false, "number"))),
                Tool("trace_doodad",
                    "Look up doodad_almighties by id or name (exact id / textual name match).",
                    ObjectSchema(("id", false, "number"), ("name", false, "string"), ("limit", false, "number"))),
                Tool("trace_mate",
                    "Look up item_summon_mates by id or item_id, joined to npc names.",
                    ObjectSchema(("id", false, "number"), ("item_id", false, "number"), ("limit", false, "number"))),
                Tool("trace_vehicle",
                    "Look up vehicle_models by id (no name column exists; model columns are returned).",
                    ObjectSchema(("id", false, "number"), ("limit", false, "number"))),
                Tool("trace_crafting",
                    "Look up crafts by id or title (exact id / textual title match).",
                    ObjectSchema(("id", false, "number"), ("title", false, "string"), ("limit", false, "number"))),
                Tool("trace_world_spawn",
                    "World spawns from Data/Worlds/world_spawns.json (by name/zone) plus npc_spawners rows by name.",
                    ObjectSchema(("name", false, "string"), ("zone_id", false, "number"), ("limit", false, "number"))),
                Tool("search_physics",
                    "Search physical_* tables (enchant abilities, explosion effects). No collision/geometry tables exist in this DB.",
                    ObjectSchema(("term", false, "string"), ("limit", false, "number"))),
                Tool("compare_source_data",
                    "Compare a table's row counts and an ordered sample between the canonical DB and a file:<name> copy.",
                    ObjectSchema(("table", true, "string"), ("db_id", false, "string"), ("limit", false, "number")))),
        };

    private static JsonObject Tool(string name, string description, JsonObject inputSchema)
        => new() { ["name"] = name, ["description"] = description, ["inputSchema"] = inputSchema };

    private static JsonObject EmptyObjectSchema() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    private static JsonObject ObjectSchema(params (string Name, bool Required, string Type)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, isRequired, type) in properties)
        {
            props[name] = new JsonObject { ["type"] = type };
            if (isRequired)
                required.Add(name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static string Arg(JsonObject arguments, string name, string defaultValue = "")
        => arguments.TryGetPropertyValue(name, out var value) && value is not null
            ? value.GetValue<string>() ?? defaultValue
            : defaultValue;

    // ------------------------------------------------------------ framing

    private static string Response(JsonNode? id, JsonNode result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
