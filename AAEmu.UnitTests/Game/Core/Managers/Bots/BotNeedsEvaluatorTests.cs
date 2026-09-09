using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// N1 needs slice (M9 substrate slice N1): pure <see cref="BotNeedsEvaluator"/>
/// (GoldNeed/LaborNeed/FoodNeed-proxy/LumberNeed/Urgency) + the
/// <see cref="NeedsDecisionScenario"/> composer routing Harvest-vs-Buy-vs-Rest
/// through EXISTING <see cref="GameplayActor"/> actions only.
///
/// Fail-pre discipline: every test FAILS on the corresponding slice defect
/// (missing need signal, stocked bot working, craft dispatched without labor,
/// nondeterministic choice, throw on empty input) and PASSES on the composed
/// real paths. No engine diffs, no tick service, no Personality reads, no new
/// columns, no CrimeManager/TrialManager touches.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class BotNeedsEvaluatorTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    // Own fixture ids (never collide with sibling-suite rig ids).
    private const uint FoodBuyItemId = 91_101;
    private const uint IsolatedPackId = 88_102;
    private const uint LumberItemId = 91_102;
    private const int FoodPrice = 30;
    private const long SeedMoney = 10_000;

    /// <summary>
    /// Seeds a merchant goods pack under an N1-only id (never the shared
    /// MerchantPackId — sibling suites must not observe our stock).
    /// </summary>
    private static void SeedIsolatedMerchantPack()
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var goodsField = typeof(NpcManager).GetField("Goods", Flags)
            ?? typeof(NpcManager).GetField("<Goods>k__BackingField", Flags)
            ?? throw new InvalidOperationException("Cannot locate NpcManager.Goods backing field");
        var goods = (Dictionary<uint, MerchantGoods>)goodsField.GetValue(NpcManager.Instance)!;
        if (!goods.TryGetValue(IsolatedPackId, out var pack))
        {
            pack = new MerchantGoods(IsolatedPackId);
            goods[IsolatedPackId] = pack;
        }
        pack.AddItemToStock(FoodBuyItemId, 0);
    }

    private static uint s_nextWorldId = 0xA000_0000; // fresh base: 0x90 village-day, 0x80 farmer, 0x7x economy/hauler, 0x6x craft/harvest, 0x5x plant/house, 0x4x rig

    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        CropHarvestLoopRig.Seed();
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task Run_BrokeBot_SelectsHarvestWork()
    {
        GameplayActorTestRig.SeedTradeItemTemplate(FoodBuyItemId, FoodPrice, 0, false);
        SeedIsolatedMerchantPack();
        var (actor, session) = CreateActorOnUniqueWorld("n1-broke-1");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1101, packId: IsolatedPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var crop = PlantMatureCrop(actor, session);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "n1-broke-1",
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = FoodBuyItemId,
            BuyCount = 1,
            BuyUnitPrice = FoodPrice,
            FoodItemTemplateId = CropHarvestLoopTests.PotatoItemId,
            HarvestDoodadObjId = crop.ObjId
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        // Broke (Money 0 → GoldNeed 1) with no affordable buy must work: the
        // harvest leg is the only legal work candidate.
        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Harvest);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Buy && r.Reason.Contains("funds-sufficient"))).IsTrue();
        await Assert.That(BagOnlyCount(actor, CropHarvestLoopTests.PotatoItemId)).IsGreaterThan(0);
    }

    [Test]
    public async Task Run_StockedBot_SelectsRest()
    {
        GameplayActorTestRig.SeedTradeItemTemplate(FoodBuyItemId, FoodPrice, 0, false);
        GameplayActorTestRig.RegisterPlainItemTemplate(LumberItemId);
        var (actor, _) = CreateActorOnUniqueWorld("n1-stocked-1");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, SeedMoney);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(actor, FoodBuyItemId, 5);
        GameplayActorTestRig.GrantItem(actor, LumberItemId, 5);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "n1-stocked-1",
            FoodItemTemplateId = FoodBuyItemId,
            LumberItemTemplateId = LumberItemId
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        // Every need satisfied (urgency 0) → Rest only: Stop through the
        // existing action, no balances moved, nothing rejected.
        await Assert.That(result.WorkSelected).IsFalse();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        await Assert.That(result.Rejections).IsEmpty();
        await Assert.That(actor.Character.Money).IsEqualTo(SeedMoney);
        await Assert.That(BagOnlyCount(actor, FoodBuyItemId)).IsEqualTo(5);
        await Assert.That(BagOnlyCount(actor, LumberItemId)).IsEqualTo(5);
    }

    [Test]
    public async Task Run_LowLaborCraftCandidate_RefusedFailClosed()
    {
        var (actor, session) = CreateActorOnUniqueWorld("n1-lowlabor-1");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SeedCraftSurface();
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        GameplayActorTestRig.GrantItem(actor, GameplayActorTestRig.CraftMaterialTemplateId, 2);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 5; // rig craft skill costs 10 labor
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "n1-lowlabor-1",
            CraftId = GameplayActorTestRig.CraftTestCraftId,
            CraftBenchObjId = benchObjId,
            CraftMaterialItemId = GameplayActorTestRig.CraftMaterialTemplateId,
            CraftMaterialAmount = 2,
            CraftLaborCost = 10
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        // The craft candidate is refused BEFORE dispatch (labor gate): no
        // Craft request exists, materials and money are untouched, and the
        // bot idles instead of starting a step that could never complete.
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Craft && r.Reason.Contains("labor-sufficient"))).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(result.TraceRecords.All(r => r.Action != ActorActionType.Craft)).IsTrue();
        await Assert.That(BagOnlyCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(2);
        await Assert.That(actor.Character.Money).IsEqualTo(0);
    }

    [Test]
    public async Task Run_SameStateTwice_IdenticalDecision()
    {
        GameplayActorTestRig.SeedTradeItemTemplate(FoodBuyItemId, FoodPrice, 0, false);
        SeedIsolatedMerchantPack();
        var (actor, session) = CreateActorOnUniqueWorld("n1-det-1");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, SeedMoney);
        actor.Character.LaborPower = 100;
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1101, packId: IsolatedPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "n1-det-1",
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = FoodBuyItemId,
            BuyCount = 1,
            BuyUnitPrice = FoodPrice,
            FoodItemTemplateId = FoodBuyItemId
        };

        var first = NeedsDecisionScenario.Run(actor, options);
        var second = NeedsDecisionScenario.Run(actor, options with { CycleId = "n1-det-2" });

        // Same policy on the same needs shape → the same routed action with
        // the same urgency, and both dispatches complete on the real path.
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Buy);
        await Assert.That(second.SelectedAction).IsEqualTo(first.SelectedAction);
        await Assert.That(second.Urgency).IsEqualTo(first.Urgency);
        await Assert.That(second.Explanation).IsEqualTo(first.Explanation);
        await Assert.That(first.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(second.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(SeedMoney - 2 * FoodPrice);
        await Assert.That(BagOnlyCount(actor, FoodBuyItemId)).IsEqualTo(2);

        // The pure evaluator is deterministic on identical input.
        var snapshot = new BotNeedsSnapshot(Money: SeedMoney, LaborPower: 100, FoodCount: 0, LumberCount: 0);
        await Assert.That(BotNeedsEvaluator.Evaluate(snapshot)).IsEqualTo(BotNeedsEvaluator.Evaluate(snapshot));
    }

    [Test]
    public async Task EvaluateAndRun_EmptyDefaults_NeverThrows()
    {
        var (actor, _) = CreateActorOnUniqueWorld("n1-empty-1");

        var needs = BotNeedsEvaluator.Evaluate(new BotNeedsSnapshot());
        var nullNeeds = BotNeedsEvaluator.Evaluate(null);

        await Assert.That(needs).IsEqualTo(nullNeeds);
        await Assert.That(needs.Urgency).IsEqualTo(1.0);

        // Default options configure no work target: every work candidate is
        // refused and the run idles instead of throwing.
        var result = NeedsDecisionScenario.Run(actor, null);

        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
    }

    // ------------------------------------------------------------ rig below

    private static (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);

        var worldIdField = typeof(WorldInstance).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        worldIdField?.SetValue(session.World, s_nextWorldId++);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, session.World.Id);

        return (actor, session);
    }

    private static Doodad PlantMatureCrop(GameplayActor actor, HeadlessSession session)
    {
        actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(actor.Character.Transform.World.Position);
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
        {
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        }
        return crop;
    }

    private static int BagOnlyCount(GameplayActor actor, uint templateId)
        => actor.Character.Inventory.GetItemsCount(SlotType.Inventory, templateId);
}
