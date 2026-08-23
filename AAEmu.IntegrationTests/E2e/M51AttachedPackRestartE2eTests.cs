using System.Globalization;
using System.Text.Json;

using AAEmu.IntegrationTests.E2e;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M5.1 gap-flag closure (t_1b82b33f — ROADMAP ~line 936): ATTACHED-pack-on-
/// slave restart persistence — the one state M4 never asserted across a
/// kill -9 with the pack re-parented to a cargo point through the REAL
/// gameplay path.
///
/// Unlike the M4Vehicles attached-pack test (which seeds the exact rows the
/// engine writes), this test drives the REAL contract action end-to-end on a
/// live stack: a bot is provisioned through the shared lifecycle and the
/// m3a-m4-replay scenario runs farm → craft → pack → vehicle, whose LOAD-PACK
/// stage fires <see cref="AAEmu.Game.Core.Managers.Bots.GameplayActor.LoadPackOntoVehicle"/>
/// → PackVehicleService.TryLoadCarriedPack → SlaveManager.AttachDoodadAtPoint
/// (retail snap-to-cargo-point). No manual row inserts anywhere.
///
/// The persisted attachment is (PackVehicleService persistence arm):
///   - slaves row: the summoned farm wagon (template 60) owned by the bot;
///   - doodads row: owner_type = Slave(2) + house_id = slave DbId,
///     attach_point ∈ {9..12} (model-1008 cargo points, the capacity source
///     slave_doodad_bindings defines), data = attach_point copy, template
///     6068 / phase 15677, item link (item_id + item_template_id 26488),
///     LOCAL snapped transform;
///   - items row: Backpack 26488 in the bot's System container (slot_type
///     255) — carried by the SLAVE's cargo doodad, not in any bag slot.
///
/// Flow: run the scenario → force a real save pass (bridge "save" cmd) →
/// snapshot the MySQL attachment state → kill -9 restart of ONLY the game
/// process → re-read and require BYTE-EQUAL rows (slave intact per the M4
/// assert set + exactly-one binding row + same attach point + item link +
/// local transform + plant_time not rewritten). Evidence JSON lands in
/// $E2E_ROOT/logs/m51-attached-pack-restart-report.json.
///
/// A failure here is a genuine attachment-survival defect and is reported as
/// such (rows/logs in the evidence), never papered over.
/// </summary>
[Collection("e2e")]
public class M51AttachedPackRestartE2eTests
{
    private const string TemplateName = "m3a-m4-replay";
    private const string BotName = "m51packload";
    private const string BotUsername = "bot_managed_" + BotName; // ManagedUsernamePrefix + lowercase

    // Canonical compact.sqlite3 ids for the route's pack + vehicle.
    private const uint FarmWagonSlaveTemplateId = 60;   // model 1008, cargo points 9-12
    private const uint PackItemTemplateId = 26488;      // 황금 평원 마취제 (trade pack)
    private const uint PlacedPackDoodadTemplateId = 6068;
    private const uint PlacedPackStartPhaseId = 15677;
    private static readonly uint[] CargoAttachPoints = [9, 10, 11, 12]; // farm-wagon capacity (slave_doodad_bindings)
    private static readonly uint[] PackStorageBoxTemplates = [3446, 4893]; // 등짐 보관 상자 cargo-slot markers
    private const int SlotTypeSystem = 255;             // ItemSlotType.System

    private sealed record SlaveSnapshot(uint Id, ulong ItemId, uint TemplateId, int AttachPoint,
        string Name, uint OwnerType, uint OwnerId, uint Summoner, int Hp, int Mp,
        float X, float Y, float Z);

    private sealed record BindingSnapshot(uint DoodadDbId, uint OwnerDbId, uint OwnerType, uint AttachPoint,
        uint TemplateId, uint CurrentPhaseId, DateTime PlantTime, ulong ItemId, uint HouseId, int Data,
        float X, float Y, float Z, uint ItemTemplateId);

