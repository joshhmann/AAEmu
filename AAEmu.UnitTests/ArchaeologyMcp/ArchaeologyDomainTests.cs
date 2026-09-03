using AAEmu.ArchaeologyMcp;
using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.ArchaeologyMcp;

/// <summary>
/// Defends the domain archaeology tools (search_everything,
/// trace_references, find_quest_objectives, and the schema-backed wrappers)
/// against a real temp SQLite DB with quest/skill tables plus temp files:
/// bounded search, exact-vs-heuristic trace evidence, quest-objective family
/// discovery, honest unsupported results, and provenance metadata.
/// </summary>
[NotInParallel]
public class ArchaeologyDomainTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _dataRoot;
    private readonly string _dbPath;
    private readonly SourceCatalog _catalog;
    private readonly ArchaeologyService _service;

    public ArchaeologyDomainTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "aaemu-arch-dom-" + Guid.NewGuid().ToString("N")[..8]);
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
                CREATE TABLE skills (id INTEGER PRIMARY KEY, name TEXT, desc TEXT);
                INSERT INTO skills (id, name, desc) VALUES (100, 'Fireball', 'A fiery projectile');
                CREATE TABLE quest_contexts (id INTEGER PRIMARY KEY, name TEXT, LEVEL INTEGER);
                INSERT INTO quest_contexts (id, name, LEVEL) VALUES (1421, 'The Burning Burrow', 10);
                CREATE TABLE quest_components (id INTEGER PRIMARY KEY, quest_context_id INTEGER, component_kind_id INTEGER);
                INSERT INTO quest_components (id, quest_context_id, component_kind_id) VALUES (7243, 1421, 1);
                CREATE TABLE quest_acts (id INTEGER PRIMARY KEY, quest_component_id INTEGER, act_detail_id INTEGER, act_detail_type TEXT);
                INSERT INTO quest_acts (id, quest_component_id, act_detail_id, act_detail_type) VALUES (6306, 7243, 747, 'QuestActObjTalk');
                CREATE TABLE quest_act_obj_talks (id INTEGER PRIMARY KEY, npc_id INTEGER, item_id INTEGER, quest_act_obj_alias_id INTEGER);
                INSERT INTO quest_act_obj_talks (id, npc_id, item_id, quest_act_obj_alias_id) VALUES (747, 1, 29040, 1);
                CREATE TABLE quest_act_obj_aliases (id INTEGER PRIMARY KEY, name TEXT);
                INSERT INTO quest_act_obj_aliases (id, name) VALUES (1, 'Burn the burrow');
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

    // ------------------------------------------------------ search_everything

    [Test]
    public async Task SearchEverything_FindsDbAndFileHits()
    {
        File.WriteAllText(Path.Combine(_dataRoot, "notes.md"), "Patrashu bread is a quest item\n");

        var result = _service.SearchEverything("Patrashu", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var data = result["data"]!;
        var dbHits = data["db_hits"]!.AsArray();
        await Assert.That(dbHits).HasCount().EqualTo(1);
        await Assert.That(dbHits[0]!["table"]?.GetValue<string>()).IsEqualTo("items");
        await Assert.That(dbHits[0]!["column"]?.GetValue<string>()).IsEqualTo("name");
        await Assert.That(dbHits[0]!["id"]?.GetValue<long>()).IsEqualTo(29040);
        await Assert.That(data["file_matches"]!.AsArray()).HasCount().EqualTo(1);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("search_everything");
        await Assert.That(result["provenance"]?["source_id"]?.GetValue<string>()).IsEqualTo("compact.sqlite3");
    }

    [Test]
    public async Task SearchEverything_Limit_Truncates()
    {
        var result = _service.SearchEverything("Guard", 1);

        var data = result["data"]!;
        await Assert.That(data["hit_count"]?.GetValue<int>()).IsEqualTo(1);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsTrue();
        await Assert.That(result["provenance"]?["truncated"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task SearchEverything_EmptyTerm_ReturnsError()
    {
        var result = _service.SearchEverything("", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("term is required");
    }

    [Test]
    public async Task SearchEverything_Timeout_ReturnsError()
    {
        // A large text table forces a full LIKE scan; the native
        // progress-handler deadline must interrupt it deterministically
        // instead of hanging or throwing an uncaught exception.
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE big (id INTEGER PRIMARY KEY, txt TEXT);
                INSERT INTO big SELECT x, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
                FROM (WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x < 15000000) SELECT x FROM c);
                """;
            command.ExecuteNonQuery();
        }

        _service.QueryTimeoutSecondsOverride = 1;
        var result = _service.SearchEverything("zzzz-no-match", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("timed out");
    }

    [Test]
    public async Task SearchEverything_SymlinkedSubdirEscapingRoot_IsSkipped()
    {
        // A symlinked subdirectory inside the data root must never pull the
        // file scan outside the requested root.
        var outsideDir = Path.Combine(Path.GetTempPath(), "aaemu-arch-dom-out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "leak.txt"), "needle-leak");
        try
        {
            var linkDir = Path.Combine(_dataRoot, "leak-dir");
            Directory.CreateSymbolicLink(linkDir, outsideDir);

            var result = _service.SearchEverything("needle-leak", null);

            await Assert.That(result["data"]!["file_matches"]!.AsArray()).HasCount().EqualTo(0);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    // ------------------------------------------------------ trace_references

    [Test]
    public async Task TraceReferences_ExactId_SeedsAndExpands()
    {
        var result = _service.TraceReferences("29040", null, null, 2, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var nodes = result["data"]!["nodes"]!.AsArray();
        var itemNode = nodes.First(n => n!["table"]?.GetValue<string>() == "items"
                                        && n["id"]?.GetValue<long>() == 29040);
        await Assert.That(itemNode["evidence"]?.GetValue<string>()).IsEqualTo("exact");
        await Assert.That(itemNode["depth"]?.GetValue<int>()).IsEqualTo(0);
        // quest_act_obj_talks.item_id is a heuristic *_id edge to items.
        var talkNode = nodes.FirstOrDefault(n => n!["table"]?.GetValue<string>() == "quest_act_obj_talks");
        await Assert.That(talkNode).IsNotNull();
        await Assert.That(talkNode!["evidence"]?.GetValue<string>()).IsEqualTo("heuristic");
        await Assert.That(talkNode["via"]?.GetValue<string>()).Contains("item_id");
    }

    [Test]
    public async Task TraceReferences_TextualMatch_IsLabeledTextual()
    {
        var result = _service.TraceReferences("Guard Captain", null, null, 1, null);

        var nodes = result["data"]!["nodes"]!.AsArray();
        var npcNode = nodes.First(n => n!["table"]?.GetValue<string>() == "npcs"
                                       && n["id"]?.GetValue<long>() == 3);
        await Assert.That(npcNode["evidence"]?.GetValue<string>()).IsEqualTo("textual");
    }

    [Test]
    public async Task TraceReferences_DepthCap_IsRespected()
    {
        var result = _service.TraceReferences("29040", null, null, 1, null);

        var depths = result["data"]!["nodes"]!.AsArray()
            .Select(n => n!["depth"]?.GetValue<int>()).ToArray();
        await Assert.That(depths.All(d => d <= 1)).IsTrue();
    }

    [Test]
    public async Task TraceReferences_TableFilter_ScopesSeeds()
    {
        var result = _service.TraceReferences("29040", null, "items", 2, null);

        var tables = result["data"]!["nodes"]!.AsArray()
            .Select(n => n!["table"]?.GetValue<string>()).Distinct().ToArray();
        await Assert.That(tables).Contains("items");
    }

    [Test]
    public async Task TraceReferences_TableWithoutIdColumn_DoesNotCrash()
    {
        // A table with no "id" column (declared PK is a differently-named
        // column) must be traced via its actual key column; a table with no
        // single key column at all must be skipped, never crash with
        // "no such column: id".
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE zones (zone_key INTEGER PRIMARY KEY, name TEXT);
                INSERT INTO zones (zone_key, name) VALUES (260, 'ArcheMall');
                CREATE TABLE zone_spawns (id INTEGER PRIMARY KEY, zone_id INTEGER, label TEXT);
                INSERT INTO zone_spawns (id, zone_id, label) VALUES (1, 260, 'mall spawn');
                CREATE TABLE schema_migrations (version TEXT, applied_at TEXT);
                INSERT INTO schema_migrations (version, applied_at) VALUES ('001', '2026-01-01');
                """;
            command.ExecuteNonQuery();
        }

        // Seed on the no-id table's PK column (zone_key) and expand through
        // the heuristic *_id edge to zone_spawns.
        var result = _service.TraceReferences("260", null, "zones", 2, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var nodes = result["data"]!["nodes"]!.AsArray();
        var zoneNode = nodes.First(n => n!["table"]?.GetValue<string>() == "zones"
                                        && n["id"]?.GetValue<long>() == 260);
        await Assert.That(zoneNode["evidence"]?.GetValue<string>()).IsEqualTo("exact");
        var spawnNode = nodes.FirstOrDefault(n => n!["table"]?.GetValue<string>() == "zone_spawns");
        await Assert.That(spawnNode).IsNotNull();
        await Assert.That(spawnNode!["evidence"]?.GetValue<string>()).IsEqualTo("heuristic");

        // A table with no single key column must be skipped without crashing.
        var noKeyResult = _service.TraceReferences("001", null, "schema_migrations", 2, null);
        await Assert.That(noKeyResult["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(noKeyResult["data"]!["node_count"]?.GetValue<int>()).IsEqualTo(0);
    }

    [Test]
    public async Task TraceReferences_Timeout_ReturnsError()
    {
        // The seed scan runs exact-match scalar queries per text column; a
        // large text table forces a full scan that the progress-handler
        // deadline must interrupt deterministically (SQLITE_INTERRUPT mapped
        // to a timeout error by RunDomain) instead of hanging.
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE big (id INTEGER PRIMARY KEY, txt TEXT);
                INSERT INTO big SELECT x, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
                FROM (WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x < 20000000) SELECT x FROM c);
                """;
            command.ExecuteNonQuery();
        }

        _service.QueryTimeoutSecondsOverride = 1;
        var result = _service.TraceReferences("zzzz-no-match", null, "big", 1, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("timed out");
    }

    // --------------------------------------------------- find_quest_objectives

    [Test]
    public async Task FindQuestObjectives_DiscoversFamilyRows()
    {
        var result = _service.FindQuestObjectives(null, null, null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        var rows = data["rows"]!.AsArray();
        await Assert.That(rows).HasCount().EqualTo(1);
        await Assert.That(rows[0]!["family"]?.GetValue<string>()).IsEqualTo("quest_act_obj_talks");
        await Assert.That(rows[0]!["act_detail_type"]?.GetValue<string>()).IsEqualTo("QuestActObjTalk");
        await Assert.That(rows[0]!["quest_context_id"]?.GetValue<long>()).IsEqualTo(1421);
        await Assert.That(rows[0]!["quest_name"]?.GetValue<string>()).IsEqualTo("The Burning Burrow");
        await Assert.That(rows[0]!["npc_id"]?.GetValue<long>()).IsEqualTo(1);
        await Assert.That(data["evidence"]?.GetValue<string>()).IsEqualTo("heuristic");
    }

    [Test]
    public async Task FindQuestObjectives_QuestIdFilter_ReturnsOnlyThatQuest()
    {
        var result = _service.FindQuestObjectives(1421, null, null, null);

        var rows = result["data"]!["rows"]!.AsArray();
        await Assert.That(rows).HasCount().EqualTo(1);
        await Assert.That(rows[0]!["quest_context_id"]?.GetValue<long>()).IsEqualTo(1421);
    }

    [Test]
    public async Task FindQuestObjectives_ObjectiveIdFilter_ReturnsOnlyThatObjective()
    {
        var result = _service.FindQuestObjectives(null, 747, null, null);

        var rows = result["data"]!["rows"]!.AsArray();
        await Assert.That(rows).HasCount().EqualTo(1);
        await Assert.That(rows[0]!["act_detail_id"]?.GetValue<long>()).IsEqualTo(747);
    }

    [Test]
    public async Task FindQuestObjectives_UnknownFamily_ReturnsEmpty()
    {
        var result = _service.FindQuestObjectives(null, null, "monster_hunts", null);

        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["row_count"]?.GetValue<int>()).IsEqualTo(0);
        await Assert.That(data["reason"]?.GetValue<string>()).Contains("no quest_act_obj_* table matches");
    }

    // ------------------------------------------------------------- wrappers

    [Test]
    public async Task TraceItem_ById_ReturnsRow()
    {
        var result = _service.TraceItem(29040, null, null);

        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["rows"]!.AsArray()).HasCount().EqualTo(1);
        await Assert.That(data["rows"]![0]!["name"]?.GetValue<string>()).IsEqualTo("Patrashu Bread");
        await Assert.That(data["evidence"]?.GetValue<string>()).IsEqualTo("exact");
    }

    [Test]
    public async Task TraceItem_ByName_ReturnsRow()
    {
        var result = _service.TraceItem(null, "bread", null);

        var data = result["data"]!;
        await Assert.That(data["rows"]!.AsArray()).HasCount().EqualTo(1);
        await Assert.That(data["evidence"]?.GetValue<string>()).IsEqualTo("textual");
    }

    [Test]
    public async Task TraceSkill_ById_ReturnsRow()
    {
        var result = _service.TraceSkill(100, null, null);

        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["rows"]![0]!["name"]?.GetValue<string>()).IsEqualTo("Fireball");
    }

    [Test]
    public async Task TraceQuest_ById_IncludesComponents()
    {
        var result = _service.TraceQuest(1421, null, null);

        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["rows"]![0]!["name"]?.GetValue<string>()).IsEqualTo("The Burning Burrow");
        var components = data["components"]!.AsArray();
        await Assert.That(components).HasCount().EqualTo(1);
        await Assert.That(components[0]!["id"]?.GetValue<long>()).IsEqualTo(7243);
        await Assert.That(data["act_count"]?.GetValue<long>()).IsEqualTo(1);
    }

    [Test]
    public async Task TraceMate_MissingTable_ReturnsUnsupported()
    {
        var result = _service.TraceMate(1, null, null);

        var data = result["data"]!;
        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["supported"]?.GetValue<bool>()).IsFalse();
        await Assert.That(data["reason"]?.GetValue<string>()).Contains("not present");
    }

    [Test]
    public async Task TraceVehicle_MissingTable_ReturnsUnsupported()
    {
        var result = _service.TraceVehicle(1, null);

        await Assert.That(result["data"]!["supported"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task TraceDoodad_MissingTable_ReturnsUnsupported()
    {
        var result = _service.TraceDoodad(1, null, null);

        await Assert.That(result["data"]!["supported"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task TraceCrafting_MissingTable_ReturnsUnsupported()
    {
        var result = _service.TraceCrafting(1, null, null);

        await Assert.That(result["data"]!["supported"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task TraceWorldSpawn_ReadsSpawnFileAndSpawners()
    {
        var worldsDir = Path.Combine(_dataRoot, "Worlds");
        Directory.CreateDirectory(worldsDir);
        File.WriteAllText(Path.Combine(worldsDir, "world_spawns.json"),
            """[{"Name":"arche_mall_world","SpawnPosition":{"ZoneId":260,"X":1.0,"Y":2.0,"Z":3.0,"Yaw":4.0}}]""");

        var result = _service.TraceWorldSpawn("arche_mall", null, null);

        var data = result["data"]!;
        await Assert.That(data["spawns"]!.AsArray()).HasCount().EqualTo(1);
        await Assert.That(data["spawns"]![0]!["name"]?.GetValue<string>()).IsEqualTo("arche_mall_world");
        await Assert.That(data["spawns"]![0]!["zone_id"]?.GetValue<int>()).IsEqualTo(260);
        await Assert.That(data["spawns"]![0]!["file"]?.GetValue<string>()).EndsWith("world_spawns.json");
        await Assert.That(data["files_matched"]!.AsArray()).HasCount().EqualTo(1);
        await Assert.That(data["files_matched"]![0]!.GetValue<string>()).EndsWith("world_spawns.json");
        await Assert.That(data["no_match"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task TraceWorldSpawn_ScansAllWorldsJsonFiles()
    {
        var worldsDir = Path.Combine(_dataRoot, "Worlds");
        Directory.CreateDirectory(worldsDir);
        File.WriteAllText(Path.Combine(worldsDir, "world_spawns.json"),
            """[{"Name":"arche_mall_world","SpawnPosition":{"ZoneId":260,"X":1.0,"Y":2.0,"Z":3.0,"Yaw":4.0}}]""");
        File.WriteAllText(Path.Combine(worldsDir, "instance_burntcastle.json"),
            """[{"Name":"instance_burntcastle_armory","SpawnPosition":{"ZoneId":236,"X":170.6,"Y":98.7,"Z":0.0,"Yaw":0.0}}]""");

        var result = _service.TraceWorldSpawn("burntcastle", null, null);

        var data = result["data"]!;
        var spawns = data["spawns"]!.AsArray();
        await Assert.That(spawns).HasCount().EqualTo(1);
        await Assert.That(spawns[0]!["name"]?.GetValue<string>()).IsEqualTo("instance_burntcastle_armory");
        await Assert.That(spawns[0]!["file"]?.GetValue<string>()).EndsWith("instance_burntcastle.json");
        await Assert.That(data["files_matched"]!.AsArray()).HasCount().EqualTo(1);
        await Assert.That(data["files_scanned"]?.GetValue<int>()).IsEqualTo(2);
    }

    [Test]
    public async Task TraceWorldSpawn_NoMatch_ReportsExplicitly()
    {
        var worldsDir = Path.Combine(_dataRoot, "Worlds");
        Directory.CreateDirectory(worldsDir);
        File.WriteAllText(Path.Combine(worldsDir, "world_spawns.json"),
            """[{"Name":"arche_mall_world","SpawnPosition":{"ZoneId":260,"X":1.0,"Y":2.0,"Z":3.0,"Yaw":4.0}}]""");

        var result = _service.TraceWorldSpawn("nonexistent_place", null, null);

        var data = result["data"]!;
        await Assert.That(data["spawns"]!.AsArray()).HasCount().EqualTo(0);
        await Assert.That(data["no_match"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task TraceWorldSpawn_CommentTolerantJson_IsParsed()
    {
        // The canonical world_spawns.json contains "//" line comments; the
        // scan must strip them (outside string literals) and still find
        // named spawns.
        var worldsDir = Path.Combine(_dataRoot, "Worlds");
        Directory.CreateDirectory(worldsDir);
        File.WriteAllText(Path.Combine(worldsDir, "world_spawns.json"),
            "[{\"Name\":\"arche_mall_world\", // main mall\n  \"SpawnPosition\":{\"ZoneId\":260,\"X\":1.0,\"Y\":2.0,\"Z\":3.0,\"Yaw\":4.0}}]");

        var result = _service.TraceWorldSpawn("arche_mall", null, null);

        var data = result["data"]!;
        var spawns = data["spawns"]!.AsArray();
        await Assert.That(spawns).HasCount().EqualTo(1);
        await Assert.That(spawns[0]!["name"]?.GetValue<string>()).IsEqualTo("arche_mall_world");
        await Assert.That(spawns[0]!["zone_id"]?.GetValue<int>()).IsEqualTo(260);
    }

    [Test]
    public async Task TraceWorldSpawn_NpcSpawnerNumericName_DoesNotOvermatch()
    {
        // A numeric name filter must not overmatch via raw substring: the
        // spawner named "13554" contains "3554" but must not be returned
        // for name=3554.
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE npc_spawners (id INTEGER PRIMARY KEY, name TEXT, npc_spawner_category_id INTEGER, maxPopulation INTEGER);
                INSERT INTO npc_spawners (id, name, npc_spawner_category_id, maxPopulation) VALUES (1, '13554', 1, 5);
                INSERT INTO npc_spawners (id, name, npc_spawner_category_id, maxPopulation) VALUES (2, '3554', 1, 5);
                INSERT INTO npc_spawners (id, name, npc_spawner_category_id, maxPopulation) VALUES (3, 'zone 3554 spawn', 1, 5);
                """;
            command.ExecuteNonQuery();
        }

        var result = _service.TraceWorldSpawn("3554", null, null);

        var data = result["data"]!;
        var spawners = data["npc_spawners"]!.AsArray();
        await Assert.That(spawners).HasCount().EqualTo(2);
        var names = spawners.Select(s => s!["name"]?.GetValue<string>()).ToArray();
        await Assert.That(names).DoesNotContain("13554");
        await Assert.That(names).Contains("3554");
        await Assert.That(names).Contains("zone 3554 spawn");
        await Assert.That(data["no_match"]?.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task SearchPhysics_NoTables_ReturnsUnsupported()
    {
        var result = _service.SearchPhysics(null, null);

        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsFalse();
        await Assert.That(data["reason"]?.GetValue<string>()).Contains("no physical_* tables");
    }

    [Test]
    public async Task CompareSourceData_ComparesCanonicalAndCopy()
    {
        var copyPath = Path.Combine(_dataRoot, "copy.sqlite3");
        File.Copy(_dbPath, copyPath);

        var result = _service.CompareSourceData("npcs", "file:copy.sqlite3", null);

        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["canonical_row_count"]?.GetValue<long>()).IsEqualTo(3);
        await Assert.That(data["other_row_count"]?.GetValue<long>()).IsEqualTo(3);
        await Assert.That(data["row_counts_match"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["sample_identical"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task CompareSourceData_CanonicalDbId_ReturnsError()
    {
        var result = _service.CompareSourceData("npcs", "compact.sqlite3", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("file:<name>");
    }

    [Test]
    public async Task CompareSourceData_MissingTable_ReturnsUnsupported()
    {
        var result = _service.CompareSourceData("bogus_table", "file:nope.sqlite3", null);

        await Assert.That(result["data"]!["supported"]?.GetValue<bool>()).IsFalse();
    }

    // ------------------------------------------------------------- lookup_row

    [Test]
    public async Task LookupRow_ById_ReturnsRowWithProvenance()
    {
        var result = _service.LookupRow("compact.sqlite3", "npcs", 1);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["table"]?.GetValue<string>()).IsEqualTo("npcs");
        await Assert.That(data["id"]?.GetValue<long>()).IsEqualTo(1);
        await Assert.That(data["rows"]!.AsArray()).HasCount().EqualTo(1);
        await Assert.That(data["rows"]![0]!["name"]?.GetValue<string>()).IsEqualTo("Guard");
        await Assert.That(data["row_count"]?.GetValue<int>()).IsEqualTo(1);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("lookup_row");
        await Assert.That(result["provenance"]?["source_id"]?.GetValue<string>()).IsEqualTo("compact.sqlite3");
        await Assert.That(result["provenance"]?["version"]?.GetValue<string>()).IsEqualTo("test-version");
    }

    [Test]
    public async Task LookupRow_MissingId_ReturnsEmptyRows()
    {
        var result = _service.LookupRow("compact.sqlite3", "npcs", 9999);

        var data = result["data"]!;
        await Assert.That(data["supported"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["rows"]!.AsArray()).HasCount().EqualTo(0);
        await Assert.That(data["row_count"]?.GetValue<int>()).IsEqualTo(0);
    }

    [Test]
    public async Task LookupRow_UnknownTable_ReturnsUnsupported()
    {
        var result = _service.LookupRow("compact.sqlite3", "bogus_table", 1);

        var data = result["data"]!;
        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["supported"]?.GetValue<bool>()).IsFalse();
        await Assert.That(data["reason"]?.GetValue<string>()).Contains("not present");
    }

    [Test]
    public async Task LookupRow_InvalidTableName_ReturnsError()
    {
        var result = _service.LookupRow("compact.sqlite3", "npcs; DROP TABLE npcs", 1);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid table name");
    }

    [Test]
    public async Task LookupRow_UnknownDb_ReturnsError()
    {
        var result = _service.LookupRow("bogus", "npcs", 1);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("unknown database id");
    }
}
