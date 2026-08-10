using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncCropHarvest : DoodadFuncTemplate
{
    // doodad_funcs
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncCropHarvest");

        // Advance the crop to its looting phase: the harvest yield comes from the
        // loot funcs (DoodadFuncLootItem / DoodadFuncLootPack) on the next phase
        // group, exactly like DoodadFuncHarvest. Without this, a crop chain whose
        // mature phase lists DoodadFuncCropHarvest never reaches its loot group and
        // harvests yield nothing (M3a-3 crop loop fix).
        owner.ToNextPhase = true;
    }
}
