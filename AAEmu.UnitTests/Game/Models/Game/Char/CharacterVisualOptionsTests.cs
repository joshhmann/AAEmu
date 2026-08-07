using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

/// <summary>
/// Regression tests for the M2b-E2E restart266 flake (run29, 2026-08-07):
/// CharacterVisualOptions.WriteOptions NRE'd on a null Stp (clients that send
/// a spawn packet with visual-option flag bit 0 never populate Stp), which
/// truncated SCUnitStatePacket mid-send and dropped the bot session. The
/// write paths must serialize a zeroed 6-byte stp block instead of null.
/// </summary>
public class CharacterVisualOptionsTests
{
    [Test]
    public async Task WriteOptions_WithNullStp_DoesNotThrowAndWritesZeroedStpBlock()
    {
        // Arrange — Stp is null exactly like a spawn packet with flag bit 0.
        var options = new CharacterVisualOptions { Stp = null };
        var stream = new PacketStream();

        // Act
        options.WriteOptions(stream);

        // Assert — 6 zeroed stp bytes + 4 bools, no exception.
        stream.Rollback();
        var stp = stream.ReadBytes(6);
        await Assert.That(stp).IsEquivalentTo(new byte[6]);
        await Assert.That(stream.Count - stream.Pos).IsEqualTo(4);
    }

    [Test]
    public async Task Write_WithFlagStpBitAndNullStp_DoesNotThrowAndWritesZeroedStpBlock()
    {
        // Arrange — flag demands the stp block but the field was never populated.
        var options = new CharacterVisualOptions { Stp = null };
        var stream = new PacketStream();

        // Act
        options.Write(stream, flag: 1);

        // Assert
        stream.Rollback();
        var stp = stream.ReadBytes(6);
        await Assert.That(stp).IsEquivalentTo(new byte[6]);
    }

    [Test]
    public async Task WriteOptions_WithStpSet_PreservesTheClientStpBytes()
    {
        // Arrange — a client that DID send the stp block keeps its bytes.
        var stp = new byte[] { 1, 2, 3, 4, 5, 6 };
        var options = new CharacterVisualOptions { Stp = stp };
        var stream = new PacketStream();

        // Act
        options.WriteOptions(stream);

        // Assert
        stream.Rollback();
        await Assert.That(stream.ReadBytes(6)).IsEquivalentTo(stp);
    }
}
