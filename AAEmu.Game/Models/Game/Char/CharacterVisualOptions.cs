using AAEmu.Commons.Network;

namespace AAEmu.Game.Models.Game.Char;

public class CharacterVisualOptions : PacketMarshaler
{
    /// <summary>Zeroed 6-byte stp block — the wire default for characters whose
    /// visual-option flag never carried the stp bit (the stp field is a fixed
    /// 6-byte block in SCUnitStatePacket regardless of the create/spawn flag).</summary>
    private static readonly byte[] EmptyStp = new byte[6];

    private byte _flag;
    public byte[] Stp;
    public bool Helmet;
    public bool BackHoldable;
    public bool Cosplay;
    public bool CosplayBackpack;

    public override void Read(PacketStream stream)
    {
        _flag = stream.ReadByte();
        if ((_flag & 1) == 1)
            Stp = stream.ReadBytes(6);
        if ((_flag & 2) == 2)
            Helmet = stream.ReadBoolean();
        if ((_flag & 4) == 4)
            BackHoldable = stream.ReadBoolean();
        if ((_flag & 8) == 8)
            Cosplay = stream.ReadBoolean();
        if ((_flag & 16) == 16)
            CosplayBackpack = stream.ReadBoolean();
    }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(_flag);
        return Write(stream, _flag);
    }

    public PacketStream Write(PacketStream stream, byte flag)
    {
        if ((flag & 1) == 1)
            stream.Write(Stp ?? EmptyStp);
        if ((flag & 2) == 2)
            stream.Write(Helmet);
        if ((flag & 4) == 4)
            stream.Write(BackHoldable);
        if ((flag & 8) == 8)
            stream.Write(Cosplay);
        if ((flag & 16) == 16)
            stream.Write(CosplayBackpack);
        return stream;
    }
    public PacketStream WriteOptions(PacketStream stream)
    {
        // all this data must be output to the SCUnitStatePacket
        // stp is only populated when the client's visual-option flag carried
        // bit 1 (custom appearance block); clients that omit it leave Stp
        // null, and a null byte[] write NREs PacketStream.Write(byte[]) —
        // truncating SCUnitStatePacket mid-send and desyncing the session
        // (M2b-E2E restart266, run29). Serialize the fixed 6-byte block as
        // zeroed bytes instead.
        stream.Write(Stp ?? EmptyStp);       // stp
        stream.Write(Helmet);          // helmet
        stream.Write(BackHoldable);    // back_holdable
        stream.Write(Cosplay);         // cosplay
        stream.Write(CosplayBackpack); // cosplay_backpack

        return stream;
    }
}