    private sealed record ItemSnapshot(ulong Id, string Type, uint TemplateId, ulong ContainerId,
        int SlotType, int Slot, int Count, uint Owner, uint MadeUnitId);

    private sealed record AttachmentSnapshot(SlaveSnapshot Slave, List<BindingSnapshot> Bindings,
        Dictionary<ulong, ItemSnapshot> Items, Dictionary<ulong, ulong> ContainerOwners);

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task M51_AttachedPackOnSlave_LoadedViaRealContractAction_SurvivesKill9_ByteEqual()
    {
        var startedAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        // Cold-world contract: the m3a-m4 route's walk-to-merchant legs only
        // complete synchronously on a FRESH boot (the world adapter teleports
        // the bot to unspawned merchants' spawners). On a warm stack a
        // already-spawned merchant makes MoveToUnit return Running and the
        // scenario crashes on its own audit probe (latent scenario bug,
        // reported — not worked around here). Restart first, like B4 does.
        E2eStack.RestartGameServer();

        string failStage = "", failReason = "", evidenceText = "";
        var scenarioPassed = false;

        try
        {
            // ---------------------------------------------------- 1. REAL ROUTE
            // The scenario drives Craft → PutDown → PackPickup → UseItem summon →
            // BoardVehicle → LoadPackOntoVehicle → DriveVehicle → UnboardVehicle
            // through the contract actions only. On PASS the pack sits attached
            // to the wagon's first free cargo point (unboard only dismounts).
            using (var bridge = new BotDriveClient(E2eStack.BridgePort))
            {
                var response = bridge.Call(
                    $"{{\"cmd\":\"scenario\",\"template\":\"{TemplateName}\",\"bot\":\"{BotName}\",\"fresh\":true}}",
                    timeoutMs: 420_000);

                scenarioPassed = response.TryGetProperty("passed", out var p) && p.GetBoolean();
                failStage = response.TryGetProperty("failStage", out var fs) ? fs.GetString() ?? "" : "";
                failReason = response.TryGetProperty("failReason", out var fr) ? fr.GetString() ?? "" : "";
                evidenceText = response.TryGetProperty("evidence", out var ev) ? ev.GetString() ?? "" : "";

                Assert.True(scenarioPassed,
                    $"m3a-m4-replay scenario FAILED at {failStage}: {failReason}\nEvidence:\n{evidenceText}");

                // Durable-state trigger: run the REAL save pass so every slave /
                // doodad / item row is flushed before the kill (the same surface
                // autosave uses — no direct DB writes).
                var saveAck = bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000);
                Assert.True(saveAck.TryGetProperty("saved", out var savedEl) && savedEl.GetBoolean(),
                    "bridge save pass did not complete before the kill");
            }

            // ------------------------------------------------- 2. PRE SNAPSHOT
            var charId = ResolveCharacterId(BotName);
            Assert.True(charId > 0, $"scenario bot '{BotName}' has no characters row");

            var pre = SnapshotAttachment(charId);
            AssertAttachmentShape(pre, charId, "pre-restart");

            // --------------------------------------------------- 3. KILL -9
            // StopGameServer kills the process tree — only MySQL survives.
            E2eStack.RestartGameServer();

            // ------------------------------------------------ 4. POST ASSERTS
            var post = SnapshotAttachment(charId);
            AssertRestartIntact(pre, post, charId);
        }
        finally
        {
            await CleanupAsync();
        }

