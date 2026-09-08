using System.Net;
using System.Net.Sockets;
using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Game.World.Interactions;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M8 C4 hauler/trader v1 slice-4 (ROADMAP M8 living-village contracts) —
/// inventory-full vendoring trip + two-pack cycle through
/// <see cref="HaulerVendorMultipackCycle"/>: audit a near-full bag, vendor
/// ONLY IsTrash-classified junk at the home merchant (quest items, essential
/// consumables, unsellables, and craft materials never sold), craft→load two
/// packs, drive both to the gold trader, unload→sell each with per-pack
/// payout proof, bank the combined proceeds, drive home empty.
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (protected row sold, refused vendor that still crafts, merged/lost pack
/// payouts, double deposit, same-key retry that pays twice) and PASSES on
/// the composed real paths. Raw engine rejections are already pinned by the
/// GameplayActor contract tests — these tests pin the COMPOSER's leg order,
/// IsTrash guards, per-pack conservation, and the combined ledger.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class HaulerVendorMultipackCycleTests
{
    private static readonly Vector3 HomePosition = new(1000f, 1000f, 100f);
    private static readonly Vector3 TraderPosition = new(1100f, 1000f, 100f); // 100 units east

    // Fixture pack recipe (the slice-1/3 SeedFixturePackRecipe shape, own
    // ids): 1 × potato (7992) → 1 × fixture cargo pack (264901), labor 10.
    private const uint PackCraftId = 99_043;
    private const uint PackCraftSkillId = 99_044;
    private const int PackCraftLaborCost = 10;
    private const uint PackMaterialItemId = 7992; // potato (plain, unsellable → never trash)

    // Trash fixtures: 44 distinct gray non-equip templates (MaxCount 1 →
    // one slot each), refund 10+i. Protected fixtures: a sellable potion
    // (essential category beats sellable+refund), a sellable quest item
    // (quest guard beats sellable+refund), an unsellable keepsake.
    private const uint TrashTemplateBase = 88_201;
    private const int TrashRowCount = 44;
    private const uint PotionTemplateId = 88_301;
    private const uint QuestItemTemplateId = 88_302;
    private const uint KeepsakeTemplateId = 88_303;

    // Fixture specialty surface (the slice-3 shape, same bundle math):
    // base 1000, payout 1365 per pack @ fresh-manager max ratio 130%.
    private const uint GoldTraderTemplateId = 1003;
    private const uint VendorMerchantTemplateId = 1004;
    private const uint SaleBundleId = 77;
    private const int BundleProfit = 1000;
    private const int BundleRatioStatic = 1000;
    private const uint SaleZoneKey = 142;
    private const uint SaleZoneGroup = 5;
    private const uint PackOriginGroup = 26;
    private const int ExpectedBasePrice = 1000;
    private const long ExpectedPayoutEach = 1365;

    private const int SellLaborCost = 60;
    private const int StartLabor = 1000;

    private static uint s_nextWorldId = 0x6400_0000; // fresh base: 0x6100/0x6200/0x6300 hauler, 0x7000 economy

    private WorldInstance? _registeredWorld;
    private WorldConfig? _previousWorldConfig;
    private static object? _previousSusManager;
    private static object? _previousModelManager;
    private static object? _previousZoneManager;
    private static object? _previousSpecialtyManager;
    private static object? _previousMailManager;
    private static object? _previousNameManager;
    private static object? _previousCharacterManager;
    private static object? _previousItemIdManager;
    private static int _previousMinLevel;

    [Before(Test)]
    public void SetUp()
    {
        // Dead-port MySQL (the slice-3 convention): the UNLOAD legs delete
        // cargo doodads via RecoverItem, the SELL legs create payout mails.
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });

        GameplayActorTestRig.SeedCargoPackSurface();
        GameplayActorTestRig.SeedCraftSurface();
        GameplayActorTestRig.SeedTradeSurface(); // grades (refund math) + npc surface
        SeedFixturePackRecipe();
        GameplayActorTestRig.SeedItemTemplate(PackMaterialItemId);
        SeedVendorTemplates();
        SeedEquipSurface();
        SeedSaleSurfaces();
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
        AppConfiguration.Instance.Specialty.MinLevelToCraftSell = _previousMinLevel;
        SetSingleton(typeof(Singleton<ZoneManager>), _previousZoneManager);
        SetSingleton(typeof(Singleton<SpecialtyManager>), _previousSpecialtyManager);
        SetSingleton(typeof(Singleton<MailManager>), _previousMailManager);
        SetSingleton(typeof(Singleton<NameManager>), _previousNameManager);
        SetSingleton(typeof(Singleton<CharacterManager>), _previousCharacterManager);
        if (_previousItemIdManager != null)
        {
            var idField = typeof(ItemManager).GetField("<itemIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? typeof(ItemManager).GetField("itemIdManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            idField?.SetValue(ItemManager.Instance, _previousItemIdManager);
            _previousItemIdManager = null;
        }
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousSusManager);
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousModelManager);
        _previousSusManager = null;
        _previousModelManager = null;
        MySQL.SetConfiguration(null);
        if (_registeredWorld != null)
        {
            var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                .GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(WorldManager.Instance)!;
            if (worlds.TryGetValue(_registeredWorld.Id, out var registered) && ReferenceEquals(registered, _registeredWorld))
                worlds.TryRemove(_registeredWorld.Id, out _);
            _registeredWorld = null;
        }
    }

    [Test]
    public async Task VendorMultipack_HappyPath_2PacksConserved()
    {
        var (actor, session, slave, vendorObjId, traderObjId) = CreateRig("m8c4-v1-full", 0x3450u);
        FillBag(actor);

        var moneyBefore = actor.Character.Money;
        var bankBefore = actor.Character.Money2;
        var result = HaulerVendorMultipackCycle.Run(actor, Options("m8c4-v1-full", slave.ObjId, vendorObjId, traderObjId), new HaulVendorPump());

        if (!result.Passed)
        {
            var diag =
                $"FAILED at {result.FailStage} ({result.Failure}): {result.FailReason}\n" +
                string.Join("\n", result.Criteria.Select(c => $"- criterion [{c.Name}]: {(c.Passed ? "PASS" : "FAIL")} {c.Detail}"));
            Console.WriteLine(diag);
        }
        await Assert.That(result.Passed).IsTrue();
        foreach (var name in new[]
                 {
                     "precheck-clean", "vendor-never-delete", "vendor-completed", "vendor-revenue", "vendor-freed-slots",
                     "board-completed", "pack-craft-1-completed", "load-1-completed", "load-1-conservation",
                     "pack-craft-2-completed", "load-2-completed", "load-2-conservation", "load-distinct-cargo-points",
                     "pack-craft-materials-conserved", "drive-completed", "drive-arrival", "drive-pack-conserved",
                     "unload-1-pack-recovered", "sell-1-completed", "sell-1-payout-formula",
                     "unload-2-pack-recovered", "sell-2-completed", "sell-2-payout-formula",
                     "labor-conserved", "deposit-completed", "deposit-conservation", "full-leg-ledger",
                     "return-home-completed", "return-home-arrival", "return-home-wagon-empty"
                 })
            await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();

        // Per-pack specialty proof: base 1000 + payout 1365 each.
        await Assert.That(result.BasePrices.Count).IsEqualTo(2);
        await Assert.That(result.BasePrices.All(b => b == ExpectedBasePrice)).IsTrue();
        await Assert.That(result.Payouts.Count).IsEqualTo(2);
        await Assert.That(result.Payouts.All(p => p == ExpectedPayoutEach)).IsTrue();

        // Vendor: all 44 trash rows sold for exactly Σ refund; the trip
        // freed the bag.
        var expectedRevenue = Enumerable.Range(0, TrashRowCount).Sum(i => 10 + i);
        await Assert.That(result.VendorSold).IsEqualTo(TrashRowCount);
        await Assert.That(result.VendorRevenue).IsEqualTo(expectedRevenue);

        // Never-delete: potion, quest item, keepsake byte-identical; the
        // trash templates are gone from the bag.
        await Assert.That(GameplayActorTestRig.BagCount(actor, PotionTemplateId)).IsEqualTo(3);
        await Assert.That(GameplayActorTestRig.BagCount(actor, QuestItemTemplateId)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, KeepsakeTemplateId)).IsEqualTo(1);
        for (var i = 0; i < TrashRowCount; i++)
            await Assert.That(GameplayActorTestRig.BagCount(actor, TrashTemplateBase + (uint)i)).IsEqualTo(0);

        // Multi-pack products: two distinct packs crafted, then both
        // consumed — nowhere left.
        await Assert.That(result.PackItemIds.Count).IsEqualTo(2);
        await Assert.That(result.PackItemIds.Distinct().Count()).IsEqualTo(2);
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId > 0)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actor, PackMaterialItemId)).IsEqualTo(0);

        // Per-pack specialty proof: two mails totaling 2× payout.
        await Assert.That(MailCopper(actor)).IsEqualTo(2 * ExpectedPayoutEach);

        // Combined ledger: bank +2730; inventory +1386 vendor −2730 banked.
        await Assert.That(result.BankDeposited).IsEqualTo(2 * ExpectedPayoutEach);
        await Assert.That(actor.Character.Money2 - bankBefore).IsEqualTo(2 * ExpectedPayoutEach);
        await Assert.That(actor.Character.Money - moneyBefore).IsEqualTo(expectedRevenue - 2 * ExpectedPayoutEach);

        // Labor: 2× craft 10 + 2× sell 60, exact.
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(StartLabor - 2 * PackCraftLaborCost - 2 * SellLaborCost));
        await Assert.That(result.LaborCharged).IsEqualTo(2 * PackCraftLaborCost + 2 * SellLaborCost);

        // Home: the empty wagon arrived.
        await Assert.That(Math.Abs(slave.Transform.World.Position.X - HomePosition.X)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(slave.Transform.World.Position.Y - HomePosition.Y)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(slave.Transform.World.Position.Z - HomePosition.Z)).IsLessThan(0.5f);
        await Assert.That(result.DistanceRemaining).IsLessThan(0.5f);
    }

    [Test]
    public async Task Vendor_NonMerchant_Holds_NothingSold()
    {
        // Fail-pre: the "vendor" is a gold trader (no merchant flag) — the
        // cycle must hold BEFORE mutating anything: no sale, no craft, no
        // board, no money movement.
        var (actor, session, slave, _, traderObjId) = CreateRig("m8c4-v1-nomerchant", 0x3460u);
        FillBag(actor);

        var moneyBefore = actor.Character.Money;
        var bankBefore = actor.Character.Money2;
        var result = HaulerVendorMultipackCycle.Run(actor,
            Options("m8c4-v1-nomerchant", slave.ObjId, traderObjId, traderObjId), new HaulVendorPump());

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("PRECHECK");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Nothing sold: all 44 trash rows retained, protected intact,
        // materials untouched, slot empty, cargo empty, money frozen.
        for (var i = 0; i < TrashRowCount; i++)
            await Assert.That(GameplayActorTestRig.BagCount(actor, TrashTemplateBase + (uint)i)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, PotionTemplateId)).IsEqualTo(3);
        await Assert.That(GameplayActorTestRig.BagCount(actor, PackMaterialItemId)).IsEqualTo(2);
        await Assert.That(actor.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(slave.AttachedDoodads.Count).IsEqualTo(0);
        await Assert.That(actor.Character.Money).IsEqualTo(moneyBefore);
        await Assert.That(actor.Character.Money2).IsEqualTo(bankBefore);
        await Assert.That(MailCopper(actor)).IsEqualTo(0);
    }

    [Test]
    public async Task Vendor_NeverDelete_ProtectedRowsSurviveWholeCycle()
    {
        // Fail-pre on the CLASSIFIER (not just the flow): a refund>0-only
        // rule would sell the potion (100c) and the quest item (200c) — the
        // IsTrash guards must keep every protected row byte-identical
        // through vendor AND the full multi-pack cycle, and the audit must
        // classify exactly the 44 trash rows.
        var (actor, session, slave, vendorObjId, traderObjId) = CreateRig("m8c4-v1-neverdelete", 0x3470u);
        FillBag(actor);

        var auditBefore = BotBagManager.AuditBag(actor.Character);
        await Assert.That(auditBefore.IsNearFull).IsTrue();
        await Assert.That(auditBefore.TrashItems.Count).IsEqualTo(TrashRowCount);
        await Assert.That(auditBefore.TrashItems.All(t =>
            t.TemplateId >= TrashTemplateBase && t.TemplateId < TrashTemplateBase + TrashRowCount)).IsTrue();

        var result = HaulerVendorMultipackCycle.Run(actor, Options("m8c4-v1-neverdelete", slave.ObjId, vendorObjId, traderObjId), new HaulVendorPump());

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "vendor-never-delete" && c.Passed)).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor, PotionTemplateId)).IsEqualTo(3);
        await Assert.That(GameplayActorTestRig.BagCount(actor, QuestItemTemplateId)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, KeepsakeTemplateId)).IsEqualTo(1);
        // Post-cycle audit: no trash left anywhere, nothing damaged.
        var auditAfter = BotBagManager.AuditBag(actor.Character);
        await Assert.That(auditAfter.TrashItems.Count).IsEqualTo(0);
        await Assert.That(auditAfter.IsNearFull).IsFalse();
    }

    [Test]
    public async Task Multipack_PerPackConservation_DistinctPointsAndPayouts()
    {
        // Fail-pre on the multi-pack accounting: a composer that merged the
        // two packs (one cargo point, one combined mail, one payout entry)
        // fails here — two distinct instances, two points, two mails, two
        // payout proofs.
        var (actor, session, slave, vendorObjId, traderObjId) = CreateRig("m8c4-v1-perpack", 0x3480u);
        FillBag(actor);

        var result = HaulerVendorMultipackCycle.Run(actor, Options("m8c4-v1-perpack", slave.ObjId, vendorObjId, traderObjId), new HaulVendorPump());

        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "load-distinct-cargo-points" && c.Passed)).IsTrue();
        await Assert.That(result.PackItemIds.Count).IsEqualTo(2);
        await Assert.That(result.PackItemIds[0]).IsNotEqualTo(result.PackItemIds[1]);
        await Assert.That(result.Payouts.Count).IsEqualTo(2);
        await Assert.That(result.Payouts[0]).IsEqualTo(ExpectedPayoutEach);
        await Assert.That(result.Payouts[1]).IsEqualTo(ExpectedPayoutEach);
        await Assert.That(result.BasePrices.Count).IsEqualTo(2);

        // Two created mails (seller == crafter → one mail per pack).
        var mails = MailManager.Instance.AllPlayerMails.Values
            .Where(m => m.Header.ReceiverId == actor.Character.Id)
            .ToList();
        await Assert.That(mails.Count).IsEqualTo(2);
        await Assert.That(mails.Sum(m => (long)m.Body.CopperCoins)).IsEqualTo(2 * ExpectedPayoutEach);
        await Assert.That(mails.All(m => m.Body.CopperCoins == ExpectedPayoutEach)).IsTrue();
    }

    [Test]
    public async Task Cycle_SameKeyRetry_NeverDuplicatesAnything()
    {
        // Fail-pre: a retry that re-sells, re-vendors, or re-crafts creates
        // new mails, new revenue, or new packs — the same-key rerun must
        // hold at PRECHECK (materials consumed) with everything frozen.
        var (actor, session, slave, vendorObjId, traderObjId) = CreateRig("m8c4-v1-retry", 0x3490u);
        FillBag(actor);

        var first = HaulerVendorMultipackCycle.Run(actor, Options("m8c4-v1-retry", slave.ObjId, vendorObjId, traderObjId), new HaulVendorPump());
        await Assert.That(first.Passed).IsTrue();
        var bankAfterFirst = actor.Character.Money2;
        var moneyAfterFirst = actor.Character.Money;
        var mailsAfterFirst = MailCopper(actor);

        var retry = HaulerVendorMultipackCycle.Run(actor, Options("m8c4-v1-retry", slave.ObjId, vendorObjId, traderObjId), new HaulVendorPump());

        await Assert.That(retry.Passed).IsFalse();
        // Materials are gone (2 consumed), so the rerun holds at PRECHECK —
        // the engine-true backstop. No new mail, no bank/ inventory movement.
        await Assert.That(retry.FailStage).IsEqualTo("PRECHECK");
        await Assert.That(MailCopper(actor)).IsEqualTo(mailsAfterFirst);
        await Assert.That(actor.Character.Money2).IsEqualTo(bankAfterFirst);
        await Assert.That(actor.Character.Money).IsEqualTo(moneyAfterFirst);
        await Assert.That(slave.AttachedDoodads.Count(d => d.ItemId > 0)).IsEqualTo(0);
    }

    // ------------------------------------------------------------ rig below

    /// <summary>
    /// Builds the slice-4 precondition at home: cargo vehicle summoned (not
    /// boarded — the composer boards), vendor merchant + gold trader placed,
    /// craft bench spawned, level/labor/cash/zone set. Bag filling is per
    /// test (FillBag) so the trigger state is explicit at the call site.
    /// </summary>
    private (GameplayActor actor, HeadlessSession session, Slave slave, uint vendorObjId, uint traderObjId) CreateRig(
        string name, uint slaveObjId)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorld(session);
        SeedMovementSingletons();
        AttachCapture(actor);
        GameplayActorTestRig.SetPosition(actor, HomePosition);
        WorldManager.Instance.AddVisibleObject(actor.Character);
        actor.Character.Level = 10;
        actor.Character.LaborPower = StartLabor;
        GameplayActorTestRig.SetMoney(actor, 100_000);
        MoveToSaleZone(actor);
        var slave = GameplayActorTestRig.SummonCargoSlave(session, actor, slaveObjId);
        PlaceInWorld(session, slave, HomePosition);
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        var vendorObjId = SpawnVendorMerchant(session);
        var traderObjId = SpawnGoldTrader(session);
        _benchObjId = benchObjId;
        return (actor, session, slave, vendorObjId, traderObjId);
    }

    private uint _benchObjId;

    /// <summary>
    /// Fills the bag to the inventory-full trigger: 44 trash rows + potion
    /// + quest item + keepsake + 2× craft material = 48/50 slots (2 free).
    /// </summary>
    private static void FillBag(GameplayActor actor)
    {
        for (var i = 0; i < TrashRowCount; i++)
            GameplayActorTestRig.GrantItem(actor, TrashTemplateBase + (uint)i, 1);
        GameplayActorTestRig.GrantItem(actor, PotionTemplateId, 3);
        GameplayActorTestRig.GrantItem(actor, QuestItemTemplateId, 1);
        GameplayActorTestRig.GrantItem(actor, KeepsakeTemplateId, 1);
        GameplayActorTestRig.GrantItem(actor, PackMaterialItemId, 2);
    }

    private HaulerVendorMultipackCycle.HaulerVendorMultipackOptions Options(
        string cycle, uint slaveObjId, uint vendorObjId, uint traderObjId)
        => new()
        {
            CycleId = cycle,
            SlaveObjId = slaveObjId,
            VendorMerchantObjId = vendorObjId,
            GoldTraderObjId = traderObjId,
            PackCraftId = PackCraftId,
            PackMaterialItemId = PackMaterialItemId,
            PackMaterialAmount = 1,
            PackCount = 2,
            BenchObjId = _benchObjId,
            TraderDestination = TraderPosition,
            Home = HomePosition,
            Speed = 10f,
            CraftTimeout = TimeSpan.FromSeconds(10),
            DriveTimeout = TimeSpan.FromSeconds(30),
            PumpBudget = TimeSpan.FromSeconds(30)
        };

    private static long MailCopper(GameplayActor actor)
        => MailManager.Instance.AllPlayerMails.Values
            .Where(m => m.Header.ReceiverId == actor.Character.Id)
            .Sum(m => m.Body.CopperCoins);

    private static void MoveToSaleZone(GameplayActor actor)
    {
        actor.Character.Transform.ZoneId = SaleZoneKey;
        var names = (Dictionary<uint, string>)typeof(NameManager)
            .GetField("_characterIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(NameManager.Instance)!;
        var ids = (Dictionary<string, uint>)typeof(NameManager)
            .GetField("_characterNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(NameManager.Instance)!;
        // MailManager.Send's receiver verification (name AND id must match)
        // gates the payout mails (the slice-3 convention).
        names[actor.Character.Id] = actor.Character.Name;
        ids[actor.Character.Name] = actor.Character.Id;
    }

    private static uint SpawnVendorMerchant(HeadlessSession session)
    {
        // General merchant at the home base (the farm vendor — the M3aM4 rig
        // convention). Template.Merchant arms the actor.Sell gate; no goods
        // pack needed (Sell only needs the merchant flag).
        var objId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: VendorMerchantTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, objId, HomePosition);
        return objId;
    }

    private static uint SpawnGoldTrader(HeadlessSession session)
    {
        // Slice-3 PlaceTrader shape: specialty_coin_id 0 = gold trader,
        // 1 m from the wagon's destination (the engine's 2.5 m sale range).
        var objId = session.SpawnNpc(GoldTraderTemplateId);
        var npc = session.World.GetNpc(objId);
        if (npc != null)
        {
            npc.Template = new NpcTemplate { Id = GoldTraderTemplateId, SpecialtyCoinId = 0 };
            npc.Transform.ZoneId = SaleZoneKey;
            npc.Transform.Local.SetPosition(TraderPosition + new Vector3(1f, 0f, 0f));
        }
        return objId;
    }

    private void RegisterWorld(HeadlessSession session)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(WorldInstance).GetField("<Id>k__BackingField", Flags)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", Flags)!
            .GetValue(WorldManager.Instance)!;
        if (!worlds.TryAdd(session.World.Id, session.World) && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held by a foreign world.");
        _registeredWorld = session.World;
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>
    /// Seeds the vendor item surface: 44 gray trash templates (sellable,
    /// refund 10+i, MaxCount 1) plus the three protected fixtures. Always
    /// sets the trade fields (the SeedTradeItemTemplate idempotence rule —
    /// a sibling's bare seed must never shadow these values).
    /// </summary>
    private static void SeedVendorTemplates()
    {
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        for (var i = 0; i < TrashRowCount; i++)
        {
            var id = TrashTemplateBase + (uint)i;
            if (!templates.TryGetValue(id, out var trash))
            {
                trash = new ItemTemplate { Id = id, MaxCount = 1, FixedGrade = 0 };
                templates[id] = trash;
            }
            trash.Sellable = true;
            trash.Refund = 10 + i;
            trash.CategoryId = (int)ItemCategory.None;
        }
        SeedProtected(PotionTemplateId, (int)ItemCategory.Potion, sellable: true, refund: 100, maxCount: 10, templates);
        SeedProtected(QuestItemTemplateId, (int)ItemCategory.Quest_Item, sellable: true, refund: 200, maxCount: 1, templates);
        SeedProtected(KeepsakeTemplateId, (int)ItemCategory.None, sellable: false, refund: 500, maxCount: 1, templates);

        static void SeedProtected(uint id, int category, bool sellable, int refund, int maxCount,
            Dictionary<uint, ItemTemplate> templates)
        {
            if (!templates.TryGetValue(id, out var template))
            {
                template = new ItemTemplate { Id = id, MaxCount = maxCount, FixedGrade = 0 };
                templates[id] = template;
            }
            template.Sellable = sellable;
            template.Refund = refund;
            template.CategoryId = category;
        }
    }

    /// <summary>
    /// Seeds the fixture sale surfaces (the slice-3 SeedSaleSurfaces shape):
    /// specialty bundle row for the fixture pack, fresh max-ratio manager,
    /// sale-zone wiring, Coins template, cargo-doodad recover phase (the
    /// carried-load UNLOADs run the real PackPickup), name/mail/character
    /// managers. Previous singleton instances are saved for TearDown.
    /// </summary>
    private static void SeedSaleSurfaces()
    {
        _previousMinLevel = AppConfiguration.Instance.Specialty.MinLevelToCraftSell;
        AppConfiguration.Instance.Specialty.MinLevelToCraftSell = 10; // canonical tooltip gate

        // Incrementing item ids (the M3a-3 trap): pack crafting, the cargo
        // doodad links and RecoverItem all resolve pack instances BY id.
        var idField = typeof(ItemManager).GetField("<itemIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? typeof(ItemManager).GetField("itemIdManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (idField != null)
        {
            _previousItemIdManager ??= idField.GetValue(ItemManager.Instance);
            var existingItems = (System.Collections.Concurrent.ConcurrentDictionary<ulong, Item>)typeof(ItemManager)
                .GetField("_allItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(ItemManager.Instance)!;
            var nextId = 0x01000000u; // ItemIdManager.FirstId — real engine range
            foreach (var existingId in existingItems.Keys)
                if (existingId is > 0 and <= uint.MaxValue && existingId >= nextId)
                    nextId = (uint)existingId + 1;
            var mock = Mock.Of<IItemIdManager>();
            mock.GetNextId().Returns(() => nextId++);
            idField.SetValue(ItemManager.Instance, mock.Object);
        }

        // Fixture pack trade fields: no refund (base == profit exactly); the
        // origin zone group arms the canonical same-zone exclusion (26 ≠ 5).
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        var packTemplate = templates[GameplayActorTestRig.CargoPackTemplateId];
        packTemplate.Refund = 0;
        packTemplate.SpecialtyZoneId = PackOriginGroup;

        // Coins template (MailForSpeciality.FinalizeForSeller early-returns
        // without it — the seller mails would never verify their receiver).
        templates.TryAdd(Item.Coins, new ItemTemplate
        {
            Id = Item.Coins,
            Name = "Coins",
            MaxCount = 1,
            FixedGrade = 0,
            Gradable = false
        });

        // The cargo placed-pack doodad gets a START phase carrying the generic
        // recover skill — after PackVehicleService's InitDoodad the loaded
        // cargo doodads are recoverable, which is what the UNLOAD legs' real
        // PackPickups (RecoverItem / 11361) require. Missing-only per surface.
        SeedDoodadIdManager();
        var funcGroupId = GameplayActorTestRig.CargoPackDoodadTemplateId + 10;
        var funcId = GameplayActorTestRig.CargoPackDoodadTemplateId + 20;
        GameplayActorTestRig.SeedRecoverablePackDoodad(funcGroupId, funcId);
        var doodadTemplates = (Dictionary<uint, DoodadTemplate>)typeof(DoodadManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(DoodadManager.Instance)!;
        if (!doodadTemplates.TryGetValue(GameplayActorTestRig.CargoPackDoodadTemplateId, out var cargoTemplate))
        {
            cargoTemplate = new DoodadTemplate { Id = GameplayActorTestRig.CargoPackDoodadTemplateId };
            doodadTemplates[GameplayActorTestRig.CargoPackDoodadTemplateId] = cargoTemplate;
        }
        var allFuncGroups = (Dictionary<uint, DoodadFuncGroups>)typeof(DoodadManager)
            .GetField("_allFuncGroups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(DoodadManager.Instance)!;
        var startGroup = new DoodadFuncGroups { Id = funcGroupId, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start };
        if (allFuncGroups == null)
        {
            allFuncGroups = [];
            SetField(DoodadManager.Instance, "_allFuncGroups", allFuncGroups);
        }
        allFuncGroups.TryAdd(funcGroupId, startGroup);
        cargoTemplate.FuncGroups ??= [];
        if (cargoTemplate.FuncGroups.All(g => g.Id != funcGroupId))
            cargoTemplate.FuncGroups.Add(startGroup);

        // Zone surface: sale zone key → group 5 (≠ pack origin 26).
        _previousZoneManager ??= GetSingleton(typeof(Singleton<ZoneManager>));
        SetSingleton(typeof(Singleton<ZoneManager>), new ZoneManager(Mock.Of<IWorldManager>().Object));
        SetField(ZoneManager.Instance, "_zoneIdToKey", new Dictionary<uint, uint>());
        SetField(ZoneManager.Instance, "_conflicts", new Dictionary<ushort, ZoneConflict>());
        SetField(ZoneManager.Instance, "_groupBannedTags", new Dictionary<uint, ZoneGroupBannedTag>());
        SetField(ZoneManager.Instance, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
        SetField(ZoneManager.Instance, "_zones", new Dictionary<uint, Zone>
        {
            [SaleZoneKey] = new() { Id = 1, ZoneKey = SaleZoneKey, GroupId = SaleZoneGroup }
        });
        SetField(ZoneManager.Instance, "_groups", new Dictionary<uint, ZoneGroup>
        {
            [SaleZoneGroup] = new() { Id = SaleZoneGroup },
            [PackOriginGroup] = new() { Id = PackOriginGroup }
        });

        // Specialty surface: one gold trader + one bundle row for the fixture
        // pack (the exact seed shape of SpecialtyManagerTests).
        _previousSpecialtyManager ??= GetSingleton(typeof(Singleton<SpecialtyManager>));
        var specialtyManager = new SpecialtyManager();
        SetField(specialtyManager, "_specialties", new Dictionary<uint, Specialty>());
        SetField(specialtyManager, "_specialtyBundleItems", new Dictionary<uint, SpecialtyBundleItem>());
        SetField(specialtyManager, "_specialtyNpc", new Dictionary<uint, SpecialtyNpc>
        {
            [GoldTraderTemplateId] = new()
            {
                Id = 1, Name = "test-gold-trader",
                NpcId = GoldTraderTemplateId,
                SpecialtyBundleId = SaleBundleId
            }
        });
        SetField(specialtyManager, "_specialtyBundleItemsMapped",
            new Dictionary<uint, Dictionary<uint, SpecialtyBundleItem>>
            {
                [GameplayActorTestRig.CargoPackTemplateId] = new()
                {
                    [SaleBundleId] = new SpecialtyBundleItem
                    {
                        Id = 1,
                        ItemId = GameplayActorTestRig.CargoPackTemplateId,
                        SpecialtyBundleId = SaleBundleId,
                        Profit = BundleProfit,
                        Ratio = BundleRatioStatic,
                        Item = packTemplate
                    }
                }
            });
        SetField(specialtyManager, "_priceRatios", new Dictionary<uint, Dictionary<uint, double>>());
        SetField(specialtyManager, "_soldPackAmountInTick", new Dictionary<uint, Dictionary<uint, int>>());
        SetSingleton(typeof(Singleton<SpecialtyManager>), specialtyManager);

        // Name/mail/character surfaces (ORDER MATTERS: the mail manager holds
        // a direct reference to the seeded NameManager instance).
        _previousNameManager ??= GetSingleton(typeof(Singleton<NameManager>));
        var nameManager = new NameManager();
        SetField(nameManager, "_characterIds", new Dictionary<uint, string>());
        SetField(nameManager, "_characterNames", new Dictionary<string, uint>());
        SetField(nameManager, "_characterAccounts", new Dictionary<uint, uint>());
        SetSingleton(typeof(Singleton<NameManager>), nameManager);

        _previousMailManager ??= GetSingleton(typeof(Singleton<MailManager>));
        var mailIdMock = Mock.Of<IMailIdManager>();
        var nextMailId = 1u;
        mailIdMock.GetNextId().Returns(() => nextMailId++);
        var mailManager = new MailManager(
            mailIdMock.Object,
            NameManager.Instance,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        SetField(mailManager, "_allPlayerMails", new Dictionary<long, BaseMail>());
        SetField(mailManager, "_deletedMailIds", new List<long>());
        SetSingleton(typeof(Singleton<MailManager>), mailManager);

        _previousCharacterManager ??= GetSingleton(typeof(Singleton<CharacterManager>));
        var characterManager = new CharacterManager(
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IAccountManager>().Object,
            NameManager.Instance,
            Mock.Of<ICharacterIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IHousingManager>().Object,
            Mock.Of<IFamilyManager>().Object,
            MailManager.Instance,
            Mock.Of<ITaskManager>().Object);
        SetField(characterManager, "_expertLimits", new Dictionary<int, ExpertLimit>
        {
            [0] = new() { UpLimit = int.MaxValue }
        });
        SetSingleton(typeof(Singleton<CharacterManager>), characterManager);
    }

    /// <summary>
    /// Seeds the fixture pack recipe: 1 × potato → 1 × fixture cargo pack at
    /// the rig bench (missing-only, the slice-1/3 SeedFixturePackRecipe
    /// shape, own ids).
    /// </summary>
    private static void SeedFixturePackRecipe()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(PackCraftId))
        {
            crafts[PackCraftId] = new Craft
            {
                Id = PackCraftId,
                SkillId = PackCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = PackMaterialItemId, Amount = 1 }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = GameplayActorTestRig.CargoPackTemplateId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(PackCraftSkillId))
        {
            skills[PackCraftSkillId] = new SkillTemplate
            {
                Id = PackCraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = PackCraftLaborCost,
                ActabilityGroupId = 0,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
        }
    }

    /// <summary>
    /// Doodad.Save() allocates the row id via DoodadIdManager BEFORE the
    /// MySQL write; the rig points MySQL at a dead port so the write fails
    /// fast and deterministically, and the id manager must be initialized to
    /// reach it (missing-only).
    /// </summary>
    private static void SeedDoodadIdManager()
    {
        var freeIdsField = typeof(AAEmu.Game.Utils.IdManager).GetField("_freeIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (freeIdsField?.GetValue(AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance) == null)
            AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance.Initialize(false);
        // The shared object-id mock returns a constant (0x200000) — every
        // cargo doodad would share an ObjId and the second UNLOAD could not
        // resolve its doodad (the M4Exit EnsureIncrementingDoodadIds lesson).
        var doodadManager = DoodadManager.Instance;
        var objIdField = typeof(DoodadManager).GetField("<objectIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? typeof(DoodadManager).GetField("objectIdManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var current = (IObjectIdManager)objIdField?.GetValue(doodadManager)!;
        if (current == null || current.GetNextId() == 0x200000)
            objIdField?.SetValue(doodadManager, new FakeObjectIdManager(0x310000));
    }

    /// <summary>
    /// Null-guards the SkillManager/BuffGameData/ItemGameData dictionaries
    /// the equip/load surface reads (missing-only, never replaces populated
    /// registries).
    /// </summary>
    private static void SeedEquipSurface()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var skillManager = SkillManager.Instance;
        foreach (var field in typeof(SkillManager).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(skillManager) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(skillManager, Activator.CreateInstance(dictType));
            }
        }

        var buffGameData = BuffGameData.Instance;
        foreach (var field in typeof(BuffGameData).GetFields(flags).Where(f => f.FieldType.IsGenericType
                     && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            if (field.GetValue(buffGameData) == null)
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(
                    field.FieldType.GetGenericArguments()[0], field.FieldType.GetGenericArguments()[1]);
                field.SetValue(buffGameData, Activator.CreateInstance(dictType));
            }
        }

        var itemGameData = ItemGameData.Instance;
        if (typeof(ItemGameData).GetField("_itemGradeBuffs", flags)?.GetValue(itemGameData) == null)
            typeof(ItemGameData).GetField("_itemGradeBuffs", flags)!.SetValue(itemGameData, new Dictionary<uint, Dictionary<byte, uint>>());
    }

    /// <summary>
    /// Seeds SusManager/ModelManager headless (the DriveVehicle-tests
    /// pattern). Restored in TearDown so sibling suites never observe the
    /// swap.
    /// </summary>
    private static void SeedMovementSingletons()
    {
        _previousSusManager = typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new SusManager(WorldManager.Instance));

        _previousModelManager = typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        var modelManager = new ModelManager();
        typeof(ModelManager)
            .GetField("_modelTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<uint, ModelType>());
        typeof(ModelManager)
            .GetField("_models", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>>());
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, modelManager);
    }

    /// <summary>Test session sink — the same pattern the slave-lifecycle and
    /// housing tests use to capture outbound packets.</summary>
    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null!;
        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null!;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    /// <summary>Attaches a real GameConnection with a capture sink to the actor.</summary>
    private static void AttachCapture(GameplayActor actor)
    {
        var capture = new PacketCaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
    }

    /// <summary>Places the slave in the world AND its region grid so the
    /// vehicle's movement broadcast reaches the rider.</summary>
    private static void PlaceInWorld(HeadlessSession session, BaseUnit unit, Vector3 position)
    {
        if (unit.ParentWorld == null)
            typeof(AAEmu.Game.Models.Game.World.GameObject)
                .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(unit, session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(unit.Transform, session.World.Id);
        unit.Transform.Local.SetPosition(position);
        WorldManager.Instance.AddVisibleObject(unit);
    }

    private static object? GetSingleton(Type singletonBase)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);

    private static void SetSingleton(Type singletonBase, object? instance)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, instance);

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    /// <summary>
    /// Headless vendor pump: craft steps apply the REAL CraftEffect once the
    /// engine queue is active (the slice-1/3 HaulCraftPump shape); drives
    /// tick in 1s steps (the slice-2/3 pump shape).
    /// </summary>
    private sealed class HaulVendorPump : IHaulVendorPump
    {
        public ActorRequest DriveCraft(GameplayActor actor, ActorRequest request, uint benchObjId, uint skillId, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            var applied = false;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                actor.Tick(TimeSpan.FromMilliseconds(20));
                if (!applied && actor.Character.Craft is { IsCraftQueueActive: true })
                {
                    var bench = actor.Character.ParentWorld?.GetDoodad(benchObjId);
                    var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
                    effect.Apply(actor.Character, null, bench, null,
                        new CastSkill(skillId, 0), new EffectSource(), null, DateTime.UtcNow);
                    applied = true;
                }
            }

            return request;
        }

        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan pollBudget)
        {
            var deadline = Environment.TickCount64 + (long)pollBudget.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
                actor.Tick(TimeSpan.FromSeconds(1));
            return request;
        }
    }
}
