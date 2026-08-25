using AAEmu.Commons.Network;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;

using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// MERCHANT-01 headless verification rig: NPC vendor buy/sell through the REAL
/// manager/packet seam — <see cref="AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket"/>
/// and <see cref="AAEmu.Game.Core.Packets.C2G.CSSellItemsPacket"/> (the shop
/// packets registered in GameNetwork under opcodes 0x0ae / 0x0b0) executed
/// against capture-backed GameConnections (the ExpeditionManagerRigTests.Conn
/// convention).
///
/// Rig surface: real session world (ParentWorld.GetNpc resolves the merchant),
/// real NpcManager goods pack (GameplayActorTestRig.SeedMerchantPack), real
/// ItemManager templates/grades, real inventory/currency services. No live
/// spawner data is required — the packets resolve the NPC purely through the
/// world's object registries.
///
/// KNOWN ENGINE BUGS surfaced by this rig (documented, NOT fixed — ownership
/// boundary of MERCHANT-01):
///
/// BUG #1 (buy funds gate) — CSBuyItemsPacket.cs:119-122: the refusal gate
/// joins the three currency checks with &amp;&amp; instead of ||, so a purchase is
/// only refused when ALL THREE (money AND honor AND vocation) exceed their
/// balances simultaneously. With honor/vocation shortfalls absent (both 0,
/// the normal case), the gate never fires: an insolvent buy GRANTS the item
/// and then drives Money NEGATIVE through ChangeMoney(None→Inventory), which
/// has no funds guard on that path. Same finding already annotated in the
/// actor layer (GameplayActor.cs:1697-1699 "The packet's check is buggy").
/// Verified by Buy_InsufficientFunds_KnownBug_PurchaseProceedsAndMoneyGoesNegative.
///
/// BUG #2 (sell refund on refused move) — CSSellItemsPacket.cs:49-65: the
/// refund is accumulated OUTSIDE the success branch of the BuyBackItems
/// move. When AddOrMoveExistingItem fails (full/refused target container) the
/// item stays in the bag, but the payout is still credited — a dupe vector.
/// Verified by Sell_BuyBackContainerFull_KnownBug_RefundPaidWhileItemStaysInBag.
///
/// BUG #3 (buy ignores grant failure) — CSBuyItemsPacket.cs:128: the return
/// value of AcquireDefaultItem is ignored; with a full bag the grant silently
/// fails (AcquireDefaultItemEx returns false on the space pre-check) yet the
/// purchase price is still charged (lines 162-165). Verified by
/// Buy_FullBag_KnownBug_ChargesMoneyWithoutGrantingItem.
/// </summary>
[NotInParallel]
public class MerchantRigTests
{
    private const uint MerchantNpcObjId = 0x6001;

    // ---- rig helpers -------------------------------------------------------

