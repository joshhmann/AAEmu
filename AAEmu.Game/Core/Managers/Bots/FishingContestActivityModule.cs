using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M9.5 fishing-contest module options. Default OFF: the contest never
/// arbitrates unless the deployment opts in (env
/// <c>AAEMU_FISHING_CONTEST_ENABLED=1</c>, mirroring
/// <see cref="BotScheduleOptions"/>).
/// </summary>
public sealed record FishingContestModuleOptions
{
    /// <summary>Master gate. Default OFF.</summary>
    public bool Enabled { get; init; }

    /// <summary>Scheduled window open (inclusive, UTC).</summary>
    public DateTimeOffset OpensAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Scheduled window close (inclusive, UTC).</summary>
    public DateTimeOffset ClosesAt { get; init; } = DateTimeOffset.MaxValue;

    /// <summary>Reads the deployment gate from the environment (default OFF).</summary>
    public static FishingContestModuleOptions FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_FISHING_CONTEST_ENABLED");
        return new FishingContestModuleOptions { Enabled = env is "1" or "true" or "True" };
    }
}

/// <summary>
/// M9.5 fishing-contest activity module (B3: an activity IS a module per the
/// M9.5 Depends line) — the arbitration half of the locked launch activity.
/// While the scheduled contest window is open (and the world is cool), bots
/// arbitrate into <c>fishing.contest</c> instead of roaming; the event
/// itself runs through <see cref="FishingContestScenario"/>.
///
/// Arbitration rank 60: above baseline presence roam (50) so an open contest
/// draws bots off patrol, below conflict-join (75) so war still preempts
/// play, below schedule phases (100). Eligibility only — the module never
/// casts, never scores, never settles: cast driving stays in the scenario
/// composer through the existing <see cref="GameplayActor.CastAt"/> action.
///
/// World-budget observance: denies at High/Critical pressure (the event
/// never runs the world hot) alongside the scenario's G1 contestant ceiling.
/// </summary>
public sealed class FishingContestActivityModule : IBotActivityModule
{
    public string Name => "FishingContest";

    public int Priority { get; } = 60;

    /// <summary>Stable activity name (the arbiter's unit of change).</summary>
    public const string ActivityName = "fishing.contest";

    private readonly FishingContestModuleOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<ServerPressure> _pressureProbe;

    public FishingContestActivityModule(
        FishingContestModuleOptions options,
        Func<DateTimeOffset>? utcNow = null,
        Func<ServerPressure>? pressureProbe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _pressureProbe = pressureProbe ?? (() => ServerPressure.Healthy);
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
            return BotActivityDecision.Deny("fishing contest disabled (FishingContestModuleOptions.Enabled off)");

        if (context.Bot.Character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        if (_pressureProbe() is { } pressure && pressure >= ServerPressure.High)
            return BotActivityDecision.Deny($"held: server pressure {pressure} (world-budget observance)");

        var now = _utcNow();
        if (now < _options.OpensAt || now > _options.ClosesAt)
            return BotActivityDecision.Deny("contest not open");

        return BotActivityDecision.Allow(ActivityName);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new BotActivity(ActivityName, Name);
    }
}