        await WriteReportAsync(startedAt, scenarioPassed, failStage, failReason, evidenceText);
    }

    // ------------------------------------------------------------- resolution

    /// <summary>The scenario bot's character DB id (name-normalized match).</summary>
    private static uint ResolveCharacterId(string botName)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM characters WHERE LOWER(name) = @name LIMIT 1";
        cmd.Parameters.AddWithValue("@name", char.ToUpperInvariant(botName[0]) + botName[1..].ToLowerInvariant());
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? 0u : Convert.ToUInt32(raw);
    }

    private static AttachmentSnapshot SnapshotAttachment(uint charId)
    {
        // Exactly the slave this bot summoned (M4 convention: keyed by summoner).
        var slaves = new List<SlaveSnapshot>();
        using (var conn = E2eStack.OpenDb("aaemu_game"))
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, item_id, template_id, attach_point, name, owner_type, owner_id, summoner, hp, mp, x, y, z " +
                    "FROM slaves WHERE summoner = @charId ORDER BY id";
                cmd.Parameters.AddWithValue("@charId", charId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    slaves.Add(new SlaveSnapshot(
                        reader.GetUInt32("id"), reader.GetUInt64("item_id"), reader.GetUInt32("template_id"),
                        reader.GetInt32("attach_point"), reader.GetString("name"),
                        reader.GetUInt32("owner_type"), reader.GetUInt32("owner_id"), reader.GetUInt32("summoner"),
                        reader.GetInt32("hp"), reader.GetInt32("mp"),
                        reader.GetFloat("x"), reader.GetFloat("y"), reader.GetFloat("z")));
                }
            }

            if (slaves.Count == 0)
                return new AttachmentSnapshot(null, [], [], []);

            // Attached-pack binding rows — the exact key the engine writes:
            // owner_type = Slave(2) + house_id = slave DbId.
            var bindings = new List<BindingSnapshot>();
            foreach (var slave in slaves)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, owner_id, owner_type, attach_point, template_id, current_phase_id, plant_time, " +
                    "item_id, house_id, data, x, y, z, item_template_id FROM doodads " +
                    "WHERE owner_type = 2 AND house_id = @slaveId ORDER BY id";
                cmd.Parameters.AddWithValue("@slaveId", slave.Id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    bindings.Add(new BindingSnapshot(
                        reader.GetUInt32("id"), reader.GetUInt32("owner_id"), reader.GetUInt32("owner_type"),
                        reader.GetUInt32("attach_point"), reader.GetUInt32("template_id"),
                        reader.GetUInt32("current_phase_id"), reader.GetDateTime("plant_time"),
                        reader.GetUInt64("item_id"), reader.GetUInt32("house_id"), reader.GetInt32("data"),
                        reader.GetFloat("x"), reader.GetFloat("y"), reader.GetFloat("z"),
                        reader.GetUInt32("item_template_id")));
                }
            }

            // Pack item rows (doodad → item link) + their container owners.
            var items = new Dictionary<ulong, ItemSnapshot>();
            var containerOwners = new Dictionary<ulong, ulong>();
            foreach (var binding in bindings)
            {
                if (binding.ItemId == 0 || items.ContainsKey(binding.ItemId))
                    continue;
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT id, type, template_id, container_id, slot_type, slot, count, owner, made_unit_id " +
                    "FROM items WHERE id = @itemId";
                cmd.Parameters.AddWithValue("@itemId", binding.ItemId);

                ulong containerId;
                ItemSnapshot itemRow;
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        continue; // missing row surfaces as an assertion gap, not a reader throw
                    itemRow = new ItemSnapshot(
                        reader.GetUInt64("id"), reader.GetString("type"), reader.GetUInt32("template_id"),
                        reader.GetUInt64("container_id"), reader.GetInt32("slot_type"), reader.GetInt32("slot"),
                        reader.GetInt32("count"), reader.GetUInt32("owner"), reader.GetUInt32("made_unit_id"));
                    containerId = itemRow.ContainerId;
                }

                items[binding.ItemId] = itemRow;

                if (containerId > 0 && !containerOwners.ContainsKey(containerId))
                {
                    using var ccmd = conn.CreateCommand();
                    ccmd.CommandText = "SELECT owner_id FROM item_containers WHERE container_id = @cid";
                    ccmd.Parameters.AddWithValue("@cid", containerId);
                    var rawOwner = ccmd.ExecuteScalar();
                    containerOwners[containerId] = rawOwner == null || rawOwner is DBNull
                        ? 0u
                        : Convert.ToUInt64(rawOwner);
                }
            }

            return new AttachmentSnapshot(slaves[0], bindings, items, containerOwners);
        }
    }

    // ------------------------------------------------------------ assertions

    /// <summary>Baseline shape of the live attachment BEFORE the kill.</summary>
    private static void AssertAttachmentShape(AttachmentSnapshot snap, uint charId, string phase)
    {
        Assert.True(snap.Slave != null, $"[{phase}] no slaves row found for summoner {charId}");
        var slave = snap.Slave!;

        // Slave itself (M4 assert set): farm wagon, character-owned, item-linked.
        // (attach_point is engine-authored for a real summon — asserted pre==post
        // in <see cref="AssertRestartIntact"/>, never against a seeded literal.)
        Assert.Equal(FarmWagonSlaveTemplateId, slave.TemplateId);
        Assert.Equal(0u, slave.OwnerType);                  // Character owner
        Assert.Equal(charId, slave.OwnerId);
        Assert.Equal(charId, slave.Summoner);
        Assert.True(slave.ItemId > 0, $"[{phase}] wagon slave row lost its summoning-item link");

        // The slave-owned doodad set: 4 invisible pack-storage-box markers
        // (templates 3446/4893 — the cargo capacity source) + exactly ONE
        // pack-carrying row (the LoadPackOntoVehicle attach).
        var packRows = snap.Bindings.Where(b => b.ItemId > 0).ToList();
        var boxRows = snap.Bindings.Where(b => b.ItemId == 0).ToList();

        Assert.True(packRows.Count == 1,
            $"[{phase}] expected exactly ONE pack-carrying binding row on slave {slave.Id}, saw {packRows.Count}");
        var binding = packRows[0];
        Assert.Equal(2u, binding.OwnerType);                // DoodadOwnerType.Slave
        Assert.Equal(slave.Id, binding.HouseId);            // bound to THIS slave
        Assert.Contains(binding.AttachPoint, CargoAttachPoints); // within model-1008 capacity
        Assert.Equal(binding.AttachPoint, (uint)binding.Data);   // attach-point copy convention
        Assert.Equal(PlacedPackDoodadTemplateId, binding.TemplateId);
        Assert.Equal(PlacedPackStartPhaseId, binding.CurrentPhaseId);
        Assert.Equal(PackItemTemplateId, binding.ItemTemplateId);

        // Capacity consistency: every doodad on a valid cargo slot; a slot
        // carries at most ONE PACK (the invisible storage-box markers share
        // the slot with the pack — FindFreeCargoPoint counts them unoccupied).
        Assert.All(snap.Bindings, b => Assert.Contains(b.AttachPoint, CargoAttachPoints));
        Assert.True(packRows.Select(b => b.AttachPoint).Distinct().Count() == packRows.Count,
            $"[{phase}] two packs attached at the same cargo point");
        Assert.All(boxRows, box =>
        {
            Assert.Contains(box.TemplateId, PackStorageBoxTemplates); // invisible marker, never a pack
            Assert.Contains(box.AttachPoint, CargoAttachPoints);
            Assert.Equal(box.AttachPoint, (uint)box.Data);
            Assert.Equal(2u, box.OwnerType);
            Assert.Equal(slave.Id, box.HouseId);
        });
        Assert.True(boxRows.Count > 0,
            $"[{phase}] no pack-storage-box markers persisted (binding spawn rows missing)");

        Assert.True(snap.Items.TryGetValue(binding.ItemId, out var item),
            $"[{phase}] binding row references item {binding.ItemId} but the items row is missing");

        // Pack carried BY THE SLAVE (System container), not floating in a bag.
        Assert.Equal(PackItemTemplateId, item.TemplateId);
        Assert.Equal(SlotTypeSystem, item.SlotType);
        Assert.Equal(1, item.Count);
        Assert.True(item.ContainerId > 0, $"[{phase}] pack item {item.Id} has no System container");
        Assert.True(snap.ContainerOwners.TryGetValue(item.ContainerId, out var containerOwner),
            $"[{phase}] pack item container {item.ContainerId} has no item_containers row");
        Assert.Equal(charId, containerOwner);
    }

    /// <summary>
    /// THE restart assertion: every persisted column of the attached-pack
    /// state must be byte-equal after the kill -9 boot (±2s DATETIME, float
    /// epsilon). Any divergence here IS the persistence defect.
    /// </summary>
    private static void AssertRestartIntact(AttachmentSnapshot pre, AttachmentSnapshot post, uint charId)
    {
        AssertAttachmentShape(post, charId, "post-restart");

        var preSlave = pre.Slave!;
        var postSlave = post.Slave!;
        Assert.Equal(preSlave.Id, postSlave.Id);            // SAME row — no dup, no re-summon rewrite
        Assert.Equal(preSlave.ItemId, postSlave.ItemId);
        Assert.Equal(preSlave.AttachPoint, postSlave.AttachPoint); // engine-authored binding survives
        Assert.Equal(preSlave.Name, postSlave.Name);
        Assert.Equal(preSlave.Hp, postSlave.Hp);
        Assert.Equal(preSlave.Mp, postSlave.Mp);
        Assert.True(MathF.Abs(preSlave.X - postSlave.X) < 0.01f &&
                    MathF.Abs(preSlave.Y - postSlave.Y) < 0.01f &&
                    MathF.Abs(preSlave.Z - postSlave.Z) < 0.01f,
            $"slaves row position moved across restart: ({preSlave.X},{preSlave.Y},{preSlave.Z}) → ({postSlave.X},{postSlave.Y},{postSlave.Z})");

        Assert.Equal(pre.Bindings.Count, post.Bindings.Count);
        foreach (var preBinding in pre.Bindings)
        {
            var postBinding = post.Bindings.SingleOrDefault(b => b.DoodadDbId == preBinding.DoodadDbId);
            Assert.True(postBinding != null,
                $"attached-pack binding doodads row {preBinding.DoodadDbId} vanished over the restart");

            Assert.Equal(preBinding.OwnerType, postBinding.OwnerType);
            Assert.Equal(preBinding.OwnerDbId, postBinding.OwnerDbId);
            Assert.Equal(preBinding.HouseId, postBinding.HouseId);      // same slave binding
            Assert.Equal(preBinding.AttachPoint, postBinding.AttachPoint); // same cargo point
            Assert.Equal(preBinding.Data, postBinding.Data);
            Assert.Equal(preBinding.TemplateId, postBinding.TemplateId);
            Assert.Equal(preBinding.CurrentPhaseId, postBinding.CurrentPhaseId);
            Assert.Equal(preBinding.ItemId, postBinding.ItemId);        // same pack instance link
            Assert.Equal(preBinding.ItemTemplateId, postBinding.ItemTemplateId);

            // Maturation clock must not be rewritten at boot (M3b-1 class).
            Assert.True(Math.Abs((postBinding.PlantTime - preBinding.PlantTime).TotalSeconds) < 2,
                $"plant_time clobbered over restart: stored {postBinding.PlantTime:O}, pre {preBinding.PlantTime:O}");

            // LOCAL snapped transform must not be recomputed/world-spaced.
            Assert.True(MathF.Abs(preBinding.X - postBinding.X) < 0.001f &&
                        MathF.Abs(preBinding.Y - postBinding.Y) < 0.001f &&
                        MathF.Abs(preBinding.Z - postBinding.Z) < 0.001f,
                $"local cargo transform clobbered over restart: " +
                $"({preBinding.X},{preBinding.Y},{preBinding.Z}) → ({postBinding.X},{postBinding.Y},{postBinding.Z})");
        }

        foreach (var (itemId, preItem) in pre.Items)
        {
            Assert.True(post.Items.TryGetValue(itemId, out var postItem),
                $"pack item row {itemId} vanished over the restart");
            Assert.Equal(preItem.Type, postItem.Type);
            Assert.Equal(preItem.TemplateId, postItem.TemplateId);
            Assert.Equal(preItem.ContainerId, postItem.ContainerId);    // still in the System container
            Assert.Equal(preItem.SlotType, postItem.SlotType);
            Assert.Equal(preItem.Slot, postItem.Slot);
            Assert.Equal(preItem.Count, postItem.Count);
            Assert.Equal(preItem.Owner, postItem.Owner);
            Assert.Equal(preItem.MadeUnitId, postItem.MadeUnitId);      // cargo ownership (80/20 split base)
            Assert.Equal(pre.ContainerOwners[preItem.ContainerId], post.ContainerOwners[postItem.ContainerId]);
        }
    }

    // ---------------------------------------------------------------- report

    private async Task WriteReportAsync(DateTime startedAt, bool scenarioPassed,
        string failStage, string failReason, string evidenceText)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            gap_flag = "t_1b82b33f — attached-pack-on-slave (LoadPackOntoVehicle) had no dedicated restart assertion",
            card = "M5.1 DoD closure · ROADMAP restart-persistence row",
            path = "real contract action: GameplayActor.LoadPackOntoVehicle → PackVehicleService.TryLoadCarriedPack → SlaveManager.AttachDoodadAtPoint",
            scenario = TemplateName,
            bot = BotName,
            verdict = scenarioPassed ? "PASS" : "FAIL",
            failStage,
            failReason,
            proxy_note = "scripted-actor / bot-functional evidence — H (feel) stays UNKNOWN",
            restarted_at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            asserted_rows = new[]
            {
                "slaves (summoner-keyed): id/item_id/template_id/attach_point/owner_type/owner_id/summoner/hp/mp/x/y/z",
                "doodads (owner_type=2 AND house_id=slave DbId): id/owner/attach_point/template/current_phase/plant_time/item link/house_id/data/local transform",
                "items (doodad.item_id): type/template/container/slot_type/slot/count/owner/made_unit_id",
                "item_containers (System container owner)"
            },
            evidence = evidenceText.Length > 4000 ? evidenceText[..4000] + "…(truncated)" : evidenceText
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "m51-attached-pack-restart-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    // --------------------------------------------------------------- cleanup

    /// <summary>Removes every row this run created (scoped strictly by the bot
    /// character id chain), leaving the shared stack byte-identical.</summary>
    private static async Task CleanupAsync()
    {
        try
        {
            var charId = ResolveCharacterId(BotName);
            if (charId > 0)
            {
                using var conn = E2eStack.OpenDb("aaemu_game");
                foreach (var sql in new[]
                         {
                             "DELETE FROM doodads WHERE owner_id = @charId OR house_id IN (SELECT id FROM slaves WHERE summoner = @charId)",
                             "DELETE FROM items WHERE owner = @charId",
                             "DELETE FROM item_containers WHERE owner_id = @charId",
                             "DELETE FROM slaves WHERE summoner = @charId OR owner_id = @charId"
                         })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@charId", charId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // Character + account rows (direct, username-scoped — the shared
            // CleanupBotRows interpolates quoting into the parameter and never
            // matches, so it cannot be relied on for teardown here).
            using var conn2 = E2eStack.OpenDb("aaemu_game");
            foreach (var sql in new[]
                     {
                         "DELETE FROM quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                         "DELETE FROM completed_quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                         "DELETE FROM playerbot_metadata WHERE character_id IN (SELECT id FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username))",
                         "DELETE FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username)"
                     })
            {
                using var cmd2 = conn2.CreateCommand();
                cmd2.CommandText = sql;
                cmd2.Parameters.AddWithValue("@username", BotUsername);
                try { await cmd2.ExecuteNonQueryAsync(); } catch { /* FK-tolerant, mirrors shared helper */ }
            }

            using var loginConn = E2eStack.OpenDb("aaemu_login");
            using var delUser = loginConn.CreateCommand();
            delUser.CommandText = "DELETE FROM users WHERE username = @username";
            delUser.Parameters.AddWithValue("@username", BotUsername);
            await delUser.ExecuteNonQueryAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[m51-attached-pack] cleanup failed (non-fatal): {e.Message}");
        }
    }
}
