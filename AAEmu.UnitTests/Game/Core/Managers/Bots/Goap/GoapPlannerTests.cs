#nullable enable

using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;

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
    public async Task GoapPlanner_MultiStepWildFarmingChain_PlansLowestCostFoundSequence()
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

    [Test]
    public async Task GoapPlanner_ParetoDominance_PreservesResourceRichPathOverSlightlyCheaperPath()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty.WithLabor(100);
        var goal = new GoapGoal("CraftPack").WithCondition(BotWorldState.HasBuildingMaterials);

        // Path A: Low step cost (1.0), but burns 90 labor (leaves 10)
        var cheapExhausting = new ResourceConsumingAction("CheapExhaustingTravel", 1.0f, requiredLabor: 0, consumedLabor: 90)
            .WithEffect(BotWorldState.NearWorkbench);

        // Path B: Higher step cost (1.5), but burns 0 labor (leaves 100)
        var carefulTravel = new ResourceConsumingAction("CarefulTravel", 1.5f, requiredLabor: 0, consumedLabor: 0)
            .WithEffect(BotWorldState.NearWorkbench);

        // Action C: Requires NearWorkbench and 50 labor
        var heavyCraft = new ResourceConsumingAction("HeavyCraft", 2.0f, requiredLabor: 50, consumedLabor: 50)
            .WithPrecondition(BotWorldState.NearWorkbench)
            .WithEffect(BotWorldState.HasBuildingMaterials);

        var actions = new IGoapAction[] { cheapExhausting, carefulTravel, heavyCraft };
        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(2);
        // Path B was preserved by Pareto dominance despite higher GCost because it had more labor
        await Assert.That(result.Actions[0].Name).IsEqualTo("CarefulTravel");
        await Assert.That(result.Actions[1].Name).IsEqualTo("HeavyCraft");
        await Assert.That(result.TotalCost).IsEqualTo(3.5f);
    }

    [Test]
    public async Task GoapPlanner_SmallPlotConstruction_Uses10LaborBuildStep()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.NearHomeSite)
            .WithLabor(50);

        var goal = new GoapGoal("ConstructPlotGoal").WithCondition(BotWorldState.PlotConstructed);
        var constructPlot = new ConstructPlotAction();

        var result = planner.Plan(null, startState, goal, [constructPlot]);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(1);
        await Assert.That(result.Actions[0].Name).IsEqualTo("ConstructPlot");
    }

    [Test]
    public async Task GoapPlanner_TaxCertificateCrafting_Uses200LaborPlaqueCraft()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.NearHomeSite)
            .WithLabor(250);

        var goal = new GoapGoal("TaxSustainmentGoal").WithCondition(BotWorldState.HasTaxCertificates);
        var craftTax = new CraftTaxCertificatesAction();

        var result = planner.Plan(null, startState, goal, [craftTax]);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(1);
        await Assert.That(result.Actions[0].Name).IsEqualTo("CraftTaxCertificates");
    }

    [Test]
    public async Task GoapPlanner_IdenticalFlagsDifferentLabor_UnreachableForPoorBotReachableForRichBot()
    {
        var planner = new GoapPlanner();

        // Same flags, same action library; only labor differs. Pins that the planner honors
        // a labor-gated precondition from the start state instead of planning it regardless.
        ulong flags = BotWorldState.HasLandPlot | BotWorldState.NearHomeSite;
        var goal = new GoapGoal("ConstructPlotGoal").WithCondition(BotWorldState.PlotConstructed);
        var actions = new IGoapAction[] { new ConstructPlotAction() };

        var richStart = new BotWorldState(flags, ulong.MaxValue, labor: 50, gold: 0);
        var poorStart = new BotWorldState(flags, ulong.MaxValue, labor: 5, gold: 0);

        var richResult = planner.Plan(null, richStart, goal, actions);
        var poorResult = planner.Plan(null, poorStart, goal, actions);

        // If state identity were flags-only these two bot states would be the same node
        // and one result would leak into the other.
        await Assert.That(richResult.Success).IsTrue();
        await Assert.That(richResult.Actions[0].Name).IsEqualTo("ConstructPlot");
        await Assert.That(richResult.TotalCost).IsEqualTo(1.5f);

        await Assert.That(poorResult.Success).IsFalse();
        await Assert.That(poorResult.Actions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GoapPlanner_IdenticalFlagsDifferentGold_WithinSearch_PreservesGoldRichPath()
    {
        var planner = new GoapPlanner();

        // Two routes reach the SAME flags (NearWorkbench) with different remaining gold.
        // The flag-only-cheap route arrives first and spends everything; the goal step at
        // NearWorkbench needs gold. Flags-only dominance would prune the gold-rich route
        // and destroy the only affordable path.
        var startState = BotWorldState.Empty
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.NearHomeSite)
            .WithGold(500);

        var goal = new GoapGoal("SustainPlot").WithCondition(BotWorldState.HasTaxCertificates);

        var greedyRoute = new GoapActionBase("GreedyRoute", 1.0f)
            .WithPrecondition(BotWorldState.HasLandPlot)
            .WithGoldCost(500)
            .WithEffect(BotWorldState.NearWorkbench);

        var thriftyRoute = new GoapActionBase("ThriftyRoute", 3.0f)
            .WithPrecondition(BotWorldState.HasLandPlot)
            .WithGoldCost(0)
            .WithEffect(BotWorldState.NearWorkbench);

        var buyCertificate = new GoapActionBase("BuyTaxCertificate", 2.0f)
            .WithPrecondition(BotWorldState.NearWorkbench)
            .WithGoldPrecondition(100)
            .WithGoldCost(100)
            .WithEffect(BotWorldState.HasTaxCertificates);

        var actions = new IGoapAction[] { greedyRoute, thriftyRoute, buyCertificate };

        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(2);
        await Assert.That(result.Actions[0].Name).IsEqualTo("ThriftyRoute");
        await Assert.That(result.Actions[1].Name).IsEqualTo("BuyTaxCertificate");
        await Assert.That(result.TotalCost).IsEqualTo(5.0f);
    }

    [Test]
    public async Task GoapPlanner_IdenticalFlagsDifferentGold_UnreachableForPoorBotReachableForRichBot()
    {
        var planner = new GoapPlanner();

        ulong flags = BotWorldState.Empty.Flags;
        var goal = new GoapGoal("BuySeeds").WithCondition(BotWorldState.HasTreeSaplings);
        var buySeedlings = new GoapActionBase("BuySaplings", 1.0f)
            .WithPrecondition(BotWorldState.NearSeedMerchant)
            .WithGoldPrecondition(300)
            .WithGoldCost(300)
            .WithEffect(BotWorldState.HasTreeSaplings);
        var actions = new IGoapAction[] { buySeedlings };

        var richStart = BotWorldState.Empty.With(BotWorldState.NearSeedMerchant).WithGold(500);
        var poorStart = BotWorldState.Empty.With(BotWorldState.NearSeedMerchant).WithGold(100);

        var richResult = planner.Plan(null, richStart, goal, actions);
        var poorResult = planner.Plan(null, poorStart, goal, actions);

        await Assert.That(richResult.Success).IsTrue();
        await Assert.That(richResult.TotalCost).IsEqualTo(1.0f);

        await Assert.That(poorResult.Success).IsFalse();
        await Assert.That(poorResult.FailureReason).IsNotNull();
    }

    [Test]
    public async Task GoapPlanner_ResourceOnlyTransition_MidPlanLaborConsumptionFlipsFeasibility()
    {
        var planner = new GoapPlanner();

        // Flags are identical across both cases AND identical throughout the search:
        // the only thing separating a plan from a failure is labor consumed by an earlier step.
        var goal = new GoapGoal("BuildPlotGoal").WithCondition(BotWorldState.PlotConstructed);

        // Solo route to NearWorkbench, burning 40 labor on the way.
        var preparatoryStep = new ResourceConsumingAction("PrepareSite", 1.0f, requiredLabor: 0, consumedLabor: 40)
            .WithPrecondition(BotWorldState.HasLandPlot)
            .WithPrecondition(BotWorldState.NearHomeSite)
            .WithEffect(BotWorldState.NearWorkbench);

        // Needs 10 labor *after* PrepareSite has spent its 40.
        var buildPlot = new ResourceConsumingAction("BuildPlot", 1.5f, requiredLabor: 10, consumedLabor: 10)
            .WithPrecondition(BotWorldState.NearWorkbench)
            .WithEffect(BotWorldState.PlotConstructed);

        var actions = new IGoapAction[] { preparatoryStep, buildPlot };

        // 50 labor: PrepareSite burns 40, leaving exactly the 10 the second step needs.
        var affordable = BotWorldState.Empty
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.NearHomeSite)
            .WithLabor(50);

        // 49 labor: identical flags, but only 9 left after the same first step -> plan does not exist.
        var unaffordable = BotWorldState.Empty
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.NearHomeSite)
            .WithLabor(49);

        var affordableResult = planner.Plan(null, affordable, goal, actions);
        var unaffordableResult = planner.Plan(null, unaffordable, goal, actions);

        await Assert.That(affordableResult.Success).IsTrue();
        await Assert.That(affordableResult.Actions.Count).IsEqualTo(2);
        await Assert.That(affordableResult.Actions[0].Name).IsEqualTo("PrepareSite");
        await Assert.That(affordableResult.Actions[1].Name).IsEqualTo("BuildPlot");

        await Assert.That(unaffordableResult.Success).IsFalse();
        await Assert.That(unaffordableResult.Actions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GoapPlanner_UnaffordableActionPrecondition_FailsInsteadOfSilentlyDropping()
    {
        var planner = new GoapPlanner();
        var goal = new GoapGoal("CraftCertificates").WithCondition(BotWorldState.HasTaxCertificates);
        var craftTax = new CraftTaxCertificatesAction();

        var state = new BotWorldState(BotWorldState.HasLandPlot | BotWorldState.NearHomeSite, ulong.MaxValue, labor: 199, gold: 0);

        var result = planner.Plan(null, state, goal, [craftTax]);

        // 199 < 200 required: the action is not applicable, the goal is unsatisfiable,
        // and the planner says so rather than returning a plan it cannot execute.
        await Assert.That(craftTax.CheckPreconditions(state)).IsFalse();
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Actions.Count).IsEqualTo(0);
        await Assert.That(result.FailureReason).IsNotNull();

        var affordable = new BotWorldState(BotWorldState.HasLandPlot | BotWorldState.NearHomeSite, ulong.MaxValue, labor: 200, gold: 0);
        var affordableResult = planner.Plan(null, affordable, goal, [craftTax]);
        await Assert.That(affordableResult.Success).IsTrue();
        await Assert.That(affordableResult.Actions[0].Name).IsEqualTo("CraftTaxCertificates");
    }

    [Test]
    public async Task GoapPlanner_CompetingPaths_DifferentResourcesSelectDifferentAffordableRoute()
    {
        var planner = new GoapPlanner();

        // Two routes to the same goal: labor-bought vs gold-bought. Same flags start.
        ulong flags = BotWorldState.HasLandPlot | BotWorldState.NearHomeSite;
        var goal = new GoapGoal("SustainPlot").WithCondition(BotWorldState.HasTaxCertificates);

        var laborRoute = new CraftTaxCertificatesAction();
        var goldRoute = new GoapActionBase("BuyTaxCertificate", 5.0f)
            .WithPrecondition(BotWorldState.HasLandPlot)
            .WithPrecondition(BotWorldState.NearHomeSite)
            .WithGoldPrecondition(1000)
            .WithGoldCost(1000)
            .WithEffect(BotWorldState.HasTaxCertificates);

        var actions = new IGoapAction[] { laborRoute, goldRoute };

        var laborRich = new BotWorldState(flags, ulong.MaxValue, labor: 200, gold: 0);
        var goldRich = new BotWorldState(flags, ulong.MaxValue, labor: 0, gold: 1000);

        var laborResult = planner.Plan(null, laborRich, goal, actions);
        var goldResult = planner.Plan(null, goldRich, goal, actions);

        await Assert.That(laborResult.Success).IsTrue();
        await Assert.That(laborResult.Actions[0].Name).IsEqualTo("CraftTaxCertificates");

        await Assert.That(goldResult.Success).IsTrue();
        await Assert.That(goldResult.Actions[0].Name).IsEqualTo("BuyTaxCertificate");
    }

    [Test]
    public async Task GoapPlanner_ExpansionBound_AndLatencyAllocation_ReportedSeparately()
    {
        // The full cross-domain registry needs a wider budget than DefaultMaxExpansions
        // (see GoapPlanner_FullProgressionFromStarterToErectHome_ChainsAcrossDomains).
        var planner = new GoapPlanner(maxExpansions: 5000);
        var startState = BotWorldState.Empty.WithGold(100);
        var goal = GoalArbitrator.GoalErectHome;
        var actions = GoapActionRegistry.Instance.GetAllActions();

        // Warm the path/JIT before measuring.
        var warm = planner.Plan(null, startState, goal, actions);
        await Assert.That(warm.Success).IsTrue();

        const int iterations = 100;
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GoapPlanResult last = warm;
        for (var i = 0; i < iterations; i++)
            last = planner.Plan(null, startState, goal, actions);
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        await Assert.That(last.Success).IsTrue();

        // Expansion count is the deterministic search bound; measured latency and
        // allocation are recorded, not asserted — no budget is claimed for them here.
        await Assert.That(last.NodesExpanded).IsGreaterThan(0);
        await Assert.That(last.NodesExpanded).IsLessThanOrEqualTo(5000);

        Console.WriteLine($"[step4] plan(iterations={iterations}) nodesExpanded={last.NodesExpanded} " +
                          $"totalMs={sw.Elapsed.TotalMilliseconds:F2} perPlanUs={sw.Elapsed.TotalMilliseconds * 1000 / iterations:F1} " +
                          $"allocatedBytes={allocated} perPlanBytes={allocated / iterations}");
    }

    private sealed class ResourceConsumingAction : GoapActionBase
    {
        private readonly ushort _requiredLabor;
        private readonly ushort _consumedLabor;

        public ResourceConsumingAction(string name, float cost, ushort requiredLabor, ushort consumedLabor)
            : base(name, cost)
        {
            _requiredLabor = requiredLabor;
            _consumedLabor = consumedLabor;
        }

        public override bool CheckPreconditions(in BotWorldState currentState)
        {
            if (!base.CheckPreconditions(currentState)) return false;
            return currentState.Labor >= _requiredLabor;
        }

        public override BotWorldState ApplyEffects(in BotWorldState currentState)
        {
            var next = base.ApplyEffects(currentState);
            var remaining = currentState.Labor >= _consumedLabor ? (ushort)(currentState.Labor - _consumedLabor) : (ushort)0;
            return next.WithLabor(remaining);
        }
    }
}
