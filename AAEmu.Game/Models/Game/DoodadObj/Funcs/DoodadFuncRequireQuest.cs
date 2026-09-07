using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncRequireQuest : DoodadPhaseFuncTemplate
{
    public WorldInteractionType WorldInteractionId { get; set; }
    public uint QuestId { get; set; }

    public override bool Use(BaseUnit caster, Doodad owner)
    {
        Logger.Trace("DoodadFuncRequireQuest QuestId: {0}, WorldIntId {1}", QuestId, WorldInteractionId);

        if (caster is Character character)
        {
            //character.Quests.OnInteraction(WorldInteractionId, character.CurrentTarget);
            if (character.Quests.HasQuest(QuestId))
                return false; // This player is on the correct quest, continue
            return true; // Player doesn't have the quest, stop execution
        }
        // No player context (e.g. world spawner boot or timer tick).
        // This gate means "only proceed for a player holding this quest", so with
        // no player present we must STOP (return true), not continue.
        // Returning false was letting doodads with RequireQuest + Timer (such as
        // Quest 307 "Trash" sacks) run their timers and self-delete 120s after boot.
        return true;
    }
}
