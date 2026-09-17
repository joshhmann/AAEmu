using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSInstanceLoadedPacket() : GamePacket(CSOffsets.CSInstanceLoadedPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // Empty struct
        // TODO Debug

        // Ignore if there is no active character set
        if (Connection.ActiveChar == null)
            return;

        Connection.SendPacket(new SCUnitStatePacket(Connection.ActiveChar));
        // Connection.SendPacket(new SCCooldownsPacket(Connection.ActiveChar));
        Connection.SendPacket(new SCDetailedTimeOfDayPacket(12f));

        Connection.ActiveChar.DisabledSetPosition = false;

        WorldManager.ResendVisibleObjectsToCharacter(Connection.ActiveChar);

        Logger.Debug("InstanceLoaded.");
    }
}