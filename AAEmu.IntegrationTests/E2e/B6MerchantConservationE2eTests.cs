using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// B6 MERCHANT-01 R-run: NPC vendor buy/sell conservation across a kill-9
/// game restart, driven over the REAL wire packets
/// (CSBuyItemsPacket 0x0ae / CSSellItemsPacket 0x0b0) on an authenticated
/// game link — the post-trio-merge (e5db6d390) paths.
///
/// Leg (all amounts copper):
///   1. fund 10_000 → wire-BUY 4 x potato seed 15659 @ 25 (pack 171, seed
///      merchant 8522) → money 9_900, 4 seeds in bag;
///   2. insolvent probe (trio fix #1, live): rig money to 10, wire-BUY 1 x
///      seed → refused, money still 10, seed count still 4; re-fund 9_900;
///   3. wire-SELL the 4-seed stack → refund 12 x 4 = 48 → money 9_948,
///      stack moved to BuyBack (trio fix #2: refund only on success);
///   4. save → MySQL snapshot → kill-9 restart (PID-verified) → snapshot
///      must be EQUAL: money 9_948, no seed rows (BuyBack is SlotType.None,
///      non-persisted by design), nothing negative, nothing duped;
///   5. post-restart function: wire-BUY 1 x seed → money 9_923.
///
/// Bridge "mail" rig/inv/char ops are reused as the read-only observable +
/// funding seam (existing test hooks; no new server code). BuyBack contents
/// are EXPECTED to vanish across the restart (EnterWorldManager wipes the
/// non-persisted container) — conservation means money + persisted rows.
/// </summary>
[Collection("e2e")]
public sealed class B6MerchantConservationE2eTests
{
    private const string Bot = "B6Merchant";
    private const string Account = "b6merchant";
    private const string Password = "e2e-secret";

