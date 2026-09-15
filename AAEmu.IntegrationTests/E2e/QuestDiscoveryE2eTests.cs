using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// QUEST-DISCOVERY-01 live-stack proof: a REAL game server boots, a bot is
/// provisioned, and the DiscoverQuests / DiscoverSelfQuests perception
/// primitives are invoked live through the MCP sidecar (the same pipe an
/// agent uses):
///
///   - bridge resolves a live Solzreed starter NPC objId by template;
///   - discover_quests on the live NPC → Completed with offerings;
///   - discover_self_quests → Completed (channels may be empty — the
///     contract call itself is the proof).
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class QuestDiscoveryE2eTests
{
    private const string BotName = "DiscoverProof01";
    private const string Token = "e2e-bot-ctrl-token";
    private const uint SolzreedOfferer3515 = 3515;

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "quest-discovery-report.json");
    private const string NetBotName = "DiscoverNet01";
    [Fact]
    [Trait("Category", "e2e")]
    public async Task DiscoverQuests_LiveNpc_ReturnsOfferings()
    {
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", "1");
        Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", Token);
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer();
            await WaitForWebApiAsync(TimeSpan.FromSeconds(120));

            // Networked bot: the bridge drive ops need a live session, so the
            // bot enters the world through the real login flow (TransferRide
            using var bot = await BotNetworkSession.ConnectAsync(
                NetBotName, "e2ediscovernet", "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.True(bot.InWorld, "bot must be in-world (real login flow)");

            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            using var sidecar = SpawnSidecar();
            var initialize = await sidecar.RpcAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
            Assert.NotNull(initialize?["result"]?["serverInfo"]?["name"]);
            await sidecar.RpcNotifyAsync("notifications/initialized");

            // ------------------------------------------------ teleport to NPC spawner
            // NPCs spawn only near players — teleport to 3515's spawner so the
            // world materializes it through the normal spawn path.
            var teleport = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{NetBotName}\",\"op\":\"teleportToNpc\",\"npc\":{SolzreedOfferer3515}}}", 30_000);
            Assert.True(teleport.TryGetProperty("x", out _), "teleport must resolve spawner: " + teleport);
            // Spawn-radius world: poll until the NPC materializes through the
            // normal spawn path (a fixed sleep races the spawn tick — Q4).
            var npcObjId = 0u;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline && npcObjId == 0)
            {
                npcObjId = ResolveNpcObjId(bridge, SolzreedOfferer3515);
                if (npcObjId == 0)
                    await Task.Delay(1_000);
            }
            Assert.True(npcObjId != 0, $"live NPC template {SolzreedOfferer3515} must exist in-world");
            legs.Add(("npc-resolve", true, $"NPC 3515 → objId {npcObjId}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ relocate managed bot
            // The sidecar discovers through the MANAGED bot (headless,
            // PlayerBotManager plane); discovery is range-gated, so relocate
            // it to the spawner position the teleport leg returned.
            var sx = teleport.GetProperty("x").GetDouble();
            var sy = teleport.GetProperty("y").GetDouble();
            var sz = teleport.GetProperty("z").GetDouble();
            using (var http = NewHttpClient())
            {
                var add = await PostJsonAsync(http, "/api/bots", $"{{\"name\":\"{BotName}\"}}");
                Assert.True(add.TryGetProperty("success", out var addOk) ? addOk.GetBoolean()
                    : (add.TryGetProperty("Success", out var addOkP) && addOkP.GetBoolean()),
                    "management add must succeed: " + add);
                var reloc = await PostJsonAsync(http, "/api/bots/relocate",
                    $"{{\"nameOrId\":\"{BotName}\",\"x\":{sx},\"y\":{sy},\"z\":{sz}}}");
                Assert.True(reloc.TryGetProperty("success", out var relOk) ? relOk.GetBoolean()
                    : (reloc.TryGetProperty("Success", out var relOkP) && relOkP.GetBoolean()),
                    "relocate must succeed: " + reloc);
            }
            legs.Add(("relocate", true, $"managed bot at spawner ({sx:F0},{sy:F0})", wall.Elapsed.TotalMilliseconds));
            // ------------------------------------------------ discover_quests
            var discBody = await CallToolAsync(sidecar, "discover_quests", new JsonObject
            {
                ["bot"] = BotName,
                ["targetObjId"] = npcObjId,
            });
            var discPoll = await PollSidecarAsync(sidecar, discBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(30));
            Assert.Equal("Completed", discPoll["state"]!.GetValue<string>());
            var offerings = discPoll["result_payload"]?["Offerings"]?.AsArray();
            Assert.NotNull(offerings);
            Assert.True(offerings!.Count > 0, "live NPC 3515 must offer quests to a fresh bot");
            legs.Add(("discover-quests", true, $"{offerings.Count} offering(s) from live NPC", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ discover_self_quests
            var selfBody = await CallToolAsync(sidecar, "discover_self_quests", new JsonObject { ["bot"] = BotName });
            var selfPoll = await PollSidecarAsync(sidecar, selfBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(30));
            Assert.Equal("Completed", selfPoll["state"]!.GetValue<string>());
            legs.Add(("discover-self", true, "self channels queried live", wall.Elapsed.TotalMilliseconds));

            await WriteReportAsync(true, legs, "quest discovery live proof", wall.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            legs.Add(("exception", false, ex.GetType().Name + ": " + ex.Message, wall.Elapsed.TotalMilliseconds));
            await WriteReportAsync(false, legs, ex.ToString(), wall.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL", null);
            Environment.SetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN", null);
        }
    }

    // ------------------------------------------------------------ helpers

    private static readonly string SidecarDll = Path.Combine(AppContext.BaseDirectory, "AAEmu.BotControlMcp.dll");

    private static uint ResolveNpcObjId(BotDriveClient bridge, uint templateId)
    {
        var reply = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{NetBotName}\",\"op\":\"npcObjId\",\"npc\":{templateId}}}", 30_000);
        return reply.TryGetProperty("objId", out var id) ? id.GetUInt32() : 0;
    }

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

    private async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new JsonObject
        {
            ["test"] = "QuestDiscovery",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["legs"] = new JsonArray(legs.Select(l => new JsonObject
            {
                ["leg"] = l.Leg,
                ["passed"] = l.Passed,
                ["detail"] = l.Detail,
                ["ms"] = l.Ms,
            }).ToArray()),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
