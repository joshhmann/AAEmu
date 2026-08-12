using System.Globalization;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.IntegrationTests.E2e;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests;

/// <summary>
/// M4-2 (t_449d0c41): placed trade-pack restart persistence — the per-object
/// restart assertions from the trade-packs dossier (scorecard-explorations/mechanics/
/// trade-packs.md §11 gaps 1/9): a placed pack's maturation timer (plant_time) and
/// cargo ownership (items.made_unit_id) must survive a kill -9 restart.
///
/// Seeds a placed trade pack exactly as PutDownBackpackEffect persists it:
///   - doodads row: template 6068 (황금 평원 마취제 꾸러미 = the placed-pack doodad for
///     pack item 26488), owner_type Character (open field), current_phase_id 15677
///     (the template's start func group), plant_time = NOW − 5 days (canonical 6-day
///     despawn timer, 5 days in → still live but close to expiry),
///   - item_containers row: System container for the pack owner,
///   - items row: Backpack 26488 in that System container with made_unit_id =
///     the CRAFTER character (80/20 split ownership survives the restart).
///
/// Then runs ONE kill -9 restart (the game's boot load path: SpawnManager
/// SpawnPersistentDoodads → Doodad.ApplyLoadedState + ItemManager.LoadUserItems)
/// and asserts every restart-relevant column is byte-identical:
///   - plant_time (the maturation timer base — a boot-time rewrite would reset the
///     6-day expiry clock, the exact M3b clobber class),
///   - item_id / item_template_id (doodad → cargo link),
///   - items.made_unit_id (cargo ownership → crafter share on later sale),
///   - item container link + owner.
///
/// Fail-before: on the pre-M3b load path, the FuncGroupId setter fired Save() with
/// plant_time = boot time during the load — this test would be RED on that code.
/// Fail-before for ownership: LoadUserItems row parsing must restore made_unit_id.
///
/// All seeded rows are cleaned up in the finally block so the shared e2e stack
/// is left byte-identical.
/// </summary>
[Collection("e2e")]
public class M4_2TradePackRestartE2eTests
{
    // Pack item 26488 (황금 평원 마취제) — canonical item_backpacks row id 196,
    // backpack_type 3 (trade pack). Placed-pack doodad 6068, start group 15677.
    private const uint PackItemTemplateId = 26488;
    private const uint PlacedPackDoodadTemplateId = 6068;
    private const uint PlacedPackStartPhaseId = 15677;

    // Seed rows (9002xx range — clear of other suites).
    private const uint PackOwnerAccountId = 900003;
    private const uint PackOwnerCharId = 900003;
    private const string PackOwnerName = "m42_seller";
    private const uint CrafterAccountId = 900004;
    private const uint CrafterCharId = 900004;
    private const string CrafterName = "m42_crafter";

    private const uint DoodadDbId = 900200;
    private const ulong PackItemId = 9002001;
    private const uint SystemContainerDbId = 9002000;

    // Open-field position in the main world (valid in-bounds coords, away from the
    // M3b house at 20010/20020).
    private const float PackX = 19950f;
    private const float PackY = 20050f;
    private const float PackZ = 100f;

    private static void EnsureStack() => E2eStack.EnsureUp();