    private const uint SeedMerchantTemplateId = 8522;
    private const uint SeedTemplateId = 15659;
    private const int SeedPrice = 25;
    private const int SeedRefund = 12;
    private const int BuyCount = 4;
    private const int FundedMoney = 10_000;
    private const int AfterBuyMoney = FundedMoney - BuyCount * SeedPrice; // 9_900
    private const int AfterSellMoney = AfterBuyMoney + BuyCount * SeedRefund; // 9_948
    private const int AfterRebuyMoney = AfterSellMoney - SeedPrice; // 9_923
    private const int SlotTypeInventory = 2;

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Merchant_BuySell_ConservedAcrossKill9_OnRealPacketPaths()
    {
        var startedAt = DateTime.UtcNow;
        var stages = new List<string>();
        BotNetworkSession? session = null;
        uint characterId = 0;
        ulong seedItemId = 0;

        try
        {
            E2eStack.EnsureUp();
            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            stages.Add("connect");
            session = await ConnectAsync();
            characterId = session.CharacterId;
            Assert.True(characterId > 0, "no character id after authenticated connect");
            StopBackgroundLoops(session);
            Drain(GetGameLink(session));

            // Fund through the existing rig seam (money only, no item).
            var rig = Call(bridge, "fund", $"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{Bot}\",\"money\":{FundedMoney}}}", stages);
            Assert.Equal(FundedMoney, rig.GetProperty("money").GetInt64());

            // Position at the live seed merchant (normal spawn path).
            RequireOk(Call(bridge, "teleport-merchant",
                $"{{\"cmd\":\"drive\",\"op\":\"teleportToNpc\",\"bot\":\"{Bot}\",\"npc\":{SeedMerchantTemplateId}}}", stages),
                "teleportToNpc 8522");
            var npcObjId = await PollNpcObjIdAsync(bridge, stages);
            stages.Add($"merchant-objid-{npcObjId}");
            Assert.True(npcObjId > 0, "seed merchant 8522 never spawned in the live world");

            // ---- 1. wire BUY 4 x seed -----------------------------------
            stages.Add("buy-wire");
            GetGameLink(session).SendGameFrame(CSOffsets.CSBuyItemsPacket, 1, body =>
            {
                WriteBc(body, npcObjId);
                WriteBc(body, 0); // doodadObjId — NPC shop
                body.Write(0u); // unkId
                body.Write((byte)1); // nBuy
                body.Write((byte)0); // nBuyBack
                body.Write(SeedTemplateId);
                body.Write((byte)0); // grade (server recomputes)
                body.Write(BuyCount);
                body.Write((byte)0); // ShopCurrencyType.Money
                body.Write(false); // useAAPoint
            });
            _ = GetGameLink(session).ReadFrameUntil(SCOffsets.SCItemTaskSuccessPacket, 20_000);

            Assert.Equal(AfterBuyMoney, await PollMoneyAsync(bridge, AfterBuyMoney, stages, "after-buy"));
            var seeds = await PollSeedStackAsync(bridge, BuyCount, stages, "after-buy");
            seedItemId = seeds.GetProperty("itemId").GetUInt64();
            var seedSlot = seeds.GetProperty("slot").GetByte();
            Assert.True(seedItemId > 0);

            // ---- 2. insolvent probe: refused, nothing moves -------------
            Call(bridge, "rig-poor", $"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{Bot}\",\"money\":10}}", stages);
            stages.Add("buy-insolvent-wire");
            GetGameLink(session).SendGameFrame(CSOffsets.CSBuyItemsPacket, 1, body =>
            {
                WriteBc(body, npcObjId);
                WriteBc(body, 0);
                body.Write(0u);
                body.Write((byte)1);
                body.Write((byte)0);
                body.Write(SeedTemplateId);
                body.Write((byte)0);
                body.Write(1);
                body.Write((byte)0);
                body.Write(false);
            });
            await Task.Delay(3000);
            Drain(GetGameLink(session));
            Assert.Equal(10, await PollMoneyAsync(bridge, 10, stages, "insolvent"));
            Assert.Equal(BuyCount, await PollSeedCountAsync(bridge, stages, "insolvent"));
            Call(bridge, "re-fund", $"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{Bot}\",\"money\":{AfterBuyMoney}}}", stages);

            // ---- 3. wire SELL the stack ---------------------------------
            stages.Add("sell-wire");
            GetGameLink(session).SendGameFrame(CSOffsets.CSSellItemsPacket, 1, body =>
            {
                WriteBc(body, npcObjId);
                WriteBc(body, 0); // unkObjId
                body.Write((byte)1); // num
                body.Write((byte)SlotTypeInventory);
                body.Write(seedSlot);
                body.Write(seedItemId);
                body.Write(0u); // unkId
            });
            Assert.Equal(AfterSellMoney, await PollMoneyAsync(bridge, AfterSellMoney, stages, "after-sell"));
            Assert.Equal(0, await PollSeedCountAsync(bridge, stages, "after-sell"));

            // ---- 4. save, snapshot, kill-9 -------------------------------
            Call(bridge, "save-before-restart", "{\"cmd\":\"save\"}", stages, 180_000);
            var pre = await WaitForPersistedMoneyAsync(characterId, AfterSellMoney, TimeSpan.FromSeconds(120));
            Assert.True(pre != null, $"MySQL never showed money {AfterSellMoney} before restart");
            var preItems = SnapshotItemRows(characterId);
            Assert.DoesNotContain(preItems, r => r.TemplateId == SeedTemplateId);
            Assert.All(preItems, r => Assert.True(r.Count >= 0, "negative item count pre-restart"));

            stages.Add("kill-9-restart");
            var killedPid = E2eStack.RestartGameServer();
            Assert.True(killedPid > 0, "restart killed no game process — no kill happened");
            AssertProcessDead(killedPid, stages);

            var post = SnapshotMoney(characterId);
            Assert.NotNull(post);
            Assert.Equal(AfterSellMoney, post!.Money);
            Assert.Equal(pre.BankMoney, post.BankMoney);
            var postItems = SnapshotItemRows(characterId);
            Assert.Equal(preItems, postItems); // byte-identical persisted rows
            Assert.DoesNotContain(postItems, r => r.TemplateId == SeedTemplateId);
            stages.Add("conservation-holds");

            // ---- 5. shop still functional after restart ------------------
            session.Dispose();
            session = null;
            using var bridgeAfter = new BotDriveClient(E2eStack.BridgePort);
            session = await ConnectAsync();
            StopBackgroundLoops(session);
            Drain(GetGameLink(session));

            RequireOk(Call(bridgeAfter, "teleport-merchant-2",
                $"{{\"cmd\":\"drive\",\"op\":\"teleportToNpc\",\"bot\":\"{Bot}\",\"npc\":{SeedMerchantTemplateId}}}", stages),
                "post-restart teleportToNpc 8522");
            var npcObjId2 = await PollNpcObjIdAsync(bridgeAfter, stages);
            Assert.True(npcObjId2 > 0, "seed merchant 8522 missing after restart");

            stages.Add("rebuy-wire");
            GetGameLink(session).SendGameFrame(CSOffsets.CSBuyItemsPacket, 1, body =>
            {
                WriteBc(body, npcObjId2);
                WriteBc(body, 0);
                body.Write(0u);
                body.Write((byte)1);
                body.Write((byte)0);
                body.Write(SeedTemplateId);
                body.Write((byte)0);
                body.Write(1);
                body.Write((byte)0);
                body.Write(false);
            });
            _ = GetGameLink(session).ReadFrameUntil(SCOffsets.SCItemTaskSuccessPacket, 20_000);
            Assert.Equal(AfterRebuyMoney, await PollMoneyAsync(bridgeAfter, AfterRebuyMoney, stages, "after-rebuy"));
            Assert.Equal(1, await PollSeedCountAsync(bridgeAfter, stages, "after-rebuy"));
            Call(bridgeAfter, "save-after-rebuy", "{\"cmd\":\"save\"}", stages, 180_000);
            var final = await WaitForPersistedMoneyAsync(characterId, AfterRebuyMoney, TimeSpan.FromSeconds(120));
            Assert.True(final != null, $"MySQL never showed money {AfterRebuyMoney} after rebuy");

            stages.Add("PASS");
            WriteEvidence(startedAt, characterId, killedPid, stages, pass: true, detail: "");
        }
        catch (Exception ex)
        {
            WriteEvidence(startedAt, characterId, 0, stages, pass: false, detail: ex.Message);
            throw;
        }
        finally
        {
            session?.Dispose();
            try { E2eStack.CleanupBotRows(Account); } catch { }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<BotNetworkSession> ConnectAsync()
        => await BotNetworkSession.ConnectAsync(Bot, Account, Password,
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

    private static JsonElement Call(BotDriveClient bridge, string stage, string request, List<string> stages, int timeoutMs = 30_000)
    {
        stages.Add(stage);
        var response = bridge.Call(request, timeoutMs);
        RequireOk(response, stage);
        return response;
    }

    private static void RequireOk(JsonElement response, string stage)
    {
        if (response.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            throw new InvalidOperationException(
                $"bridge '{stage}' failed: {(response.TryGetProperty("error", out var e) ? e.GetString() : "?")}");
    }

    private static async Task<uint> PollNpcObjIdAsync(BotDriveClient bridge, List<string> stages)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var response = bridge.Call(
                $"{{\"cmd\":\"drive\",\"op\":\"npcObjId\",\"bot\":\"{Bot}\",\"npc\":{SeedMerchantTemplateId}}}", 30_000);
            RequireOk(response, "npcObjId");
            var objId = response.GetProperty("objId").GetUInt32();
            if (objId > 0)
                return objId;
            await Task.Delay(2000);
        }
        return 0;
    }

    private static async Task<long> PollMoneyAsync(BotDriveClient bridge, long expected, List<string> stages, string tag)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long money = -1;
        while (DateTime.UtcNow < deadline)
        {
            var response = bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"char\",\"bot\":\"{Bot}\"}}", 30_000);
            RequireOk(response, $"char-{tag}");
            money = response.GetProperty("money").GetInt64();
            Assert.True(money >= 0, $"money went NEGATIVE ({money}) at {tag}");
            if (money == expected)
                return money;
            await Task.Delay(500);
        }
        Assert.Equal(expected, money);
        return money;
    }

