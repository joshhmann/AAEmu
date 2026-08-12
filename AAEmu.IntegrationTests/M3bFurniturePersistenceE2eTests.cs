using System.Globalization;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.IntegrationTests.E2e;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests;

/// <summary>
/// M3b-1 (t_fb3e5f8c): furniture + bound doodad persistence — restart round trip.
///
/// Seeds a completed house (template 172) with its full set of bound doodads (door,
/// windows, chimney, nameplate, ladder — the housing_binding_doodads rows for template
/// 172) plus a piece of furniture (chandelier), each with DISTINCT position/rotation
/// state, then runs TWO process-level restarts and asserts the MySQL rows survive with
/// every persistence column intact:
///
///   - transform: x/y/z + roll/pitch/yaw (rotation/attachment integrity)
///   - attachment: attach_point, house_id, parent_doodad
///   - ownership: owner_id, owner_type
///   - phase: current_phase_id (incl. a NON-start phase — the exact row the old load
///     path clobbered on every boot, see SpawnManager.SpawnPersistentDoodads)
///
/// Fail-before: before the M3b-1 fix, SpawnPersistentDoodads armed IsPersistent BEFORE
/// restoring the row fields; the FuncGroupId setter then fired Save() with a zeroed
/// transform/owner/house whenever the stored phase differed from the template start
/// group. The ladder doodad (6885 @ attach 24) is seeded at phase 18467 which has NO
/// phase funcs — so on the old code the clobbered row was never repaired and the next
/// restart loaded the ladder at the world origin, ownerless. This test would have been
/// RED on that code.
///
/// The house owner row (characters + login users) is created and cleaned up within the
/// test so the shared e2e stack is left byte-identical afterwards.
/// </summary>
[Collection("e2e")]
public class M3bFurniturePersistenceE2eTests
{
    // Template 172 (아담한 누이아 주택) — canonical housing_binding_doodads rows
    // (owner_id 172): (attach, doodad_id) pairs.
    private static readonly (int Attach, uint DoodadId, uint PhaseId, string Name)[] BoundDoodads =
    [
        (36, 4278, 10521, "door"),      // nuia_door01 (start group = closed)
        (37, 4322, 10608, "window-a"),  // nuia_window01 (start group)
        (38, 4322, 10608, "window-b"),  // second window
        (57, 2392, 6172, "nameplate"),  // commonbuilding_nameplate
        (1, 4922, 12249, "chimney"),    // nuia.housing_s_floor_chimney
        (24, 6885, 18467, "ladder"),    // housing_ladder — NON-START phase, no phase funcs
    ];

    // Chandelier (housing_decorations id 100 → doodad 1294, start group 2014) — furniture.
    private const uint ChandelierDoodadId = 1294;
    private const uint ChandelierPhaseId = 2014;

    private const uint HouseTemplateId = 172;
    private const uint OwnerAccountId = 900001;
    private const uint OwnerCharId = 900001;
    private const string OwnerName = "m3b1_owner";
    private const string OwnerAccountName = "m3b1_account";
    private const uint HouseDbId = 900001;

    // Distinct LOCAL positions (relative to the house at 20010/20020/100 — the
    // DB stores local position/rotation; Doodad.Save writes Transform.Local) so
    // a clobber to 0,0,0 (or to origin) is unambiguous and world positions stay
    // in bounds (GetZoneId FATALs otherwise).
    private static readonly (float X, float Y, float Z, float Roll, float Pitch, float Yaw)[] Spawns =
    [
        (2.5f, 0.5f, 1.0f, 0.1f, 0.2f, 0.3f),
        (0.5f, 2.5f, 1.5f, 0f, 0f, 0.5f),
        (-0.5f, 2.5f, 1.5f, 0f, 0f, 1.0f),
        (0.5f, 0.5f, 2.5f, 0f, 0f, 1.5f),
        (-2.0f, 0.5f, 3.0f, 0f, 0f, 2.0f),
        (1.0f, -1.0f, 2.0f, 0.05f, 0.05f, 2.5f),
    ];

    private static void EnsureStack() => E2eStack.EnsureUp();

