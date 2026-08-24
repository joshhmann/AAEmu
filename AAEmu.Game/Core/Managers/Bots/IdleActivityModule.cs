using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// G3-B3 third module: idle — the terminal fallback. Lowest priority: it
/// only wins when every higher module declined, in which case the bot's
/// route is cleared and it stands still (the executor then reports dormant,
/// so the bot stops waking until something explicitly wakes it again).
///
/// Never touches a bot mid-combat (denies alongside every other module →
/// the arbiter returns NoCandidate and leaves the world untouched).
/// </summary>
public sealed class IdleActivityModule : IBotActivityModule
{
    private readonly BotRoamStepExecutor _stepExecutor;

    public string Name => "Idle";
    public int Priority { get; } = 0;

    public IdleActivityModule(BotRoamStepExecutor stepExecutor)
    {
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Bot.Character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        return BotActivityDecision.Allow("idle");
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _stepExecutor.SetRoamRoute(context.Bot.Character, null);
        return new BotActivity("idle", Name);
    }
}
