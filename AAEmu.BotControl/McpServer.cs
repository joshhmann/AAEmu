using System.Text.Json;
using System.Text.Json.Nodes;

namespace AAEmu.BotControl;

/// <summary>
/// Minimal MCP stdio server (P1 t_2ea94a20) exposing the game's bot control
/// API as MCP tools. Speaks newline-delimited JSON-RPC 2.0 (the MCP stdio
/// transport). Tools: bot_list, bot_add, bot_remove, bot_relocate,
/// bot_status — each maps 1:1 onto a WebApi control endpoint; every
/// mutation still executes inside the game process (single execution
/// boundary, no parallel bot path).
/// </summary>
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-03-26";
    public const string ServerName = "aaemu-bot-control";
    public const string ServerVersion = "1.0.0";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IBotControlClient _client;

    public McpServer(IBotControlClient client) => _client = client;

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
                    ["capabilities"] = new JsonObject(),
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
            "bot_list" => await _client.GetAsync("/api/bots"),
            "bot_status" => await _client.GetAsync("/api/bots/status"),
            "bot_add" => await _client.PostAsync("/api/bots", BuildAddBody(arguments)),
            "bot_remove" => await _client.PostAsync("/api/bots/remove", BuildRemoveBody(arguments)),
            "bot_relocate" => await _client.PostAsync("/api/bots/relocate", BuildRelocateBody(arguments)),
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
                Tool("bot_list",
                    "List all registered player bots (structured snapshot: name, id, state, fidelity, position).",
                    EmptyObjectSchema()),
                Tool("bot_status",
                    "Bot registry + embodied state summary (registered/active counts and the full snapshot).",
                    EmptyObjectSchema()),
                Tool("bot_add",
                    "Add/provision a player bot by name (idempotent adopt-or-create; optional spawn home x/y/z).",
                    ObjectSchema(("name", true, "string"), ("x", false, "number"), ("y", false, "number"), ("z", false, "number"))),
                Tool("bot_remove",
                    "Remove a player bot by name or numeric id (deactivates, leave-saves, drops the registry entry).",
                    ObjectSchema(("nameOrId", true, "string"))),
                Tool("bot_relocate",
                    "Relocate a player bot's patrol home to x/y/z (terrain-clamped, route re-armed).",
                    ObjectSchema(("nameOrId", true, "string"), ("x", true, "number"), ("y", true, "number"), ("z", true, "number")))),
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

    private sealed record AddArgs(string? Name, float? X, float? Y, float? Z);
    private sealed record RemoveArgs(string? NameOrId);
    private sealed record RelocateArgs(string? NameOrId, float? X, float? Y, float? Z);

    private static string BuildAddBody(JsonObject arguments)
    {
        var args = JsonSerializer.Deserialize<AddArgs>(arguments.ToJsonString(), JsonOpts) ?? new AddArgs(null, null, null, null);
        return JsonSerializer.Serialize(new { name = args.Name, x = args.X, y = args.Y, z = args.Z });
    }

    private static string BuildRemoveBody(JsonObject arguments)
    {
        var args = JsonSerializer.Deserialize<RemoveArgs>(arguments.ToJsonString(), JsonOpts) ?? new RemoveArgs(null);
        return JsonSerializer.Serialize(new { nameOrId = args.NameOrId });
    }

    private static string BuildRelocateBody(JsonObject arguments)
    {
        var args = JsonSerializer.Deserialize<RelocateArgs>(arguments.ToJsonString(), JsonOpts) ?? new RelocateArgs(null, null, null, null);
        return JsonSerializer.Serialize(new { nameOrId = args.NameOrId, x = args.X, y = args.Y, z = args.Z });
    }

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
