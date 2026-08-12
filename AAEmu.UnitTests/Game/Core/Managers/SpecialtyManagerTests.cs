using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.IO;
using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;
using Microsoft.Data.Sqlite;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M4-2 (t_449d0c41): trade-pack sale + reward correctness, maturation timers and
/// the canonical 1.2 gates, driven on the REAL engine paths.
///
/// Covers (per the trade-packs dossier, scorecard-explorations/mechanics/trade-packs.md):
///  - loader correctness: `vendor_exist` reads its own column (was reading `id` → all true)
///  - sale math: base = floor(profit × ratio/1000) + refund; dynamic ratio 70–130%;
///    +5% interest; coin routes ÷10000; 80/20 seller/crafter split (Feature.backpackProfitShare)
///  - mail payout: 22 h RecvDate (TradePackMailDelayInMinutes = 1320), seller/crafter mails
///  - canonical gates: level 10 to craft (CharacterCraft) and sell (SellSpecialty);
///    StoreCantSellSameZone when selling at the pack's own production zone trader
///  - maturation: placed-pack 6-day expiry (IsExpiredPlacedPack) + sweep (SweepExpiredPlacedPacks)
///  - restart behavior: a fresh SpecialtyManager starts every route ratio at max (130)
///    (documented divergence — sold counts are not persisted; card notes)
///
/// Singleton discipline follows t_4f11a519: singletons are saved and restored per test;
/// the ItemManager template surface is extended additively (crops-rig convention).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class SpecialtyManagerTests
{
    // Canonical 1.2 ids (compact.sqlite3, verified 2026-08-11 by the trade-packs dossier).
    private const uint PackTemplateId = 26488;        // 황금 평원 마취제 (origin zone group 22, refund 20000)
    private const uint CoinStabilizerTemplateId = 32103; // 안정된 흑탄 가루 (item-trader coin, 100g value)
    private const uint GoldTraderNpcId = 10664;       // 미스티 (Solzreed gold trader, bundle 10)
    private const uint SolzreedZoneKey = 142;         // zone group 5
    private const uint GoldenPlainsZoneKey = 998;     // fake key mapping to origin group 22
    private const uint SolzreedZoneGroup = 5;
    private const uint PackOriginZoneGroup = 22;
    private const int BundleIdSolzreedGold = 10;

    private static readonly uint SolzreedRatioBaseProfit = 12800;
    private static readonly uint SolzreedRatioStatic = 3785;

    // Worked math (dossier §8, data-verified):
    //   base = floor(12800 × 3.785) + 20000 = 68448
    //   @130% + 5% interest: 68448 × 1.30 = 88982.4 → +5% = 93431.52 → round = 93432
    //   80/20: seller = round(93432 × 0.80) = 74746, crafter = 18686
    //   coin trader: round(93432 / 10000) = 9
    private const int ExpectedBasePrice = 68448;
    private const int ExpectedPayoutGold = 93432;
    private const int ExpectedSellerShare = 74746;
    private const int ExpectedCrafterShare = 18686;
    private const int ExpectedCoinCount = 9;

    private object _previousSpecialtyManager;
    private object _previousWorldManager;
    private object _previousZoneManager;
    private object _previousItemManager;
    private object _previousMailManager;
    private object _previousNameManager;
    private object _previousCharacterManager;
    private double _previousMailDelay;
    private double _previousExpiryHours;
    private int _previousMinLevel;

    private GameplayActor _seller;
    private HeadlessSession _sellerSession;
    private GameplayActor _crafter;
    private List<byte[]> _sellerPackets = [];
    private readonly List<uint> _addedItemTemplates = [];

    [Before(Test)]
    public void SetUp()
    {
        // Base surface (missing-only, never replaced) — ItemManager, WorldManager,
        // SkillManager, ExperienceManager, AccountManager, TaskManager, ids.
        GameplayActorTestRig.Seed();

        _previousSpecialtyManager = GetSingletonInstance<SpecialtyManager>();
        _previousWorldManager = GetSingletonInstance<WorldManager>();
        _previousZoneManager = GetSingletonInstance<ZoneManager>();
        _previousItemManager = GetSingletonInstance<ItemManager>();
        _previousMailManager = GetSingletonInstance<MailManager>();
        _previousNameManager = GetSingletonInstance<NameManager>();
        _previousCharacterManager = GetSingletonInstance<CharacterManager>();

        var specialty = AppConfiguration.Instance.Specialty;
        _previousMailDelay = specialty.TradePackMailDelayInMinutes;
        _previousExpiryHours = specialty.PlacedPackExpiryHours;
        _previousMinLevel = specialty.MinLevelToCraftSell;
        specialty.TradePackMailDelayInMinutes = 1320; // canonical 22 h
        specialty.PlacedPackExpiryHours = 144;        // canonical 6 days
        specialty.MinLevelToCraftSell = 10;           // canonical tooltip gate
        AppConfiguration.Instance.World ??= new WorldConfig();

        SeedItemManagerSurface();
        SeedZoneManager();
        SeedSpecialtyManager();
        SeedNameManager(); // MUST precede SeedMailManager — the mail manager holds a
                           // direct reference to the seeded NameManager instance.
        SeedMailManager();
        SeedCharacterManager();
        SeedEquipSurface();

        // Two real headless actors (seller + crafter) with packet-capturing connections.
        (_seller, _sellerSession) = GameplayActorTestRig.CreateActor("m4-2-seller");
        (_crafter, _) = GameplayActorTestRig.CreateActor("m4-2-crafter");
        _seller.Character.Name = "Seller";
        var capture = new PacketCaptureSession();
        _seller.Character.Connection = new GameConnection(capture) { ActiveChar = _seller.Character };
        _sellerPackets = capture.CapturedPackets;
        RegisterWorld(_sellerSession.World);

        // ChangeLabor(-60, Commerce) resolves Actabilities[31] (SellSpecialty's labor cost).
        _seller.Character.Actability.Actabilities[(uint)ActabilityType.Commerce] =
            new Actability(new ActabilityTemplate { Id = (uint)ActabilityType.Commerce });

        _seller.Character.Level = 10;
        _seller.Character.LaborPower = 100;
        _seller.Character.Transform.Local.SetPosition(new Vector3(1000f, 1000f, 100f));

        // Register both actors in the seeded NameManager so MailManager.Send's
        // receiver verification (name AND id must match) passes for seller and
        // crafter mails alike.
        SeedNameManagerNames();
    }

    [After(Test)]
    public void TearDown()
    {
        SetSingletonInstance(typeof(Singleton<SpecialtyManager>), _previousSpecialtyManager);
        SetSingletonInstance(typeof(Singleton<WorldManager>), _previousWorldManager);
        SetSingletonInstance(typeof(Singleton<ZoneManager>), _previousZoneManager);
        SetSingletonInstance(typeof(Singleton<ItemManager>), _previousItemManager);
        SetSingletonInstance(typeof(Singleton<MailManager>), _previousMailManager);
        SetSingletonInstance(typeof(Singleton<NameManager>), _previousNameManager);
        SetSingletonInstance(typeof(Singleton<CharacterManager>), _previousCharacterManager);

        var specialty = AppConfiguration.Instance.Specialty;
        specialty.TradePackMailDelayInMinutes = _previousMailDelay;
        specialty.PlacedPackExpiryHours = _previousExpiryHours;
        specialty.MinLevelToCraftSell = _previousMinLevel;

        // Rejected-sale tests leave the pack in the (owner-id-keyed, process-shared)
        // equipment container — remove it so the next test starts with an empty
        // Backpack slot (t_449d0c41 rig lesson: shared-container pollution).
        var leftover = _seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        if (leftover != null)
            _seller.Character.Inventory.Equipment.RemoveItem(ItemTaskType.Invalid, leftover, true);

        // Drop our headless world from the shared WorldManager registry — headless
        // worlds all share instanceId 1 and leaving ours registered starves sibling
        // rigs (their TryAdd fails → doodads resolve to our Regions-less world → NRE).
        UnregisterWorld(_sellerSession.World);
    }

    // ================================================================ rig helpers

    private void SeedItemManagerSurface()
    {
        var manager = ItemManager.Instance;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(manager, "_templates") ?? [];

        if (!templates.ContainsKey(PackTemplateId))
        {
            templates[PackTemplateId] = new BackpackTemplate
            {
                Id = PackTemplateId,
                Name = "황금 평원 마취제",
                MaxCount = 1,
                Refund = 20000,
                SpecialtyZoneId = PackOriginZoneGroup,
                BackpackType = BackpackType.TradePack,
                FixedGrade = 0,
                Gradable = false
            };
            _addedItemTemplates.Add(PackTemplateId);
        }

        if (!templates.ContainsKey(CoinStabilizerTemplateId))
        {
            templates[CoinStabilizerTemplateId] = new ItemTemplate
            {
                Id = CoinStabilizerTemplateId,
                Name = "안정된 흑탄 가루",
                MaxCount = 100,
                FixedGrade = 0,
                Gradable = false
            };
            _addedItemTemplates.Add(CoinStabilizerTemplateId);
        }

        // Item.Coins (500) must exist for the gold-payout path: MailForSpeciality's
        // FinalizeForSeller early-returns when GetTemplate(Item.Coins) is null, which
        // leaves the mail's ReceiverName unset and Send() throws on a null key.
        if (!templates.ContainsKey(Item.Coins))
        {
            templates[Item.Coins] = new ItemTemplate
            {
                Id = Item.Coins,
                Name = "Coins",
                MaxCount = 1,
                FixedGrade = 0,
                Gradable = false
            };
            _addedItemTemplates.Add(Item.Coins);
        }

        // The base rig's mock IItemIdManager returns 0 for every item (M3a-3 trap):
        // swap in an incrementing source so each created pack gets a fresh id.
        var idField = typeof(ItemManager).GetField("<itemIdManager>P", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(ItemManager).GetField("itemIdManager", BindingFlags.NonPublic | BindingFlags.Instance);
        var current = (IItemIdManager)idField?.GetValue(manager);
        if (current == null || current.GetNextId() == 0)
        {
            var mock = Mock.Of<IItemIdManager>();
            var nextId = 0x01000000u;
            mock.GetNextId().Returns(() => nextId++);
            idField?.SetValue(manager, mock.Object);
        }

        if (GetField(manager, "_allItems") is not ConcurrentDictionary<ulong, Item>)
            SetField(manager, "_allItems", new ConcurrentDictionary<ulong, Item>());
        if (GetField(manager, "_allPersistentContainers") is not ConcurrentDictionary<ulong, ItemContainer>)
            SetField(manager, "_allPersistentContainers", new ConcurrentDictionary<ulong, ItemContainer>());
    }

    private void SeedZoneManager()
    {
        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        SetField(zoneManager, "_zones", new Dictionary<uint, Zone>
        {
            [SolzreedZoneKey] = new() { Id = 1, ZoneKey = SolzreedZoneKey, GroupId = SolzreedZoneGroup },
            [GoldenPlainsZoneKey] = new() { Id = 2, ZoneKey = GoldenPlainsZoneKey, GroupId = PackOriginZoneGroup }
        });
        // Unit.OnZoneChange resolves the zone GROUP on every Transform.ZoneId write.
        SetField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>
        {
            [SolzreedZoneGroup] = new() { Id = SolzreedZoneGroup },
            [PackOriginZoneGroup] = new() { Id = PackOriginZoneGroup }
        });
        SetSingletonInstance(typeof(Singleton<ZoneManager>), zoneManager);
    }

    private void SeedSpecialtyManager()
    {
        var manager = new SpecialtyManager();
        SetField(manager, "_specialties", new Dictionary<uint, Specialty>());
        SetField(manager, "_specialtyBundleItems", new Dictionary<uint, SpecialtyBundleItem>());
        SetField(manager, "_specialtyNpc", new Dictionary<uint, SpecialtyNpc>
        {
            [GoldTraderNpcId] = new() { Id = 1, Name = "미스티", NpcId = GoldTraderNpcId, SpecialtyBundleId = BundleIdSolzreedGold }
        });
        SetField(manager, "_specialtyBundleItemsMapped", new Dictionary<uint, Dictionary<uint, SpecialtyBundleItem>>
        {
            [PackTemplateId] = new()
            {
                [BundleIdSolzreedGold] = new SpecialtyBundleItem
                {
                    Id = 1,
                    ItemId = PackTemplateId,
                    SpecialtyBundleId = BundleIdSolzreedGold,
                    Profit = SolzreedRatioBaseProfit,
                    Ratio = SolzreedRatioStatic,
                    Item = ItemManager.Instance.GetTemplate(PackTemplateId)
                }
            }
        });
        SetField(manager, "_priceRatios", new Dictionary<uint, Dictionary<uint, double>>());
        SetField(manager, "_soldPackAmountInTick", new Dictionary<uint, Dictionary<uint, int>>());
        SetSingletonInstance(typeof(Singleton<SpecialtyManager>), manager);
    }

    private void SeedMailManager()
    {
        var mailIdManager = Mock.Of<IMailIdManager>();
        var nextMailId = 1u;
        mailIdManager.GetNextId().Returns(() => nextMailId++);

        // Real NameManager (seeded with both names) doubles as the mail manager's
        // receiver-verification source so seller AND crafter mails verify.
        var mailManager = new MailManager(
            mailIdManager.Object,
            NameManager.Instance,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        SetField(mailManager, "_allPlayerMails", new Dictionary<long, BaseMail>());
        SetField(mailManager, "_deletedMailIds", new List<long>());
        SetSingletonInstance(typeof(Singleton<MailManager>), mailManager);
    }

    private void SeedNameManager()
    {
        var nameManager = new NameManager();
        SetField(nameManager, "_characterIds", new Dictionary<uint, string>());
        SetField(nameManager, "_characterNames", new Dictionary<string, uint>());
        SetField(nameManager, "_characterAccounts", new Dictionary<uint, uint>());
        SetSingletonInstance(typeof(Singleton<NameManager>), nameManager);
    }

    private void SeedCharacterManager()
    {
        var manager = new CharacterManager(
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
        SetField(manager, "_expertLimits", new Dictionary<int, ExpertLimit>
        {
            [0] = new() { UpLimit = int.MaxValue }
        });
        SetSingletonInstance(typeof(Singleton<CharacterManager>), manager);
    }

    /// <summary>
    /// Equipping a pack runs the real Unit.UpdateGearBonuses path
    /// (ItemGameData.GetItemBuff + SkillManager buff lookups + QuestManager
    /// acquire events). Seed the registry surfaces so equip doesn't NRE
    /// (BotBodyPartEquipmentTests.SeedPacketSurface pattern; missing-only).
    /// </summary>
    private void SeedEquipSurface()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;

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
        if (GetField(itemGameData, "_itemGradeBuffs") == null)
            SetField(itemGameData, "_itemGradeBuffs", new Dictionary<uint, Dictionary<byte, uint>>());
    }

    /// <summary>Equips a fresh pack (made by the given crafter id) into the Backpack slot.</summary>
    private void EquipPack(uint crafterId)
    {
        var equipped = _seller.Character.Inventory.Equipment.AcquireDefaultItem(
            ItemTaskType.CraftPickupProduct, PackTemplateId, 1, -1, crafterId);
        if (!equipped)
        {
            var probe = ItemManager.Instance.GetTemplate(PackTemplateId);
            var items = GetField(ItemManager.Instance, "_allItems") as System.Collections.IDictionary;
            Console.WriteLine($"[m4-2 diag] template={probe?.GetType().Name ?? "NULL"}, allItems={items?.Count}, freeSlots={_seller.Character.Inventory.Equipment.FreeSlotCount}, size={_seller.Character.Inventory.Equipment.ContainerSize}");
            throw new InvalidOperationException("EquipPack failed — pack could not be equipped");
        }
        // The engine routes BackpackTemplate items to slot 26 (AcquireDefaultItemEx).
        if (_seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) == null)
            throw new InvalidOperationException("EquipPack failed — pack did not land in the Backpack slot");
    }

    /// <summary>Places a specialty-trader NPC 1 m in front of the seller, in the given zone.</summary>
    private void PlaceTrader(uint npcTemplateId, uint specialtyCoinId, uint zoneKey)
    {
        var npc = new Npc
        {
            ObjId = 0xC001,
            TemplateId = npcTemplateId,
            Template = new NpcTemplate { SpecialtyCoinId = specialtyCoinId },
            Hp = 100,
            MaxHp = 100
        };
        npc.Transform.ZoneId = zoneKey;
        npc.Transform.Local.SetPosition(_seller.Character.Transform.World.Position + new Vector3(1f, 0f, 0f));
        _sellerSession.World.SetNpc(npc.ObjId, npc);
    }

    private void RegisterWorld(WorldInstance world)
    {
        // Headless-session worlds never get Regions allocated (production
        // WorldManager.CreateWorldInstance does it) — Spawn → GetRegionByPos
        // would NRE. Same allocation as CropHarvestLoopRig.RegisterWorld.
        if (world.Regions == null)
        {
            world.Regions = new Region[
                world.Template.CellX * WorldManager.SECTORS_PER_CELL,
                world.Template.CellY * WorldManager.SECTORS_PER_CELL];
        }
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)GetField(WorldManager.Instance, "_worlds");
        worlds.TryAdd(world.Id, world);
    }

    private void UnregisterWorld(WorldInstance world)
    {
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)GetField(WorldManager.Instance, "_worlds");
        // Only remove OUR instance — a sibling class may own the same id-1 slot
        // (headless worlds share instanceId 1); removing theirs would break it.
        if (worlds.TryGetValue(world.Id, out var registered) && ReferenceEquals(registered, world))
            worlds.TryRemove(world.Id, out _);
    }

    /// <summary>
    /// Decodes the error-message type from a captured SCErrorMsgPacket frame.
    /// Wire layout: [len u16][0xdd][level][(hash u8)(count u8) if level==1][typeId u16][error1 i16]...
    /// (G2C packet-capture rig lesson, t_61a95041).
    /// </summary>
    private static short DecodeErrorType(byte[] frame)
    {
        if (frame.Length < 10 || frame[2] != 0xdd)
            throw new InvalidOperationException($"Frame is not a level-1 game packet (len {frame.Length}, marker {frame.ElementAtOrDefault(2):X2})");
        var level = frame[3];
        var opcodeOffset = level == 1 ? 6 : 4;
        var opcode = BitConverter.ToUInt16(frame, opcodeOffset);
        if (opcode != SCOffsets.SCErrorMsgPacket)
            throw new InvalidOperationException($"Frame opcode 0x{opcode:X4} is not SCErrorMsgPacket (0x{SCOffsets.SCErrorMsgPacket:X4})");
        return BitConverter.ToInt16(frame, opcodeOffset + 2);
    }

    private List<BaseMail> CapturedMails()
    {
        var dict = (Dictionary<long, BaseMail>)GetField(MailManager.Instance, "_allPlayerMails");
        return dict.Values.ToList();
    }

    private static object GetSingletonInstance<T>() where T : class
        => typeof(Singleton<T>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    private static void SetSingletonInstance(Type singletonBase, object instance)
    {
        singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, instance);
    }

    private static object GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        return field?.GetValue(target);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    // ================================================================ vendor_exist loader

    [Test]
    public async Task Load_VendorExist_ReadsVendorExistColumn()
    {
        // Data-verified: 2162 rows, exactly 239 with vendor_exist='t' (dossier §2.4).
        // The old loader read the `id` column (GetBoolean("id", true)) → every row true.
        var dbPath = Path.Combine(FileManager.AppPath, "Data", "compact.sqlite3");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine(
                $"[SpecialtyManager] SKIPPED — {dbPath} not found (canonical 1.2 compact.sqlite3 is gitignored; " +
                "place it at AAEmu.Game/Data/compact.sqlite3 to run this test)");
            return;
        }

        var manager = new SpecialtyManager();
        manager.Load();

        var specialties = (Dictionary<uint, Specialty>)GetField(manager, "_specialties");
        await Assert.That(specialties.Count).IsEqualTo(2162);
        var vendorExists = specialties.Values.Count(s => s.VendorExist);
        await Assert.That(vendorExists).IsEqualTo(239);
    }

    // ================================================================ sale math + payout

    [Test]
    public async Task SellSpecialty_SellerIsCrafter_GoldPayout_Interest_RecvDate()
    {
        // Arrange — pack crafted by the seller (crafterId == seller.Id → no split), sold
        // at the Solzreed gold trader (bundle 10, gold payout, zone group 5 ≠ origin 22).
        EquipPack(_seller.Character.Id);
        PlaceTrader(GoldTraderNpcId, specialtyCoinId: 0, SolzreedZoneKey);

        // Act
        var basePrice = SpecialtyManager.Instance.SellSpecialty(_seller.Character, 0xC001);

        // Assert — base price formula (floor(profit × ratio/1000) + refund)
        await Assert.That(basePrice).IsEqualTo(ExpectedBasePrice);

        // Pack consumed, labor −60, dynamic ratio defaulted to max (130) on a fresh manager
        await Assert.That(_seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNull();
        await Assert.That(_seller.Character.LaborPower).IsEqualTo((short)40);

        // Mail: gold payout = round(base × 130% × 1.05) = 93432, RecvDate = now + 22 h
        var mails = CapturedMails();
        await Assert.That(mails.Count).IsEqualTo(1);
        var mail = mails[0];
        await Assert.That(mail.Body.CopperCoins).IsEqualTo(ExpectedPayoutGold);
        await Assert.That(mail.Title).IsEqualTo("Speciality Payment"); // seller == crafter → plain title
        var expectedRecv = DateTime.UtcNow.AddMinutes(1320);
        await Assert.That(mail.Body.RecvDate).IsGreaterThan(expectedRecv.AddMinutes(-1));
        await Assert.That(mail.Body.RecvDate).IsLessThan(expectedRecv.AddMinutes(1));
        // body() template carries the pack name, rate, base and payout (Trino 1.2 format)
        await Assert.That(mail.Body.Text).Contains("93432");
    }

    [Test]
    public async Task SellSpecialty_CrafterDifferent_Splits8020_BetweenSellerAndCrafter()
    {
        // Arrange — pack crafted by a different character (MadeUnitId = crafter.Id).
        // Feature.backpackProfitShare (bit 56) is set in the default fset → 80/20 applies.
        SeedNameManagerNames();
        EquipPack(_crafter.Character.Id);
        PlaceTrader(GoldTraderNpcId, specialtyCoinId: 0, SolzreedZoneKey);

        // Act
        var basePrice = SpecialtyManager.Instance.SellSpecialty(_seller.Character, 0xC001);

        // Assert — two mails: seller 80% (74746), crafter 20% (18686)
        await Assert.That(basePrice).IsEqualTo(ExpectedBasePrice);
        var mails = CapturedMails();
        await Assert.That(mails.Count).IsEqualTo(2);

        var sellerMail = mails.First(m => m.Header.ReceiverId == _seller.Character.Id);
        var crafterMail = mails.First(m => m.Header.ReceiverId == _crafter.Character.Id);
        await Assert.That(sellerMail.Body.CopperCoins).IsEqualTo(ExpectedSellerShare);
        await Assert.That(crafterMail.Body.CopperCoins).IsEqualTo(ExpectedCrafterShare);
        await Assert.That(sellerMail.Title).IsEqualTo("Speciality Payment [Delivery]");
        await Assert.That(crafterMail.Title).IsEqualTo("Speciality Payment [Crafter]");
        await Assert.That(sellerMail.Body.CopperCoins + crafterMail.Body.CopperCoins)
            .IsEqualTo(ExpectedPayoutGold);
    }

    [Test]
    public async Task SellSpecialty_CoinTrader_PaysStabilizerItemsAt10000To1()
    {
        // Arrange — item trader (specialty_coin_id 32103): payout converted round(coins/10000).
        EquipPack(_seller.Character.Id);
        PlaceTrader(GoldTraderNpcId, CoinStabilizerTemplateId, SolzreedZoneKey);

        // Act
        var basePrice = SpecialtyManager.Instance.SellSpecialty(_seller.Character, 0xC001);

        // Assert — 93432 c → round(9.3432) = 9 stabilizers as a mail attachment
        await Assert.That(basePrice).IsEqualTo(ExpectedBasePrice);
        var mails = CapturedMails();
        await Assert.That(mails.Count).IsEqualTo(1);
        await Assert.That(mails[0].Body.CopperCoins).IsEqualTo(0); // item payout, not gold
        var attachment = mails[0].Body.Attachments.Single();
        await Assert.That(attachment.TemplateId).IsEqualTo(CoinStabilizerTemplateId);
        await Assert.That(attachment.Count).IsEqualTo(ExpectedCoinCount);
    }

    [Test]
    public async Task SellSpecialty_BelowMinLevel_RejectedLevelLowToUse()
    {
        // Arrange — level 9 (< 10): canonical "10레벨 미만은 특산품 제작/판매 불가".
        _seller.Character.Level = 9;
        EquipPack(_seller.Character.Id);
        PlaceTrader(GoldTraderNpcId, specialtyCoinId: 0, SolzreedZoneKey);

        // Act
        var result = SpecialtyManager.Instance.SellSpecialty(_seller.Character, 0xC001);

        // Assert — rejected with LevelLowToUse; nothing consumed, no mail, no labor cost
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(_sellerPackets.Any()).IsTrue();
        await Assert.That(DecodeErrorType(_sellerPackets.Last())).IsEqualTo((short)ErrorMessageType.LevelLowToUse);
        await Assert.That(_seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();
        await Assert.That(_seller.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(CapturedMails().Count).IsEqualTo(0);
    }

    [Test]
    public async Task SellSpecialty_SameZoneAsPackOrigin_StoreCantSellSameZone()
    {
        // Arrange — the trader NPC stands in the pack's OWN production zone group (22):
        // canonical "생산지 교역상에게는 판매 불가" must surface StoreCantSellSameZone (512),
        // not the generic Invalid.
        EquipPack(_seller.Character.Id);
        PlaceTrader(GoldTraderNpcId, specialtyCoinId: 0, GoldenPlainsZoneKey);

        // Act
        var result = SpecialtyManager.Instance.SellSpecialty(_seller.Character, 0xC001);

        // Assert — dedicated error, pack NOT consumed
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(DecodeErrorType(_sellerPackets.Last()))
            .IsEqualTo((short)ErrorMessageType.StoreCantSellSameZone);
        await Assert.That(_seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();
        await Assert.That(CapturedMails().Count).IsEqualTo(0);
    }

    [Test]
    public async Task SellSpecialty_NonTraderNpc_Invalid()
    {
        // Arrange — an NPC that is not a specialty trader at all.
        EquipPack(_seller.Character.Id);
        PlaceTrader(1000, specialtyCoinId: 0, SolzreedZoneKey);

        // Act
        var result = SpecialtyManager.Instance.SellSpecialty(_seller.Character, 0xC001);

        // Assert — generic Invalid for non-traders (the old code sent StoreCantSellSameZone here)
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(DecodeErrorType(_sellerPackets.Last())).IsEqualTo((short)ErrorMessageType.Invalid);
        await Assert.That(_seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();
    }

    // ================================================================ craft gate

    [Test]
    public async Task CharacterCraft_PackCraft_BelowLevel10_RejectedLevelLowToUse()
    {
        // Arrange — pack craft (product 26488 is a BackpackTemplate) at level 9.
        _seller.Character.Level = 9;
        _seller.Character.Craft = new CharacterCraft(_seller.Character);
        var packCraft = new Craft
        {
            SkillId = 0,
            CraftMaterials = [],
            CraftProducts = [new CraftProduct { ItemId = PackTemplateId, Amount = 1 }]
        };

        // Act
        _seller.Character.Craft.Craft(packCraft, 1, 0);

        // Assert — rejected before any material check; craft cancelled
        await Assert.That(DecodeErrorType(_sellerPackets.Last())).IsEqualTo((short)ErrorMessageType.LevelLowToUse);
        await Assert.That(_seller.Character.Craft.IsCrafting).IsFalse();
    }

    [Test]
    public async Task CharacterCraft_PackCraft_Level10_PassesGate()
    {
        // Arrange — level 10: the gate must NOT fire (craft proceeds into the skill cast).
        // (CreateActor already wires Skills + Actability + the TestSkillId skill.)
        _seller.Character.Level = 10;
        _seller.Character.Craft = new CharacterCraft(_seller.Character);
        var packCraft = new Craft
        {
            SkillId = GameplayActorTestRig.TestSkillId,
            CraftMaterials = [],
            CraftProducts = [new CraftProduct { ItemId = PackTemplateId, Amount = 1 }]
        };

        // Act
        _seller.Character.Craft.Craft(packCraft, 1, 0);

        // Assert — no level error; craft is in progress (not cancelled by the gate)
        await Assert.That(_sellerPackets.Any(p => IsErrorPacket(p, ErrorMessageType.LevelLowToUse))).IsFalse();
        await Assert.That(_seller.Character.Craft.IsCrafting).IsTrue();
    }

    // ================================================================ pickup (DoodadFuncRecoverItem)

    [Test]
    public async Task DoodadFuncRecoverItem_PicksUpPack_EquipsBackToBackpackSlot()
    {
        // Arrange — a placed trade-pack doodad (PutDownBackpackEffect moved the pack
        // into the System container and stamped ItemId/ItemTemplateId on the doodad).
        var pack = ItemManager.Instance.Create(PackTemplateId, 1, 0);
        pack.OwnerId = _seller.Character.Id;
        pack.SlotType = SlotType.System;
        _seller.Character.Inventory.SystemContainer.AddOrMoveExistingItem(ItemTaskType.DropBackpack, pack);

        var doodad = new Doodad
        {
            ObjId = 0xE101,
            TemplateId = 6068,
            ItemId = pack.Id,
            ItemTemplateId = PackTemplateId,
            PlantTime = DateTime.UtcNow.AddDays(-3),
            OwnerId = _seller.Character.Id,
            OwnerType = DoodadOwnerType.Character
        };

        // Act — canonical pickup: anyone can recover the pack (anti-dupe: the item must
        // still live in a System container, else the pickup is refused).
        new DoodadFuncRecoverItem().Use(_seller.Character, doodad, 0);

        // Assert — pack back on the Backpack slot, doodad cleared of item refs, phase advances
        var equipped = _seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        await Assert.That(equipped).IsNotNull();
        await Assert.That(equipped.Id).IsEqualTo(pack.Id);
        await Assert.That(doodad.ItemId).IsEqualTo(0u);
        await Assert.That(doodad.ItemTemplateId).IsEqualTo(0u);
        await Assert.That(doodad.ToNextPhase).IsTrue();
    }

    [Test]
    public async Task DoodadFuncRecoverItem_AlreadyPickedUp_RefusedWithError()
    {
        // Arrange — the item is no longer in a System container (someone else picked it
        // up first): the func must refuse and NOT advance the phase (anti-dupe).
        var pack = ItemManager.Instance.Create(PackTemplateId, 1, 0);
        pack.OwnerId = _seller.Character.Id;
        pack.SlotType = SlotType.Equipment; // not the System container anymore
        _seller.Character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.Invalid, pack, (int)EquipmentItemSlot.Backpack);

        var doodad = new Doodad
        {
            ObjId = 0xE102,
            TemplateId = 6068,
            ItemId = pack.Id,
            ItemTemplateId = PackTemplateId,
            PlantTime = DateTime.UtcNow.AddDays(-3),
            OwnerType = DoodadOwnerType.Character
        };

        // Act
        new DoodadFuncRecoverItem().Use(_seller.Character, doodad, 0);

        // Assert — refused: InteractionRecoverParent error, phase does NOT advance
        await Assert.That(doodad.ToNextPhase).IsFalse();
        await Assert.That(_sellerPackets.Any(p => IsErrorPacket(p, ErrorMessageType.InteractionRecoverParent))).IsTrue();
        // the item stays where it is
        await Assert.That(_seller.Character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)).IsNotNull();
    }

    // ================================================================ restart assertions

    [Test]
    public async Task ApplyLoadedState_RestoresPlantTime_MaturationTimerSurvivesRestart()
    {
        // Arrange — a placed trade pack whose row was persisted by PutDownBackpackEffect
        // (plant_time = 5 days ago) and is now being restored by the boot load path
        // (SpawnManager → Doodad.ApplyLoadedState, M3b seam: loads never write the row).
        var plantedAt = DateTime.UtcNow.AddDays(-5);
        var doodad = new Doodad();
        doodad.ApplyLoadedState(
            dbId: 42, phaseId: 0, plantTime: plantedAt, growthTime: DateTime.MinValue, phaseTime: DateTime.UtcNow,
            ownerId: _seller.Character.Id, ownerType: DoodadOwnerType.Character,
            attachPoint: AttachPointKind.None, itemId: 100, ownerDbId: 0, scale: 1f, data: 0,
            farmType: FarmType.Invalid);
        doodad.ItemTemplateId = PackTemplateId;

        // Act/Assert — the boot-restored clock is still the ORIGINAL one: the pack is
        // not yet expired (5d < 6d) and expires exactly at the canonical boundary.
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(doodad, DateTime.UtcNow)).IsFalse();
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(doodad, plantedAt.AddDays(6).AddSeconds(1))).IsTrue();
    }

    private bool IsErrorPacket(byte[] frame, ErrorMessageType type)
    {
        if (frame.Length < 10 || frame[2] != 0xdd)
            return false;
        var level = frame[3];
        var opcodeOffset = level == 1 ? 6 : 4;
        if (BitConverter.ToUInt16(frame, opcodeOffset) != SCOffsets.SCErrorMsgPacket)
            return false;
        return BitConverter.ToInt16(frame, opcodeOffset + 2) == (short)type;
    }

    // ================================================================ maturation (6-day expiry)

    [Test]
    public async Task IsExpiredPlacedPack_ExpiredTradePack_True()
    {
        var doodad = PlacedPack(plantTime: DateTime.UtcNow.AddHours(-145));
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(doodad, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    public async Task IsExpiredPlacedPack_FreshPack_False()
    {
        var doodad = PlacedPack(plantTime: DateTime.UtcNow.AddHours(-1));
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(doodad, DateTime.UtcNow)).IsFalse();
    }

    [Test]
    public async Task IsExpiredPlacedPack_ExactlyAtBoundary_False()
    {
        // Strictly less-than: a pack planted exactly 144 h ago is not yet expired.
        // Use ONE clock read so the boundary comparison is exact.
        var now = DateTime.UtcNow;
        var doodad = PlacedPack(plantTime: now.AddHours(-144));
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(doodad, now)).IsFalse();
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(doodad, now.AddSeconds(1))).IsTrue();
    }

    [Test]
    public async Task IsExpiredPlacedPack_NonTradePackDoodad_False()
    {
        // A recoverable doodad whose item is NOT a trade pack (e.g. a plain item) never expires.
        var doodad = PlacedPack(plantTime: DateTime.UtcNow.AddHours(-200));
        doodad.ItemTemplateId = 4045; // plain template (no BackpackTemplate in the seeded surface)
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(doodad, DateTime.UtcNow)).IsFalse();
    }

    [Test]
    public async Task IsExpiredPlacedPack_NoItemOrNoPlantTime_False()
    {
        var noItem = PlacedPack(plantTime: DateTime.UtcNow.AddHours(-200));
        noItem.ItemId = 0;
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(noItem, DateTime.UtcNow)).IsFalse();

        var noPlantTime = PlacedPack(plantTime: DateTime.MinValue);
        await Assert.That(SpecialtyManager.IsExpiredPlacedPack(noPlantTime, DateTime.UtcNow)).IsFalse();
    }

    [Test]
    public async Task SweepExpiredPlacedPacks_DespawnsExpiredOnly()
    {
        // Arrange — one expired pack, one fresh pack on the same world.
        var world = new WorldInstance(new WorldTemplate { Id = 77, Name = "pack_world" }, 0, false, 77);
        world.SpawnManager = new SpawnManager(world);
        RegisterWorld(world);

        var expired = new CountingDeleteDoodad
        {
            ObjId = 1,
            TemplateId = 6068,
            ItemId = 100,
            ItemTemplateId = PackTemplateId,
            PlantTime = DateTime.UtcNow.AddHours(-145),
            ParentWorld = world
        };
        var fresh = new CountingDeleteDoodad
        {
            ObjId = 2,
            TemplateId = 6068,
            ItemId = 101,
            ItemTemplateId = PackTemplateId,
            PlantTime = DateTime.UtcNow,
            ParentWorld = world
        };
        world.SpawnManager.AddPlayerDoodad(expired);
        world.SpawnManager.AddPlayerDoodad(fresh);

        // Act
        SpecialtyManager.Instance.SweepExpiredPlacedPacks(DateTime.UtcNow);

        // Assert — expired deleted (removed from the player-doodad list), fresh untouched
        await Assert.That(expired.DeleteCount).IsEqualTo(1);
        await Assert.That(fresh.DeleteCount).IsEqualTo(0);
        await Assert.That(world.SpawnManager.GetAllPlayerDoodads()).Contains(fresh);
        await Assert.That(world.SpawnManager.GetAllPlayerDoodads()).DoesNotContain(expired);
    }

    private Doodad PlacedPack(DateTime plantTime)
    {
        return new Doodad
        {
            ObjId = 0xE001,
            TemplateId = 6068,
            ItemId = 100,
            ItemTemplateId = PackTemplateId,
            PlantTime = plantTime
        };
    }

    /// <summary>Doodad subclass that replaces the MySQL write tail of Delete() with an
    /// in-memory record (Save() virtual-seam precedent, PhaseStateRestartRecoveryTests).</summary>
    private sealed class CountingDeleteDoodad : Doodad
    {
        public int DeleteCount { get; private set; }

        public override void Delete()
        {
            DeleteCount++;
            ParentWorld?.SpawnManager?.RemovePlayerDoodad(this);
        }
    }

    // ================================================================ ratio state

    [Test]
    public async Task FreshManager_EveryRouteRatio_DefaultsToMax130()
    {
        // Restart behavior (documented divergence, dossier §11 gap 9): sold-pack counts are
        // in-memory only, so after a restart every route ratio re-initializes to max (130).
        var ratios = (Dictionary<uint, Dictionary<uint, double>>)GetField(SpecialtyManager.Instance, "_priceRatios");
        await Assert.That(ratios.Count).IsEqualTo(0);

        var ratio = SpecialtyManager.Instance.GetRatioForSpecialty(EquippedSeller());
        await Assert.That(ratio).IsEqualTo(130);
        await Assert.That(ratios[PackTemplateId][SolzreedZoneGroup]).IsEqualTo(130);
    }

    private Character EquippedSeller()
    {
        EquipPack(_seller.Character.Id);
        _seller.Character.Transform.ZoneId = SolzreedZoneKey;
        return _seller.Character;
    }

    [Test]
    public async Task ConsumeRatio_DecaysPerSoldPack_ClampedAtMin()
    {
        // Arrange — 3 packs sold this tick for (pack, Solzreed): −ceil(3 × 0.5) = −2.
        var ratios = (Dictionary<uint, Dictionary<uint, double>>)GetField(SpecialtyManager.Instance, "_priceRatios");
        ratios[PackTemplateId] = new Dictionary<uint, double> { [SolzreedZoneGroup] = 130 };
        var sold = (Dictionary<uint, Dictionary<uint, int>>)GetField(SpecialtyManager.Instance, "_soldPackAmountInTick");
        sold[PackTemplateId] = new Dictionary<uint, int> { [SolzreedZoneGroup] = 3 };

        // Act
        SpecialtyManager.Instance.ConsumeRatio();

        // Assert — 130 − 2 = 128
        await Assert.That(ratios[PackTemplateId][SolzreedZoneGroup]).IsEqualTo(128);

        // Clamp: at the floor (70) a large batch cannot push below MinSpecialtyRatio
        sold[PackTemplateId][SolzreedZoneGroup] = 200;
        ratios[PackTemplateId][SolzreedZoneGroup] = 70;
        SpecialtyManager.Instance.ConsumeRatio();
        await Assert.That(ratios[PackTemplateId][SolzreedZoneGroup]).IsEqualTo(70);
    }

    [Test]
    public async Task RegenRatio_RegeneratesUpToMax130()
    {
        var ratios = (Dictionary<uint, Dictionary<uint, double>>)GetField(SpecialtyManager.Instance, "_priceRatios");
        ratios[PackTemplateId] = new Dictionary<uint, double> { [SolzreedZoneGroup] = 70 };
        var sold = (Dictionary<uint, Dictionary<uint, int>>)GetField(SpecialtyManager.Instance, "_soldPackAmountInTick");
        sold[PackTemplateId] = new Dictionary<uint, int> { [SolzreedZoneGroup] = 0 };

        // Act — two regen ticks (+5 each), then a burst to verify the cap
        SpecialtyManager.Instance.RegenRatio();
        await Assert.That(ratios[PackTemplateId][SolzreedZoneGroup]).IsEqualTo(75);
        SpecialtyManager.Instance.RegenRatio();
        await Assert.That(ratios[PackTemplateId][SolzreedZoneGroup]).IsEqualTo(80);
        ratios[PackTemplateId][SolzreedZoneGroup] = 129;
        SpecialtyManager.Instance.RegenRatio();
        await Assert.That(ratios[PackTemplateId][SolzreedZoneGroup]).IsEqualTo(130);
    }

    private void SeedNameManagerNames()
    {
        var ids = (Dictionary<uint, string>)GetField(NameManager.Instance, "_characterIds");
        var names = (Dictionary<string, uint>)GetField(NameManager.Instance, "_characterNames");
        ids[_seller.Character.Id] = _seller.Character.Name;
        names[_seller.Character.Name] = _seller.Character.Id;
        ids[_crafter.Character.Id] = "Crafter42";
        names["Crafter42"] = _crafter.Character.Id;
    }
}

/// <summary>Minimal ISession fake that captures every encoded packet sent to the client.</summary>
public sealed class PacketCaptureSession : ISession
{
    public List<byte[]> CapturedPackets { get; } = [];

    public IPAddress Ip => IPAddress.Loopback;
    public uint SessionId => 1;
    public Socket Socket => null;

    public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
    public void AddAttribute(string name, object attribute) { }
    public object GetAttribute(string name) => null;
    public void ClearAttribute(string name) { }
    public void Close() { }
}
