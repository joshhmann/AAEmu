using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;

#pragma warning disable IDE0130 // Namespace does not match folder structure

namespace AAEmu.Game.Models.Game.Quests.Acts;

/// <summary>
/// Only used in one instance of a test quest
/// </summary>
/// <param name="parentComponent"></param>
public class QuestActObjItemGroupUse(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public override bool CountsAsAnObjective => true;
    public uint ItemGroupId { get; set; }
    public uint HighlightDoodadId { get; set; }
    public int HighlightDoodadPhase { get; set; }
    public bool UseAlias { get; set; }
    public uint QuestActObjAliasId { get; set; }
    public bool DropWhenDestroy { get; set; }

    /// <summary>
    /// Checks if the items from the group have been used the specified amount of times
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    /// <returns></returns>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        Logger.Debug($"{QuestActTemplateName}({DetailId}).RunAct: Quest: {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), ItemGroupId {ItemGroupId}, Count {currentObjectiveCount}/{Count}");
        return currentObjectiveCount >= Count;
    }

    public override void InitializeAction(Quest quest, QuestAct questAct)
    {
        base.InitializeAction(quest, questAct);
        quest.Owner.Events.OnItemUse += questAct.OnItemUse;
    }

    public override void FinalizeAction(Quest quest, QuestAct questAct)
    {
        quest.Owner.Events.OnItemUse -= questAct.OnItemUse;
        base.FinalizeAction(quest, questAct);
    }

    public override void OnItemUse(QuestAct questAct, object sender, OnItemUseArgs args)
    {
        if (questAct.Id != ActId)
            return;

        // Only count item uses for items that are part of this quest's item group
        if (!QuestManager.Instance.CheckGroupItem(ItemGroupId, args.ItemId))
            return;

        Logger.Debug($"{QuestActTemplateName}({DetailId}).OnItemUse: Quest: {questAct.QuestComponent.Parent.Parent.TemplateId}, Owner {questAct.QuestComponent.Parent.Parent.Owner.Name} ({questAct.QuestComponent.Parent.Parent.Owner.Id}), ItemId {args.ItemId}");
        AddObjective(questAct, 1);
    }
}
