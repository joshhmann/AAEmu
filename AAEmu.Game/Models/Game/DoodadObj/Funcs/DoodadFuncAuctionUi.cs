using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncAuctionUi : DoodadFuncTemplate
{
    // doodad_funcs
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncAuctionUi");
        // Same open path as an auctioneer NPC right-click (CSStartInteractionPacket):
        // list the auctioneer interaction skill so the client opens the auction house.
        if (caster is not Character character)
            return;
        character.SendPacket(new SCNpcInteractionSkillListPacket(owner.ObjId, owner.ObjId, 0, 0, 0, 0, [SkillsEnum.UseAuctioneer]));
    }
}
