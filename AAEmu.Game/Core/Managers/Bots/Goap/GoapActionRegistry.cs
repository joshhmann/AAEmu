#nullable enable

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

[Flags]
public enum GoapDomain
{
    None = 0,
    Survival = 1 << 0,
    WildFarming = 1 << 1,
    Combat = 1 << 2,
    All = Survival | WildFarming | Combat
}

/// <summary>
/// Central registry of available GOAP actions categorized by gameplay domain.
/// Supports domain-scoped action queries to preserve tight branching factor (b &lt;= 5).
/// </summary>
public sealed class GoapActionRegistry : Singleton<GoapActionRegistry>
{
    private readonly List<IGoapAction> _survivalActions = [];
    private readonly List<IGoapAction> _wildFarmingActions = [];
    private readonly List<IGoapAction> _combatActions = [];
    private readonly List<IGoapAction> _allActions = [];

    public GoapActionRegistry()
    {
        InitializeDefaultActions();
    }

    private void InitializeDefaultActions()
    {
        // Survival & Recovery
        Register(GoapDomain.Survival, new ConsumeFoodAction());
        Register(GoapDomain.Survival, new SitRestAction());
        Register(GoapDomain.Survival, new StandUpAction());
        Register(GoapDomain.Survival, new DrinkPotionAction());

        // Wild Farming & Forestry
        Register(GoapDomain.WildFarming, new TravelToSeedMerchantAction());
        Register(GoapDomain.WildFarming, new BuySaplingsAction());
        Register(GoapDomain.WildFarming, new HikeToWildFarmAction());
        Register(GoapDomain.WildFarming, new PlantWildSaplingAction());
        Register(GoapDomain.WildFarming, new ChopMatureTreeAction());

        // Tactical Combat & Threat Defense
        Register(GoapDomain.Combat, new AcquireHostileTargetAction());
        Register(GoapDomain.Combat, new ApproachTargetAction());
        Register(GoapDomain.Combat, new ExecuteCombatComboAction());
        Register(GoapDomain.Combat, new LootCorpseAction());
    }

    public void Register(GoapDomain domain, IGoapAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        _allActions.Add(action);

        if ((domain & GoapDomain.Survival) != 0)
            _survivalActions.Add(action);

        if ((domain & GoapDomain.WildFarming) != 0)
            _wildFarmingActions.Add(action);

        if ((domain & GoapDomain.Combat) != 0)
            _combatActions.Add(action);
    }

    public IReadOnlyList<IGoapAction> GetActions(GoapDomain domain = GoapDomain.All)
    {
        if (domain == GoapDomain.All)
            return _allActions;

        var result = new List<IGoapAction>();

        if ((domain & GoapDomain.Survival) != 0)
            result.AddRange(_survivalActions);

        if ((domain & GoapDomain.WildFarming) != 0)
            result.AddRange(_wildFarmingActions);

        if ((domain & GoapDomain.Combat) != 0)
            result.AddRange(_combatActions);

        return result;
    }

    public IReadOnlyList<IGoapAction> SurvivalActions => _survivalActions;
    public IReadOnlyList<IGoapAction> WildFarmingActions => _wildFarmingActions;
    public IReadOnlyList<IGoapAction> CombatActions => _combatActions;
    public IReadOnlyList<IGoapAction> AllActions => _allActions;
    public IReadOnlyList<IGoapAction> GetAllActions() => _allActions;
}
