using System.Globalization;
using System.Text.Json;

using AAEmu.IntegrationTests.E2e;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// M8 C4 slice-5 (ROADMAP M8 living-village contracts): restart MID-ROUTE
/// attachment equality for the hauler chain.
///
/// The m3a-m4-replay scenario drives the slice-1 path end-to-end on a live
/// stack (farm → craft → pack → vehicle LOAD → drive → unboard): on PASS the
/// pack sits attached to the wagon's first free cargo point through the REAL
/// GameplayActor.LoadPackOntoVehicle → PackVehicleService.TryLoadCarriedPack
/// → SlaveManager.AttachDoodadAtPoint chain — i.e. mid-route (loaded and
/// moved, pre-sale). No manual row inserts anywhere.
///
/// Flow: run the scenario → force a real save pass (bridge "save" cmd) →
/// snapshot the MySQL attachment state (M51 byte-equal shape: slaves row +
/// slave-owned binding rows + pack item rows + container owners) AND the
/// bot's ledger state (characters.money/money2, owned item rows, owned
/// container rows, received mail money) → kill -9 restart of ONLY the game
/// process (E2eStack.RestartGameServer: PID-handle kill + confirmed exit —
/// never pkill) → re-read and require BYTE-EQUAL attachments (same rows,
/// same attach point, same item link, same local transform, plant_time not
/// rewritten) AND ledger equality (same copper everywhere: no dup, no loss).
/// Evidence JSON lands in $E2E_ROOT/logs/c5s5-hauler-midroute-restart-report.json.
///
/// Runs on an ISOLATED lane: E2E_ROOT + E2E_*_PORT + E2E_DB_PORT +
/// COMPOSE_PROJECT_NAME all differ from the shared stack (M6 soak pattern);
/// KillStaleServers only ever touches dotnet processes whose cwd sits under
/// this lane's root (path-segment match), and teardown is PID-verified.
///
/// A failure here is a genuine restart-persistence defect and is reported as
/// such (rows/logs in the evidence), never papered over.
/// </summary>
[Collection("e2e")]
public class HaulerMidRouteRestartE2eTests
{
    private const string TemplateName = "m3a-m4-replay";
    private const string BotName = "c5s5midroute";
    private const string BotUsername = "bot_managed_" + BotName; // ManagedUsernamePrefix + lowercase

    // Canonical compact.sqlite3 ids for the route's pack + vehicle (M51 shape).
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

    // Ledger surface: every persisted copper position of the bot. Datetime
    // columns (mail send/open/received dates, item unsecure/unpack/created
    // times) are deliberately EXCLUDED — they are metadata, not money; the
    // ledger law covers money, money2, and mail copper exactly.
    private sealed record LedgerItemRow(ulong Id, string Type, uint TemplateId, ulong ContainerId,
        int SlotType, int Slot, int Count, uint Owner, uint MadeUnitId, int Grade);

    private sealed record LedgerContainerRow(ulong ContainerId, string ContainerType, int SlotType, int Size, ulong OwnerId);

    private sealed record LedgerMailRow(int Id, int Type, int Status, string Title,
        int Money1, int Money2, int Money3, long[] Attachments);