    [Fact]
    [Trait("Category", "e2e")]
    public void M42_PlacedTradePack_Restart_RowsSurviveWithPlantTimeAndMadeUnitId()
    {
        EnsureStack();

        // --- seed: accounts + characters (pack owner + crafter) ------------------
        using (var login = E2eStack.OpenDb("aaemu_login"))
        {
            InsertUser(login, PackOwnerAccountId, PackOwnerName);
            InsertUser(login, CrafterAccountId, CrafterName);
        }

        // Planted 5 days ago: canonical 6-day despawn timer still running (1 day left).
        var plantedAt = DateTime.UtcNow.AddDays(-5);
        var plantedAtSql = plantedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        using (var game = E2eStack.OpenDb("aaemu_game"))
        {
            InsertCharacter(game, PackOwnerCharId, PackOwnerName, PackOwnerAccountId);
            InsertCharacter(game, CrafterCharId, CrafterName, CrafterAccountId);
            InsertSystemContainer(game);
            InsertPackItem(game, PackOwnerCharId, CrafterCharId);
            InsertPlacedPackDoodad(game, plantedAtSql);
        }

        try
        {
            // --- restart: the boot load path reads the seeded rows ---------------
            E2eStack.RestartGameServer();

            // The placed pack + its cargo must survive byte-identical: the maturation
            // timer base (plant_time) and the cargo ownership (made_unit_id).
            AssertPlacedPackRowsIntact(plantedAt, "after-restart");
        }
        finally
        {
            // Cleanup: leave the shared stack byte-identical.
            using var game = E2eStack.OpenDb("aaemu_game");
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM doodads WHERE id = @doodadId";
                cmd.Parameters.AddWithValue("@doodadId", DoodadDbId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM items WHERE id = @itemId";
                cmd.Parameters.AddWithValue("@itemId", PackItemId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM item_containers WHERE container_id = @containerId";
                cmd.Parameters.AddWithValue("@containerId", SystemContainerDbId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = game.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM characters WHERE id IN (@a, @b)";
                cmd.Parameters.AddWithValue("@a", PackOwnerCharId);
                cmd.Parameters.AddWithValue("@b", CrafterCharId);
                cmd.ExecuteNonQuery();
            }
            using var login = E2eStack.OpenDb("aaemu_login");
            using var cmd2 = login.CreateCommand();
            cmd2.CommandText = "DELETE FROM users WHERE id IN (@a, @b)";
            cmd2.Parameters.AddWithValue("@a", PackOwnerAccountId);
            cmd2.Parameters.AddWithValue("@b", CrafterAccountId);
            cmd2.ExecuteNonQuery();
        }
    }

    // ================================================================ assertions

    private static void AssertPlacedPackRowsIntact(DateTime plantedAt, string phase)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");

        // Doodad row: template, phase, plant_time, item link, ownership.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT template_id, current_phase_id, plant_time, item_id, item_template_id, " +
                "owner_id, owner_type, x, y, z FROM doodads WHERE id = @doodadId";
            cmd.Parameters.AddWithValue("@doodadId", DoodadDbId);

            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), $"[{phase}] placed-pack doodad row missing after restart");
            Assert.True(reader.GetUInt32("template_id") == PlacedPackDoodadTemplateId,
                $"[{phase}] template_id clobbered to {reader.GetUInt32("template_id")}");
            Assert.True(reader.GetUInt32("current_phase_id") == PlacedPackStartPhaseId,
                $"[{phase}] current_phase_id clobbered to {reader.GetUInt32("current_phase_id")}");

            // THE maturation-timer assertion: plant_time must be the ORIGINAL seed
            // value (±1s for MySQL DATETIME precision), not the boot time. A rewrite
            // here resets the 6-day despawn clock on every restart.
            var plantTime = reader.GetDateTime("plant_time");
            Assert.True(Math.Abs((plantTime - plantedAt).TotalSeconds) < 2,
                $"[{phase}] plant_time clobbered: stored {plantTime:O}, seeded {plantedAt:O}");

