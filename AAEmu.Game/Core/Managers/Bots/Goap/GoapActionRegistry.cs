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
    Homestead = 1 << 3,
    All = Survival | WildFarming | Combat | Homestead
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
    private readonly List<IGoapAction> _homesteadActions = [];
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

        // Homestead & Housing Progression
        Register(GoapDomain.Homestead, new AcquireScarecrowAction());
        Register(GoapDomain.Homestead, new TravelToHousingZoneAction());
        Register(GoapDomain.Homestead, new SurveyAndPlacePlotAction());
        Register(GoapDomain.Homestead, new TravelToHomeSiteAction());
        Register(GoapDomain.Homestead, new PlantOnPlotAction());
        Register(GoapDomain.Homestead, new HarvestTimberAction());
        Register(GoapDomain.Homestead, new TravelToWorkbenchAction());
        Register(GoapDomain.Homestead, new CraftMaterialPackAction());
        Register(GoapDomain.Homestead, new ConstructHomeAction());
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

        if ((domain & GoapDomain.Homestead) != 0)
            _homesteadActions.Add(action);
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

        if ((domain & GoapDomain.Homestead) != 0)
            result.AddRange(_homesteadActions);

        return result;
    }

    public IReadOnlyList<IGoapAction> SurvivalActions => _survivalActions;
    public IReadOnlyList<IGoapAction> WildFarmingActions => _wildFarmingActions;
    public IReadOnlyList<IGoapAction> CombatActions => _combatActions;
    public IReadOnlyList<IGoapAction> HomesteadActions => _homesteadActions;
    public IReadOnlyList<IGoapAction> AllActions => _allActions;
    public IReadOnlyList<IGoapAction> GetAllActions() => _allActions;
}
