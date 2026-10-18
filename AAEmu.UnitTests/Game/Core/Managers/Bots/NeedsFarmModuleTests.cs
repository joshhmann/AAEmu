using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;

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
using AAEmu.Game.Models.Game.World.Transform;
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

    // Attribution prerequisites, not autonomous live or persistence proof.
    // These use the existing real actor/engine rig; no echo actor or observer
    // stamps. Only an emitted terminal row can prove pre-NewRequest staging.
    [Test]
    public async Task Run_NeedsAudit_InterruptedPlantUsesSelectedProposalBeforeTerminalEmission()
    {
        var (actor, session) = CreateActorOnUniqueWorld("nf-audit-plant");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        GameplayActorTestRig.SetFarmGateEnabled(true);
        GameplayActorTestRig.SetFarmAllowlist(true);
        actor.SetPendingDecision("parent.activity", "parent-policy", 2, 0, "parent-seed", null);
        actor.SetPendingCycleId("parent-cycle");
        actor.SetPendingWakeSequence(73);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-audit-plant-cycle",
            PolicyVersion = "nf-audit-policy",
            PlantSeedItemTemplateId = GameplayActorTestRig.TestSeedItemId,
            PlantPosition = TestPosition
        };

        var result = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Plant);
        await Assert.That(result.SelectedProposal!.Goal).IsEqualTo("needs.plant");
        // Real seed cost/placement occurred; the rig's deliberately dead DB
        // produces Interrupted at Save. This is NOT successful persistence.
        await Assert.That(result.Request!.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestSeedItemId)).IsEqualTo(2);
        var perception = actor.AuditTrace.First(r => r.Action == ActorActionType.Observe);
        await Assert.That(perception.DecisionGoal).IsEqualTo("parent.activity");
        await Assert.That(perception.DecisionCycleId).IsEqualTo("parent-cycle");
        await Assert.That(perception.WakeSequence).IsEqualTo(73L);
        await AssertSelectedNeedsTerminalAudit(actor, result, options);
        await AssertLaterOrdinaryAuditUnattributed(actor);
    }

    [Test]
    public async Task Run_NeedsAudit_CompletedHarvestAndRejectedReselectionPreserveEarlierAudit()
    {
        var (actor, session) = CreateActorOnUniqueWorld("nf-audit-harvest");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        var crop = PlantMatureCrop(actor, session);
        var before = GameplayActorTestRig.BagCount(actor, CropHarvestLoopTests.PotatoItemId);
        var options = new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-audit-harvest-first",
            PolicyVersion = "nf-audit-policy-first",
            FoodItemTemplateId = CropHarvestLoopTests.PotatoItemId,
            HarvestDoodadObjId = crop.ObjId
        };

        var first = NeedsDecisionScenario.Run(actor, options);

        await Assert.That(first.SelectedProposal!.Goal).IsEqualTo("needs.harvest");
        await Assert.That(first.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, CropHarvestLoopTests.PotatoItemId) - before >= 2).IsTrue();
        await Assert.That(session.World.GetDoodad(crop.ObjId)).IsNull();
        await AssertSelectedNeedsTerminalAudit(actor, first, options);
        var emittedFirst = actor.AuditTrace.Single(r => r.TraceId == first.Request.TraceId);
        var immutableJson = emittedFirst.ToJson();
        // A fresh selection still targets the now-deleted real crop. The actor
        // actually creates and rejects that request, rather than echoing a label.
        var laterOptions = options with { CycleId = "nf-audit-harvest-later", PolicyVersion = "nf-audit-policy-later" };
        var later = NeedsDecisionScenario.Run(actor, laterOptions);
        await Assert.That(later.SelectedAction).IsEqualTo(ActorActionType.Harvest);
        await Assert.That(later.Request!.State).IsEqualTo(ActorLifecycleState.Rejected);
        await AssertSelectedNeedsTerminalAudit(actor, later, laterOptions);
        await Assert.That(actor.AuditTrace.Single(r => r.TraceId == emittedFirst.TraceId).ToJson()).IsEqualTo(immutableJson);
        await AssertLaterOrdinaryAuditUnattributed(actor);
    }

    [Test]
    public async Task Run_NeedsAudit_DeclinedPlantCannotLabelLaterOrdinaryRequest()
    {
        var (actor, session) = CreateActorOnUniqueWorld("nf-audit-declined");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestSeedItemId, 3);
        // Actual public-farm gate remains off: no Plant is dispatched.
        var result = NeedsDecisionScenario.Run(actor, new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-audit-declined-cycle",
            PlantSeedItemTemplateId = GameplayActorTestRig.TestSeedItemId,
            PlantPosition = TestPosition
        });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Plant)).IsFalse();
        await AssertLaterOrdinaryAuditUnattributed(actor);
    }

    [Test]
    public async Task Run_NeedsAudit_InvalidCallerPolicyAbortsWithoutDispatchOrLaterLeak()
    {
        var (actor, _) = CreateActorOnUniqueWorld("nf-audit-abort");
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 0);
        // Existing proposal-constructor validation rejects a malformed public
        // option after ordinary perception; no invented exception/actor seam.
        var result = NeedsDecisionScenario.Run(actor, new NeedsDecisionScenario.NeedsOptions
        {
            CycleId = "nf-audit-aborted-cycle",
            PolicyVersion = ""
        });
        await Assert.That(result.FailStage).IsEqualTo("RUN");
        await Assert.That(result.Request).IsNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Plant or ActorActionType.Harvest or ActorActionType.Stop)).IsFalse();
        await AssertLaterOrdinaryAuditUnattributed(actor);
    }

    private static async Task AssertSelectedNeedsTerminalAudit(GameplayActor actor,
        NeedsDecisionScenario.NeedsRunResult result, NeedsDecisionScenario.NeedsOptions options)
    {
        var emitted = actor.AuditTrace.Single(r => r.TraceId == result.Request!.TraceId);
        using var json = JsonDocument.Parse(emitted.ToJson());
        await Assert.That(json.RootElement.GetProperty("decision_goal").GetString()).IsEqualTo(result.SelectedProposal!.Goal);
        await Assert.That(json.RootElement.GetProperty("decision_policy").GetString()).IsEqualTo(result.SelectedProposal.PolicyVersion);
        await Assert.That(json.RootElement.GetProperty("decision_cycle_id").GetString()).IsEqualTo(options.CycleId);
        await Assert.That(emitted.Result).IsEqualTo(result.Request!.State);
    }

    private static async Task AssertLaterOrdinaryAuditUnattributed(GameplayActor actor)
    {
        _ = actor.Observe();
        var emitted = actor.AuditTrace.Last();
        await Assert.That(emitted.Action).IsEqualTo(ActorActionType.Observe);
        using var json = JsonDocument.Parse(emitted.ToJson());
        await Assert.That(json.RootElement.GetProperty("decision_goal").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(json.RootElement.GetProperty("decision_policy").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(json.RootElement.GetProperty("decision_cycle_id").ValueKind).IsEqualTo(JsonValueKind.Null);
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
        actor.Character.LaborPower = 100; // The real crop skill requires labor.
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

    [Test]
    public async Task StepAsync_SeedPresentOffSoil_RoutesThroughTheBrainDecision()
    {
        // The needs brain's first live dispatch caller: the travel arm's soil
        // destination is decided by NeedsBrain.Decide through the planner and walked
        // by the existing route layer. The leg's own wake record proves the routing:
        // the decision that produced the leg rides in the reason, naming the
        // soil-travel verdict and routed=true, and the route's target is the point
        // the shared bounded spiral resolved — never a fabricated one.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-brain-1");
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

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Traveling);
        await Assert.That(state.NeedsFarmReason).Contains("brain=verdict=travel:reason=soil-resolved:verb=move-to");
        await Assert.That(state.NeedsFarmReason).Contains("routed=true:dispatch=walk");
        var route = executor.GetRoamRoute(runtime.CharacterId);
        await Assert.That(route).IsNotNull();
        await Assert.That(MathUtil.CalculateDistance(route!.CurrentTarget, soil, false) <= 1f).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Plant)).IsFalse();
    }

    [Test]
    public async Task StepAsync_BrainHolds_NoRouteArmedAndNoSecondSoilSearch()
    {
        // Fail-closed + additive: a surface that answers no for every candidate is
        // the brain's own bounded-spiral demand (no plantable soil anywhere near), so
        // the arm falls back to the leg's pre-brain outcome — bounded defer, no route,
        // no Plant — and the search it already ran is NOT run a second time. The probe
        // tally is the proof: one resolve wake costs one bounded pass (the perception
        // spiral, the coarser discovery extension, and the underfoot read); a wired
        // fallback that re-searched would cost two.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-brain-2");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        StockLoopSeed(rigged);
        var probes = 0;
        var (executor, runtime, clock) = FarmLoopRig(rigged,
            (c, p) => { probes++; return false; }, (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.SeekingSoil);
        await Assert.That(state.NeedsFarmReason).Contains("brain=verdict=seek-soil:reason=no-soil-nearby");
        await Assert.That(state.NeedsFarmReason).Contains("routed=false:dispatch=fallback:search-covered=true");
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Plant or ActorActionType.Move)).IsFalse();
        // The bounded spiral's own candidate set (the NeedsSoil geometry) plus the
        // handful of underfoot reads this leg makes: comfortably under the ~260 a
        // second search would add.
        await Assert.That(probes > 100 && probes < 200).IsTrue();
    }

    [Test]
    public async Task StepAsync_MatureCropInRange_RoutesHarvestThroughTheBrainDecision()
    {
        // The brain's HARVEST action arm: the mature-in-range wake is decided by
        // NeedsBrain.Decide through the planner and reports the existing Harvest verb
        // on the tracked crop — and the harvest itself still lands through the leg's
        // own dispatch tail, unchanged.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-brain-harvest");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        var crop = PlantMatureCrop(actor, session);
        crop.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        var (executor, runtime, clock) = FarmLoopRig(rigged, (c, p) => true, (_, _) => [crop]);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmReason)
            .Contains("brain=verdict=harvest:reason=crop-mature:verb=harvest");
        await Assert.That(state.NeedsFarmReason).Contains("routed=true:dispatch=harvest");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Harvest
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Replanting);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StepAsync_NativeHarvest_HoldsSameActorThenReassessesWithoutSecondCastDelay(bool needsWithdrawn)
    {
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-native-harvest-owner");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        var crop = PlantMatureCrop(actor, session);
        crop.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        string? activity = "needs.farm";
        var (executor, runtime, clock) = FarmLoopRig(rigged, (_, _) => true,
            (_, _) => session.World.GetAllDoodads(), _ => activity);
        using var fixture = new GameplayActorHarvestTests.NativeHarvestSkill(150);
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var request = actor.ActiveRequest!;
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(request.Action).IsEqualTo(ActorActionType.Harvest);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(ReferenceEquals(state.NeedsFarmHarvestRequest, request)).IsTrue();
        await Assert.That(ReferenceEquals(executor.GetOrCreateActor(actor.Character), actor)).IsTrue();
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Harvesting);
        await Assert.That(crop.FuncGroupId).IsEqualTo(CropHarvestLoopTests.MaturePhase);
        await Assert.That(GameplayActorTestRig.BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsEqualTo(0);

        // Only the ordinary TaskManager callbacks run here. The request still
        // needs its original actor's next scheduler wake to observe completion.
        await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        activity = needsWithdrawn ? null : "needs.farm";
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(state.NeedsFarmHarvestRequest).IsNull();
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Replanting);
        await Assert.That(state.NeedsFarmCropObjId).IsEqualTo(0u);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)99);
        await Assert.That(GameplayActorTestRig.BagCount(actor, CropHarvestLoopTests.PotatoItemId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Harvest)).IsEqualTo(1);

        // Re-evaluate updated needs on the next ordinary wake. This fixture's
        // food need is satisfied: retained activity rests, withdrawn activity
        // admits no farm work. Replanting is not forced by harvest completion.
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Plant)).IsFalse();
        var rest = actor.AuditTrace.LastOrDefault(r => r.Action == ActorActionType.Stop);
        if (needsWithdrawn)
            await Assert.That(rest).IsNull();
        else
        {
            await Assert.That(rest).IsNotNull();
            await Assert.That(rest!.Result).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Idle);
        }
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)99);
        await Assert.That(session.World.GetDoodad(crop.ObjId)).IsNull();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Harvest)).IsEqualTo(1);
    }

    [Test]
    public async Task StepAsync_OnSoilWithSeed_RoutesPlantThroughTheBrainDecision()
    {
        // The brain's PLANT action arm: seed on hand and standing on valid soil is
        // decided as the existing Plant verb, and the plant still lands through the
        // leg's own dispatch tail with the crop tracked for the wait branch.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-brain-plant");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        StockLoopSeed(rigged);
        GameplayActorTestRig.SetFarmGateEnabled(true);
        SeedLoopPotatoAllowlist();
        // Standing ON valid soil: the soil provider answers yes underfoot, so the leg
        // takes its plant branch on this very wake.
        var (executor, runtime, clock) = FarmLoopRig(rigged, (c, p) => true, (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmReason)
            .Contains("brain=verdict=plant:reason=seed-on-soil:verb=plant");
        await Assert.That(state.NeedsFarmReason).Contains("routed=true:dispatch=plant");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Plant)).IsTrue();
    }

    [Test]
    public async Task StepAsync_ImmatureCrop_HoldsOnTheNamedWait_NoDispatch()
    {
        // The brain's third action row is the one that STOPS a wake: a live immature
        // crop is the named maturity wait, routed and reported, and NOTHING is
        // dispatched — the engine stays the sole maturity authority.
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("nf-brain-wait");
        var (actor, session) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.Character.LaborPower = 100;
        actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate,
            CropHarvestLoopTests.PotatoSeedItemId, 5);
        var crop = CropHarvestLoopRig.Plant(actor.Character, session.World,
            CropHarvestLoopRig.MakeHouse(actor.Character));
        crop.Transform.Local.SetPosition(TestPosition + new Vector3(2f, 0f, 0f));
        var (executor, runtime, clock) = FarmLoopRig(rigged, (c, p) => true, (_, _) => [crop]);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.WaitingMaturity);
        await Assert.That(state.NeedsFarmReason)
            .Contains("brain=verdict=wait-maturity:reason=crop-immature:verb=hold");
        await Assert.That(state.NeedsFarmReason).Contains("routed=true:dispatch=wait-maturity");
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Harvest or ActorActionType.Plant))
            .IsFalse();
        await Assert.That(state.NeedsFarmCropObjId).IsEqualTo(crop.ObjId);
    }

    // ------------------------------------------------------------ travel-to-soil + maturity-wait/RESUME

    private static (BotRoamStepExecutor Executor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) FarmLoopRig(
        (GameplayActor Actor, HeadlessSession Session) rigged,
        Func<Character, Vector3, bool> soil,
        Func<Character, float, IEnumerable<Doodad>>? doodads = null,
        Func<uint, string?>? activity = null)
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
            ActiveActivityProvider = activity ?? (_ => "needs.farm"),
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

    [Test]
    public async Task BreadMode_UsesFinishedUsableReserve_NotRawGrainOrGold()
    {
        var (actor, _) = CreateActorOnUniqueWorld("food-reserve");
        using var fixture = new HardBreadFixture(actor);
        GameplayActorTestRig.SetMoney(actor, 1000);
        GameplayActorTestRig.GrantItem(actor, NeedsFarmModuleOptions.BarleyItemId, 100);
        var module = new NeedsFarmActivityModule(new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true });
        await Assert.That(module.CanActivate(ContextFor(NewBot(actor))).CanActivate).IsTrue();
        fixture.Bread.Count = 5; // Initial stocked-state comparison, not generated progress.
        GameplayActorTestRig.SetMoney(actor, 0);
        await Assert.That(module.CanActivate(ContextFor(NewBot(actor))).CanActivate).IsFalse();
        fixture.Bread.Template.LootQuestId = 1;
        await Assert.That(module.CanActivate(ContextFor(NewBot(actor))).CanActivate).IsTrue();
    }

    [Test]
    public async Task BreadMode_DefaultCropAndExplicitDisabledGate_ArePreserved()
    {
        var options = new NeedsFarmModuleOptions();
        await Assert.That(options.ProduceHardBread).IsFalse();
        await Assert.That(options.SeedItemTemplateId).IsEqualTo(15659u);
        await Assert.That(options.HarvestItemTemplateId).IsEqualTo(7992u);
        await Assert.That(options.SeedUnitPrice).IsEqualTo(25L);
        var actor = CreateActorOnUniqueWorld("food-gate").Actor;
        var module = new NeedsFarmActivityModule(options with { ProduceHardBread = true });
        await Assert.That(module.CanActivate(ContextFor(NewBot(actor))).CanActivate).IsFalse();
        await Assert.That((options with { ProduceHardBread = true }).SeedItemTemplateId).IsEqualTo(15652u);
    }

    [Test]
    public async Task BreadMode_NativeCraftPending_DrainsAfterAdmissionWithdrawn_WithoutForcedRefill()
    {
        var (actor, session) = CreateActorOnUniqueWorld("food-pending");
        using var fixture = new HardBreadFixture(actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.GrantItem(actor, NeedsFarmModuleOptions.ProcessedGrainItemId, 12);
        var benchId = GameplayActorTestRig.SpawnCraftBench(session, actor, NeedsFarmModuleOptions.CookingDoodadId);
        var bench = session.World.GetDoodad(benchId);
        using var native = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var clock = new FakeTimeProvider();
        var activity = "needs.farm";
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true },
            ActorFactory = _ => actor,
            ActiveActivityProvider = _ => activity,
            TimeProvider = clock,
            GroundHeightProvider = (_, _) => 0,
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [bench]
        };
        var bot = NewBot(actor);
        try
        {
            await executor.StepAsync(bot, CancellationToken.None);
            var pending = executor.GetBotState(bot.CharacterId)!.NeedsFoodRequest!;
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(GameplayActorTestRig.BagCount(actor, NeedsFarmModuleOptions.ProcessedGrainItemId)).IsEqualTo(12);
            activity = "presence.roam";
            await scheduler.WaitForTerminal(actor, pending);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(executor.GetBotState(bot.CharacterId)!.NeedsFoodRequest).IsNull();
            await Assert.That(GameplayActorTestRig.BagCount(actor, NeedsFarmModuleOptions.ProcessedGrainItemId)).IsEqualTo(8);
            await Assert.That(GameplayActorTestRig.BagCount(actor, NeedsFarmModuleOptions.HardBreadItemId)).IsEqualTo(10);
            await Assert.That(actor.AuditTrace.Count(x => x.Action == ActorActionType.Craft)).IsEqualTo(1);
            await Assert.That(actor.AuditTrace.Any(x => x.Action is ActorActionType.Plant or ActorActionType.Buy or ActorActionType.UseItem)).IsFalse();
        }
        finally
        {
            actor.PreemptCurrent("food fixture cleanup");
        }
    }

    [Test]
    public async Task BreadMode_ProtectedProcessedIngredient_ReleasesAndRetriesWhenProtectionChanges()
    {
        var (actor, session) = CreateActorOnUniqueWorld("food-protected");
        using var fixture = new HardBreadFixture(actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.GrantItem(actor, 30902, 8);
        var ingredient = ItemManager.Instance.GetTemplate(30902);
        ingredient.LootQuestId = 1;
        var bench = session.World.GetDoodad(GameplayActorTestRig.SpawnCraftBench(session, actor, 525));
        using var native = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var clock = new FakeTimeProvider();
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        var bot = NewBot(actor);
        NeedsFarmActivityModule module = null!;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor, TimeProvider = clock,
            ActiveActivityProvider = _ => module.CanActivate(ContextFor(bot)).CanActivate ? "needs.farm" : "presence.roam",
            GroundHeightProvider = (_, _) => 0,
            NearbyNpcProvider = (_, _) => [], NearbyDoodadProvider = (_, _) => [bench]
        };
        module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        try
        {
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(actor.AuditTrace.Any(x => x.Action is ActorActionType.Craft or ActorActionType.Sell or ActorActionType.UseItem)).IsFalse();
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(8);
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsFalse();
            ingredient.LootQuestId = 0; // Changed native protection fact, not a blocked-state reset.
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(bot, CancellationToken.None);
            var pending = executor.GetBotState(bot.CharacterId)!.NeedsFoodRequest!;
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Running);
            await scheduler.WaitForTerminal(actor, pending);
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(4);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 17664)).IsEqualTo(10);
        }
        finally { actor.PreemptCurrent("food protection fixture cleanup"); }
    }

    [Test]
    public async Task BreadMode_UnavailableBench_ReleasesToUsefulMovement_AndRetriesPublicBench()
    {
        var (actor, session) = CreateActorOnUniqueWorld("food-unavailable");
        using var fixture = new HardBreadFixture(actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.GrantItem(actor, 30902, 8);
        Doodad? bench = null;
        using var native = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var clock = new FakeTimeProvider();
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        NeedsFarmActivityModule module = null!;
        var bot = NewBot(actor);
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor, TimeProvider = clock,
            ActiveActivityProvider = _ => module.CanActivate(ContextFor(bot)).CanActivate ? "needs.farm" : "presence.roam",
            GroundHeightProvider = (_, _) => 0, NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => bench == null ? [] : [bench]
        };
        module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        try
        {
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(executor.GetBotState(bot.CharacterId)!.NeedsLegActive).IsFalse();
            await Assert.That(actor.AuditTrace.Any(x => x.Action == ActorActionType.Craft)).IsFalse();
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsFalse();
            executor.SetRoamRoute(actor.Character, BotPath.PathTo(TestPosition + new Vector3(20, 0, 0)));
            clock.Advance(TimeSpan.FromSeconds(1));
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(actor.Character.Transform.World.Position.X).IsGreaterThan(TestPosition.X);
            clock.Advance(TimeSpan.FromSeconds(10));
            await executor.StepAsync(bot, CancellationToken.None);
            bench = session.World.GetDoodad(GameplayActorTestRig.SpawnCraftBench(session, actor, 525));
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(bot, CancellationToken.None);
            var pending = executor.GetBotState(bot.CharacterId)!.NeedsFoodRequest!;
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Running);
            await scheduler.WaitForTerminal(actor, pending);
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(4);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 17664)).IsEqualTo(10);
        }
        finally { actor.PreemptCurrent("food bench fixture cleanup"); }
    }

    [Test]
    public async Task BreadMode_MissingKnifeMerchant_PreservesRoamUntilMerchantAppears_ThenPreservesApproach()
    {
        // Reconstructed policy fixture, not the failed live run: supplied raw
        // barley and an empty perception feed reproduce the reviewer's schedule.
        // The later merchant uses canonical NPC 1921 / pack 145 / item 8326,
        // but its visibility and position are controlled only in this unit world.
        var (actor, session) = CreateActorOnUniqueWorld("food-knife-route");
        using var fixture = new HardBreadFixture(actor);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 700);
        actor.Character.LaborPower = 96;
        GameplayActorTestRig.GrantItem(actor, NeedsFarmModuleOptions.BarleyItemId, 10);
        var goods = (Dictionary<uint, MerchantGoods>)GameplayActorTestRig.GetField(NpcManager.Instance, "<Goods>k__BackingField");
        var previousPack = goods.GetValueOrDefault(145u);
        var knifePack = new MerchantGoods(145);
        knifePack.AddItemToStock(NeedsFarmModuleOptions.ProcessingKnifeItemId, 0);
        goods[145] = knifePack;
        var clock = new FakeTimeProvider();
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        var bot = NewBot(actor);
        Npc? merchant = null;
        BotGoalArbiter arbiter = null!;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor, TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100), RoamSpeed = 2f,
            ActiveActivityProvider = id => arbiter.GetActiveActivity(id),
            GroundHeightProvider = (_, _) => TestPosition.Z,
            NearbyNpcProvider = (_, _) => merchant == null ? [] : [merchant],
            NearbyDoodadProvider = (_, _) => []
        };
        var module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        arbiter = new BotGoalArbiter([module, new PresenceRoamProbe()]);
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);
        var patrol = BotPath.PathTo(TestPosition + new Vector3(20, 0, 0));
        executor.SetRoamRoute(actor.Character, patrol);
        try
        {
            await steps.StepAsync(bot, CancellationToken.None);
            var state = executor.GetBotState(bot.CharacterId)!;
            await Assert.That(state.NeedsFoodBlockedReason).IsEqualTo("no observed processing-knife merchant");
            await Assert.That(state.NeedsLegActive).IsFalse();
            var originalMove = actor.ActiveRequest!;
            await Assert.That(originalMove.Action).IsEqualTo(ActorActionType.Move);
            await Assert.That(originalMove.MoveOwner).IsEqualTo("ROAM");
            await Assert.That(ReferenceEquals(executor.GetRoamRoute(bot.CharacterId), patrol)).IsTrue();
            var afterFirstWake = actor.Character.Transform.World.Position;
            for (var i = 0; i < 4; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await steps.StepAsync(bot, CancellationToken.None);
            }
            await Assert.That(actor.Character.Transform.World.Position.X).IsGreaterThan(afterFirstWake.X);
            var patrolStillRunning = ReferenceEquals(actor.ActiveRequest, originalMove) && !originalMove.IsTerminal;
            var interruptedPatrols = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move
                && r.Result == ActorLifecycleState.Interrupted && r.Detail == "stop requested");
            var moduleStops = actor.AuditTrace.Count(r =>
            {
                if (r.Action != ActorActionType.Stop) return false;
                using var json = JsonDocument.Parse(r.ToJson());
                return json.RootElement.GetProperty("decision_policy").GetString() == "module:NeedsFarm";
            });

            // Exercise changed availability before the red assertion, so the
            // missing-merchant failure cannot hide a broken ordinary approach.
            var merchantPosition = TestPosition + new Vector3(-10, 12, 0);
            var merchantObjId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1921, packId: 145);
            GameplayActorTestRig.SetNpcPosition(session, merchantObjId, merchantPosition);
            merchant = session.World.GetNpc(merchantObjId)!;
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await steps.StepAsync(bot, CancellationToken.None);
            await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Traveling);
            var approach = actor.ActiveRequest!;
            await Assert.That(approach.MoveOwner).IsEqualTo("OTHER:needs-farm-travel");
            await Assert.That(approach.Destination).IsEqualTo((Vector3?)merchantPosition);
            var distance = MathUtil.CalculateDistance(actor.Character.Transform.World.Position, merchantPosition, true);
            await Assert.That(distance).IsGreaterThan(GameplayActor.MaxShopRange);
            var stopsBeforeApproachWake = actor.AuditTrace.Count(r => r.Action == ActorActionType.Stop);
            for (var i = 0; i < 2; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await steps.StepAsync(bot, CancellationToken.None);
            }
            await Assert.That(ReferenceEquals(actor.ActiveRequest, approach) && !approach.IsTerminal).IsTrue();
            await Assert.That(MathUtil.CalculateDistance(actor.Character.Transform.World.Position, merchantPosition, true))
                .IsLessThan(distance);
            await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Stop)).IsEqualTo(stopsBeforeApproachWake);
            await Assert.That(GameplayActorTestRig.BagCount(actor, NeedsFarmModuleOptions.BarleyItemId)).IsEqualTo(10);
            await Assert.That(GameplayActorTestRig.BagCount(actor, NeedsFarmModuleOptions.ProcessingKnifeItemId)).IsEqualTo(0);
            await Assert.That(actor.Character.Money).IsEqualTo(700L);
            await Assert.That(patrolStillRunning && interruptedPatrols == 0 && moduleStops == 0).IsEqualTo(true,
                $"Unchanged missing merchant must preserve the original ROAM Move. original={originalMove.State}/{originalMove.Detail}; interrupted patrols={interruptedPatrols}; module Stops={moduleStops}; moved={actor.Character.Transform.World.Position - TestPosition}. Revealed merchant approach survived two wakes.");
        }
        finally
        {
            actor.PreemptCurrent("food knife routing fixture cleanup");
            if (previousPack == null) goods.Remove(145);
            else goods[145] = previousPack;
        }
    }

    [Test]
    public async Task BreadMode_PendingNativeCraft_FromIdle_IsNotStoppedByLowerPriorityWildlife()
    {
        var (actor, session) = CreateActorOnUniqueWorld("food-hunt-hold");
        using var fixture = new HardBreadFixture(actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.GrantItem(actor, 30902, 4);
        var bench = session.World.GetDoodad(GameplayActorTestRig.SpawnCraftBench(session, actor, 525));
        var prey = new Npc
        {
            ObjId = 9876, Template = new NpcTemplate { Id = 9876, Scale = 1 },
            Hp = 100, MaxHp = 100, Faction = new SystemFaction { Id = (FactionsEnum)115 }
        };
        prey.Transform.World.Position = TestPosition + new Vector3(2, 0, 0);
        using var native = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var clock = new FakeTimeProvider();
        var showPrey = false;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true },
            ActorFactory = _ => actor, TimeProvider = clock, ActiveActivityProvider = _ => "needs.farm",
            GroundHeightProvider = (_, _) => 0, NearbyDoodadProvider = (_, _) => [bench],
            EnableWildlifeHunt = true, HuntScanInterval = TimeSpan.FromMilliseconds(100),
            NearbyNpcProvider = (_, _) => showPrey ? [prey] : [],
            UnitResolver = (_, id) => id == prey.ObjId ? prey : null
        };
        var bot = NewBot(actor);
        try
        {
            await executor.StepAsync(bot, CancellationToken.None);
            var pending = executor.GetBotState(bot.CharacterId)!.NeedsFoodRequest!;
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(executor.GetBotState(bot.CharacterId)!.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Idle);
            showPrey = true;
            clock.Advance(TimeSpan.FromMilliseconds(200));
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(4);
            await scheduler.WaitForTerminal(actor, pending);
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 17664)).IsEqualTo(10);
            await Assert.That(actor.AuditTrace.Any(x => x.Action == ActorActionType.Stop)).IsFalse();
        }
        finally { actor.PreemptCurrent("food hunt fixture cleanup"); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LegacyPotato_ProtectedAndMetadataNullSeeds_StillChooseOrdinarySoilTravel(bool missingMetadata)
    {
        CropHarvestLoopRig.Seed();
        var rigged = CreateActorOnUniqueWorld("legacy-seed-count");
        var (actor, _) = rigged;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        StockLoopSeed(rigged);
        var seed = actor.Character.Inventory.Bag.Items.Single(x => x != null && x.TemplateId == 15659);
        var template = seed.Template;
        var oldQuest = template.LootQuestId;
        if (missingMetadata) seed.Template = null!;
        else template.LootQuestId = 1;
        var (executor, bot, clock) = FarmLoopRig(rigged,
            (_, p) => p.X >= TestPosition.X + 5, (_, _) => []);
        try
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(executor.GetBotState(bot.CharacterId)!.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Traveling);
            clock.Advance(TimeSpan.FromSeconds(1));
            await executor.StepAsync(bot, CancellationToken.None);
            await Assert.That(actor.Character.Transform.World.Position.X).IsGreaterThan(TestPosition.X);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 15659)).IsEqualTo(3);
            await Assert.That(actor.AuditTrace.Any(x => x.Action == ActorActionType.Buy)).IsFalse();
        }
        finally
        {
            seed.Template = template;
            template.LootQuestId = oldQuest;
            actor.PreemptCurrent("legacy seed fixture cleanup");
        }
    }

    [Test]
    public async Task BreadMode_NativeGcdRefusal_RetriesAfterRealReadinessWithoutResourceMutation()
    {
        var (actor, session) = CreateActorOnUniqueWorld("food-real-gcd");
        using var fixture = new HardBreadFixture(actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.GrantItem(actor, 30902, 4);
        var bench = session.World.GetDoodad(GameplayActorTestRig.SpawnCraftBench(session, actor, 525));
        using var native = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var skill = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.CraftTestSkillId);
        var previousIgnore = skill.IgnoreGlobalCooldown;
        skill.IgnoreGlobalCooldown = false;
        actor.Character.GlobalCooldown = DateTime.UtcNow.AddSeconds(1); // Initial ordinary readiness fixture.
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        var bot = NewBot(actor);
        NeedsFarmActivityModule module = null!;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor,
            ActiveActivityProvider = _ => module.CanActivate(ContextFor(bot)).CanActivate ? "needs.farm" : "presence.roam",
            GroundHeightProvider = (_, _) => 0, NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [bench]
        };
        module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        try
        {
            var prime = actor.Craft(6751, bench.ObjId);
            await Assert.That(prime.State).IsEqualTo(ActorLifecycleState.Running);
            actor.PreemptCurrent("initial ordinary craft cancellation before effects");
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(4);
            await executor.StepAsync(bot, CancellationToken.None);
            await executor.StepAsync(bot, CancellationToken.None); // Common tick observes the native refused skill.
            await Assert.That(actor.AuditTrace.Last(x => x.Action == ActorActionType.Craft).Result).IsEqualTo(ActorLifecycleState.Interrupted);
            await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsFalse();
            var mana = actor.Character.Mp;
            await Task.Delay(1200); // Real native interval expiry; no clock/MP/labor/stock mutation.
            await Assert.That(actor.Character.Mp).IsEqualTo(mana);
            await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
            await executor.StepAsync(bot, CancellationToken.None);
            var pending = executor.GetBotState(bot.CharacterId)!.NeedsFoodRequest!;
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Running);
            await scheduler.WaitForTerminal(actor, pending);
            await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(0);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 17664)).IsEqualTo(10);
        }
        finally
        {
            skill.IgnoreGlobalCooldown = previousIgnore;
            actor.PreemptCurrent("food GCD fixture cleanup");
        }
    }

    [Test]
    public async Task BreadMode_ExhaustedUnreachableApproach_RetriesDifferentReachableBench()
    {
        var (actor, session) = CreateActorOnUniqueWorld("food-new-target");
        using var fixture = new HardBreadFixture(actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        actor.NoProgressWindow = TimeSpan.FromMilliseconds(500); // Initial native navigation failure fixture.
        actor.MaxUnstickNudges = 0;
        GameplayActorTestRig.GrantItem(actor, 30902, 4);
        var bench = session.World.GetDoodad(GameplayActorTestRig.SpawnCraftBench(session, actor, 525));
        bench.Transform.Local.SetPosition(TestPosition + new Vector3(2, 0, 60)); // Beyond ordinary craft reach; terrain cannot reach this fixture's height.
        using var native = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        var bot = NewBot(actor);
        NeedsFarmActivityModule module = null!;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor,
            ActiveActivityProvider = _ => module.CanActivate(ContextFor(bot)).CanActivate ? "needs.farm" : "presence.roam",
            GroundHeightProvider = (_, _) => TestPosition.Z,
            NearbyNpcProvider = (_, _) => [], NearbyDoodadProvider = (_, _) => [bench]
        };
        module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        try
        {
            for (var i = 0; i < 400 && module.CanActivate(ContextFor(bot)).CanActivate; i++)
            {
                await executor.StepAsync(bot, CancellationToken.None);
                await Task.Delay(25);
            }
            var exhausted = executor.GetBotState(bot.CharacterId)!;
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsEqualTo(false,
                $"attempts={exhausted.NeedsFarmSoilAttempts}, phase={exhausted.NeedsFarmPhase}, reason={exhausted.NeedsFarmReason}, pathFinished={exhausted.Path?.IsFinished}, request={actor.ActiveRequest?.Action}/{actor.ActiveRequest?.State}, position={actor.Character.Transform.World.Position}");
            await Assert.That(actor.AuditTrace.Any(x => x.Action == ActorActionType.Move)).IsTrue();
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(4);
            bench = session.World.GetDoodad(GameplayActorTestRig.SpawnCraftBench(session, actor, 525));
            bench.Transform.Local.SetPosition(TestPosition + new Vector3(30, 0, 0)); // Changed target requires a real, reachable native approach.
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
            for (var i = 0; i < 160 && executor.GetBotState(bot.CharacterId)!.NeedsFoodRequest == null; i++)
            {
                await executor.StepAsync(bot, CancellationToken.None);
                await Task.Delay(50);
            }
            var retried = executor.GetBotState(bot.CharacterId)!;
            var pending = retried.NeedsFoodRequest;
            await Assert.That(pending != null).IsEqualTo(true,
                $"attempts={retried.NeedsFarmSoilAttempts}, phase={retried.NeedsFarmPhase}, reason={retried.NeedsFarmReason}, blocked={retried.NeedsFoodBlockedReason}, position={actor.Character.Transform.World.Position}, nativeRequests={string.Join(';', actor.AuditTrace.Select(x => $"{x.Action}:{x.Result}:{x.Detail}"))}");
            await scheduler.WaitForTerminal(actor, pending!);
            await Assert.That(pending!.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(0);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 17664)).IsEqualTo(10);
        }
        finally { actor.PreemptCurrent("food changed target fixture cleanup"); }
    }

    [Test]
    [Arguments("reached")]
    [Arguments("exhausted")]
    [Arguments("merchant")]
    [Arguments("timed-out")]
    [Arguments("foreign")]
    [Arguments("not-merchant")]
    [Arguments("no-stock")]
    [Arguments("nonfinite")]
    [Arguments("outside")]
    [Arguments("wrong-member")]
    [Arguments("event-only")]
    public async Task BreadMode_KnownKnifeSource_TravelsToObserveOrReleasesUnchangedWork(string sourceCase)
    {
        // Reconstructed metadata and perception; no live-game setup or earned
        // resource claim. Every displacement/terminal below uses the real actor.
        var (actor, session) = CreateActorOnUniqueWorld("food-known-knife");
        using var fixture = new HardBreadFixture(actor);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 700);
        actor.Character.LaborPower = 96;
        GameplayActorTestRig.GrantItem(actor, NeedsFarmModuleOptions.BarleyItemId, 10);
        GameplayActorTestRig.SeedTradeItemTemplate(8326, 50, 10, false);
        var templates = (Dictionary<uint, NpcTemplate>)GameplayActorTestRig.GetField(NpcManager.Instance, "<Templates>k__BackingField");
        var goods = (Dictionary<uint, MerchantGoods>)GameplayActorTestRig.GetField(NpcManager.Instance, "<Goods>k__BackingField");
        var previousTemplate = templates.GetValueOrDefault(1921u);
        var previousPack = goods.GetValueOrDefault(145u);
        templates[1921] = new NpcTemplate { Id = 1921, Merchant = sourceCase != "not-merchant", MerchantPackId = 145 };
        var pack = new MerchantGoods(145);
        if (sourceCase != "no-stock") pack.AddItemToStock(8326, 0);
        goods[145] = pack;
        var sourcePosition = sourceCase == "exhausted"
            ? TestPosition + new Vector3(2, 0, 60)
            : TestPosition + new Vector3(100, 0, 0);
        if (sourceCase == "outside") sourcePosition = TestPosition + new Vector3(151, 0, 0);
        if (sourceCase == "nonfinite") sourcePosition = new Vector3(float.NaN, TestPosition.Y, TestPosition.Z);
        var source = new NpcSpawner
        {
            Id = 98145, UnitId = 1921, ParentWorld = session.World,
            Position = new WorldSpawnPosition { WorldId = session.World.Id, X = sourcePosition.X, Y = sourcePosition.Y, Z = sourcePosition.Z },
            Template = new NpcSpawnerTemplate
            {
                Npcs = [new NpcSpawnerNpc { MemberId = sourceCase == "wrong-member" ? 7756u : 1921u, MemberType = "Npc" }]
            }
        };
        if (sourceCase == "foreign") source.ParentWorld = CreateActorOnUniqueWorld("food-foreign-source").Session.World;
        var sourceMap = (Dictionary<uint, List<NpcSpawner>>)GameplayActorTestRig.GetField(session.World.SpawnManager,
            sourceCase == "event-only" ? "<NpcEventSpawners>k__BackingField" : "<NpcSpawners>k__BackingField");
        sourceMap[98145] = [source];
        var clock = new FakeTimeProvider();
        var bot = NewBot(actor);
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        Npc? merchant = null;
        BotGoalArbiter arbiter = null!;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor, TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100), RoamSpeed = 5f,
            RoamLegTimeout = sourceCase == "timed-out" ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(60),
            ActiveActivityProvider = id => arbiter.GetActiveActivity(id),
            GroundHeightProvider = (_, _) => TestPosition.Z,
            NearbyNpcProvider = (character, radius) => merchant != null
                && MathUtil.CalculateDistance(character.Transform.World.Position, merchant.Transform.World.Position, true) <= radius
                ? [merchant] : [],
            NearbyDoodadProvider = (_, _) => []
        };
        var module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        arbiter = new BotGoalArbiter([module, new PresenceRoamProbe()]);
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);
        var patrol = new BotPath([TestPosition + new Vector3(0, 20, 0)], BotPath.LoopMode.Loop);
        executor.SetRoamRoute(actor.Character, patrol);
        if (sourceCase == "exhausted")
        {
            // Isolate genuine flat-arrival/three-approach exhaustion from the
            // separate native stuck-failure and timed-out navigation paths.
            actor.NoProgressWindow = TimeSpan.Zero;
            actor.MaxUnstickNudges = 0;
        }
        try
        {
            await steps.StepAsync(bot, CancellationToken.None);
            var state = executor.GetBotState(bot.CharacterId)!;
            var validSource = sourceCase is "reached" or "exhausted" or "merchant" or "timed-out";
            if (validSource)
            {
                await Assert.That(state.NeedsFarmPhase).IsEqualTo(NeedsFarmLoopPhase.Traveling);
                await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo("OTHER:needs-farm-travel");
                await Assert.That(actor.ActiveRequest.TargetId).IsEqualTo(0u);
                await Assert.That(actor.ActiveRequest.Destination).IsEqualTo((Vector3?)sourcePosition);
                if (sourceCase == "merchant")
                {
                    // The actor must actually walk into local perception before
                    // a seller is revealed; the source never gives Buy an ID.
                    for (var i = 0; i < 80 && MathUtil.CalculateDistance(actor.Character.Transform.World.Position, sourcePosition, true) > 45f; i++)
                    {
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        await steps.StepAsync(bot, CancellationToken.None);
                    }
                    await Assert.That(MathUtil.CalculateDistance(actor.Character.Transform.World.Position, sourcePosition, true)).IsLessThanOrEqualTo(45f);
                    var merchantId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1921, packId: 145);
                    GameplayActorTestRig.SetNpcPosition(session, merchantId, sourcePosition);
                    merchant = session.World.GetNpc(merchantId)!;
                    for (var i = 0; i < 80 && GameplayActorTestRig.BagCount(actor, 8326) == 0; i++)
                    {
                        clock.Advance(TimeSpan.FromMilliseconds(500));
                        await steps.StepAsync(bot, CancellationToken.None);
                    }
                    await Assert.That(GameplayActorTestRig.BagCount(actor, 8326)).IsEqualTo(1);
                    await Assert.That(actor.Character.Money).IsEqualTo(650L);
                    await Assert.That(GameplayActorTestRig.BagCount(actor, 8005)).IsEqualTo(10);
                    await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(0);
                    await Assert.That(actor.Character.LaborPower).IsEqualTo((short)96);
                    await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Buy && r.Result == ActorLifecycleState.Completed)).IsEqualTo(1);
                    return;
                }
                var positionBeforeBlockedWake = actor.Character.Transform.World.Position;
                for (var i = 0; i < 160 && state.NeedsFoodBlockedReason == null; i++)
                {
                    positionBeforeBlockedWake = actor.Character.Transform.World.Position;
                    clock.Advance(TimeSpan.FromMilliseconds(500));
                    await steps.StepAsync(bot, CancellationToken.None);
                }
                await Assert.That(state.NeedsFoodBlockedReason).IsNotNull();
                await Assert.That(state.NeedsFarmSoilAttempts).IsEqualTo(BotRoamStepExecutor.NeedsFarmMaxSoilAttempts);
                if (sourceCase == "reached")
                    await Assert.That(MathUtil.CalculateDistance(positionBeforeBlockedWake, sourcePosition, true))
                        .IsLessThanOrEqualTo(GameplayActor.ArrivalRadius);
                else if (sourceCase == "exhausted")
                    await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move && r.Result != ActorLifecycleState.Completed)).IsGreaterThanOrEqualTo(BotRoamStepExecutor.NeedsFarmMaxSoilAttempts);
                else
                    await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move
                        && r.MoveOwner == "OTHER:needs-farm-travel" && r.Result == ActorLifecycleState.TimedOut
                        && r.Failure == ActorFailureReason.Navigation)).IsTrue();
            }
            await Assert.That(state.NeedsLegActive).IsFalse();
            await Assert.That(ReferenceEquals(executor.GetRoamRoute(bot.CharacterId), patrol)).IsTrue();
            if (actor.ActiveRequest == null)
            {
                // A failure settled after Tick releases the source now; the
                // next ordinary wake dispatches the restored useful patrol.
                clock.Advance(TimeSpan.FromMilliseconds(50));
                await steps.StepAsync(bot, CancellationToken.None);
            }
            var usefulMove = actor.ActiveRequest!;
            var before = actor.Character.Transform.World.Position;
            var stops = actor.AuditTrace.Count(r => r.Action == ActorActionType.Stop);
            for (var i = 0; i < 4; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(sourceCase == "timed-out" ? 50 : 500));
                await steps.StepAsync(bot, CancellationToken.None);
            }
            await Assert.That(actor.Character.Transform.World.Position != before).IsTrue();
            await Assert.That(ReferenceEquals(actor.ActiveRequest, usefulMove) && !usefulMove.IsTerminal).IsTrue();
            await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Stop)).IsEqualTo(stops);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 8005)).IsEqualTo(10);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 8326)).IsEqualTo(0);
            await Assert.That(actor.Character.Money).IsEqualTo(700L);
            if (validSource && sourceCase != "timed-out")
            {
                // Even after the static source is spent, a newly observed real
                // merchant is a relevant change and retains ordinary approach.
                var livePosition = actor.Character.Transform.World.Position + new Vector3(20, 0, 0);
                var merchantId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1921, packId: 145);
                GameplayActorTestRig.SetNpcPosition(session, merchantId, livePosition);
                merchant = session.World.GetNpc(merchantId)!;
                await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await steps.StepAsync(bot, CancellationToken.None);
                var approach = actor.ActiveRequest!;
                var distance = MathUtil.CalculateDistance(actor.Character.Transform.World.Position, livePosition, true);
                await Assert.That(approach.MoveOwner).IsEqualTo("OTHER:needs-farm-travel");
                stops = actor.AuditTrace.Count(r => r.Action == ActorActionType.Stop);
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await steps.StepAsync(bot, CancellationToken.None);
                await Assert.That(ReferenceEquals(actor.ActiveRequest, approach) && !approach.IsTerminal).IsTrue();
                await Assert.That(MathUtil.CalculateDistance(actor.Character.Transform.World.Position, livePosition, true)).IsLessThan(distance);
                await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Stop)).IsEqualTo(stops);
            }
        }
        finally
        {
            actor.PreemptCurrent("known knife source fixture cleanup");
            if (previousTemplate == null) templates.Remove(1921);
            else templates[1921] = previousTemplate;
            if (previousPack == null) goods.Remove(145);
            else goods[145] = previousPack;
        }
    }

    [Test]
    public async Task BreadMode_KnownKnifeHintFailsAfterMerchantAppears_StationaryProfileAdmitsActualSeller()
    {
        // Reconstructed needs-only public-seam profile: deliberately no
        // PresenceRoam module, so a missing route cannot silently re-arm a
        // default patrol and invalidate a bad refusal through displacement.
        var (actor, session) = CreateActorOnUniqueWorld("food-hint-live-seller");
        using var fixture = new HardBreadFixture(actor);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 700);
        actor.Character.LaborPower = 96;
        GameplayActorTestRig.GrantItem(actor, NeedsFarmModuleOptions.BarleyItemId, 10);
        GameplayActorTestRig.SeedTradeItemTemplate(8326, 50, 10, false);
        var templates = (Dictionary<uint, NpcTemplate>)GameplayActorTestRig.GetField(NpcManager.Instance, "<Templates>k__BackingField");
        var goods = (Dictionary<uint, MerchantGoods>)GameplayActorTestRig.GetField(NpcManager.Instance, "<Goods>k__BackingField");
        var previousTemplate = templates.GetValueOrDefault(1921u);
        var previousPack = goods.GetValueOrDefault(145u);
        templates[1921] = new NpcTemplate { Id = 1921, Merchant = true, MerchantPackId = 145 };
        var pack = new MerchantGoods(145);
        pack.AddItemToStock(8326, 0);
        goods[145] = pack;
        var sourcePosition = TestPosition + new Vector3(100, 0, 0);
        var sourceMap = (Dictionary<uint, List<NpcSpawner>>)GameplayActorTestRig.GetField(session.World.SpawnManager, "<NpcSpawners>k__BackingField");
        sourceMap[98145] = [new NpcSpawner
        {
            Id = 98145, UnitId = 1921, ParentWorld = session.World,
            Position = new WorldSpawnPosition { WorldId = session.World.Id, X = sourcePosition.X, Y = sourcePosition.Y, Z = sourcePosition.Z },
            Template = new NpcSpawnerTemplate { Npcs = [new NpcSpawnerNpc { MemberId = 1921, MemberType = "Npc" }] }
        }];
        var clock = new FakeTimeProvider();
        var bot = NewBot(actor);
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        Npc? merchant = null;
        BotGoalArbiter arbiter = null!;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor, TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100), RoamLegTimeout = TimeSpan.FromMilliseconds(250),
            ActiveActivityProvider = id => arbiter.GetActiveActivity(id),
            GroundHeightProvider = (_, _) => TestPosition.Z,
            NearbyNpcProvider = (character, radius) => merchant != null
                && MathUtil.CalculateDistance(character.Transform.World.Position, merchant.Transform.World.Position, true) <= radius
                ? [merchant] : [],
            NearbyDoodadProvider = (_, _) => []
        };
        var module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        arbiter = new BotGoalArbiter([module]);
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);
        try
        {
            // No patrol is installed and no fallback module can install one.
            await steps.StepAsync(bot, CancellationToken.None);
            var hint = actor.ActiveRequest!;
            await Assert.That(hint.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(hint.MoveOwner).IsEqualTo("OTHER:needs-farm-travel");
            await Assert.That(hint.TargetId).IsEqualTo(0u);
            var stationaryPosition = actor.Character.Transform.World.Position;
            var merchantId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: 1921, packId: 145);
            GameplayActorTestRig.SetNpcPosition(session, merchantId, stationaryPosition + new Vector3(2, 0, 0));
            merchant = session.World.GetNpc(merchantId)!;
            await Assert.That(MathUtil.CalculateDistance(stationaryPosition, merchant.Transform.World.Position, true))
                .IsLessThanOrEqualTo(GameplayActor.MaxShopRange);

            // Reveal precedes the genuine native timeout, unlike the earlier
            // changed-availability control that revealed only after blocking.
            clock.Advance(TimeSpan.FromMilliseconds(200));
            await steps.StepAsync(bot, CancellationToken.None);
            await Assert.That(hint.State).IsEqualTo(ActorLifecycleState.TimedOut);
            await Assert.That(hint.Failure).IsEqualTo(ActorFailureReason.Navigation);
            await Assert.That(actor.ActiveRequest).IsNull();
            await Assert.That(executor.GetRoamRoute(bot.CharacterId)).IsNull();
            await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(stationaryPosition);
            await Assert.That(executor.GetBotState(bot.CharacterId)!.NeedsFoodBlockedReason).IsNull();
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();

            clock.Advance(TimeSpan.FromMilliseconds(100));
            await steps.StepAsync(bot, CancellationToken.None);
            var buy = actor.AuditTrace.Single(r => r.Action == ActorActionType.Buy);
            await Assert.That(buy.Result).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(buy.TargetId).IsEqualTo(merchantId);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 8326)).IsEqualTo(1);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 8005)).IsEqualTo(10);
            await Assert.That(GameplayActorTestRig.BagCount(actor, 30902)).IsEqualTo(0);
            await Assert.That(actor.Character.Money).IsEqualTo(650L);
            await Assert.That(actor.Character.LaborPower).IsEqualTo((short)96);
            await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(stationaryPosition);
            await Assert.That(executor.GetRoamRoute(bot.CharacterId)).IsNull();
            await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(1);
        }
        finally
        {
            actor.PreemptCurrent("merchant-before-hint-failure fixture cleanup");
            if (previousTemplate == null) templates.Remove(1921);
            else templates[1921] = previousTemplate;
            if (previousPack == null) goods.Remove(145);
            else goods[145] = previousPack;
        }
    }

    [Test]
    public async Task RefusedFarmTravelLegTwice_StandsAsideBoundedly_AndTheLowerBandTakesOver()
    {
        // The farm module acts on the outcome the executor itself reported through
        // T4's channel: the same travel leg refused the same way twice means the
        // farm's own leg keeps failing, so it denies with an observable reason and
        // the lower band (roam) takes over instead of the same farm wake
        // repeating. Only a hard refusal counts - a timed-out leg keeps the
        // executor's own attempt bookkeeping as its owner. The rig rejects the
        // request the executor issued; the outcome is built by the real executor
        // from the real leg's terminal state and no reason is injected.
        var (actor, session) = CreateActorOnUniqueWorld("food-refused-travel");
        using var fixture = new HardBreadFixture(actor);
        GameplayActorTestRig.SetPosition(actor, TestPosition);
        GameplayActorTestRig.SetMoney(actor, 700);
        actor.Character.LaborPower = 96;
        GameplayActorTestRig.GrantItem(actor, NeedsFarmModuleOptions.BarleyItemId, 10);
        GameplayActorTestRig.SeedTradeItemTemplate(8326, 50, 10, false);
        var templates = (Dictionary<uint, NpcTemplate>)GameplayActorTestRig.GetField(NpcManager.Instance, "<Templates>k__BackingField");
        var goods = (Dictionary<uint, MerchantGoods>)GameplayActorTestRig.GetField(NpcManager.Instance, "<Goods>k__BackingField");
        var previousTemplate = templates.GetValueOrDefault(1921u);
        var previousPack = goods.GetValueOrDefault(145u);
        templates[1921] = new NpcTemplate { Id = 1921, Merchant = true, MerchantPackId = 145 };
        var pack = new MerchantGoods(145);
        pack.AddItemToStock(8326, 0);
        goods[145] = pack;
        var sourcePosition = TestPosition + new Vector3(100, 0, 0);
        var sourceMap = (Dictionary<uint, List<NpcSpawner>>)GameplayActorTestRig.GetField(session.World.SpawnManager, "<NpcSpawners>k__BackingField");
        sourceMap[98145] = [new NpcSpawner
        {
            Id = 98145, UnitId = 1921, ParentWorld = session.World,
            Position = new WorldSpawnPosition { WorldId = session.World.Id, X = sourcePosition.X, Y = sourcePosition.Y, Z = sourcePosition.Z },
            Template = new NpcSpawnerTemplate { Npcs = [new NpcSpawnerNpc { MemberId = 1921, MemberType = "Npc" }] }
        }];
        var clock = new FakeTimeProvider();
        var bot = NewBot(actor);
        var options = new NeedsFarmModuleOptions { Enabled = true, ProduceHardBread = true };
        Npc? merchant = null;
        BotGoalArbiter arbiter = null!;
        BotRoamStepExecutor executor = new()
        {
            NeedsFarmOptions = options, ActorFactory = _ => actor, TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100), RoamSpeed = 5f,
            RoamLegTimeout = TimeSpan.FromSeconds(60),
            ActiveActivityProvider = id => arbiter.GetActiveActivity(id),
            GroundHeightProvider = (_, _) => TestPosition.Z,
            NearbyNpcProvider = (character, radius) => merchant != null
                && MathUtil.CalculateDistance(character.Transform.World.Position, merchant.Transform.World.Position, true) <= radius
                ? [merchant] : [],
            NearbyDoodadProvider = (_, _) => []
        };
        var module = new NeedsFarmActivityModule(options) { FoodEligibilityProvider = executor.FoodBlockedReason };
        var roam = new PresenceRoamProbe();
        arbiter = new BotGoalArbiter([module, roam]);
        // Production parity (Program.cs): the executor is the only thing that sees
        // a leg end, so it reports it to the arbiter that hands it to the owner.
        executor.LegOutcomeSink = arbiter.ReportLegOutcome;
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);
        try
        {
            await steps.StepAsync(bot, CancellationToken.None);
            var travel = actor.ActiveRequest!;
            await Assert.That(travel.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(travel.MoveOwner).IsEqualTo("OTHER:needs-farm-travel");

            var refusals = 0;
            var reasons = new List<string>();
            for (var wake = 0; wake < 12 && !reasons.Any(r => r.Contains("standing aside", StringComparison.Ordinal)); wake++)
            {
                if (actor.ActiveRequest is { IsTerminal: false } request)
                {
                    request.Reject(ActorFailureReason.Navigation, "no path to the seed source");
                    refusals++;
                }
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await steps.StepAsync(bot, CancellationToken.None);
                reasons.Add(steps.GetLastDecision(bot.CharacterId)?.Reason ?? "");
            }

            await Assert.That(refusals).IsGreaterThanOrEqualTo(2);
            var standAside = reasons.First(r => r.Contains("standing aside", StringComparison.Ordinal));
            // Bounded and observable: the reason names the count, the failure and
            // how long the farm will stay out.
            await Assert.That(standAside).Contains("failed 2 times the same way");
            await Assert.That(standAside).Contains("no path to the seed source");
            await Assert.That(standAside).Contains($"{options.LegFailureStandAsideWakes} elections");
            // The lower band is in charge, not the same farm wake.
            await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("presence.roam");
            await Assert.That(roam.Activations).IsGreaterThanOrEqualTo(1);

            // The stand-aside holds for its bounded number of elections, then the
            // farm tries again - it is not a permanent disable.
            var heldWakes = 0;
            for (var wake = 0; wake < options.LegFailureStandAsideWakes + 4; wake++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await steps.StepAsync(bot, CancellationToken.None);
                var reason = steps.GetLastDecision(bot.CharacterId)?.Reason ?? "";
                if (reason.Contains("standing aside", StringComparison.Ordinal))
                {
                    heldWakes++;
                    continue;
                }
                break;
            }
            await Assert.That(heldWakes).IsGreaterThan(0);
            var after = steps.GetLastDecision(bot.CharacterId)?.Reason ?? "";
            await Assert.That(after.Contains("standing aside", StringComparison.Ordinal)).IsFalse();
        }
        finally
        {
            actor.PreemptCurrent("refused farm travel fixture cleanup");
            if (previousTemplate == null) templates.Remove(1921);
            else templates[1921] = previousTemplate;
            if (previousPack == null) goods.Remove(145);
            else goods[145] = previousPack;
        }
    }

    // Explicit native fixture metadata; canonical production timing/yields belong
    // to the isolated earned-input test, not these short policy controls.
    internal sealed class HardBreadFixture : IDisposable
    {
        private readonly Dictionary<uint, AAEmu.Game.Models.Game.Items.Templates.ItemTemplate> _items =
            (Dictionary<uint, AAEmu.Game.Models.Game.Items.Templates.ItemTemplate>)GameplayActorTestRig.GetField(ItemManager.Instance, "_templates");
        private readonly Dictionary<uint, AAEmu.Game.Models.Game.Items.Templates.ItemTemplate?> _before = [];
        private readonly Dictionary<uint, AAEmu.Game.Models.Game.Crafts.Craft> _crafts;
        private readonly AAEmu.Game.Models.Game.Crafts.Craft? _beforeCraft;
        private readonly Dictionary<uint, AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate> _skills;
        private readonly AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate? _beforeBreadSkill;
        public Item Bread { get; }

        public HardBreadFixture(GameplayActor actor)
        {
            GameplayActorTestRig.SeedCraftSurface();
            foreach (var id in new uint[] { 15652, 8005, 8326, 30902, 17664 })
            {
                _before[id] = _items.GetValueOrDefault(id);
                _items[id] = new AAEmu.Game.Models.Game.Items.Templates.ItemTemplate { Id = id, MaxCount = 1000 };
            }
            _skills = (Dictionary<uint, AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate>)GameplayActorTestRig.GetField(SkillManager.Instance, "_skills");
            _beforeBreadSkill = _skills.GetValueOrDefault(18664u);
            _skills.Remove(18664);
            Bread = OrdinaryRecoveryTestRig.StockHeal(actor, 17664, 40);
            Bread.Count = 0;
            _crafts = (Dictionary<uint, AAEmu.Game.Models.Game.Crafts.Craft>)GameplayActorTestRig.GetField(CraftManager.Instance, "_crafts");
            _beforeCraft = _crafts.GetValueOrDefault(6751u);
            _crafts[6751] = new AAEmu.Game.Models.Game.Crafts.Craft
            {
                Id = 6751, SkillId = GameplayActorTestRig.CraftTestSkillId, ReqDoodadId = 525,
                CraftMaterials = [new AAEmu.Game.Models.Game.Crafts.CraftMaterial { ItemId = 30902, Amount = 4 }],
                CraftProducts = [new AAEmu.Game.Models.Game.Crafts.CraftProduct { ItemId = 17664, Amount = 10, Rate = 100 }]
            };
        }

        public void Dispose()
        {
            foreach (var (id, previous) in _before)
            {
                if (previous == null) _items.Remove(id);
                else _items[id] = previous;
            }
            if (_beforeCraft == null) _crafts.Remove(6751);
            else _crafts[6751] = _beforeCraft;
            if (_beforeBreadSkill == null) _skills.Remove(18664);
            else _skills[18664] = _beforeBreadSkill;
        }
    }
}