            Assert.True(reader.GetUInt64("item_id") == PackItemId,
                $"[{phase}] item_id clobbered to {reader.GetUInt64("item_id")}");
            Assert.True(reader.GetUInt32("item_template_id") == PackItemTemplateId,
                $"[{phase}] item_template_id clobbered to {reader.GetUInt32("item_template_id")}");
            Assert.True(reader.GetUInt32("owner_id") == PackOwnerCharId,
                $"[{phase}] owner_id clobbered to {reader.GetUInt32("owner_id")}");
            Assert.True(reader.GetInt32("owner_type") == (int)DoodadOwnerType.Character,
                $"[{phase}] owner_type clobbered to {reader.GetInt32("owner_type")}");
            Assert.True(Math.Abs(reader.GetFloat("x") - PackX) < 0.001f &&
                        Math.Abs(reader.GetFloat("y") - PackY) < 0.001f,
                $"[{phase}] position clobbered to {reader.GetFloat("x")}/{reader.GetFloat("y")}");
        }

        // Item row: cargo ownership (made_unit_id → crafter share) must survive.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT template_id, container_id, slot_type, made_unit_id, owner FROM items WHERE id = @itemId";
            cmd.Parameters.AddWithValue("@itemId", PackItemId);

            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), $"[{phase}] pack item row missing after restart");
            Assert.True(reader.GetUInt32("template_id") == PackItemTemplateId,
                $"[{phase}] item template clobbered to {reader.GetUInt32("template_id")}");
            Assert.True(reader.GetUInt64("container_id") == SystemContainerDbId,
                $"[{phase}] item container link clobbered to {reader.GetUInt64("container_id")}");
            Assert.True(reader.GetUInt32("made_unit_id") == CrafterCharId,
                $"[{phase}] made_unit_id (cargo ownership) clobbered to {reader.GetUInt32("made_unit_id")}");
            Assert.True(reader.GetUInt32("owner") == PackOwnerCharId,
                $"[{phase}] item owner clobbered to {reader.GetUInt32("owner")}");
        }
    }

    // ================================================================ seeding

    private static void InsertUser(MySqlConnection conn, uint id, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT IGNORE INTO users (id, username, password, email, last_ip) VALUES (@id, @name, '', '', '')";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.ExecuteNonQuery();
    }

    private static void InsertCharacter(MySqlConnection conn, uint charId, string name, uint accountId)
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

    private static void InsertSystemContainer(MySqlConnection conn)
    {
        // SlotType.System = 0xFF; the container's .NET class is what LoadUserItems
        // instantiates (ItemContainer.CreateByTypeName).
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO item_containers (container_id, container_type, slot_type, container_size, owner_id, mate_id) " +
            "VALUES (@containerId, 'AAEmu.Game.Models.Game.Items.Containers.SystemContainer', 255, 0, @ownerId, 0)";
        cmd.Parameters.AddWithValue("@containerId", SystemContainerDbId);
        cmd.Parameters.AddWithValue("@ownerId", PackOwnerCharId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertPackItem(MySqlConnection conn, uint ownerId, uint crafterId)
    {
        // Backpack 26488 (trade pack) in the System container, crafted by crafterId.
        // made_unit_id is the cargo-ownership column that drives the 80/20 split on sale.
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO items (id, type, template_id, container_id, slot_type, slot, count, details, " +
            "lifespan_mins, made_unit_id, unsecure_time, unpack_time, owner, created_at, grade, flags, ucc, " +
            "expire_time, expire_online_minutes, charge_time, charge_count) " +
            "VALUES (@id, 'AAEmu.Game.Models.Game.Items.Backpack', @templateId, @containerId, 255, 0, 1, '', " +
            "0, @madeUnitId, @now, @now, @ownerId, @now, 0, 0, 0, @now, 0, @now, 0)";
        cmd.Parameters.AddWithValue("@id", PackItemId);
        cmd.Parameters.AddWithValue("@templateId", PackItemTemplateId);
        cmd.Parameters.AddWithValue("@containerId", SystemContainerDbId);
        cmd.Parameters.AddWithValue("@madeUnitId", crafterId);
        cmd.Parameters.AddWithValue("@ownerId", ownerId);
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    private static void InsertPlacedPackDoodad(MySqlConnection conn, string plantedAtSql)
    {
        // Seeds the doodad with owner_type 254 = DoodadOwnerType.Character (the value
        // PutDownBackpackEffect writes for an open-field pack).
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT IGNORE INTO doodads (id, owner_id, owner_type, attach_point, template_id, current_phase_id, " +
            "plant_time, growth_time, phase_time, x, y, z, roll, pitch, yaw, scale, item_id, house_id, " +
            "parent_doodad, item_template_id, item_container_id, data, farm_type) " +
            "VALUES (@id, @ownerId, 254, 0, @templateId, @phaseId, @plantTime, @plantTime, @plantTime, " +
            "@x, @y, @z, 0, 0, 0, 1, @itemId, 0, 0, @itemTemplateId, 0, 0, 0)";
        cmd.Parameters.AddWithValue("@id", DoodadDbId);
        cmd.Parameters.AddWithValue("@ownerId", PackOwnerCharId);
        cmd.Parameters.AddWithValue("@templateId", PlacedPackDoodadTemplateId);
        cmd.Parameters.AddWithValue("@phaseId", PlacedPackStartPhaseId);
        cmd.Parameters.AddWithValue("@plantTime", plantedAtSql);
        cmd.Parameters.AddWithValue("@x", PackX.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@y", PackY.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@z", PackZ.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@itemId", PackItemId);
        cmd.Parameters.AddWithValue("@itemTemplateId", PackItemTemplateId);
        cmd.ExecuteNonQuery();
    }
}
