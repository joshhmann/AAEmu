using System.Text.Json;
using System.Text.Json.Nodes;

using AAEmu.BotControl;

namespace AAEmu.BotControlMcp;

/// <summary>
/// MCP stdio sidecar (M5 stage 4, t_446228b5) exposing the game's
/// CONTRACT-ACTION API (/api/actors/*) as MCP tools — the AGENT tier
/// frontend for bot control. Tools map 1:1 to the M5 contract actions
/// (observe/move/interact/accept_quest/turn_in/loot/use_item/mount/trace
/// plus the B1 surface: move_to_unit/stop/target/cast/dismount/advance_quest/
/// turn_in_doodad/auto_turn_in/interrupt + lifecycle poll).
///
/// This sidecar is a SEPARATE PROCESS: it only speaks HTTP to the game's
/// WebApi (enqueue-only path). No engine internals, no game-process code.
/// A crashed MCP client can never wedge the world — the sidecar's request
/// goes through the API's enqueue-only queue, and the pending action
/// completes or times out server-side per its lifecycle.
///
/// The P1 management surface (bot_add/remove/list/relocate/status, t_2ea94a20)
/// lives in AAEmu.BotControl and is deliberately NOT duplicated here.
/// </summary>
public sealed class ActionMcpServer
{
    public const string ProtocolVersion = "2025-03-26";
    public const string ServerName = "aaemu-bot-actions";
    public const string ServerVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IBotControlClient _client;

    public ActionMcpServer(IBotControlClient client) => _client = client;

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
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        var response = name switch
        {
            // ---- M5 contract actions (t_659f891f / IGameplayActor) ----
            "observe" => await _client.PostAsync("/api/actors/observe", Body(arguments, "bot")),
            "move" => await _client.PostAsync("/api/actors/move",
                Body(arguments, "bot", "x", "y", "z", "speed", "timeoutSec", "idempotencyKey")),
            "interact" => await _client.PostAsync("/api/actors/interact",
                Body(arguments, "bot", "doodadObjId", "skillId", "idempotencyKey")),
            "accept_quest" => await _client.PostAsync("/api/actors/accept_quest",
                Body(arguments, "bot", "questId", "acceptorType", "acceptorId", "idempotencyKey")),
            "turn_in_quest" => await _client.PostAsync("/api/actors/turn_in_quest",
                Body(arguments, "bot", "questId", "npcObjId", "selectedReward", "idempotencyKey")),
            "loot" => await _client.PostAsync("/api/actors/loot",
                Body(arguments, "bot", "lootOwnerObjId", "idempotencyKey")),
            "use_item" => await _client.PostAsync("/api/actors/use_item",
                Body(arguments, "bot", "itemTemplateId", "targetObjId", "idempotencyKey")),
            "mount" => await _client.PostAsync("/api/actors/mount",
                Body(arguments, "bot", "mateObjId", "idempotencyKey")),
            // ---- B1 surface (feat/bot-actor-surface-b1) ----
            "move_to_unit" => await _client.PostAsync("/api/actors/move_to_unit",
                Body(arguments, "bot", "targetObjId", "speed", "timeoutSec", "idempotencyKey")),
            "stop" => await _client.PostAsync("/api/actors/stop", Body(arguments, "bot")),
            "target" => await _client.PostAsync("/api/actors/target",
                Body(arguments, "bot", "targetObjId")),
            "cast" => await _client.PostAsync("/api/actors/cast",
                Body(arguments, "bot", "skillId", "targetObjId", "idempotencyKey")),
            "dismount" => await _client.PostAsync("/api/actors/dismount",
                Body(arguments, "bot", "mateObjId", "idempotencyKey")),
            "advance_quest" => await _client.PostAsync("/api/actors/advance_quest",
                Body(arguments, "bot", "questId", "idempotencyKey")),
            "turn_in_doodad" => await _client.PostAsync("/api/actors/turn_in_doodad",
                Body(arguments, "bot", "questId", "doodadObjId", "selectedReward", "idempotencyKey")),
            "auto_turn_in" => await _client.PostAsync("/api/actors/auto_turn_in",
                Body(arguments, "bot", "questId", "selectedReward", "idempotencyKey")),
            "interrupt" => await _client.PostAsync("/api/actors/interrupt",
                Body(arguments, "bot", "traceId")),
            // ---- lifecycle / audit reads ----
            "action_status" => await _client.GetAsync($"/api/actors/actions/{Arg(arguments, "traceId")}"),
            "trace" => await _client.GetAsync($"/api/actors/trace?bot={Uri.EscapeDataString(Arg(arguments, "bot"))}"
                + (arguments.ContainsKey("limit") ? $"&limit={arguments["limit"]}" : string.Empty)),
            _ => throw new InvalidOperationException($"Unknown tool: {name}"),
        };

