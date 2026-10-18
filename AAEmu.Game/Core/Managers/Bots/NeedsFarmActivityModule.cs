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

    /// <summary>Opt-in hardbread supply through the existing public-farm loop; potato remains the default.</summary>
    public bool ProduceHardBread { get; init; }

    public const uint BarleySeedItemId = 15652;
    public const uint BarleyItemId = 8005;
    public const uint ProcessingKnifeItemId = 8326;
    public const uint ProcessedGrainItemId = 30902;
    public const uint HardBreadItemId = 17664;
    public const uint HardBreadCraftId = 6751;
    public const uint CookingDoodadId = 525;

    public uint SeedItemTemplateId => ProduceHardBread ? BarleySeedItemId : BotRoamStepExecutor.NeedsFarmSeedItemTemplateId;
    public uint HarvestItemTemplateId => ProduceHardBread ? BarleyItemId : BotRoamStepExecutor.NeedsFarmFoodItemTemplateId;
    public long SeedUnitPrice => ProduceHardBread ? 75 : BotRoamStepExecutor.NeedsFarmSeedUnitPrice;

    internal static int CountUnprotected(Character character, uint templateId, bool requireHealing = false)
    {
        var bag = character.Inventory?.Bag;
        if (bag == null)
            return 0;
        var count = 0;
        foreach (var item in bag.Items)
        {
            if (item.TemplateId == templateId && item.Template != null
                && !OutOfCombatRecoveryModule.IsProtectedRecoveryItem(character, item)
                && (!requireHealing || item.Template.LevelRequirement <= character.Level
                    && OutOfCombatRecoveryModule.IsSupportedHealthConsumable(item)))
                count += item.Count;
        }
        return count;
    }

    // Native reagent consumption may choose any matching stack. If any is
    // protected, a policy cannot authorize that ingredient by counting another.
    internal static bool HasProtectedIngredient(Character character, uint templateId)
    {
        var bag = character.Inventory?.Bag;
        if (bag == null)
            return true;
        foreach (var item in bag.Items)
        {
            if (item.TemplateId == templateId && OutOfCombatRecoveryModule.IsProtectedRecoveryItem(character, item))
                return true;
        }
        return false;
    }

    /// <summary>Urgency above which a needy bot farms instead of roaming (the scenario rest line).</summary>
    public double UrgencyThreshold { get; init; } = 0.2;

    /// <summary>Copper sufficiency line the gold need is measured against.</summary>
    public long GoldTarget { get; init; } = 1000;

    /// <summary>Bag-count line the food need is measured against.</summary>
    public int FoodTarget { get; init; } = 5;

    /// <summary>
    /// Identical farm-leg failures (same action, same detail) before the module
    /// stands aside (the T4 return path). One failure is a fact about one wake,
    /// not about the farm.
    /// </summary>
    public int LegFailureThreshold { get; init; } = 2;

    /// <summary>
    /// Elections the module stands aside for once that bound is reached. Bounded
    /// on purpose: a transient block must not disable the farm for the session.
    /// </summary>
    public int LegFailureStandAsideWakes { get; init; } = 20;

    /// <summary>Bag-count line the lumber need is measured against.</summary>
    public int LumberTarget { get; init; } = 5;

    /// <summary>Labor scale the snapshot is measured against.</summary>
    public int LaborCap { get; init; } = BotNeedsEvaluator.DefaultLaborCap;

    /// <summary>Bag template counted as food (0 = food dimension ignored — treated as satisfied).</summary>
    public uint FoodItemTemplateId { get; init; }

    /// <summary>Bag template counted as lumber (0 = lumber dimension ignored — treated as satisfied).</summary>
    public uint LumberItemTemplateId { get; init; }

    /// <summary>
    /// Reads the deployment gate: explicit env false always wins (default
    /// OFF); otherwise env true, otherwise the machine-local
    /// <c>Config.Local.json</c> opt-in. Shared <c>Config.json</c> is never
    /// consulted — a fleet-wide default must not switch farm bots on for
    /// every checkout.
    /// </summary>
    public static NeedsFarmModuleOptions FromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED");
        var breadEnv = Environment.GetEnvironmentVariable("AAEMU_FOOD_SUPPLY_ENABLED");
        var enabled = env is "1" or "true" or "True";
        var bread = breadEnv is "1" or "true" or "True";
        try
        {
            const string localConfig = "Config.Local.json";
            if (File.Exists(localConfig))
            {
                var text = File.ReadAllText(localConfig);
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("Bots", out var bots))
                {
                    if (env is not ("0" or "false" or "False" or "1" or "true" or "True")
                        && bots.TryGetProperty("EnableNeedsFarm", out var prop))
                        enabled = prop.ValueKind == System.Text.Json.JsonValueKind.True;
                    if (breadEnv == null && bots.TryGetProperty("ProduceHardBread", out var food))
                        bread = food.ValueKind == System.Text.Json.JsonValueKind.True;
                }
            }
        }
        catch
        {
        }
        return new NeedsFarmModuleOptions { Enabled = enabled, ProduceHardBread = bread };
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

    /// <inheritdoc />
    public IReadOnlyList<string> DeclaredActivities => [ActivityName];

    /// <summary>Stable activity name (the arbiter's unit of change).</summary>
    public const string ActivityName = "needs.farm";

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly NeedsFarmModuleOptions _options;
    private readonly Func<ServerPressure> _pressureProbe;

    // T4 return path (plan §7 T4, audit L3-b): this module's OWN leg, as the
    // executor reported it (the arbiter hands the fact to the module that owned
    // the leg, once). Identity is (action, detail): the same leg failing the same
    // way is the farm being blocked or exhausted, a different failure is a fresh
    // fact. The stand-aside is bounded by the options and dropped by a leg that
    // did not fail and by a lifecycle generation change.
    private sealed record LegFailure(ulong Generation, ActorActionType Action, string Detail, int Count, int StandAside);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, LegFailure> _legFailures = new();
    /// <summary>Read-only, generation-bound refusal for unchanged food-production facts.</summary>
    public Func<PlayerBotRuntime, string?>? FoodEligibilityProvider { get; init; }

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

        // T4 return path (plan §7 T4, audit L3-b): the leg this module's own
        // activity issued came back terminal. One failure is a fact about one
        // wake; the same failure twice means the farm is blocked or exhausted, so
        // the module stands aside for a bounded number of elections and denies
        // with the reason — the next band (roam) takes over instead of the same
        // farm wake repeating.
        if (StandAsideReason(context) is { } asideReason)
            return BotActivityDecision.Deny(asideReason);


        if (_options.ProduceHardBread)
        {
            if (FoodEligibilityProvider?.Invoke(context.Bot) is { } blocked)
                return BotActivityDecision.Deny(blocked);
            var bread = NeedsFarmModuleOptions.CountUnprotected(character, NeedsFarmModuleOptions.HardBreadItemId, requireHealing: true);
            return bread < _options.FoodTarget
                ? BotActivityDecision.Allow(ActivityName)
                : BotActivityDecision.Deny($"usable hardbread reserve met ({bread}/{_options.FoodTarget})");
        }
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

    /// <summary>
    /// The module's own terminal leg, as reported through the T4 return path.
    /// Returns the stand-aside reason while the farm is stepping aside, and null
    /// otherwise — no fact, a leg that did not fail (which clears the memory), or
    /// a failure that has not earned the bound yet (the farm is still tried).
    /// </summary>
    private string? StandAsideReason(BotActivityContext context)
    {
        var characterId = context.Bot.CharacterId;
        if (context.LastLegOutcome is not { } leg)
        {
            if (_legFailures.TryGetValue(characterId, out var standing) && standing.StandAside > 0)
            {
                var left = standing.StandAside - 1;
                if (left > 0)
                {
                    _legFailures[characterId] = standing with { StandAside = left };
                    return $"farm still standing aside after {standing.Count} failed legs ({standing.Detail}); {left} elections left";
                }

                _legFailures.TryRemove(characterId, out _);
                Logger.Info("Farm stand-aside over for bot {CharacterId}: trying the farm again after {Count} failed legs ({Detail})",
                    characterId, standing.Count, standing.Detail);
            }
            return null;
        }

        if (leg.State != ActorLifecycleState.Rejected)
        {
            // Only a hard refusal counts as the farm being blocked or exhausted.
            // A timed-out leg may still be recovered: the executor's own attempt
            // bookkeeping already surfaces that class through
            // FoodEligibilityProvider. A completed leg is progress and clears the
            // memory; an interrupted or expired one leaves the streak as it was.
            if (leg.State == ActorLifecycleState.Completed)
                _legFailures.TryRemove(characterId, out _);
            return null;
        }

        var detail = string.IsNullOrWhiteSpace(leg.Detail) ? leg.Action.ToString() : leg.Detail;
        var same = _legFailures.TryGetValue(characterId, out var saved)
            && saved.Generation == context.Bot.Generation
            && saved.Action == leg.Action
            && string.Equals(saved.Detail, detail, StringComparison.Ordinal);
        var count = same ? saved!.Count + 1 : 1;
        if (count < _options.LegFailureThreshold)
        {
            _legFailures[characterId] = new LegFailure(context.Bot.Generation, leg.Action, detail, count, 0);
            Logger.Debug("Farm leg failed bot {CharacterId}: {Action} ({Detail}) {Count}/{Threshold} — still farming",
                characterId, leg.Action, detail, count, _options.LegFailureThreshold);
            return null;
        }

        var standAside = _options.LegFailureStandAsideWakes;
        if (standAside <= 0)
        {
            _legFailures.TryRemove(characterId, out _);
            return null;
        }

        _legFailures[characterId] = new LegFailure(context.Bot.Generation, leg.Action, detail, count, standAside);
        var reason = $"farm leg failed {count} times the same way ({detail}) — standing aside for {standAside} elections";
        Logger.Warn("Farm stand-aside bot {CharacterId}: {Reason}", characterId, reason);
        return reason;
    }

    private static int CountInBag(Character character, uint templateId)
    {
        if (character.Inventory?.Bag == null)
            return 0;
        return character.Inventory.GetItemsCount(SlotType.Inventory, templateId);
    }
}
