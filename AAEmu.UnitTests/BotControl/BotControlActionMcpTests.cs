using System.Text.Json.Nodes;
using AAEmu.BotControl;
using AAEmu.BotControlMcp;

namespace AAEmu.UnitTests.BotControl;

/// <summary>
/// Rig for the contract-action MCP sidecar (M5 stage 4, t_446228b5):
/// JSON-RPC framing (initialize / tools/list / tools/call / notifications /
/// errors) and the 1:1 tool→endpoint mapping through a recording fake
/// client. No network, no game process — pure protocol surface. The P1
/// management surface (bot_add/remove/list/relocate/status) is verified
/// ABSENT here (it lives in AAEmu.BotControl, t_2ea94a20).
/// </summary>
[NotInParallel]
public class BotControlActionMcpTests
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
        var server = new ActionMcpServer(client);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""));

        await Assert.That(response?["id"]?.GetValue<int>()).IsEqualTo(1);
        await Assert.That(response?["result"]?["protocolVersion"]?.GetValue<string>())
            .IsEqualTo(ActionMcpServer.ProtocolVersion);
        await Assert.That(response?["result"]?["serverInfo"]?["name"]?.GetValue<string>())
            .IsEqualTo(ActionMcpServer.ServerName);
        await Assert.That(client.Calls).IsEmpty();
    }

    [Test]
    public async Task InitializedNotification_ReturnsNull()
    {
        var server = new ActionMcpServer(new FakeClient());

        var response = await server.HandleAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task Ping_ReturnsEmptyResult()
    {
        var server = new ActionMcpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":9,"method":"ping"}"""));

        await Assert.That(response?["id"]?.GetValue<int>()).IsEqualTo(9);
        await Assert.That(response?["result"]).IsNotNull();
    }

    [Test]
    public async Task ToolsList_ExposesNineteenContractActionTools()
    {
        var server = new ActionMcpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        var tools = response?["result"]?["tools"]?.AsArray();
        await Assert.That(tools).HasCount().EqualTo(19);
        var names = tools!.Select(t => t?["name"]?.GetValue<string>()).OrderBy(n => n).ToArray();
        await Assert.That(names).IsEquivalentTo(new[]
        {
            "accept_quest", "action_status", "advance_quest", "auto_turn_in", "cast",
            "dismount", "interact", "interrupt", "loot", "mount",
            "move", "move_to_unit", "observe", "stop", "target",
            "trace", "turn_in_doodad", "turn_in_quest", "use_item",
        });
    }

    [Test]
    public async Task ToolsList_ContainsNoManagementTools()
    {
        // The P1 management surface (bot_add/remove/list/relocate/status)
        // stays on AAEmu.BotControl (t_2ea94a20) — never duplicated here.
        var server = new ActionMcpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        var names = response?["result"]?["tools"]?.AsArray()
            .Select(t => t?["name"]?.GetValue<string>()).ToArray();
        await Assert.That(names).DoesNotContain("bot_add");
        await Assert.That(names).DoesNotContain("bot_remove");
        await Assert.That(names).DoesNotContain("bot_list");
        await Assert.That(names).DoesNotContain("bot_relocate");
        await Assert.That(names).DoesNotContain("bot_status");
    }

    [Test]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var server = new ActionMcpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"bogus"}"""));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32601);
    }

    [Test]
    public async Task GarbageLine_ReturnsParseError()
    {
        var server = new ActionMcpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("not json at all"));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32700);
    }

    // ------------------------------------------- M5 contract action mapping

    [Test]
    public async Task Call_observe_PostsObserveEndpoint()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"observe","arguments":{"bot":"McpBot01"}}}"""));

        await Assert.That(client.Calls).HasCount().EqualTo(1);
        await Assert.That(client.Calls[0]).IsEqualTo(("POST", "/api/actors/observe", """{"bot":"McpBot01"}"""));
        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsFalse();
        await Assert.That(response?["result"]?["content"]?[0]?["text"]?.GetValue<string>())
            .Contains("success");
    }

    [Test]
    public async Task Call_move_PostsCoordinatesWithOptionalArgs()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"move","arguments":{"bot":"McpBot01","x":15572,"y":15364,"z":126.5,"speed":2,"timeoutSec":20,"idempotencyKey":"k1"}}}""");

        await Assert.That(client.Calls).HasCount().EqualTo(1);
        await Assert.That(client.Calls[0].Method).IsEqualTo("POST");
        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/move");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["bot"]?.GetValue<string>()).IsEqualTo("McpBot01");
        await Assert.That(body["x"]?.GetValue<float>()).IsEqualTo(15572f);
        await Assert.That(body["y"]?.GetValue<float>()).IsEqualTo(15364f);
        await Assert.That(body["z"]?.GetValue<float>()).IsEqualTo(126.5f);
        await Assert.That(body["speed"]?.GetValue<float>()).IsEqualTo(2f);
        await Assert.That(body["timeoutSec"]?.GetValue<int>()).IsEqualTo(20);
        await Assert.That(body["idempotencyKey"]?.GetValue<string>()).IsEqualTo("k1");
    }

    [Test]
    public async Task Call_interact_PostsDoodad()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"interact","arguments":{"bot":"McpBot01","doodadObjId":42}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/interact");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["doodadObjId"]?.GetValue<uint>()).IsEqualTo(42u);
    }

    [Test]
    public async Task Call_accept_quest_PostsQuestAndAcceptor()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"accept_quest","arguments":{"bot":"McpBot01","questId":1001,"acceptorType":"Npc","acceptorId":500}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/accept_quest");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["questId"]?.GetValue<uint>()).IsEqualTo(1001u);
        await Assert.That(body["acceptorType"]?.GetValue<string>()).IsEqualTo("Npc");
        await Assert.That(body["acceptorId"]?.GetValue<uint>()).IsEqualTo(500u);
    }

    [Test]
    public async Task Call_turn_in_quest_PostsNpcAndReward()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"turn_in_quest","arguments":{"bot":"McpBot01","questId":1001,"npcObjId":500,"selectedReward":2}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/turn_in_quest");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["npcObjId"]?.GetValue<uint>()).IsEqualTo(500u);
        await Assert.That(body["selectedReward"]?.GetValue<int>()).IsEqualTo(2);
    }

    [Test]
    public async Task Call_loot_PostsLootOwner()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"loot","arguments":{"bot":"McpBot01","lootOwnerObjId":77}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/loot");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["lootOwnerObjId"]?.GetValue<uint>()).IsEqualTo(77u);
    }

    [Test]
    public async Task Call_use_item_PostsTemplateAndOptionalTarget()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"use_item","arguments":{"bot":"McpBot01","itemTemplateId":5001,"targetObjId":88}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/use_item");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["itemTemplateId"]?.GetValue<uint>()).IsEqualTo(5001u);
        await Assert.That(body["targetObjId"]?.GetValue<uint>()).IsEqualTo(88u);
    }

    [Test]
    public async Task Call_mount_PostsMate()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"mount","arguments":{"bot":"McpBot01","mateObjId":99}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/mount");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["mateObjId"]?.GetValue<uint>()).IsEqualTo(99u);
    }

    // ------------------------------------------------ B1 surface mapping

    [Test]
    public async Task Call_move_to_unit_PostsTargetAndSpeed()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"move_to_unit","arguments":{"bot":"McpBot01","targetObjId":55,"speed":3,"timeoutSec":15}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/move_to_unit");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["targetObjId"]?.GetValue<uint>()).IsEqualTo(55u);
        await Assert.That(body["speed"]?.GetValue<float>()).IsEqualTo(3f);
        await Assert.That(body["timeoutSec"]?.GetValue<int>()).IsEqualTo(15);
    }

    [Test]
    public async Task Call_stop_PostsBotOnly()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"stop","arguments":{"bot":"McpBot01"}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/stop");
        await Assert.That(client.Calls[0].Body).IsEqualTo("""{"bot":"McpBot01"}""");
    }

    [Test]
    public async Task Call_target_PostsTarget()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"target","arguments":{"bot":"McpBot01","targetObjId":66}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/target");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["targetObjId"]?.GetValue<uint>()).IsEqualTo(66u);
    }

    [Test]
    public async Task Call_cast_PostsSkillAndTarget()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"cast","arguments":{"bot":"McpBot01","skillId":101,"targetObjId":66}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/cast");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["skillId"]?.GetValue<uint>()).IsEqualTo(101u);
        await Assert.That(body["targetObjId"]?.GetValue<uint>()).IsEqualTo(66u);
    }

    [Test]
    public async Task Call_dismount_PostsOptionalMate()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":16,"method":"tools/call","params":{"name":"dismount","arguments":{"bot":"McpBot01"}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/dismount");
        await Assert.That(client.Calls[0].Body).IsEqualTo("""{"bot":"McpBot01"}""");
    }

    [Test]
    public async Task Call_advance_quest_PostsQuest()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":17,"method":"tools/call","params":{"name":"advance_quest","arguments":{"bot":"McpBot01","questId":1001}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/advance_quest");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["questId"]?.GetValue<uint>()).IsEqualTo(1001u);
    }

    [Test]
    public async Task Call_turn_in_doodad_PostsDoodad()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":18,"method":"tools/call","params":{"name":"turn_in_doodad","arguments":{"bot":"McpBot01","questId":1001,"doodadObjId":42}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/turn_in_doodad");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["doodadObjId"]?.GetValue<uint>()).IsEqualTo(42u);
    }

    [Test]
    public async Task Call_auto_turn_in_PostsQuest()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":19,"method":"tools/call","params":{"name":"auto_turn_in","arguments":{"bot":"McpBot01","questId":1001}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/auto_turn_in");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["questId"]?.GetValue<uint>()).IsEqualTo(1001u);
    }

    [Test]
    public async Task Call_interrupt_PostsTraceId()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"interrupt","arguments":{"bot":"McpBot01","traceId":"11111111-2222-3333-4444-555555555555"}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/interrupt");
        var body = JsonNode.Parse(client.Calls[0].Body!)!;
        await Assert.That(body["traceId"]?.GetValue<string>()).IsEqualTo("11111111-2222-3333-4444-555555555555");
    }

    // ------------------------------------------------- lifecycle / audit

    [Test]
    public async Task Call_action_status_GetsTraceEndpoint()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"11111111-2222-3333-4444-555555555555"}}}""");

        await Assert.That(client.Calls).HasCount().EqualTo(1);
        await Assert.That(client.Calls[0]).IsEqualTo(("GET", "/api/actors/actions/11111111-2222-3333-4444-555555555555", null));
    }

    [Test]
    public async Task Call_action_status_EncodesTraceArgument()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":27,"method":"tools/call","params":{"name":"action_status","arguments":{"traceId":"11111111-2222-3333-4444-555555555555/extra"}}}""");

        await Assert.That(client.Calls[0].Path)
            .IsEqualTo("/api/actors/actions/11111111-2222-3333-4444-555555555555%2Fextra");
    }

    [Test]
    public async Task Call_trace_GetsTraceQueryWithLimit()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"trace","arguments":{"bot":"McpBot01","limit":20}}}""");

        await Assert.That(client.Calls).HasCount().EqualTo(1);
        await Assert.That(client.Calls[0].Method).IsEqualTo("GET");
        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/trace?bot=McpBot01&limit=20");
    }

    [Test]
    public async Task Call_trace_WithoutLimit_GetsTraceQuery()
    {
        var client = new FakeClient();
        var server = new ActionMcpServer(client);

        await server.HandleAsync(
            """{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"trace","arguments":{"bot":"Mcp Bot"}}}""");

        await Assert.That(client.Calls[0].Path).IsEqualTo("/api/actors/trace?bot=Mcp%20Bot");
    }

    [Test]
    public async Task ToolsList_EveryToolHasRequiredSchemaFields()
    {
        var server = new ActionMcpServer(new FakeClient());

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":30,"method":"tools/list"}"""));
        var tools = response?["result"]?["tools"]?.AsArray()!;

        foreach (var tool in tools)
        {
            var name = tool?["name"]?.GetValue<string>();
            var schema = tool?["inputSchema"]?.AsObject();
            await Assert.That(name).IsNotNull();
            await Assert.That(schema?["type"]?.GetValue<string>()).IsEqualTo("object");
            var properties = schema?["properties"]?.AsObject();
            var required = schema?["required"]?.AsArray();
            await Assert.That(properties).IsNotNull();
            await Assert.That(required).IsNotNull();
            await Assert.That(required!.Count).IsGreaterThan(0);

            foreach (var requiredName in required!)
                await Assert.That(properties!.ContainsKey(requiredName!.GetValue<string>())).IsTrue();
        }
    }

    [Test]
    public async Task CallTool_MapsEveryRegisteredToolToExactWireRequest()
    {
        var cases = new[]
        {
            ("observe", """{"bot":"McpBot01"}""", "POST", "/api/actors/observe", """{"bot":"McpBot01"}"""),
            ("move", """{"bot":"McpBot01","x":1,"y":2,"z":3,"speed":2,"timeoutSec":20,"idempotencyKey":"k"}""", "POST", "/api/actors/move", """{"bot":"McpBot01","x":1,"y":2,"z":3,"speed":2,"timeoutSec":20,"idempotencyKey":"k"}"""),
            ("interact", """{"bot":"McpBot01","doodadObjId":42,"skillId":7,"idempotencyKey":"k"}""", "POST", "/api/actors/interact", """{"bot":"McpBot01","doodadObjId":42,"skillId":7,"idempotencyKey":"k"}"""),
            ("accept_quest", """{"bot":"McpBot01","questId":1001,"acceptorType":"Npc","acceptorId":500,"idempotencyKey":"k"}""", "POST", "/api/actors/accept_quest", """{"bot":"McpBot01","questId":1001,"acceptorType":"Npc","acceptorId":500,"idempotencyKey":"k"}"""),
            ("turn_in_quest", """{"bot":"McpBot01","questId":1001,"npcObjId":500,"selectedReward":2,"idempotencyKey":"k"}""", "POST", "/api/actors/turn_in_quest", """{"bot":"McpBot01","questId":1001,"npcObjId":500,"selectedReward":2,"idempotencyKey":"k"}"""),
            ("loot", """{"bot":"McpBot01","lootOwnerObjId":77,"idempotencyKey":"k"}""", "POST", "/api/actors/loot", """{"bot":"McpBot01","lootOwnerObjId":77,"idempotencyKey":"k"}"""),
            ("use_item", """{"bot":"McpBot01","itemTemplateId":5001,"targetObjId":88,"idempotencyKey":"k"}""", "POST", "/api/actors/use_item", """{"bot":"McpBot01","itemTemplateId":5001,"targetObjId":88,"idempotencyKey":"k"}"""),
            ("mount", """{"bot":"McpBot01","mateObjId":99,"idempotencyKey":"k"}""", "POST", "/api/actors/mount", """{"bot":"McpBot01","mateObjId":99,"idempotencyKey":"k"}"""),
            ("move_to_unit", """{"bot":"McpBot01","targetObjId":55,"speed":3,"timeoutSec":15,"idempotencyKey":"k"}""", "POST", "/api/actors/move_to_unit", """{"bot":"McpBot01","targetObjId":55,"speed":3,"timeoutSec":15,"idempotencyKey":"k"}"""),
            ("stop", """{"bot":"McpBot01"}""", "POST", "/api/actors/stop", """{"bot":"McpBot01"}"""),
            ("target", """{"bot":"McpBot01","targetObjId":66}""", "POST", "/api/actors/target", """{"bot":"McpBot01","targetObjId":66}"""),
            ("cast", """{"bot":"McpBot01","skillId":101,"targetObjId":66,"idempotencyKey":"k"}""", "POST", "/api/actors/cast", """{"bot":"McpBot01","skillId":101,"targetObjId":66,"idempotencyKey":"k"}"""),
            ("dismount", """{"bot":"McpBot01","mateObjId":99,"idempotencyKey":"k"}""", "POST", "/api/actors/dismount", """{"bot":"McpBot01","mateObjId":99,"idempotencyKey":"k"}"""),
            ("advance_quest", """{"bot":"McpBot01","questId":1001,"idempotencyKey":"k"}""", "POST", "/api/actors/advance_quest", """{"bot":"McpBot01","questId":1001,"idempotencyKey":"k"}"""),
            ("turn_in_doodad", """{"bot":"McpBot01","questId":1001,"doodadObjId":42,"selectedReward":2,"idempotencyKey":"k"}""", "POST", "/api/actors/turn_in_doodad", """{"bot":"McpBot01","questId":1001,"doodadObjId":42,"selectedReward":2,"idempotencyKey":"k"}"""),
            ("auto_turn_in", """{"bot":"McpBot01","questId":1001,"selectedReward":2,"idempotencyKey":"k"}""", "POST", "/api/actors/auto_turn_in", """{"bot":"McpBot01","questId":1001,"selectedReward":2,"idempotencyKey":"k"}"""),
            ("interrupt", """{"bot":"McpBot01","traceId":"11111111-2222-3333-4444-555555555555"}""", "POST", "/api/actors/interrupt", """{"bot":"McpBot01","traceId":"11111111-2222-3333-4444-555555555555"}"""),
            ("action_status", """{"traceId":"11111111-2222-3333-4444-555555555555"}""", "GET", "/api/actors/actions/11111111-2222-3333-4444-555555555555", null),
            ("trace", """{"bot":"Mcp Bot/01","limit":20}""", "GET", "/api/actors/trace?bot=Mcp%20Bot%2F01&limit=20", null),
        };

        foreach (var (name, arguments, method, path, expectedBody) in cases)
        {
            var client = new FakeClient();
            var server = new ActionMcpServer(client);
            var requestNode = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 31,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = name,
                    ["arguments"] = JsonNode.Parse(arguments),
                },
            };

            var response = Parse(await server.HandleAsync(requestNode.ToJsonString()));

            await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsFalse();
            await Assert.That(client.Calls).HasCount().EqualTo(1);
            await Assert.That(client.Calls[0].Method).IsEqualTo(method);
            await Assert.That(client.Calls[0].Path).IsEqualTo(path);
            if (expectedBody is null)
                await Assert.That(client.Calls[0].Body).IsNull();
            else
                await Assert.That(client.Calls[0].Body).IsEqualTo(expectedBody);
        }
    }

    // -------------------------------------------------------------- errors

    [Test]
    public async Task Call_UnknownTool_ReturnsError()
    {
        var server = new ActionMcpServer(new FakeClient());

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"bot_fly","arguments":{}}}"""));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32603);
        await Assert.That(response?["error"]?["message"]?.GetValue<string>()).Contains("Unknown tool");
    }

    [Test]
    public async Task Call_HttpError_SetsIsErrorFlag()
    {
        var client = new FakeClient { Response = (401, """{"message":"Missing or invalid X-Auth-Token"}""") };
        var server = new ActionMcpServer(client);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":25,"method":"tools/call","params":{"name":"observe","arguments":{"bot":"McpBot01"}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        await Assert.That(response?["result"]?["content"]?[0]?["text"]?.GetValue<string>())
            .Contains("X-Auth-Token");
    }

    [Test]
    public async Task Call_ValidationError_IsReturnedAsToolError()
    {
        // The API validates (bot required, finite coords) and answers 4xx —
        // the sidecar must surface that as isError=true with the API body.
        var client = new FakeClient { Response = (400, """{"message":"x, y and z are required"}""") };
        var server = new ActionMcpServer(client);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":26,"method":"tools/call","params":{"name":"move","arguments":{"bot":"McpBot01"}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        await Assert.That(response?["result"]?["content"]?[0]?["text"]?.GetValue<string>())
            .Contains("x, y and z are required");
    }
}
