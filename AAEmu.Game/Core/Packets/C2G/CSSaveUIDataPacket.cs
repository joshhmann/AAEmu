using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSaveUIDataPacket() : GamePacket(CSOffsets.CSSaveUIDataPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var uiDataType = stream.ReadUInt16();
        var id = stream.ReadUInt32();
        // UI data is an opaque client blob, not text: it can contain bytes that are not valid
        // UTF-8. ReadString() decodes as UTF-8, which collapses such bytes to U+FFFD and destroys
        // the original. Map bytes 1:1 through Latin-1 instead, keeping ReadString()'s NUL trim.
        var len = stream.ReadUInt16();
        var data = System.Text.Encoding.Latin1.GetString(stream.ReadBytes(len)).Trim('\0');

        Connection.ActiveChar.SetOption(uiDataType, data);
    }
}
