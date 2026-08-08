using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Fail-closed DI default for <see cref="IPlayerBotLifecycleService"/>.
///
/// Until slice #3 (CharacterLifecycleService extraction) merges and is wired
/// as the lifecycle implementation, bot activation is REFUSED: the manager
/// records a failed activation instead of pretending the bot is embodied.
/// Nothing resolves the manager before the scheduler card lands, so this
/// placeholder is inert in production — it exists so DI composition is
/// complete and the failure mode is explicit, not accidental.
/// </summary>
public sealed class UnwiredPlayerBotLifecycleService : IPlayerBotLifecycleService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public bool ActivateHeadless(Character character, object? botContext)
    {
        Logger.Warn(
            "PlayerBot lifecycle not wired yet (slice #3 pending): refusing activation of character {CharacterId} ({CharacterName})",
            character.Id, character.Name);
        return false;
    }

    public bool Deactivate(Character character, string reason)
    {
        Logger.Warn(
            "PlayerBot lifecycle not wired yet (slice #3 pending): refusing deactivation of character {CharacterId} ({CharacterName})",
            character.Id, character.Name);
        return false;
    }
}
