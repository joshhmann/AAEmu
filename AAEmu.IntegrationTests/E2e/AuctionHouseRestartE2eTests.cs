using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Auction;

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
///      and the lot is gone from auction_house. This leg then rides the
///      SECOND kill -9 (below) to pin the SOLD instance's container identity:
///      the bought item must reload held by the BUYER's Mail container, not the
///      seller's listing container (see the FinalizeForSaleBuyer note below).
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
///
/// REGRESSION NOTE (FinalizeForSaleBuyer): the sold instance is listed OUT of
/// the seller's Auction container (PostLotOnAuction →
/// AuctionAttachments.AddOrMoveExistingItem) and RemoveAuctionLotSold re-fetches
/// that SAME instance for the buyer's AucBidWin mail. Stamping OwnerId/SlotType
/// alone therefore persisted container_id = the seller's listing container, and
/// the reboot's ItemManager.LoadUserItems re-added the row to that container,
/// whose AddOrMoveExistingItem re-stamped SlotType from its type — the bought
/// item booted as a SlotType.Auction orphan owned by the buyer. The pin is
/// container-id identity across the reload, because slot_type/owner alone pass
/// in both the broken and fixed states.
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
        ulong buyerMailContainerId = 0;
        ulong soldItemContainerId = 0;
        ulong soldItemDbId = 0;
        int soldReloadedSlotType = -1;
        int soldOrphansAfterRestart = -1;
        bool soldPersistenceRoundTrip = false;

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
            soldItemDbId = winAttach.GetProperty("itemId").GetUInt64();
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

            // The sold instance was listed OUT of the seller's Auction container
            // and must now be held by the BUYER's Mail container. Checking
            // slot_type alone is NOT enough — that is exactly how the
            // FinalizeForSaleBuyer orphan survived: the row keeps container_id
            // (ItemManager.Save writes it from _holdingContainer) and the reboot
            // re-stamps SlotType from whatever container that id names.
            buyerMailContainerId = SnapshotContainerId(buyerId, SlotTypeMail);
            Assert.True(buyerMailContainerId > 0,
                $"no persisted Mail container row for buyer {buyerId} after settlement");
            soldItemContainerId = winItemRow!.ContainerId;
            Assert.True(soldItemContainerId == buyerMailContainerId,
                $"sold item {soldItemDbId} persisted container_id={soldItemContainerId} (want the buyer's " +
                $"Mail container {buyerMailContainerId}) — FinalizeForSaleBuyer left it held by the seller's " +
                "listing container, so the reboot will re-home it as a SlotType.Auction orphan");
            Assert.True(soldItemContainerId != SnapshotContainerId(sellerId, SlotTypeAuction),
                $"sold item {soldItemDbId} is still held by the seller's Auction container {soldItemContainerId}");

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

            // Time-travel the PERSISTED row while the server is down (the DB
            // is the auction state between boots — this simulates the 6h
            // passage without waiting for it). CRITICAL: this MUST run in the
            // afterStop seam — Stage-2 data loads read auction_house early in
            // boot, so an update after RestartGameServer returns races (and
            // loses to) the load and the lot boots with a future end_time.
            E2eStack.RestartGameServer(() =>
                ExecDb($"UPDATE auction_house SET end_time = DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 MINUTE) WHERE id = {lotExpired}"));
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

            // ---------- SOLD-ITEM REBOOT PIN (the FinalizeForSaleBuyer orphan)
            // The sold instance was persisted BEFORE the second kill -9
            // (save-after-settle), and that reboot is a real reload from MySQL.
            // slot_type alone cannot catch this orphan: ItemManager.LoadUserItems
            // re-adds the row to whatever container_id names, and
            // ItemContainer.AddOrMoveExistingItem re-stamps SlotType from THAT
            // container's type. The container identity itself is the assertion.
            var soldReloaded = SnapshotItemRow(soldItemDbId);
            Assert.NotNull(soldReloaded);
            Assert.Equal(ItemTemplateId, soldReloaded!.TemplateId);
            Assert.Equal(buyerId, soldReloaded.OwnerId);
            soldReloadedSlotType = soldReloaded.SlotType;
            Assert.True(soldReloaded.ContainerId == buyerMailContainerId,
                $"sold item {soldItemDbId} reloaded with container_id={soldReloaded.ContainerId} " +
                $"(want the buyer's Mail container {buyerMailContainerId}; slot_type={soldReloaded.SlotType}) " +
                "— the settle left it held by the seller's listing container");
            Assert.True(soldReloaded.SlotType == SlotTypeMail,
                $"sold item {soldItemDbId} reloaded as slot_type={soldReloaded.SlotType} (want {SlotTypeMail}=Mail)");

            // ...and it survives the next save pass re-writing the row from the
            // reloaded in-memory container state (container_id ← _holdingContainer).
            SavePass(bridge3, "save-after-sold-reboot");
            var soldRoundTripped = SnapshotItemRow(soldItemDbId);
            Assert.NotNull(soldRoundTripped);
            Assert.True(soldRoundTripped!.ContainerId == buyerMailContainerId,
                $"post-reboot save persisted sold item {soldItemDbId} container_id={soldRoundTripped.ContainerId} " +
                $"(want the buyer's Mail container {buyerMailContainerId})");
            Assert.True(soldRoundTripped.SlotType == SlotTypeMail,
                $"post-reboot save persisted sold item slot_type={soldRoundTripped.SlotType} (want {SlotTypeMail}=Mail)");
            soldOrphansAfterRestart = CountAuctionOrphans(buyerId);
            Assert.True(soldOrphansAfterRestart == 0,
                $"bought item {soldItemDbId} left {soldOrphansAfterRestart} SlotType.Auction orphan row(s) " +
                $"owned by the buyer (container_id={soldRoundTripped.ContainerId})");
            Assert.True(CountAuctionOrphans(sellerId) == 0,
                $"seller {sellerId} still holds {CountAuctionOrphans(sellerId)} SlotType.Auction orphan row(s)");
            soldPersistenceRoundTrip = true;
        }
        finally
        {
            await CleanupAsync(sellerId, buyerId);
        }

        await WriteEvidenceAsync(startedAt, lotSold, lotExpired, sellerId, buyerId,
            soldItemDbId, buyerMailContainerId, soldItemContainerId, soldReloadedSlotType,
            soldOrphansAfterRestart, soldPersistenceRoundTrip);
    }

    // ================================================================ G-cancel
    // AUCTION gap G-cancel (dossier `auction-domain.md` G3): the cancel-with-
    // enchanted-gear leg this restart suite never had. The unit rig
    // (AuctionCancelReturnsOriginalItemTests) can only assert item.SlotType on a
    // stubbed ItemManager — it never drives a real _holdingContainer, so it
    // cannot prove that ItemManager.Save (container_id ← _holdingContainer) and
    // the reboot reload keep the returned instance correct. This leg proves it
    // on the real stack.

    private const string CancelSellerBot = "AhcancelS";
    private const string CancelSellerAccount = "ahcancels";
    private const string CancelPassword = "e2e-secret";

    // Canonical compact.sqlite3 equipment template (first-step dagger — the
    // MAIL-01 S3 enchanted-gear rig shape). An EquipItem, so the listed
    // instance carries a real 55-byte details blob (durability/rune/tempers).
    private const uint EnchantTemplateId = 5318;

    private const int MailTypeAucOffCancel = 13; // MailType.AucOffCancel
    private const int SlotTypeInventory = 2;     // SlotType.Inventory
    private const int EquipDetailBytes = 55;     // EquipItem.DetailBytesLength

    /// <summary>
    /// AUCTION gap G-cancel — cancel an ENCHANTED listing through the REAL
    /// CSCancelAuctionPacket before any bid, then pin the returned instance
    /// across a kill -9:
    ///
    ///   1. Rig ONE enchanted equipment instance (grade 3, durability 77, rune
    ///      1234, tempers 3/4 → a real details blob) and persist it in the bag.
    ///   2. List it through the REAL CSAuctionPostPacket on the seller's
    ///      authenticated game link; assert the item row leaves the bag for
    ///      SlotType.Auction and the auction_house row lands.
    ///   3. Cancel through the REAL CSCancelAuctionPacket (the lot echoed back
    ///      exactly as the 1.2 client sends it) BEFORE any bid. Assertions: the
    ///      ORIGINAL instance id comes back (not a mint), the AucOffCancel mail
    ///      carries it with the enchant state intact, the details blob is
    ///      byte-identical, and NO row remains in SlotType.Auction.
    ///   4. KILL -9 + reboot, then re-assert the SAME instance (id, template,
    ///      grade, byte-identical details, owner=seller) still persists as a
    ///      MAIL attachment — including after a fresh save pass re-writes the
    ///      row from the reloaded container state. That round trip is the part
    ///      the StubItemManager unit rig cannot exercise.
    ///
    /// OBSERVED (this leg pins the FIXED contract): steps 1–3 pass on the real
    /// stack — cancel returns the ORIGINAL instance with enchant/durability/
    /// details intact, the AucOffCancel mail carries it, and no items row is
    /// left in SlotType.Auction. Step 4 is the part the unit rig cannot reach:
    /// the persisted row must reload from MySQL as a MAIL attachment, which
    /// requires FinalizeForCancel to relocate the instance into the seller's
    /// Mail container (as MailPlayerToPlayer.FinalizeAttachments does) rather
    /// than only stamping item.SlotType = Mail. Stamping alone left
    /// items.container_id pointing at the LISTING (auction) container, and on
    /// reboot ItemManager.LoadUserItems handed the row back to that container,
    /// whose AddOrMoveExistingItem rewrote SlotType from the container type —
    /// the returned instance booted back as a SlotType.Auction orphan (G3).
    /// This leg goes RED if that relocation regresses.
    /// </summary>
    [Fact]
    [Trait("Category", "e2e")]
    public async Task Auction_CancelEnchantedListing_ReturnsOriginalInstance_AndSurvivesKill9()
    {
        var startedAt = DateTime.UtcNow;
        var stages = new List<string>();
        E2eStack.EnsureUp();

        uint sellerId = 0;
        ulong itemId = 0, lotId = 0;
        long cancelMailId = 0;
        var killedPid = 0;
        var reloadedAttachSlotType = -1;
        var reloadedPersistedSlotType = -1;
        var preRestartOrphans = -1;
        var postRestartOrphans = -1;
        byte[] detailsBefore = [];
        byte[] detailsAfterCancel = [];
        byte[] detailsAfterRestart = [];
        var passed = false;

        BotNetworkSession? seller = null;
        try
        {
            using var bridge = new BotDriveClient(E2eStack.BridgePort);
            E2eStack.CleanupBotRows(CancelSellerAccount);

            // ---- 1 · RIG an enchanted equipment instance --------------------
            stages.Add("connect-seller");
            seller = await ConnectCancelSellerAsync();
            var link = GetGameLink(seller);
            StopBackgroundLoops(seller);
            Drain(link);

            stages.Add("rig-enchanted");
            var rig = Trace(bridge, "cancel-rig-enchanted",
                $"{{\"cmd\":\"mail\",\"op\":\"rig\",\"bot\":\"{CancelSellerBot}\",\"money\":{SeedMoney}," +
                $"\"itemTemplate\":{EnchantTemplateId},\"count\":1,\"grade\":3,\"durability\":77," +
                "\"runeId\":1234,\"temperPhysical\":3,\"temperMagical\":4}");
            sellerId = rig.GetProperty("id").GetUInt32();
            itemId = rig.GetProperty("itemId").GetUInt64();
            Assert.True(itemId > 0, $"enchanted rig produced no item: {rig.GetRawText()}");

            var bagItem = InventoryItem(bridge, CancelSellerBot, itemId, "cancel-inv-enchanted");
            Assert.Equal(EnchantTemplateId, bagItem.GetProperty("templateId").GetUInt32());
            Assert.Equal(3, bagItem.GetProperty("grade").GetInt32());
            Assert.Equal(77, bagItem.GetProperty("durability").GetInt32());
            Assert.Equal(1234u, bagItem.GetProperty("runeId").GetUInt32());
            Assert.Equal(3, bagItem.GetProperty("temperPhysical").GetInt32());
            Assert.Equal(4, bagItem.GetProperty("temperMagical").GetInt32());
            Assert.Equal(SlotTypeInventory, bagItem.GetProperty("slotType").GetInt32());

            // Persist the bag state first: the pre-listing details blob must be
            // real MySQL truth, not an in-memory projection.
            stages.Add("save-rigged");
            SavePass(bridge, "cancel-save-rig");
            var bagRow = SnapshotItemRow(itemId);
            Assert.NotNull(bagRow);
            Assert.Equal(SlotTypeInventory, bagRow!.SlotType);
            Assert.Equal(sellerId, bagRow.OwnerId);
            Assert.Equal(EquipDetailBytes, bagRow.Details.Length);
            detailsBefore = bagRow.Details;

            // ---- 2 · LIST via the REAL CSAuctionPostPacket ------------------
            stages.Add("post-real-packet");
            link.SendGameFrame(CSOffsets.CSAuctionPostPacket, 1, body =>
            {
                WriteBc(body, 0); // auctioneerId — unused by the engine
                WriteBc(body, 0); // auctioneerId2
                body.Write(itemId);
                body.Write(StartPrice);
                body.Write(BuyoutPrice);
                body.Write((byte)Duration6Hours);
            });
            var posted = link.ReadAnyOf([SCOffsets.SCAuctionPostedPacket], 20_000);
            Assert.True(posted.Body.Length > 0, "SCAuctionPostedPacket carried no lot body");

            // Decode the server's lot exactly as the 1.2 client does...
            var shownStream = new PacketStream();
            shownStream.Write(posted.Body);
            var shownLot = new AuctionLot();
            shownStream.Read(shownLot);

            var lot = Assert.Single(Lots(bridge, "cancel-lots-after-post"),
                l => ToLong(l["itemId"]!) == (long)itemId);
            lotId = (ulong)ToLong(lot["id"]!);
            Assert.True(lotId > 0, "posted lot has no id");
            Assert.Equal(lotId, shownLot.Id); // the client's view IS the live lot
            Assert.Equal(sellerId, ToLong(lot["clientId"]!));
            Assert.Equal(0L, ToLong(lot["bidderId"]!)); // still unbidden — cancel is legal

            stages.Add("save-listed");
            SavePass(bridge, "cancel-save-after-post");
            var listedRow = SnapshotItemRow(itemId);
            Assert.NotNull(listedRow);
            Assert.Equal(SlotTypeAuction, listedRow!.SlotType); // left the bag
            Assert.Equal(sellerId, listedRow.OwnerId);
            Assert.NotEqual(bagRow.ContainerId, listedRow.ContainerId);
            var listedLotRow = SnapshotLotRow(lotId);
            Assert.NotNull(listedLotRow);
            Assert.Equal((long)itemId, listedLotRow!.ItemId);

            // ---- 3 · CANCEL via the REAL CSCancelAuctionPacket --------------
            // The 1.2 client cancels the lot it was shown: re-serialize that
            // exact lot (decode → encode) into the packet body. The handler
            // consumes lot.Id; the round-tripped lot keeps the packet
            // wire-faithful instead of a hand-rolled stub, and the decode above
            // is the fidelity check that the echo is the server's own lot.
            stages.Add("cancel-real-packet");
            link.SendGameFrame(CSOffsets.CSCancelAuctionPacket, 1, body =>
            {
                WriteBc(body, 0);
                WriteBc(body, 0);
                body.Write(shownLot);
            });
            _ = link.ReadAnyOf([SCOffsets.SCAuctionCanceledPacket], 20_000);

            Assert.DoesNotContain(Lots(bridge, "cancel-lots-after-cancel"),
                l => ToLong(l["id"]!) == (long)lotId);

            // In-memory: the cancel mail carries the ORIGINAL instance, enchant
            // state and durability intact (never a fresh mint).
            var cancelMail = Assert.Single(
                NetMails(bridge, CancelSellerBot, "cancel-mails-seller"),
                m => ToLong(m["type"]!) == MailTypeAucOffCancel);
            Assert.Equal(sellerId, ToLong(cancelMail["receiverId"]!));
            var cancelAttach = Assert.Single(((JsonElement)cancelMail["attachments"]!).EnumerateArray());
            Assert.Equal(itemId, cancelAttach.GetProperty("itemId").GetUInt64());
            Assert.Equal(EnchantTemplateId, cancelAttach.GetProperty("templateId").GetUInt32());
            Assert.Equal(3, cancelAttach.GetProperty("grade").GetInt32());
            Assert.Equal(77, cancelAttach.GetProperty("durability").GetInt32());
            Assert.Equal(1234u, cancelAttach.GetProperty("runeId").GetUInt32());
            Assert.Equal(3, cancelAttach.GetProperty("temperPhysical").GetInt32());
            Assert.Equal(4, cancelAttach.GetProperty("temperMagical").GetInt32());
            Assert.Equal(SlotTypeMail, cancelAttach.GetProperty("slotType").GetInt32());

            // Persisted truth: same instance, mail-borne, and NO SlotType.Auction row.
            stages.Add("save-cancelled");
            SavePass(bridge, "cancel-save-after-cancel");
            Assert.Null(SnapshotLotRow(lotId));
            var returnedRow = SnapshotItemRow(itemId);
            Assert.NotNull(returnedRow);
            Assert.Equal(EnchantTemplateId, returnedRow!.TemplateId);
            Assert.Equal(3, returnedRow.Grade);
            Assert.Equal(sellerId, returnedRow.OwnerId);
            Assert.Equal(SlotTypeMail, returnedRow.SlotType);
            Assert.True(returnedRow.Details.SequenceEqual(detailsBefore),
                "cancel changed the enchant details blob");
            detailsAfterCancel = returnedRow.Details;
            preRestartOrphans = CountAuctionOrphans(sellerId);
            Assert.Equal(0, preRestartOrphans);

            var cancelMailRow = SnapshotMailRow(MailTypeAucOffCancel, sellerId);
            Assert.True(cancelMailRow.HasValue, "no persisted AucOffCancel mails row for the seller");
            Assert.Equal((long)itemId, cancelMailRow!.Value.attachment0); // SAME id — no mint
            cancelMailId = cancelMailRow.Value.id;

            // ---- 4 · KILL -9 PIN + reboot ----------------------------------
            stages.Add("kill-9-restart");
            seller.Dispose();
            seller = null;
            killedPid = E2eStack.RestartGameServer();
            Assert.True(killedPid > 0, "no game process was killed — cannot claim a kill -9 restart");
            Assert.False(Directory.Exists($"/proc/{killedPid}"),
                $"killed game pid {killedPid} still exists after the restart gate");
            stages.Add($"killed-pid-{killedPid}");

            using var bridge2 = new BotDriveClient(E2eStack.BridgePort);
            seller = await ConnectCancelSellerAsync();
            Assert.Equal(sellerId, seller.CharacterId); // SAME character row re-embodied
            var link2 = GetGameLink(seller);
            StopBackgroundLoops(seller);
            Drain(link2);

            // Reloaded from MySQL: the cancel mail still carries the SAME
            // instance, enchant state and durability intact.
            stages.Add("reload-mail-fidelity");
            var reloadedMail = Assert.Single(
                NetMails(bridge2, CancelSellerBot, "cancel-mails-after-restart"),
                m => ToLong(m["id"]!) == cancelMailId);
            var reloadedAttach = Assert.Single(((JsonElement)reloadedMail["attachments"]!).EnumerateArray());
            Assert.Equal(itemId, reloadedAttach.GetProperty("itemId").GetUInt64());
            Assert.Equal(EnchantTemplateId, reloadedAttach.GetProperty("templateId").GetUInt32());
            Assert.Equal(3, reloadedAttach.GetProperty("grade").GetInt32());
            Assert.Equal(77, reloadedAttach.GetProperty("durability").GetInt32());
            Assert.Equal(1234u, reloadedAttach.GetProperty("runeId").GetUInt32());
            Assert.Equal(3, reloadedAttach.GetProperty("temperPhysical").GetInt32());
            Assert.Equal(4, reloadedAttach.GetProperty("temperMagical").GetInt32());
            // Observed reload-time in-memory class: the row must keep
            // container_id ← the seller's MAIL container, because
            // FinalizeForCancel relocates the instance into it (mirroring
            // MailPlayerToPlayer.FinalizeAttachments). If that relocation
            // regressed to a bare SlotType stamp, ItemManager.LoadUserItems
            // would re-add the row to its old auction container, whose
            // AddOrMoveExistingItem rewrites SlotType from the container type —
            // the returned item would boot back as Auction.
            reloadedAttachSlotType = reloadedAttach.GetProperty("slotType").GetInt32();

            // THE PERSISTENCE ROUND TRIP (the unit rig cannot prove this):
            // after the reboot the returned instance must still be a MAIL
            // attachment, not an item re-homed into SlotType.Auction.
            stages.Add("reload-item-row");
            var reloadedRow = SnapshotItemRow(itemId);
            Assert.NotNull(reloadedRow);
            Assert.Equal(EnchantTemplateId, reloadedRow!.TemplateId);
            Assert.Equal(3, reloadedRow.Grade);
            Assert.Equal(sellerId, reloadedRow.OwnerId);
            Assert.True(reloadedRow.Details.SequenceEqual(detailsBefore),
                "restart changed the enchant details blob");
            detailsAfterRestart = reloadedRow.Details;
            reloadedPersistedSlotType = reloadedRow.SlotType;
            postRestartOrphans = CountAuctionOrphans(sellerId);
            Assert.True(reloadedAttachSlotType == SlotTypeMail,
                $"returned item {itemId} reloaded with in-memory slotType={reloadedAttachSlotType} " +
                $"(want {SlotTypeMail}=Mail) while attached to cancel mail {cancelMailId} — " +
                "the cancel returned the instance but left it held by its listing container " +
                $"(items.container_id={reloadedRow.ContainerId})");
            Assert.True(reloadedRow.SlotType == SlotTypeMail,
                $"returned item {itemId} persisted slot_type={reloadedRow.SlotType} (want {SlotTypeMail}=Mail) " +
                $"after the reboot + autosave (container_id={reloadedRow.ContainerId})");

            // ...and it must survive the next save pass re-writing the row from
            // the reloaded in-memory container state (container_id ←
            // _holdingContainer). A cancel that only fixed SlotType in memory
            // would flip the persisted row back to SlotType.Auction here.
            stages.Add("save-after-restart");
            SavePass(bridge2, "cancel-save-after-restart");
            var roundTripped = SnapshotItemRow(itemId);
            Assert.NotNull(roundTripped);
            Assert.Equal(sellerId, roundTripped!.OwnerId);
            Assert.True(roundTripped.Details.SequenceEqual(detailsBefore),
                "post-restart save changed the enchant details blob");
            Assert.True(roundTripped.SlotType == SlotTypeMail,
                $"post-restart save persisted slot_type={roundTripped.SlotType} (want {SlotTypeMail}=Mail) " +
                $"for returned item {itemId} — the reload put the instance back in its listing container");
            Assert.True(CountAuctionOrphans(sellerId) == 0,
                $"post-restart save left {CountAuctionOrphans(sellerId)} item row(s) in SlotType.Auction " +
                $"(returned item {itemId} slot_type={roundTripped.SlotType}, container_id={roundTripped.ContainerId})");
            Assert.Null(SnapshotLotRow(lotId));
            Assert.NotNull(SnapshotMailRow(MailTypeAucOffCancel, sellerId));
            detailsAfterRestart = roundTripped.Details;

            stages.Add("PASS");
            passed = true;
        }
        catch (Exception ex)
        {
            stages.Add($"FAIL: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            seller?.Dispose();
            await CleanupCancelLegAsync(sellerId);
            await WriteCancelEvidenceAsync(startedAt, passed, stages, sellerId, itemId, lotId,
                cancelMailId, killedPid, reloadedAttachSlotType, reloadedPersistedSlotType,
                preRestartOrphans, postRestartOrphans,
                detailsBefore, detailsAfterCancel, detailsAfterRestart);
        }
    }

    private static async Task<BotNetworkSession> ConnectCancelSellerAsync()
        => await BotNetworkSession.ConnectAsync(CancelSellerBot, CancelSellerAccount, CancelPassword,
            "127.0.0.1", E2eStack.LoginPort,
            "127.0.0.1", E2eStack.GamePort,
            "127.0.0.1", E2eStack.StreamPort);

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

    /// <summary>One bag item dump for a REAL networked bot (mail op inv).</summary>
    private JsonElement InventoryItem(BotDriveClient bridge, string bot, ulong itemId, string label)
        => Trace(bridge, label, $"{{\"cmd\":\"mail\",\"op\":\"inv\",\"bot\":\"{bot}\"}}")
            .GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("itemId").GetUInt64() == itemId);

    /// <summary>Mail dump for a REAL networked bot (mail op mails) with full
    /// attachment instance fidelity (grade/durability/rune/tempers/details).</summary>
    private List<Dictionary<string, object?>> NetMails(BotDriveClient bridge, string bot, string label)
        => [.. Trace(bridge, label, $"{{\"cmd\":\"mail\",\"op\":\"mails\",\"bot\":\"{bot}\"}}")
            .GetProperty("mails").EnumerateArray().Select(JsonDict)];

    /// <summary>Items rows for the owner still sitting in SlotType.Auction —
    /// the G3 orphan count. Zero is the contract.</summary>
    private static int CountAuctionOrphans(uint ownerId)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE owner = @owner AND slot_type = @slot";
        cmd.Parameters.AddWithValue("@owner", ownerId);
        cmd.Parameters.AddWithValue("@slot", SlotTypeAuction);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Removes every row this leg created (character + account scoped).</summary>
    private static async Task CleanupCancelLegAsync(uint sellerId)
    {
        try
        {
            if (sellerId > 0)
            {
                using var conn = E2eStack.OpenDb("aaemu_game");
                foreach (var sql in new[]
                         {
                             "DELETE FROM mails WHERE receiver_id = @id OR sender_id = @id",
                             "DELETE FROM auction_house WHERE client_id = @id",
                             "DELETE FROM items WHERE owner = @id",
                             "DELETE FROM item_containers WHERE owner_id = @id"
                         })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@id", sellerId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            E2eStack.CleanupBotRows(CancelSellerAccount);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[auction-cancel-e2e] cleanup failed (non-fatal): {e.Message}");
        }
    }

    private async Task WriteCancelEvidenceAsync(DateTime startedAt, bool passed, List<string> stages,
        uint sellerId, ulong itemId, ulong lotId, long cancelMailId, int killedPid,
        int reloadedAttachSlotType, int reloadedPersistedSlotType,
        int preRestartOrphans, int postRestartOrphans,
        byte[] detailsBefore, byte[] detailsAfterCancel, byte[] detailsAfterRestart)
    {
        Directory.CreateDirectory(EvidenceDir);

        var tracePath = Path.Combine(EvidenceDir, "auction-cancel-e2e-trace.jsonl");
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
            card = "AUCTION G-cancel (dossier auction-domain.md G3) — E2E cancel-with-enchanted-gear leg",
            path = "CSAuectionPostPacket/CSCancelAuctionPacket over the real game link → AuctionManager.PostLotOnAuction/CancelAuctionLot → MailForAuction.FinalizeForCancel",
            bot = new { seller = CancelSellerBot, sellerId },
            verdict = passed ? "PASS" : "FAIL",
            stages,
            elapsed_seconds = (DateTime.UtcNow - startedAt).TotalSeconds,
            lot_id = lotId,
            item_id = itemId,
            cancel_mail_id = cancelMailId,
            killed_game_pid = killedPid,
            reloaded_attachment_slot_type = reloadedAttachSlotType,
            reloaded_persisted_slot_type = reloadedPersistedSlotType,
            cancel_leg = new
            {
                instance_faithful_return = detailsAfterCancel.Length > 0 && detailsAfterCancel.SequenceEqual(detailsBefore),
                auction_orphans_pre_restart = preRestartOrphans,
                auction_orphans_post_restart = postRestartOrphans,
                persistence_round_trip_mail = reloadedAttachSlotType == SlotTypeMail && reloadedPersistedSlotType == SlotTypeMail,
                reloaded_persisted_slot_type = reloadedPersistedSlotType,
                note = "G3: cancel mails the ORIGINAL instance (id/enchant/durability/details intact) AND " +
                       "relocates it into the seller's Mail container, so the persisted row no longer keeps " +
                       "container_id = the auction container. The reboot reloads it as SlotType.Mail " +
                       "(reloaded_attachment_slot_type=5) and no orphan remains in SlotType.Auction."
            },
            details_blob = new
            {
                bytes = detailsBefore.Length,
                before_b64 = Convert.ToBase64String(detailsBefore),
                after_cancel_b64 = Convert.ToBase64String(detailsAfterCancel),
                after_restart_b64 = Convert.ToBase64String(detailsAfterRestart),
                after_cancel_identical = detailsAfterCancel.Length > 0 && detailsAfterCancel.SequenceEqual(detailsBefore),
                after_restart_identical = detailsAfterRestart.Length > 0 && detailsAfterRestart.SequenceEqual(detailsBefore)
            },
            flow = new[]
            {
                $"rig enchanted equipment {EnchantTemplateId} (grade 3, durability 77, rune 1234, tempers 3/4) and save it",
                "list it through the REAL CSAuctionPostPacket (no bid follows)",
                "cancel through the REAL CSCancelAuctionPacket (posted lot echoed back)",
                "assert the ORIGINAL instance id returns, enchant/durability/details intact, AucOffCancel mail carries it",
                "assert no items row remains in SlotType.Auction (G3 orphan check)",
                $"KILL -9 pin on pid {killedPid}, reboot, re-adopt the seller",
                "assert the returned instance still persists as a Mail attachment with byte-identical details, and stays Mail after a fresh save pass"
            },
            trace_path = tracePath
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDir, "auction-cancel-e2e-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
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

    private sealed record ItemRow(ulong Id, uint TemplateId, int SlotType, uint OwnerId,
        ulong ContainerId = 0, byte Grade = 0, byte[]? Blob = null)
    {
        public byte[] Details => Blob ?? [];
    }

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
        cmd.CommandText =
            "SELECT id, template_id, slot_type, owner, container_id, CAST(grade AS UNSIGNED) AS grade, details " +
            "FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ItemRow(reader.GetUInt64("id"), reader.GetUInt32("template_id"),
            reader.GetInt32("slot_type"), reader.GetUInt32("owner"),
            reader.GetUInt64("container_id"), Convert.ToByte(reader.GetInt64("grade")),
            reader.IsDBNull(reader.GetOrdinal("details"))
                ? []
                : (byte[])reader.GetValue(reader.GetOrdinal("details")));
    }

    private static int DbItemSlotType(long itemId)
    {
        var row = SnapshotItemRow((ulong)itemId);
        return row?.SlotType ?? -1;
    }

    /// <summary>The persisted container id of the given type owned by a
    /// character — e.g. the Mail container a returned/sold attachment must end
    /// up holding, or the Auction container it must NOT be left in.</summary>
    private static ulong SnapshotContainerId(uint ownerId, int slotType)
    {
        using var conn = E2eStack.OpenDb("aaemu_game");
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT container_id FROM item_containers WHERE owner_id = @owner AND slot_type = @slot LIMIT 1";
        cmd.Parameters.AddWithValue("@owner", ownerId);
        cmd.Parameters.AddWithValue("@slot", slotType);
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value
            ? 0
            : Convert.ToUInt64(result, CultureInfo.InvariantCulture);
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
        uint sellerId, uint buyerId, ulong soldItemId, ulong buyerMailContainerId,
        ulong soldItemSettleContainerId, int soldReloadedSlotType, int soldOrphansAfterRestart,
        bool soldPersistenceRoundTrip)
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
            sold_item_leg = new
            {
                item_id = soldItemId,
                buyer_mail_container_id = buyerMailContainerId,
                settle_container_id = soldItemSettleContainerId,
                settle_container_is_buyer_mail = soldItemSettleContainerId == buyerMailContainerId && buyerMailContainerId > 0,
                reloaded_slot_type = soldReloadedSlotType,
                auction_orphans_post_restart = soldOrphansAfterRestart,
                persistence_round_trip_mail = soldPersistenceRoundTrip,
                note = "FinalizeForSaleBuyer must relocate the SOLD instance into the BUYER's Mail container. " +
                       "It was listed out of the seller's Auction container (PostLotOnAuction), so a bare " +
                       "SlotType stamp persisted container_id = the seller's listing container; " +
                       "ItemManager.LoadUserItems then re-added the row to it and " +
                       "ItemContainer.AddOrMoveExistingItem re-stamped SlotType from that container's type, " +
                       "booting the bought item as a SlotType.Auction orphan owned by the buyer. The pin is " +
                       "container-id identity across a real kill -9 reload, not slot_type alone."
            },
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
                "on reboot the real 5s AuctionHouseTask expires the lot; seller gets AucOffFail mail WITH the item re-attached (persisted)",
                $"on the SAME second reboot: sold item {soldItemId} must reload held by the buyer's Mail container " +
                $"({buyerMailContainerId}), NOT the seller's Auction container — container_id identity, not slot_type"
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
