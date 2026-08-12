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
            InsertSummonItem(game, SummonItemId, CharId);
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
    private static void InsertSummonItem(MySqlConnection conn, ulong itemId, uint ownerId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO items (id, type, template_id, container_id, slot_type, slot, count, details, " +
            "lifespan_mins, made_unit_id, owner, grade, flags, created_at) " +
            "VALUES (@id, 'SummonSlave', @templateId, 0, 0, 0, 1, '', 0, 0, @owner, 0, 0, @createdAt)";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.Parameters.AddWithValue("@templateId", RowboatScrollTemplateId);
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
}
