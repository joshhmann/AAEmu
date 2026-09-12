using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;

using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;

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
///
/// BUG #4 (buy shop-source bypass) — CSBuyItemsPacket: the item-membership
/// gate was `if (npcObjId != 0 &amp;&amp; (pack == null || !pack.SellsItem(itemId)))`,
/// so a crafted packet with npcObjId=0 (and either doodadObjId=0 or ANY
/// nearby doodad) skipped the membership check entirely and bought any
/// registered item template at its list price. FIXED: the membership gate is
/// unconditional, and a doodad-only packet must resolve a real shop source —
/// doodad.FuncGroupId → DoodadManager funcs → DoodadFuncStoreUi →
/// MerchantPackId → NpcManager pack — before any line is considered (blanket
/// fail-closed was rejected: the canonical doodad→goods chain is real).
/// Regressions: Buy_NoNpcNoDoodad_ArbitraryItem_RefusedNoGrantNoCharge,
/// Buy_DoodadWithoutStoreUi_RefusedNoGrantNoCharge,
/// Buy_RealMerchant_ItemNotInPack_Refused.
/// Over-refusal guard for the new path: Buy_DoodadWithStoreUi_SellsPackItem_Granted.
/// </summary>
[NotInParallel]
public class MerchantRigTests
{
    private const uint MerchantNpcObjId = 0x6001;

    // ---- BUG #4 (shop-source gate) fixtures --------------------------------
    //
    // A template deliberately NOT added to any merchant pack: the gate must
    // be membership-based, so this must be refused no matter how the packet
    // names (or omits) its shop source.
    private const uint NotSoldItemTemplateId = 88_901;
    /// <summary>ObjId of the rig doodad (one per test world).</summary>
    private const uint DoodadObjId = 0x6002;
    private const uint DoodadTemplateId = 88_702;
    /// <summary>Func group registered with NO funcs at all — no store UI, no goods.</summary>
    private const uint NoStoreUiFuncGroupId = 88_703;
    /// <summary>Func group carrying the seeded DoodadFuncStoreUi row below.</summary>
    private const uint StoreUiFuncGroupId = 88_704;
    private const uint StoreUiFuncId = 88_705;

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
    /// Spawns a doodad 1 m from the actor, registered in the session world so
    /// <c>ParentWorld.GetDoodad(doodadObjId)</c> resolves — the exact lookup
    /// CSBuyItemsPacket performs. Same headless-registry bypass as
    /// <see cref="SpawnMerchant"/> (no DoodadManager template requirement:
    /// the packet reads only FuncGroupId through the manager's func tables).
    /// </summary>
    private static uint SpawnDoodadAtActor(HeadlessSession session, GameplayActor actor, uint doodadTemplateId,
        out Doodad doodad)
    {
        doodad = new Doodad { ObjId = DoodadObjId, TemplateId = doodadTemplateId };
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(doodad.Transform, session.World.Id);
        typeof(AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(doodad, session.World);
        // 1 m from the actor — comfortably inside the packet's 3 m shop range.
        var actorPos = actor.Character.Transform.World.Position;
        doodad.Transform.Local.SetPosition(new System.Numerics.Vector3(actorPos.X + 1f, actorPos.Y, actorPos.Z));
        session.World.AddObject(doodad);
        return doodad.ObjId;
    }

    /// <summary>
    /// Seeds a DoodadFuncStoreUi func row + template in the DoodadManager
    /// func tables (the same reflection pattern <see cref="SpawnMerchant"/>
    /// uses for the NPC side; DoodadManager exposes no test-injection point).
    /// A doodad on this group resolves to <paramref name="merchantPackId"/>.
    /// </summary>
    private static void SeedStoreUiFunc(uint groupId, uint funcId, uint merchantPackId)
    {
        var manager = DoodadManager.Instance;
        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)GameplayActorTestRig.GetField(manager, "_funcsByGroups");
        var funcsById = (Dictionary<uint, DoodadFunc>)GameplayActorTestRig.GetField(manager, "_funcsById");
        var funcTemplates = (Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)GameplayActorTestRig.GetField(manager, "_funcTemplates");

        var func = new DoodadFunc
        {
            GroupId = groupId,
            FuncId = funcId,
            FuncKey = funcId,
            FuncType = "DoodadFuncStoreUi",
            NextPhase = -1,
            SkillId = 0
        };
        if (!funcsById.ContainsKey(funcId))
            funcsById[funcId] = func;
        if (!funcsByGroups.TryGetValue(groupId, out var group))
        {
            group = [];
            funcsByGroups[groupId] = group;
        }
        if (group.All(f => f.FuncId != funcId))
            group.Add(func);

        if (!funcTemplates.TryGetValue("DoodadFuncStoreUi", out var storeUiTemplates))
        {
            storeUiTemplates = [];
            funcTemplates["DoodadFuncStoreUi"] = storeUiTemplates;
        }
        storeUiTemplates[funcId] = new DoodadFuncStoreUi { Id = funcId, MerchantPackId = merchantPackId };
    }

