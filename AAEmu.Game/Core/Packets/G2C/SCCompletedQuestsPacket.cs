using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Quests;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCCompletedQuestsPacket(CompletedQuest[] quests) : GamePacket(SCOffsets.SCCompletedQuestsPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(quests.Length); // TODO max 200
        foreach (var quest in quests)
        {
            var body = new byte[8];
            quest.Body.CopyTo(body, 0);

            // BUG-014: Id is uint server-side (block id = questId / 64 for quest
            // ids >= 4,194,304); the 1.2 client wire format is a ushort block id,
            // so cast at the boundary — the client wraps high block ids itself.
            stream.Write((ushort)quest.Id); // idx
            stream.Write(body); // body
        }
        return stream;
    }
}