    private static async Task<JsonElement> PollSeedStackAsync(BotDriveClient bridge, int expectedCount, List<string> stages, string tag)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var match = FindSeedStack(bridge, tag);
            if (match != null && match.Value.GetProperty("count").GetInt32() == expectedCount)
                return match.Value;
            await Task.Delay(500);
        }
        var last = FindSeedStack(bridge, tag);
        Assert.NotNull(last);
        Assert.Equal(expectedCount, last!.Value.GetProperty("count").GetInt32());
        return last.Value;
    }

    private static async Task<int> PollSeedCountAsync(BotDriveClient bridge, List<string> stages, string tag)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var match = FindSeedStack(bridge, tag);
            if (match == null)
                return 0;
            await Task.Delay(500);
        }
        var last = FindSeedStack(bridge, tag);
        return last?.GetProperty("count").GetInt32() ?? 0;
    }

    private static JsonElement? FindSeedStack(BotDriveClient bridge, string tag)
    {
        var response = bridge.Call($"{{\"cmd\":\"mail\",\"op\":\"inv\",\"bot\":\"{Bot}\"}}", 30_000);
        RequireOk(response, $"inv-{tag}");
        foreach (var item in response.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("templateId").GetUInt32() == SeedTemplateId &&
                item.GetProperty("slotType").GetInt32() == SlotTypeInventory)
                return item.Clone();
        }
        return null;
    }

    private sealed record MoneyRow(long Money, long BankMoney);

    private static MoneyRow? SnapshotMoney(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT money, money2 FROM characters WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new MoneyRow(reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    private sealed record ItemRow(int SlotType, uint TemplateId, int Count)
    {
        public override string ToString() => $"({SlotType},{TemplateId},{Count})";
    }

    private static List<ItemRow> SnapshotItemRows(uint characterId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT slot_type, template_id, SUM(count) FROM items WHERE owner = @id " +
            "GROUP BY slot_type, template_id ORDER BY slot_type, template_id";
        cmd.Parameters.AddWithValue("@id", characterId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<ItemRow>();
        while (reader.Read())
            rows.Add(new ItemRow(reader.GetInt32(0), reader.GetUInt32(1), reader.GetInt32(2)));
        return rows;
    }

    private static async Task<MoneyRow?> WaitForPersistedMoneyAsync(uint characterId, long expectedMoney, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = SnapshotMoney(characterId);
            if (snapshot != null && snapshot.Money == expectedMoney)
                return snapshot;
            await Task.Delay(2000);
        }
        return null;
    }

    private static void AssertProcessDead(int pid, List<string> stages)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _ = Process.GetProcessById(pid);
                Thread.Sleep(500);
            }
            catch (ArgumentException)
            {
                stages.Add($"kill-verified-pid-{pid}");
                return;
            }
        }
        throw new InvalidOperationException($"restart kill unverified: pid {pid} still alive");
    }

    private static BotTcpLink GetGameLink(BotNetworkSession session)
        => (BotTcpLink)typeof(BotNetworkSession)
            .GetField("_game", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

    private static void StopBackgroundLoops(BotNetworkSession session)
    {
        if (typeof(BotNetworkSession)
                .GetField("_keepAliveCts", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(session) is CancellationTokenSource cts)
            cts.Cancel();
    }

    private static void Drain(BotTcpLink link) => _ = link.DrainAll();

    private static void WriteBc(PacketStream stream, uint value)
    {
        stream.Write((byte)(value & 0xff));
        stream.Write((byte)((value >> 8) & 0xff));
        stream.Write((byte)((value >> 16) & 0xff));
    }

    private static void WriteEvidence(DateTime startedAt, uint characterId, int killedPid, List<string> stages, bool pass, string detail)
    {
        try
        {
            var dir = Path.Combine(E2eStack.E2eRoot, "logs");
            Directory.CreateDirectory(dir);
            var payload = new
            {
                test = "B6MerchantConservation",
                startedUtc = startedAt,
                characterId,
                killedPid,
                pass,
                detail,
                stages
            };
            File.WriteAllText(Path.Combine(dir, "b6-merchant-conservation-report.json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            var md = new System.Text.StringBuilder();
            md.AppendLine("# b6-merchant-conservation — buy/sell conservation across kill-9 (real CSBuy/CSSell paths)");
            md.AppendLine($"- characterId {characterId}: buy 4 x seed 15659 @ 25 → 9900; sell refund 12 x 4 → 9948; restart equal; rebuy → 9923");
            md.AppendLine($"- killed game pid {killedPid} (PID-verified dead before reboot)");
            md.AppendLine($"- verdict: {(pass ? "PASS" : "FAIL")} ({stages.Count} stages){(string.IsNullOrEmpty(detail) ? "" : $" — {detail}")}");
            File.WriteAllText(Path.Combine(dir, "b6-merchant-conservation-reconcile.md"), md.ToString());
        }
        catch
        {
        }
    }
}
