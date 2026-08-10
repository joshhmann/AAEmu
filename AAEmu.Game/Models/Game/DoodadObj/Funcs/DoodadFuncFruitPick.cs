using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncFruitPick : DoodadFuncTemplate
{
    // doodad_funcs
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncFruitPick");

        // Advance the fruit tree to its picking/looting phase, matching
        // DoodadFuncHarvest / DoodadFuncCropHarvest. The fruit yield comes from
        // the loot funcs on the next phase group (M3a-3 crop loop fix — fruit
        // trees are plantable via item_spawn_doodads, e.g. items 13925/14829).
        owner.ToNextPhase = true;
    }
}
