using System.Globalization;
using AAEmu.IntegrationTests.E2e;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests;

/// <summary>
/// M4-3 (t_4a91a4f5) vehicle restart-recovery E2E — the slaves-row round trip,
/// the dossier's §8 restart contract driven on the REAL stack (MySQL + login +
/// game binaries):
///
///   Cycle 1: seed a summoned+ridden rowboat (slave template 15, driver
///     attach_point = 1) exactly as the DB holds it while the vehicle is out,
///     with distinctive HP/MP/position. Restart the game server. Assert the
///     row survives byte-intact (item binding, owner/summoner, attachment,
///     HP/MP, position) and the summoner has exactly ONE row (no duplication,
///     no boot-time respawn creating a second row).
///   Cycle 2: mutate the row the way a post-combat save would (HP/MP changed,
///     position moved). Restart again. Assert the new values survived and the
///     count is still exactly 1.
///
/// Unit-side (SlaveLifecycleTests) covers: no in-world ghost slaves after a
/// fresh manager, Save() firing on the despawn/disconnect lifecycle events,
/// and the canonical "re-summon from the item" recovery path.
/// </summary>
[Collection("e2e")]
public class M4VehiclesE2eTests
{
    private const uint AccountId = 910001;
    private const uint CharId = 910001;
    private const uint SlaveRowId = 910002;
    private const uint SummonItemId = 910003;
    private const uint RowboatTemplateId = 15;          // slaves.template_id — rowboat (compact.sqlite3)
    private const uint RowboatScrollTemplateId = 17863; // 솔즈리드 나룻배 소환 주문서 (summon scroll item)

    // Attached-pack-on-slave restart (M5.1 gap flag t_1b82b33f): a trade pack
    // loaded onto a slave cargo point via PackVehicleService →
    // SlaveManager.AttachDoodadAtPoint must survive kill -9. Seed ids are
    // clear of the rowboat test above (910010+).
    private const uint WagonAccountId = 910010;
    private const uint WagonCharId = 910010;
    private const uint WagonSlaveRowId = 910011;
    private const uint WagonSummonItemId = 910012;
    private const uint AttachedPackDoodadDbId = 910013;
    private const ulong AttachedPackItemId = 910014;
    private const uint AttachedPackContainerId = 910015;
    private const uint FarmWagonTemplateId = 60;        // slaves.template_id — Farm Wagon (model 1008, cargo points 9-12)
    private const uint FarmWagonScrollTemplateId = 18660; // item_summon_slaves: 18660 → slave 60 (farm wagon summon scroll)
    private const uint PackItemTemplateId = 26488;      // 황금 평원 마취제 (trade pack)
    private const uint PlacedPackDoodadTemplateId = 6068;
    private const uint PlacedPackStartPhaseId = 15677;
    // Canonical local snap of Farm Wagon model 1008 attach point 9 (cargo
    // point Cannon0) — the value ApplyAttachPointLocation writes and the
    // restart must preserve.
    private const float CargoSnapX = -0.55f;
    private const float CargoSnapY = -2.0f;
    private const float CargoSnapZ = 1.15f;

    private static void EnsureStack() => E2eStack.EnsureUp();

