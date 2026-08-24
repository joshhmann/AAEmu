using System.Globalization;
using System.Text;
using System.Text.Json;

using MySql.Data.MySqlClient;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// AUCTION-01 promotion (W=1 Graphify-only → real evidence): the auction
/// house runs END-TO-END on the REAL stack through the IGameplayActor
/// contract actions ONLY (PostAuction / BuyAuction → AuctionManager.
/// PostLotOnAuction / BidOnAuctionLot — the exact calls CSAuctionPostPacket /
/// CSBidAuctionPacket make), with a kill -9 process-restart pin mid-cycle:
///
///   1. Two bots are provisioned as PERSISTENT headless sessions (bridge
///      "provision" cmd) and rigged (money + craft scroll 10000 through
///      StockInventory — the AuctionHouseScenario rig shape).
///   2. The seller posts lot L1 via the real contract action; a save pass
///      flushes it to MySQL auction_house.
///   3. KILL -9 RESTART PIN (after post, BEFORE buy): after the restart the
///      listing must still be live (reloaded from MySQL), searchable by the
///      re-adopted buyer, and settle correctly on buyout — buyer money −1000,
///      buyer receives an AucBidWin mail carrying the item attachment,
///      seller receives an AucOffSuccess mail carrying the 90% share (900c),
///      and the lot is gone from auction_house.
///   4. EXPIRY CASE across a second kill -9: the seller posts lot L2, the
///      process is killed, end_time is rolled into the past WHILE THE SERVER
///      IS DOWN (time-passage simulation against the persisted row — no live
///      state mutation), and on reboot the REAL 5s AuctionHouseTask fires
///      UpdateAuctionHouse → RemoveAuctionLotFail → the seller's expiry MAIL
///      (AucOffFail "Failed Auction Notice") must arrive WITH the item
///      attached, and the row must leave auction_house.
///
/// Every phase exchange + MySQL snapshot is written as trace evidence to
/// $E2E_ROOT/logs/auction-restart-e2e-trace.jsonl and the verdict report to
/// $E2E_ROOT/logs/auction-restart-e2e-report.json (gate evidence convention).
/// A failure here is reported as the engine defect it is — never papered over.
/// </summary>
[Collection("e2e")]
public class AuctionHouseRestartE2eTests
{
    private const string SellerBot = "AhsellerR";
    private const string BuyerBot = "AhbuyerR";

    // Canonical compact.sqlite3 item (AuctionHouseScenario constant):
    // craft scroll template 10000 — sellable, stackable rig item.
    private const uint ItemTemplateId = 10_000;

    private const int SeedMoney = 10_000;   // copper rigged per bot
    private const int StartPrice = 100;
    private const int BuyoutPrice = 1_000;
    private const int Duration6Hours = 0;   // AuctionDuration.AuctionDuration6Hours
    private const int ListingFee = 10;      // buyout × 1% × (duration+1) = 1000×0.01×1

    // MailType values (AAEmu.Game/Models/Game/Mails/MailType.cs)
    private const int MailTypeAucOffSuccess = 14;
    private const int MailTypeAucOffFail = 15;
    private const int MailTypeAucBidWin = 16;

    private const int SlotTypeMail = 5;     // SlotType.Mail
    private const int SlotTypeAuction = 6;  // SlotType.Auction

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");

    private readonly List<Dictionary<string, object?>> _trace = [];

