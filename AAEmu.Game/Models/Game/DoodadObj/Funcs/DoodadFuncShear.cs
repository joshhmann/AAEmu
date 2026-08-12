using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncShear : DoodadFuncTemplate
{
    // doodad_funcs
    public uint ShearTypeId { get; set; }
    public int ShearTerm { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Debug("DoodadFuncShear: doodad {0} (TemplateId {1}), ShearTypeId {2}, ShearTerm {3} ms, nextPhase {4}",
            owner.ObjId, owner.TemplateId, ShearTypeId, ShearTerm, nextPhase);

        // Shearing advances the animal to its sheared phase (canonical sheep
        // chain: woolly sheep → sheared sheep → regrow). The regrow cooldown
        // (ShearTerm, canonical 60,000 ms) is published on the doodad as its
        // growth deadline; when the sheared phase carries a timer func, that
        // timer is the authoritative revert and overwrites the deadline.
        if (ShearTerm > 0)
        {
            owner.GrowthTime = DateTime.UtcNow.AddMilliseconds(ShearTerm);
        }

        owner.ToNextPhase = true;
    }
}