    [Fact]
    [Trait("Category", "e2e")]
    public void M3b1_FurnitureAndBoundDoodads_RestartTwice_RowsSurviveWithRotationAndAttachment()
    {
        EnsureStack();

        // --- seed: account + character (house owner) -----------------------------
        using (var login = E2eStack.OpenDb("aaemu_login"))
        using (var cmd = login.CreateCommand())
        {
            cmd.CommandText = "INSERT IGNORE INTO users (id, username, password, email, last_ip) VALUES (@id, @name, '', '', '')";
            cmd.Parameters.AddWithValue("@id", OwnerAccountId);
            cmd.Parameters.AddWithValue("@name", OwnerAccountName);
            cmd.ExecuteNonQuery();
        }

        using (var game = E2eStack.OpenDb("aaemu_game"))
        {
            InsertCharacter(game, OwnerCharId, OwnerName);
            InsertHouse(game);
            var dbId = 900100;
            foreach (var (attach, doodadId, phaseId, name) in BoundDoodads)
            {
                var spawn = Spawns[Array.FindIndex(BoundDoodads, b => b.Name == name)];
                InsertDoodad(game, (uint)dbId, doodadId, phaseId, attach, spawn, HouseDbId, OwnerCharId);
                dbId++;
            }

            // furniture (chandelier) — attach point None, own rotation
            InsertDoodad(game, (uint)dbId, ChandelierDoodadId, ChandelierPhaseId, 0,
                (0.5f, 0.5f, 4.0f, 0f, 0f, 2.9f), HouseDbId, OwnerCharId);
        }

        try
        {
            // --- restart 1: the load path reads the seeded rows -----------------
            E2eStack.RestartGameServer();

            // All rows must survive the load path untouched.
            AssertRowsIntact("after-restart-1");

            // --- restart 2: restart-safe, no duplication -------------------------
            E2eStack.RestartGameServer();

            AssertRowsIntact("after-restart-2");
            Assert.Equal(7, CountDoodadRows());
        }
        finally
        {
            // Cleanup: leave the shared stack byte-identical.
            using var game = E2eStack.OpenDb("aaemu_game");
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM doodads WHERE house_id = @houseId OR id BETWEEN 900100 AND 900107";
                cmd.Parameters.AddWithValue("@houseId", HouseDbId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM housings WHERE id = @houseId";
                cmd.Parameters.AddWithValue("@houseId", HouseDbId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM characters WHERE id = @charId";
                cmd.Parameters.AddWithValue("@charId", OwnerCharId);
                cmd.ExecuteNonQuery();
            }
            using var login = E2eStack.OpenDb("aaemu_login");
            using var cmd2 = login.CreateCommand();
            cmd2.CommandText = "DELETE FROM users WHERE id = @id";
            cmd2.Parameters.AddWithValue("@id", OwnerAccountId);
            cmd2.ExecuteNonQuery();
        }
    }

    // ================================================================ assertions

    private static void AssertRowsIntact(string phase)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, template_id, current_phase_id, attach_point, x, y, z, roll, pitch, yaw, " +
            "owner_id, owner_type, house_id, parent_doodad FROM doodads " +
            "WHERE house_id = @houseId ORDER BY id";
        cmd.Parameters.AddWithValue("@houseId", HouseDbId);

        var rows = new List<(uint Id, uint TemplateId, uint Phase, int Attach, float X, float Y, float Z,
            float Roll, float Pitch, float Yaw, uint OwnerId, int OwnerType, uint HouseId, uint Parent)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetUInt32("id"), reader.GetUInt32("template_id"), reader.GetUInt32("current_phase_id"),
                reader.GetInt32("attach_point"), reader.GetFloat("x"), reader.GetFloat("y"), reader.GetFloat("z"),
                reader.GetFloat("roll"), reader.GetFloat("pitch"), reader.GetFloat("yaw"),
                reader.GetUInt32("owner_id"), reader.GetInt32("owner_type"), reader.GetUInt32("house_id"),
                reader.GetUInt32("parent_doodad")));
        }

        Assert.Equal(7, rows.Count);
        var byAttach = rows.GroupBy(r => r.Attach).ToDictionary(g => g.Key, g => g.First());

        // Bound doodads: rotation/attachment/ownership must be untouched.
        foreach (var (attach, doodadId, phaseId, name) in BoundDoodads)
        {
            var row = byAttach[attach];
            Assert.True(row.TemplateId == doodadId, $"[{phase}] {name}: template id {row.TemplateId} != {doodadId}");
            Assert.True(row.Phase == phaseId, $"[{phase}] {name}: phase {row.Phase} != {phaseId}");
            Assert.True(row.X == Spawns[Array.FindIndex(BoundDoodads, b => b.Name == name)].X,
                $"[{phase}] {name}: x clobbered to {row.X}");
            Assert.True(row.Y == Spawns[Array.FindIndex(BoundDoodads, b => b.Name == name)].Y,
                $"[{phase}] {name}: y clobbered to {row.Y}");
            Assert.True(row.Z == Spawns[Array.FindIndex(BoundDoodads, b => b.Name == name)].Z,
                $"[{phase}] {name}: z clobbered to {row.Z}");
            Assert.True(Math.Abs(row.Yaw - Spawns[Array.FindIndex(BoundDoodads, b => b.Name == name)].Yaw) < 0.001f,
                $"[{phase}] {name}: yaw clobbered to {row.Yaw}");
            Assert.True(row.OwnerId == OwnerCharId, $"[{phase}] {name}: owner clobbered to {row.OwnerId}");
            Assert.True(row.OwnerType == (int)DoodadOwnerType.Housing,
                $"[{phase}] {name}: owner_type clobbered to {row.OwnerType}");
            Assert.True(row.HouseId == HouseDbId, $"[{phase}] {name}: house_id clobbered to {row.HouseId}");
            Assert.True(row.Parent == 0, $"[{phase}] {name}: parent_doodad wrong {row.Parent}");
        }

        // Furniture: the chandelier keeps its own rotation. (Template id is NOT a
        // unique key — two windows share template 4322 — so find it by id.)
        var furniture = rows.FirstOrDefault(r => r.TemplateId == ChandelierDoodadId);
        Assert.NotNull(furniture);
        Assert.True(furniture.Phase == ChandelierPhaseId, $"[{phase}] chandelier: phase {furniture.Phase}");
        Assert.True(Math.Abs(furniture.Yaw - 2.9f) < 0.001f, $"[{phase}] chandelier: yaw clobbered to {furniture.Yaw}");
        Assert.True(furniture.OwnerType == (int)DoodadOwnerType.Housing, $"[{phase}] chandelier: owner_type {furniture.OwnerType}");
        Assert.True(furniture.HouseId == HouseDbId, $"[{phase}] chandelier: house_id {furniture.HouseId}");
    }

    private static int CountDoodadRows()
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM doodads WHERE house_id = @houseId";
        cmd.Parameters.AddWithValue("@houseId", HouseDbId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ================================================================ seeding

    private static void InsertCharacter(MySqlConnection conn, uint charId, string name)
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
        cmd.Parameters.AddWithValue("@accountId", OwnerAccountId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.ExecuteNonQuery();
    }

    private static void InsertHouse(MySqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO housings (id, account_id, owner, co_owner, template_id, name, x, y, z, " +
            "yaw, pitch, roll, current_step, current_action, permission, place_date, protected_until, " +
            "faction_id, sell_to, sell_price, allow_recover) " +
            "VALUES (@id, @accountId, @owner, @owner, @templateId, 'M3b1 House', 20010, 20020, 100, " +
            "0, 0, 0, -1, 0, 0, @placedate, @protect, 148, 0, 0, 1)";
        cmd.Parameters.AddWithValue("@id", HouseDbId);
        cmd.Parameters.AddWithValue("@accountId", OwnerAccountId);
        cmd.Parameters.AddWithValue("@owner", OwnerCharId);
        cmd.Parameters.AddWithValue("@templateId", HouseTemplateId);
        cmd.Parameters.AddWithValue("@placedate", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@protect", DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static void InsertDoodad(MySqlConnection conn, uint dbId, uint templateId, uint phaseId, int attach,
        (float X, float Y, float Z, float Roll, float Pitch, float Yaw) spawn, uint houseId, uint ownerId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO doodads (id, owner_id, owner_type, attach_point, template_id, current_phase_id, " +
            "plant_time, growth_time, phase_time, x, y, z, roll, pitch, yaw, scale, item_id, house_id, " +
            "parent_doodad, item_template_id, item_container_id, data, farm_type) " +
            "VALUES (@id, @ownerId, 3, @attach, @templateId, @phaseId, @now, @now, @now, " +
            "@x, @y, @z, @roll, @pitch, @yaw, 1, 0, @houseId, 0, 0, 0, 0, 0)";
        cmd.Parameters.AddWithValue("@id", dbId);
        cmd.Parameters.AddWithValue("@ownerId", ownerId);
        cmd.Parameters.AddWithValue("@attach", attach);
        cmd.Parameters.AddWithValue("@templateId", templateId);
        cmd.Parameters.AddWithValue("@phaseId", phaseId);
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@x", spawn.X.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@y", spawn.Y.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@z", spawn.Z.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@roll", spawn.Roll.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@pitch", spawn.Pitch.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@yaw", spawn.Yaw.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@houseId", houseId);
        cmd.ExecuteNonQuery();
    }
}
