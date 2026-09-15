using System.Net;
using System.Net.Sockets;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj.Funcs;

/// <summary>
/// Mirage Island kiosk doodad 7983 (무인 창고) carries DoodadFuncAuctionUi 2
/// (skill 12083) + DoodadFuncBankUi 2 (skill 13238). These funcs must open the
/// same client state as the NPC right-click path (CSStartInteractionPacket:
/// banker → UseWarehouse, auctioneer → UseAuctioneer), which is just the
/// interaction skill list packet — there is no Character.UseWarehouse /
/// UseAuctioneer helper to call. Non-Character casters are a safe no-op
/// (Unit.SendPacket is connection-null-safe, and the funcs return early).
/// </summary>
public class DoodadFuncKioskUiTests
{
    private const uint KioskAuctionFuncId = 2;
    private const uint KioskBankFuncId = 2;
    private const uint KioskObjId = 0xD001;

    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;

        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private static (CharacterMock Character, PacketCaptureSession Capture) CreateCharacter()
    {
        var capture = new PacketCaptureSession();
        var character = new CharacterMock { Id = 1, ObjId = 0xB000, Name = "KioskTester" };
        character.Connection = new GameConnection(capture) { ActiveChar = character };
        return (character, capture);
    }

    private static Doodad CreateKiosk()
    {
        return new Doodad { ObjId = KioskObjId };
    }

    [Test]
    public async Task BankUi_CharacterCaster_OpensWarehouseLikeBankerNpc()
    {
        var (character, capture) = CreateCharacter();
        var kiosk = CreateKiosk();

        new DoodadFuncBankUi { Id = KioskBankFuncId }.Use(character, kiosk, SkillsEnum.UseWarehouse);

        // Byte-identical to what CSStartInteractionPacket sends for a banker NPC.
        byte[] expected = new SCNpcInteractionSkillListPacket(
            kiosk.ObjId, kiosk.ObjId, 0, 0, 0, 0, [SkillsEnum.UseWarehouse]).Encode();
        await Assert.That(capture.CapturedPackets.Count).IsEqualTo(1);
        await Assert.That(capture.CapturedPackets[0]).IsEquivalentTo(expected);
    }

    [Test]
    public async Task AuctionUi_CharacterCaster_OpensAuctionHouseLikeAuctioneerNpc()
    {
        var (character, capture) = CreateCharacter();
        var kiosk = CreateKiosk();

        new DoodadFuncAuctionUi { Id = KioskAuctionFuncId }.Use(character, kiosk, SkillsEnum.UseAuctioneer);

        // Byte-identical to what CSStartInteractionPacket sends for an auctioneer NPC.
        byte[] expected = new SCNpcInteractionSkillListPacket(
            kiosk.ObjId, kiosk.ObjId, 0, 0, 0, 0, [SkillsEnum.UseAuctioneer]).Encode();
        await Assert.That(capture.CapturedPackets.Count).IsEqualTo(1);
        await Assert.That(capture.CapturedPackets[0]).IsEquivalentTo(expected);
    }

    [Test]
    public async Task BankUi_NonCharacterCaster_IsSafeNoOp()
    {
        var (character, capture) = CreateCharacter();
        var kiosk = CreateKiosk();

        new DoodadFuncBankUi { Id = KioskBankFuncId }.Use(new Npc(), kiosk, SkillsEnum.UseWarehouse);

        await Assert.That(capture.CapturedPackets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AuctionUi_NonCharacterCaster_IsSafeNoOp()
    {
        var (character, capture) = CreateCharacter();
        var kiosk = CreateKiosk();

        new DoodadFuncAuctionUi { Id = KioskAuctionFuncId }.Use(new Npc(), kiosk, SkillsEnum.UseAuctioneer);

        await Assert.That(capture.CapturedPackets.Count).IsEqualTo(0);
    }
}
