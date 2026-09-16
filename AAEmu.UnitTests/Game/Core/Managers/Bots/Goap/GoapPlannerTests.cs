#nullable enable

using AAEmu.Game.Core.Managers.Bots.Goap;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.Goap;

public class GoapPlannerTests
{
    [Test]
    public async Task BotWorldState_BitFlags_SetAndSatisfiesCorrectly()
    {
        var state = BotWorldState.Empty
            .With(BotWorldState.NearSeedMerchant)
            .With(BotWorldState.HasTreeSaplings);

        await Assert.That(state.Has(BotWorldState.NearSeedMerchant)).IsTrue();
        await Assert.That(state.Has(BotWorldState.HasTreeSaplings)).IsTrue();
        await Assert.That(state.Has(BotWorldState.InCombat)).IsFalse();

        var req = BotWorldState.Empty.With(BotWorldState.NearSeedMerchant);
        await Assert.That(state.Satisfies(req)).IsTrue();

        var unsatisfiedReq = BotWorldState.Empty.With(BotWorldState.AtWildFarm);
        await Assert.That(state.Satisfies(unsatisfiedReq)).IsFalse();
    }

    [Test]
    public async Task BotWorldState_ResourceConstraints_Enforced()
    {
        var state = BotWorldState.Empty
            .WithLabor(50)
            .WithGold(100);

        var reqPass = BotWorldState.Empty.WithLabor(30).WithGold(50);
        var reqFailLabor = BotWorldState.Empty.WithLabor(60);
        var reqFailGold = BotWorldState.Empty.WithGold(200);

        await Assert.That(state.Satisfies(reqPass)).IsTrue();
        await Assert.That(state.Satisfies(reqFailLabor)).IsFalse();
        await Assert.That(state.Satisfies(reqFailGold)).IsFalse();
    }

    [Test]
    public async Task GoapPlanner_GoalAlreadySatisfied_ReturnsEmptySuccessPlan()
    {
        var planner = new GoapPlanner();
        var state = BotWorldState.Empty.With(BotWorldState.SecretGrovePlanted);
        var goal = new GoapGoal("PlantGrove").WithCondition(BotWorldState.SecretGrovePlanted);

        var result = planner.Plan(null, state, goal, []);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(0);
        await Assert.That(result.TotalCost).IsEqualTo(0f);
    }

    [Test]
    public async Task GoapPlanner_SingleStepAction_DiscoversPlan()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty.With(BotWorldState.HasTreeSaplings).With(BotWorldState.AtWildFarm);
        var goal = new GoapGoal("PlantGrove").WithCondition(BotWorldState.SecretGrovePlanted);

        var plantAction = new GoapActionBase("PlantDoodad", 2.0f)
            .WithPrecondition(BotWorldState.HasTreeSaplings)
            .WithPrecondition(BotWorldState.AtWildFarm)
            .WithEffect(BotWorldState.SecretGrovePlanted);

        var result = planner.Plan(null, startState, goal, [plantAction]);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(1);
        await Assert.That(result.Actions[0].Name).IsEqualTo("PlantDoodad");
        await Assert.That(result.TotalCost).IsEqualTo(2.0f);
    }

    [Test]
    public async Task GoapPlanner_MultiStepWildFarmingChain_PlansOptimalSequence()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty;
        var goal = new GoapGoal("SecretWildFarm").WithCondition(BotWorldState.SecretGrovePlanted);

        // Action library
        var travelToMerchant = new GoapActionBase("TravelToSeedMerchant", 1.5f)
            .WithEffect(BotWorldState.NearSeedMerchant);

        var buySaplings = new GoapActionBase("BuyTreeSaplings", 1.0f)
            .WithPrecondition(BotWorldState.NearSeedMerchant)
            .WithEffect(BotWorldState.HasTreeSaplings);

        var travelToMountain = new GoapActionBase("HikeToSecretPlateau", 3.0f)
            .WithEffect(BotWorldState.AtWildFarm);

        var plantGrove = new GoapActionBase("PlantSecretGrove", 2.0f)
            .WithPrecondition(BotWorldState.HasTreeSaplings)
            .WithPrecondition(BotWorldState.AtWildFarm)
            .WithEffect(BotWorldState.SecretGrovePlanted);

        // Unrelated distractor action
        var chopWood = new GoapActionBase("ChopWood", 5.0f)
            .WithPrecondition(BotWorldState.AtWildFarm)
            .WithEffect(BotWorldState.BagFull);

        var actions = new IGoapAction[]
        {
            chopWood,
            plantGrove,
            travelToMountain,
            buySaplings,
            travelToMerchant
        };

        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(4);

        // Sequence must have TravelToSeedMerchant -> BuyTreeSaplings -> HikeToSecretPlateau -> PlantSecretGrove
        await Assert.That(result.Actions[0].Name).IsEqualTo("TravelToSeedMerchant");
        await Assert.That(result.Actions[1].Name).IsEqualTo("BuyTreeSaplings");
        await Assert.That(result.Actions[2].Name).IsEqualTo("HikeToSecretPlateau");
        await Assert.That(result.Actions[3].Name).IsEqualTo("PlantSecretGrove");

        await Assert.That(result.TotalCost).IsEqualTo(7.5f);
    }

    [Test]
    public async Task GoapPlanner_CompetingPaths_SelectsCheaperCostRoute()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty;
        var goal = new GoapGoal("ReachWildFarm").WithCondition(BotWorldState.AtWildFarm);

        // Path A: Low cost road travel
        var roadTravel = new GoapActionBase("TravelViaRoadNetwork", 2.5f)
            .WithEffect(BotWorldState.AtWildFarm);

        // Path B: High cost rough terrain
        var roughTravel = new GoapActionBase("ClimbSteepCliff", 9.0f)
            .WithEffect(BotWorldState.AtWildFarm);

        var result = planner.Plan(null, startState, goal, [roughTravel, roadTravel]);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(1);
        await Assert.That(result.Actions[0].Name).IsEqualTo("TravelViaRoadNetwork");
        await Assert.That(result.TotalCost).IsEqualTo(2.5f);
    }

    [Test]
    public async Task GoapPlanner_ImpossibleGoal_FailsGracefullyWithoutInfiniteLoop()
    {
        var planner = new GoapPlanner(maxExpansions: 50);
        var startState = BotWorldState.Empty;
        var goal = new GoapGoal("Unreachable").WithCondition(BotWorldState.SecretGrovePlanted);

        // Action that requires an impossible condition
        var action = new GoapActionBase("UselessAction", 1.0f)
            .WithPrecondition(BotWorldState.InCombat)
            .WithEffect(BotWorldState.SecretGrovePlanted);

        var result = planner.Plan(null, startState, goal, [action]);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Actions.Count).IsEqualTo(0);
        await Assert.That(result.FailureReason).IsNotNull();
        await Assert.That(result.FailureReason!.Contains("No valid plan could satisfy the goal")).IsTrue();
    }
}
