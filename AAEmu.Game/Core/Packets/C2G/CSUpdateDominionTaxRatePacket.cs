using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSUpdateDominionTaxRatePacket() : GamePacket(CSOffsets.CSUpdateDominionTaxRatePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var id = stream.ReadUInt16();
        var taxRate = stream.ReadInt32();

        Logger.Debug("UpdateDominionTaxRate, Id: {0}, TaxRate: {1}", id, taxRate);

        if (Connection.ActiveChar == null)
            return;

        if (!DominionManager.Instance.ChangeTaxRate(Connection.ActiveChar, id, taxRate, out var error))
            Connection.ActiveChar.SendErrorMessage(error);
    }
}
