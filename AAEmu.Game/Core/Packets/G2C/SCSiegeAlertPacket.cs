using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Siege schedule phase alert (opcode 0xed, offset constant pre-existing).
/// The live 1.2 payload shape for this opcode has never been captured; this
/// minimal marshaler (dominion zone-group id + SiegePhase byte) is the slice-1
/// placeholder documented in scorecard-explorations/mechanics/dominion-domain.md.
/// </summary>
public class SCSiegeAlertPacket(ushort dominionId, byte period) : GamePacket(SCOffsets.SCSiegeAlertPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(dominionId);
        stream.Write(period);
        return stream;
    }
}
