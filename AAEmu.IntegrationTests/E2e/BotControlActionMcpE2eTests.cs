using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// MCP sidecar live evidence (M5 stage 4, t_446228b5): a REAL game server
/// boots with the bot control flag enabled, a bot is provisioned through the
/// management surface, and the CONTRACT-ACTION sidecar (AAEmu.BotControlMcp —
/// a separate process) is driven end-to-end over MCP stdio as an AGENT would:
///
///   - handshake + tools/list (19 contract-action tools, no management tools)
///   - observe → enqueue → action_status poll → full lifecycle (Completed)
///   - move → poll → the bot actually moved (position delta)
///   - trace tool → per-bot audit trail
///   - crash isolation: SIGKILL the sidecar between enqueue and poll; the
///     pending action still completes server-side when a NEW sidecar polls —
///     a crashed MCP client cannot wedge the world
///   - no admin verbs on the action surface (bot_add etc. absent; management
///     stays on the P1 surface /api/bots)
///
/// Evidence is the REAL sidecar binary spawned as a subprocess speaking
/// newline-delimited JSON-RPC 2.0 on stdin/stdout — the same pipe a Hermes
/// profile (native MCP client) uses.
/// </summary>
[Collection("e2e")]
public class BotControlActionMcpE2eTests
{
    private const string BotName = "McpSidecar01";
    private const string Token = "e2e-bot-ctrl-token";

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";

