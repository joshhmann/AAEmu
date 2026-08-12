using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSExecuteCraft() : GamePacket(CSOffsets.CSExecuteCraft, 1)
{
    public override void Read(PacketStream stream)
    {
        var craftId = stream.ReadUInt32();
        var objId = stream.ReadBc();
        var count = stream.ReadInt32();

        Logger.Debug("CSExecuteCraft, craftId : {0} , objId : {1}, count : {2}", craftId, objId, count);

        var character = Connection.ActiveChar;
        if (character == null)
            return;

        var craft = CraftManager.Instance.GetCraftById(craftId);
        if (craft == null)
        {
            // Invalid craftId from the client — never crash the packet handler
            Logger.Warn("CSExecuteCraft: unknown craftId {0} from {1}", craftId, character.Name);
            character.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.CraftInvalidCraftType, 0, false);
            return;
        }

        if (count <= 0)
        {
            Logger.Warn("CSExecuteCraft: invalid count {0} for craft {1} from {2}", count, craftId, character.Name);
            character.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.CraftInvalidAmount, 0, false);
            return;
        }

        if (character.Craft.IsCraftQueueActive)
        {
            // A second CSExecuteCraft while a queue is already running would
            // overwrite CurrentCraft/Count/DoodadId mid-queue — reject it.
            Logger.Warn("CSExecuteCraft: {0} tried to start craft {1} while already crafting", character.Name, craftId);
            return;
        }

        character.Craft.Craft(craft, count, objId);
    }
}
