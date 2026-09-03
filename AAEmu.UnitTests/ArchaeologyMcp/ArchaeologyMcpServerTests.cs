using System.Text.Json.Nodes;
using AAEmu.ArchaeologyMcp;

namespace AAEmu.UnitTests.ArchaeologyMcp;

/// <summary>
/// Rig for the read-only archaeology MCP server: JSON-RPC framing
/// (initialize / tools/list / tools/call / notifications / errors) and the
/// tool surface. Uses a temp repo root so tests never touch the real
/// canonical DB or repo data.
/// </summary>
[NotInParallel]
public class ArchaeologyMcpServerTests
{
    private static JsonNode? Parse(string? line) => line is null ? null : JsonNode.Parse(line);

    private static (string RepoRoot, string DataRoot, string DbPath) CreateTempRepo()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "aaemu-arch-mcp-" + Guid.NewGuid().ToString("N")[..8]);
        var dataRoot = Path.Combine(repoRoot, "AAEmu.Game", "Data");
        Directory.CreateDirectory(dataRoot);
        return (repoRoot, dataRoot, Path.Combine(dataRoot, "compact.sqlite3"));
    }

    private static ArchaeologyService CreateService(string repoRoot, string dataRoot, string dbPath)
    {
        var catalog = new SourceCatalog(repoRoot, dataRoot, dbPath, "test-version", new Dictionary<string, string>());
        return new ArchaeologyService(catalog, new MetadataCache(null));
    }

    private static ArchaeologyMcpServer CreateServer(string repoRoot, string dataRoot, string dbPath)
        => new(CreateService(repoRoot, dataRoot, dbPath), new PakArchiveService(string.Empty, "test-version"));

    // ------------------------------------------------------------- protocol

    [Test]
    public async Task Initialize_ReturnsProtocolAndServerInfo()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}"""));

        await Assert.That(response?["id"]?.GetValue<int>()).IsEqualTo(1);
        await Assert.That(response?["result"]?["protocolVersion"]?.GetValue<string>())
            .IsEqualTo(ArchaeologyMcpServer.ProtocolVersion);
        await Assert.That(response?["result"]?["serverInfo"]?["name"]?.GetValue<string>())
            .IsEqualTo(ArchaeologyMcpServer.ServerName);
    }

    [Test]
    public async Task InitializedNotification_ReturnsNull()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = await server.HandleAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task Ping_ReturnsEmptyResult()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":9,"method":"ping"}"""));

        await Assert.That(response?["id"]?.GetValue<int>()).IsEqualTo(9);
        await Assert.That(response?["result"]).IsNotNull();
    }

    [Test]
    public async Task ToolsList_ExposesTwentyFourReadOnlyTools()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));

        var tools = response?["result"]?["tools"]?.AsArray();
        await Assert.That(tools).HasCount().EqualTo(24);
        var names = tools!.Select(t => t?["name"]?.GetValue<string>()).OrderBy(n => n).ToArray();
        await Assert.That(names).IsEquivalentTo(new[]
        {
            "compare_source_data", "describe_table", "find_quest_objectives",
            "list_databases", "list_pak_entries", "list_sources", "list_tables",
            "lookup_row", "query_sql", "read_file", "read_pak_entry",
            "search_everything", "search_files", "search_physics",
            "trace_crafting", "trace_doodad", "trace_item", "trace_mate",
            "trace_npc", "trace_quest", "trace_references", "trace_skill",
            "trace_vehicle", "trace_world_spawn",
        });
    }

    [Test]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"bogus"}"""));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32601);
    }

    [Test]
    public async Task GarbageLine_ReturnsParseError()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync("not json at all"));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32700);
    }

    [Test]
    public async Task Call_UnknownTool_ReturnsError()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"drop_database","arguments":{}}}"""));

        await Assert.That(response?["error"]?["code"]?.GetValue<int>()).IsEqualTo(-32603);
        await Assert.That(response?["error"]?["message"]?.GetValue<string>()).Contains("Unknown tool");
    }

    // ------------------------------------------------------------ tool calls

    [Test]
    public async Task Call_list_sources_WithConfiguredPak_IncludesGamePak()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var pakPath = Path.Combine(repoRoot, "game.pak");
        File.WriteAllBytes(pakPath, [0x01, 0x02, 0x03]);
        var catalog = new SourceCatalog(repoRoot, dataRoot, dbPath, "test-version",
            new Dictionary<string, string>(), pakPath, "pak-test-version");
        var server = new ArchaeologyMcpServer(
            new ArchaeologyService(catalog, new MetadataCache(null)),
            new PakArchiveService(pakPath, "pak-test-version"));

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_sources","arguments":{}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsFalse();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        var sources = payload["data"]!["sources"]!.AsArray();
        await Assert.That(sources).HasCount().EqualTo(8);
        var pak = sources.First(s => s!["source_id"]?.GetValue<string>() == "game_pak")!;
        await Assert.That(pak["source_type"]?.GetValue<string>()).IsEqualTo("aapak");
        await Assert.That(pak["path"]?.GetValue<string>()).IsEqualTo(pakPath);
        await Assert.That(pak["version"]?.GetValue<string>()).IsEqualTo("pak-test-version");
        await Assert.That(pak["searchable"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Call_query_sql_RejectsMutationAndReturnsIsError()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"query_sql","arguments":{"sql":"DROP TABLE npcs"}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        await Assert.That(payload["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(payload["error"]?.GetValue<string>()).Contains("forbidden keyword");
    }

    [Test]
    public async Task Call_lookup_row_ReturnsRowThroughProtocol()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE npcs (id INTEGER PRIMARY KEY, name TEXT NOT NULL); " +
                                  "INSERT INTO npcs (id, name) VALUES (1, 'Guard');";
            command.ExecuteNonQuery();
        }

        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"lookup_row","arguments":{"table":"npcs","id":1}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsFalse();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        await Assert.That(payload["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(payload["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("lookup_row");
        await Assert.That(payload["data"]?["rows"]?.AsArray()).HasCount().EqualTo(1);
        await Assert.That(payload["data"]!["rows"]![0]!["name"]?.GetValue<string>()).IsEqualTo("Guard");
    }

    [Test]
    public async Task Call_read_file_RejectsTraversal()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"read_file","arguments":{"path":"../../etc/passwd"}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        await Assert.That(payload["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(payload["error"]?.GetValue<string>()).Contains("not allowed");
    }

    [Test]
    public async Task Call_search_files_RejectsUnknownRoot()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath);

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"search_files","arguments":{"pattern":"x","root":"/etc"}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        await Assert.That(payload["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(payload["error"]?.GetValue<string>()).Contains("unknown or missing root");
    }

    // ------------------------------------- regression: root id allowlisting

    [Test]
    public async Task RootId_ValidIdentifiers_AreAccepted()
    {
        await Assert.That(SourceCatalog.IsValidRootId("data")).IsTrue();
        await Assert.That(SourceCatalog.IsValidRootId("pak-lua")).IsTrue();
        await Assert.That(SourceCatalog.IsValidRootId("a_b-c9")).IsTrue();
    }

    [Test]
    public async Task RootId_InvalidIdentifiers_AreRejected()
    {
        await Assert.That(SourceCatalog.IsValidRootId("")).IsFalse();
        await Assert.That(SourceCatalog.IsValidRootId("../etc")).IsFalse();
        await Assert.That(SourceCatalog.IsValidRootId("/abs/path")).IsFalse();
        await Assert.That(SourceCatalog.IsValidRootId("a b")).IsFalse();
        await Assert.That(SourceCatalog.IsValidRootId("a:b")).IsFalse();
    }

    // ------------------------------------------------- pak archive surface

    [Test]
    public async Task Call_list_pak_entries_Unconfigured_ReturnsDeterministicError()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath); // pak path unset

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"list_pak_entries","arguments":{}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        await Assert.That(payload["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(payload["error"]?.GetValue<string>()).Contains("not configured");
        await Assert.That(payload["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("list_pak_entries");
    }

    [Test]
    public async Task Call_read_pak_entry_Unconfigured_ReturnsDeterministicError()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var server = CreateServer(repoRoot, dataRoot, dbPath); // pak path unset

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"read_pak_entry","arguments":{"name":"ui/questcontext/quest.lua"}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        await Assert.That(payload["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(payload["error"]?.GetValue<string>()).Contains("not configured");
        await Assert.That(payload["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("read_pak_entry");
    }

    [Test]
    public async Task Call_read_pak_entry_InvalidName_ReturnsDeterministicError()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        // Configured pak path: name validation runs before the file-existence
        // check, so the archive itself need not exist for this rejection.
        var server = new ArchaeologyMcpServer(
            CreateService(repoRoot, dataRoot, dbPath),
            new PakArchiveService(Path.Combine(repoRoot, "test.pak"), "test-version"));

        var response = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"read_pak_entry","arguments":{"name":"../secret"}}}"""));

        await Assert.That(response?["result"]?["isError"]?.GetValue<bool>()).IsTrue();
        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var payload = JsonNode.Parse(text!)!;
        await Assert.That(payload["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(payload["error"]?.GetValue<string>()).Contains("invalid entry name");
    }

    [Test]
    public async Task Call_list_pak_entries_And_ReadEntry_OnTempArchive()
    {
        var (repoRoot, dataRoot, dbPath) = CreateTempRepo();
        var pakPath = Path.Combine(repoRoot, "test.pak");
        CreateTinyPak(pakPath);
        var server = new ArchaeologyMcpServer(
            CreateService(repoRoot, dataRoot, dbPath), new PakArchiveService(pakPath, "test-version"));

        var listResponse = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"list_pak_entries","arguments":{"pattern":"art/"}}}"""));
        await Assert.That(listResponse?["result"]?["isError"]?.GetValue<bool>()).IsFalse();
        var listText = listResponse?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var listPayload = JsonNode.Parse(listText!)!;
        await Assert.That(listPayload["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(listPayload["data"]?["entries"]?.AsArray()).HasCount().EqualTo(2);
        await Assert.That(listPayload["provenance"]?["source_id"]?.GetValue<string>()).IsEqualTo("game_pak");
        await Assert.That(listPayload["provenance"]?["path"]?.GetValue<string>()).IsEqualTo(pakPath);

        var readResponse = Parse(await server.HandleAsync(
            """{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"read_pak_entry","arguments":{"name":"ui/questcontext/quest.lua"}}}"""));
        await Assert.That(readResponse?["result"]?["isError"]?.GetValue<bool>()).IsFalse();
        var readText = readResponse?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var readPayload = JsonNode.Parse(readText!)!;
        await Assert.That(readPayload["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(readPayload["data"]?["name"]?.GetValue<string>()).IsEqualTo("ui/questcontext/quest.lua");
        await Assert.That(readPayload["data"]?["size"]?.GetValue<long>()).IsEqualTo(8);
        await Assert.That(readPayload["data"]?["truncated"]?.GetValue<bool>()).IsFalse();
    }

    /// <summary>Builds a minimal TypeA AAPak in temp via the library's own write path.</summary>
    private static void CreateTinyPak(string path)
    {
        var pak = new AAEmu.Commons.Utils.AAPak.AAPak(path, openAsReadOnly: false, createAsNewPak: true);
        try
        {
            if (!pak.isOpen)
                throw new InvalidOperationException("failed to create test pak");
            var now = DateTime.UtcNow;
            Add(pak, "art/characters/guard.nut", "guard-data", now);
            Add(pak, "art/characters/merchant.nut", "merchant-data", now);
            Add(pak, "ui/questcontext/quest.lua", "quest-ui", now);
        }
        finally
        {
            pak.ClosePak();
        }
    }

    private static void Add(AAEmu.Commons.Utils.AAPak.AAPak pak, string name, string content, DateTime now)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        if (!pak.AddAsNewFile(name, stream, now, now, autoSpareSpace: false, out _))
            throw new InvalidOperationException($"failed to add entry: {name}");
    }
}
