using System;
using System.Text;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCResponseUIDataPacketTests
{
    // UI option blobs are opaque client bytes, not text: quest-tracker payloads carry bytes
    // like 0xFF that are not valid UTF-8. The packets map them 1:1 through Latin-1 —
    // SCResponseUIDataPacket writes Latin-1 bytes, CSSaveUIDataPacket reads
    // (UInt16 length + raw bytes + Latin-1 decode + NUL trim) — so the blob must
    // survive the trip byte-identical.
    [Test]
    public async Task Write_BinaryBlob_RoundTripsByteIdentical()
    {
        var original = new byte[] { 0x22, 0xFF, 0x22, 0x0D, 0x0A, 0x72, 0x6F, 0x61, 0x64, 0x6D, 0x61, 0x70, 0x00, 0x80, 0xFE, 0x22 };
        const uint characterId = 1;
        const ushort uiDataType = 6;

        var stream = new SCResponseUIDataPacket(characterId, uiDataType, Encoding.Latin1.GetString(original)).Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadUInt32()).IsEqualTo(characterId);
        await Assert.That(stream.ReadUInt16()).IsEqualTo(uiDataType);

        // Exact CSSaveUIDataPacket read sequence.
        var len = stream.ReadUInt16();
        var data = Encoding.Latin1.GetString(stream.ReadBytes(len)).Trim('\0');
        var roundTripped = Encoding.Latin1.GetBytes(data);

        await Assert.That(Convert.ToHexString(roundTripped)).IsEqualTo(Convert.ToHexString(original));
        await Assert.That(stream.ReadInt32()).IsEqualTo(original.Length + 1);
    }

    // For pure-ASCII blobs the Latin-1 path must emit the exact framing the legacy
    // Write(string) path did, so existing settings/keybind payloads are wire-compatible.
    [Test]
    public async Task Write_AsciiBlob_FramingMatchesLegacyWriteString()
    {
        const string ascii = "ShowFps|ShowPlayerHelmet|option_camera_fov_set";

        var actual = new SCResponseUIDataPacket(1, 1, ascii).Write(new PacketStream()).GetBytes();

        var legacy = new PacketStream();
        legacy.Write(1u);
        legacy.Write((ushort)1);
        legacy.Write(ascii);
        legacy.Write(ascii.Length + 1);
        var expected = legacy.GetBytes();

        await Assert.That(Convert.ToHexString(actual)).IsEqualTo(Convert.ToHexString(expected));
    }
}
