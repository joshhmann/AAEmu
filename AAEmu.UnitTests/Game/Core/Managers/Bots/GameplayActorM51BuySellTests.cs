using System.Numerics;
using System.Text.Json;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 Buy/Sell contract tests (t_8741b03d) — the four trade actions
/// (Buy · Sell · PostAuction · BuyAuction) driven through the REAL engine
/// trade paths:
///  - merchant buy: CSBuyItemsPacket branch (NpcManager.GetGoods →
///    AcquireDefaultItem → ChangeMoney);
///  - merchant sell: CSSellItemsPacket branch (BuyBackItems move +
///    MarkItemForDbDeletion + ChangeMoney refund);
///  - auction listing: CSAuctionPostPacket call (PostLotOnAuction);
///  - auction purchase: CSBidAuctionPacket buy-now branch
///    (BidOnAuctionLot — money deducted, lot removed, engine mail path).
/// Coverage: full lifecycle (Requested → Accepted → Running → Completed |
/// Rejected(reason) | Interrupted | TimedOut machinery is B1-shared),
/// failure taxonomy (§17 only), idempotency (retries/timeouts must not
/// duplicate items, currency transfers or interactions — the request-key
/// dedupe is primary, the engine-true state change is the backstop), and
/// the structured trace record on every action.
/// </summary>
[NotInParallel]
public class GameplayActorM51BuySellTests
{
    #region Buy — merchant shop (CSBuyItemsPacket path)

