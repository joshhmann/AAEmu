using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSUnknownInstancePacket() : GamePacket(CSOffsets.CSUnknownInstancePacket, 1)
{
    public ushort Type { get; private set; }
    public int ZoneId { get; private set; }
    public uint InstId { get; private set; }

    public override void Read(PacketStream stream)
    {
        var type = stream.ReadUInt16();
        var zoneId = stream.ReadInt32();
        var instId = stream.ReadUInt32();

        Type = type;
        ZoneId = zoneId;
        InstId = instId;

        Logger.Warn("UnknownInstance, Type: {0}, ZoneId: {1}, InstId: {2}", type, zoneId, instId);
    }
}