    [Fact]
    [Trait("Category", "e2e")]
    public void M4Vehicles_RowboatOutAndRidden_RestartTwice_RowIntactNoDup()
    {
        EnsureStack();

        using (var login = E2eStack.OpenDb("aaemu_login"))
        using (var cmd = login.CreateCommand())
        {
            cmd.CommandText = "INSERT IGNORE INTO users (id, username, password, email, last_ip) VALUES (@id, @name, '', '', '')";
            cmd.Parameters.AddWithValue("@id", AccountId);
            cmd.Parameters.AddWithValue("@name", "m4_vehicle_owner");
            cmd.ExecuteNonQuery();
        }

        using (var game = E2eStack.OpenDb("aaemu_game"))
        {
            InsertCharacter(game, CharId, AccountId, "m4_vehicle_owner");
            InsertSummonItem(game, SummonItemId, CharId, RowboatScrollTemplateId);
        }

        try
        {
            // Cycle 1: vehicle out + ridden when the server stops (attach_point = Driver)
            AssertDbSettled();
            SeedSlaveRow(hp: 1234, mp: 456, x: 100.5f, y: 200.5f, z: 1.5f);
            AssertSeeded();

            E2eStack.RestartGameServer();
            AssertRowIntact("cycle1-after-restart", hp: 1234, mp: 456, x: 100.5f, y: 200.5f, z: 1.5f);
            Console.WriteLine("[m4-vehicles] CYCLE 1 PASS (out+ridden rowboat survives restart, attachment intact, no dup)");

            // Cycle 2: mutate the row the way a post-combat save would
            UpdateSlaveRow(hp: 876, mp: 234, x: 300.25f, y: 400.75f, z: 2.25f);

            E2eStack.RestartGameServer();
            AssertRowIntact("cycle2-after-restart", hp: 876, mp: 234, x: 300.25f, y: 400.75f, z: 2.25f);
            Console.WriteLine("[m4-vehicles] CYCLE 2 PASS (post-save values survive second restart, still exactly 1 row)");
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// M5.1 gap flag (t_1b82b33f): attached-pack-on-slave restart — a trade
    /// pack loaded onto a slave cargo point via the REAL gameplay path
    /// (PackVehicleService.TryLoadCarriedPack → SlaveManager.AttachDoodadAtPoint)
    /// persists as a slave-owned doodads row (owner_type Slave, house_id =
    /// slave DbId, attach_point = cargo point, item link, LOCAL snapped
    /// transform). This test seeds exactly the row the engine now writes,
    /// kills the game server (kill -9 semantics), boots it again, and
    /// asserts the full attached-pack state survives byte-intact: the slave
    /// row, the single binding row (owner_type=2 + house_id=slaveId), the
    /// binding (attach_point/data), the item link and the local transform —
    /// no loss, no duplication, exactly 1 binding row.
    ///
    /// Rows are seeded with distinctive values so any boot-time rewrite
    /// (the M3b-1 clobber class) fails the assertion.
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public void M4Vehicles_AttachedPackOnSlave_Restart_RowBindingTransformSurvive_ExactlyOneBindingRow()
    {
        EnsureStack();

        using (var login = E2eStack.OpenDb("aaemu_login"))
        using (var cmd = login.CreateCommand())
        {
            cmd.CommandText = "INSERT IGNORE INTO users (id, username, password, email, last_ip) VALUES (@id, @name, '', '', '')";
            cmd.Parameters.AddWithValue("@id", WagonAccountId);
            cmd.Parameters.AddWithValue("@name", "m4_attached_pack_owner");
            cmd.ExecuteNonQuery();
        }

        // Planted 5 days ago: canonical 6-day despawn timer still running —
        // the maturation clock must not be rewritten at boot.
        var plantedAt = DateTime.UtcNow.AddDays(-5);
        var plantedAtSql = plantedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        using (var game = E2eStack.OpenDb("aaemu_game"))
        {
            InsertCharacter(game, WagonCharId, WagonAccountId, "m4_attached_pack_owner");
            InsertSummonItem(game, WagonSummonItemId, WagonCharId, FarmWagonScrollTemplateId);
            InsertAttachedPackSystemContainer(game);
            InsertAttachedPackItem(game);
            SeedAttachedPackSlaveRow();
            SeedAttachedPackDoodad(plantedAtSql);
        }

        try
        {
            AssertAttachedPackSeeded(plantedAt);

            // kill -9: StopGameServer kills the process tree; the MySQL rows
            // are the only thing that can survive. Boot fresh and verify.
            E2eStack.RestartGameServer();
            AssertAttachedPackIntact(plantedAt);
            Console.WriteLine("[m4-vehicles] ATTACHED-PACK RESTART PASS (pack on slave cargo survives kill -9: row + binding + transform, exactly 1 binding row)");
        }
        finally
        {
            CleanupAttachedPack();
        }
    }

    // ================================================================ seeding

    private static void InsertCharacter(MySqlConnection conn, uint charId, uint accountId, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO characters (id, account_id, name, race, gender, unit_model_params, level, " +
            "experience, recoverable_exp, hp, mp, consumed_lp, ability1, ability2, ability3, world_id, zone_id, " +
            "x, y, z, faction_id, faction_name, expedition_id, family, dead_count, rez_wait_duration, " +
            "rez_penalty_duration, auto_use_aapoint, prev_point, point, gift, expanded_expert, slots) " +
            "VALUES (@id, @accountId, @name, 0, 0, '', 1, 0, 0, 100, 100, 0, 0, 0, 0, 0, 0, 0, 0, 0, " +
            "148, '', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '')";
        cmd.Parameters.AddWithValue("@id", charId);
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Summon scroll item the vehicle is bound to (the re-summon path anchor).</summary>
    private static void InsertSummonItem(MySqlConnection conn, ulong itemId, uint ownerId, uint templateId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO items (id, type, template_id, container_id, slot_type, slot, count, details, " +
            "lifespan_mins, made_unit_id, owner, grade, flags, created_at) " +
            "VALUES (@id, 'SummonSlave', @templateId, 0, 0, 0, 1, '', 0, 0, @owner, 0, 0, @createdAt)";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@templateId", templateId);
        cmd.Parameters.AddWithValue("@owner", ownerId);
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static void SeedSlaveRow(int hp, int mp, float x, float y, float z)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO slaves (id, item_id, template_id, attach_point, name, owner_type, owner_id, summoner, hp, mp, x, y, z) " +
            "VALUES (@id, @itemId, @templateId, 1, 'm4-e2e-rowboat', 0, @owner, @owner, @hp, @mp, @x, @y, @z)";
        cmd.Parameters.AddWithValue("@id", SlaveRowId);
        cmd.Parameters.AddWithValue("@itemId", SummonItemId);
        cmd.Parameters.AddWithValue("@templateId", RowboatTemplateId);
        cmd.Parameters.AddWithValue("@owner", CharId);
        cmd.Parameters.AddWithValue("@hp", hp);
        cmd.Parameters.AddWithValue("@mp", mp);
        cmd.Parameters.AddWithValue("@x", x);
        cmd.Parameters.AddWithValue("@y", y);
        cmd.Parameters.AddWithValue("@z", z);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateSlaveRow(int hp, int mp, float x, float y, float z)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE slaves SET hp = @hp, mp = @mp, x = @x, y = @y, z = @z, updated_at = @now WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", SlaveRowId);
        cmd.Parameters.AddWithValue("@hp", hp);
        cmd.Parameters.AddWithValue("@mp", mp);
        cmd.Parameters.AddWithValue("@x", x);
        cmd.Parameters.AddWithValue("@y", y);
        cmd.Parameters.AddWithValue("@z", z);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    // ================================================================ assertions

    /// <summary>
    /// Guards against the stack-bring-up race observed on one run (MySQL
    /// container re-created mid-init after a previous suite's `down -v`):
    /// the game's schema must be present and empty before we seed, so a
    /// half-initialized database fails HERE with a clear message instead of
    /// masquerading as a restart-recovery failure.
    /// </summary>
    private static void AssertDbSettled()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM slaves";
        var n = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(0, n);
    }

    /// <summary>Confirms the seed landed before the first restart — evidence of a clean baseline.</summary>
    private static void AssertSeeded()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM slaves WHERE summoner = @owner";
        cmd.Parameters.AddWithValue("@owner", CharId);
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    private static void AssertRowIntact(string phase, int hp, int mp, float x, float y, float z)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");

        // exactly one row for this summoner — no duplication across restarts
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM slaves WHERE summoner = @owner";
            cmd.Parameters.AddWithValue("@owner", CharId);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT item_id, template_id, attach_point, name, owner_type, owner_id, summoner, hp, mp, x, y, z " +
                "FROM slaves WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", SlaveRowId);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), $"[{phase}] slaves row {SlaveRowId} missing after restart");
            Assert.Equal(SummonItemId, reader.GetUInt64("item_id"));
            Assert.Equal(RowboatTemplateId, reader.GetUInt32("template_id"));
            Assert.Equal(1, reader.GetInt32("attach_point"));   // driver binding survives
            Assert.Equal("m4-e2e-rowboat", reader.GetString("name"));
            Assert.Equal(0, reader.GetInt32("owner_type"));
            Assert.Equal(CharId, reader.GetUInt32("owner_id"));
            Assert.Equal(CharId, reader.GetUInt32("summoner"));
            Assert.Equal(hp, reader.GetInt32("hp"));
            Assert.Equal(mp, reader.GetInt32("mp"));
            Assert.Equal(x, reader.GetFloat("x"), 2);
            Assert.Equal(y, reader.GetFloat("y"), 2);
            Assert.Equal(z, reader.GetFloat("z"), 2);
        }
    }

