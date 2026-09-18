#nullable enable

using System.Numerics;
using System.Text;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using HeadlessSession = AAEmu.Game.Models.Game.Bots.HeadlessSession;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.Goap;

/// <summary>
/// M7 10-Bot Homestead Deployment &amp; Progression Spike:
/// Deploys 10 starter playerbots across all 4 canonical races/starting zones:
///   - 3x Nuian (Solzreed Peninsula)
///   - 2x Elf (Gweonid Forest)
///   - 3x Harani (Arcum Iris)
///   - 2x Firran (Falcon Plateau)
///
/// Each bot begins at its canonical racial hub with the starter Straw Hat Scarecrow
/// Garden design (15596) and 10x Bound Tax Certificates (31892).
///
/// The GOAP planning engine drives each bot through the multi-stage homestead cycle:
///   1. Arbitrate Goal: GoalClaimHomestead (utility 58f)
///   2. Action 0: TravelToHousingZone (navigates along roads/terrain to residential zone)
///   3. Action 1: SurveyAndPlacePlot (claims 8x8 farm plot with scarecrow design)
///   4. Action 2: PlantOnPlot (cultivates tree saplings / crops)
///   5. Action 3: HarvestTimber (chops mature trees for logs/timber)
///   6. Action 4: TravelToWorkbench (navigates to carpentry / masonry workbench)
///   7. Action 5: CraftMaterialPack (crafts lumber / building material pack)
///   8. Action 6: TravelToHomeSite (transports material pack to plot)
///   9. Action 7: ConstructHome (advances home frame with materials)
///
/// Machine-readable trace output: scorecard-explorations/generated/m7-homestead-10bot-spike.jsonl
/// Human evidence dossier: scorecard-explorations/generated/m7-homestead-10bot-spike.md
/// </summary>
public class HomesteadTenBotScenarioRigTests
{
    private sealed class BotDeploymentSpec
    {
        public required string Name { get; init; }
        public required Race Race { get; init; }
        public required string StartingZone { get; init; }
        public required Vector3 StartPosition { get; init; }
        public required Vector3 HousingZoneCenter { get; init; }
        public required Vector3 WorkbenchPosition { get; init; }
    }

    private sealed class BotRunResult
    {
        public required BotDeploymentSpec Spec { get; init; }
        public required PlayerBotRuntime Bot { get; init; }
        public required BotContext Context { get; init; }
        public required List<ActorAuditRecord> AuditTrace { get; init; }
        public required List<string> Milestones { get; init; }
        public float DistanceTraversed { get; set; }
        public bool Passed { get; set; }
    }

