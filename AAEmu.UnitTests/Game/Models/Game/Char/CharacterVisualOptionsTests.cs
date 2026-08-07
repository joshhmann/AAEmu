using AAEmu.Commons.Network;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

/// <summary>
/// CharacterVisualOptions serialization defect (RCA t_c8ffadb6 Family 1, 106×
/// PacketStream NRE in E2E runs): CSSpawnCharacterPacket accepts flag-0 visual
/// options (protocol-legal — the server's own Read handles it), which leaves
/// Stp null; WriteOptions (CharacterVisualOptions.cs:52) and Write(stream, 31)
/// then wrote Stp unconditionally → PacketStream NRE at Write(byte[]),
/// truncating the SCUnitStatePacket mid-body. Null Stp must serialize as a
/// 6-byte zero block (read side is ReadBytes(6)) with the on-wire layout
/// unchanged for the 1.2 client.
/// </summary>
public class CharacterVisualOptionsTests
{
    private static CharacterVisualOptions CreateOptions(byte[] stp)
        => new()
        {
            Stp = stp,
            Helmet = true,
            BackHoldable = false,
            Cosplay = true,
            CosplayBackpack = false
        };

    /// <summary>Builds the full client→server spawn payload: flag byte + gated fields
    /// (the shape CSSpawnCharacterPacket.Read consumes).</summary>
    private static PacketStream BuildClientPayload(CharacterVisualOptions options, byte flag)
    {
        var payload = new PacketStream();
        payload.Write(flag);
        options.Write(payload, flag);
        return payload;
    }

    [Test]
    public async Task WriteOptions_NullStp_DoesNotThrowAndWritesZeroBlock()
    {
        var options = CreateOptions(stp: null);
        var stream = new PacketStream();

        // Was NRE: stream.Write(Stp) with Stp == null (CharacterVisualOptions.cs:52).
        options.WriteOptions(stream);

        var body = stream.GetBytes();
        // WriteOptions emits the 5 fields without the flag byte: 6 stp + 4 bools.
        await Assert.That(body.Length).IsEqualTo(10);
        await Assert.That(body[..6].SequenceEqual(new byte[6])).IsTrue();
    }

    [Test]
    public async Task Write_Flag31_NullStp_WritesZeroBlockAndRoundTrips()
    {
        var options = CreateOptions(stp: null);
        var stream = new PacketStream();

        // Flag-gated write path (the SCUnitStatePacket variant). Note: the
        // flag byte itself is only written by the parameterless Write(stream)
        // overload / the client — Write(stream, flag) emits the fields only.
        options.Write(stream, 31);

        var body = stream.GetBytes();
        await Assert.That(body.Length).IsEqualTo(10); // 6 stp + 4 bools, no flag byte
        await Assert.That(body[..6].SequenceEqual(new byte[6])).IsTrue();
        // Wire order locked by the read side (CharacterVisualOptions.Read):
        // flag, stp(6), helmet, back_holdable, cosplay, cosplay_backpack.
        await Assert.That(body[6]).IsEqualTo((byte)1); // helmet
        await Assert.That(body[7]).IsEqualTo((byte)0); // back_holdable
        await Assert.That(body[8]).IsEqualTo((byte)1); // cosplay
        await Assert.That(body[9]).IsEqualTo((byte)0); // cosplay_backpack

        // Round-trip through the server's Read path (CSSpawnCharacterPacket):
        // client payload = flag byte + gated fields.
        var roundTrip = new CharacterVisualOptions();
        roundTrip.Read(BuildClientPayload(options, 31));
        await Assert.That(roundTrip.Stp is not null && roundTrip.Stp.SequenceEqual(new byte[6])).IsTrue();
        await Assert.That(roundTrip.Helmet).IsTrue();
        await Assert.That(roundTrip.BackHoldable).IsFalse();
        await Assert.That(roundTrip.Cosplay).IsTrue();
        await Assert.That(roundTrip.CosplayBackpack).IsFalse();
    }

    [Test]
    public async Task Write_Flag31_WithStp_PreservesStpBytes()
    {
        byte[] stp = [1, 2, 3, 4, 5, 6];
        var options = CreateOptions(stp);
        var stream = new PacketStream();

        options.Write(stream, 31);

        var body = stream.GetBytes();
        await Assert.That(body[..6].SequenceEqual(stp)).IsTrue();

        var roundTrip = new CharacterVisualOptions();
        roundTrip.Read(BuildClientPayload(options, 31));
        await Assert.That(roundTrip.Stp is not null && roundTrip.Stp.SequenceEqual(stp)).IsTrue();
    }

    [Test]
    public async Task ClientPayload_Flag31_RoundTripsThroughRead()
    {
        // Full client→server spawn payload — the layout a real 1.2 client
        // sends on spawn (flag 31 = STP + helmet + back_holdable + cosplay +
        // backpack), and the payload the E2E harness now sends.
        var options = CreateOptions(stp: [7, 8, 9, 10, 11, 12]);
        var payload = BuildClientPayload(options, 31);

        var body = payload.GetBytes();
        await Assert.That(body.Length).IsEqualTo(11); // flag + 6 stp + 4 bools
        await Assert.That(body[0]).IsEqualTo((byte)31);
        await Assert.That(body[1..7].SequenceEqual(new byte[] { 7, 8, 9, 10, 11, 12 })).IsTrue();
        await Assert.That(body[7]).IsEqualTo((byte)1); // helmet
        await Assert.That(body[8]).IsEqualTo((byte)0); // back_holdable
        await Assert.That(body[9]).IsEqualTo((byte)1); // cosplay
        await Assert.That(body[10]).IsEqualTo((byte)0); // cosplay_backpack

        // CSSpawnCharacterPacket.Read path consumes the payload verbatim.
        var roundTrip = new CharacterVisualOptions();
        roundTrip.Read(new PacketStream().Write(body));
        await Assert.That(roundTrip.Stp is not null && roundTrip.Stp.SequenceEqual(new byte[] { 7, 8, 9, 10, 11, 12 })).IsTrue();
        await Assert.That(roundTrip.Helmet).IsTrue();
        await Assert.That(roundTrip.BackHoldable).IsFalse();
        await Assert.That(roundTrip.Cosplay).IsTrue();
        await Assert.That(roundTrip.CosplayBackpack).IsFalse();
    }
}
