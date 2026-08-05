using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;

#pragma warning disable IDE0130 // Namespace does not match folder structure

namespace AAEmu.Game.Models.Game.Quests.Acts;

/// <summary>
/// Not used
/// </summary>
/// <param name="parentComponent"></param>
public class QuestActObjItemGroupGather(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public override bool CountsAsAnObjective => true;
    public uint ItemGroupId { get; set; }
    public bool Cleanup { get; set; }
    public uint HighlightDoodadId { get; set; }
    public int HighlightDoodadPhase { get; set; }
    public bool UseAlias { get; set; }
    public uint QuestActObjAliasId { get; set; }
    public bool DropWhenDestroy { get; set; }
    public bool DestroyWhenDrop { get; set; }

    /// <summary>
    /// Total amount of items owned in the inventory that are part of this quest's item group
    /// </summary>
    /// <param name="quest"></param>
    /// <returns></returns>
    private int GetGroupItemCount(Quest quest)
    {
        var totalCount = 0;
        foreach (var itemId in QuestManager.Instance.GetGroupItems(ItemGroupId))
            totalCount += quest.Owner.Inventory.GetItemsCount(itemId);
        return totalCount;
    }

    /// <summary>
    /// Checks if the number of items from the group have been acquired 
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        Logger.Debug($"{QuestActTemplateName}({DetailId}).RunAct: Quest: {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), ItemGroupId {ItemGroupId}, Count {currentObjectiveCount}/{Count}");
        SetObjective(quest, GetGroupItemCount(quest));
        return GetObjective(quest) >= Count;
    }

    public override void InitializeAction(Quest quest, QuestAct questAct)
    {
        base.InitializeAction(quest, questAct);
        SetObjective(quest, GetGroupItemCount(quest));

        // Register event handler
        quest.Owner.Events.OnItemGroupGather += questAct.OnItemGroupGather;
    }

    public override void FinalizeAction(Quest quest, QuestAct questAct)
    {
        base.FinalizeAction(quest, questAct);

        // Un-register event handler
        quest.Owner.Events.OnItemGroupGather -= questAct.OnItemGroupGather;
    }

    public override void QuestCleanup(Quest quest)
    {
        base.QuestCleanup(quest);
        if (!Cleanup)
            return;

        // Remove the gathered items from the player's inventory, spread over the group members
        var cleanupCount = Math.Min(GetObjective(quest), MaxObjective());
        foreach (var itemId in QuestManager.Instance.GetGroupItems(ItemGroupId))
        {
            if (cleanupCount <= 0)
                break;
            var ownedCount = quest.Owner.Inventory.GetItemsCount(itemId);
            if (ownedCount <= 0)
                continue;

            var removeCount = Math.Min(cleanupCount, ownedCount);
            quest.Owner?.Inventory.ConsumeItem(null, ItemTaskType.QuestRemoveSupplies, itemId, removeCount, null);
            cleanupCount -= removeCount;
        }
    }

    public override void QuestDropped(Quest quest)
    {
        base.QuestDropped(quest);
        if (!DestroyWhenDrop)
            return;

        // Remove the gathered items from the player's inventory, spread over the group members
        var cleanupCount = Math.Min(GetObjective(quest), MaxObjective());
        foreach (var itemId in QuestManager.Instance.GetGroupItems(ItemGroupId))
        {
            if (cleanupCount <= 0)
                break;
            var ownedCount = quest.Owner.Inventory.GetItemsCount(itemId);
            if (ownedCount <= 0)
                continue;

            var removeCount = Math.Min(cleanupCount, ownedCount);
            quest.Owner?.Inventory.ConsumeItem(null, ItemTaskType.QuestRemoveSupplies, itemId, removeCount, null);
            cleanupCount -= removeCount;
        }
    }

    public override void OnItemGroupGather(QuestAct questAct, object sender, OnItemGroupGatherArgs args)
    {
        if (questAct.Id != ActId || args.ItemGroupId != ItemGroupId)
            return;

        // Just adding/removing the count should technically be enough without having to do a new count
        // AddObjective(questAct, args.Count, Count);
        SetObjective(questAct, GetGroupItemCount(questAct.QuestComponent.Parent.Parent));
    }
}
