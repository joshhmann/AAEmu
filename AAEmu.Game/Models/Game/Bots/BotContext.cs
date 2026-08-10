namespace AAEmu.Game.Models.Game.Bots;

/// <summary>
/// Bot identity context passed to ICharacterLifecycleService.ActivateHeadless.
/// Slice-3 minimal: identity only. Fidelity / scheduling / playerbot_*
/// metadata arrive with IPlayerBotManager (review slice 5) and IBotPersistence
/// (slice 7); this type grows there while the lifecycle contract stays stable.
/// </summary>
public sealed class BotContext
{
    /// <summary>Bot identity (managed account / playerbot registry id).</summary>
    public uint BotId { get; init; }

    /// <summary>Display name (also the character name).</summary>
    public string Name { get; init; } = string.Empty;
}
