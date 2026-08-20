using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Hermetic rig for the B4 playerbot_metadata store (M6 deferred gate #5).
/// No MySQL: these tests lock the SQL shapes, the graceful-degradation
/// contract (no DB → empty reads, mutations cached + left dirty, never a
/// throw), the dirty-set semantics around SaveDirty, and the coordinator's
/// ResolveHome precedence. The live round-trip (rows surviving a real
/// restart) rides the B4 E2E — see B4BotRestartPersistenceE2eTests.
///
/// Each test uses its own character id: the store is a process-wide
/// singleton whose cache/dirty sets are shared across the suite.
/// </summary>
public class PlayerBotMetadataStoreTests
{
    // ---------------------------------------------------------------- SQL shapes

    [Test]
    public async Task UpsertSql_TargetsPlayerBotMetadataWithReplaceInto()
    {
        var sql = PlayerBotMetadataStore.BuildUpsertSql();

        await Assert.That(sql).Contains("REPLACE INTO");
        await Assert.That(sql).Contains("playerbot_metadata");
        await Assert.That(sql).Contains("@character_id");
        await Assert.That(sql).Contains("@personality");
        await Assert.That(sql).Contains("@profession");
        await Assert.That(sql).Contains("@has_home");
        await Assert.That(sql).Contains("@home_world_id");
        await Assert.That(sql).Contains("@home_zone_id");
        await Assert.That(sql).Contains("@home_x");
        await Assert.That(sql).Contains("@home_y");
        await Assert.That(sql).Contains("@home_z");
        await Assert.That(sql).Contains("@schedule");
        await Assert.That(sql).Contains("@behavior_config");
        await Assert.That(sql).Contains("@planner_state");
    }

    [Test]
    public async Task SelectSql_TargetsPlayerBotMetadataByCharacterId()
    {
        var sql = PlayerBotMetadataStore.BuildSelectSql();

        await Assert.That(sql).Contains("FROM `playerbot_metadata`");
        await Assert.That(sql).Contains("WHERE `character_id` = @character_id");
        await Assert.That(sql).Contains("`schedule`");
        await Assert.That(sql).Contains("`planner_state`");
    }

    [Test]
    public async Task EnsureSchemaSql_CreatesPlayerBotMetadataIfMissing()
    {
        var sql = PlayerBotMetadataStore.BuildEnsureSchemaSql();

        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS");
        await Assert.That(sql).Contains("playerbot_metadata");
        await Assert.That(sql).Contains("`character_id`");
        await Assert.That(sql).Contains("`schedule`");
    }

    [Test]
    public async Task EnsureSchemaCheckSql_ProbesInformationSchemaTables()
    {
        var sql = PlayerBotMetadataStore.BuildEnsureSchemaCheckSql();

        await Assert.That(sql).Contains("information_schema.TABLES");
        await Assert.That(sql).Contains("aaemu_game");
        await Assert.That(sql).Contains("playerbot_metadata");
    }

    // ---------------------------------------------------------------- graceful degradation (no MySQL in the gate)

    [Test]
    public async Task GetForRead_WithoutMySql_ReturnsEmptyGracefully()
    {
        // No MySQL configured in the hermetic gate: the read must degrade to
        // Empty (never throw) — the caller then falls back to its own
        // defaults (template-spawn home, no schedule).
        var metadata = PlayerBotMetadataStore.Instance.GetForRead(990000001u);

        await Assert.That(metadata).IsNotNull();
        await Assert.That(metadata.CharacterId).IsEqualTo(990000001u);
        await Assert.That(metadata.HasHome).IsFalse();
        await Assert.That(metadata.Schedule).IsEqualTo(string.Empty);
        await Assert.That(metadata.Personality).IsEqualTo(string.Empty);
        await Assert.That(metadata.PlannerState).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RecordHome_WithoutMySql_DoesNotThrow_UpdatesCache_LeavesRowDirty()
    {
        var store = PlayerBotMetadataStore.Instance;

        store.RecordHome(990000002u, 1u, 283u, 19950f, 20050f, 100f);

        // Write-through failed (no DB) → the row stays dirty for the next
        // SaveManager tick, and the CACHE still serves the mutation.
        await Assert.That(store.IsDirty(990000002u)).IsTrue();
        var metadata = store.GetForRead(990000002u);
        await Assert.That(metadata.HasHome).IsTrue();
        await Assert.That(metadata.HomeWorldId).IsEqualTo(1u);
        await Assert.That(metadata.HomeZoneId).IsEqualTo(283u);
        await Assert.That(metadata.HomeX).IsEqualTo(19950f);
        await Assert.That(metadata.HomeY).IsEqualTo(20050f);
        await Assert.That(metadata.HomeZ).IsEqualTo(100f);
    }

    [Test]
    public async Task RecordHome_SecondCall_UpdatesCachedValues()
    {
        var store = PlayerBotMetadataStore.Instance;

        store.RecordHome(990000003u, 1u, 283u, 1f, 2f, 3f);
        store.RecordHome(990000003u, 2u, 179u, 10f, 20f, 30f);

        var metadata = store.GetForRead(990000003u);
        await Assert.That(metadata.HomeWorldId).IsEqualTo(2u);
        await Assert.That(metadata.HomeZoneId).IsEqualTo(179u);
        await Assert.That(metadata.HomeX).IsEqualTo(10f);
        await Assert.That(metadata.HomeY).IsEqualTo(20f);
        await Assert.That(metadata.HomeZ).IsEqualTo(30f);
        await Assert.That(store.IsDirty(990000003u)).IsTrue();
    }

    [Test]
    public async Task RecordSchedule_WithoutMySql_StoresJson_LeavesRowDirty()
    {
        var store = PlayerBotMetadataStore.Instance;
        const string json = "{\"kind\":\"roam-loop\",\"waypoints\":8,\"radius\":30,\"phase\":0,\"loop\":true}";

        store.RecordSchedule(990000004u, json);

        await Assert.That(store.GetForRead(990000004u).Schedule).IsEqualTo(json);
        await Assert.That(store.IsDirty(990000004u)).IsTrue();
    }

    [Test]
    public async Task RecordFields_WithoutMySql_UpdateCache_LeaveRowDirty()
    {
        var store = PlayerBotMetadataStore.Instance;

        store.RecordPersonality(990000005u, "cheerful");
        store.RecordProfession(990000005u, "farmer");
        store.RecordBehaviorConfig(990000005u, "{\"greet\":true}");
        store.RecordPlannerState(990000005u, "{\"goal\":\"idle\"}");

        var metadata = store.GetForRead(990000005u);
        await Assert.That(metadata.Personality).IsEqualTo("cheerful");
        await Assert.That(metadata.Profession).IsEqualTo("farmer");
        await Assert.That(metadata.BehaviorConfig).IsEqualTo("{\"greet\":true}");
        await Assert.That(metadata.PlannerState).IsEqualTo("{\"goal\":\"idle\"}");
        await Assert.That(store.IsDirty(990000005u)).IsTrue();
    }

    // ---------------------------------------------------------------- dirty semantics

    [Test]
    public async Task SaveDirty_NullConnection_DoesNotThrow_KeepsRowDirty()
    {
        var store = PlayerBotMetadataStore.Instance;
        store.RecordHome(990000006u, 1u, 283u, 5f, 6f, 7f);
        await Assert.That(store.IsDirty(990000006u)).IsTrue();

        // A broken/absent ambient connection must never break SaveManager —
        // and must NOT clear the dirty flag (the row was never persisted).
        store.SaveDirty(null!, null!);

        await Assert.That(store.IsDirty(990000006u)).IsTrue();
    }

    // ---------------------------------------------------------------- ResolveHome precedence

    [Test]
    public async Task ResolveHome_ExplicitHome_WinsOverStoredAndTemplate()
    {
        var explicitHome = new Vector3(19950f, 20050f, 100f);
        var stored = new PlayerBotMetadata
        {
            CharacterId = 1u, HasHome = true, HomeX = 1f, HomeY = 2f, HomeZ = 3f
        };
        var template = new Vector3(15578f, 15382f, 126f);

        var home = BotPresenceCoordinator.ResolveHome(explicitHome, stored, template);

        await Assert.That(home).IsEqualTo(explicitHome);
    }

    [Test]
    public async Task ResolveHome_NoExplicit_StoredHomeWinsOverTemplate()
    {
        var stored = new PlayerBotMetadata
        {
            CharacterId = 1u, HasHome = true, HomeX = 19950f, HomeY = 20050f, HomeZ = 100f
        };
        var template = new Vector3(15578f, 15382f, 126f);

        var home = BotPresenceCoordinator.ResolveHome(default, stored, template);

        await Assert.That(home).IsEqualTo(new Vector3(19950f, 20050f, 100f));
    }

    [Test]
    public async Task ResolveHome_NoExplicitNoStoredHome_FallsBackToTemplate()
    {
        var template = new Vector3(15578f, 15382f, 126f);

        // No stored row at all (Empty: HasHome = false).
        var home = BotPresenceCoordinator.ResolveHome(default, PlayerBotMetadata.Empty(1u), template);

        await Assert.That(home).IsEqualTo(template);
    }

    [Test]
    public async Task ResolveHome_StoredWithoutHome_FallsThroughToTemplate()
    {
        // A stored row with HasHome = false (e.g. only a schedule recorded)
        // must NOT shadow the template default.
        var stored = new PlayerBotMetadata
        {
            CharacterId = 1u, HasHome = false, HomeX = 9f, HomeY = 9f, HomeZ = 9f, Schedule = "{}"
        };
        var template = new Vector3(15578f, 15382f, 126f);

        var home = BotPresenceCoordinator.ResolveHome(default, stored, template);

        await Assert.That(home).IsEqualTo(template);
    }

    // ---------------------------------------------------------------- schedule payload

    [Test]
    public async Task BuildRoamScheduleJson_IsDeterministicRoamLoopDescriptor()
    {
        var home = new Vector3(19950f, 20050f, 100f);
        var route = BotPresenceCoordinator.BuildRoamRoute(home, 30f, seed: 1,
            groundHeightProvider: (_, _) => 0f);

        var json = BotPresenceCoordinator.BuildRoamScheduleJson(home, route, 30f, phase: 1);
        var jsonAgain = BotPresenceCoordinator.BuildRoamScheduleJson(home,
            BotPresenceCoordinator.BuildRoamRoute(home, 30f, seed: 1, groundHeightProvider: (_, _) => 0f), 30f, phase: 1);

        await Assert.That(json).IsEqualTo(jsonAgain); // restart re-arm equality
        await Assert.That(json).Contains("\"kind\":\"roam-loop\"");
        await Assert.That(json).Contains("\"waypoints\":8");
        await Assert.That(json).Contains("\"radius\":30");
        await Assert.That(json).Contains("\"phase\":1");
        await Assert.That(json).Contains("\"loop\":true");
        await Assert.That(json).Contains("\"home\":[19950,20050,100]");
    }
}
