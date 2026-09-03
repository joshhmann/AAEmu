using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Structured status snapshot of the character's bag and equipment durability.
/// </summary>
public sealed record BagAuditSnapshot
{
    public int TotalCapacity { get; init; }
    public int FreeSlots { get; init; }
    public int UsedSlots { get; init; }
    public float FullnessPercent => TotalCapacity > 0 ? (float)UsedSlots / TotalCapacity : 0f;
    public bool IsNearFull => FreeSlots <= 2 || FullnessPercent >= 0.90f;
    public IReadOnlyList<Item> TrashItems { get; init; } = [];
    public IReadOnlyList<EquipItem> DamagedEquipment { get; init; } = [];
    public long TotalTrashEstimatedValue { get; init; }
}

/// <summary>
/// Autonomous bag management and equipment maintenance service for playerbots.
/// Identifies vendor junk, audits bag capacity, and drives vendoring and blacksmith repairs.
/// </summary>
public static class BotBagManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static readonly HashSet<int> TrashCategoryIds =
    [
        (int)ItemCategory.Trash_Miscellaneous,
        (int)ItemCategory.Trash_Craft,
        (int)ItemCategory.Trash_Weapon,
        (int)ItemCategory.Trash_Armor,
        (int)ItemCategory.Trash_Material,
        (int)ItemCategory.Trash_Doodad,
        (int)ItemCategory.Trash_Consume
    ];

    public static readonly HashSet<int> EssentialConsumableCategories =
    [
        (int)ItemCategory.Potion,
        (int)ItemCategory.Food,
        (int)ItemCategory.Drink,
        (int)ItemCategory.Mana_Potion
    ];

    /// <summary>
    /// Returns true if the item is classified as vendor junk and safe to sell.
    /// Quest items, equipped items, and essential potions/food are strictly protected.
    /// </summary>
    public static bool IsTrash(Item item)
    {
        if (item?.Template == null)
            return false;

        if (!item.Template.Sellable)
            return false;

        // Protected quest items cannot be sold
        if (item.Template.CategoryId == (int)ItemCategory.Quest_Item ||
            item.Template.CategoryId == (int)ItemCategory.Quest_Equip_Weapon ||
            item.Template.CategoryId == (int)ItemCategory.uest_Equip_Armor ||
            item.Template.CategoryId == (int)ItemCategory.Quest_Equip_Accessory ||
            item.Template.LootQuestId > 0)
        {
            return false;
        }

        // Essential food & potions are preserved for sustain
        if (EssentialConsumableCategories.Contains(item.Template.CategoryId))
            return false;

        // Explicit trash categories
        if (TrashCategoryIds.Contains(item.Template.CategoryId))
            return true;

        // Gray / common grade non-equipment with refund value
        if (item.Grade == 0 && item is not EquipItem && item.Template.Refund > 0)
            return true;

        return false;
    }

    /// <summary>
    /// Audits the character's inventory bag and equipped gear durability.
    /// </summary>
    public static BagAuditSnapshot AuditBag(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);

        var bag = character.Inventory?.Bag;
        var total = bag?.ContainerSize ?? 0;
        var free = bag?.FreeSlotCount ?? 0;
        var used = Math.Max(0, total - free);

        var trash = new List<Item>();
        long estimatedValue = 0;
        if (bag != null)
        {
            foreach (var item in bag.GetItemsSnapshot())
            {
                if (item != null && IsTrash(item))
                {
                    trash.Add(item);
                    estimatedValue += item.Template.Refund * item.Count;
                }
            }
        }

        var damaged = new List<EquipItem>();
        var equip = character.Inventory?.Equipment;
        if (equip != null)
        {
            foreach (var item in equip.GetItemsSnapshot())
            {
                if (item is EquipItem eq && eq.Durability < eq.MaxDurability)
                    damaged.Add(eq);
            }
        }

        return new BagAuditSnapshot
        {
            TotalCapacity = total,
            FreeSlots = free,
            UsedSlots = used,
            TrashItems = trash,
            DamagedEquipment = damaged,
            TotalTrashEstimatedValue = estimatedValue
        };
    }

    /// <summary>
    /// Sells all classified trash items in the bot's bag to the specified merchant NPC.
    /// </summary>
    public static (int SoldCount, long RevenueEarned) SellAllTrash(GameplayActor actor, uint merchantNpcObjId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var audit = AuditBag(actor.Character);
        var soldCount = 0;
        long totalRevenue = 0;

        foreach (var item in audit.TrashItems)
        {
            var result = actor.Sell(merchantNpcObjId, item.Id);
            if (result.State == ActorLifecycleState.Completed)
            {
                soldCount++;
                if (result.Result is int revenue)
                    totalRevenue += revenue;
                else if (result.Result is long revLong)
                    totalRevenue += revLong;
            }
        }

        Logger.Info("[BotBagManager] Character {Name} sold {Count} trash items to merchant {MerchantId} for {Revenue} copper",
            actor.Character.Name, soldCount, merchantNpcObjId, totalRevenue);

        return (soldCount, totalRevenue);
    }

    /// <summary>
    /// Repairs all damaged equipment at the specified blacksmith or merchant NPC.
    /// </summary>
    public static (int RepairedCount, long Cost) RepairAllEquipment(GameplayActor actor, uint blacksmithNpcObjId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var moneyBefore = actor.Character.Money;
        var result = actor.Repair(blacksmithNpcObjId);
        var repairedCount = result.Result is int count ? count : 0;
        var cost = Math.Max(0, moneyBefore - actor.Character.Money);

        Logger.Info("[BotBagManager] Character {Name} repaired {Count} equipment items at blacksmith {BlacksmithId} (cost: {Cost} copper)",
            actor.Character.Name, repairedCount, blacksmithNpcObjId, cost);

        return (repairedCount, cost);
    }
}
