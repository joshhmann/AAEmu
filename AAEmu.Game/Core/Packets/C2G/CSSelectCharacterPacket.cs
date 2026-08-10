using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSelectCharacterPacket() : GamePacket(CSOffsets.CSSelectCharacterPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var characterId = stream.ReadUInt32();
        _ = stream.ReadBoolean(); // gm
        stream.ReadByte();

        if (Connection.Characters.TryGetValue(characterId, out var character))
        {
            // Character entry now lives in the shared lifecycle service
            // (ARCHITECTURE_REVIEW H3): Load → Connection bind → ObjId →
            // TryAddCharacter → Simulation → client state → buffs/HP/MP.
            // Human path is byte-identical to the pre-extraction body.
            CharacterLifecycleService.Instance.ActivateHuman(Connection, character);
        }
        else
        {
            // TODO: Character not found
            Logger.Error($"Character {characterId} not found in list of loaded characters of this account {Connection.AccountId}");
        }
    }
}
