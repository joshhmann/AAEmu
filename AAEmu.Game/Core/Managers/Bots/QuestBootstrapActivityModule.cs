using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest-bootstrap module options. Default OFF: quest pursuit never
/// arbitrates unless the deployment opts in (env
/// <c>AAEMU_QUEST_BOOTSTRAP_ENABLED=1</c>, mirroring
/// <see cref="NeedsFarmModuleOptions"/>).
/// </summary>
public sealed record QuestBootstrapModuleOptions
{
    /// <summary>Master gate. Default OFF.</summary>
    public bool Enabled { get; init; }

    /// <summary>Copper below which a quest-less bot still seeks work (the copper bootstrap).</summary>
    public long GoldTarget { get; init; } = 1000;

    /// <summary>Reads the deployment gate from the environment (default OFF).</summary>
    public static QuestBootstrapModuleOptions FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_QUEST_BOOTSTRAP_ENABLED");
        return new QuestBootstrapModuleOptions { Enabled = env is "1" or "true" or "True" };
    }
}

/// <summary>
/// Copper-bootstrap driver, arbitration half: when the deployment opts in
/// and the bot has quest work (active quests to advance/turn in, or no
/// copper to earn it with), bots arbitrate into <c>quest.progress</c>
/// instead of roaming.
///
/// Arbitration rank 58: above NeedsFarm (55) so a broke bot quests for
/// copper before the farm leg rests, below fishing contest (60) so events
/// still preempt work, below conflict-join (75), below schedules (100).
/// Eligibility only — the quest leg itself runs through
/// <see cref="QuestDecisionScenario"/> on the step path; this module never
/// accepts, advances, or turns in.
///
/// World-budget observance: denies at High/Critical pressure.
/// </summary>
public sealed class QuestBootstrapActivityModule : IBotActivityModule
{
    public string Name => "QuestBootstrap";

    public int Priority { get; } = 58;

    /// <summary>Stable activity name (the arbiter's unit of change).</summary>
    public const string ActivityName = "quest.progress";

    private readonly QuestBootstrapModuleOptions _options;
    private readonly Func<ServerPressure> _pressureProbe;

    public QuestBootstrapActivityModule(
        QuestBootstrapModuleOptions options,
        Func<ServerPressure>? pressureProbe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _pressureProbe = pressureProbe ?? (() => ServerPressure.Healthy);
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
            return BotActivityDecision.Deny("quest bootstrap disabled (QuestBootstrapModuleOptions.Enabled off)");

        var character = context.Bot.Character;
        if (character.IsDead)
            return BotActivityDecision.Deny("bot is dead");

        if (character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        if (_pressureProbe() is { } pressure && pressure >= ServerPressure.High)
            return BotActivityDecision.Deny($"held: server pressure {pressure} (world-budget observance)");

        // Quest work exists when quests are active (advance/turn-in) or the
        // bot is broke (discover + accept for copper). A rich quest-less bot
        // falls through to farm/roam.
        var activeCount = character.Quests?.ActiveQuests.Count ?? 0;
        if (activeCount == 0 && character.Money >= _options.GoldTarget)
            return BotActivityDecision.Deny(
                $"no active quests and copper {character.Money} at or above {_options.GoldTarget}");

        return BotActivityDecision.Allow(ActivityName);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new BotActivity(ActivityName, Name);
    }
}
