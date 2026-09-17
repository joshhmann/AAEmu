using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;

namespace AAEmu.UnitTests.Game.Core.Packets.C2G;

/// <summary>
/// Lane A incident fix #2 (LOG-FIRST ONLY): the client "leave mirage island" UI button
/// sends opcode 0x13e, whose Read parses type/zoneId/instId. These tests pin the parse
/// shape so a future live-capture slice can route with confidence. No routing here.
/// </summary>
public class CSUnknownInstancePacketTests
{
    private static PacketStream CraftWireBytes(ushort type, int zoneId, uint instId)
    {
        var stream = new PacketStream();
        stream.Write(type);
        stream.Write(zoneId);
        stream.Write(instId);
        return new PacketStream(stream.GetBytes());
    }

    [Test]
    public async Task Read_KnownTriple_DecodesTypeZoneIdInstIdExactly()
    {
        const ushort type = 0x0002;
        const int zoneId = 801;
        const uint instId = 1701;

        var packet = new CSUnknownInstancePacket();
        packet.Read(CraftWireBytes(type, zoneId, instId));

        await Assert.That(packet.Type).IsEqualTo(type);
        await Assert.That(packet.ZoneId).IsEqualTo(zoneId);
        await Assert.That(packet.InstId).IsEqualTo(instId);
    }

    [Test]
    public async Task Read_BoundaryTriple_DecodesExactly()
    {
        const ushort type = ushort.MaxValue;
        const int zoneId = -1;
        const uint instId = uint.MaxValue;

        var packet = new CSUnknownInstancePacket();
        packet.Read(CraftWireBytes(type, zoneId, instId));

        await Assert.That(packet.Type).IsEqualTo(type);
        await Assert.That(packet.ZoneId).IsEqualTo(zoneId);
        await Assert.That(packet.InstId).IsEqualTo(instId);
    }

    [Test]
    public async Task Read_EmptyStream_DoesNotThrow_LeavesDefaults()
    {
        var packet = new CSUnknownInstancePacket();
        packet.Read(new PacketStream(Array.Empty<byte>()));

        await Assert.That(packet.Type).IsEqualTo((ushort)0);
        await Assert.That(packet.ZoneId).IsEqualTo(0);
        await Assert.That(packet.InstId).IsEqualTo(0u);
    }

    [Test]
    public async Task Read_TruncatedStream_DoesNotThrow()
    {
        // Garbage: fewer bytes than the 2 + 4 + 4 the payload carries.
        var packet = new CSUnknownInstancePacket();
        packet.Read(new PacketStream(new byte[] { 0x01, 0x02, 0x03 }));

        await Assert.That(packet.Type).IsNotEqualTo((ushort)0);
    }
}
