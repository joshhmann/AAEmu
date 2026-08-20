using System.Net;
using System.Net.Sockets;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Network.Stream;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Network;

/// <summary>
/// Regression tests for the protocol-handler zero-progress receive loop.
/// PacketStream over-reads log-and-return-0 instead of throwing, so a 1-byte
/// remnant made packetLen == 0 and the parse loop never advanced — observed
/// in production 2026-08-20 as ~20k ERROR lines/sec on the stream port after
/// a stray LAN client half-closed.
/// </summary>
public class ProtocolHandlerSpinGuardTests
{
    private sealed class FakeSession : ISession
    {
        public bool Closed { get; private set; }
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null!;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null!;
        public void ClearAttribute(string name) { }
        public void Close() => Closed = true;
    }

    private static byte[] Bytes(params int[] values) =>
        values.Select(v => (byte)v).ToArray();

    [Test]
    public async Task StreamHandler_TruncatedRemnant_StashesAndReturns()
    {
        var session = new FakeSession();
        var connection = new StreamConnection(session);
        var handler = new StreamProtocolHandler();

        // Single dangling byte: not enough for a length word. Must stash and
        // return immediately (pre-fix: infinite loop on packetLen == 0).
        handler.OnReceive(connection, Bytes(0x05), 0, 1);

        await Assert.That(connection.LastPacket).IsNotNull();
        await Assert.That(connection.LastPacket!.Count).IsEqualTo(1);
        await Assert.That(session.Closed).IsFalse();
    }

    [Test]
    public async Task StreamHandler_TruncatedRemnant_CompletesOnNextSegment()
    {
        var session = new FakeSession();
        var connection = new StreamConnection(session);
        var handler = new StreamProtocolHandler();

        handler.OnReceive(connection, Bytes(0x02), 0, 1);
        // Second segment completes [len=2][type=0xFFFF]: a well-formed,
        // unknown-type packet that must be consumed without spinning.
        handler.OnReceive(connection, Bytes(0x00, 0xFF, 0xFF), 0, 3);

        await Assert.That(connection.LastPacket).IsNull();
        await Assert.That(session.Closed).IsFalse();
    }

    [Test]
    public async Task StreamHandler_GarbageLength_ClosesConnection()
    {
        var session = new FakeSession();
        var connection = new StreamConnection(session);
        var handler = new StreamProtocolHandler();

        // len=0: too small to hold a packet type — not this protocol.
        handler.OnReceive(connection, Bytes(0x00, 0x00, 0x00, 0x00), 0, 4);

        await Assert.That(session.Closed).IsTrue();
    }

    [Test]
    public async Task GameHandler_TruncatedRemnant_StashesAndReturns()
    {
        var session = new FakeSession();
        var connection = new GameConnection(session);
        var handler = new GameProtocolHandler();

        handler.OnReceive(connection, Bytes(0x04), 0, 1);

        await Assert.That(connection.LastPacket).IsNotNull();
        await Assert.That(connection.LastPacket!.Count).IsEqualTo(1);
        await Assert.That(session.Closed).IsFalse();
    }

    [Test]
    public async Task GameHandler_GarbageLength_ClosesConnection()
    {
        var session = new FakeSession();
        var connection = new GameConnection(session);
        var handler = new GameProtocolHandler();

        // len=1: too small for unk+level+type — not this protocol.
        handler.OnReceive(connection, Bytes(0x01, 0x00, 0x99, 0x88), 0, 4);

        await Assert.That(session.Closed).IsTrue();
    }
}
