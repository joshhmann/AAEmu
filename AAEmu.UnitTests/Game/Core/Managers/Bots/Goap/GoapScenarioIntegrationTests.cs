#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using HeadlessSession = AAEmu.Game.Models.Game.Bots.HeadlessSession;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.Goap;

public class GoapScenarioIntegrationTests
{
    #region Test Actor Rig

    private sealed class TestRecordingActor : IGameplayActor
    {
        public uint ActorId => 1;
        public Character Character { get; }
        public ActorRequest? ActiveRequest { get; private set; }
        public IReadOnlyList<ActorAuditRecord> AuditTrace => [];
        public List<ActorRequest> DispatchedRequests { get; } = [];

        public TestRecordingActor(Character character)
        {
            Character = character;
        }

        public void SetPendingDecision(string? goal, string? policy, int candidates, int rejections, string? seed) { }
        public ActorObservation Observe() => new() { ActorId = ActorId };
        public void Tick(TimeSpan elapsed) { }

        private ActorRequest Track(ActorRequest request)
        {
            ActiveRequest = request;
            DispatchedRequests.Add(request);
            return request;
        }

        public ActorRequest NavigateTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Move, 0, destination, 0, timeout, null, idempotencyKey);
            req.Accept("Accepted navigation");
            req.Start("Started movement leg");
            return Track(req);
        }

        public ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, merchantNpcObjId, null, 0, null, payload: itemTemplateId, idempotencyKey);
            req.Accept("Accepted buy");
            req.Start("Executing merchant trade");
            return Track(req);
        }

        public ActorRequest Plant(uint itemTemplateId, Vector3 position, float zRot = 0f, float scale = 1f, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, 0, position, 0, null, payload: itemTemplateId, idempotencyKey);
            req.Accept("Accepted plant");
            req.Start("Planting doodad");
            return Track(req);
        }

        public ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, 0, null, null, idempotencyKey);
            req.Accept("Accepted harvest");
            req.Start("Chopping tree");
            return Track(req);
        }

        public ActorRequest UseItem(uint itemTemplateId, uint targetObjId = 0, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, targetObjId, null, 0, null, payload: itemTemplateId, idempotencyKey);
            req.Accept("Accepted use");
            req.Start("Using consumable");
            return Track(req);
        }

        public ActorRequest SetTarget(uint targetObjId)
        {
            var req = new ActorRequest(ActorActionType.Target, targetObjId, null, 0, null);
            req.Accept("Accepted target");
            req.Start("Target locked");
            return Track(req);
        }

        public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Move, targetObjId, null, 0, timeout, null, idempotencyKey);
            req.Accept("Accepted approach");
            req.Start("Approaching target");
            return Track(req);
        }

        public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Cast, targetObjId, null, skillId, null, null, idempotencyKey);
            req.Accept("Accepted cast");
            req.Start("Casting combo");
            return Track(req);
        }

        public ActorRequest Loot(uint lootOwnerObjId, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Loot, lootOwnerObjId, null, 0, null, null, idempotencyKey);
            req.Accept("Accepted loot");
            req.Start("Looting corpse");
            return Track(req);
        }

        private static ActorRequest Unsupported() => throw new NotSupportedException();
        public ActorRequest MoveTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest NavigateToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Stop() => Unsupported();
        public ActorRequest CastAt(uint skillId, Vector3 position, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AutoAttack(uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest StopAutoAttack(string? idempotencyKey = null) => Unsupported();
        public ActorRequest Interact(uint doodadObjId, uint skillId = 0, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, skillId, null, null, idempotencyKey);
            req.Accept("Accepted interact");
            req.Start("Interacting");
            return Track(req);
        }
        public ActorRequest Equip(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyInvite(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyAccept(string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionCreate(string name, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionInvite(string invitedName, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionAccept(AAEmu.Game.Models.StaticValues.FactionsEnum expeditionId, uint inviterId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionLeave(string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeOffer(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradePutup(uint itemTemplateId, int count, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeLockOk(string? idempotencyKey = null) => Unsupported();
        public ActorRequest Mount(uint mateObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Dismount(uint mateObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DismissMate(uint tlId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BoardVehicle(uint vehicleObjId, AttachPointKind attachPoint = AttachPointKind.Driver, string? idempotencyKey = null) => Unsupported();
        public ActorRequest UnboardVehicle(uint vehicleObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DriveVehicle(uint vehicleObjId, Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuildHouse(uint designId, uint designItemTemplateId, Vector3 position, float zRot = 0f, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public bool Interrupt(Guid traceId) => false;
        public ActorRequest AcceptQuest(uint questId, AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverQuests(uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverSelfQuests(string? idempotencyKey = null) => Unsupported();
        public ActorRequest PlayCinema(uint cinemaId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest SellSpecialty(uint merchantNpcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Repair(uint blacksmithNpcObjId, ulong itemId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, AAEmu.Game.Models.Game.Auction.AuctionDuration duration, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null) => Unsupported();
        public ActorRequest InteractWith(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Talk(uint npcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest InteractNpc(uint npcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorAuditRecord? FindByKey(string idempotencyKey) => null;
    }

    #endregion

    private static PlayerBotRuntime CreateBot(string name, Vector3 startPos, int hp = 1000, int maxHp = 1000, short labor = 100, long money = 500)
    {
        GameplayActorTestRig.Seed();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, 1, Race.Nuian);
        var ch = session.Character;
        ch.Hp = hp;
        ch.MaxHp = maxHp;
        ch.Mp = 500;
        ch.MaxMp = 500;
        ch.LaborPower = labor;
        ch.Money = money;
        ch.Transform.World.Position = startPos;
        return new PlayerBotRuntime(ch, "scenario-tests");
    }

    [Test]
    public async Task ScenarioA_CleanFarmingSuccess_RequiresObservedEffectsForEveryStep()
    {
        // 1. Initial State: healthy bot, 100 labor, 500 gold, 0 saplings, at town square
        var merchantPos = new Vector3(14485f, 14411f, 112.5f);
        var farmPos = new Vector3(14210.5f, 14680.2f, 123.6f);

        var bot = CreateBot("farm-bot", startPos: new Vector3(14000f, 14000f, 100f));
        var actor = new TestRecordingActor(bot.Character);
        var telemetry = new InMemoryGoapTelemetrySink();
        var context = new BotContext();
        // Fixture-gate opt-in: the bag overrides set below describe a SIMULATED
        // inventory (rig world, no merchant trade), and BotMemory only surfaces
        // them once the fixture layer is explicitly opened. Without it the
        // projection reads the real (empty) bag: the resource-correct plan keeps
        // its BuyTreeSaplings step (correct — the bot genuinely has no saplings),
        // the completed purchase never observes its effect, and Step 3's stale
        // expectation of index 2 fails with index 1. The plan shape asserted
        // below is the affordable-path plan for a bot that DOES hold saplings,
        // which is the premise this scenario documents.
        context.Memory.EnableFixtureOverrides();
        context.Memory.KnownSeedMerchantPos = merchantPos;
        context.Memory.TargetWildFarmPos = farmPos;

        var runner = new GoapPlanRunner(telemetrySink: telemetry);

        // Step 1: Tick 1 -> selects PlantWildFarm, generates 4-action plan, begins Action 0 (TravelToSeedMerchant)
        bool live1 = runner.Tick(bot, actor, context);
        await Assert.That(live1).IsTrue();
        await Assert.That(runner.ActiveGoal!.Name).IsEqualTo("PlantWildFarm");
        await Assert.That(runner.ActivePlan!.Actions.Count).IsEqualTo(4);
        await Assert.That(runner.CurrentActionIndex).IsEqualTo(0);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("TravelToSeedMerchant");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);

        // Movement request was dispatched
        await Assert.That(actor.DispatchedRequests.Count).IsEqualTo(1);
        await Assert.That(actor.DispatchedRequests[0].Action).IsEqualTo(ActorActionType.Move);

        // Bot is still far away -> next tick still running (NO premature effect assumption!)
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentActionIndex).IsEqualTo(0);
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);

        // Now bot reaches merchant position in real world
        bot.Character.Transform.World.Position = merchantPos;
        actor.DispatchedRequests[0].Complete();

        // Step 2: Next Tick -> observed state verifies NearSeedMerchant -> Action 0 succeeds! Action 1 (BuyTreeSaplings) starts
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentActionIndex).IsEqualTo(1);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("BuyTreeSaplings");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);

        // Complete the purchase in real game world
        actor.DispatchedRequests[1].Complete();
        context.Memory.HasSaplingsOverride = true; // Saplings actually in bag now

        // Step 3: Next Tick -> observed state verifies HasTreeSaplings -> Action 1 succeeds! Action 2 (HikeToSecretPlateau) starts
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentActionIndex).IsEqualTo(2);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("HikeToSecretPlateau");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);

        // Bot arrives at wild farm POI
        bot.Character.Transform.World.Position = farmPos;
        actor.DispatchedRequests[2].Complete();

        // Step 4: Next Tick -> observed state verifies AtWildFarm -> Action 2 succeeds! Action 3 (PlantSecretGrove) starts
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentActionIndex).IsEqualTo(3);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("PlantSecretGrove");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);

        // Plant request completes in world: grove doodad recorded
        actor.DispatchedRequests[3].Complete();
        context.Memory.RecordGrove(new WildGroveRecord(farmPos, 9001, context.GetUtcNow(), TimeSpan.FromHours(4), 15659));

        // Step 5: Next Tick -> observed state verifies SecretGrovePlanted -> Goal satisfied!
        runner.Tick(bot, actor, context);
        await Assert.That(runner.PrimaryGoal).IsNull();
        await Assert.That(runner.ActivePlan).IsNull();

        // Verify Telemetry Audit Trail
        var events = telemetry.Events;
        await Assert.That(events.Any(e => e.EventType == GoapTelemetryEventType.GoalSelected)).IsTrue();
        await Assert.That(events.Any(e => e.EventType == GoapTelemetryEventType.PlanGenerated)).IsTrue();
        await Assert.That(events.Count(e => e.EventType == GoapTelemetryEventType.ActionSucceeded)).IsEqualTo(4);
        await Assert.That(events.Any(e => e.EventType == GoapTelemetryEventType.GoalSatisfied)).IsTrue();
    }

    [Test]
    public async Task ScenarioB_CombatInterrupt_PreservesPrimaryIntentAndResumesFarming()
    {
        var merchantPos = new Vector3(14485f, 14411f, 112.5f);
        var farmPos = new Vector3(14210.5f, 14680.2f, 123.6f);

        var bot = CreateBot("interrupt-bot", startPos: merchantPos, hp: 1000);
        var actor = new TestRecordingActor(bot.Character);
        var telemetry = new InMemoryGoapTelemetrySink();
        var context = new BotContext();
        // Fixture-gate opt-in (see ScenarioA): the "already has saplings" premise
        // is rig simulation. Gate closed, the bot legitimately has none and the
        // affordable-path plan opens with BuyTreeSaplings instead of the hike.
        context.Memory.EnableFixtureOverrides();
        context.Memory.KnownSeedMerchantPos = merchantPos;
        context.Memory.TargetWildFarmPos = farmPos;
        context.Memory.HasSaplingsOverride = true; // Already has saplings, hiking to farm

        var runner = new GoapPlanRunner(telemetrySink: telemetry);

        // Start primary intent: PlantWildFarm
        runner.SetPrimaryGoal(GoalArbitrator.GoalPlantWildFarm, context);
        runner.Tick(bot, actor, context);

        await Assert.That(runner.ActiveGoal!.Name).IsEqualTo("PlantWildFarm");
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("HikeToSecretPlateau");

        // INJECT COMBAT INTERRUPT: Hostile mob ambushes bot on mountain trail!
        bot.Character.IsInBattle = true;
        var monster = new Npc
        {
            ObjId = 1001,
            Hp = 500,
            MaxHp = 500
        };
        monster.Transform.World.Position = bot.Character.Transform.World.Position + new Vector3(2f, 0f, 0f);
        bot.Character.CurrentTarget = monster;

        // Next Tick -> Watchdog detects hostile threat -> DefendSelf interrupt triggered!
        runner.Tick(bot, actor, context);

        // Crucial Check: Primary intent must be preserved!
        await Assert.That(runner.PrimaryGoal!.Name).IsEqualTo("PlantWildFarm");
        await Assert.That(runner.InterruptGoal!.Name).IsEqualTo("DefendSelf");
        await Assert.That(runner.ActiveGoal!.Name).IsEqualTo("DefendSelf");

        // Verify Interrupt Telemetry
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.InterruptTriggered)).IsTrue();

        // Combat plan generates (combo skill casting)
        runner.Tick(bot, actor, context);

        // Monster is defeated
        monster.Hp = 0;
        await Assert.That(monster.IsDead).IsTrue();

        // Bot loots monster
        context.Memory.HasLootedCurrentTarget = true;
        bot.Character.IsInBattle = false;
        bot.Character.CurrentTarget = null;

        // Next Tick -> DefendSelf goal satisfied -> Primary intent resumed!
        runner.Tick(bot, actor, context);

        await Assert.That(runner.InterruptGoal).IsNull();
        await Assert.That(runner.PrimaryGoal!.Name).IsEqualTo("PlantWildFarm");
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.PrimaryIntentResumed)).IsTrue();

        // Bot arrives at wild farm and finishes planting grove
        bot.Character.Transform.World.Position = farmPos;
        runner.Tick(bot, actor, context); // Regens farming plan from current position
        runner.Tick(bot, actor, context); // Executes plant

        context.Memory.RecordGrove(new WildGroveRecord(farmPos, 8888, context.GetUtcNow(), TimeSpan.FromHours(4), 15659));
        runner.Tick(bot, actor, context); // Verifies secret grove planted

        await Assert.That(runner.PrimaryGoal).IsNull();
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.GoalSatisfied)).IsTrue();
    }

    [Test]
    public async Task ScenarioC_ExecutionFailure_RejectsWithoutFabricatedInventoryAndGracefullyAbandons()
    {
        var merchantPos = new Vector3(14485f, 14411f, 112.5f);
        var farmPos = new Vector3(14210.5f, 14680.2f, 123.6f);

        var bot = CreateBot("fail-bot", startPos: merchantPos, labor: 100, money: 500);
        var actor = new TestRecordingActor(bot.Character);
        var telemetry = new InMemoryGoapTelemetrySink();
        var context = new BotContext { MaxActionRetries = 2 };
        context.Memory.KnownSeedMerchantPos = merchantPos;
        context.Memory.TargetWildFarmPos = farmPos;

        var runner = new GoapPlanRunner(telemetrySink: telemetry) { MaxActionRetries = 2 };
        runner.SetPrimaryGoal(GoalArbitrator.GoalPlantWildFarm, context);

        // Tick 1 -> Generates plan, bot already at merchant -> Action 0 (BuyTreeSaplings) starts
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("BuyTreeSaplings");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);

        // SIMULATE PURCHASE FAILURE: Merchant has no stock / request rejected
        actor.DispatchedRequests[0].Reject(ActorFailureReason.RejectedAction, "Seed merchant out of stock");

        // Next Tick -> Action reports Failed! Inventory has NO saplings (no fabrication!)
        runner.Tick(bot, actor, context);
        await Assert.That(context.Memory.HasSaplingsOverride ?? false).IsFalse();
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.ActionFailed)).IsTrue();

        // Retry 1
        actor.DispatchedRequests[1].Reject(ActorFailureReason.RejectedAction, "Seed merchant out of stock");
        runner.Tick(bot, actor, context);

        // Retry 2 (max retries reached)
        actor.DispatchedRequests[2].Reject(ActorFailureReason.RejectedAction, "Seed merchant out of stock");
        runner.Tick(bot, actor, context);

        // Goal cannot be achieved without saplings -> Gracefully abandoned! Zero infinite loops!
        await Assert.That(runner.PrimaryGoal).IsNull();
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.GoalAbandoned)).IsTrue();
    }

    [Test]
    public async Task ScenarioD_WorldChangesAfterPlanning_InvalidatesActionAndReplansSafely()
    {
        var merchantPos = new Vector3(14485f, 14411f, 112.5f);
        var farmPos = new Vector3(14210.5f, 14680.2f, 123.6f);

        var bot = CreateBot("invalidation-bot", startPos: merchantPos);
        var actor = new TestRecordingActor(bot.Character);
        var telemetry = new InMemoryGoapTelemetrySink();
        var context = new BotContext();
        // Fixture-gate opt-in (see ScenarioA): "bot already has saplings" is rig
        // simulation, not observation.
        context.Memory.EnableFixtureOverrides();
        context.Memory.KnownSeedMerchantPos = merchantPos;
        context.Memory.TargetWildFarmPos = farmPos;
        context.Memory.HasSaplingsOverride = true; // Bot already has saplings

        var runner = new GoapPlanRunner(telemetrySink: telemetry);
        runner.SetPrimaryGoal(GoalArbitrator.GoalPlantWildFarm, context);

        // Tick 1 -> Hiking to wild farm
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("HikeToSecretPlateau");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);

        // WORLD CHANGE EVENT: Enemy player builds on the plateau, invalidating the secret farm POI!
        context.Memory.TargetWildFarmPoiInvalidated = true;

        // Next Tick -> Action notices invalid destination -> ActionInvalidated emitted!
        runner.Tick(bot, actor, context);

        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.ActionInvalidated)).IsTrue();
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.ReplanTriggered)).IsTrue();

        // Since POI is invalidated, arbitrator marks PlantWildFarm as unachievable and falls back safely
        runner.Tick(bot, actor, context);
        await Assert.That(runner.PrimaryGoal?.Name).IsNotEqualTo("PlantWildFarm");
    }
}