    private static uint SpawnMerchant(HeadlessSession session, uint packId)
    {
        var npc = new Npc
        {
            ObjId = MerchantNpcObjId,
            TemplateId = 88_000,
            Id = 88_000,
            Template = new NpcTemplate { Id = 88_000, Merchant = true, MerchantPackId = packId },
            Hp = 100,
            MaxHp = 100
        };
        // Same headless registry bypass as GameplayActorTestRig.SummonSlave:
        // pre-set the Transform._instanceId / GameObject._parentWorld backing
        // fields so nothing touches the shared WorldManager world registry.
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(npc.Transform, session.World.Id);
        typeof(AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(npc, session.World);
        session.World.AddObject(npc);
        return npc.ObjId;
    }

    /// <summary>
    /// Encodes the CSBuyItemsPacket client payload (Read order: npcBc,
    /// doodadBc, unkId u32, nBuy u8, nBuyBack u8, per-item {itemId u32, grade
    /// u8, count i32, currency u8}, useAAPoint bool).
    /// </summary>
    private static PacketStream BuyPayload(uint npcObjId, params (uint itemId, int count)[] items)
    {
        var ps = new PacketStream();
        ps.WriteBc(npcObjId);
        ps.WriteBc(0); // doodadObjId — unused for NPC shops
        ps.Write(0u);  // unkId (shop type?)
        ps.Write((byte)items.Length);
        ps.Write((byte)0); // nBuyBack
        foreach (var (itemId, count) in items)
        {
            ps.Write(itemId);
            ps.Write((byte)0); // grade (server recomputes; packet passes -1 internally)
            ps.Write(count);
            ps.Write((byte)ShopCurrencyType.Money);
        }

        ps.Write(false); // useAAPoint
        return ps;
    }

    /// <summary>
    /// Encodes the CSSellItemsPacket client payload (Read order: npcBc,
    /// unkObjId Bc, num u8, per-item {slotType u8, slot u8, itemId u64,
    /// unkId u32}).
    /// </summary>
    private static PacketStream SellPayload(uint npcObjId, Item item)
    {
        var ps = new PacketStream();
        ps.WriteBc(npcObjId);
        ps.WriteBc(0); // unkObjId
        ps.Write((byte)1); // num
        ps.Write((byte)SlotType.Inventory);
        ps.Write((byte)item.Slot);
        ps.Write(item.Id);
        ps.Write(0u); // unkId
        return ps;
    }

    /// <summary>
    /// Delivers an encoded client payload to a freshly constructed shop
    /// packet bound to the capture-backed connection — the exact decode seam
    /// GameProtocolHandler.OnReceive drives (packet.Connection assignment +
    /// Read(PacketStream); these packets keep behavior inside Read).
    /// </summary>
    private static void Deliver(AAEmu.Game.Core.Network.Game.GamePacket packet, GameConnection connection, PacketStream payload)
    {
        // PacketBase<T>.Connection has a public setter (protected getter).
        packet.Connection = connection;
        packet.Read(payload);
    }

    private static (GameplayActor Actor, HeadlessSession Session, GameConnection Conn, uint NpcObjId) Rig(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        var conn = GameplayActorTestRig.AttachCaptureConnection(actor);

        // Always-applied idempotent seeds (sibling rigs can swap ItemManager
        // mid-suite — the one-shot rig seed alone is not enough, t_4f11a519).
        GameplayActorTestRig.SeedTradeItemTemplate(GameplayActorTestRig.BuyItemTemplateId, price: 50, refund: 0, sellable: false);
        GameplayActorTestRig.SeedTradeItemTemplate(GameplayActorTestRig.SellItemTemplateId, price: 0, refund: 25, sellable: true);
        var pack = GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var npcObjId = SpawnMerchant(session, pack);
        return (actor, session, conn, npcObjId);
    }

    // ---- 1. buy happy path -------------------------------------------------

    [Test]
    public async Task Buy_SeededMerchant_ItemGrantedAndMoneyDeducted()
    {
        var (actor, _, conn, npcObjId) = Rig("merch-buy");
        GameplayActorTestRig.SetMoney(actor, 10_000);

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayload(npcObjId, (GameplayActorTestRig.BuyItemTemplateId, 2))));