    /// <summary>
    /// Ensures the DoodadManager singleton and its func dictionaries exist —
    /// the exact surface <c>DoodadManager.Instance.GetFuncsForGroup/
    /// GetFuncTemplate</c> reads. DoodadManager is DI-only (no parameterless
    /// ctor) and the headless rig never seeds it for merchant tests, so the
    /// missing-only instance bootstrap mirrors
    /// GameplayActorTestRig.SeedDoodadManager (private there); a sibling
    /// rig's established manager is never replaced.
    /// </summary>
    private static void SeedDoodadShopSurface()
    {
        if (!GameplayActorTestRig.SingletonSeeded(typeof(Singleton<DoodadManager>)))
        {
            var objectIdManager = Mock.Of<IObjectIdManager>();
            objectIdManager.GetNextId().Returns(0x200000u);
            GameplayActorTestRig.SeedSingleton(typeof(Singleton<DoodadManager>),
                new DoodadManager(
                    objectIdManager.Object,
                    Mock.Of<IDoodadIdManager>().Object,
                    ItemManager.Instance,
                    new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
                    Mock.Of<ISusManager>().Object));
        }

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        foreach (var name in new[] { "_templates", "_funcsByGroups", "_funcsById", "_funcTemplates", "_phaseFuncs", "_phaseFuncTemplates" })
        {
            var field = typeof(DoodadManager).GetField(name, flags);
            if (field?.GetValue(DoodadManager.Instance) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field!.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(DoodadManager.Instance, Activator.CreateInstance(dictType));
            }
        }
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
    /// Same encoding as <see cref="BuyPayload"/>, but with an explicit
    /// <paramref name="doodadObjId"/> — the client-controlled doodad-shop
    /// field (BUG #4's exploit input).
    /// </summary>
    private static PacketStream BuyPayloadWithDoodad(uint npcObjId, uint doodadObjId,
        params (uint itemId, int count)[] items)
    {
        var ps = new PacketStream();
        ps.WriteBc(npcObjId);
        ps.WriteBc(doodadObjId);
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
    /// Encodes the buyback half of the CSBuyItemsPacket client payload
    /// (nBuy = 0, nBuyBack = slotIndices.Length, per-entry buyback slot
    /// index i32, useAAPoint bool).
    /// </summary>
    private static PacketStream BuybackPayload(uint npcObjId, params int[] slotIndices)
    {
        var ps = new PacketStream();
        ps.WriteBc(npcObjId);
        ps.WriteBc(0); // doodadObjId — unused for NPC shops
        ps.Write(0u);  // unkId (shop type?)
        ps.Write((byte)0); // nBuy
        ps.Write((byte)slotIndices.Length); // nBuyBack
        foreach (var slot in slotIndices)
            ps.Write(slot);
        ps.Write(false); // useAAPoint
        return ps;
    }

    /// <summary>
    /// Attaches a FRESH capture-backed connection we can inspect (Rig's
    /// connection hides its session) and returns both ends.
    /// </summary>
    private static (GameConnection Conn, PacketCaptureSession Capture) InspectableConn(GameplayActor actor)
    {
        var capture = new PacketCaptureSession();
        var conn = new GameConnection(capture) { ActiveChar = actor.Character };
        actor.Character.Connection = conn;
        return (conn, capture);
    }

    /// <summary>Counts captured packets carrying the given G2C opcode.</summary>
    private static int CapturedOpcodeCount(PacketCaptureSession capture, ushort opcode)
    {
        var count = 0;
        foreach (var bytes in capture.CapturedPackets)
        {
            try
            {
                var stream = new PacketStream();
                stream.Write(bytes);
                stream.ReadUInt16(); // length prefix
                stream.ReadByte();   // 0xdd
                stream.ReadByte();   // level (1)
                stream.ReadByte();   // hash (0)
                stream.ReadByte();   // count (0)
                if (stream.ReadUInt16() == opcode) // TypeId
                    count++;
            }
            catch
            {
                // malformed capture — skip
            }
        }
        return count;
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

    // ---- 3c. shop-source gate — regression: BUG #4 (buy bypass) ------------

    [Test]
    public async Task Buy_NoNpcNoDoodad_ArbitraryItem_RefusedNoGrantNoCharge()
    {
        var (actor, _, conn, _) = Rig("merch-nosource");
        GameplayActorTestRig.SeedTradeItemTemplate(NotSoldItemTemplateId, price: 50, refund: 0, sellable: false);
        GameplayActorTestRig.SetMoney(actor, 10_000);

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayload(0, (NotSoldItemTemplateId, 1))));

        // FIXED (BUG #4, CSBuyItemsPacket.cs): the membership gate reads
        // `pack == null || !pack.SellsItem(itemId)` with no npcObjId guard, so
        // a source-less packet has no pack to authorise it. The old gate was
        // `npcObjId != 0 && (...)`, so npcObjId=0 skipped the check entirely
        // and bought any registered template at its list price.
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, NotSoldItemTemplateId)).IsNull();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000); // untouched
    }

    [Test]
    public async Task Buy_RealMerchant_ItemNotInPack_Refused()
    {
        var (actor, _, conn, npcObjId) = Rig("merch-notinpack");
        GameplayActorTestRig.SeedTradeItemTemplate(NotSoldItemTemplateId, price: 50, refund: 0, sellable: false);
        GameplayActorTestRig.SetMoney(actor, 10_000);

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayload(npcObjId, (NotSoldItemTemplateId, 1))));

        // The gate is MEMBERSHIP-based, not "an NPC exists": the merchant is
        // real, in range and packing goods — this template simply is not one
        // of them. (The rig's other buy tests only ever request in-pack items.)
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, NotSoldItemTemplateId)).IsNull();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000); // untouched
    }

    [Test]
    public async Task Buy_DoodadWithoutStoreUi_RefusedNoGrantNoCharge()
    {
        var (actor, session, _, _) = Rig("merch-nostoreui");
        GameplayActorTestRig.SeedTradeItemTemplate(NotSoldItemTemplateId, price: 50, refund: 0, sellable: false);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        var (conn, capture) = InspectableConn(actor);

        // A real, resolvable doodad within shop range whose current func
        // group carries NO DoodadFuncStoreUi row — proximity alone must not
        // authorise a purchase.
        SeedDoodadShopSurface();
        var doodadObjId = SpawnDoodadAtActor(session, actor, DoodadTemplateId, out var doodad);
        doodad.FuncGroupId = NoStoreUiFuncGroupId;

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayloadWithDoodad(0, doodadObjId, (NotSoldItemTemplateId, 1))));

        // FIXED (BUG #4, CSBuyItemsPacket.cs): the doodad branch resolves a
        // goods pack through doodad.FuncGroupId → DoodadFuncStoreUi →
        // MerchantPackId and refuses when there is none. The old gate was
        // skipped entirely for npcObjId=0, so this exact packet granted the
        // item and charged for it.
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, NotSoldItemTemplateId)).IsNull();
        await Assert.That(actor.Character.Money).IsEqualTo(10_000); // untouched
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCItemTaskSuccessPacket)).IsEqualTo(0);
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCErrorMsgPacket)).IsEqualTo(1);
    }

    [Test]
    public async Task Buy_DoodadWithStoreUi_SellsPackItem_Granted()
    {
        var (actor, session, _, _) = Rig("merch-storeui");
        GameplayActorTestRig.SetMoney(actor, 10_000);

        // Guard against over-refusal on the NEW path: a doodad whose func
        // group really carries DoodadFuncStoreUi → a seeded pack holding the
        // rig buy item must still be able to sell it (the canonical
        // General Vendor doodad chain, doodad_func_store_uis).
        SeedDoodadShopSurface();
        SeedStoreUiFunc(StoreUiFuncGroupId, StoreUiFuncId, GameplayActorTestRig.MerchantPackId);
        var doodadObjId = SpawnDoodadAtActor(session, actor, DoodadTemplateId, out var doodad);
        doodad.FuncGroupId = StoreUiFuncGroupId;

        var (conn, _) = InspectableConn(actor);
        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuyPayloadWithDoodad(0, doodadObjId, (GameplayActorTestRig.BuyItemTemplateId, 2))));

        var granted = GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.BuyItemTemplateId);
        await Assert.That(granted).IsNotNull();
        await Assert.That(granted!.Count).IsEqualTo(2);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - 2 * 50); // Price=50 per unit
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
    // ---- 6. buyback rebuy with a full bag — paid + lost without the fix ---

    [Test]
    public async Task BuyBack_FullBag_RefusedAtomically_NoChargeItemStaysInBuyBack()
    {
        var (actor, session, _, npcObjId) = Rig("merch-buyback-full");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        var (conn, capture) = InspectableConn(actor);

        // Sell one item: it leaves the bag for the (non-persisted) buyback
        // window and the refund is credited (25 * 100/100 * 1 = 25).
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, 1);
        var item = GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId);
        await Assert.That(item).IsNotNull();
        var soldItemId = item!.Id;
        new AAEmu.Game.Core.Packets.C2G.CSSellItemsPacket()
            .Tap(p => Deliver(p, conn, SellPayload(npcObjId, item)));
        var buybackSlot = actor.Character.BuyBackItems.GetItemByItemId(soldItemId)?.Slot;
        await Assert.That(buybackSlot).IsNotNull();
        var moneyBeforeRebuy = actor.Character.Money;

        // Fill the single-slot bag with an unrelated max stack — the rebuy
        // grant cannot land (AddOrMoveExistingItem returns false).
        actor.Character.Inventory.Bag.ContainerSize = 1;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 99);
        capture.CapturedPackets.Clear();

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuybackPayload(npcObjId, buybackSlot!.Value)));

        // FIXED (CSBuyItemsPacket.cs buyback path): fail-closed — the refund
        // price is NOT charged, the item stays in the buyback window (a
        // charged-but-ungranted item would sit in the non-persisted BuyBack
        // container and be wiped on relogin: paid + lost), no success packet
        // is emitted, and the client gets the BagFull error idiom.
        await Assert.That(actor.Character.Money).IsEqualTo(moneyBeforeRebuy); // untouched
        await Assert.That(actor.Character.BuyBackItems.GetItemByItemId(soldItemId)).IsNotNull();
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId)).IsNull();
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCItemTaskSuccessPacket)).IsEqualTo(0);
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCErrorMsgPacket)).IsEqualTo(1);
    }

    // ---- 7. buyback rebuy happy path (must stay green before/after) --------

    [Test]
    public async Task BuyBack_WithSpace_GrantsItemAndChargesRefund()
    {
        var (actor, session, _, npcObjId) = Rig("merch-buyback-ok");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        var (conn, capture) = InspectableConn(actor);

        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.SellItemTemplateId, 3);
        var item = GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId);
        await Assert.That(item).IsNotNull();
        var soldItemId = item!.Id;
        new AAEmu.Game.Core.Packets.C2G.CSSellItemsPacket()
            .Tap(p => Deliver(p, conn, SellPayload(npcObjId, item)));
        // Refund formula: Refund(25) * RefundMultiplier(grade0=100)/100 * Count(3) = 75.
        await Assert.That(actor.Character.Money).IsEqualTo(10_075);
        var buybackSlot = actor.Character.BuyBackItems.GetItemByItemId(soldItemId)?.Slot;
        await Assert.That(buybackSlot).IsNotNull();
        capture.CapturedPackets.Clear();

        new AAEmu.Game.Core.Packets.C2G.CSBuyItemsPacket()
            .Tap(p => Deliver(p, conn, BuybackPayload(npcObjId, buybackSlot!.Value)));

        // Sell + rebuy roundtrip is money-neutral and item-neutral.
        await Assert.That(actor.Character.Money).IsEqualTo(10_000);
        await Assert.That(GameplayActorTestRig.FindBagItem(actor, GameplayActorTestRig.SellItemTemplateId)).IsNotNull();
        await Assert.That(actor.Character.BuyBackItems.GetItemByItemId(soldItemId)).IsNull();
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCItemTaskSuccessPacket)).IsGreaterThanOrEqualTo(1);
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
