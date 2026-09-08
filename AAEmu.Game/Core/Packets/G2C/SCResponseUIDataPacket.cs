using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCResponseUIDataPacket(uint characterId, ushort uiDataType, string uiData)
    : GamePacket(SCOffsets.SCResponseUIDataPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(characterId);
        stream.Write(uiDataType);
        // Latin-1 to mirror CSSaveUIDataPacket: Write(string) would UTF-8 encode the blob,
        // expanding every byte >= 0x80. Write(byte[], true) emits the same ushort-length +
        // payload framing, so the wire format is unchanged, only the encoding is lossless.
        var uiBytes = System.Text.Encoding.Latin1.GetBytes(uiData);
        stream.Write(uiBytes, true);
        stream.Write(uiBytes.Length + 1);
        return stream;
    }
}
