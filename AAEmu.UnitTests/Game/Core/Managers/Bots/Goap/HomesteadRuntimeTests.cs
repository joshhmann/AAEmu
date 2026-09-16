#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using HeadlessSession = AAEmu.Game.Models.Game.Bots.HeadlessSession;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.Goap;

public class HomesteadRuntimeTests
{
    #region Test Recording Actor

    private sealed class TestHomesteadRecordingActor : IGameplayActor
    {
        public uint ActorId => 101;
        public Character Character { get; }
        public ActorRequest? ActiveRequest { get; private set; }
        public IReadOnlyList<ActorAuditRecord> AuditTrace => [];
        public List<ActorRequest> DispatchedRequests { get; } = [];

        public TestHomesteadRecordingActor(Character character)
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
            req.Start("Started movement");
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
            req.Start("Planting crop on plot");
            return Track(req);
        }

        public ActorRequest InteractDoodad(uint doodadObjId, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, 0, null, null, idempotencyKey);
            req.Accept("Accepted doodad interaction");
            req.Start("Interacting with doodad/plot");
            return Track(req);
        }

        public ActorRequest CraftItem(uint recipeId, int quantity = 1, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, 0, null, 0, null, payload: recipeId, idempotencyKey);
            req.Accept("Accepted craft");
            req.Start("Crafting material pack");
            return Track(req);
        }

        public ActorRequest BuildHouse(uint designId, uint designItemTemplateId, Vector3 position, float zRot = 0f, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, 0, position, 0, null, payload: designId, idempotencyKey);
            req.Accept("Accepted house build");
            req.Start("Placing house/garden frame");
            return Track(req);
        }

        public ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, 0, null, null, idempotencyKey);
            req.Accept("Accepted harvest");
            req.Start("Chopping mature tree");
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
            req.Start("Casting skill");
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
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, skillId, TimeSpan.FromSeconds(5), null, idempotencyKey);
            req.Accept("Accepted interact");
            req.Start($"Interacting with doodad {doodadObjId}");
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
        public ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Craft, doodadObjId, null, craftId, timeout ?? TimeSpan.FromSeconds(5), null, idempotencyKey);
            req.Accept("Accepted craft");
            req.Start($"Crafting recipe {craftId}");
            return Track(req);
        }
        public ActorRequest DriveVehicle(uint vehicleObjId, Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null) => Unsupported();
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
        return new PlayerBotRuntime(ch, "homestead-tests");
    }

    [Test]
    public async Task BotWorldState_HomesteadBitFlags_SetAndSatisfiesCorrectly()
    {
        var state = BotWorldState.Empty
            .With(BotWorldState.HasScarecrowDesign)
            .With(BotWorldState.HasTaxCertificates)
            .With(BotWorldState.NearHousingZone)
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.HasTimber)
            .With(BotWorldState.HasBuildingMaterials)
            .With(BotWorldState.NearWorkbench)
            .With(BotWorldState.NearHomeSite)
            .With(BotWorldState.HomeConstructed);

        await Assert.That(state.Has(BotWorldState.HasScarecrowDesign)).IsTrue();
        await Assert.That(state.Has(BotWorldState.HasTaxCertificates)).IsTrue();
        await Assert.That(state.Has(BotWorldState.NearHousingZone)).IsTrue();
        await Assert.That(state.Has(BotWorldState.HasLandPlot)).IsTrue();
        await Assert.That(state.Has(BotWorldState.HasTimber)).IsTrue();
        await Assert.That(state.Has(BotWorldState.HasBuildingMaterials)).IsTrue();
        await Assert.That(state.Has(BotWorldState.NearWorkbench)).IsTrue();
        await Assert.That(state.Has(BotWorldState.NearHomeSite)).IsTrue();
        await Assert.That(state.Has(BotWorldState.HomeConstructed)).IsTrue();

        var plotReq = BotWorldState.Empty.With(BotWorldState.HasLandPlot);
        await Assert.That(state.Satisfies(plotReq)).IsTrue();

        var houseReq = BotWorldState.Empty.With(BotWorldState.HomeConstructed);
        await Assert.That(state.Satisfies(houseReq)).IsTrue();

        var emptyState = BotWorldState.Empty;
        await Assert.That(emptyState.Has(BotWorldState.HasLandPlot)).IsFalse();
        await Assert.That(emptyState.Satisfies(plotReq)).IsFalse();
    }

    [Test]
    public async Task GoapPlanner_StarterToClaimHomestead_DiscoversOrderedPlan()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty;
        var goal = GoalArbitrator.GoalClaimHomestead;

        var actions = new List<IGoapAction>
        {
            new AcquireScarecrowAction(),
            new TravelToHousingZoneAction(),
            new SurveyAndPlacePlotAction()
        };

        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(3);
        await Assert.That(result.Actions[0].Name).IsEqualTo("AcquireScarecrow");
        await Assert.That(result.Actions[1].Name).IsEqualTo("TravelToHousingZone");
        await Assert.That(result.Actions[2].Name).IsEqualTo("SurveyAndPlacePlot");
        await Assert.That(result.TotalCost).IsEqualTo(9.0f); // 2.0 + 3.0 + 4.0
    }

    [Test]
    public async Task GoapPlanner_CultivatePlotToHomeConstruction_DiscoversFullChain()
    {
        var planner = new GoapPlanner();
        var startState = BotWorldState.Empty
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.NearHomeSite)
            .With(BotWorldState.HasTreeSaplings);

        var goal = GoalArbitrator.GoalErectHome;

        var actions = GoapActionRegistry.Instance.HomesteadActions;

        var result = planner.Plan(null, startState, goal, actions);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Actions.Count).IsEqualTo(6);
        await Assert.That(result.Actions[0].Name).IsEqualTo("PlantOnPlot");
        await Assert.That(result.Actions[1].Name).IsEqualTo("HarvestTimber");
        await Assert.That(result.Actions[2].Name).IsEqualTo("TravelToWorkbench");
        await Assert.That(result.Actions[3].Name).IsEqualTo("CraftMaterialPack");
        await Assert.That(result.Actions[4].Name).IsEqualTo("TravelToHomeSite");
        await Assert.That(result.Actions[5].Name).IsEqualTo("ConstructHome");
    }

    [Test]
    public async Task GoapPlanner_FullProgressionFromStarterToErectHome_ChainsAcrossDomains()
    {
        var planner = new GoapPlanner(maxExpansions: 5000);
        var startState = BotWorldState.Empty.WithGold(100);
        var goal = GoalArbitrator.GoalErectHome;

        var actions = GoapActionRegistry.Instance.GetAllActions();

        var result = planner.Plan(null, startState, goal, actions);
        await Assert.That(result.Success).IsTrue();
        var actionNames = result.Actions.Select(a => a.Name).ToList();

        // Must acquire scarecrow before claiming plot
        var acquireIdx = actionNames.IndexOf("AcquireScarecrow");
        var surveyIdx = actionNames.IndexOf("SurveyAndPlacePlot");
        await Assert.That(acquireIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(surveyIdx).IsGreaterThan(acquireIdx);

        // Must plant and harvest before crafting material pack
        var plantIdx = actionNames.IndexOf("PlantOnPlot");
        var harvestIdx = actionNames.IndexOf("HarvestTimber");
        var craftIdx = actionNames.IndexOf("CraftMaterialPack");
        await Assert.That(plantIdx).IsGreaterThan(surveyIdx);
        await Assert.That(harvestIdx).IsGreaterThan(plantIdx);
        await Assert.That(craftIdx).IsGreaterThan(harvestIdx);

        // Must travel back to home site and construct home as terminal step
        var travelHomeIdx = actionNames.LastIndexOf("TravelToHomeSite");
        var constructIdx = actionNames.IndexOf("ConstructHome");
        await Assert.That(travelHomeIdx).IsGreaterThan(craftIdx);
        await Assert.That(constructIdx).IsGreaterThan(travelHomeIdx);
        await Assert.That(constructIdx).IsEqualTo(actionNames.Count - 1);
    }

    [Test]
    public async Task GoalArbitrator_ArbitratesHomesteadProgressionStages()
    {
        var arbitrator = new GoalArbitrator();
        var bot = CreateBot("prog-bot", startPos: Vector3.Zero);
        var context = new BotContext();

        // 1. Starter bot with scarecrow design -> ClaimHomestead
        var stateStarter = BotWorldState.Empty.With(BotWorldState.HasScarecrowDesign);
        var goal1 = arbitrator.ArbitrateGoal(bot, stateStarter, context, null);
        await Assert.That(goal1).IsNotNull();
        await Assert.That(goal1!.Name).IsEqualTo("ClaimHomestead");

        // 2. Bot with land plot but no materials -> CultivatePlot
        var stateHasPlot = BotWorldState.Empty.With(BotWorldState.HasLandPlot);
        var goal2 = arbitrator.ArbitrateGoal(bot, stateHasPlot, context, null);
        await Assert.That(goal2).IsNotNull();
        await Assert.That(goal2!.Name).IsEqualTo("CultivatePlot");

        // 3. Bot with land plot and materials ready -> ErectHome
        var stateReadyToBuild = BotWorldState.Empty
            .With(BotWorldState.HasLandPlot)
            .With(BotWorldState.HasBuildingMaterials);
        var goal3 = arbitrator.ArbitrateGoal(bot, stateReadyToBuild, context, null);
        await Assert.That(goal3).IsNotNull();
        await Assert.That(goal3!.Name).IsEqualTo("ErectHome");

        // 4. Low health preempts homestead goals -> Recover
        var stateLowHealth = stateReadyToBuild.With(BotWorldState.LowHealth);
        var goalEmergency = arbitrator.ArbitrateGoal(bot, stateLowHealth, context, null);
        await Assert.That(goalEmergency).IsNotNull();
        await Assert.That(goalEmergency!.Name).IsEqualTo("Recover");

        // 5. In combat preempts homestead goals -> DefendSelf
        var stateCombat = stateReadyToBuild.With(BotWorldState.InCombat);
        var goalCombat = arbitrator.ArbitrateGoal(bot, stateCombat, context, null);
        await Assert.That(goalCombat).IsNotNull();
        await Assert.That(goalCombat!.Name).IsEqualTo("DefendSelf");
    }

    [Test]
    public async Task GoapPlanRunner_MultiTickClaimHomesteadExecution_StepByStep()
    {
        var housingZone = new Vector3(20450f, 10820f, 130f);
        var bot = CreateBot("claim-bot", startPos: new Vector3(20000f, 10000f, 100f));
        var actor = new TestHomesteadRecordingActor(bot.Character);
        var telemetry = new InMemoryGoapTelemetrySink();
        var context = new BotContext();
        context.Memory.KnownHousingZonePos = housingZone;

        var runner = new GoapPlanRunner(telemetrySink: telemetry);
        runner.SetPrimaryGoal(GoalArbitrator.GoalClaimHomestead, context);

        // Tick 1: Selects GoalClaimHomestead, plans 3 actions, dispatches Action 0: AcquireScarecrow
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("AcquireScarecrow");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);
        await Assert.That(actor.DispatchedRequests.Count).IsEqualTo(1);
        await Assert.That(actor.DispatchedRequests[0].Action).IsEqualTo(ActorActionType.Interact);

        // SIMULATE WORLD STATE: Character receives Scarecrow Garden design and tax certificates
        context.Memory.HasScarecrowDesignOverride = true;
        context.Memory.HasTaxCertificatesOverride = true;
        actor.DispatchedRequests[0].Complete("Received starter scarecrow design");

        // Tick 2: Action 0 succeeds, advances to Action 1: TravelToHousingZone
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("TravelToHousingZone");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);
        await Assert.That(actor.DispatchedRequests.Count).IsEqualTo(2);
        await Assert.That(actor.DispatchedRequests[1].Action).IsEqualTo(ActorActionType.Move);

        // SIMULATE MOVEMENT COMPLETION: Bot arrives at housing zone
        bot.Character.Transform.World.Position = housingZone;
        actor.DispatchedRequests[1].Complete("Arrived at residential zone");

        // Tick 3: Action 1 succeeds, advances to Action 2: SurveyAndPlacePlot
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("SurveyAndPlacePlot");
        await Assert.That(runner.CurrentActionStatus).IsEqualTo(GoapActionStatus.Running);
        await Assert.That(actor.DispatchedRequests.Count).IsEqualTo(3);
        await Assert.That(actor.DispatchedRequests[2].Action).IsEqualTo(ActorActionType.Interact); // BuildHouse

        // SIMULATE LAND CLAIM: House/Garden successfully placed on world
        context.Memory.HasLandPlotOverride = true;
        context.Memory.OwnedHouseId = 5001;
        actor.DispatchedRequests[2].Complete("Scarecrow garden built on residential plot");

        // Tick 4: Action 2 succeeds, GoalSatisfied emitted!
        runner.Tick(bot, actor, context);
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.GoalSatisfied && e.GoalName == "ClaimHomestead")).IsTrue();
        await Assert.That(runner.PrimaryGoal).IsNull();
        await Assert.That(runner.ActivePlan).IsNull();
    }

    [Test]
    public async Task GoapPlanRunner_MultiTickErectHomeExecution_FullCycle()
    {
        var homePos = new Vector3(20455f, 10825f, 130f);
        var workbenchPos = new Vector3(20480f, 10860f, 130f);

        var bot = CreateBot("builder-bot", startPos: homePos);
        var actor = new TestHomesteadRecordingActor(bot.Character);
        var telemetry = new InMemoryGoapTelemetrySink();
        var context = new BotContext();
        context.Memory.OwnedHouseId = 7001;
        context.Memory.OwnedHousePos = homePos;
        context.Memory.KnownWorkbenchPos = workbenchPos;

        // Pre-condition: bot owns plot, is at home site, and has tree saplings in bag
        context.Memory.HasLandPlotOverride = true;
        context.Memory.HasSaplingsOverride = true;

        var runner = new GoapPlanRunner(telemetrySink: telemetry);
        runner.SetPrimaryGoal(GoalArbitrator.GoalErectHome, context);

        // Step 1: PlantOnPlot
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("PlantOnPlot");
        context.Memory.AddGrove(1, homePos, DateTime.UtcNow.AddMinutes(-30));
        actor.DispatchedRequests[^1].Complete("Trees planted on plot");

        // Step 2: HarvestTimber
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("HarvestTimber");
        context.Memory.HasTimberOverride = true;
        actor.DispatchedRequests[^1].Complete("Timber gathered from mature trees");

        // Step 3: TravelToWorkbench
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("TravelToWorkbench");
        bot.Character.Transform.World.Position = workbenchPos;
        actor.DispatchedRequests[^1].Complete("Arrived at workbench");

        // Step 4: CraftMaterialPack
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("CraftMaterialPack");
        context.Memory.HasBuildingMaterialsOverride = true;
        actor.DispatchedRequests[^1].Complete("Lumber pack crafted");

        // Step 5: TravelToHomeSite
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("TravelToHomeSite");
        bot.Character.Transform.World.Position = homePos;
        actor.DispatchedRequests[^1].Complete("Carried pack back to home site");

        // Step 6: ConstructHome
        runner.Tick(bot, actor, context);
        await Assert.That(runner.CurrentAction!.Name).IsEqualTo("ConstructHome");
        context.Memory.HomeConstructedOverride = true;
        actor.DispatchedRequests[^1].Complete("Home construction completed");

        // Step 7: Goal Satisfied!
        runner.Tick(bot, actor, context);
        await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.GoalSatisfied && e.GoalName == "ErectHome")).IsTrue();
        await Assert.That(runner.PrimaryGoal).IsNull();
    }
}
