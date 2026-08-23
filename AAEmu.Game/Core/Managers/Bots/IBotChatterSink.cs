using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Send seam for bot chatter. Production uses <see cref="LocalChatChatterSink"/>
/// (the REAL local-chat path — the same broadcast the CSSendChatMessagePacket
/// "say" case performs, so human players actually see the lines in-game);
/// tests inject a recording fake.
/// </summary>
public interface IBotChatterSink
{
    /// <summary>Emits one line from a bot through the configured chat path.</summary>
    void Say(Character speaker, string message);
}

/// <summary>
/// The real local-chat path: proximity (say) broadcast from the speaking
/// character, byte-for-byte what a human player's ChatType.White message does.
/// </summary>
public sealed class LocalChatChatterSink : IBotChatterSink
{
    /// <inheritdoc />
    public void Say(Character speaker, string message)
        => speaker.BroadcastPacket(new SCChatMessagePacket(ChatType.White, speaker, message, 0, 0), true);
}