        var text = response.Body.Length == 0 ? $"(HTTP {response.Status})" : response.Body;
        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            }),
            ["isError"] = response.Status is < 200 or >= 300,
        };
        return Response(id, result);
    }

    private static JsonObject ToolsList()
        => new()
        {
            ["tools"] = new JsonArray(
                Tool("observe",
                    "Observation snapshot of a registered bot (position, targets, nearby entities). POST /api/actors/observe.",
                    ObjectSchema(("bot", true, "string"))),
                Tool("move",
                    "Walk a bot to an absolute position (bounded, terrain-aware). POST /api/actors/move.",
                    ObjectSchema(("bot", true, "string"), ("x", true, "number"), ("y", true, "number"), ("z", true, "number"),
                        ("speed", false, "number"), ("timeoutSec", false, "number"), ("idempotencyKey", false, "string"))),
                Tool("interact",
                    "Interact with a doodad (skillId 0 = skill-less branch). POST /api/actors/interact.",
                    ObjectSchema(("bot", true, "string"), ("doodadObjId", true, "number"),
                        ("skillId", false, "number"), ("idempotencyKey", false, "string"))),
                Tool("accept_quest",
                    "Accept a quest through the real AddQuest gate. POST /api/actors/accept_quest.",
                    ObjectSchema(("bot", true, "string"), ("questId", true, "number"), ("acceptorType", true, "string"),
                        ("acceptorId", true, "number"), ("idempotencyKey", false, "string"))),
                Tool("turn_in_quest",
                    "Turn in a quest at an NPC. POST /api/actors/turn_in_quest.",
                    ObjectSchema(("bot", true, "string"), ("questId", true, "number"), ("npcObjId", false, "number"),
                        ("selectedReward", false, "number"), ("idempotencyKey", false, "string"))),
                Tool("loot",
                    "Loot a corpse/bag owner (loot-all). POST /api/actors/loot.",
                    ObjectSchema(("bot", true, "string"), ("lootOwnerObjId", true, "number"),
                        ("idempotencyKey", false, "string"))),
                Tool("use_item",
                    "Use an inventory item (targetObjId 0 = self). POST /api/actors/use_item.",
                    ObjectSchema(("bot", true, "string"), ("itemTemplateId", true, "number"),
                        ("targetObjId", false, "number"), ("idempotencyKey", false, "string"))),
                Tool("mount",
                    "Mount an owned mate. POST /api/actors/mount.",
                    ObjectSchema(("bot", true, "string"), ("mateObjId", true, "number"),
                        ("idempotencyKey", false, "string"))),
                Tool("move_to_unit",
                    "Walk to a unit's current position. POST /api/actors/move_to_unit.",
                    ObjectSchema(("bot", true, "string"), ("targetObjId", true, "number"),
                        ("speed", false, "number"), ("timeoutSec", false, "number"), ("idempotencyKey", false, "string"))),
                Tool("stop",
                    "Stop the bot's running request (no-op when idle). POST /api/actors/stop.",
                    ObjectSchema(("bot", true, "string"))),
                Tool("target",
                    "Set the bot's current target. POST /api/actors/target.",
                    ObjectSchema(("bot", true, "string"), ("targetObjId", true, "number"))),
                Tool("cast",
                    "Cast a known skill at a unit. POST /api/actors/cast.",
                    ObjectSchema(("bot", true, "string"), ("skillId", true, "number"), ("targetObjId", true, "number"),
                        ("idempotencyKey", false, "string"))),
                Tool("dismount",
                    "Dismount (mateObjId 0 = current mount). POST /api/actors/dismount.",
                    ObjectSchema(("bot", true, "string"), ("mateObjId", false, "number"),
                        ("idempotencyKey", false, "string"))),
                Tool("advance_quest",
                    "One step-machine advance on an active quest. POST /api/actors/advance_quest.",
                    ObjectSchema(("bot", true, "string"), ("questId", true, "number"),
                        ("idempotencyKey", false, "string"))),
                Tool("turn_in_doodad",
                    "Turn in a quest at a doodad. POST /api/actors/turn_in_doodad.",
                    ObjectSchema(("bot", true, "string"), ("questId", true, "number"), ("doodadObjId", true, "number"),
                        ("selectedReward", false, "number"), ("idempotencyKey", false, "string"))),
                Tool("auto_turn_in",
                    "Auto-complete turn-in (no world target). POST /api/actors/auto_turn_in.",
                    ObjectSchema(("bot", true, "string"), ("questId", true, "number"),
                        ("selectedReward", false, "number"), ("idempotencyKey", false, "string"))),
                Tool("interrupt",
                    "Cancel a running request by its API trace id. POST /api/actors/interrupt.",
                    ObjectSchema(("bot", true, "string"), ("traceId", true, "string"))),
                Tool("action_status",
                    "Poll one action's lifecycle by trace id (GET /api/actors/actions/{traceId}) — the async response channel for every enqueued action.",
                    ObjectSchema(("traceId", true, "string"))),
                Tool("trace",
                    "Per-bot audit trail, newest first (GET /api/actors/trace?bot=..&limit=..).",
                    ObjectSchema(("bot", true, "string"), ("limit", false, "number")))),
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

    // ------------------------------------------------------------- body maps

    /// <summary>Builds the lowercase wire-shape body from the allowed argument names.</summary>
    private static string Body(JsonObject arguments, params string[] allowed)
    {
        var body = new JsonObject();
        foreach (var name in allowed)
        {
            if (arguments.TryGetPropertyValue(name, out var value) && value is not null)
                body[name] = value.DeepClone();
        }

        return body.ToJsonString();
    }

    private static string Arg(JsonObject arguments, string name)
        => arguments.TryGetPropertyValue(name, out var value) && value is not null
            ? value.GetValue<string>() ?? string.Empty
            : string.Empty;

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
