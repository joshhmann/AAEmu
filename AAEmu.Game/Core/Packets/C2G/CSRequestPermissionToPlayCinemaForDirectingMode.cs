using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSRequestPermissionToPlayCinemaForDirectingMode()
    : GamePacket(CSOffsets.CSRequestPermissionToPlayCinemaForDirectingMode, 1)
{
    public override void Read(PacketStream stream)
    {
        var questContextId = stream.ReadUInt32();
        var npcObjId = stream.ReadBc();
        var doodadObjId = stream.ReadBc();

        Logger.Debug("CSRequestPermissionToPlayCinemaForDirectingMode questContextId={0}", questContextId);

        // Directing-mode (scripted) quest cinemas come through here instead of CSStartedCinemaPacket,
        // and there is no SC "permission granted" packet in the protocol (this was an empty stub).
        // Firing OnCinemaStarted ensures the active QuestActObjCinema registers CurrentlyPlayingCinemaId
        // so that the subsequent CSCompletedCinemaPacket advances the quest objective rather than
        // leaving the player permanently frozen at the end of the cutscene.
        var character = Connection?.ActiveChar;
        if (character != null)
        {
            character.Events.OnCinemaStarted(character, new OnCinemaStartedArgs { CinemaId = character.CurrentlyPlayingCinemaId });
        }
    }
}