    [Fact]
    [Trait("Category", "e2e")]
    public async Task BotControlMcp_Sidecar_LiveEvidence()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));

            // ------------------------------------------------ provisioning
            // Bot creation is the MANAGEMENT surface's job (t_2ea94a20) —
            // the action sidecar never adds/removes bots.
            using (var http = NewHttpClient())
            {
                var add = await PostJsonAsync(http, "/api/bots", $"{{\"name\":\"{BotName}\"}}");
                Assert.True(add.TryGetProperty("success", out var addOk) ? addOk.GetBoolean()
                    : (add.TryGetProperty("Success", out var addOkP) && addOkP.GetBoolean()),
                    "management add must succeed: " + add);
            }

            // ------------------------------------------------ handshake
            using var sidecar = SpawnSidecar();
            var initialize = await sidecar.RpcAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
            Assert.NotNull(initialize?["result"]?["serverInfo"]?["name"]);
            Assert.Equal(ActionMcpProtocol.ServerName, initialize!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            await sidecar.RpcNotifyAsync("notifications/initialized");

            // ------------------------------------------------ tools/list
            var toolsList = await sidecar.RpcAsync("tools/list");
            var tools = toolsList!["result"]!["tools"]!.AsArray();
            Assert.Equal(19, tools.Count);
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();
            Assert.Contains("observe", names);
            Assert.Contains("move", names);
            Assert.Contains("interact", names);
            Assert.Contains("accept_quest", names);
            Assert.Contains("turn_in_quest", names);
            Assert.Contains("loot", names);
            Assert.Contains("use_item", names);
            Assert.Contains("mount", names);
            Assert.Contains("trace", names);
            Assert.Contains("action_status", names);
            // B1 surface
            Assert.Contains("move_to_unit", names);
            Assert.Contains("stop", names);
            Assert.Contains("target", names);
            Assert.Contains("cast", names);
            Assert.Contains("dismount", names);
            Assert.Contains("advance_quest", names);
            Assert.Contains("turn_in_doodad", names);
            Assert.Contains("auto_turn_in", names);
            Assert.Contains("interrupt", names);
            // No management verbs (t_2ea94a20 surface stays in AAEmu.BotControl)
            Assert.DoesNotContain("bot_add", names);
            Assert.DoesNotContain("bot_remove", names);
            Assert.DoesNotContain("bot_list", names);
            Assert.DoesNotContain("bot_relocate", names);

            // ------------------------------------------------ observe
            var observeBody = await CallToolAsync(sidecar, "observe", new JsonObject { ["bot"] = BotName });
            Assert.True(observeBody["success"]?.GetValue<bool>() ?? false, "observe enqueue must succeed: " + observeBody);
            var observeTrace = observeBody["trace_id"]!.GetValue<string>();

            var observePoll = await PollSidecarAsync(sidecar, observeTrace, TimeSpan.FromSeconds(20));
            Assert.Equal("Completed", observePoll["state"]!.GetValue<string>());
            var observation = observePoll["result_payload"]!;
            Assert.True(observation is JsonObject && observation["Position"] is JsonObject,
                "observation must carry a position: " + observePoll);

            // ------------------------------------------------ move
            var startX = observation["Position"]!["X"]!.GetValue<double>();
            var startY = observation["Position"]!["Y"]!.GetValue<double>();
            var startZ = observation["Position"]!["Z"]!.GetValue<double>();
            var moveBody = await CallToolAsync(sidecar, "move", new JsonObject
            {
                ["bot"] = BotName,
                ["x"] = startX + 6,
                ["y"] = startY,
                ["z"] = startZ,
                ["speed"] = 2.0,
                ["timeoutSec"] = 20,
            });
            var moveTrace = moveBody["trace_id"]!.GetValue<string>();

            var movePoll = await PollSidecarAsync(sidecar, moveTrace, TimeSpan.FromSeconds(30));
            Assert.Equal("Completed", movePoll["state"]!.GetValue<string>());

            // The bot actually moved (position changed from the move).
            var afterBody = await CallToolAsync(sidecar, "observe", new JsonObject { ["bot"] = BotName });
            var afterPoll = await PollSidecarAsync(sidecar, afterBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(20));
            var movedX = afterPoll["result_payload"]!["Position"]!["X"]!.GetValue<double>();
            Assert.True(Math.Abs(movedX - (startX + 6)) <= 3.0,
                $"bot should have moved ~6 units in X (start {startX:F1} → {movedX:F1})");

            // ------------------------------------------------ trace tool
            var traceBody = await CallToolAsync(sidecar, "trace", new JsonObject { ["bot"] = BotName, ["limit"] = 20 });
            Assert.True(traceBody["trace"] is JsonArray { Count: >= 3 },
                "trace should contain at least the 3+ commands issued: " + traceBody);

            // ------------------------------------------------ crash isolation
            // Kill the sidecar between enqueue and poll. The pending action
            // must still complete server-side (enqueue-only path); a fresh
            // sidecar observes the outcome — the world was never wedged.
            string crashTrace;
            using (var doomed = SpawnSidecar())
            {
                await doomed.RpcAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
                var fireBody = await CallToolAsync(doomed, "stop", new JsonObject { ["bot"] = BotName });
                Assert.True(fireBody["success"]?.GetValue<bool>() ?? false, "stop enqueue must succeed");
                crashTrace = fireBody["trace_id"]!.GetValue<string>();
                // doomed disposed here — SIGKILL mid-request, before any poll.
            }

            using var fresh = SpawnSidecar();
            await fresh.RpcAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
            var crashPoll = await PollSidecarAsync(fresh, crashTrace, TimeSpan.FromSeconds(20));
            Assert.Equal("Completed", crashPoll["state"]!.GetValue<string>());

            // World continues: a fresh command on the fresh sidecar completes.
            var worldAliveBody = await CallToolAsync(fresh, "observe", new JsonObject { ["bot"] = BotName });
            var worldAlivePoll = await PollSidecarAsync(fresh, worldAliveBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(20));
            Assert.Equal("Completed", worldAlivePoll["state"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }

    // ------------------------------------------------------------ helpers

    private static readonly string SidecarDll = Path.Combine(AppContext.BaseDirectory, "AAEmu.BotControlMcp.dll");

    /// <summary>Asserts a tools/call result is not an error and returns the parsed API body.</summary>
    private static async Task<JsonNode> CallToolAsync(SidecarProcess sidecar, string tool, JsonObject arguments)
    {
        var response = await sidecar.RpcAsync("tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments,
        });
        var isError = response?["result"]?["isError"]?.GetValue<bool>() ?? true;
        Assert.False(isError, $"{tool} should not be an error: {response}");
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(text), $"{tool} returned empty content");
        return JsonNode.Parse(text!)!;
    }

    private static async Task<JsonNode> PollSidecarAsync(SidecarProcess sidecar, string traceId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var body = await CallToolAsync(sidecar, "action_status", new JsonObject { ["traceId"] = traceId });
            var state = body["state"]?.GetValue<string>();
            if (state is "Completed" or "Rejected" or "Interrupted" or "TimedOut")
                return body;

            await Task.Delay(300);
        }

        throw new TimeoutException($"trace {traceId} never reached a terminal state within {timeout}");
    }

    private sealed class SidecarProcess : IDisposable
    {
        private readonly Process _proc;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private int _nextId = 1;

        public SidecarProcess()
        {
            Assert.True(File.Exists(SidecarDll), $"sidecar binary missing: {SidecarDll}");
            var psi = new ProcessStartInfo("dotnet", SidecarDll)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.Environment["AAEMU_BOT_CTRL_URL"] = WebApiBase;
            psi.Environment["AAEMU_BOT_CTRL_TOKEN"] = Token;
            _proc = Process.Start(psi)!;
            _stdin = _proc.StandardInput;
            _stdout = _proc.StandardOutput;
        }

        public async Task<JsonNode?> RpcAsync(string method, JsonObject? parameters = null)
        {
            var id = _nextId++;
            var line = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JsonObject(),
            }.ToJsonString();
            await _stdin.WriteLineAsync(line);
            await _stdin.FlushAsync();

            var response = await _stdout.ReadLineAsync();
            Assert.False(string.IsNullOrEmpty(response), "sidecar closed stdout without a response");
            return JsonNode.Parse(response!);
        }

        public async Task RpcNotifyAsync(string method)
        {
            var line = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
            }.ToJsonString();
            await _stdin.WriteLineAsync(line);
            await _stdin.FlushAsync();
        }

        public void Dispose()
        {
            try
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(5_000);
            }
            catch
            {
            }
        }
    }

    private static SidecarProcess SpawnSidecar() => new();

    private static async Task WaitForWebApiAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("127.0.0.1", E2eStack.WebApiPort);
                return;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }

        throw new TimeoutException("WebApi never came up on port " + E2eStack.WebApiPort);
    }

    private static HttpClient NewHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(WebApiBase), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("X-Auth-Token", Token);
        return client;
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {path} → {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}

/// <summary>Protocol constants shared with the sidecar for the e2e assertions.</summary>
internal static class ActionMcpProtocol
{
    public const string ServerName = "aaemu-bot-actions";
}
