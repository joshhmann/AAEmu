using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.Quests.Acts;

public class QuestActCheckSphere(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public uint SphereId { get; set; }

    public override void InitializeAction(Quest quest, QuestAct questAct)
    {
        base.InitializeAction(quest, questAct);
        ((GameObject)quest.Owner).ParentWorld.SphereQuestManager.AddSphereQuestTriggers(quest.Owner, quest, ParentComponent.Id, 0);
        quest.Owner.Events.OnEnterSphere += questAct.OnEnterSphere;
        quest.Owner.Events.OnExitSphere += questAct.OnExitSphere;
    }

    public override void FinalizeAction(Quest quest, QuestAct questAct)
    {
        ((GameObject)quest.Owner).ParentWorld.SphereQuestManager.RemoveSphereQuestTriggers(quest.Owner.Id, (uint)quest.Id);
        quest.Owner.Events.OnEnterSphere -= questAct.OnEnterSphere;
        quest.Owner.Events.OnExitSphere -= questAct.OnExitSphere;
        base.FinalizeAction(quest, questAct);
    }

    /// <summary>
    /// Checks if the owner is currently inside one of the quest spheres of this component.
    /// This is a "check" act (like QuestActCheckGuard), not an objective act: the loader
    /// keeps ThisComponentObjectiveIndex = 0xFF, so RunAct evaluates the owner's LIVE
    /// position against the component's quest spheres instead of an objective counter
    /// (which would always read 0 for this act).
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    /// <returns></returns>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        Logger.Debug($"{QuestActTemplateName}({DetailId}).RunAct: Quest {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), SphereId {SphereId}");

        if (quest.Owner is not Character character)
            return false;

        var spheres = character.ParentWorld?.SphereQuestManager.GetQuestSpheres(ParentComponent.Id);
        if (spheres == null || spheres.Count <= 0)
            return false;

        var position = character.Transform?.World?.Position ?? Vector3.Zero;
        return spheres.Any(sphere => sphere.Contains(position));
    }

    public override void OnEnterSphere(QuestAct questAct, object sender, OnEnterSphereArgs args)
    {
        if (questAct.Id != ActId || args.SphereQuest.QuestId != ParentQuestTemplate.Id || args.SphereQuest.ComponentId != ParentComponent.Id)
            return;
        Logger.Debug($"{QuestActTemplateName}({DetailId}).OnEnterSphere: Quest {questAct.QuestComponent.Parent.Parent.TemplateId}, Owner {questAct.QuestComponent.Parent.Parent.Owner.Name} ({questAct.QuestComponent.Parent.Parent.Owner.Id}), SphereId {SphereId}");
        // Check act: there is no objective counter for this act (ThisComponentObjectiveIndex
        // is 0xFF, so SetObjective would write past the Objectives array), sphere entry only
        // requests a re-evaluation — RunAct checks the owner's live position against the sphere.
        questAct.RequestEvaluation();
    }

    public override void OnExitSphere(QuestAct questAct, object sender, OnExitSphereArgs args)
    {
        if (questAct.Id != ActId || args.SphereQuest.QuestId != ParentQuestTemplate.Id || args.SphereQuest.ComponentId != ParentComponent.Id)
            return;
        Logger.Debug($"{QuestActTemplateName}({DetailId}).OnExitSphere: Quest {questAct.QuestComponent.Parent.Parent.TemplateId}, Owner {questAct.QuestComponent.Parent.Parent.Owner.Name} ({questAct.QuestComponent.Parent.Parent.Owner.Id}), SphereId {SphereId}");
        questAct.RequestEvaluation();
    }
}
