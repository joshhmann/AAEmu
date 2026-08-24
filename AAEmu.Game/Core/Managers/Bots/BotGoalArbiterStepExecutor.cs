namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// G3-B3 wiring seam: the least invasive arbitration point — an
/// <see cref="IBotStepExecutor"/> DECORATOR. Every scheduler wake first runs
/// one goal-arbitration pass for the bot (<see cref="IBotGoalArbiter.Arbitrate"/>:
/// pick the single active activity, activate on change only), then delegates
/// the actual step to the inner executor
/// (<see cref="BotRoamStepExecutor"/> — the M5 actor/roam surface, unchanged).
///
/// Zero scheduler changes: the PlayerBotScheduler keeps consuming the same
/// <see cref="IBotStepExecutor"/> seam; DI swaps this decorator in as the
/// production binding. With ZERO registered modules the arbiter is inert and
/// this executor is a pure pass-through — byte-for-byte today's behavior.
/// </summary>
public sealed class BotGoalArbiterStepExecutor : IBotStepExecutor
{
    private readonly IBotGoalArbiter _arbiter;
    private readonly IBotStepExecutor _inner;
    private readonly Func<float> _gameHourProvider;

    public BotGoalArbiterStepExecutor(IBotGoalArbiter arbiter, IBotStepExecutor inner,
        Func<float>? gameHourProvider = null)
    {
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gameHourProvider = gameHourProvider ?? BotActivityGameClock.Hour;
    }

    /// <inheritdoc />
    public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bot);

        // One arbitration pass per wake. Steady state costs one CanActivate
        // probe per module and applies nothing; transitions arm routes once.
        // NoCandidate leaves the world untouched — the inner step still runs,
        // so any motion the bot already has simply continues.
        _arbiter.Arbitrate(bot, _gameHourProvider());

        return _inner.StepAsync(bot, cancellationToken);
    }
}