    private sealed record LedgerSnapshot(long Money, long BankMoney, List<LedgerItemRow> Items,
        List<LedgerContainerRow> Containers, List<LedgerMailRow> Mails);

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Hauler_MidRoute_LoadedPackAndLedger_SurviveKill9_ByteEqual()
    {
        var startedAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        // Cold-world contract (M51/B4 lesson): the m3a-m4 route's
        // walk-to-merchant legs only complete synchronously on a FRESH boot.
        // Restart first.
        E2eStack.RestartGameServer();

        string failStage = "", failReason = "", evidenceText = "";
        var scenarioPassed = false;

        try
        {
            // ---------------------------------------------------- 1. REAL ROUTE
            // Craft → PutDown → PackPickup → UseItem summon → BoardVehicle →
            // LoadPackOntoVehicle → DriveVehicle → UnboardVehicle through the
            // contract actions only. On PASS the pack sits attached to the
            // wagon's first free cargo point (unboard only dismounts) —
            // mid-route: loaded and moved, pre-sale.
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

                // Durable-state trigger: run the REAL save pass so every
                // slave / doodad / item / mail row is flushed before the kill
                // (the same surface autosave uses — no direct DB writes).
                var saveAck = bridge.Call("{\"cmd\":\"save\"}", timeoutMs: 120_000);
                Assert.True(saveAck.TryGetProperty("saved", out var savedEl) && savedEl.GetBoolean(),
                    "bridge save pass did not complete before the kill");
            }

            // --------------------------------------------- 2. PRE SNAPSHOTS
            var charId = ResolveCharacterId(BotName);
            Assert.True(charId > 0, $"scenario bot '{BotName}' has no characters row");

            var preAttachment = SnapshotAttachment(charId);
            AssertAttachmentShape(preAttachment, charId, "pre-restart");

            var preLedger = SnapshotLedger(charId);
            AssertLedgerSane(preLedger, charId, "pre-restart");

            // --------------------------------------------------- 3. KILL -9
            // RestartGameServer kills ONLY the game process tree via its PID
            // handle (confirmed exit) — MySQL + login stay. Never pkill.
            var killedPid = E2eStack.RestartGameServer();

            // ---------------------------------------------- 4. POST ASSERTS
            var postAttachment = SnapshotAttachment(charId);
            AssertRestartIntact(preAttachment, postAttachment, charId);

            var postLedger = SnapshotLedger(charId);
            AssertLedgerEqual(preLedger, postLedger, charId, killedPid);
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

    private static LedgerSnapshot SnapshotLedger(uint charId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");

        long money, bankMoney;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT money, money2 FROM characters WHERE id = @charId";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"characters row {charId} vanished mid-snapshot");
            money = reader.GetInt64("money");
            bankMoney = reader.GetInt64("money2");
        }

        var items = new List<LedgerItemRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, type, template_id, container_id, slot_type, slot, count, owner, made_unit_id, grade " +
                "FROM items WHERE owner = @charId ORDER BY id";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new LedgerItemRow(
                    reader.GetUInt64("id"), reader.GetString("type"), reader.GetUInt32("template_id"),
                    reader.GetUInt64("container_id"), reader.GetInt32("slot_type"), reader.GetInt32("slot"),
                    reader.GetInt32("count"), reader.GetUInt32("owner"), reader.GetUInt32("made_unit_id"),
                    reader.GetInt32("grade")));
            }
        }

        var containers = new List<LedgerContainerRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT container_id, container_type, slot_type, container_size, owner_id " +
                "FROM item_containers WHERE owner_id = @charId ORDER BY container_id";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                containers.Add(new LedgerContainerRow(
                    reader.GetUInt64("container_id"), reader.GetString("container_type"),
                    reader.GetInt32("slot_type"), reader.GetInt32("container_size"),
                    reader.GetUInt64("owner_id")));
            }
        }

        var mails = new List<LedgerMailRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, type, status, title, money_amount_1, money_amount_2, money_amount_3, " +
                "attachment0, attachment1, attachment2, attachment3, attachment4, " +
                "attachment5, attachment6, attachment7, attachment8, attachment9 " +
                "FROM mails WHERE receiver_id = @charId ORDER BY id";
            cmd.Parameters.AddWithValue("@charId", charId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                mails.Add(new LedgerMailRow(
                    reader.GetInt32("id"), reader.GetInt32("type"), reader.GetInt32("status"),
                    reader.GetString("title"),
                    reader.GetInt32("money_amount_1"), reader.GetInt32("money_amount_2"),
                    reader.GetInt32("money_amount_3"),
                    [
                        reader.GetInt64("attachment0"), reader.GetInt64("attachment1"),
                        reader.GetInt64("attachment2"), reader.GetInt64("attachment3"),
                        reader.GetInt64("attachment4"), reader.GetInt64("attachment5"),
                        reader.GetInt64("attachment6"), reader.GetInt64("attachment7"),
                        reader.GetInt64("attachment8"), reader.GetInt64("attachment9")
                    ]));
            }
        }

        return new LedgerSnapshot(money, bankMoney, items, containers, mails);
    }

    // ------------------------------------------------------------ assertions

    /// <summary>Baseline shape of the live attachment BEFORE the kill (M51 shape).</summary>
    private static void AssertAttachmentShape(AttachmentSnapshot snap, uint charId, string phase)
    {
        Assert.True(snap.Slave != null, $"[{phase}] no slaves row found for summoner {charId}");
        var slave = snap.Slave!;

        Assert.Equal(FarmWagonSlaveTemplateId, slave.TemplateId);
        Assert.Equal(0u, slave.OwnerType);                  // Character owner
        Assert.Equal(charId, slave.OwnerId);
        Assert.Equal(charId, slave.Summoner);
        Assert.True(slave.ItemId > 0, $"[{phase}] wagon slave row lost its summoning-item link");

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

    /// <summary>Baseline sanity of the ledger BEFORE the kill: the bot holds
    /// its pack in the System container and its copper positions read clean.</summary>
    private static void AssertLedgerSane(LedgerSnapshot snap, uint charId, string phase)
    {
        Assert.True(snap.Money >= 0, $"[{phase}] negative inventory copper");
        Assert.True(snap.BankMoney >= 0, $"[{phase}] negative bank copper");
        Assert.True(snap.Items.Count > 0, $"[{phase}] bot owns no item rows at all");
        Assert.True(snap.Containers.Count > 0, $"[{phase}] bot owns no container rows at all");
    }

    /// <summary>
    /// THE restart attachment assertion (M51 byte-equal shape): every
    /// persisted column of the attached-pack state must be byte-equal after
    /// the kill -9 boot (±2s DATETIME, float epsilon). Any divergence here IS
    /// the persistence defect.
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

    /// <summary>
    /// THE restart ledger assertion: every persisted copper position must be
    /// identical after the kill -9 boot — no dup, no loss. The killed game
    /// PID is recorded so a silent no-kill (stale process surviving) cannot
    /// masquerade as a clean restart.
    /// </summary>
    private static void AssertLedgerEqual(LedgerSnapshot pre, LedgerSnapshot post, uint charId, int killedPid)
    {
        Assert.True(killedPid > 0, "no game PID was killed — the restart cannot be trusted");

        Assert.Equal(pre.Money, post.Money);                // inventory copper identical
        Assert.Equal(pre.BankMoney, post.BankMoney);        // bank copper identical

        Assert.Equal(pre.Items.Count, post.Items.Count);
        foreach (var preItem in pre.Items)
        {
            var postItem = post.Items.SingleOrDefault(i => i.Id == preItem.Id);
            Assert.True(postItem != null, $"ledger item row {preItem.Id} vanished over the restart");
            Assert.Equal(preItem.Type, postItem.Type);
            Assert.Equal(preItem.TemplateId, postItem.TemplateId);
            Assert.Equal(preItem.ContainerId, postItem.ContainerId);
            Assert.Equal(preItem.SlotType, postItem.SlotType);
            Assert.Equal(preItem.Slot, postItem.Slot);
            Assert.Equal(preItem.Count, postItem.Count);
            Assert.Equal(preItem.Owner, postItem.Owner);
            Assert.Equal(preItem.MadeUnitId, postItem.MadeUnitId);
            Assert.Equal(preItem.Grade, postItem.Grade);
        }

        Assert.Equal(pre.Containers.Count, post.Containers.Count);
        foreach (var preContainer in pre.Containers)
        {
            var postContainer = post.Containers.SingleOrDefault(c => c.ContainerId == preContainer.ContainerId);
            Assert.True(postContainer != null,
                $"ledger container row {preContainer.ContainerId} vanished over the restart");
            Assert.Equal(preContainer.ContainerType, postContainer.ContainerType);
            Assert.Equal(preContainer.SlotType, postContainer.SlotType);
            Assert.Equal(preContainer.Size, postContainer.Size);
            Assert.Equal(preContainer.OwnerId, postContainer.OwnerId);
        }

        Assert.Equal(pre.Mails.Count, post.Mails.Count);
        foreach (var preMail in pre.Mails)
        {
            var postMail = post.Mails.SingleOrDefault(m => m.Id == preMail.Id);
            Assert.True(postMail != null, $"ledger mail {preMail.Id} vanished over the restart");
            Assert.Equal(preMail.Type, postMail.Type);
            Assert.Equal(preMail.Status, postMail.Status);
            Assert.Equal(preMail.Title, postMail.Title);
            Assert.Equal(preMail.Money1, postMail.Money1);
            Assert.Equal(preMail.Money2, postMail.Money2);
            Assert.Equal(preMail.Money3, postMail.Money3);
            Assert.Equal(preMail.Attachments, postMail.Attachments);
        }

        // The ledger law: total copper in every persisted position is
        // identical — the restart created and destroyed nothing.
        var preCopper = pre.Money + pre.BankMoney + pre.Mails.Sum(m => (long)m.Money1 + m.Money2 + m.Money3);
        var postCopper = post.Money + post.BankMoney + post.Mails.Sum(m => (long)m.Money1 + m.Money2 + m.Money3);
        Assert.Equal(preCopper, postCopper);
    }

    // ---------------------------------------------------------------- report

    private async Task WriteReportAsync(DateTime startedAt, bool scenarioPassed,
        string failStage, string failReason, string evidenceText)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new
        {
            slice = "M8 C4 slice-5 — restart mid-route attachment equality for the hauler chain",
            gap_flag = "hauler chain had no mid-route kill -9 assertion: pack loaded via the slice-1 path + driven, then ledger + attachment equality across the kill",
            card = "M8 C4 slice-5 · ROADMAP living-village restart row",
            path = "real contract actions via m3a-m4-replay (LoadPackOntoVehicle → DriveVehicle), kill -9 of ONLY the game process, MySQL byte-compare",
            lane = E2eStack.E2eRoot,
            source_revision = E2eStack.SourceRevision,
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
                "item_containers (System container owner)",
                "LEDGER: characters(money, money2) + all owned items + owned containers + received mail money/attachments + total-copper law"
            },
            evidence = evidenceText.Length > 4000 ? evidenceText[..4000] + "…(truncated)" : evidenceText
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "c5s5-hauler-midroute-restart-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    // --------------------------------------------------------------- cleanup

    /// <summary>Removes every row this run created (scoped strictly by the bot
    /// character id chain + username), leaving the lane DB byte-identical.</summary>
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
            Console.WriteLine($"[c5s5-midroute] cleanup failed (non-fatal): {e.Message}");
        }
    }
}
