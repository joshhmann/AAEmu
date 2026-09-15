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
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;
using Microsoft.Extensions.Time.Testing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;
using AAEmu.Game.Utils;
using System.Reflection;
using AAEmu.Game.Models.Game.CommonFarm;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using NeedsFarmLoopPhase = AAEmu.Game.Core.Managers.Bots.BotRoamStepExecutor.NeedsFarmLoopPhase;
using AAEmu.Game.GameData;

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


    // --------------------------------------- FromEnvironment precedence

    [Test]
    public async Task FromEnvironment_Unset_DefaultsOff()
    {
        var previous = Environment.GetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", null);
            await Assert.That(NeedsFarmModuleOptions.FromEnvironment().Enabled).IsFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", previous);
        }
    }

    [Test]
    public async Task FromEnvironment_ExplicitFalse_Wins()
    {
        var previous = Environment.GetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", "0");
            await Assert.That(NeedsFarmModuleOptions.FromEnvironment().Enabled).IsFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", previous);
        }
    }

    [Test]
    public async Task FromEnvironment_ExplicitTrue_OptsIn()
    {
        var previous = Environment.GetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", "1");
            await Assert.That(NeedsFarmModuleOptions.FromEnvironment().Enabled).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AAEMU_NEEDS_FARM_ENABLED", previous);
        }
    }
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
    // ------------------------------------------------------------ executor preemption (FIX 1)

    private static class NeedsPreemptionRig
    {
        internal const uint NeedsMerchantNpcTemplateId = 91_201;
        internal static uint NeedsMerchantPackId = 91_301;

        internal static void SeedNeedsMerchantPack()
        {
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var goodsField = typeof(NpcManager).GetField("Goods", Flags)
                ?? typeof(NpcManager).GetField("<Goods>k__BackingField", Flags)
                ?? throw new InvalidOperationException("Cannot locate NpcManager.Goods backing field");
            var goods = (Dictionary<uint, MerchantGoods>)goodsField.GetValue(NpcManager.Instance)!;
            if (!goods.TryGetValue(NeedsMerchantPackId, out var pack))
            {
                pack = new MerchantGoods(NeedsMerchantPackId);
                goods[NeedsMerchantPackId] = pack;
            }
            pack.AddItemToStock(BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 0);
        }

        internal static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateExecutor(
            (GameplayActor Actor, HeadlessSession Session) rigged,
            string activity,
            Func<Character, float, IEnumerable<Npc>>? nearbyNpcs = null,
            Func<Character, float, IEnumerable<Doodad>>? nearbyDoodads = null)
        {
            var (actor, _) = rigged;
            var runtime = new PlayerBotRuntime(actor.Character, "needs-preempt");
            var clock = new FakeTimeProvider();
            BotRoamStepExecutor executor = new()
            {
                ActorFactory = _ => actor,
                TimeProvider = clock,
                ActiveCadence = TimeSpan.FromMilliseconds(100),
                RoamSpeed = 2f,
                EnableWildlifeHunt = true,
                EnableWildlifeButcher = true,
                HuntScanInterval = TimeSpan.FromMilliseconds(100),
                ButcherScanInterval = TimeSpan.FromMilliseconds(100),
                HuntCastInterval = TimeSpan.FromMilliseconds(100),
                ActiveActivityProvider = _ => activity,
                NearbyNpcProvider = nearbyNpcs,
                NearbyDoodadProvider = nearbyDoodads,
                UnitResolver = (c, id) => c.ParentWorld?.GetUnit(id),
                DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
            };
            return (executor, actor, runtime, clock);
        }

        internal static Npc SpawnWildlife(GameplayActor actor, Vector3 position)
        {
            var wildlife = new Npc
            {
                ObjId = (uint)Random.Shared.Next(50_000, 60_000),
                Hp = 100,
                MaxHp = 100,
                Faction = new SystemFaction { Id = (FactionsEnum)115 }
            };
            wildlife.Transform.World.Position = position;
            return wildlife;
        }

        internal static Doodad SpawnOwnedCrop(GameplayActor actor, HeadlessSession session, Vector3 position)
        {
            var crop = new Doodad
            {
                TemplateId = CropHarvestLoopTests.PotatoDoodadId,
                OwnerType = DoodadOwnerType.Character,
                OwnerId = actor.Character.Id
            };
            crop.FuncGroupId = CropHarvestLoopTests.MaturePhase;
            crop.Transform = actor.Character.Transform.CloneDetached(crop);
            crop.IsPersistent = false;
            session.World.AddObject(crop);
            session.World.SpawnManager?.AddPlayerDoodad(crop);
            crop.Transform.Local.SetPosition(position);
            return crop;
        }
    }

    [Test]
    public async Task StepAsync_NeedsFarmActiveAndWorkCompletes_PreemptsWildlifeAcquisitionAndRoute()
    {
        // CASE 1: needs.farm active + a completable needs action (buy the
        // seed at an in-range merchant) + visible wildlife + an armed route
        // → needs executes (Buy lands), NO wildlife target is acquired, NO
        // roam leg is issued.
        var rigged = CreateActorOnUniqueWorld("nf-preempt-1");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var merchant = session.World.GetNpc(merchantObjId)!;
        var wildlife = NeedsPreemptionRig.SpawnWildlife(actor, TestPosition + new Vector3(2f, 0f, 0f));
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: (_, _) => [wildlife, merchant],
            nearbyDoodads: (_, _) => []);
        executor.SetRoamRoute(runtime.Character,
            new BotPath([TestPosition + new Vector3(50f, 0f, 0f)], BotPath.LoopMode.Loop));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.TargetNpcObjId ?? 0).IsEqualTo(0u);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move)).IsFalse();
    }

    [Test]
    public async Task StepAsync_NeedsFarmActive_PreemptsButcherAcquisition()
    {
        // CASE 2: needs.farm active + completable needs work (buy) +
        // butcherable livestock + an armed route → needs wins the wake: no
        // butcher target acquired, no Interact issued, no roam leg issued.
        LivestockInteractionRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-preempt-2");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var merchant = session.World.GetNpc(merchantObjId)!;
        var cow = new Doodad
        {
            TemplateId = LivestockInteractionTests.DairyCalfDoodadId,
            OwnerType = DoodadOwnerType.Character,
            OwnerId = actor.Character.Id,
            IsPersistent = false
        };
        cow.FuncGroupId = LivestockInteractionTests.CowPhase;
        cow.Transform = actor.Character.Transform.CloneDetached(cow);
        cow.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        session.World.AddObject(cow);
        session.World.SpawnManager?.AddPlayerDoodad(cow);
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: (_, _) => [merchant],
            nearbyDoodads: (_, _) => [cow]);
        executor.SetRoamRoute(runtime.Character,
            new BotPath([TestPosition + new Vector3(50f, 0f, 0f)], BotPath.LoopMode.Loop));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.TargetButcherDoodadObjId ?? 0).IsEqualTo(0u);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Interact)).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move)).IsFalse();
    }

    [Test]
    public async Task StepAsync_NoNeedsActivity_WildlifeButcherAndRouteUnchanged()
    {
        // CASE 3: no needs activity → the opportunistic legs behave exactly
        // as before: wildlife is acquired and the route still issues legs
        // when nothing competes.
        var rigged = CreateActorOnUniqueWorld("nf-preempt-3");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var wildlife = NeedsPreemptionRig.SpawnWildlife(actor, TestPosition + new Vector3(2f, 0f, 0f));
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "presence.roam",
            nearbyNpcs: (_, _) => [wildlife],
            nearbyDoodads: (_, _) => []);
        executor.SetRoamRoute(runtime.Character,
            new BotPath([TestPosition + new Vector3(50f, 0f, 0f)], BotPath.LoopMode.Loop));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.Character.CurrentTarget).IsNotNull();
        await Assert.That(actor.Character.CurrentTarget!.ObjId).IsEqualTo(wildlife.ObjId);
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.NeedsLegActive ?? true).IsFalse();
    }

    [Test]
    public async Task StepAsync_ConflictActivity_PreemptsNeedsFarm()
    {
        // CASE 4: a higher-priority conflict activity preempts needs — the
        // needs branch never runs (no Buy) while PvP engages the hostile.
        GameplayActorTestRig.ForceSeedTeamManager();
        var rigged = CreateActorOnUniqueWorld("nf-preempt-4");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var (foe, foeSession) = GameplayActorTestRig.CreateActor("nf-preempt-4-foe");
        GameplayActorTestRig.JoinActorWorld(session, foe);
        GameplayActorTestRig.SetPosition(foe, TestPosition + new Vector3(2f, 0f, 0f));
        var runtime = new PlayerBotRuntime(actor.Character, "needs-preempt");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            EnableWildlifeHunt = true,
            ActiveActivityProvider = _ => "conflict.23163",
            CanAttackPlayer = (_, _) => true,
            NearbyCharacterProvider = (_, _) => [foe.Character],
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
            UnitResolver = (c, id) => c.ParentWorld?.GetUnit(id),
            DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
        };
        _ = foeSession;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy)).IsFalse();
        await Assert.That(actor.Character.CurrentTarget).IsNotNull();
        await Assert.That(actor.Character.CurrentTarget!.ObjId).IsEqualTo(foe.Character.ObjId);
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.NeedsLegActive ?? true).IsFalse();
    }

    // ------------------------------------------------------------ bounded discovery (FIX 2)

    [Test]
    public async Task StepAsync_NeedsCropOutsidePerceptionRange_NotSelected()
    {
        // Production no-provider path with a crop beyond the perception
        // radius → bounded discovery drops it: no harvest dispatches.
        var rigged = CreateActorOnUniqueWorld("nf-bound-1");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        var far = NeedsPreemptionRig.SpawnOwnedCrop(actor, session,
            TestPosition + new Vector3(BotRoamStepExecutor.NeedsFarmPerceptionRadius + 20f, 0f, 0f));
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: (_, _) => [],
            nearbyDoodads: null); // null → production bounded path
        _ = far;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Harvest)).IsFalse();
    }

    [Test]
    public async Task StepAsync_NeedsCropInsidePerceptionRange_SelectsHarvest()
    {
        // Same no-provider path with the crop inside the radius → the
        // harvest dispatches and completes through the real engine path.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-bound-2");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 0;
        actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: (_, _) => [],
            nearbyDoodads: null); // null → production bounded path

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Harvest
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task StepAsync_NeedsMerchantOutsidePerceptionRange_NotSelected()
    {
        // A seed merchant beyond the perception radius → bounded discovery
        // never resolves it: no Buy dispatches (the rest Stop lands instead).
        var rigged = CreateActorOnUniqueWorld("nf-bound-3");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId,
            TestPosition + new Vector3(BotRoamStepExecutor.NeedsFarmPerceptionRadius + 20f, 0f, 0f));
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: null, // null → production bounded path
            nearbyDoodads: (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy)).IsFalse();
    }

    [Test]
    public async Task StepAsync_NeedsMerchantInsidePerceptionRange_SelectsBuy()
    {
        // The same merchant inside the radius and in shop range → the buy
        // dispatches and completes through the real engine path.
        var rigged = CreateActorOnUniqueWorld("nf-bound-4");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: null, // null → production bounded path
            nearbyDoodads: (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task StepAsync_NeedsDiscoveryNoProvider_UsesBoundedCandidateSet()
    {
        // The no-provider production path hands the leg a bounded candidate
        // set, not the whole world: a far merchant + a far crop coexist with
        // an in-range merchant, and the leg resolves the NEAR one while the
        // far candidates never surface (asserted through the world lookups
        // the engine itself validates at dispatch).
        var rigged = CreateActorOnUniqueWorld("nf-bound-5");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var nearObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, nearObjId, TestPosition);
        var farObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId + 1,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, farObjId,
            TestPosition + new Vector3(BotRoamStepExecutor.NeedsFarmPerceptionRadius + 20f, 0f, 0f));
        var farCrop = NeedsPreemptionRig.SpawnOwnedCrop(actor, session,
            TestPosition + new Vector3(BotRoamStepExecutor.NeedsFarmPerceptionRadius + 30f, 0f, 0f));
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: null, nearbyDoodads: null); // null → production bounded path
        _ = farCrop;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var buys = actor.AuditTrace.Where(r => r.Action == ActorActionType.Buy).ToList();
        await Assert.That(buys).IsNotEmpty();
        await Assert.That(buys.All(r => r.TargetId == nearObjId)).IsTrue();
        await Assert.That(buys.Any(r => r.TargetId == farObjId)).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Harvest)).IsFalse();
    }

    // ------------------------------------------------------------ travel-to-soil + maturity-wait/RESUME

    private static (BotRoamStepExecutor Executor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) FarmLoopRig(
        (GameplayActor Actor, HeadlessSession Session) rigged,
        Func<Character, Vector3, bool> soil,
        Func<Character, float, IEnumerable<Doodad>>? doodads = null)
    {
        var (actor, _) = rigged;
        var runtime = new PlayerBotRuntime(actor.Character, "needs-farm-loop");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => "needs.farm",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = doodads,
            FarmSoilProvider = soil,
            UnitResolver = (c, id) => c.ParentWorld?.GetUnit(id),
            DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
        };
        return (executor, runtime, clock);
    }

    private static void StockLoopSeed((GameplayActor Actor, HeadlessSession Session) rigged)
        => GameplayActorTestRig.GrantItem(rigged.Actor,
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId, 3);

    private static void SeedLoopPotatoAllowlist()
    {
        // The executor plants the CANONICAL potato seed (15659 → doodad
        // 2259): IsValidFarmSoil reads the live CommonFarmGameData
        // allowlist for Farm, which the TestSeed (93001) helper never
        // touches. Missing-only additive — id-keyed so TearDown-safe.
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var data = CommonFarmGameData.Instance;
        var field = typeof(CommonFarmGameData).GetField("_farmGroupDoodads", flags)
            ?? throw new InvalidOperationException("Cannot locate CommonFarmGameData._farmGroupDoodads");
        var doodads = (Dictionary<uint, FarmGroupDoodads>)field.GetValue(data)!;
        if (doodads == null)
        {
            doodads = [];
            field.SetValue(data, doodads);
        }
        const uint LoopKey = 0x9F10_0001;
        if (!doodads.TryGetValue(LoopKey, out var row)
            || row.FarmGroupId != FarmType.Farm
            || row.DoodadId != CropHarvestLoopTests.PotatoDoodadId)
        {
            doodads[LoopKey] = new FarmGroupDoodads
            {
                Id = LoopKey,
                FarmGroupId = FarmType.Farm,
                DoodadId = CropHarvestLoopTests.PotatoDoodadId,
                ItemId = CropHarvestLoopTests.PotatoSeedItemId
            };
        }
        var farmZones = (Dictionary<uint, FarmType>)GameplayActorTestRig.GetField(
            PublicFarmManager.Instance, "_farmZones");
        if (!farmZones.ContainsKey(GameplayActorTestRig.TestFarmSubZoneId))
            farmZones[GameplayActorTestRig.TestFarmSubZoneId] = FarmType.Farm;
    }

    [Test]
    public async Task StepAsync_SeedPresentOffSoil_ArmsRouteToSoil_NoTeleport()
    {
        // Seed present + off-soil → the route layer arms a BotPath to a
        // resolved soil destination. The route layer walks on the SAME wake
        // (branch 2 issues the MoveTo leg after the needs leg returns false,
        // and the actor tick advances it) — so the bot must have moved TOWARD
        // soil by a small bounded step (never teleported: displacement ≪
        // the 10 m to soil), and no Plant dispatches off-soil.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-travel-1");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        StockLoopSeed(rigged);
        GameplayActorTestRig.SetFarmGateEnabled(true);
        SeedLoopPotatoAllowlist();
        var soil = TestPosition + new Vector3(10f, 0f, 0f);
        var (executor, runtime, clock) = FarmLoopRig(rigged,
            (c, p) => MathUtil.CalculateDistance(p, soil, false) <= 1f,
            (_, _) => []);
        var before = actor.Character.Transform.World.Position;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Traveling);
        await Assert.That(state.NeedsFarmSoilTarget).IsNotNull();
        var route = executor.GetRoamRoute(runtime.CharacterId);
        await Assert.That(route).IsNotNull();
        await Assert.That(MathUtil.CalculateDistance(route!.CurrentTarget, soil, false) <= 1f).IsTrue();
        var moved = MathUtil.CalculateDistance(before, actor.Character.Transform.World.Position, false);
        await Assert.That(moved > 0f && moved < 5f).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Plant)).IsFalse();
    }

    [Test]
    public async Task StepAsync_TravelArrival_PlantsNormally()
    {
        // Armed soil route + arrival (position AT soil) → patrol restored,
        // plant dispatches through the normal decision path.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-travel-2");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        StockLoopSeed(rigged);
        GameplayActorTestRig.SetFarmGateEnabled(true);
        SeedLoopPotatoAllowlist();
        var soil = TestPosition + new Vector3(10f, 0f, 0f);
        var (executor, runtime, clock) = FarmLoopRig(rigged,
            (c, p) => MathUtil.CalculateDistance(p, soil, false) <= 1f,
            (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        // Arrival: stop the walk where it is NOT (the route leg is live),
        // then stand the bot on soil and clear the spent route the way
        // branch 3b would on arrival.
        if (actor.ActiveRequest is { IsTerminal: false })
            _ = actor.Stop();
        GameplayActorTestRig.SetPosition(actor, soil);
        executor.SetRoamRoute(actor.Character, null);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Plant)).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.NeedsFarmSoilTarget).IsNull();
    }

    [Test]
    public async Task StepAsync_StaleSoilDestination_DiscardsAndReResolvesBoundedly()
    {
        var rigged = CreateActorOnUniqueWorld("nf-travel-3");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        StockLoopSeed(rigged);
        var soil = TestPosition + new Vector3(10f, 0f, 0f);
        var (executor, runtime, clock) = FarmLoopRig(rigged, (c, p) => false, (_, _) => []);
        executor.SetRoamRoute(actor.Character, BotPath.PathTo(soil));
        executor.GetBotState(runtime.CharacterId)!.NeedsFarmSoilTarget = soil;
        for (var i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);
        }

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmSoilAttempts <= BotRoamStepExecutor.NeedsFarmMaxSoilAttempts + 1).IsTrue();
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.SeekingSoil);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Plant)).IsFalse();
    }

    [Test]
    public async Task StepAsync_NoFarmNearby_BoundedDefer_NoSpin()
    {
        var rigged = CreateActorOnUniqueWorld("nf-travel-4");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        StockLoopSeed(rigged);
        var probes = 0;
        var (executor, runtime, clock) = FarmLoopRig(rigged,
            (c, p) => { probes++; return false; }, (_, _) => []);
        for (var i = 0; i < 25; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);
        }

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.SeekingSoil);
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Plant or ActorActionType.Move)).IsFalse();
        await Assert.That(probes < 25 * 73).IsTrue();
    }

    [Test]
    public async Task StepAsync_PlantedImmature_Defers_NoHarvestIssued()
    {
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-wait-1");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        var (executor, runtime, clock) = FarmLoopRig(rigged, (c, p) => true, (_, _) => [crop]);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);
        }

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Harvest)).IsFalse();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.WaitingMaturity);
        await Assert.That(state.NeedsFarmCropObjId).IsEqualTo(crop.ObjId);
    }

    [Test]
    public async Task StepAsync_CropVanished_StaleDropped_ReEvaluates()
    {
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-wait-2");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        List<Doodad> crops = [crop];
        var (executor, runtime, clock) = FarmLoopRig(rigged, (c, p) => true, (_, _) => crops);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        session.World.RemoveObject(crop);
        crops.Clear(); // scan no longer sees it either — gone means gone
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)!.NeedsFarmCropObjId).IsEqualTo(0u);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Harvest)).IsFalse();
    }

    [Test]
    public async Task StepAsync_MatureCrop_HarvestsViaNormalPath()
    {
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-wait-3");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        if (crop.FuncGroupId != CropHarvestLoopTests.MaturePhase)
            throw new InvalidOperationException($"crop did not reach mature phase (got {crop.FuncGroupId})");
        var (executor, runtime, clock) = FarmLoopRig(rigged, (c, p) => true, (_, _) => [crop]);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Harvest
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Replanting);
        await Assert.That(state.NeedsFarmCropObjId).IsEqualTo(0u);
    }

    [Test]
    public async Task StepAsync_NeedsInactive_RoamUnchanged()
    {
        var rigged = CreateActorOnUniqueWorld("nf-wait-4");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => "presence.roam",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => []
        };
        var runtime = NewBot(actor);
        executor.SetRoamRoute(runtime.Character,
            new BotPath([TestPosition + new Vector3(10f, 0f, 0f)], BotPath.LoopMode.Loop));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        // The route layer issues the MoveTo leg on the same wake (the leg is
        // Running, not terminal — audit emits only on terminal transition —
        // so the live ActiveRequest is the proof, the BotRoamStepExecutorTests
        // precedent).
        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(actor.ActiveRequest!.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.NeedsFarmPhase
            ?? NeedsFarmLoopPhase.Idle).IsEqualTo(NeedsFarmLoopPhase.Idle);
    }
    // ------------------------------------------------------------ earn leg (seed-absent + funds-short)

    // Own earn fixture ids (N1 88xxx/91xxx range, never collide with sibling-suite rig ids).
    private const uint EarnSurplusItemId = 91_109;
    private const uint EarnMerchantNpcTemplateId = 91_206;
    private const uint EarnFundedMerchantNpcTemplateId = 91_207;

    private static (int Price, int Refund, bool Sellable) SnapshotTrade(uint templateId)
    {
        var template = ItemManager.Instance.GetTemplate(templateId);
        return template == null ? (0, 0, false) : (template.Price, template.Refund, template.Sellable);
    }

    private static void RestoreTrade(uint templateId, (int Price, int Refund, bool Sellable) snapshot)
        => GameplayActorTestRig.SeedTradeItemTemplate(templateId, snapshot.Price, snapshot.Refund, snapshot.Sellable);

    [Test]
    public async Task Run_BrokeSeedless_WithSurplus_SellsThroughRealPath_MoneyRises()
    {
        // Broke + seedless with a sellable surplus: Buy is refused for funds,
        // so the earn leg (Sell) is the only legal work — copper rises through
        // the REAL CSSellItemsPacket engine path.
        var (actor, session) = CreateActorOnUniqueWorld("nf-earn-1");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(FoodBuyItemId, FoodPrice, 0, false);
        GameplayActorTestRig.SeedTradeItemTemplate(EarnSurplusItemId, price: 0, refund: 30, sellable: true);
        GameplayActorTestRig.StockItem(session, EarnSurplusItemId, 1);
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: EarnMerchantNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-earn-91109",
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = FoodBuyItemId,
            BuyCount = 1,
            BuyUnitPrice = FoodPrice,
            SellMerchantNpcObjId = merchantObjId,
            SellSurplusItemTemplateId = EarnSurplusItemId
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Sell);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(result.ExpectedPostconditionSatisfied).IsTrue();
        await Assert.That(actor.Character.Money).IsEqualTo(30);
        await Assert.That(GameplayActorTestRig.BagCount(actor, EarnSurplusItemId)).IsEqualTo(0);
        await Assert.That(result.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Buy && r.Reason.Contains("funds-sufficient"))).IsTrue();
    }

    [Test]
    public async Task Run_EarnExhausted_StillBroke_RestsBounded_NoResell()
    {
        // The earn pays too little for the seed: once the surplus is gone the
        // sell candidate is refused BEFORE preference and the decision rests —
        // no repeated sell dispatch, no spin.
        var (actor, session) = CreateActorOnUniqueWorld("nf-earn-2");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(FoodBuyItemId, FoodPrice, 0, false);
        GameplayActorTestRig.SeedTradeItemTemplate(EarnSurplusItemId, price: 0, refund: 5, sellable: true);
        GameplayActorTestRig.StockItem(session, EarnSurplusItemId, 1);
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: EarnMerchantNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-earn-91109-a",
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = FoodBuyItemId,
            BuyCount = 1,
            BuyUnitPrice = FoodPrice,
            SellMerchantNpcObjId = merchantObjId,
            SellSurplusItemTemplateId = EarnSurplusItemId
        };

        var first = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Sell);
        await Assert.That(first.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(5);

        var second = NeedsDecisionScenario.Run(actor, options with { CycleId = "nf-earn-91109-b" });

        await Assert.That(second.WorkSelected).IsFalse();
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(actor.Character.Money).IsEqualTo(5);
        await Assert.That(second.Rejections.Any(r =>
            r.Proposal.Action == ActorActionType.Sell && r.Reason.Contains("sellable-surplus-present"))).IsTrue();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Sell)).IsEqualTo(1);
    }

    [Test]
    public async Task Run_Funded_WithSurplus_BuyOutranksSell()
    {
        // Funded bots buy as before even with a sellable surplus in the bag:
        // Buy outranks Sell, so the earn leg never preempts real work and the
        // surplus stays untouched.
        var (actor, session) = CreateActorOnUniqueWorld("nf-earn-3");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(FoodBuyItemId, FoodPrice, 0, false);
        SeedIsolatedMerchantPack();
        GameplayActorTestRig.SeedTradeItemTemplate(EarnSurplusItemId, price: 0, refund: 30, sellable: true);
        GameplayActorTestRig.StockItem(session, EarnSurplusItemId, 1);
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: EarnFundedMerchantNpcTemplateId, packId: SeedPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-earn-91109-c",
            MerchantNpcObjId = merchantObjId,
            BuyItemTemplateId = FoodBuyItemId,
            BuyCount = 1,
            BuyUnitPrice = FoodPrice,
            SellMerchantNpcObjId = merchantObjId,
            SellSurplusItemTemplateId = EarnSurplusItemId
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Buy);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Money).IsEqualTo(10_000 - FoodPrice);
        await Assert.That(GameplayActorTestRig.BagCount(actor, EarnSurplusItemId)).IsEqualTo(1);
    }

    [Test]
    public async Task StepAsync_SeedlessBrokeWithSurplus_SellsThenBuysNextWake()
    {
        // Seed-absent + funds-short with a sellable harvest surplus: wake 1
        // lands exactly one Sell (re-evaluation discipline — never a scripted
        // sell-then-buy chain in one wake); wake 2 re-evaluates funded and
        // lands the seed Buy through the existing loop.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-earn-4");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var merchant = session.World.GetNpc(merchantObjId)!;
        // The harvested food output is the loop's sellable surplus: patch the
        // canonical potato additively (the M3aM4 SeedSellableHarvestYield
        // precedent) and restore it after — sibling suites read it unsellable.
        var potato = BotRoamStepExecutor.NeedsFarmFoodItemTemplateId;
        var snapshot = SnapshotTrade(potato);
        try
        {
            GameplayActorTestRig.SeedTradeItemTemplate(potato, price: 100, refund: 30, sellable: true);
            GameplayActorTestRig.StockItem(session, potato, 3);
            var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
                nearbyNpcs: (_, _) => [merchant],
                nearbyDoodads: (_, _) => []);

            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);

            await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Sell
                && r.Result == ActorLifecycleState.Completed)).IsEqualTo(1);
            await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy)).IsFalse();
            await Assert.That(actor.Character.Money).IsEqualTo(90);
            var afterEarn = executor.GetBotState(runtime.CharacterId)!;
            await Assert.That(afterEarn.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Idle);
            await Assert.That(afterEarn.NeedsFarmReason.Contains("surplus sold")).IsTrue();

            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);

            await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy
                && r.Result == ActorLifecycleState.Completed)).IsTrue();
            await Assert.That(GameplayActorTestRig.BagCount(actor,
                BotRoamStepExecutor.NeedsFarmSeedItemTemplateId)).IsEqualTo(1);
        }
        finally
        {
            RestoreTrade(potato, snapshot);
        }
    }
    [Test]
    public async Task StepAsync_BusyPatrolMove_PausesForDecision_BuyLands()
    {
        // Live-wake starvation (.165 finding): a patrolling bot holds a live
        // Move leg, which TryBegin-skipped the needs leg forever while
        // needs.farm stayed active. The 0b gate now pauses patrol/hunt
        // movement for the decision tick: funded + seedless + merchant in
        // range lands the seed Buy on the SAME wake instead of skipping.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-busy-1");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 10_000);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SeedTradeItemTemplate(
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId,
            price: (int)BotRoamStepExecutor.NeedsFarmSeedUnitPrice, refund: 0, sellable: false);
        NeedsPreemptionRig.SeedNeedsMerchantPack();
        var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session,
            npcTemplateId: NeedsPreemptionRig.NeedsMerchantNpcTemplateId,
            packId: NeedsPreemptionRig.NeedsMerchantPackId);
        GameplayActorTestRig.SetNpcPosition(session, merchantObjId, TestPosition);
        var merchant = session.World.GetNpc(merchantObjId)!;
        var (executor, _, runtime, clock) = NeedsPreemptionRig.CreateExecutor(rigged, "needs.farm",
            nearbyNpcs: (_, _) => [merchant],
            nearbyDoodads: (_, _) => []);

        // Patrol-like movement: a live Move leg 50 m out (never arrives in
        // one wake), the exact shape that starved the leg live.
        _ = actor.MoveTo(TestPosition + new Vector3(50f, 0f, 0f), 2f, TimeSpan.FromSeconds(30));
        await Assert.That(actor.ActiveRequest is { IsTerminal: false }).IsTrue();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Stop)).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Buy
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor,
            BotRoamStepExecutor.NeedsFarmSeedItemTemplateId)).IsEqualTo(1);
    }

    [Test]
    public async Task StepAsync_FarmTravelEnRoute_PreservesMovement_NoStop()
    {
        // The pause must never strangle farm travel itself: en-route to soil
        // (Traveling phase, live farm-route Move) skips the decision without
        // stopping, and the route layer keeps advancing the walk.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-busy-2");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        StockLoopSeed(rigged);
        GameplayActorTestRig.SetFarmGateEnabled(true);
        SeedLoopPotatoAllowlist();
        var soil = TestPosition + new Vector3(10f, 0f, 0f);
        var (executor, runtime, clock) = FarmLoopRig(rigged,
            (c, p) => MathUtil.CalculateDistance(p, soil, false) <= 1f,
            (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.NeedsFarmPhase)
            .IsEqualTo(NeedsFarmLoopPhase.Traveling);
        var distAfterWake1 = MathUtil.CalculateDistance(
            actor.Character.Transform.World.Position, soil, false);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Stop)).IsFalse();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.NeedsFarmPhase)
            .IsEqualTo(NeedsFarmLoopPhase.Traveling);
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.NeedsLegActive).IsFalse();
        var distAfterWake2 = MathUtil.CalculateDistance(
            actor.Character.Transform.World.Position, soil, false);
        await Assert.That(distAfterWake2 < distAfterWake1).IsTrue();
    }
}