    private static readonly List<BotDeploymentSpec> BotSpecs =
    [
        // Nuian trio in Solzreed Peninsula
        new BotDeploymentSpec
        {
            Name = "HomesteadNuian01",
            Race = Race.Nuian,
            StartingZone = "Solzreed Peninsula (Wardton Hub)",
            StartPosition = new Vector3(14149.5f, 14992.0f, 133.4f),
            HousingZoneCenter = new Vector3(14450.0f, 14500.0f, 115.0f),
            WorkbenchPosition = new Vector3(14420.0f, 14520.0f, 114.0f)
        },
        new BotDeploymentSpec
        {
            Name = "HomesteadNuian02",
            Race = Race.Nuian,
            StartingZone = "Solzreed Peninsula (Crescent Throne Outskirts)",
            StartPosition = new Vector3(14200.0f, 14950.0f, 130.0f),
            HousingZoneCenter = new Vector3(14460.0f, 14490.0f, 115.5f),
            WorkbenchPosition = new Vector3(14420.0f, 14520.0f, 114.0f)
        },
        new BotDeploymentSpec
        {
            Name = "HomesteadNuian03",
            Race = Race.Nuian,
            StartingZone = "Solzreed Peninsula (Lakeside Trail)",
            StartPosition = new Vector3(14100.0f, 15050.0f, 135.0f),
            HousingZoneCenter = new Vector3(14470.0f, 14480.0f, 116.0f),
            WorkbenchPosition = new Vector3(14420.0f, 14520.0f, 114.0f)
        },

        // Elf duo in Gweonid Forest
        new BotDeploymentSpec
        {
            Name = "HomesteadElf01",
            Race = Race.Elf,
            StartingZone = "Gweonid Forest (Memory Arbor)",
            StartPosition = new Vector3(16500.0f, 13500.0f, 180.0f),
            HousingZoneCenter = new Vector3(16800.0f, 13200.0f, 175.0f),
            WorkbenchPosition = new Vector3(16780.0f, 13220.0f, 175.0f)
        },
        new BotDeploymentSpec
        {
            Name = "HomesteadElf02",
            Race = Race.Elf,
            StartingZone = "Gweonid Forest (Elder Grove)",
            StartPosition = new Vector3(16550.0f, 13450.0f, 182.0f),
            HousingZoneCenter = new Vector3(16810.0f, 13190.0f, 174.5f),
            WorkbenchPosition = new Vector3(16780.0f, 13220.0f, 175.0f)
        },

        // Harani trio in Arcum Iris
        new BotDeploymentSpec
        {
            Name = "HomesteadHarani01",
            Race = Race.Hariharan,
            StartingZone = "Arcum Iris (Widesleeves Oasis)",
            StartPosition = new Vector3(21602.4f, 7325.0f, 120.0f),
            HousingZoneCenter = new Vector3(21900.0f, 7500.0f, 118.0f),
            WorkbenchPosition = new Vector3(21840.0f, 7540.0f, 118.0f)
        },
        new BotDeploymentSpec
        {
            Name = "HomesteadHarani02",
            Race = Race.Hariharan,
            StartingZone = "Arcum Iris (Silk Ridge Pass)",
            StartPosition = new Vector3(21650.0f, 7350.0f, 122.0f),
            HousingZoneCenter = new Vector3(21910.0f, 7490.0f, 118.5f),
            WorkbenchPosition = new Vector3(21840.0f, 7540.0f, 118.0f)
        },
        new BotDeploymentSpec
        {
            Name = "HomesteadHarani03",
            Race = Race.Hariharan,
            StartingZone = "Arcum Iris (Dune Edge)",
            StartPosition = new Vector3(21580.0f, 7280.0f, 119.0f),
            HousingZoneCenter = new Vector3(21890.0f, 7520.0f, 117.5f),
            WorkbenchPosition = new Vector3(21840.0f, 7540.0f, 118.0f)
        },

        // Firran duo in Falcon Plateau
        new BotDeploymentSpec
        {
            Name = "HomesteadFirran01",
            Race = Race.Ferre,
            StartingZone = "Falcon Plateau (Cloudkeep Outpost)",
            StartPosition = new Vector3(23500.0f, 8500.0f, 250.0f),
            HousingZoneCenter = new Vector3(23200.0f, 8800.0f, 245.0f),
            WorkbenchPosition = new Vector3(23140.0f, 8840.0f, 245.0f)
        },
        new BotDeploymentSpec
        {
            Name = "HomesteadFirran02",
            Race = Race.Ferre,
            StartingZone = "Falcon Plateau (Windy Valley)",
            StartPosition = new Vector3(23550.0f, 8550.0f, 252.0f),
            HousingZoneCenter = new Vector3(23210.0f, 8790.0f, 244.5f),
            WorkbenchPosition = new Vector3(23140.0f, 8840.0f, 245.0f)
        }
    ];

    private sealed class TraceRecordingActor : IGameplayActor
    {
        private readonly Character _character;
        private readonly List<ActorAuditRecord> _trace = [];
        public uint ActorId => _character.ObjId;
        public Character Character => _character;
        public ActorRequest? ActiveRequest { get; private set; }
        public List<ActorRequest> DispatchedRequests { get; } = [];
        public IReadOnlyList<ActorAuditRecord> AuditTrace => _trace;
        public float DistanceTraversed { get; set; }

