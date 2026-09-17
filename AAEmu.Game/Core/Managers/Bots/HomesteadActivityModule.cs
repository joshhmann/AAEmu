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
        if (housing != null && housing.GetAllHouses().Any(h => h.OwnerId == character.Id && h.CurrentStep == -1 && h.Template != null && h.Template.MainModelId > 0))
        {
            return BotActivityDecision.Deny("homestead completed: home fully constructed");
        }

        return BotActivityDecision.Allow(ActivityName);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new BotActivity(ActivityName, Name);
    }
}
