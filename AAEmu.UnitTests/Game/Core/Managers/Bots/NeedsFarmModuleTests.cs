using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Tier 0 autonomous public-farm driver: the NeedsFarm module gate matrix +
/// the Plant proposal routing through the existing actor actions.
/// Fail-pre discipline: each deny case FAILS if the module allows, each
/// allow case FAILS if it denies or names the activity unstably.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class NeedsFarmModuleTests
{
    private static readonly Vector3 TestPosition = new(1000f, 1000f, 100f);

    // Own fixture ids (never collide with sibling-suite rig ids).
    private const uint SeedPackId = 88_103;
    private const uint FoodBuyItemId = 91_103;
    private const int FoodPrice = 25;

    private WorldConfig _previousWorldConfig;

    /// <summary>Worlds this test registered (drained identity-guarded in TearDown, cargo pattern).</summary>
    private readonly List<WorldInstance> _registeredWorlds = [];

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        CropHarvestLoopRig.Seed();
        GameplayActorTestRig.SeedTradeItemTemplate(FoodBuyItemId, FoodPrice, 0, false);
        SeedIsolatedMerchantPack();
        GameplayActorTestRig.SeedPlantSurface();

        // Doodad.Save() must fail FAST and deterministically headless: a dead
        // port turns the MySQL write into an immediate MySqlException instead
        // of a localhost:3306 attempt (the M3b plant-test convention) — the
        // engine call throws AFTER placement landed in-memory, which the
        // actor converts to Interrupted (effect applied, never Rejected).
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });

        GameplayActorTestRig.SetFarmGateEnabled(false);
        GameplayActorTestRig.SetFarmAllowlist(false);
    }

    [After(Test)]
    public void TearDown()
    {
        UnregisterWorlds();
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
        GameplayActorTestRig.SetFarmGateEnabled(false);
        GameplayActorTestRig.SetFarmAllowlist(false);
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    /// <summary>P1: identity-guarded unregister — never drop a sibling lane's same-id world.</summary>
    private void UnregisterWorlds()
    {
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        foreach (var world in _registeredWorlds)
        {
            if (worlds?.TryGetValue(world.Id, out var registered) == true && ReferenceEquals(registered, world))
                worlds.TryRemove(world.Id, out _);
        }
        _registeredWorlds.Clear();
    }

    private static void SeedIsolatedMerchantPack()
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var goodsField = typeof(NpcManager).GetField("Goods", Flags)
            ?? typeof(NpcManager).GetField("<Goods>k__BackingField", Flags)
            ?? throw new InvalidOperationException("Cannot locate NpcManager.Goods backing field");
        var goods = (Dictionary<uint, MerchantGoods>)goodsField.GetValue(NpcManager.Instance)!;
        if (!goods.TryGetValue(SeedPackId, out var pack))
        {
            pack = new MerchantGoods(SeedPackId);
            goods[SeedPackId] = pack;
        }
        pack.AddItemToStock(FoodBuyItemId, 0);
    }

    private static uint s_nextWorldId = 0xB000_0000;


    private (GameplayActor Actor, HeadlessSession Session) CreateActorOnUniqueWorld(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);

        var worldIdField = typeof(WorldInstance).GetField("<Id>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        worldIdField?.SetValue(session.World, s_nextWorldId++);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        if (worlds != null && !worlds.TryAdd(session.World.Id, session.World) && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held by a foreign world.");
        _registeredWorlds.Add(session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, session.World.Id);

        return (actor, session);
    }

    private static PlayerBotRuntime NewBot(GameplayActor actor)
        => new(actor.Character, "needs-farm-tests");

    private static BotActivityContext ContextFor(PlayerBotRuntime bot, string? active = null)
        => new() { Bot = bot, GameHour = 12f, ActiveActivity = active };

    private static NeedsFarmActivityModule EnabledModule(Func<ServerPressure>? pressure = null)
        => new(new NeedsFarmModuleOptions { Enabled = true }, pressure);

    // ------------------------------------------------------------ gate matrix

    [Test]
    public async Task Priority_Is55_BetweenRoamAndContest()
    {
        await Assert.That(new NeedsFarmActivityModule(new NeedsFarmModuleOptions()).Priority).IsEqualTo(55);
    }

    [Test]
    public async Task CanActivate_DisabledGate_Denies()
    {
        var module = new NeedsFarmActivityModule(new NeedsFarmModuleOptions { Enabled = false });
        var (actor, _) = CreateActorOnUniqueWorld("nf-off");
        GameplayActorTestRig.SetMoney(actor, 0);

        var decision = module.CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
    }

    [Test]
    public async Task CanActivate_DeadBot_Denies()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-dead");
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.Hp = 0;

        var decision = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("dead");
    }

    [Test]
    public async Task CanActivate_InBattle_Denies()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-battle");
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.IsInBattle = true;

        var decision = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("battle");
    }

    [Test]
    public async Task CanActivate_HighPressure_Denies()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-pressure");
        GameplayActorTestRig.SetMoney(actor, 0);

        var decision = EnabledModule(() => ServerPressure.High).CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("pressure");
    }

    [Test]
    public async Task CanActivate_StockedBot_DeniesAtRestLine()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-stocked");
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(actor, FoodBuyItemId, 5);

        var module = new NeedsFarmActivityModule(new NeedsFarmModuleOptions
        {
            Enabled = true,
            FoodItemTemplateId = FoodBuyItemId
        });

        var decision = module.CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("needs met");
    }

    [Test]
    public async Task CanActivate_BrokeBot_AllowsNamedActivity()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-broke");
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;

        var first = EnabledModule().CanActivate(ContextFor(NewBot(actor)));
        var second = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(first.CanActivate).IsTrue();
        await Assert.That(first.ActivityName).IsEqualTo(NeedsFarmActivityModule.ActivityName);
        await Assert.That(second.ActivityName).IsEqualTo(first.ActivityName);
    }

    [Test]
    public async Task Activate_ReturnsStableActivityIdentity()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-activate");
        var module = EnabledModule();
        var bot = NewBot(actor);

        var activity = module.Activate(ContextFor(bot));

        await Assert.That(activity.Name).IsEqualTo(NeedsFarmActivityModule.ActivityName);
        await Assert.That(activity.ModuleName).IsEqualTo("NeedsFarm");
    }

    [Test]
    public async Task Arbitrate_NeedsFarmBeatsPresenceRoam_BelowContest()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-arbiter");
        GameplayActorTestRig.SetMoney(actor, 0);
        var bot = NewBot(actor);
        var needs = EnabledModule();
        var roam = new PresenceRoamProbe();
        var contest = new ContestProbe();
        var arbiter = new BotGoalArbiter([contest, needs, roam]);

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.ModuleName).IsEqualTo("NeedsFarm");
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("needs.farm");
        await Assert.That(roam.Activations, "lower-priority roam must never activate").IsEqualTo(0);
        await Assert.That(contest.Activations, "denied contest must never activate").IsEqualTo(0);
    }

    private sealed class PresenceRoamProbe : IBotActivityModule
    {
        public string Name => "PresenceRoam";
        public int Priority => 50;
        public int Activations { get; private set; }
        public BotActivityDecision CanActivate(BotActivityContext context) => BotActivityDecision.Allow("presence.roam");
        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity("presence.roam", Name);
        }
    }

    private sealed class ContestProbe : IBotActivityModule
    {
        public string Name => "FishingContest";
        public int Priority => 60;
        public int Activations { get; private set; }
        public BotActivityDecision CanActivate(BotActivityContext context) => BotActivityDecision.Deny("contest not open");
        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity("fishing.contest", Name);
        }
    }

    // ------------------------------------------------------------ plant routing

    [Test]
    public async Task Run_SeedInBag_SelectsPlant()
    {
        var (actor, session) = CreateActorOnUniqueWorld("nf-plant-1");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        GameplayActorTestRig.SetFarmGateEnabled(true);
        GameplayActorTestRig.SetFarmAllowlist(true);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-plant-1",
            PlantSeedItemTemplateId = GameplayActorTestRig.TestSeedItemId,
            PlantPosition = TestPosition
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Plant);
        // Headless persistence boundary: the engine lands placement
        // in-memory (seed consumed, crop spawned) then throws at the MySQL
        // Save tail — Interrupted, never Rejected (B1 invariant).
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Interrupted);
    }

    [Test]
    public async Task Run_OffFarmPosition_RefusesPlant()
    {
        var (actor, session) = CreateActorOnUniqueWorld("nf-plant-offfarm");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        // Farm gate stays OFF (the SetUp default): TestPosition is not
        // public-farm soil, so the on-public-farm precondition refuses and
        // the decision falls through to rest.
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-plant-offfarm",
            PlantSeedItemTemplateId = GameplayActorTestRig.TestSeedItemId,
            PlantPosition = TestPosition
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Plant && r.Reason.Contains("on-public-farm"))).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
    }

    [Test]
    public async Task Run_DisallowedDoodad_RefusesPlant()
    {
        var (actor, session) = CreateActorOnUniqueWorld("nf-plant-deny");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        GameplayActorTestRig.SetFarmGateEnabled(true);
        GameplayActorTestRig.SetFarmAllowlist(false); // crop NOT on the Farm allowlist
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-plant-deny",
            PlantSeedItemTemplateId = GameplayActorTestRig.TestSeedItemId,
            PlantPosition = TestPosition
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Plant && r.Reason.Contains("farm-allows-doodad"))).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
    }

    [Test]
    public async Task Run_MatureCropAndNoSeed_SelectsHarvest()
    {
        var (actor, session) = CreateActorOnUniqueWorld("nf-harvest-1");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        var crop = PlantMatureCrop(actor, session);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-harvest-1",
            FoodItemTemplateId = CropHarvestLoopTests.PotatoItemId,
            HarvestDoodadObjId = crop.ObjId
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Harvest);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
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
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        return crop;
    }
}