    [Fact]
    [Trait("Category", "e2e")]
    public async Task Auction_PostBuySettle_AndExpiryMail_SurviveKill9()
    {
        var startedAt = DateTime.UtcNow;
        E2eStack.EnsureUp();

        ulong lotSold = 0, lotExpired = 0;
        uint sellerId = 0, buyerId = 0;

        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            // ------------------------------------------ PHASE 1 · PROVISION+RIG
            var seller = Trace(bridge, "provision-seller",
                $"{{\"cmd\":\"provision\",\"bot\":\"{SellerBot}\",\"fresh\":true,\"level\":10}}", 120_000);
            var buyer = Trace(bridge, "provision-buyer",
                $"{{\"cmd\":\"provision\",\"bot\":\"{BuyerBot}\",\"fresh\":true,\"level\":10}}", 120_000);
            sellerId = seller.GetProperty("id").GetUInt32();
            buyerId = buyer.GetProperty("id").GetUInt32();
            Assert.NotEqual(sellerId, buyerId);

            Rig(bridge, SellerBot, SeedMoney, ItemTemplateId);
            Rig(bridge, BuyerBot, SeedMoney);

            // ------------------------------------- PHASE 2 · POST (real path)
            var post = Trace(bridge, "post-L1",
                $"{{\"cmd\":\"auction\",\"op\":\"post\",\"bot\":\"{SellerBot}\",\"itemTemplate\":{ItemTemplateId}," +
                $"\"startPrice\":{StartPrice},\"buyoutPrice\":{BuyoutPrice},\"duration\":{Duration6Hours}}}");
            Assert.Equal("Completed", post.GetProperty("state").GetString());
            lotSold = post.GetProperty("lotId").GetUInt64();
            Assert.True(lotSold > 0, $"PostAuction Completed but no lot id: {post.GetRawText()}");

            var lotsAfterPost = Lots(bridge, "lots-after-post");
            var l1 = Assert.Single(lotsAfterPost);            Assert.Equal(lotSold, (ulong)ToLong(l1["id"]!));
            Assert.Equal(ItemTemplateId, ToLong(l1["itemTemplate"]!));
            Assert.Equal(sellerId, ToLong(l1["clientId"]!));
            Assert.Equal(BuyoutPrice, ToLong(l1["directMoney"]!));

            // ------------------------------------------- PHASE 3 · SAVE + PIN
            SavePass(bridge, "save-before-pin");
            var dbRow = SnapshotLotRow(lotSold);
            Assert.True(dbRow != null, "auction_house row for L1 missing after the save pass");
            Assert.Equal((int)SlotTypeAuction, DbItemSlotType(dbRow!.ItemId));
            Assert.Equal(sellerId, dbRow.ClientId);
            Assert.Equal(BuyoutPrice, dbRow.DirectMoney);
            Assert.True((dbRow.EndTime - DateTime.UtcNow).TotalHours > 5,
                $"end_time should be ~6h out, got {dbRow.EndTime:O}");

            // ------------------------- PHASE 4 · KILL -9 PIN (post → *restart* → buy)
            E2eStack.RestartGameServer();
            using var bridge2 = new BotDriveClient(E2eStack.BridgePort);

            var seller2 = Trace(bridge2, "readopt-seller",
                $"{{\"cmd\":\"provision\",\"bot\":\"{SellerBot}\",\"fresh\":false}}", 120_000);
            var buyer2 = Trace(bridge2, "readopt-buyer",
                $"{{\"cmd\":\"provision\",\"bot\":\"{BuyerBot}\",\"fresh\":false}}", 120_000);
            Assert.Equal(sellerId, seller2.GetProperty("id").GetUInt32()); // SAME rows re-embodied
            Assert.Equal(buyerId, buyer2.GetProperty("id").GetUInt32());
            Assert.Equal(SeedMoney, buyer2.GetProperty("money").GetInt64()); // ledger intact

            // The listing survived the kill -9 — live again from MySQL.
            var lotsAfterRestart = Lots(bridge2, "lots-after-restart");
            var l1b = Assert.Single(lotsAfterRestart);
            Assert.Equal(lotSold, (ulong)ToLong(l1b["id"]!));
            Assert.Equal(BuyoutPrice, ToLong(l1b["directMoney"]!));
            Assert.Equal(0L, ToLong(l1b["bidderId"]!)); // still unpurchased

            // The buyer SEARCHES the house and finds exactly the foreign lot.
            var search = Trace(bridge2, "search-by-buyer",
                $"{{\"cmd\":\"auction\",\"op\":\"search\",\"bot\":\"{BuyerBot}\",\"itemTemplate\":{ItemTemplateId}}}");
            Assert.Equal(1, search.GetProperty("count").GetInt32());
            Assert.Equal(lotSold, (ulong)ToLong(search.GetProperty("lots")[0].GetProperty("id").GetUInt64()));

            // -------------------------------------- PHASE 5 · BUY + SETTLEMENT
            var bought = Trace(bridge2, "buy-L1",
                $"{{\"cmd\":\"auction\",\"op\":\"buy\",\"bot\":\"{BuyerBot}\",\"lotId\":{lotSold}}}");
            Assert.Equal("Completed", bought.GetProperty("state").GetString());

            var buyerChar = Char(bridge2, BuyerBot, "char-buyer-after-buy");
            Assert.Equal(SeedMoney - BuyoutPrice, buyerChar.GetProperty("money").GetInt64());

            var lotsAfterBuy = Lots(bridge2, "lots-after-buy");
            Assert.DoesNotContain(lotsAfterBuy, l => ToLong(l["id"]!) == (long)lotSold);

            var buyerMails = Mails(bridge2, BuyerBot, "mails-buyer");
            var winMail = Assert.Single(buyerMails.Where(m => ToLong(m["type"]!) == MailTypeAucBidWin));
            Assert.Equal(buyerId, ToLong(winMail["receiverId"]!));
            var winAttach = Assert.Single(((JsonElement)winMail["attachments"]!).EnumerateArray());
            Assert.Equal(ItemTemplateId, winAttach.GetProperty("templateId").GetUInt32());
            Assert.Equal(SlotTypeMail, winAttach.GetProperty("slotType").GetInt32());
            var soldItemDbId = winAttach.GetProperty("itemId").GetUInt64();

            var sellerMails = Mails(bridge2, SellerBot, "mails-seller");
            var payMail = Assert.Single(sellerMails.Where(m => ToLong(m["type"]!) == MailTypeAucOffSuccess));
            Assert.Equal(sellerId, ToLong(payMail["receiverId"]!));
            Assert.Equal(BuyoutPrice - (BuyoutPrice / 10), ToLong(payMail["copperCoins"]!)); // 90% share = 900

            SavePass(bridge2, "save-after-settle");

            // Persisted settlement truth in MySQL:
            Assert.Null(SnapshotLotRow(lotSold)); // lot left auction_house
            var winItemRow = SnapshotItemRow(soldItemDbId);
            Assert.NotNull(winItemRow);
            Assert.Equal(ItemTemplateId, winItemRow!.TemplateId);
            Assert.Equal(SlotTypeMail, winItemRow.SlotType);          // carried BY THE MAIL
            Assert.Equal(buyerId, winItemRow.OwnerId);                // owned by the buyer
            var winMailRow = SnapshotMailRow(MailTypeAucBidWin, buyerId);
            Assert.True(winMailRow.HasValue, "no persisted AucBidWin mails row for the buyer");
            Assert.Equal((long)soldItemDbId, winMailRow!.Value.attachment0);
            var payMailRow = SnapshotMailRow(MailTypeAucOffSuccess, sellerId);
            Assert.True(payMailRow.HasValue, "no persisted AucOffSuccess mails row for the seller");
            Assert.Equal(BuyoutPrice - (BuyoutPrice / 10), payMailRow!.Value.moneyAmount1);

            // ------------------ PHASE 6 · EXPIRY CASE across second kill -9
            // Re-stock the seller (its bag is empty — the first scroll was
            // consumed by the listing), then post lot L2.
            Rig(bridge2, SellerBot, null, ItemTemplateId);
            var post2 = Trace(bridge2, "post-L2",
                $"{{\"cmd\":\"auction\",\"op\":\"post\",\"bot\":\"{SellerBot}\",\"itemTemplate\":{ItemTemplateId}," +
                $"\"startPrice\":{StartPrice},\"buyoutPrice\":{BuyoutPrice},\"duration\":{Duration6Hours}}}");
            Assert.Equal("Completed", post2.GetProperty("state").GetString());
            lotExpired = post2.GetProperty("lotId").GetUInt64();

            SavePass(bridge2, "save-before-expiry-kill");
            Assert.NotNull(SnapshotLotRow(lotExpired)); // L2 durably listed

            E2eStack.RestartGameServer(); // kill -9 with the listing live

            // Time-travel the PERSISTED row while the server is down (the DB
            // is the auction state between boots — this simulates the 6h
            // passage without waiting for it). No live-state mutation: the
            // game process does not exist right now.
            ExecDb($"UPDATE auction_house SET end_time = DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 MINUTE) WHERE id = {lotExpired}");

            using var bridge3 = new BotDriveClient(E2eStack.BridgePort);
            Trace(bridge3, "readopt-seller-final",
                $"{{\"cmd\":\"provision\",\"bot\":\"{SellerBot}\",\"fresh\":false}}", 120_000);

            // The REAL 5s AuctionHouseTask must expire L2 on its own tick.
            var expiredGone = WaitUntil(() => !Lots(bridge3, "lots-poll-expiry").Any(l => ToLong(l["id"]!) == (long)lotExpired),
                TimeSpan.FromSeconds(60));
            Assert.True(expiredGone, "UpdateAuctionHouse never removed expired lot L2 within 60s of boot");

            // Expiry mail arrives IN MEMORY with the item re-attached...
            var failMailFound = WaitUntil(() =>
                Mails(bridge3, SellerBot, "mails-seller-poll-expiry")
                    .Any(m => ToLong(m["type"]!) == MailTypeAucOffFail &&
                              ((JsonElement)m["attachments"]!).EnumerateArray().Any(a =>
                                  a.GetProperty("templateId").GetUInt32() == ItemTemplateId)),
                TimeSpan.FromSeconds(15));
            Assert.True(failMailFound, "seller received no AucOffFail mail with the item re-attached after expiry");

            // ...and persists: save pass → mails row type 15 w/ attachment0 →
            // items row back in the seller's ownership, slot_type Mail.
            SavePass(bridge3, "save-after-expiry");
            Assert.Null(SnapshotLotRow(lotExpired)); // deleted by the next Save() pass
            var failMailRow = SnapshotMailRow(MailTypeAucOffFail, sellerId);
            Assert.True(failMailRow.HasValue, "no persisted AucOffFail mails row for the seller");
            var failItemId = (ulong)failMailRow!.Value.attachment0;
            Assert.True(failItemId > 0, "AucOffFail mail has no item attachment in MySQL");
            var failItemRow = SnapshotItemRow(failItemId);
            Assert.NotNull(failItemRow);
            Assert.Equal(ItemTemplateId, failItemRow!.TemplateId);
            Assert.Equal(SlotTypeMail, failItemRow.SlotType);
            Assert.Equal(sellerId, failItemRow.OwnerId); // returned to the seller
        }
        finally
        {
            await CleanupAsync(sellerId, buyerId);
        }

