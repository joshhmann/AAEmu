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
/// ENGINE BUGS surfaced by this rig — all three FIXED (each with its own
/// regression test below; the original buggy assertions are preserved in
/// git history as the discovery record):
///
/// BUG #1 (buy funds gate) — CSBuyItemsPacket: the refusal gate used to
/// join the three currency checks with &amp;&amp; instead of per-currency
/// refusal, so a purchase was only refused when ALL THREE balances were
/// overdrawn simultaneously. An insolvent money buy granted the item and
/// drove Money NEGATIVE through ChangeMoney(None→Inventory), which has no
/// funds guard on that path. FIXED: three independent OR-shaped gates with
/// matching error feedback (NotEnoughMoney / NotEnoughHonorPoint /
/// NotEnoughLivingPoint).
/// Regression: Buy_InsufficientFunds_RefusedCleanly_MoneyAndBagUntouched.
///
/// BUG #2 (sell refund on refused move) — CSSellItemsPacket: the refund
/// was accumulated OUTSIDE the success branch of the BuyBackItems move.
/// When AddOrMoveExistingItem failed the item stayed in the bag while the
/// payout was still credited — a dupe vector. FIXED: refund accumulation
/// lives strictly inside the success branch.
/// Regression: Sell_BuyBackContainerFull_Refused_NoRefundItemStaysInBag.
///
/// BUG #3 (buy ignores grant failure) — CSBuyItemsPacket: the return value
/// of AcquireDefaultItem was ignored; a full bag silently failed the grant
/// while the purchase price was still charged. FIXED: grants run through
/// AcquireDefaultItemEx with per-line stack snapshots and any failure rolls
/// the whole purchase back atomically before any charge (BagFull error).
/// Regression: Buy_FullBag_RefusedAtomically_NoChargeNoPartialItems.
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

    // ---- 2. buy insufficient funds — regression: BUG #1 (funds gate) ------

    [Test]
    public async Task Buy_InsufficientFunds_RefusedCleanly_MoneyAndBagUntouched()
    {
        var (actor, _, conn, npcObjId) = Rig("merch-poor");
        GameplayActorTestRig.SetMoney(actor, 10); // 40 short of the 50-price item

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayload(npcObjId, (GameplayActorTestRig.BuyItemTemplateId, 1))));

        // FIXED (BUG #1, CSBuyItemsPacket.cs): the refusal gate is OR-shaped
        // per currency — a money shortfall alone refuses the purchase before
        // any grant or charge. The old && gate never fired when honor and
        // vocation had no shortfall, letting Money go negative.
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.BuyItemTemplateId)).IsNull();
        await Assert.That(actor.Character.Money).IsEqualTo(10); // untouched
    }

    // ---- 3. buy with a full bag — regression: BUG #3 -----------------------

    [Test]
    public async Task Buy_FullBag_RefusedAtomically_NoChargeNoPartialItems()
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

        // FIXED (BUG #3, CSBuyItemsPacket.cs): grant failure rolls the whole
        // purchase back atomically — no charge, no partial items. The old
        // code ignored the grant result and deducted the price regardless.
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.BuyItemTemplateId)).IsNull();
        await Assert.That(actor.Character.Money).IsEqualTo(before); // untouched
    }

    // ---- 3b. multi-line buy with a late grant failure — BUG #3 atomicity ---

    [Test]
    public async Task Buy_MultiLineLateGrantFailure_RollsBackEarlierLines()
    {
        var (actor, session, conn, npcObjId) = Rig("merch-multibuy");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.SellItemTemplateId); // second line
        // Two-slot bag holding one max-size stack: line 1 grants into the
        // single free slot (succeeds), line 2 then fails its space pre-check
        // — the earlier line's grant must be rolled back, not kept as a
        // partial purchase.
        actor.Character.Inventory.Bag.ContainerSize = 2;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 99);
        var before = actor.Character.Money;

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayload(npcObjId,
                (GameplayActorTestRig.BuyItemTemplateId, 2),
                (GameplayActorTestRig.SellItemTemplateId, 1))));

        // Atomic purchase: neither line landed, nothing was charged.
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.BuyItemTemplateId)).IsNull();
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId)).IsNull();
        await Assert.That(actor.Character.Money).IsEqualTo(before);
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

    // ---- 5. sell into a full buyback container — regression: BUG #2 -------

    [Test]
    public async Task Sell_BuyBackContainerFull_Refused_NoRefundItemStaysInBag()
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

        // FIXED (BUG #2, CSSellItemsPacket.cs): the refund is accumulated
        // strictly inside the success branch of the buyback move — a refused
        // move pays nothing. The old code paid the refund unconditionally,
        // leaving the item in the bag AND the gold credited (dupe vector).
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId)).IsNotNull();
        await Assert.That(actor.Character.Money).IsEqualTo(1_000); // refund withheld
        await Assert.That(actor.Character.BuyBackItems.GetItemByItemId(item!.Id)).IsNull();
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
