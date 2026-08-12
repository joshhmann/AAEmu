using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncDairyCollect : DoodadFuncTemplate
{
    // doodad_funcs
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Debug("DoodadFuncDairyCollect: doodad {0} (TemplateId {1}), nextPhase {2}", owner.ObjId, owner.TemplateId, nextPhase);

        // The collect func itself carries no item data (doodad_func_dairy_collects
        // has only an id): collecting advances the animal to its milked phase,
        // and the milk yield comes from the loot funcs on that phase — exactly
        // like the canonical dairy chain (happy cow 5786 → milked cow 8436 →
        // LootPack 81 → milk 8055). Mirrors DoodadFuncCropHarvest / FruitPick.
        owner.ToNextPhase = true;
    }
}