        var granted = GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.BuyItemTemplateId);
        await Assert.That(granted).IsNotNull();
        await Assert.That(granted!.Count).IsEqualTo(2);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 2 * 50); // Price=50 per unit
    }

    // ---- 2. buy insufficient funds — documents BUG #1 ----------------------

    [Test]
    public async Task Buy_InsufficientFunds_KnownBug_PurchaseProceedsAndMoneyGoesNegative()
    {
        var (actor, _, conn, npcObjId) = Rig("merch-poor");
        GameplayActorTestRig.SetMoney(actor, 10); // 40 short of the 50-price item

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayload(npcObjId, (GameplayActorTestRig.BuyItemTemplateId, 1))));

        // SPEC expectation: refused, money unchanged, no item.
        // ACTUAL (BUG #1, CSBuyItemsPacket.cs:119-122): the gate needs ALL
        // THREE currencies overdrawn (&& instead of ||) — honor/vocation are
        // 0 so it never fires; the item is granted and Money goes negative.
        var granted = GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.BuyItemTemplateId);
        await Assert.That(granted).IsNotNull(); // documents the bug
        await Assert.That(actor.Character.Money).IsEqualTo(10 - 50); // -40: negative balance accepted
    }

    // ---- 3. buy with a full bag — documents BUG #3 -------------------------

    [Test]
    public async Task Buy_FullBag_KnownBug_ChargesMoneyWithoutGrantingItem()
    {
        var (actor, session, conn, npcObjId) = Rig("merch-fullbag");
        GameplayActorTestRig.SetMoney(actor, 10_000);

        // Shrink the bag to one slot and occupy it with a max-size stack of
        // the ordinary rig item (MaxCount 99) — the merchant item no longer
        // fits (AcquireDefaultItemEx space pre-check fails → returns false).
        actor.Character.Inventory.Bag.ContainerSize = 1;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 99);
        var before = actor.Character.Money;

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayload(npcObjId, (GameplayActorTestRig.BuyItemTemplateId, 1))));

        // SPEC expectation: no grant AND no charge.
        // ACTUAL (BUG #3, CSBuyItemsPacket.cs:128): the grant's false return
        // is ignored; the purchase price is still deducted (lines 162-165).
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.BuyItemTemplateId)).IsNull();
        await Assert.That(actor.Character.Money).IsEqualTo(before - 50); // paid, received nothing
    }

    // ---- 4. sell happy path ------------------------------------------------

    [Test]
    public async Task Sell_SeededMerchant_MoneyCreditedAndItemMovedToBuyBack()
    {
        var (actor, session, conn, npcObjId) = Rig("merch-sell");
        GameplayActorTestRig.SetMoney(actor, 1_000);
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, 3);
        var item = GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId);
        await Assert.That(item).IsNotNull();

        new AAEmu.Game.Core.Packets.C2G.CSSellItemsPacket()
            .Tap(p => Deliver(p, conn, SellPayload(npcObjId, item!)));

        // Refund formula (CSSellItemsPacket.cs:61): Refund(25) *
        // RefundMultiplier(grade0=100)/100 * Count(3) = 75.
        await Assert.That(actor.Character.Money).IsEqualTo(1_075);
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId)).IsNull();
        await Assert.That(actor.Character.BuyBackItems.GetItemByItemId(item!.Id)).IsNotNull();
    }

    // ---- 5. sell into a full buyback container — documents BUG #2 ----------

    [Test]
    public async Task Sell_BuyBackContainerFull_KnownBug_RefundPaidWhileItemStaysInBag()
    {
        var (actor, session, conn, npcObjId) = Rig("merch-sellfull");
        GameplayActorTestRig.SetMoney(actor, 1_000);
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, 1);
        var item = GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId);
        await Assert.That(item).IsNotNull();

        // Zero-capacity buyback container: the engine-side move MUST refuse.
        actor.Character.BuyBackItems.ContainerSize = 0;

        new AAEmu.Game.Core.Packets.C2G.CSSellItemsPacket()
            .Tap(p => Deliver(p, conn, SellPayload(npcObjId, item!)));

        // SPEC expectation: refusal — no payout while the item never left
        // the seller's possession.
        // ACTUAL (BUG #2, CSSellItemsPacket.cs:49-65): the failed move only
        // warns; the refund is accumulated outside the success branch and
        // paid unconditionally — the player keeps the item AND gets the gold.
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId)).IsNotNull();
        await Assert.That(actor.Character.Money).IsEqualTo(1_000 + 25); // refund paid anyway
    }
}

/// <summary>Tiny extension: run an action against a value, return the value.</summary>
file static class MerchantRigTapExtensions
{
    public static T Tap<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