    [Test]
    public async Task Buy_FromMerchant_Completes_GrantsItems_AndDeductsMoney()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1001);
        var before = GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId);

        var request = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, count: 5);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(50L * 5);
        // Item landed in the bag through the real acquisition path.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId))
            .IsEqualTo(before + 5);
        // Money deducted through the real currency path.
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 250);

        // Lifecycle transition log is complete (audit state_changes).
        var record = actor.AuditTrace.Last();
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Buy);
        await Assert.That(record.TargetId).IsEqualTo(merchant);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Requested"))).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Accepted"))).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
    }

    [Test]
    public async Task Buy_UnknownMerchant_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-buy-2");
        GameplayActorTestRig.SetMoney(actor, 10_000);

        var request = actor.Buy(0xDEAD_BEEF, GameplayActorTestRig.BuyItemTemplateId, 1);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not a merchant")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task Buy_NonMerchantNpc_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-3");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        // Spawn an NPC WITHOUT the merchant template fields.
        var npcObjId = session.SpawnNpc(1002);

        var request = actor.Buy(npcObjId, GameplayActorTestRig.BuyItemTemplateId, 1);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
    }

    [Test]
    public async Task Buy_ItemNotSoldByPack_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-4");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1003);

        // Template exists but is not in the merchant's pack.
        GameplayActorTestRig.SeedTradeItemTemplate(99_901, price: 50, refund: 0, sellable: false);
        var request = actor.Buy(merchant, 99_901, 1);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("does not sell")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
    }

    [Test]
    public async Task Buy_MerchantOutOfRange_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-5");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1004);
        GameplayActorTestRig.SetNpcPosition(session, merchant, new Vector3(50, 0, 0));

        var request = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, 1);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("out of shop range")).IsTrue();
    }

    [Test]
    public async Task Buy_NotEnoughMoney_Rejected_NoGrant_NoCharge()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-6");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 100); // needs 250 for 5×50
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1005);

        var request = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, 5);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not enough money")).IsTrue();
        // No item granted, no money charged.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId)).IsEqualTo(0);
        await Assert.That(actor.Character.Money).IsEqualTo(100);
    }

    [Test]
    public async Task Buy_NonPositiveCount_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-7");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1006);

        var request = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task Buy_SameKeyRetry_NeverExecutesTwice_NoDoubleGrant_NoDoubleCharge()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-8");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1007);

        var original = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, 5, idempotencyKey: "buy:apples:5");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 250);

        // Retry with the SAME key: refused BEFORE execution — the audit
        // record shows no Running transition, so the item grant and currency
        // charge can never duplicate.
        var retry = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, 5, idempotencyKey: "buy:apples:5");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId)).IsEqualTo(5);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 250);

        // FindByKey correlates the retry back to the ORIGINAL completed attempt.
        var byKey = actor.FindByKey("buy:apples:5");
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);
    }

    [Test]
    public async Task Buy_BusyActor_RejectedStateTransition()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-buy-9");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1008);

        // A long move occupies the actor (single-writer rule).
        var move = actor.MoveTo(new Vector3(100, 0, 0), speed: 1f);
        var buy = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, 1);

        await Assert.That(buy.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(buy.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(buy.Detail?.Contains("busy")).IsTrue();
        // The buy never executed.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId)).IsEqualTo(0);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
    }

    #endregion

    #region Sell — merchant shop (CSSellItemsPacket path)

    [Test]
    public async Task Sell_ToMerchant_Completes_ItemLeavesBag_MoneyCredited()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-sell-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 1_000);
        GameplayActorTestRig.SeedMerchantPack(); // pack exists; sell only needs the merchant flag
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1101);

        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, count: 4);
        _ = actor.Character.Inventory.Bag.GetAllItemsByTemplate(GameplayActorTestRig.SellItemTemplateId, -1, out var items, out _);
        var itemId = items[0].Id;

        var request = actor.Sell(merchant, itemId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(25 * 4);
        // The engine MOVED the item out of the bag into BuyBackItems.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.SellItemTemplateId)).IsEqualTo(0);
        await Assert.That(actor.Character.BuyBackItems.GetItemByItemId(itemId)).IsNotNull();
        // Refund credited through the real currency path (25 × grade mult 100/100 × 4).
        await Assert.That(actor.Character.Money).IsEqualTo(1_000 + 100);

        var record = actor.AuditTrace.Last();
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Sell);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
    }

    [Test]
    public async Task Sell_UnknownMerchant_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-sell-2");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, count: 1);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.SellItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var request = actor.Sell(0xDEAD_BEEF, itemId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.SellItemTemplateId)).IsEqualTo(1);
    }

    [Test]
    public async Task Sell_ItemNotInInventory_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-sell-3");
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1102);

        var request = actor.Sell(merchant, itemId: 0xFFFF_FFFF_FFFF_0001);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in inventory")).IsTrue();
    }

    [Test]
    public async Task Sell_NotSellableItem_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-sell-4");
        GameplayActorTestRig.SetMoney(actor, 1_000);
        GameplayActorTestRig.SeedMerchantPack();
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1103);

        // A template with Sellable=false (the Buy rig template is exactly that).
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.BuyItemTemplateId, count: 1);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.BuyItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var request = actor.Sell(merchant, itemId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not sellable")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(1_000);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId)).IsEqualTo(1);
    }

    [Test]
    public async Task Sell_SameKeyRetry_NeverExecutesTwice_NoDoubleRefund()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-sell-5");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 1_000);
        GameplayActorTestRig.SeedMerchantPack();
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1104);

        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, count: 3);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.SellItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var original = actor.Sell(merchant, itemId, idempotencyKey: "sell:item-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(1_000 + 75);

        var retry = actor.Sell(merchant, itemId, idempotencyKey: "sell:item-1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        // No double refund.
        await Assert.That(actor.Character.Money).IsEqualTo(1_000 + 75);
    }

    [Test]
    public async Task Sell_FreshKeyRetry_AfterSuccess_FindsNoItem_NoDoubleSell()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-sell-6");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 1_000);
        GameplayActorTestRig.SeedMerchantPack();
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1105);

        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, count: 2);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.SellItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var original = actor.Sell(merchant, itemId, idempotencyKey: "sell:fresh-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(1_000 + 50);

        // A FRESH key (timeout-ambiguity retry): the engine moved the item
        // out of the bag, so the retry finds nothing to sell — the item can
        // never be sold twice, no double refund.
        var retry = actor.Sell(merchant, itemId, idempotencyKey: "sell:fresh-2");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail?.Contains("not found in inventory")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(1_000 + 50);
    }

    #endregion

    #region PostAuction — auction listing (CSAuctionPostPacket path)

    [Test]
    public async Task PostAuction_Completes_LotRegistered_ItemLeavesBag_FeeDeducted()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-post-1");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.AuctionItemTemplateId, count: 1);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.AuctionItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var request = actor.PostAuction(itemId, startPrice: 100, buyoutPrice: 1_000,
            AuctionDuration.AuctionDuration6Hours);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // Fee = buyout × 1% × (duration+1) = 1000 × .01 × 1 = 10.
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 10);
        // The engine moved the item into AuctionAttachments (out of the bag).
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.AuctionItemTemplateId)).IsEqualTo(0);
        // The lot is registered with the item + client identity.
        var lot = AuctionManager.Instance.AuctionLots.Values.FirstOrDefault(l => l.Item?.Id == itemId);
        await Assert.That(lot).IsNotNull();
        await Assert.That(lot!.ClientId).IsEqualTo(actor.Character.Id);

        var record = actor.AuditTrace.Last();
        await Assert.That(record.Action).IsEqualTo(ActorActionType.AuctionPost);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task PostAuction_ItemNotOwned_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-post-2");
        GameplayActorTestRig.SetMoney(actor, 10_000);

        var request = actor.PostAuction(itemId: 0xFFFF_FFFF_FFFF_0002, 100, 1_000,
            AuctionDuration.AuctionDuration6Hours);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in inventory")).IsTrue();
    }

    [Test]
    public async Task PostAuction_InvalidPrices_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-post-3");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.AuctionItemTemplateId, count: 1);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.AuctionItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var zero = actor.PostAuction(itemId, 0, 0, AuctionDuration.AuctionDuration6Hours);
        await Assert.That(zero.State).IsEqualTo(ActorLifecycleState.Rejected);

        var negative = actor.PostAuction(itemId, -1, 100, AuctionDuration.AuctionDuration6Hours);
        await Assert.That(negative.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(negative.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task PostAuction_FeeUnaffordable_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-post-4");
        GameplayActorTestRig.SetMoney(actor, 5); // fee 10 > 5
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.AuctionItemTemplateId, count: 1);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.AuctionItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var request = actor.PostAuction(itemId, 100, 1_000, AuctionDuration.AuctionDuration6Hours);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("listing fee")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(5);
    }

    [Test]
    public async Task PostAuction_SameKeyRetry_NeverExecutesTwice_NoDoubleListing()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-post-5");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.AuctionItemTemplateId, count: 1);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.AuctionItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var original = actor.PostAuction(itemId, 100, 1_000, AuctionDuration.AuctionDuration6Hours,
            idempotencyKey: "post:item-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 10);

        var retry = actor.PostAuction(itemId, 100, 1_000, AuctionDuration.AuctionDuration6Hours,
            idempotencyKey: "post:item-1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 10);
        // Only ONE lot exists for this item.
        await Assert.That(AuctionManager.Instance.AuctionLots.Values.Count(l => l.Item?.Id == itemId)).IsEqualTo(1);
    }

    [Test]
    public async Task PostAuction_FreshKeyRetry_AfterSuccess_ItemAlreadyListed_NoDoubleListing()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-post-6");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.AuctionItemTemplateId, count: 1);
        var itemId = actor.Character.Inventory.Bag
            .GetAllItemsByTemplate(GameplayActorTestRig.AuctionItemTemplateId, -1, out var items, out _)
            ? items[0].Id : 0;

        var original = actor.PostAuction(itemId, 100, 1_000, AuctionDuration.AuctionDuration6Hours,
            idempotencyKey: "post:fresh-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Fresh key (timeout ambiguity): the engine moved the item to
        // AuctionAttachments, so the retry finds nothing to list.
        var retry = actor.PostAuction(itemId, 100, 1_000, AuctionDuration.AuctionDuration6Hours,
            idempotencyKey: "post:fresh-2");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail?.Contains("not found in inventory")).IsTrue();
        await Assert.That(AuctionManager.Instance.AuctionLots.Values.Count(l => l.Item?.Id == itemId)).IsEqualTo(1);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 10);
    }

    #endregion

    #region BuyAuction — auction purchase (CSBidAuctionPacket buy-now branch)

    [Test]
    public async Task BuyAuction_Completes_LotRemoved_MoneyDeducted()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-ah-1");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedAuctionLot(lotId: 7001, GameplayActorTestRig.AuctionItemTemplateId,
            count: 1, startPrice: 100, buyoutPrice: 1_000,
            clientId: 9_999, clientName: "other-seller");

        var request = actor.BuyAuction(7001, price: 1_000);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(1_000);
        // Money deducted through the real currency path.
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 1_000);
        // The engine removed the lot (buy-now is terminal).
        await Assert.That(AuctionManager.Instance.AuctionLots.ContainsKey(7001)).IsFalse();

        var record = actor.AuditTrace.Last();
        await Assert.That(record.Action).IsEqualTo(ActorActionType.AuctionBuy);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task BuyAuction_LotNotFound_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-ah-2");
        GameplayActorTestRig.SetMoney(actor, 10_000);

        var request = actor.BuyAuction(0xDEAD_BEEF, price: 500);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("lot")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
    }

    [Test]
    public async Task BuyAuction_LotWithoutBuyout_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-ah-3");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedAuctionLot(lotId: 7003, GameplayActorTestRig.AuctionItemTemplateId,
            count: 1, startPrice: 100, buyoutPrice: 0,
            clientId: 9_999, clientName: "other-seller");

        var request = actor.BuyAuction(7003, price: 200);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("buyout")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
    }

    [Test]
    public async Task BuyAuction_OfferBelowBuyout_Rejected_NotABid()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-ah-4");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedAuctionLot(lotId: 7004, GameplayActorTestRig.AuctionItemTemplateId,
            count: 1, startPrice: 100, buyoutPrice: 1_000,
            clientId: 9_999, clientName: "other-seller");

        // The packet's buy-now branch requires offer >= DirectMoney; below
        // buyout is the BID branch — this surface is purchase only.
        var request = actor.BuyAuction(7004, price: 200);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("below buyout")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
        await Assert.That(AuctionManager.Instance.AuctionLots.ContainsKey(7004)).IsTrue();
    }

    [Test]
    public async Task BuyAuction_NotEnoughMoney_Rejected_BeforeEngineCall()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-ah-5");
        GameplayActorTestRig.SetMoney(actor, 500); // buyout 1000 > 500
        GameplayActorTestRig.SeedAuctionLot(lotId: 7005, GameplayActorTestRig.AuctionItemTemplateId,
            count: 1, startPrice: 100, buyoutPrice: 1_000,
            clientId: 9_999, clientName: "other-seller");

        var request = actor.BuyAuction(7005, price: 1_000);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not enough money")).IsTrue();
        // The engine never ran: the lot is still listed and no money moved.
        await Assert.That(AuctionManager.Instance.AuctionLots.ContainsKey(7005)).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(500);
    }

    [Test]
    public async Task BuyAuction_SameKeyRetry_NeverExecutesTwice_NoDoubleCharge()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-ah-6");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedAuctionLot(lotId: 7006, GameplayActorTestRig.AuctionItemTemplateId,
            count: 1, startPrice: 100, buyoutPrice: 1_000,
            clientId: 9_999, clientName: "other-seller");

        var original = actor.BuyAuction(7006, price: 1_000, idempotencyKey: "ah-buy:7006");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(9_000);

        var retry = actor.BuyAuction(7006, price: 1_000, idempotencyKey: "ah-buy:7006");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        // No double charge.
        await Assert.That(actor.Character.Money).IsEqualTo(9_000);
    }

    [Test]
    public async Task BuyAuction_FreshKeyRetry_AfterSuccess_LotGone_NoDoubleBuy()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m51-ah-7");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedAuctionLot(lotId: 7007, GameplayActorTestRig.AuctionItemTemplateId,
            count: 1, startPrice: 100, buyoutPrice: 1_000,
            clientId: 9_999, clientName: "other-seller");

        var original = actor.BuyAuction(7007, price: 1_000, idempotencyKey: "ah-buy:fresh-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(9_000);

        // Fresh key (timeout ambiguity): the engine REMOVED the lot on
        // purchase, so the retry finds nothing to buy — no double charge.
        var retry = actor.BuyAuction(7007, price: 1_000, idempotencyKey: "ah-buy:fresh-2");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail?.Contains("not found")).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(9_000);
    }

    #endregion

    #region Trace records — every action emits the structured record

    [Test]
    public async Task Buy_EmitsJsonTraceRecord_WithContractFields()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-trace-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1201);

        _ = actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, 1);

        var record = actor.AuditTrace.Last();
        await Assert.That(record).IsNotNull();
        var json = JsonSerializer.Deserialize<JsonElement>(record.ToJson());
        await Assert.That(json.TryGetProperty("trace_id", out _)).IsTrue();
        await Assert.That(json.TryGetProperty("actor_id", out _)).IsTrue();
        await Assert.That(json.GetProperty("action").GetString()).IsEqualTo("Buy");
        await Assert.That(json.TryGetProperty("target_id", out _)).IsTrue();
        await Assert.That(json.TryGetProperty("requested_at", out _)).IsTrue();
        await Assert.That(json.TryGetProperty("started_at", out _)).IsTrue();
        await Assert.That(json.TryGetProperty("completed_at", out _)).IsTrue();
        await Assert.That(json.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(json.TryGetProperty("failure", out _)).IsTrue();
        await Assert.That(json.TryGetProperty("detail", out _)).IsTrue();
        await Assert.That(json.TryGetProperty("state_changes", out _)).IsTrue();
    }

    [Test]
    public async Task RejectedAction_EmitsTraceRecord_WithFailureReason()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m51-trace-2");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1202);
        GameplayActorTestRig.SeedTradeItemTemplate(99_902, price: 50, refund: 0, sellable: false);

        _ = actor.Buy(merchant, 99_902, 1); // not in the pack

        var record = actor.AuditTrace.Last();
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(record.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(record.StartedAtUtc).IsNotNull(); // accepted before rejection
        var json = JsonSerializer.Deserialize<JsonElement>(record.ToJson());
        await Assert.That(json.GetProperty("result").GetString()).IsEqualTo("Rejected");
        await Assert.That(json.GetProperty("failure").GetString()).IsEqualTo("RejectedAction");
    }

    #endregion
}