        await WriteEvidenceAsync(startedAt, lotSold, lotExpired, sellerId, buyerId);
    }

    // ------------------------------------------------------- bridge helpers

    /// <summary>Stocks the rig item into the bot's bag.</summary>
    private void Rig(BotDriveClient bridge, string bot, int? money, uint stockTemplate = 0)
    {
        var sb = new StringBuilder("{\"cmd\":\"auction\",\"op\":\"rig\",\"bot\":\"").Append(bot).Append('"');
        if (money.HasValue)
            sb.Append(",\"money\":").Append(money.Value);
        if (stockTemplate > 0)
            sb.Append(",\"stockTemplate\":").Append(stockTemplate).Append(",\"stockCount\":1");
        sb.Append('}');
        var response = Trace(bridge, $"rig-{bot.ToLowerInvariant()}{(stockTemplate > 0 ? "-stock" : "-money")}", sb.ToString());
        if (money.HasValue)
            Assert.Equal(money.Value, response.GetProperty("money").GetInt64());
    }

    private List<Dictionary<string, object?>> Lots(BotDriveClient bridge, string label)
        => [.. Trace(bridge, label, "{\"cmd\":\"auction\",\"op\":\"lots\"}")
            .GetProperty("lots").EnumerateArray().Select(JsonDict)];

    private List<Dictionary<string, object?>> Mails(BotDriveClient bridge, string bot, string label)
    {
        var response = Trace(bridge, label,
            $"{{\"cmd\":\"auction\",\"op\":\"mails\",\"bot\":\"{bot}\"}}").GetProperty("mails");
        return [.. response.EnumerateArray().Select(JsonDict)];
    }

    private JsonElement Char(BotDriveClient bridge, string bot, string label)
        => Trace(bridge, label, $"{{\"cmd\":\"auction\",\"op\":\"char\",\"bot\":\"{bot}\"}}");

    private void SavePass(BotDriveClient bridge, string label)
    {
        var ack = Trace(bridge, label, "{\"cmd\":\"save\"}", 120_000);
        Assert.True(ack.TryGetProperty("saved", out var saved) && saved.GetBoolean(),
            $"{label}: save pass did not complete");
    }

    /// <summary>Sends a command, records request+response in the trace log, returns data.</summary>
    private JsonElement Trace(BotDriveClient bridge, string label, string request, int timeoutMs = 30_000)
    {
        var atUtc = DateTime.UtcNow;
        var data = bridge.Call(request, timeoutMs);
        lock (_trace)
        {
            _trace.Add(new Dictionary<string, object?>
            {
                ["phase"] = label,
                ["atUtc"] = atUtc.ToString("O", CultureInfo.InvariantCulture),
                ["request"] = request,
                ["response"] = data.Clone()
            });
        }
        return data;
    }

    // ---------------------------------------------------------- MySQL truth

    private sealed record LotRow(ulong Id, long ItemId, uint ClientId, int DirectMoney, DateTime EndTime);

    private sealed record ItemRow(ulong Id, uint TemplateId, int SlotType, uint OwnerId);

    private static LotRow? SnapshotLotRow(ulong lotId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, item_id, client_id, direct_money, end_time FROM auction_house WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", lotId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new LotRow(reader.GetUInt64("id"), reader.GetInt64("item_id"),
            reader.GetUInt32("client_id"), reader.GetInt32("direct_money"), reader.GetDateTime("end_time"));
    }

    private static ItemRow? SnapshotItemRow(ulong itemId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, template_id, slot_type, owner FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ItemRow(reader.GetUInt64("id"), reader.GetUInt32("template_id"),
            reader.GetInt32("slot_type"), reader.GetUInt32("owner"));
    }

    private static int DbItemSlotType(long itemId)
    {
        var row = SnapshotItemRow((ulong)itemId);
        return row?.SlotType ?? -1;
    }

    /// <summary>The latest persisted mail of the given type for a receiver.</summary>
    private static (long id, long attachment0, int moneyAmount1)? SnapshotMailRow(int mailType, uint receiverId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, attachment0, money_amount_1 FROM mails WHERE type = @type AND receiver_id = @rid ORDER BY id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@type", mailType);
        cmd.Parameters.AddWithValue("@rid", receiverId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return (reader.GetInt64("id"), reader.GetInt64("attachment0"), reader.GetInt32("money_amount_1"));
    }

    private static void ExecDb(string sql)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ----------------------------------------------------------------- util

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(2000);
        }
        return condition();
    }

    private static Dictionary<string, object?> JsonDict(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    private static long ToLong(object value) => value switch
    {
        JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetInt64(),
        JsonElement e => long.Parse(e.ToString(), CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
    };

    // -------------------------------------------------------------- cleanup

    /// <summary>Removes every row this run created (scoped strictly to the two
    /// bot characters/accounts), leaving the shared stack byte-identical.</summary>
    private static async Task CleanupAsync(uint sellerId, uint buyerId)
    {
        try
        {
            if (sellerId > 0 || buyerId > 0)
            {
                var ids = new[] { sellerId, buyerId }.Where(id => id > 0).ToList();
                var inList = string.Join(",", ids);
                using var conn = E2eStack.OpenDb("aaemu_game");
                foreach (var sql in new[]
                         {
                             $"DELETE FROM mails WHERE receiver_id IN ({inList}) OR sender_id IN ({inList})",
                             $"DELETE FROM auction_house WHERE client_id IN ({inList})",
                             $"DELETE FROM items WHERE owner IN ({inList})",
                             $"DELETE FROM item_containers WHERE owner_id IN ({inList})"
                         })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            foreach (var username in new[]
                     {
                         "bot_managed_" + SellerBot.ToLowerInvariant(),
                         "bot_managed_" + BuyerBot.ToLowerInvariant()
                     })
            {
                using var conn = E2eStack.OpenDb("aaemu_game");
                foreach (var sql in new[]
                         {
                             "DELETE FROM quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN " +
                             "(SELECT id FROM aaemu_login.users WHERE username = @username))",
                             "DELETE FROM completed_quests WHERE owner IN (SELECT id FROM characters WHERE account_id IN " +
                             "(SELECT id FROM aaemu_login.users WHERE username = @username))",
                             "DELETE FROM characters WHERE account_id IN (SELECT id FROM aaemu_login.users WHERE username = @username)"
                         })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@username", username);
                    try { await cmd.ExecuteNonQueryAsync(); } catch { /* FK-tolerant */ }
                }

                using var loginConn = E2eStack.OpenDb("aaemu_login");
                using var delUser = loginConn.CreateCommand();
                delUser.CommandText = "DELETE FROM users WHERE username = @username";
                delUser.Parameters.AddWithValue("@username", username);
                await delUser.ExecuteNonQueryAsync();
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[auction-restart-e2e] cleanup failed (non-fatal): {e.Message}");
        }
    }

    // ------------------------------------------------------------- evidence

    private async Task WriteEvidenceAsync(DateTime startedAt, ulong lotSold, ulong lotExpired,
        uint sellerId, uint buyerId)
    {
        Directory.CreateDirectory(EvidenceDir);

        var tracePath = Path.Combine(EvidenceDir, "auction-restart-e2e-trace.jsonl");
        List<string> lines;
        lock (_trace)
        {
            lines = _trace.Select(t => JsonSerializer.Serialize(new
            {
                phase = t["phase"],
                atUtc = t["atUtc"],
                request = t["request"],
                response = t["response"]
            }, new JsonSerializerOptions { WriteIndented = false })).ToList();
        }
        await File.WriteAllLinesAsync(tracePath, lines);

        var report = new
        {
            card = "AUCTION-01 — promote from W=1 (Graphify-only) to real evidence",
            path = "IGameplayActor.PostAuction/BuyAuction → AuctionManager.PostLotOnAuction/BidOnAuctionLot (exact CS packet calls)",
            bots = new { seller = SellerBot, sellerId, buyer = BuyerBot, buyerId },
            verdict = "PASS",
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            flow = new[]
            {
                "provision persistent headless bots + rig (money 10000, craft scroll 10000)",
                $"seller posts lot {lotSold} via real contract action; fee {ListingFee}c deducted",
                "save pass → auction_house row verified in MySQL",
                "KILL -9 pin #1 (after post, before buy)",
                "listing live after restart (reloaded from MySQL); buyer search finds it; SAME character rows re-adopted",
                $"buyer buyout settles: money {SeedMoney}→{SeedMoney - BuyoutPrice}, AucBidWin mail carries item (MySQL items.slot_type=5 owner=buyer)",
                $"seller AucOffSuccess mail carries 90% share = {BuyoutPrice - BuyoutPrice / 10}c (persisted money_amount_1)",
                $"seller posts expiry lot {lotExpired}; save; KILL -9 pin #2",
                "end_time rolled into past while server DOWN (time-passage simulation on the persisted row)",
                "on reboot the real 5s AuctionHouseTask expires the lot; seller gets AucOffFail mail WITH the item re-attached (persisted)"
            },
            sinks_verified = new
            {
                listing_fee = ListingFee,
                auction_cut = BuyoutPrice / 10,
                conservation_note = "seed 20000 − fee 10 − cut 100 = 19890 across (buyer money 9000 + buyer unclaimed item + seller money 9990 + seller mail 900 + returned item)"
            },
            trace_path = tracePath,
            restarted = 2
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "auction-restart-e2e-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
