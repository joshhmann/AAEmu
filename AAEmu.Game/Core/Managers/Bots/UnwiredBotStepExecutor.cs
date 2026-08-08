using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Fail-closed DI default for <see cref="IBotStepExecutor"/>.
///
/// Until the M5 actor (IGameplayActor) slice lands and is wired as the step
/// implementation, every step is a warn-once no-op that returns <c>null</c>
/// (dormant — the bot does not spin). Nothing wakes bots before the
/// PopulationDirector slice lands, so this placeholder is inert in
/// production; it exists so DI composition is complete and the failure mode
/// is explicit, not accidental.
/// </summary>
public sealed class UnwiredBotStepExecutor : IBotStepExecutor
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private int _warned;

    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Logger.Warn(
                "PlayerBot step executor not wired yet (M5 actor pending): bot {CharacterId} ({CharacterName}) wake is a no-op",
                bot.CharacterId, bot.Character.Name);
        }

        return Task.FromResult<TimeSpan?>(null);
    }
}
