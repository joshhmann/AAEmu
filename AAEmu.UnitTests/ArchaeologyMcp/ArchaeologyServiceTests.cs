using System.Text.Json.Nodes;
using AAEmu.ArchaeologyMcp;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.ArchaeologyMcp;

/// <summary>
/// Defends the read-only archaeology service against a real temp SQLite DB
/// and temp files: source listing, table introspection, parameterized
/// query_sql, bounded reads, allowlisted search, traversal/symlink
/// rejection, row/output limits, and provenance/truncation metadata.
/// </summary>
[NotInParallel]
public class ArchaeologyServiceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _dataRoot;
    private readonly string _dbPath;
    private readonly SourceCatalog _catalog;
    private readonly ArchaeologyService _service;

    public ArchaeologyServiceTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "aaemu-arch-svc-" + Guid.NewGuid().ToString("N")[..8]);
        _dataRoot = Path.Combine(_repoRoot, "AAEmu.Game", "Data");
        Directory.CreateDirectory(_dataRoot);
        _dbPath = Path.Combine(_dataRoot, "compact.sqlite3");

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE npcs (id INTEGER PRIMARY KEY, name TEXT NOT NULL, level INTEGER);
                INSERT INTO npcs (id, name, level) VALUES (1, 'Guard', 10);
                INSERT INTO npcs (id, name, level) VALUES (2, 'Merchant', 5);
                INSERT INTO npcs (id, name, level) VALUES (3, 'Guard Captain', 20);
                CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT);
                INSERT INTO items (id, name) VALUES (29040, 'Patrashu Bread');
                """;
            command.ExecuteNonQuery();
        }

        _catalog = new SourceCatalog(_repoRoot, _dataRoot, _dbPath, "test-version", new Dictionary<string, string>());
        _service = new ArchaeologyService(_catalog, new MetadataCache(null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    // ------------------------------------------------------------ sources

    [Test]
    public async Task ListSources_WithoutPakPath_OmitsGamePak()
    {
        var result = _service.ListSources();

        var ids = result["data"]!["sources"]!.AsArray()
            .Select(s => s!["source_id"]?.GetValue<string>()).ToArray();
        await Assert.That(ids).DoesNotContain("game_pak");
    }

    [Test]
    public async Task ListSources_WithPakPath_IncludesGamePakEntry()
    {
        var pakPath = Path.Combine(_repoRoot, "game.pak");
        File.WriteAllBytes(pakPath, [0x01, 0x02, 0x03, 0x04]);
        var catalog = new SourceCatalog(_repoRoot, _dataRoot, _dbPath, "test-version",
            new Dictionary<string, string>(), pakPath, "pak-test-version");
        var service = new ArchaeologyService(catalog, new MetadataCache(null));

        var result = service.ListSources();

        var sources = result["data"]!["sources"]!.AsArray();
        await Assert.That(sources).HasCount().EqualTo(8);
        var pak = sources.First(s => s!["source_id"]?.GetValue<string>() == "game_pak")!;
        await Assert.That(pak["source_type"]?.GetValue<string>()).IsEqualTo("aapak");
        await Assert.That(pak["path"]?.GetValue<string>()).IsEqualTo(pakPath);
        await Assert.That(pak["version"]?.GetValue<string>()).IsEqualTo("pak-test-version");
        await Assert.That(pak["size"]!.GetValue<long>()).IsEqualTo(4);
        await Assert.That(pak["searchable"]?.GetValue<bool>()).IsFalse();
        await Assert.That(pak["logical_domain"]?.GetValue<string>()).Contains("client asset");
    }

    [Test]
    public async Task ListSources_WithPakPathMissingFile_StillListsEntryWithZeroSize()
    {
        var pakPath = Path.Combine(_repoRoot, "missing.pak");
        var catalog = new SourceCatalog(_repoRoot, _dataRoot, _dbPath, "test-version",
            new Dictionary<string, string>(), pakPath, "pak-test-version");
        var service = new ArchaeologyService(catalog, new MetadataCache(null));

        var result = service.ListSources();

        var pak = result["data"]!["sources"]!.AsArray()
            .First(s => s!["source_id"]?.GetValue<string>() == "game_pak")!;
        await Assert.That(pak["size"]!.GetValue<long>()).IsEqualTo(0);
        await Assert.That(pak["path"]?.GetValue<string>()).IsEqualTo(pakPath);
    }

    [Test]
    public async Task ListDatabases_ReturnsCanonicalDb()
    {
        var result = _service.ListDatabases();

        var dbs = result["data"]!["databases"]!.AsArray();
        await Assert.That(dbs).HasCount().EqualTo(1);
        await Assert.That(dbs[0]!["db_id"]?.GetValue<string>()).IsEqualTo("compact.sqlite3");
        await Assert.That(dbs[0]!["read_only"]?.GetValue<bool>()).IsTrue();
        await Assert.That(dbs[0]!["exists"]?.GetValue<bool>()).IsTrue();
    }

    // ------------------------------------------------------------- tables

    [Test]
    public async Task ListTables_ReturnsSortedTables()
    {
        var result = _service.ListTables("compact.sqlite3");

        var tables = result["data"]!["tables"]!.AsArray()
            .Select(t => t!.GetValue<string>()).ToArray();
        await Assert.That(tables).IsEquivalentTo(new[] { "items", "npcs" });
        await Assert.That(result["provenance"]?["source_id"]?.GetValue<string>()).IsEqualTo("compact.sqlite3");
    }

    [Test]
    public async Task DescribeTable_ReturnsColumns()
    {
        var result = _service.DescribeTable("compact.sqlite3", "npcs");

        var columns = result["data"]!["columns"]!.AsArray();
        await Assert.That(columns).HasCount().EqualTo(3);
        await Assert.That(columns[0]!["name"]?.GetValue<string>()).IsEqualTo("id");
        await Assert.That(columns[0]!["pk"]?.GetValue<bool>()).IsTrue();
        await Assert.That(columns[1]!["name"]?.GetValue<string>()).IsEqualTo("name");
        await Assert.That(columns[1]!["notnull"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task DescribeTable_InvalidName_ReturnsError()
    {
        var result = _service.DescribeTable("compact.sqlite3", "npcs; DROP TABLE npcs");

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid table name");
    }

    // ------------------------------------------------------------ query_sql

    [Test]
    public async Task QuerySql_Select_ReturnsRowsWithColumns()
    {
        var result = _service.QuerySql("compact.sqlite3", "SELECT id, name FROM npcs ORDER BY id", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var data = result["data"]!;
        await Assert.That(data["columns"]!.AsArray().Select(c => c!.GetValue<string>()))
            .IsEquivalentTo(new[] { "id", "name" });
        await Assert.That(data["rows"]!.AsArray()).HasCount().EqualTo(3);
        await Assert.That(data["row_count"]?.GetValue<int>()).IsEqualTo(3);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsFalse();
        await Assert.That(data["rows"]![0]!["name"]?.GetValue<string>()).IsEqualTo("Guard");
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("query_sql");
        await Assert.That(result["provenance"]?["path"]?.GetValue<string>()).IsEqualTo(_dbPath);
    }

    [Test]
    public async Task QuerySql_Parameterized_ReturnsFilteredRows()
    {
        var parameters = new JsonObject { ["level"] = 10 };
        var result = _service.QuerySql(
            "compact.sqlite3", "SELECT name FROM npcs WHERE level = @level", parameters, null);

        var rows = result["data"]!["rows"]!.AsArray();
        await Assert.That(rows).HasCount().EqualTo(1);
        await Assert.That(rows[0]!["name"]?.GetValue<string>()).IsEqualTo("Guard");
    }

    [Test]
    public async Task QuerySql_WithClause_IsAccepted()
    {
        var result = _service.QuerySql(
            "compact.sqlite3", "WITH high AS (SELECT name FROM npcs WHERE level >= 20) SELECT * FROM high", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(result["data"]!["rows"]!.AsArray()).HasCount().EqualTo(1);
    }

    [Test]
    public async Task QuerySql_Mutation_IsRejected()
    {
        var result = _service.QuerySql("compact.sqlite3", "DELETE FROM npcs WHERE id = 1", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("SQL rejected");
    }

    [Test]
    public async Task QuerySql_Mutation_DoesNotChangeData()
    {
        _service.QuerySql("compact.sqlite3", "DELETE FROM npcs", null, null);

        var result = _service.QuerySql("compact.sqlite3", "SELECT COUNT(*) AS c FROM npcs", null, null);
        await Assert.That(result["data"]!["rows"]![0]!["c"]?.GetValue<long>()).IsEqualTo(3);
    }

    [Test]
    public async Task QuerySql_Limit_TruncatesRows()
    {
        var result = _service.QuerySql("compact.sqlite3", "SELECT id FROM npcs ORDER BY id", null, 2);

        var data = result["data"]!;
        await Assert.That(data["rows"]!.AsArray()).HasCount().EqualTo(2);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["limit"]?.GetValue<int>()).IsEqualTo(2);
        await Assert.That(result["provenance"]?["truncated"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task QuerySql_UnknownDb_Throws()
    {
        await Assert.That(() => _service.QuerySql("bogus", "SELECT 1", null, null))
            .Throws<ArgumentException>();
    }

    // ------------------------------------------------------------ read_file

    [Test]
    public async Task ReadFile_ReadsAllowedFile()
    {
        var file = Path.Combine(_dataRoot, "sample.json");
        File.WriteAllText(file, """{"a": 1, "b": "hello"}""");

        var result = _service.ReadFile("AAEmu.Game/Data/sample.json", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(result["data"]!["content"]?.GetValue<string>()).Contains("hello");
        await Assert.That(result["data"]!["size"]?.GetValue<long>()).IsEqualTo(22);
        await Assert.That(result["data"]!["truncated"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["provenance"]?["path"]?.GetValue<string>()).IsEqualTo(file);
    }

    [Test]
    public async Task ReadFile_OffsetLimit_ReadsSlice()
    {
        var file = Path.Combine(_dataRoot, "sample.txt");
        File.WriteAllText(file, "0123456789");

        var result = _service.ReadFile("AAEmu.Game/Data/sample.txt", 2, 4);

        await Assert.That(result["data"]!["content"]?.GetValue<string>()).IsEqualTo("2345");
        await Assert.That(result["data"]!["offset"]?.GetValue<int>()).IsEqualTo(2);
        await Assert.That(result["data"]!["bytes_read"]?.GetValue<int>()).IsEqualTo(4);
    }

    [Test]
    public async Task ReadFile_Traversal_IsRejected()
    {
        var result = _service.ReadFile("../../etc/passwd", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("not allowed");
    }

    [Test]
    public async Task ReadFile_AbsoluteOutsideRoot_IsRejected()
    {
        var result = _service.ReadFile("/etc/hostname", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task ReadFile_SymlinkEscapingRoot_IsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "aaemu-arch-outside-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var link = Path.Combine(_dataRoot, "escape.txt");
            File.CreateSymbolicLink(link, outside);

            var result = _service.ReadFile("AAEmu.Game/Data/escape.txt", null, null);
            await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public async Task ReadFile_MissingFile_ReturnsError()
    {
        var result = _service.ReadFile("AAEmu.Game/Data/nope.json", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("not found");
    }

    // --------------------------------------------------------- search_files

    [Test]
    public async Task SearchFiles_FindsMatchesWithLines()
    {
        var file = Path.Combine(_dataRoot, "notes.md");
        File.WriteAllText(file, "line one\nZephyrion bread here\nline three");

        var result = _service.SearchFiles("Zephyrion", null, null, null);

        var matches = result["data"]!["matches"]!.AsArray();
        await Assert.That(matches).HasCount().EqualTo(1);
        await Assert.That(matches[0]!["path"]?.GetValue<string>()).IsEqualTo(file);
        await Assert.That(matches[0]!["line"]?.GetValue<int>()).IsEqualTo(2);
        await Assert.That(result["data"]!["truncated"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("search_files");
    }

    [Test]
    public async Task SearchFiles_GlobFilter_LimitsScope()
    {
        File.WriteAllText(Path.Combine(_dataRoot, "a.json"), "needle");
        File.WriteAllText(Path.Combine(_dataRoot, "b.txt"), "needle");

        var result = _service.SearchFiles("needle", null, "*.txt", null);

        var matches = result["data"]!["matches"]!.AsArray();
        await Assert.That(matches).HasCount().EqualTo(1);
        await Assert.That(matches[0]!["path"]?.GetValue<string>()).EndsWith("b.txt");
    }

    [Test]
    public async Task SearchFiles_InvalidRegex_ReturnsError()
    {
        var result = _service.SearchFiles("[unclosed", null, null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid regex");
    }

    [Test]
    public async Task SearchFiles_UnknownRoot_ReturnsError()
    {
        var result = _service.SearchFiles("x", "bogus-root", null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("unknown or missing root");
    }

    [Test]
    public async Task SearchFiles_Limit_Truncates()
    {
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(_dataRoot, $"f{i}.txt"), "needle");

        var result = _service.SearchFiles("needle", null, "*.txt", 2);

        await Assert.That(result["data"]!["matches"]!.AsArray()).HasCount().EqualTo(2);
        await Assert.That(result["data"]!["truncated"]?.GetValue<bool>()).IsTrue();
        await Assert.That(result["provenance"]?["truncated"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task SearchFiles_ScanCap_TruncatesDeterministically()
    {
        // The deterministic scan cap (MaxSearchFilesScanned = 10_000) must
        // bound the walk even when no matches exist: files_scanned stops at
        // the cap and truncated is reported instead of scanning unboundedly.
        for (var i = 0; i < ArchaeologyService.MaxSearchFilesScanned + 1; i++)
            File.WriteAllText(Path.Combine(_dataRoot, $"cap{i:D5}.txt"), "no-match-here");

        var result = _service.SearchFiles("needle", null, "*.txt", null);

        var data = result["data"]!;
        await Assert.That(data["matches"]!.AsArray()).HasCount().EqualTo(0);
        await Assert.That(data["files_scanned"]?.GetValue<int>())
            .IsEqualTo(ArchaeologyService.MaxSearchFilesScanned);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsTrue();
        await Assert.That(result["provenance"]?["truncated"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task SearchFiles_DoesNotEscapeRoot()
    {
        // A file outside the data root must never be searched even when the
        // pattern would match it.
        var outside = Path.Combine(Path.GetTempPath(), "aaemu-arch-out-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(outside, "needle-outside");
        try
        {
            var result = _service.SearchFiles("needle-outside", null, null, null);
            await Assert.That(result["data"]!["matches"]!.AsArray()).HasCount().EqualTo(0);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ------------------------------------------- regression: offset beyond EOF

    [Test]
    public async Task ReadFile_OffsetBeyondEof_ReturnsBoundedEmptyResult()
    {
        var file = Path.Combine(_dataRoot, "short.txt");
        File.WriteAllText(file, "abc");

        var result = _service.ReadFile("AAEmu.Game/Data/short.txt", 100, null);

        // Deterministic bounded result — never a negative allocation or an
        // exception.
        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(result["data"]!["bytes_read"]?.GetValue<int>()).IsEqualTo(0);
        await Assert.That(result["data"]!["content"]?.GetValue<string>()).IsEqualTo(string.Empty);
        await Assert.That(result["data"]!["offset"]?.GetValue<int>()).IsEqualTo(100);
        await Assert.That(result["data"]!["truncated"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task ReadFile_OffsetExactlyAtEof_ReturnsBoundedEmptyResult()
    {
        var file = Path.Combine(_dataRoot, "short2.txt");
        File.WriteAllText(file, "abc");

        var result = _service.ReadFile("AAEmu.Game/Data/short2.txt", 3, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(result["data"]!["bytes_read"]?.GetValue<int>()).IsEqualTo(0);
        await Assert.That(result["data"]!["content"]?.GetValue<string>()).IsEqualTo(string.Empty);
    }

    // ------------------------------------- regression: symlinked parent dirs

    [Test]
    public async Task ReadFile_SymlinkedParentDirEscapingRoot_IsRejected()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "aaemu-arch-outdir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "secret");
        try
        {
            // Symlink the PARENT directory inside the data root; the file
            // itself is a regular file. The guard must reject the escape.
            var linkDir = Path.Combine(_dataRoot, "escape-dir");
            Directory.CreateSymbolicLink(linkDir, outsideDir);

            var result = _service.ReadFile("AAEmu.Game/Data/escape-dir/secret.txt", null, null);
            await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
            await Assert.That(result["error"]?.GetValue<string>()).Contains("not allowed");
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Test]
    public async Task SearchFiles_SymlinkedSubdirEscapingRoot_IsSkipped()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "aaemu-arch-outdir2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "leak.txt"), "needle-leak");
        try
        {
            var linkDir = Path.Combine(_dataRoot, "leak-dir");
            Directory.CreateSymbolicLink(linkDir, outsideDir);

            var result = _service.SearchFiles("needle-leak", null, null, null);
            await Assert.That(result["data"]!["matches"]!.AsArray()).HasCount().EqualTo(0);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Test]
    public async Task SearchFiles_NestedSymlinkedSubdirEscapingRoot_IsSkipped()
    {
        // A symlink nested two levels deep inside the data root must never
        // pull the recursive walk outside the root.
        var outsideDir = Path.Combine(Path.GetTempPath(), "aaemu-arch-outdir3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "leak.txt"), "needle-leak-nested");
        try
        {
            var nested = Path.Combine(_dataRoot, "level1");
            Directory.CreateDirectory(nested);
            var linkDir = Path.Combine(nested, "leak-dir");
            Directory.CreateSymbolicLink(linkDir, outsideDir);

            var result = _service.SearchFiles("needle-leak-nested", null, null, null);
            await Assert.That(result["data"]!["matches"]!.AsArray()).HasCount().EqualTo(0);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    // ------------------------------------- regression: db id allowlisting

    [Test]
    public async Task QuerySql_TraversalDbId_Throws()
    {
        await Assert.That(() => _service.QuerySql("file:../outside.db", "SELECT 1", null, null))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task QuerySql_AbsoluteDbId_Throws()
    {
        await Assert.That(() => _service.QuerySql("file:/etc/passwd", "SELECT 1", null, null))
            .Throws<ArgumentException>();
    }

    // ------------------------------------- regression: Phase-1 gap fixes

    [Test]
    public async Task SearchFiles_TraversalGlob_IsRejected()
    {
        // A glob with a ".." segment must never walk outside the root.
        var result = _service.SearchFiles("needle", null, "../*.txt", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("unsafe glob");
    }

    [Test]
    public async Task SearchFiles_AbsoluteGlob_IsRejected()
    {
        var result = _service.SearchFiles("needle", null, "/etc/*", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("unsafe glob");
    }

    [Test]
    public async Task SearchFiles_RegexTimeout_ReturnsError()
    {
        // Catastrophic backtracking must fail deterministically, never hang.
        File.WriteAllText(Path.Combine(_dataRoot, "long.txt"), new string('a', 40_000) + "!");

        var result = _service.SearchFiles("(a+)+$", null, "*.txt", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("regex timeout");
    }

    [Test]
    public async Task SearchFiles_BinObjPaths_AreSkipped()
    {
        // Build output (bin/obj) must never be exposed by search.
        var binDir = Path.Combine(_repoRoot, "AAEmu.Game", "bin", "Debug");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "generated.cs"), "needle-in-bin");

        var result = _service.SearchFiles("needle-in-bin", "game-source", null, null);

        await Assert.That(result["data"]!["matches"]!.AsArray()).HasCount().EqualTo(0);
    }

    [Test]
    public async Task ListDatabases_SymlinkedDbEscapingRoot_IsSkipped()
    {
        var outside = Path.Combine(Path.GetTempPath(), "aaemu-arch-db-" + Guid.NewGuid().ToString("N")[..8] + ".sqlite3");
        File.WriteAllText(outside, "not a real db");
        try
        {
            var link = Path.Combine(_dataRoot, "escape.sqlite3");
            File.CreateSymbolicLink(link, outside);

            var result = _service.ListDatabases();
            var dbs = result["data"]!["databases"]!.AsArray();
            await Assert.That(dbs).HasCount().EqualTo(1); // only the canonical db
            await Assert.That(dbs[0]!["db_id"]?.GetValue<string>()).IsEqualTo("compact.sqlite3");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public async Task QuerySql_SymlinkedDbEscapingRoot_Throws()
    {
        var outside = Path.Combine(Path.GetTempPath(), "aaemu-arch-db2-" + Guid.NewGuid().ToString("N")[..8] + ".sqlite3");
        File.WriteAllText(outside, "not a real db");
        try
        {
            var link = Path.Combine(_dataRoot, "escape2.sqlite3");
            File.CreateSymbolicLink(link, outside);

            await Assert.That(() => _service.QuerySql("file:escape2.sqlite3", "SELECT 1", null, null))
                .Throws<ArgumentException>();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public async Task QuerySql_Timeout_ReturnsError()
    {
        // The wall-clock deadline is enforced via sqlite3_progress_handler:
        // an unbounded recursive CTE must be interrupted deterministically.
        _service.QueryTimeoutSecondsOverride = 1;
        var result = _service.QuerySql(
            "compact.sqlite3",
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c) SELECT count(*) FROM c",
            null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("timed out");
    }
}