        public TraceRecordingActor(Character character)
        {
            _character = character;
        }

        public void SetPendingDecision(string? goal, string? policy, int candidates, int rejections, string? seed) { }
        public ActorObservation Observe() => new() { ActorId = ActorId };
        public void Tick(TimeSpan elapsed) { }

        private ActorRequest Track(ActorRequest req)
        {
            ActiveRequest = req;
            DispatchedRequests.Add(req);
            return req;
        }

        public void RecordAudit(ActorRequest req, string detail)
        {
            _trace.Add(new ActorAuditRecord(
                TraceId: req.TraceId,
                ActorId: ActorId,
                Action: req.Action,
                TargetId: req.TargetId,
                RequestedAtUtc: req.RequestedAtUtc,
                StartedAtUtc: req.StartedAtUtc,
                CompletedAtUtc: req.CompletedAtUtc,
                Result: req.State,
                Failure: req.Failure,
                Detail: detail,
                StateChanges: req.StateChanges
            ));
        }

        public ActorRequest NavigateTo(Vector3 destination, float speed = 5.4f, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Move, 0, destination, 0, timeout, null, idempotencyKey);
            req.Accept("Accepted navigation");
            req.Start($"Navigating to ({destination.X:F1}, {destination.Y:F1}, {destination.Z:F1}) at {speed}m/s");
            return Track(req);
        }

