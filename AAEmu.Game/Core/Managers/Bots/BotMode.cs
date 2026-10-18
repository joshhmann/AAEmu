namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The bot's top-level mode: the layer above activity choice. Every mode has
/// exactly one owner (see <c>player-ai-mode-layer.md</c>):
///   - <see cref="Dead"/> is owned by the scheduler's death watch, which skips
///     the arbiter entirely and resurrects the bot, so no module serves it;
///   - <see cref="Fighting"/> is owned by self-defence (critical HP is the
///     executor's survival gate, which runs before every activity);
///   - <see cref="Free"/> is where the ordinary activities compete.
/// Live run 11 (E325) is why this exists: with no Fighting owner the arbiter
/// elected nothing while the bot was being hit, and the fight never ended.
/// </summary>
public enum BotMode
{
    Free,
    Fighting,
    Dead,
}

public static class BotModes
{
    /// <summary>The modes the arbiter must always be able to fill.</summary>
    public static IReadOnlyList<BotMode> ArbiterModes { get; } = [BotMode.Free, BotMode.Fighting];
}