    // ================================================================ cleanup

    private static void Cleanup()
    {
        try
        {
            using var game = E2eStack.OpenDb("aaemu_game");
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM slaves WHERE id = @id OR summoner = @owner";
                cmd.Parameters.AddWithValue("@id", SlaveRowId);
                cmd.Parameters.AddWithValue("@owner", CharId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM items WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", SummonItemId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM characters WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", CharId);
                cmd.ExecuteNonQuery();
            }
            using var login = E2eStack.OpenDb("aaemu_login");
            using var cmd2 = login.CreateCommand();
            cmd2.CommandText = "DELETE FROM users WHERE id = @id";
            cmd2.Parameters.AddWithValue("@id", AccountId);
            cmd2.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[m4-vehicles] cleanup failed (non-fatal): {e.Message}");
        }
    }

    // ================================================================ attached-pack seeding

    /// <summary>System container the pack item lives in after a carried load (SlotType.System = 0xFF).</summary>
    private static void InsertAttachedPackSystemContainer(MySqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO item_containers (container_id, container_type, slot_type, container_size, owner_id, mate_id) " +
            "VALUES (@containerId, 'AAEmu.Game.Models.Game.Items.Containers.SystemContainer', 255, 0, @ownerId, 0)";
        cmd.Parameters.AddWithValue("@containerId", AttachedPackContainerId);
        cmd.Parameters.AddWithValue("@ownerId", WagonCharId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Pack item (Backpack 26488) in the System container — the carried-load cargo state.</summary>
    private static void InsertAttachedPackItem(MySqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO items (id, type, template_id, container_id, slot_type, slot, count, details, " +
            "lifespan_mins, made_unit_id, owner, grade, flags, created_at) " +
            "VALUES (@id, 'AAEmu.Game.Models.Game.Items.Backpack', @templateId, @containerId, 255, 0, 1, '', " +
            "0, 0, @ownerId, 0, 0, @createdAt)";
        cmd.Parameters.AddWithValue("@id", AttachedPackItemId);
        cmd.Parameters.AddWithValue("@templateId", PackItemTemplateId);
        cmd.Parameters.AddWithValue("@containerId", AttachedPackContainerId);
        cmd.Parameters.AddWithValue("@ownerId", WagonCharId);
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The farm wagon out + ridden when the server stops — same row shape as
    /// the rowboat seed, but the canonical cargo vehicle (template 60, model
    /// 1008: pack-storage-box bindings at attach points 9-12).
    /// </summary>
    private static void SeedAttachedPackSlaveRow()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO slaves (id, item_id, template_id, attach_point, name, owner_type, owner_id, summoner, hp, mp, x, y, z) " +
            "VALUES (@id, @itemId, @templateId, 1, 'm4-e2e-farm-wagon', 0, @owner, @owner, 1234, 456, 100.5, 200.5, 1.5)";
        cmd.Parameters.AddWithValue("@id", WagonSlaveRowId);
        cmd.Parameters.AddWithValue("@itemId", WagonSummonItemId);
        cmd.Parameters.AddWithValue("@templateId", FarmWagonTemplateId);
        cmd.Parameters.AddWithValue("@owner", WagonCharId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The attached-pack doodads row — exactly what the persistence arm in
    /// PackVehicleService now writes after AttachDoodadAtPoint:
    ///   owner_type = 2 (Slave), house_id = slave DbId, attach_point = 9
    ///   (first cargo point), data = 9 (attach-point copy), template 6068
    ///   (placed-pack doodad for pack 26488), current_phase 15677 (start
    ///   group), item link, LOCAL transform = the canonical model-1008 snap
    ///   of cargo point 9, and plant_time 5 days back (maturation clock).
    /// </summary>
    private static void SeedAttachedPackDoodad(string plantedAtSql)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO doodads (id, owner_id, owner_type, attach_point, template_id, current_phase_id, " +
            "plant_time, growth_time, phase_time, x, y, z, roll, pitch, yaw, scale, item_id, house_id, " +
            "parent_doodad, item_template_id, item_container_id, data, farm_type) " +
            "VALUES (@id, @ownerId, 2, 9, @templateId, @phaseId, @plantTime, @plantTime, @plantTime, " +
            "@x, @y, @z, 0, 0, 0, 1, @itemId, @houseId, 0, @itemTemplateId, 0, 9, 0)";
        cmd.Parameters.AddWithValue("@id", AttachedPackDoodadDbId);
        cmd.Parameters.AddWithValue("@ownerId", WagonCharId);
        cmd.Parameters.AddWithValue("@templateId", PlacedPackDoodadTemplateId);
        cmd.Parameters.AddWithValue("@phaseId", PlacedPackStartPhaseId);
        cmd.Parameters.AddWithValue("@plantTime", plantedAtSql);
        cmd.Parameters.AddWithValue("@x", CargoSnapX.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@y", CargoSnapY.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@z", CargoSnapZ.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@itemId", AttachedPackItemId);
        cmd.Parameters.AddWithValue("@houseId", WagonSlaveRowId);
        cmd.Parameters.AddWithValue("@itemTemplateId", PackItemTemplateId);
        cmd.ExecuteNonQuery();
    }

    // ================================================================ attached-pack assertions

    private static void AssertAttachedPackSeeded(DateTime plantedAt)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");

        // Slave row seeded for the summoner.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM slaves WHERE summoner = @owner";
            cmd.Parameters.AddWithValue("@owner", WagonCharId);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        // Exactly 1 binding row: owner_type Slave(2) + house_id = slave DbId.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM doodads WHERE owner_type = 2 AND house_id = @houseId";
            cmd.Parameters.AddWithValue("@houseId", WagonSlaveRowId);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        AssertAttachedPackRow(conn, "seeded", plantedAt);
    }

    private static void AssertAttachedPackIntact(DateTime plantedAt)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");

        // Exactly one slave row for the summoner — no boot-time respawn dup.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM slaves WHERE summoner = @owner";
            cmd.Parameters.AddWithValue("@owner", WagonCharId);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        // Exactly ONE binding row (owner_type=Slave + house_id=slaveDbId) —
        // the attached-pack contract: no loss, no duplication.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM doodads WHERE owner_type = 2 AND house_id = @houseId";
            cmd.Parameters.AddWithValue("@houseId", WagonSlaveRowId);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        AssertAttachedPackRow(conn, "after-restart", plantedAt);
    }

    /// <summary>Byte-intact check of the binding + item link + LOCAL transform.</summary>
    private static void AssertAttachedPackRow(MySqlConnection conn, string phase, DateTime plantedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT owner_id, owner_type, attach_point, template_id, current_phase_id, plant_time, " +
            "item_id, item_template_id, house_id, x, y, z, data FROM doodads WHERE id = @doodadId";
        cmd.Parameters.AddWithValue("@doodadId", AttachedPackDoodadDbId);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"[{phase}] attached-pack doodads row missing");
        Assert.Equal(WagonCharId, reader.GetUInt32("owner_id"));
        Assert.Equal(2u, reader.GetUInt32("owner_type"));               // DoodadOwnerType.Slave
        Assert.Equal(9u, reader.GetUInt32("attach_point"));             // cargo point Cannon0
        Assert.Equal(PlacedPackDoodadTemplateId, reader.GetUInt32("template_id"));
        Assert.Equal(PlacedPackStartPhaseId, reader.GetUInt32("current_phase_id"));

        // Maturation clock must not be rewritten at boot (M3b-1 clobber class).
        var plantTime = reader.GetDateTime("plant_time");
        Assert.True(Math.Abs((plantTime - plantedAt).TotalSeconds) < 2,
            $"[{phase}] plant_time clobbered: stored {plantTime:O}, seeded {plantedAt:O}");

        // Item link (doodad → pack item) survives.
        Assert.Equal(AttachedPackItemId, reader.GetUInt64("item_id"));
        Assert.Equal(PackItemTemplateId, reader.GetUInt32("item_template_id"));

        // Binding row key: house_id == slave DbId.
        Assert.Equal(WagonSlaveRowId, reader.GetUInt32("house_id"));
        Assert.Equal(9, reader.GetInt32("data"));                      // attach-point copy

        // LOCAL transform = the canonical cargo snap — a world-space rewrite
        // (or a lost parent) would shift these.
        Assert.True(Math.Abs(reader.GetFloat("x") - CargoSnapX) < 0.001f &&
                    Math.Abs(reader.GetFloat("y") - CargoSnapY) < 0.001f &&
                    Math.Abs(reader.GetFloat("z") - CargoSnapZ) < 0.001f,
            $"[{phase}] local transform clobbered to {reader.GetFloat("x")}/{reader.GetFloat("y")}/{reader.GetFloat("z")}");
    }

    private static void CleanupAttachedPack()
    {
        try
        {
            using var game = E2eStack.OpenDb("aaemu_game");
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM doodads WHERE id = @doodadId";
                cmd.Parameters.AddWithValue("@doodadId", AttachedPackDoodadDbId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM slaves WHERE id = @id OR summoner = @owner";
                cmd.Parameters.AddWithValue("@id", WagonSlaveRowId);
                cmd.Parameters.AddWithValue("@owner", WagonCharId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM items WHERE id IN (@scrollId, @packId)";
                cmd.Parameters.AddWithValue("@scrollId", WagonSummonItemId);
                cmd.Parameters.AddWithValue("@packId", AttachedPackItemId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM item_containers WHERE container_id = @containerId";
                cmd.Parameters.AddWithValue("@containerId", AttachedPackContainerId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM characters WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", WagonCharId);
                cmd.ExecuteNonQuery();
            }
            using var login = E2eStack.OpenDb("aaemu_login");
            using var cmd2 = login.CreateCommand();
            cmd2.CommandText = "DELETE FROM users WHERE id = @id";
            cmd2.Parameters.AddWithValue("@id", WagonAccountId);
            cmd2.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[m4-vehicles] attached-pack cleanup failed (non-fatal): {e.Message}");
        }
    }
}