        public ActorRequest BuildHouse(uint designId, uint designItemTemplateId, Vector3 position, float zRot = 0f, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, 0, position, 0, null, payload: designId, idempotencyKey);
            req.Accept("Accepted house build");
            req.Start($"Placing house/garden frame (design {designId}, template {designItemTemplateId}) at ({position.X:F1}, {position.Y:F1}, {position.Z:F1})");
            return Track(req);
        }

        public ActorRequest Plant(uint itemTemplateId, Vector3 position, float zRot = 0f, float scale = 1f, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, 0, position, 0, null, payload: itemTemplateId, idempotencyKey);
            req.Accept("Accepted plant");
            req.Start($"Cultivated crop/sapling {itemTemplateId} on plot at ({position.X:F1}, {position.Y:F1}, {position.Z:F1})");
            return Track(req);
        }

        public ActorRequest Harvest(uint doodadObjId, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, 0, null, null, idempotencyKey);
            req.Accept("Accepted harvest");
            req.Start($"Harvested timber from mature tree (doodad {doodadObjId})");
            return Track(req);
        }

        public ActorRequest Craft(uint craftId, uint doodadObjId, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Craft, doodadObjId, null, craftId, timeout ?? TimeSpan.FromSeconds(5), null, idempotencyKey);
            req.Accept("Accepted craft");
            req.Start($"Crafting recipe {craftId} at workbench");
            return Track(req);
        }

        public ActorRequest CraftItem(uint recipeId, int quantity = 1, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, 0, null, 0, null, payload: recipeId, idempotencyKey);
            req.Accept("Accepted craft item");
            req.Start($"Crafted {quantity}x lumber/material pack (recipe {recipeId})");
            return Track(req);
        }

        public ActorRequest Interact(uint doodadObjId, uint skillId = 0, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, doodadObjId, null, skillId, TimeSpan.FromSeconds(5), null, idempotencyKey);
            req.Accept("Accepted interact");
            req.Start($"Interacted with object {doodadObjId}");
            return Track(req);
        }

        public ActorRequest Buy(uint merchantNpcObjId, uint itemTemplateId, int count, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, merchantNpcObjId, null, 0, null, payload: itemTemplateId, idempotencyKey);
            req.Accept("Accepted buy");
            req.Start($"Purchased {count}x item {itemTemplateId}");
            return Track(req);
        }

        public ActorRequest InteractDoodad(uint doodadObjId, string? idempotencyKey = null) => Interact(doodadObjId);
        public ActorRequest UseItem(uint itemTemplateId, uint targetObjId = 0, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Interact, targetObjId, null, 0, null, payload: itemTemplateId, idempotencyKey);
            req.Accept("Accepted use");
            req.Start($"Used item {itemTemplateId}");
            return Track(req);
        }

        public ActorRequest Cast(uint skillId, uint targetObjId = 0, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Cast, targetObjId, null, skillId, null, null, idempotencyKey);
            req.Accept("Accepted cast");
            req.Start($"Cast skill {skillId}");
            return Track(req);
        }

        public ActorRequest Loot(uint corpseObjId, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Loot, corpseObjId, null, 0, null, null, idempotencyKey);
            req.Accept("Accepted loot");
            req.Start($"Looted corpse {corpseObjId}");
            return Track(req);
        }

        public ActorRequest MoveTo(Vector3 destination, float speed = 5.4f, TimeSpan? timeout = null, string? idempotencyKey = null) => NavigateTo(destination, speed, timeout, idempotencyKey);
        public ActorRequest MoveToUnit(uint targetObjId, float speed = 5.4f, TimeSpan? timeout = null, string? idempotencyKey = null)
        {
            var req = new ActorRequest(ActorActionType.Move, targetObjId, null, 0, timeout, null, idempotencyKey);
            req.Accept("Accepted move to unit");
            req.Start($"Moving to unit {targetObjId}");
            return Track(req);
        }

        public ActorRequest NavigateToUnit(uint targetObjId, float speed = 5.4f, TimeSpan? timeout = null, string? idempotencyKey = null) => MoveToUnit(targetObjId, speed, timeout, idempotencyKey);

        private static ActorRequest Unsupported() => throw new NotSupportedException();
        public ActorRequest SetTarget(uint targetObjId) => Unsupported();
        public ActorRequest ClearTarget(string? idempotencyKey = null) => Unsupported();
        public ActorRequest CastAt(uint skillId, Vector3 position, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AutoAttack(uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest StopAutoAttack(string? idempotencyKey = null) => Unsupported();
        public ActorRequest AcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverQuests(uint targetObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DiscoverSelfQuests(string? idempotencyKey = null) => Unsupported();
        public ActorRequest InteractWith(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Talk(uint npcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest InteractNpc(uint npcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PlayCinema(uint cinemaId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Equip(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyInvite(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PartyAccept(string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionCreate(string name, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionInvite(string invitedName, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionAccept(FactionsEnum expeditionId, uint inviterId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest ExpeditionLeave(string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeOffer(uint targetCharacterObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradePutup(uint itemTemplateId, int count, string? idempotencyKey = null) => Unsupported();
        public ActorRequest TradeLockOk(string? idempotencyKey = null) => Unsupported();
        public ActorRequest Mount(uint mateObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Dismount(uint mateObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DismissMate(uint tlId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BoardVehicle(uint vehicleObjId, AttachPointKind attachPoint = AttachPointKind.Driver, string? idempotencyKey = null) => Unsupported();
        public ActorRequest UnboardVehicle(uint vehicleObjId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DriveVehicle(uint vehicleObjId, Vector3 destination, float speed = 5.4f, TimeSpan? timeout = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PackPickup(uint doodadObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PutDown(uint packItemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest LoadPackOntoVehicle(uint slaveObjId, uint? placedPackDoodadObjId = null, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawMoney(long amount, string? idempotencyKey = null) => Unsupported();
        public ActorRequest DepositItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest WithdrawItem(uint itemTemplateId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Stop() => Unsupported();
        public bool Interrupt(Guid traceId) => false;
        public ActorRequest Sell(uint merchantNpcObjId, ulong itemId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest SellSpecialty(uint merchantNpcObjId, string? idempotencyKey = null) => Unsupported();
        public ActorRequest Repair(uint blacksmithNpcObjId, ulong itemId = 0, string? idempotencyKey = null) => Unsupported();
        public ActorRequest PostAuction(ulong itemId, int startPrice, int buyoutPrice, AuctionDuration duration, string? idempotencyKey = null) => Unsupported();
        public ActorRequest BuyAuction(ulong lotId, int price, string? idempotencyKey = null) => Unsupported();
        public ActorAuditRecord? FindByKey(string idempotencyKey) => null;
    }

    [Test]
    public async Task DeployTenBots_HomesteadProgression_FullTraceGeneratedAndVerified()
    {
        GameplayActorTestRig.Seed();
        var allResults = new List<BotRunResult>();
        var totalAudits = new List<ActorAuditRecord>();

        foreach (var spec in BotSpecs)
        {
            // 1. Provision starter character at canonical race coordinates
            var session = HeadlessSession.Create((uint)spec.Name.GetHashCode() & 0xFFFF, spec.Name, 1, spec.Race);
            var character = session.Character;
            character.Hp = 1000;
            character.MaxHp = 1000;
            character.Mp = 500;
            character.MaxMp = 500;
            character.Money = 500;
            character.Transform.World.Position = spec.StartPosition;

            var bot = new PlayerBotRuntime(character, "homestead-deployment");
            var actor = new TraceRecordingActor(character);
            var telemetry = new InMemoryGoapTelemetrySink();
            var context = new BotContext();
            // Fixture-gate opt-in: every override below is labeled rig simulation,
            // never production observation (step-5 contract).
            context.Memory.EnableFixtureOverrides();
            var milestones = new List<string>();
            // Canonical spatial memory known to the bot from exploration / cartography
            context.Memory.KnownHousingZonePos = spec.HousingZoneCenter;
            context.Memory.KnownWorkbenchPos = spec.WorkbenchPosition;

            // Starter state: Newborn bot arrives with Straw Hat Scarecrow Garden design and 10x Tax Certs
            context.Memory.HasScarecrowDesignOverride = true;
            context.Memory.HasTaxCertificatesOverride = true;

            var spawnReq = new ActorRequest(ActorActionType.Observe, 0, spec.StartPosition, 0, null);
            spawnReq.Accept("Spawned in starting hub");
            spawnReq.Start($"Arrived at {spec.StartingZone}");
            spawnReq.Complete($"Ready for progression at ({spec.StartPosition.X:F1}, {spec.StartPosition.Y:F1}, {spec.StartPosition.Z:F1})");
            actor.RecordAudit(spawnReq, $"Spawned at {spec.StartingZone} ({spec.StartPosition.X:F1}, {spec.StartPosition.Y:F1}, {spec.StartPosition.Z:F1})");

            var kitReq = new ActorRequest(ActorActionType.Interact, AcquireScarecrowAction.ScarecrowDesignTemplateId, null, 0, null, payload: AcquireScarecrowAction.TaxCertificateTemplateId);
            kitReq.Accept("Starter kit verified");
            kitReq.Start($"Seeded 8x8 Scarecrow Garden design (15596) and 10x Bound Tax Certificates ({AcquireScarecrowAction.TaxCertificateTemplateId})");
            kitReq.Complete("Starter homestead kit ready in bag");
            actor.RecordAudit(kitReq, $"[SYNTHETIC SEED] Starter homestead kit: Scarecrow Garden design (15596) and 10x Bound Tax Certificates ({AcquireScarecrowAction.TaxCertificateTemplateId})");

            milestones.Add($"[M1_BORN] Spawns at {spec.StartingZone} ({spec.StartPosition.X:F1}, {spec.StartPosition.Y:F1}, {spec.StartPosition.Z:F1})");
            milestones.Add($"[M2_STARTER_KIT] Bag seeded with 8x8 Scarecrow Design (15596) and 10x Bound Tax Certificates ({AcquireScarecrowAction.TaxCertificateTemplateId})");

            // 2. Goal Arbitration
            var arbitrator = new GoalArbitrator();
            var observedState = new BotWorldState();
            observedState = observedState
                .With(BotWorldState.HasScarecrowDesign)
                .With(BotWorldState.HasTaxCertificates);

            var arbitratedGoal = arbitrator.ArbitrateGoal(bot, observedState, context, currentPrimaryGoal: null);
            await Assert.That(arbitratedGoal).IsNotNull();
            await Assert.That(arbitratedGoal!.Name).IsEqualTo(GoalArbitrator.GoalClaimHomestead.Name);
            milestones.Add($"[M3_GOAL_ARBITRATED] Primary Goal selected: {arbitratedGoal.Name} (Priority {arbitratedGoal.BasePriority:F0})");

            var runner = new GoapPlanRunner(telemetrySink: telemetry);
            runner.SetPrimaryGoal(arbitratedGoal, context);

            // Step 1: TravelToHousingZoneAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction).IsNotNull();
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("TravelToHousingZone");
            var transitDist = Vector3.Distance(bot.Character.Transform.World.Position, spec.HousingZoneCenter);
            actor.DistanceTraversed += transitDist;
            bot.Character.Transform.World.Position = spec.HousingZoneCenter;
            var req1 = actor.DispatchedRequests[^1];
            req1.Complete($"Arrived at residential zone centroid ({spec.HousingZoneCenter.X:F1}, {spec.HousingZoneCenter.Y:F1}, {spec.HousingZoneCenter.Z:F1})");
            actor.RecordAudit(req1, $"Navigated {transitDist:F1}m along roads to residential zone centroid");
            milestones.Add($"[M4_ZONE_TRANSIT] Arrived at residential zone centroid ({spec.HousingZoneCenter.X:F1}, {spec.HousingZoneCenter.Y:F1}, {spec.HousingZoneCenter.Z:F1})");

            // Step 2: SurveyAndPlacePlotAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("SurveyAndPlacePlot");
            context.Memory.HasLandPlotOverride = true;
            context.Memory.OwnedHouseId = 9000 + (uint)allResults.Count;
            context.Memory.OwnedHousePos = spec.HousingZoneCenter;
            var req2 = actor.DispatchedRequests[^1];
            req2.Complete($"Erected Straw Hat Scarecrow Garden (Plot ID {context.Memory.OwnedHouseId})");
            actor.RecordAudit(req2, $"Surveyed residential boundary and claimed 8x8 scarecrow garden plot (Plot ID {context.Memory.OwnedHouseId})");
            milestones.Add($"[M5_PLOT_CLAIMED] Erected Straw Hat Scarecrow Garden (Plot ID {context.Memory.OwnedHouseId})");

            // GoalClaimHomestead Satisfied!
            runner.Tick(bot, actor, context);
            await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.GoalSatisfied && e.GoalName == "ClaimHomestead")).IsTrue();

            // Next Intent: Cultivate & Erect Home on Plot
            context.Memory.HasSaplingsOverride = true;
            runner.SetPrimaryGoal(GoalArbitrator.GoalErectHome, context);
            milestones.Add("[M6_INTENT_ADVANCE] Primary Goal arbitrated to ErectHome (cultivate & construct)");

            // Step 3: PlantOnPlotAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("PlantOnPlot");
            context.Memory.AddGrove(1, spec.HousingZoneCenter, DateTime.UtcNow.AddMinutes(-30));
            var req3 = actor.DispatchedRequests[^1];
            req3.Complete("Planted tree saplings on private garden plot");
            actor.RecordAudit(req3, "Cultivated timber grove on private plot");
            milestones.Add("[M7_PLANTED] Cultivated timber grove on private plot");

            // Step 4: HarvestTimberAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("HarvestTimber");
            context.Memory.HasTimberOverride = true;
            var req4 = actor.DispatchedRequests[^1];
            req4.Complete("Chopped mature trees, yielding logs and timber");
            actor.RecordAudit(req4, "Harvested timber from mature trees");
            milestones.Add("[M8_TIMBER_GATHERED] Harvested timber from mature trees");

            // Step 5: TravelToWorkbenchAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("TravelToWorkbench");
            var wbDist = Vector3.Distance(bot.Character.Transform.World.Position, spec.WorkbenchPosition);
            actor.DistanceTraversed += wbDist;
            bot.Character.Transform.World.Position = spec.WorkbenchPosition;
            var req5 = actor.DispatchedRequests[^1];
            req5.Complete($"Arrived at crafting workbench ({spec.WorkbenchPosition.X:F1}, {spec.WorkbenchPosition.Y:F1}, {spec.WorkbenchPosition.Z:F1})");
            actor.RecordAudit(req5, $"Navigated {wbDist:F1}m to crafting workbench");
            milestones.Add($"[M9_WORKBENCH_TRANSIT] Arrived at crafting workbench ({spec.WorkbenchPosition.X:F1}, {spec.WorkbenchPosition.Y:F1}, {spec.WorkbenchPosition.Z:F1})");

            // Step 6: CraftMaterialPackAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("CraftMaterialPack");
            context.Memory.HasBuildingMaterialsOverride = true;
            var req6 = actor.DispatchedRequests[^1];
            req6.Complete("Crafted lumber pack at workbench");
            actor.RecordAudit(req6, "Crafted 1x lumber pack (recipe 1001) at carpentry workbench");
            milestones.Add("[M10_PACK_CRAFTED] Crafted lumber pack from harvested timber");

            // Step 7: TravelToHomeSiteAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("TravelToHomeSite");
            var homeDist = Vector3.Distance(bot.Character.Transform.World.Position, spec.HousingZoneCenter);
            actor.DistanceTraversed += homeDist;
            bot.Character.Transform.World.Position = spec.HousingZoneCenter;
            var req7 = actor.DispatchedRequests[^1];
            req7.Complete("Carried lumber pack to homestead plot");
            actor.RecordAudit(req7, $"Transported lumber pack {homeDist:F1}m back to plot frame");
            milestones.Add("[M11_PACK_TRANSPORT] Transported lumber pack to plot frame");

            // Step 8: ConstructHomeAction
            runner.Tick(bot, actor, context);
            await Assert.That(runner.CurrentAction!.Name).IsEqualTo("ConstructHome");
            context.Memory.HomeConstructedOverride = true;
            var req8 = actor.DispatchedRequests[^1];
            req8.Complete("Applied lumber pack to complete farm structure");
            actor.RecordAudit(req8, $"Applied lumber pack to complete farm structure (House ID {context.Memory.OwnedHouseId})");
            milestones.Add("[M12_HOME_CONSTRUCTED] Successfully constructed farmstead!");

            // Goal Satisfied!
            runner.Tick(bot, actor, context);
            await Assert.That(telemetry.Events.Any(e => e.EventType == GoapTelemetryEventType.GoalSatisfied && e.GoalName == "ErectHome")).IsTrue();

            var runResult = new BotRunResult
            {
                Spec = spec,
                Bot = bot,
                Context = context,
                AuditTrace = [.. actor.AuditTrace],
                Milestones = milestones,
                DistanceTraversed = actor.DistanceTraversed,
                Passed = true
            };

            allResults.Add(runResult);
            totalAudits.AddRange(runResult.AuditTrace);
        }

        // Verify all 10 bots succeeded
        await Assert.That(allResults.Count).IsEqualTo(10);
        await Assert.That(allResults.All(r => r.Passed)).IsTrue();
        await Assert.That(totalAudits.Count).IsGreaterThanOrEqualTo(70);

        // Write the machine-readable trace and human markdown dossier
        WriteEvidence(allResults, totalAudits);
    }

    private static void WriteEvidence(List<BotRunResult> results, List<ActorAuditRecord> totalAudits)
    {
        var repoRoot = RepoRoot();
        var jsonlPath = Path.Combine(repoRoot, "scorecard-explorations", "generated", "m7-homestead-10bot-spike.jsonl");
        var mdPath = Path.Combine(repoRoot, "scorecard-explorations", "generated", "m7-homestead-10bot-spike.md");

        Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);

        // 1. Machine-readable JSONL trace
        var sb = new StringBuilder();
        foreach (var audit in totalAudits)
        {
            sb.AppendLine(audit.ToJson());
        }
        File.WriteAllText(jsonlPath, sb.ToString());

        // 2. Human Markdown dossier
        var md = new StringBuilder();
        md.AppendLine("# M7 Homestead 10-Bot Deployment & Progression Spike Dossier");
        md.AppendLine();
        md.AppendLine("> **Evidence Layer:** `[SYNTHETIC ORCHESTRATION CONTRACT]` (Unit Test Harness Rig).");
        md.AppendLine("> **Test Harness:** `HomesteadTenBotScenarioRigTests` (Deterministic zero wall-clock search).");
        md.AppendLine($"> **Machine-Readable Trace:** `{Path.GetFileName(jsonlPath)}` ({totalAudits.Count} audit records, 10 bots).");
        md.AppendLine("> **Scope:** 10 starter playerbots simulated across Solzreed, Gweonid, Arcum Iris, and Falcon Plateau.");
        md.AppendLine("> **Lifecycle:** Seeded Kit -> Housing Navigation -> 8x8 Placement -> Seeded Sapling -> Harvest -> Workbench -> Home Construction.");
        md.AppendLine("> **Honesty Notice (AGENTS.md):** This harness proves GOAP action planning, state transitions, and actor request contracts under synthetic test conditions. It does NOT prove live server, network, or human client gameplay loop (Live=UNKNOWN, H=UNKNOWN).");
        md.AppendLine();
        md.AppendLine("## Deployment Summary");
        md.AppendLine();
        md.AppendLine("| Bot Name | Race | Starting Region | Housing Zone | Distance Traversed | Audit Events | Rig Verdict |");
        md.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

        foreach (var res in results)
        {
            md.AppendLine($"| **{res.Spec.Name}** | {res.Spec.Race} | {res.Spec.StartingZone} | {res.Spec.HousingZoneCenter.X:F0}, {res.Spec.HousingZoneCenter.Y:F0} | {res.DistanceTraversed:F1} m | {res.AuditTrace.Count} | **{(res.Passed ? "SYNTHETIC PASS" : "FAIL")}** |");
        }

        md.AppendLine();
        md.AppendLine("## Per-Bot Progression Milestone Reconstructions");
        md.AppendLine();

        foreach (var res in results)
        {
            md.AppendLine($"### Bot: `{res.Spec.Name}` ({res.Spec.Race})");
            md.AppendLine($"- **Starting Location:** {res.Spec.StartingZone} at `({res.Spec.StartPosition.X:F1}, {res.Spec.StartPosition.Y:F1}, {res.Spec.StartPosition.Z:F1})`");
            md.AppendLine($"- **Plot Location:** `({res.Spec.HousingZoneCenter.X:F1}, {res.Spec.HousingZoneCenter.Y:F1}, {res.Spec.HousingZoneCenter.Z:F1})` (Plot ID `{res.Context.Memory.OwnedHouseId}`)");
            md.AppendLine($"- **Workbench Location:** `({res.Spec.WorkbenchPosition.X:F1}, {res.Spec.WorkbenchPosition.Y:F1}, {res.Spec.WorkbenchPosition.Z:F1})`");
            md.AppendLine($"- **Cumulative Road & Transit Distance:** {res.DistanceTraversed:F1} meters");
            md.AppendLine("- **Observed Milestones:**");
            foreach (var m in res.Milestones)
            {
                md.AppendLine($"  - {m}");
            }
            md.AppendLine();
        }

        File.WriteAllText(mdPath, md.ToString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }
}
