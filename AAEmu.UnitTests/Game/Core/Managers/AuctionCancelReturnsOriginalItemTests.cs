using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Auction.Templates;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Procs;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Headless rigs for AuctionManager.CancelAuctionLot — the G-cancel
/// instance-faithfulness pin.
///
/// Regression pin (auction-domain.md G3): the cancel path used to
/// itemManager.Create(template,count,grade) and mail a FRESH copy, losing the
/// listed item instance's enchant/durability/details and orphaning the
/// original lot.Item row in SlotType.Auction. It must return the ORIGINAL
/// listed instance (same Id, durability/enchant preserved, moved to the
/// cancel mail's SlotType.Mail) — mirroring the expiry path
/// (RemoveAuctionLotFail → FinalizeForFail), and must still be refused while
/// a bidder exists.
/// </summary>
[NotInParallel]
public class AuctionCancelReturnsOriginalItemTests
{
    private const uint SellerId = 1;
    private const string SellerName = "seller";
    private const ulong ItemId = 424242;
    private const uint TemplateId = 88_103;
    private const ulong LotId = 7001;

    private MailManager _mailManager;
    private NameManager _nameManager;
    private ServiceProvider _services;

    /// <summary>Item-memory stub: GetItemByItemId returns the configured
    /// instance (mirrors the expiry rig's StubItemManager).</summary>
    private sealed class StubItemManager(Item item) : IItemManager
    {
        public event EventHandler? OnItemsLoaded;
        public ItemTemplate GetTemplate(uint id) => item.Template;
        public EquipItemSet GetEquippedItemSet(uint id) => throw new NotSupportedException();
        public GradeTemplate GetGradeTemplate(int grade) => throw new NotSupportedException();
        public Holdable GetHoldable(uint id) => throw new NotSupportedException();
        public EquipSlotEnchantingCost GetEquipSlotEnchantingCost(uint slotTypeId) => throw new NotSupportedException();
        public GradeTemplate GetGradeTemplateByOrder(int gradeOrder) => throw new NotSupportedException();
        public ItemGradeEnchantingSupport GetItemGradEnchantingSupportByItemId(uint itemId) => throw new NotSupportedException();
        public List<LootPackDroppingNpc> GetLootPackIdByNpcId(uint npcId) => throw new NotSupportedException();
        public List<ItemTemplate> GetAllItems() => [];
        public List<Item> GetLootConvertFish(uint templateId) => throw new NotSupportedException();
        public GradeDistributions GetGradeDistributions(byte id) => throw new NotSupportedException();
        public uint GetSocketChance(uint numSockets) => 0;
        public ItemCapScale GetItemCapScale(uint skillId) => throw new NotSupportedException();
        public float GetDurabilityRepairCostFactor() => 0;
        public float GetDurabilityConst() => 0;
        public float GetHoldableDurabilityConst() => 0;
        public float GetWearableDurabilityConst() => 0;
        public float GetItemStatConst() => 0;
        public float GetHoldableStatConst() => 0;
        public float GetWearableStatConst() => 0;
        public float GetStatValueConst() => 0;
        public AttributeModifiers GetAttributeModifiers(uint id) => throw new NotSupportedException();
        public List<uint> GetItemIdsFromDoodad(uint doodadId) => [];
        public uint GetDoodadIdFromItem(uint itemTemplateId) => 0;
        public ItemTemplate GetItemTemplateFromItemId(uint itemId) => throw new NotSupportedException();
        public List<ItemTemplate> GetItemTemplatesForAuctionSearch(AuctionSearch searchTemplate) => throw new NotSupportedException();
        public ItemProcTemplate GetItemProcTemplate(uint templateId) => throw new NotSupportedException();
        public List<uint> GetItemProcBindings(uint itemId) => [];
        public List<BonusTemplate> GetUnitModifiers(uint itemId) => throw new NotSupportedException();
        public ArmorGradeBuff GetArmorGradeBuff(ArmorType type, ItemGrade grade) => throw new NotSupportedException();
        public Item Create(uint templateId, int count, byte grade, bool generateId = true) => throw new NotSupportedException();
        public bool AddItem(Item item) => false;
        public Item GetItemByItemId(ulong itemId) => itemId == item.Id ? item : null;
        public ItemContainer GetItemContainerForCharacter(uint characterId, SlotType slotType, Unit parentUnit, uint mateId) => throw new NotSupportedException();
        public CofferContainer NewCofferContainer(uint characterId) => throw new NotSupportedException();
        public ItemContainer GetItemContainerByDbId(ulong dbId) => throw new NotSupportedException();
        public void ReleaseId(ulong itemId) { }
        public void LoadUserItems() { }
        public List<Item> LoadPlayerInventory(ICharacter character) => [];
        public void Load() { }
        public void PostLoad() { }
        public SlotType GetContainerSlotTypeByContainerId(ulong dbId) => throw new NotSupportedException();
        public (int, int, int) Save(MySql.Data.MySqlClient.MySqlConnection connection, MySql.Data.MySqlClient.MySqlTransaction transaction) => (0, 0, 0);
        public ItemSet GetItemSet(uint itemSetId) => throw new NotSupportedException();
        public bool UnwrapItem(Character character, SlotType slotType, byte slot, ulong itemId) => false;
        public void UpdateItemTimers() { }
        public bool IsAutoEquipTradePack(uint itemTemplateId) => false;
        public bool DeleteItemContainer(ItemContainer container) => false;
    }

