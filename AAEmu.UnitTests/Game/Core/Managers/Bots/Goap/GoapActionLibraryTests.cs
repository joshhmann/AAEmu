#nullable enable

using AAEmu.Game.Core.Managers.Bots.Goap;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.Goap;

public class GoapActionLibraryTests
{
    [Test]
    public async Task GoapActionRegistry_InitializesWithDefaultDomains()
    {
        var registry = new GoapActionRegistry();

        await Assert.That(registry.SurvivalActions.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(registry.WildFarmingActions.Count).IsGreaterThanOrEqualTo(5);
        await Assert.That(registry.CombatActions.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(registry.AllActions.Count).IsGreaterThanOrEqualTo(13);
    }

    [Test]
    public async Task GoapPlanner_WildFarmingDomain_DiscoversAutonomousPlan()
    {
        var registry = new GoapActionRegistry();
        var planner = new GoapPlanner();

        var startState = BotWorldState.Empty
            .WithLabor(100)
            .WithGold(500);
        var goal = new GoapGoal("SecretWildFarm")
            .WithCondition(BotWorldState.SecretGrovePlanted);

        var actions = registry.GetActions(GoapDomain.WildFarming);
        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(4);

        // Sequence must have TravelToSeedMerchant -> BuyTreeSaplings -> HikeToSecretPlateau -> PlantSecretGrove
        await Assert.That(result.Actions[0].Name).IsEqualTo("TravelToSeedMerchant");
        await Assert.That(result.Actions[1].Name).IsEqualTo("BuyTreeSaplings");
        await Assert.That(result.Actions[2].Name).IsEqualTo("HikeToSecretPlateau");
        await Assert.That(result.Actions[3].Name).IsEqualTo("PlantSecretGrove");

        await Assert.That(result.TotalCost).IsGreaterThan(0f);
    }

    [Test]
    public async Task GoapPlanner_RecoveryDomain_DiscoversRestCycle()
    {
        var registry = new GoapActionRegistry();
        var planner = new GoapPlanner();

        // Bot is low on health and sitting down
        var startState = BotWorldState.Empty
            .With(BotWorldState.LowHealth)
            .With(BotWorldState.HasEdibleFood);

        // Goal: completely recovered and standing up
        var goal = new GoapGoal("RestAndRecover")
            .WithCondition(BotWorldState.Recovered);

        var actions = registry.GetActions(GoapDomain.Survival);
        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsGreaterThanOrEqualTo(1);

        // Must include SitDownToRegen to achieve Recovered
        await Assert.That(result.Actions.Any(a => a.Name == "SitDownToRegen")).IsTrue();
    }

    [Test]
    public async Task GoapPlanner_CombatDomain_DiscoversEngagementAndLootPlan()
    {
        var registry = new GoapActionRegistry();
        var planner = new GoapPlanner();

        var startState = BotWorldState.Empty;
        var goal = new GoapGoal("HuntAndLoot")
            .WithCondition(BotWorldState.HasLooted);

        var actions = registry.GetActions(GoapDomain.Combat);
        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(4);

        // AcquireHostileTarget -> ApproachTargetInRange -> ExecuteCombatCombo -> LootMonsterCorpse
        await Assert.That(result.Actions[0].Name).IsEqualTo("AcquireHostileTarget");
        await Assert.That(result.Actions[1].Name).IsEqualTo("ApproachTargetInRange");
        await Assert.That(result.Actions[2].Name).IsEqualTo("ExecuteCombatCombo");
        await Assert.That(result.Actions[3].Name).IsEqualTo("LootMonsterCorpse");
    }

    [Test]
    public async Task GoapPlanner_MultiDomainActionLibrary_FiltersAndPlansEfficiently()
    {
        var registry = new GoapActionRegistry();
        var planner = new GoapPlanner();

        var startState = BotWorldState.Empty
            .WithLabor(100)
            .WithGold(500);
        var goal = new GoapGoal("SecretWildFarm")
            .WithCondition(BotWorldState.SecretGrovePlanted);

        // All 13 actions available across all domains
        var actions = registry.GetAllActions();
        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(4);

        // Combat and recovery actions are ignored by heuristic steering
        await Assert.That(result.Actions[0].Name).IsEqualTo("TravelToSeedMerchant");
        await Assert.That(result.Actions[1].Name).IsEqualTo("BuyTreeSaplings");
        await Assert.That(result.Actions[2].Name).IsEqualTo("HikeToSecretPlateau");
        await Assert.That(result.Actions[3].Name).IsEqualTo("PlantSecretGrove");

        // Bounded search (well under 100 nodes expanded)
        await Assert.That(result.NodesExpanded).IsLessThan(100);
    }
}
