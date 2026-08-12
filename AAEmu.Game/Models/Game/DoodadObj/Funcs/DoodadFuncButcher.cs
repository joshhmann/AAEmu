using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncButcher : DoodadFuncTemplate
{
    // doodad_funcs
    public string CorpseModel { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Debug("DoodadFuncButcher: doodad {0} (TemplateId {1}), CorpseModel {2}, nextPhase {3}",
            owner.ObjId, owner.TemplateId, CorpseModel, nextPhase);

        // Butchering advances the animal to its butchered/corpse phase; the
        // meat yield comes from the loot funcs on that phase (canonical cow
        // chain: cow 5782 → butchered 5790 → LootPack 79/80 → beef 8048; the
        // CorpseModel field is client-side display info — the phase group's
        // model is what the phase-change packet shows).
        owner.ToNextPhase = true;
    }
}
