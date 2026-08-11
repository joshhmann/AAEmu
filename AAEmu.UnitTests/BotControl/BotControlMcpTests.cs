using System.Text.Json.Nodes;
using AAEmu.BotControl;

namespace AAEmu.UnitTests.BotControl;

/// <summary>
/// Rig for the MCP stdio gateway (P1 t_2ea94a20): JSON-RPC framing
/// (initialize / tools/list / tools/call / notifications / errors) and the
/// 1:1 tool→endpoint mapping through a recording fake client. No network,
/// no game process — pure protocol surface.
/// </summary>
[NotInParallel]
public class BotControlMcpTests
{
    private sealed class FakeClient : IBotControlClient
    {
        public List<(string Method, string Path, string? Body)> Calls { get; } = [];
        public (int Status, string Body) Response { get; set; } = (200, """{"success":true,"message":"ok"}""");

        public Task<(int Status, string Body)> GetAsync(string path)
        {
            Calls.Add(("GET", path, null));
            return Task.FromResult(Response);
        }

        public Task<(int Status, string Body)> PostAsync(string path, string jsonBody)
        {
            Calls.Add(("POST", path, jsonBody));
            return Task.FromResult(Response);
        }
    }

    private static JsonNode? Parse(string? line) => line is null ? null : JsonNode.Parse(line);

    // ------------------------------------------------------------- protocol

    [Test]
    public async Task Initialize_ReturnsProtocolAndServerInfo()
    {
        var client = new FakeClient();
        var server = new McpServer(client);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""));

        await Assert.That(response?["id"]?.GetValue<int>()).IsEqualTo(1);
        await Assert.That(response?["result"]?["protocolVersion"]?.GetValue<string>())
            .IsEqualTo(McpServer.ProtocolVersion);
        await Assert.That(response?["result"]?["serverInfo"]?["name"]?.GetValue<string>())
            .IsEqualTo(McpServer.ServerName);
        await Assert.That(client.Calls).IsEmpty();
    }

    [Test]
    public async Task InitializedNotification_ReturnsNull()
    {
        var server = new McpServer(new FakeClient());

        var response = await server.HandleAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task Ping_ReturnsEmptyResult()
    {
        var server = new McpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":9,"method":"ping"}"""));

        await Assert.That(response?["id"]?.GetValue<int>()).IsEqualTo(9);
        await Assert.That(response?["result"]).IsNotNull();
    }

    [Test]
    public async Task ToolsList_ExposesFiveBotTools()
    {
        var server = new McpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        var tools = response?["result"]?["tools"]?.AsArray();
        await Assert.That(tools).HasCount().EqualTo(5);
        var names = tools!.Select(t => t?["name"]?.GetValue<string>()).OrderBy(n => n).ToArray();
        await Assert.That(names).IsEquivalentTo(
            new[] { "bot_add", "bot_list", "bot_relocate", "bot_remove", "bot_status" });
    }

    [Test]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var server = new McpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"bogus"}"""));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32601);
    }

    [Test]
    public async Task GarbageLine_ReturnsParseError()
    {
        var server = new McpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("not json at all"));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32700);
    }

    // ------------------------------------------------------------ tool calls

    [Test]
    public async Task Call_bot_list_GetsBotsEndpoint()
    {
        var client = new FakeClient();
        var server = new McpServer(client);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"bot_list","arguments":{}}}"""));

        await Assert.That(client.Calls).HasCount().EqualTo(1);
        await Assert.That(client.Calls[0]).IsEqualTo(("GET", "/api/bots", null));
        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsFalse();
        await Assert.That(response?["result"]?["content"]?[0]?["text"]?.GetValue<string>())
            .Contains("success");
    }

    [Test]
    public async Task Call_bot_status_GetsStatusEndpoint()
    {
        var client = new FakeClient();
        var server = new McpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"bot_status","arguments":{}}}""");

        await Assert.That(client.Calls[0]).IsEqualTo(("GET", "/api/bots/status", null));
    }

    [Test]
    public async Task Call_bot_add_PostsNameAndPosition()
    {
        var client = new FakeClient();
        var server = new McpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"bot_add","arguments":{"name":"McpBot01","x":15572,"y":15364,"z":126.5}}}""");

        await Assert.That(client.Calls).HasCount().EqualTo(1);
        await Assert.That(client.Calls[0].Method).IsEqualTo("POST");
        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/bots");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["name"]?.GetValue<string>()).IsEqualTo("McpBot01");
        await Assert.That(body["x"]?.GetValue<float>()).IsEqualTo(15572f);
        await Assert.That(body["z"]?.GetValue<float>()).IsEqualTo(126.5f);
    }

    [Test]
    public async Task Call_bot_remove_PostsNameOrId()
    {
        var client = new FakeClient();
        var server = new McpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"bot_remove","arguments":{"nameOrId":"McpBot01"}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/bots/remove");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["nameOrId"]?.GetValue<string>()).IsEqualTo("McpBot01");
    }

    [Test]
    public async Task Call_bot_relocate_PostsCoordinates()
    {
        var client = new FakeClient();
        var server = new McpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"bot_relocate","arguments":{"nameOrId":"McpBot01","x":1.5,"y":2.5,"z":3.5}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/bots/relocate");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["x"]?.GetValue<float>()).IsEqualTo(1.5f);
        await Assert.That(body["y"]?.GetValue<float>()).IsEqualTo(2.5f);
        await Assert.That(body["z"]?.GetValue<float>()).IsEqualTo(3.5f);
    }

    [Test]
    public async Task Call_UnknownTool_ReturnsError()
    {
        var server = new McpServer(new FakeClient());

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"bot_fly","arguments":{}}}"""));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32603);
        await Assert.That(response?["error"]?["message"]?.GetValue<string>()).Contains("Unknown tool");
    }

    [Test]
    public async Task Call_HttpError_SetsIsErrorFlag()
    {
        var client = new FakeClient { Response = (401, """{"message":"Missing or invalid X-Auth-Token"}""") };
        var server = new McpServer(client);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"bot_list","arguments":{}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        await Assert.That(response?["result"]?["content"]?[0]?["text"]?.GetValue<string>())
            .Contains("X-Auth-Token");
    }
}
