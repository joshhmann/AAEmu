#nullable enable

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots.Goap;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Autonomous homestead progression module driving starter bots to acquire an 8x8 scarecrow garden
/// plot, cultivate timber, craft material packs, and construct their homestead.
/// </summary>
public sealed class HomesteadActivityModule : IBotActivityModule
{
    public string Name => "Homestead";

    public int Priority { get; } = 60;

    /// <inheritdoc />
    public IReadOnlyList<string> DeclaredActivities => [ActivityName];

    /// <summary>Stable activity name (the arbiter's unit of change).</summary>
    public const string ActivityName = "homestead.progression";

    private readonly Func<ServerPressure> _pressureProbe;

    /// <summary>
    /// Opt-in fixture flag. Default is false (P1 corrective queue: ordinary starter bots
    /// must progress legitimately; unfinished homestead automation requires explicit opt-in).
    /// </summary>
    public bool Enabled { get; set; } = false;

    public HomesteadActivityModule(Func<ServerPressure>? pressureProbe = null)
    {
        _pressureProbe = pressureProbe ?? (() => ServerPressure.Healthy);
    }

    /// <inheritdoc />
    public BotActivityDecision CanActivate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Enabled)
            return BotActivityDecision.Deny("homestead progression disabled (opt-in fixture)");

        var character = context.Bot.Character;
        if (character.IsDead)
            return BotActivityDecision.Deny("bot is dead");

        if (character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        if (_pressureProbe() is { } pressure && pressure >= ServerPressure.High)
            return BotActivityDecision.Deny($"held: server pressure {pressure} (world-budget observance)");

        // If bot already completed full house construction, yield to ordinary living world loops
        var housing = HousingManager.PeekInstance;
        var ownHouses = housing?.GetAllHouses().Where(h => h.OwnerId == character.Id).ToList();
        if (ownHouses != null && ownHouses.Any(h => h.CurrentStep == -1 && h.Template != null && h.Template.MainModelId > 0))
        {
            return BotActivityDecision.Deny("homestead completed: home fully constructed");
        }

        // Drawn motivation: owning no plot at all is an unmet need this activity
        // exists to serve; continuing an already-started build is a weaker draw
        // that a band-mate can outbid.
        return BotActivityDecision.Allow(ActivityName,
            ownHouses is { Count: > 0 } ? BotActivityMotivation.Wants : BotActivityMotivation.Needs);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new BotActivity(ActivityName, Name);
    }
}
