using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.Game.Models.Game.Quests.Acts;

public class QuestActCheckGuard(QuestComponentTemplate parentComponent) : QuestActTemplate(parentComponent)
{
    public uint NpcId { get; set; }

    /// <summary>
    /// Escort/protect guard check: verifies the guard NPC is actually present in the world and alive.
    /// Returns false when the guard is dead, despawned, or cannot be resolved at all, so the
    /// escort/protect objective can never silently pass.
    /// </summary>
    /// <param name="quest"></param>
    /// <param name="questAct"></param>
    /// <param name="currentObjectiveCount"></param>
    /// <returns></returns>
    public override bool RunAct(Quest quest, QuestAct questAct, int currentObjectiveCount)
    {
        Logger.Warn($"{QuestActTemplateName}({DetailId}).RunAct: Quest {quest.TemplateId}, Owner {quest.Owner.Name} ({quest.Owner.Id}), NpcId {NpcId}");

        if (quest.Owner is not Character character)
            return false;

        var guard = character.ParentWorld.GetNpcByTemplateId(NpcId);
        if (guard == null)
        {
            // Guard is missing or has despawned — a missing guard must not pass the check
            return false;
        }

        return !guard.IsDead;
    }
}
