using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Tier 0 needs-farm module options. Default OFF: the farm driver never
/// arbitrates unless the deployment opts in (env
/// <c>AAEMU_NEEDS_FARM_ENABLED=1</c>, mirroring
/// <see cref="FishingContestModuleOptions"/>).
/// </summary>
public sealed record NeedsFarmModuleOptions
{
    /// <summary>Master gate. Default OFF.</summary>
    public bool Enabled { get; init; }

    /// <summary>Urgency above which a needy bot farms instead of roaming (the scenario rest line).</summary>
    public double UrgencyThreshold { get; init; } = 0.2;

    /// <summary>Copper sufficiency line the gold need is measured against.</summary>
    public long GoldTarget { get; init; } = 1000;

    /// <summary>Bag-count line the food need is measured against.</summary>
    public int FoodTarget { get; init; } = 5;

    /// <summary>Bag-count line the lumber need is measured against.</summary>
    public int LumberTarget { get; init; } = 5;

    /// <summary>Labor scale the snapshot is measured against.</summary>
    public int LaborCap { get; init; } = BotNeedsEvaluator.DefaultLaborCap;

    /// <summary>Bag template counted as food (0 = food dimension ignored — treated as satisfied).</summary>
    public uint FoodItemTemplateId { get; init; }

    /// <summary>Bag template counted as lumber (0 = lumber dimension ignored — treated as satisfied).</summary>
    public uint LumberItemTemplateId { get; init; }

    /// <summary>Reads the deployment gate from the environment (default OFF).</summary>
    public static NeedsFarmModuleOptions FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED");
        return new NeedsFarmModuleOptions { Enabled = env is "1" or "true" or "True" };
    }
}

/// <summary>
/// Tier 0 autonomous public-farm driver, arbitration half: when the
/// deployment opts in and the bot has unmet needs (urgency above the
/// threshold), bots arbitrate into <c>needs.farm</c> instead of roaming.
/// Needs-driven, not personality-driven — no personality gating.
///
/// Arbitration rank 55: above baseline presence roam (50) so needy bots
/// farm instead of patrol, below fishing contest (60) so events still
/// preempt work, below conflict-join (75) so war still preempts play,
/// below schedule phases (100). Eligibility only — the needs leg itself
/// runs through <see cref="NeedsDecisionScenario"/> on the step path;
/// this module never moves, buys, plants, or harvests.
///
/// World-budget observance: denies at High/Critical pressure (needs work
/// never runs the world hot).
/// </summary>
public sealed class NeedsFarmActivityModule : IBotActivityModule
{
    public string Name => "NeedsFarm";

    public int Priority { get; } = 55;

    /// <summary>Stable activity name (the arbiter's unit of change).</summary>
    public const string ActivityName = "needs.farm";

    private readonly NeedsFarmModuleOptions _options;
    private readonly Func<ServerPressure> _pressureProbe;

    public NeedsFarmActivityModule(
        NeedsFarmModuleOptions options,
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
            return BotActivityDecision.Deny("needs farm disabled (NeedsFarmModuleOptions.Enabled off)");

        var character = context.Bot.Character;
        if (character.IsDead)
            return BotActivityDecision.Deny("bot is dead");

        if (character.IsInBattle)
            return BotActivityDecision.Deny("bot in battle");

        if (_pressureProbe() is { } pressure && pressure >= ServerPressure.High)
            return BotActivityDecision.Deny($"held: server pressure {pressure} (world-budget observance)");

        // Needs peek straight off the ordinary Character record — no temp
        // actor, no observation plumbing: the module seam must stay cheap
        // and side-effect free. Unconfigured food/lumber dimensions count
        // as satisfied (no signal); low urgency falls through to roam. The
        // at-or-below boundary matches the scenario rest line (urgency at
        // or below RestUrgency offers Rest alone).
        var snapshot = new BotNeedsSnapshot(
            Money: character.Money,
            LaborPower: character.LaborPower,
            LaborCap: _options.LaborCap,
            FoodCount: _options.FoodItemTemplateId == 0
                ? _options.FoodTarget
                : CountInBag(character, _options.FoodItemTemplateId),
            LumberCount: _options.LumberItemTemplateId == 0
                ? _options.LumberTarget
                : CountInBag(character, _options.LumberItemTemplateId));
        var needs = BotNeedsEvaluator.Evaluate(snapshot, new BotNeedsThresholds(
            GoldTarget: _options.GoldTarget,
            FoodTarget: _options.FoodTarget,
            LumberTarget: _options.LumberTarget,
            RestUrgency: _options.UrgencyThreshold));
        if (needs.Urgency <= _options.UrgencyThreshold)
            return BotActivityDecision.Deny($"needs met (urgency {needs.Urgency:F2} at or below {_options.UrgencyThreshold:F2})");

        return BotActivityDecision.Allow(ActivityName);
    }

    /// <inheritdoc />
    public BotActivity Activate(BotActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new BotActivity(ActivityName, Name);
    }

    private static int CountInBag(Character character, uint templateId)
    {
        if (character.Inventory?.Bag == null)
            return 0;
        return character.Inventory.GetItemsCount(SlotType.Inventory, templateId);
    }
}
