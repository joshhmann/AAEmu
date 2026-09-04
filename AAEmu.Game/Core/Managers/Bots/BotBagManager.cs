using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
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
                if (item != null && (IsTrash(item) || IsObsoleteEquipment(character, item)))
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
    /// Calculates an effective gear score for evaluating weapon and armor upgrades.
    /// Incorporates item level, level requirement, item grade, weapon DPS, and durability state.
    /// </summary>
    public static int CalculateGearScore(Item? item)
    {
        if (item?.Template == null)
            return 0;

        // A broken item (0 durability with positive MaxDurability) provides no combat value
        if (item is EquipItem { MaxDurability: > 0, Durability: <= 0 })
            return 0;

        var level = Math.Max(item.Template.LevelRequirement, item.Template.Level);
        var score = level * 100 + item.Grade * 15;

        if (item.Template is WeaponTemplate wt && wt.HoldableTemplate != null)
        {
            score += wt.HoldableTemplate.EnchantedDps1000 / 100;
        }

        return Math.Max(1, score);
    }

    /// <summary>
    /// Chooses the optimal equipment slot for a candidate item:
    /// returns the first empty allowed slot, or the occupied allowed slot with the lowest gear score.
    /// </summary>
    public static EquipmentItemSlot? ChooseTargetSlot(Character character, Item bagItem)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (bagItem?.Template == null)
            return null;

        var allowedSlots = EquipmentContainer.GetAllowedGearSlots(bagItem.Template);
        if (allowedSlots.Count == 0)
            return null;

        var inventory = character.Inventory;
        if (inventory?.Equipment == null)
            return null;

        // 1. First priority: any empty allowed slot
        foreach (var slot in allowedSlots)
        {
            if (inventory.Equipment.GetItemBySlot((int)slot) == null)
                return slot;
        }

        // 2. Second priority: the allowed slot with the weakest occupant
        EquipmentItemSlot? bestSlot = null;
        var lowestOccupantScore = int.MaxValue;

        foreach (var slot in allowedSlots)
        {
            var occupant = inventory.Equipment.GetItemBySlot((int)slot);
            var occupantScore = CalculateGearScore(occupant);
            if (occupantScore < lowestOccupantScore)
            {
                lowestOccupantScore = occupantScore;
                bestSlot = slot;
            }
        }

        return bestSlot;
    }

    /// <summary>
    /// Evaluates whether an item in the bag is an upgrade for the character.
    /// Returns true if an allowed slot is empty or if its gear score exceeds the occupant's score.
    /// </summary>
    public static bool IsUpgrade(Character character, Item bagItem, out EquipmentItemSlot targetSlot)
    {
        targetSlot = default;
        ArgumentNullException.ThrowIfNull(character);
        if (bagItem?.Template == null)
            return false;

        // Level requirement gate
        if (bagItem.Template.LevelRequirement > 0 && bagItem.Template.LevelRequirement > character.Level)
            return false;

        var slot = ChooseTargetSlot(character, bagItem);
        if (slot == null)
            return false;

        targetSlot = slot.Value;
        var occupant = character.Inventory?.Equipment?.GetItemBySlot((int)targetSlot);
        if (occupant == null)
            return true;

        if (occupant.TemplateId == bagItem.TemplateId && occupant.Grade >= bagItem.Grade)
            return false;

        var candidateScore = CalculateGearScore(bagItem);
        var occupantScore = CalculateGearScore(occupant);

        return candidateScore > occupantScore;
    }

    /// <summary>
    /// Checks if an equippable item in the bag is strictly inferior to what is currently equipped.
    /// Protected quest items are never considered obsolete.
    /// </summary>
    public static bool IsObsoleteEquipment(Character character, Item item)
    {
        if (item is not EquipItem || item.Template == null)
            return false;

        if (!item.Template.Sellable || item.Template.Refund <= 0)
            return false;

        // Protected quest items cannot be treated as trash
        if (item.Template.CategoryId == (int)ItemCategory.Quest_Item ||
            item.Template.CategoryId == (int)ItemCategory.Quest_Equip_Weapon ||
            item.Template.CategoryId == (int)ItemCategory.uest_Equip_Armor ||
            item.Template.CategoryId == (int)ItemCategory.Quest_Equip_Accessory ||
            item.Template.LootQuestId > 0)
        {
            return false;
        }

        var allowedSlots = EquipmentContainer.GetAllowedGearSlots(item.Template);
        if (allowedSlots.Count == 0)
            return false;

        var itemScore = CalculateGearScore(item);
        foreach (var slot in allowedSlots)
        {
            var occupant = character.Inventory?.Equipment?.GetItemBySlot((int)slot);
            if (occupant == null || CalculateGearScore(occupant) <= itemScore)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Scans the bot's bag for equipment upgrades and equips them via the real GameplayActor.Equip contract.
    /// Displaces old equipment into the bag so subsequent vendoring can sell obsolete items.
    /// </summary>
    public static (int EquippedCount, IReadOnlyList<string> EquippedItems) AutoEquipUpgrades(GameplayActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var character = actor.Character;
        var bag = character.Inventory?.Bag;
        if (bag == null)
            return (0, []);

        var equippedCount = 0;
        var log = new List<string>();

        var candidates = bag.GetItemsSnapshot();
        var sortedCandidates = candidates
            .Where(i => i?.Template != null)
            .OrderByDescending(CalculateGearScore)
            .ToList();

        foreach (var item in sortedCandidates)
        {
            if (bag.GetItemByItemId(item.Id) == null)
                continue;

            if (IsUpgrade(character, item, out var targetSlot))
            {
                var req = actor.Equip(item.TemplateId);
                if (req.State == ActorLifecycleState.Completed)
                {
                    equippedCount++;
                    var entry = $"{item.Template.Name ?? item.TemplateId.ToString()} ({item.TemplateId}) into {targetSlot}";
                    log.Add(entry);
                    Logger.Info("[BotBagManager] Character {Name} auto-equipped upgrade: {Entry}", character.Name, entry);
                }
            }
        }

        return (equippedCount, log);
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
