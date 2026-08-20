using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSResurrectCharacterPacket() : GamePacket(CSOffsets.CSResurrectCharacterPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var inPlace = stream.ReadBoolean();

        Logger.Debug("ResurrectCharacter, InPlace: {0}", inPlace);

        // The resurrection semantics (portal selection, HP/MP restore,
        // broadcasts, revival debuffs, underwater/breath reset) live in
        // CharacterResurrection — the same real engine path the M6.2 bot
        // death watch uses for headless bots (which have no client to send
        // this packet). No server-side relocation here: the retail client
        // re-enters at the portal.
        CharacterResurrection.Resurrect(Connection.ActiveChar, inPlace);
    }
}
