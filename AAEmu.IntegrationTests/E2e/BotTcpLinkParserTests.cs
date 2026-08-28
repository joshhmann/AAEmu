using System.IO.Compression;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

public sealed class BotTcpLinkParserTests
{
    [Fact]
    public void ParseGameFrames_DecompressesEveryInnerFrame_AndReadsVariablePiscDamage()
    {
        const uint casterObjId = 0x010203;
        const uint victimObjId = 0x0a0b0c;
        var damageBody = BuildUnitDamagedBody(casterObjId, victimObjId, 0x01);
        var firstHitBody = new PacketStream()
            .WriteBc(casterObjId)
            .WriteBc(victimObjId)
            .Write(18131u)
            .GetBytes();
        var secondFirstHitBody = new PacketStream()
            .WriteBc(victimObjId)
            .WriteBc(casterObjId)
            .Write(18131u)
            .GetBytes();

        var compressedPayload = BuildCompressedPayload(
            (SCOffsets.SCCombatFirstHitPacket, firstHitBody),
            (SCOffsets.SCUnitDamagedPacket, damageBody),
            (SCOffsets.SCCombatFirstHitPacket, secondFirstHitBody));
        var frame = BuildLevel4Frame(compressedPayload, packetCount: 3);
        var frames = new List<(ushort Type, byte[] Body)>();

        var consumed = BotTcpLink.ParseGameFrames(frame, 0, frame.Length, frames);

        Assert.Equal(frame.Length, consumed);
        Assert.Equal(3, frames.Count);
        Assert.Equal(SCOffsets.SCCombatFirstHitPacket, frames[0].Type);
        Assert.Equal(SCOffsets.SCUnitDamagedPacket, frames[1].Type);
        Assert.Equal(SCOffsets.SCCombatFirstHitPacket, frames[2].Type);
        Assert.True(BotTcpLink.TryReadVictimMatchedNonImmuneUnitDamaged(
            frames[1].Body, victimObjId, out var immune));
        Assert.False(immune);
    }

    [Fact]
    public void TryReadVictimMatchedNonImmuneUnitDamaged_RejectsImmuneAndWrongVictim()
    {
        const uint casterObjId = 0x010203;
        const uint victimObjId = 0x0a0b0c;
        var immuneBody = BuildUnitDamagedBody(casterObjId, victimObjId, 0x12);
        var wrongVictimBody = BuildUnitDamagedBody(casterObjId, 0x0d0e0f, 0x01);

        Assert.False(BotTcpLink.TryReadVictimMatchedNonImmuneUnitDamaged(
            immuneBody, victimObjId, out var immune));
        Assert.True(immune);
        Assert.False(BotTcpLink.TryReadVictimMatchedNonImmuneUnitDamaged(
            wrongVictimBody, victimObjId, out var wrongVictimImmune));
        Assert.False(wrongVictimImmune);
    }

    private static byte[] BuildUnitDamagedBody(uint casterObjId, uint victimObjId, ushort hitType)
    {
        var stream = new PacketStream()
            .Write((byte)0) // CastType.Skill
            .Write(18131u)
            .Write((ushort)1)
            .Write((byte)0) // SkillCasterType.Unit
            .WriteBc(casterObjId)
            .WriteBc(casterObjId)
            .WriteBc(victimObjId)
            .Write((byte)0); // crimeState
        stream.WritePisc(300, 0, 0); // variable-width first PISC (ushort, byte, byte)
        stream.WritePisc(0, 0, 70000); // variable-width second PISC (byte, byte, bc)
        stream.Write((byte)0); // holdable
        stream.Write(hitType);
        stream.Write((byte)1); // flag
        stream.Write((byte)1); // result
        return stream.GetBytes();
    }

    private static byte[] BuildCompressedPayload(params (ushort Type, byte[] Body)[] frames)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (var (type, body) in frames)
            {
                deflate.WriteByte(0);
                deflate.WriteByte(0);
                deflate.Write(BitConverter.GetBytes(type));
                deflate.Write(body);
            }
        }

        return output.ToArray();
    }

    private static byte[] BuildLevel4Frame(byte[] compressedPayload, ushort packetCount)
    {
        var length = checked((ushort)(4 + compressedPayload.Length));
        var frame = new byte[2 + length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 2), length);
        frame[2] = 0xdd;
        frame[3] = 4;
        BitConverter.TryWriteBytes(frame.AsSpan(4, 2), packetCount);
        Buffer.BlockCopy(compressedPayload, 0, frame, 6, compressedPayload.Length);
        return frame;
    }
}
