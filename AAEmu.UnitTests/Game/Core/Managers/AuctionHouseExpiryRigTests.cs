using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Auction.Templates;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Procs;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.GameData.Framework;
using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Headless rigs for the auction EXPIRY sweep (UpdateAuctionHouse — the
/// recurring 5s AuctionHouseTask body).
///
/// Regression pin: a single bad lot used to throw out of the sweep into
/// Task.Run where the exception was silently swallowed — permanently
/// stalling expiry processing (the E2E restart pin's "never removed expired
/// lot" failure). The sweep must be per-lot isolated and null-safe on lots
/// whose Item memory went missing across a reboot.
/// </summary>
[NotInParallel]
public class AuctionHouseExpiryRigTests
{
    /// <summary>Item-memory stub: GetItemByItemId returns the configured
    /// item or null (the post-reboot missing-item shape).</summary>
    private sealed class StubItemManager(Item? item) : IItemManager
    {
        public event EventHandler? OnItemsLoaded;
        public ItemTemplate GetTemplate(uint id) => throw new NotSupportedException();
        public EquipItemSet GetEquippedItemSet(uint id) => throw new NotSupportedException();
        public GradeTemplate GetGradeTemplate(int grade) => throw new NotSupportedException();
        public Holdable GetHoldable(uint id) => throw new NotSupportedException();
        public EquipSlotEnchantingCost GetEquipSlotEnchantingCost(uint slotTypeId) => throw new NotSupportedException();
        public GradeTemplate GetGradeTemplateByOrder(int gradeOrder) => throw new NotSupportedException();
        public ItemGradeEnchantingSupport GetItemGradEnchantingSupportByItemId(uint itemId) => throw new NotSupportedException();
        public List<LootPackDroppingNpc> GetLootPackIdByNpcId(uint npcId) => [];
        public List<ItemTemplate> GetAllItems() => [];
        public List<Item> GetLootConvertFish(uint templateId) => [];
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
        public List<ItemTemplate> GetItemTemplatesForAuctionSearch(AuctionSearch searchTemplate) => [];
        public ItemProcTemplate GetItemProcTemplate(uint templateId) => throw new NotSupportedException();
        public List<uint> GetItemProcBindings(uint itemId) => [];
        public List<BonusTemplate> GetUnitModifiers(uint itemId) => [];
        public ArmorGradeBuff GetArmorGradeBuff(ArmorType type, ItemGrade grade) => throw new NotSupportedException();
        public Item Create(uint templateId, int count, byte grade, bool generateId = true) => throw new NotSupportedException();
        public bool AddItem(Item item) => false;
        public Item GetItemByItemId(ulong itemId) => item!;
        public ItemContainer GetItemContainerForCharacter(uint characterId, SlotType slotType, Unit parentUnit, uint mateId) => throw new NotSupportedException();
        public CofferContainer NewCofferContainer(uint characterId) => throw new NotSupportedException();
        public ItemContainer GetItemContainerByDbId(ulong dbId) => throw new NotSupportedException();
        public bool DeleteItemContainer(ItemContainer container) => false;
        public void LoadUserItems() { }
        public void ReleaseId(ulong itemId) { }
        public List<Item> LoadPlayerInventory(ICharacter character) => [];
        public bool IsAutoEquipTradePack(uint itemTemplateId) => false;
        public void UpdateItemTimers() { }
        public bool UnwrapItem(Character character, SlotType slotType, byte slot, ulong itemId) => false;
        public ItemSet GetItemSet(uint itemSetId) => throw new NotSupportedException();
        public SlotType GetContainerSlotTypeByContainerId(ulong dbId) => throw new NotSupportedException();
        public (int, int, int) Save(MySqlConnection connection, MySqlTransaction transaction) => (0, 0, 0);
        public void Load() { }
        public void PostLoad() { }
    }

    private static AuctionManager CreateManager(Item? itemMemory)
        => new(
            new StubItemManager(itemMemory),
            Mock.Of<INameManager>().Object,
            Mock.Of<AAEmu.Game.Core.Managers.Id.IAuctionIdManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object);

    private static AuctionLot ExpiredLot(ulong id, Item? item = null)
        => new()
        {
            Id = id,
            Duration = AuctionDuration.AuctionDuration6Hours,
            Item = item!,
            EndTime = DateTime.UtcNow.AddMinutes(-1),
            PostDate = DateTime.UtcNow.AddHours(-7),
            ClientId = 1,
            ClientName = "seller",
            StartMoney = 100,
            DirectMoney = 1000,
            BidderId = 0,
            BidderName = "",
            BidMoney = 0
        };

    [Test]
    public async Task UpdateAuctionHouse_ExpiredLotWithMissingItemMemory_RemovesLotWithoutThrowing()
    {
        var manager = CreateManager(itemMemory: null); // post-reboot: item not in memory
        var lot = ExpiredLot(1);
        manager.AuctionLots.TryAdd(lot.Id, lot);

        // Must NOT throw (previously: NRE on Item.Id escaped into Task.Run
        // and silently killed every subsequent sweep).
        manager.UpdateAuctionHouse();

        await Assert.That(manager.AuctionLots.ContainsKey(lot.Id))
            .IsFalse().Because("an expired lot with missing item memory is still removed from the house");
    }

    [Test]
    public async Task UpdateAuctionHouse_OneBadLotDoesNotBlockOtherExpiries()
    {
        var healthyItem = new Item { Id = 424242, TemplateId = 10000, Count = 1 };
        var manager = CreateManager(healthyItem);

        var badLot = ExpiredLot(1); // Item = null while memory HAS items → mail path throws → isolation must save the sweep
        var goodLot = ExpiredLot(2, healthyItem);
        manager.AuctionLots.TryAdd(badLot.Id, badLot);
        manager.AuctionLots.TryAdd(goodLot.Id, goodLot);

        manager.UpdateAuctionHouse();

        await Assert.That(manager.AuctionLots.ContainsKey(goodLot.Id))
            .IsFalse().Because("a failing sibling lot must not block other expiries");
    }

    [Test]
    public async Task UpdateAuctionHouse_UnexpiredLots_Untouched()
    {
        var manager = CreateManager(null);
        var liveLot = ExpiredLot(3);
        liveLot.EndTime = DateTime.UtcNow.AddHours(6);
        manager.AuctionLots.TryAdd(liveLot.Id, liveLot);

        manager.UpdateAuctionHouse();

        await Assert.That(manager.AuctionLots.ContainsKey(liveLot.Id)).IsTrue();
    }
}