    [Before(Test)]
    public void Setup()
    {
        _nameManager = new NameManager();
        _nameManager.Load([], [], []);
        _nameManager.AddCharacter(SellerId, SellerName, 1);

        var mailIdManager = new MailIdManager();
        mailIdManager.Initialize();

        _mailManager = new MailManager(
            mailIdManager,
            _nameManager,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        _mailManager._allPlayerMails = [];

        // Reset singleton caches so Instance resolves via ServiceProvider.
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        _services = new ServiceCollection()
            .AddSingleton(_mailManager)
            .AddSingleton(_nameManager)
            .BuildServiceProvider();
        SingletonContainer.ServiceProvider = _services;
    }

    [After(Test)]
    public void Teardown()
    {
        _mailManager._allPlayerMails = null;
        _mailManager = null;
        _nameManager = null;
        _services?.Dispose();
        SingletonContainer.ServiceProvider = null;
        typeof(Singleton<MailManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Singleton<NameManager>)
            .GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static AuctionManager CreateManager(Item itemMemory, ulong lotId, bool hasBidder)
        => new(
            new StubItemManager(itemMemory),
            Mock.Of<INameManager>().Object,
            Mock.Of<AAEmu.Game.Core.Managers.Id.IAuctionIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object);

    private static AuctionLot Lot(Item item, bool hasBidder)
        => new()
        {
            Id = LotId,
            Duration = AuctionDuration.AuctionDuration6Hours,
            Item = item,
            EndTime = DateTime.UtcNow.AddHours(6),
            PostDate = DateTime.UtcNow.AddHours(-1),
            ClientId = SellerId,
            ClientName = SellerName,
            StartMoney = 100,
            DirectMoney = 1000,
            BidderId = hasBidder ? 2u : 0u,
            BidderName = hasBidder ? "bidder" : "",
            BidMoney = hasBidder ? 200 : 0
        };

    private static EquipItem EnchantedDurableItem()
    {
        var template = new EquipItemTemplate { Id = TemplateId };
        var item = new EquipItem(ItemId, template, 1)
        {
            Grade = 5,          // enchant grade distinct from default 0
            Durability = 42,    // worn durability distinct from full
            TemperPhysical = 3,
            TemperMagical = 7
        };
        item.SlotType = SlotType.Auction; // listed item sits in the auction container
        return item;
    }

    [Test]
    public async Task CancelAuctionLot_ReturnsOriginalItemInstance_EnchantAndDurabilityPreserved()
    {
        var item = EnchantedDurableItem();
        var manager = CreateManager(item, LotId, hasBidder: false);
        var lot = Lot(item, hasBidder: false);
        manager.AuctionLots.TryAdd(lot.Id, lot);

        var player = new CharacterMock { Id = SellerId };
        manager.CancelAuctionLot(player, LotId);

        // Lot is removed from the house.
        await Assert.That(manager.AuctionLots.ContainsKey(LotId)).IsFalse();

        // A single AucOffCancel mail was delivered to the owner carrying the SAME
        // instance (Id, enchant-grade, durability all preserved).
        var mails = _mailManager.AllPlayerMails.Values
            .Where(m => m.Header.ReceiverId == SellerId)
            .ToList();
        await Assert.That(mails.Count).IsEqualTo(1);
        await Assert.That(mails[0].MailType).IsEqualTo(MailType.AucOffCancel);

        var attachment = mails[0].Body.Attachments.Single();
        await Assert.That(attachment.Id).IsEqualTo(ItemId);
        await Assert.That(attachment).IsSameReferenceAs(item)
            .Because("cancel must mail the ORIGINAL listed instance, not a template copy");
        var equip = (EquipItem)attachment;
        await Assert.That(equip.Grade).IsEqualTo((byte)5);
        await Assert.That(equip.Durability).IsEqualTo((byte)42);
        await Assert.That(equip.TemperPhysical).IsEqualTo((ushort)3);
        await Assert.That(equip.TemperMagical).IsEqualTo((ushort)7);

        // The instance moved out of SlotType.Auction into the mail (no orphan).
        await Assert.That(item.SlotType).IsEqualTo(SlotType.Mail);
        await Assert.That(MailManager.Instance.AllPlayerMails.Values
            .SelectMany(m => m.Body.Attachments)
            .Any(a => a.Id == ItemId && a.SlotType == SlotType.Mail)).IsTrue();
    }

    [Test]
    public async Task CancelAuctionLot_NoOrphanRemainsInAuctionContainer()
    {
        var item = EnchantedDurableItem();
        var manager = CreateManager(item, LotId, hasBidder: false);

        // Item is currently "in" the auction container before cancel.
        await Assert.That(item.SlotType).IsEqualTo(SlotType.Auction);

        var lot = Lot(item, hasBidder: false);
        manager.AuctionLots.TryAdd(lot.Id, lot);
        var player = new CharacterMock { Id = SellerId };

        manager.CancelAuctionLot(player, LotId);

        // After cancel no item is left addressed at SlotType.Auction.
        await Assert.That(MailManager.Instance.AllPlayerMails.Values
            .SelectMany(m => m.Body.Attachments)
            .Any(a => a.Id == ItemId && a.SlotType == SlotType.Auction)).IsFalse();
        await Assert.That(item.SlotType).IsEqualTo(SlotType.Mail);
    }

    [Test]
    public async Task CancelAuctionLot_RefusedWhenBiddersExist()
    {
        var item = EnchantedDurableItem();
        var manager = CreateManager(item, LotId, hasBidder: true);
        var lot = Lot(item, hasBidder: true);
        manager.AuctionLots.TryAdd(lot.Id, lot);

        var player = new CharacterMock { Id = SellerId };
        manager.CancelAuctionLot(player, LotId);

        // Lot stays listed, item untouched in the auction container, and NO
        // cancel mail is produced.
        await Assert.That(manager.AuctionLots.ContainsKey(LotId)).IsTrue();
        await Assert.That(item.SlotType).IsEqualTo(SlotType.Auction);
        await Assert.That(MailManager.Instance.AllPlayerMails.Values
            .SelectMany(m => m.Body.Attachments)
            .Any(a => a.Id == ItemId)).IsFalse();
    }
}
