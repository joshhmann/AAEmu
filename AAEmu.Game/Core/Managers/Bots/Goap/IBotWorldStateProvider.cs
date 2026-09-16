#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Projector interface translating rich AAEmu engine state, character data, and persistent memory
/// into the compact 64-bit BotWorldState used by the GOAP planner.
/// </summary>
public interface IBotWorldStateProvider
{
    /// <summary>
    /// Projects current game reality and memory context into a compressed BotWorldState.
    /// </summary>
    BotWorldState Project(PlayerBotRuntime bot, BotContext context);
}
