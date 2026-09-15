using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// MOVEMENT-STOP-01 live-stack proof: a REAL game server boots, a bot is
/// provisioned through the management surface, and the stop/interrupt
/// contract is proven live through the MCP sidecar (the same pipe an agent
/// uses):
///
///   - move leg enqueued → Running on the live actor;
///   - stop while the API-owned move runs → Rejected(busy) per the
///     single-writer anti-race rule (the caller must poll or interrupt);
///   - interrupt enqueued → the live Move request terminates Interrupted
///     with a full transition log (Requested → Accepted → Running →
///     Interrupted);
///   - idle stop → Completed without touching world state;
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class MovementStopInterruptE2eTests
{
    private const string BotName = "StopProof01";
    private const string Token = "e2e-bot-ctrl-token";

    private static string WebApiBase => $"http://127.0.0.1:{E2eStack.WebApiPort}";
    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "movement-stop-interrupt-report.json");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Stop_InterruptsLiveMoveLeg_WithFullTransitionLog()
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

            using (var http = NewHttpClient())
            {
                var add = await PostJsonAsync(http, "/api/bots", $"{{\"name\":\"{BotName}\"}}");
                Assert.True(add.TryGetProperty("success", out var addOk) ? addOk.GetBoolean()
                    : (add.TryGetProperty("Success", out var addOkP) && addOkP.GetBoolean()),
                    "management add must succeed: " + add);
            }

            using var sidecar = SpawnSidecar();
            var initialize = await sidecar.RpcAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
            Assert.NotNull(initialize?["result"]?["serverInfo"]?["name"]);
            await sidecar.RpcNotifyAsync("notifications/initialized");

            // ------------------------------------------------ observe (start pos)
            var observeBody = await CallToolAsync(sidecar, "observe", new JsonObject { ["bot"] = BotName });
            var observePoll = await PollSidecarAsync(sidecar, observeBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(20));
            Assert.Equal("Completed", observePoll["state"]!.GetValue<string>());
            var startX = observePoll["result_payload"]!["Position"]!["X"]!.GetValue<double>();
            var startY = observePoll["result_payload"]!["Position"]!["Y"]!.GetValue<double>();
            var startZ = observePoll["result_payload"]!["Position"]!["Z"]!.GetValue<double>();
            legs.Add(("observe-start", true, $"start=({startX:F1},{startY:F1},{startZ:F1})", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ move (long leg, stays Running)
            var moveBody = await CallToolAsync(sidecar, "move", new JsonObject
            {
                ["bot"] = BotName,
                ["x"] = startX + 200,
                ["y"] = startY,
                ["z"] = startZ,
                ["speed"] = 2.0,
                ["timeoutSec"] = 120,
            });
            var moveTrace = moveBody["trace_id"]!.GetValue<string>();
            await Task.Delay(2_000);
            var moveMid = await CallToolAsync(sidecar, "action_status", new JsonObject { ["traceId"] = moveTrace });
            Assert.Equal("Running", moveMid["state"]!.GetValue<string>());
            legs.Add(("move-running", true, $"move {moveTrace} Running", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ stop while busy (anti-race rejection)
            var stopBody = await CallToolAsync(sidecar, "stop", new JsonObject { ["bot"] = BotName });
            var stopPoll = await PollSidecarAsync(sidecar, stopBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(20));
            Assert.Equal("Rejected", stopPoll["state"]!.GetValue<string>());
            legs.Add(("stop-busy-rejected", true, "stop while API move runs → Rejected(busy)", wall.Elapsed.TotalMilliseconds));
            // ------------------------------------------------ interrupt (preempts the live leg)
            var intBody = await CallToolAsync(sidecar, "interrupt", new JsonObject { ["bot"] = BotName, ["traceId"] = moveTrace });
            var intPoll = await PollSidecarAsync(sidecar, intBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(20));
            var moveEnd = await PollSidecarAsync(sidecar, moveTrace, TimeSpan.FromSeconds(20));
            Assert.Equal("Interrupted", moveEnd["state"]!.GetValue<string>());
            var changes = moveEnd["state_changes"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            Assert.Contains("Requested", changes[0]);
            Assert.True(changes.Any(c => c.StartsWith("Running")), "transition log must show Running");
            Assert.True(changes[^1].StartsWith("Interrupted"), "transition log must end Interrupted");
            legs.Add(("interrupt-preempts", true, $"move → Interrupted ({changes.Count} transitions)", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ idle stop (no world touch)
            var idleBody = await CallToolAsync(sidecar, "stop", new JsonObject { ["bot"] = BotName });
            var idlePoll = await PollSidecarAsync(sidecar, idleBody["trace_id"]!.GetValue<string>(), TimeSpan.FromSeconds(20));
            Assert.Equal("Completed", idlePoll["state"]!.GetValue<string>());
            legs.Add(("stop-idle", true, "idle stop completes", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ trace parity
            var traceBody = await CallToolAsync(sidecar, "trace", new JsonObject { ["bot"] = BotName, ["limit"] = 20 });
            var traceJson = traceBody.ToJsonString();
            Assert.Contains("Interrupted", traceJson);
            legs.Add(("trace-parity", true, "trace carries the Interrupted record", wall.Elapsed.TotalMilliseconds));

            await WriteReportAsync(true, legs, "stop/interrupt live proof", wall.Elapsed.TotalSeconds);
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
            ["test"] = "MovementStopInterrupt",
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
