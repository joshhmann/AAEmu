using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Real lifecycle implementation for the player-bot seam (integration card
/// t_6bad0654 — replaces <see cref="UnwiredPlayerBotLifecycleService"/>).
///
/// Delegates embodiment/teardown to the shared
/// <see cref="ICharacterLifecycleService"/> (the exact core the human
/// CSSelectCharacterPacket path runs), then adds the ONE thing headless bots
/// need that human clients get for free: region-graph placement.
///
/// Why placement matters (PRESENCE PROOF): a human character becomes visible
/// to other clients when its first CSMoveUnitPacket arrives (Unit.cs
/// CheckMovedPosition → WorldManager.AddVisibleObject). A headless bot never
/// sends movement packets — without an explicit placement call it exists in
/// WorldManager._characters but in NO region, so real clients nearby never
/// receive its SCUnitStatePacket and the bot is invisible. This adapter
/// places the character into the region graph at activation, which is what
/// makes the bot appear in the live world (Option A visibility).
///
/// Composition rule (AGENTS.md #9/#10): no bot-only activation path — the
/// shared lifecycle core runs, then the standard visibility facility is
/// invoked exactly as movement would do it for a human.
/// </summary>
public sealed class PlayerBotLifecycleAdapter : IPlayerBotLifecycleService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly ICharacterLifecycleService _lifecycle;

    public PlayerBotLifecycleAdapter(ICharacterLifecycleService lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    /// <inheritdoc />
    public bool ActivateHeadless(Character character, object? botContext)
    {
        ArgumentNullException.ThrowIfNull(character);

        try
        {
            var ctx = botContext as BotContext ?? new BotContext { BotId = character.Id, Name = character.Name };
            _lifecycle.ActivateHeadless(character, ctx);

            // PRESENCE: place the character into the region graph so real
            // clients in the area receive SCUnitStatePacket (the headless
            // equivalent of the first CSMoveUnitPacket placement).
            WorldManager.Instance.AddVisibleObject(character);

            Logger.Info("PlayerBot embodied (real lifecycle): {CharacterName} (id {CharacterId}, objId {ObjId})",
                character.Name, character.Id, character.ObjId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "PlayerBot lifecycle activation failed: character {CharacterId} ({CharacterName})",
                character.Id, character.Name);
            return false;
        }
    }

    /// <inheritdoc />
    public bool Deactivate(Character character, string reason)
    {
        ArgumentNullException.ThrowIfNull(character);

        try
        {
            _lifecycle.Deactivate(character, CharacterLifecycleReason.Logout);
            Logger.Info("PlayerBot deactivated (real lifecycle): {CharacterName} (id {CharacterId}, reason {Reason})",
                character.Name, character.Id, reason);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "PlayerBot lifecycle deactivation failed: character {CharacterId} ({CharacterName})",
                character.Id, character.Name);
            return false;
        }
    }
}
