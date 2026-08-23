using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Economy loop v0 (<c>m8-economy-cycle-v0</c>) rig: the repeatable
/// BUY → PLANT → GROW → HARVEST → CRAFT → SELL → DEPOSIT day cycle driven
/// through the M5.1 CONTRACT ACTIONS on the fixture world, with the full
/// ledger-reconciliation law asserted (currency / bank / stage sums / labor /
/// seeds / lifecycle) — the deterministic gate evidence for the scenario
/// (the live E2E hook drives the same scenario with canonical ids against
/// the real stack, then force-restarts the game process and reconciles the
/// ledger snapshot against MySQL).
///
/// Fixture mapping (the surfaces the M3aM4 replay rig already exercises —
/// same pre-authorized rig conventions, cited per member):
///   - farm: REAL potato chain (seed 15659 → crop 2259 → yield 7992) via
///     <see cref="CropHarvestLoopRig"/>; the contract Plant's MySQL Save()
///     tail lands as Interrupted-at-boundary WITH the effect applied and the
///     pump materializes the mature crop (the accepted Harvest-rig
///     convention, verbatim from the M3aM4 rig pump);
///   - craft: fixture recipe 99011 (potato ×1 + fixture water ×1 → fixture
///     product) at the rig bench through the real CraftEffect chain (the
///     M3aM4 rig's Drive convention);
///   - trade/bank: rig merchant buy/sell + a real bank deposit round trip.
///
/// H stays UNKNOWN — proxy/bot-functional evidence only.
/// </summary>
[NotInParallel] // process-wide MySQL.SetConfiguration + singleton state
public class EconomyDayCycleScenarioRigTests
{
    // ---- fixture route ids ----------------------------------------------
    private const uint RigSeedItemId = CropHarvestLoopTests.PotatoSeedItemId;   // 15659 (real potato seed)
    private const int RigSeedsPerCycle = 2;
    private const uint RigHarvestItemId = CropHarvestLoopTests.PotatoItemId;    // 7992 (potato — craft material)
    private const uint RigWaterItemId = 88_105;                                 // fixture aux material (not merchant-sold)
    private const uint RigProductId = 88_106;                                   // fixture sellable craft product
    private const uint RigCraftId = 99_011;                                     // fixture recipe (M3aM4 uses 99_001)
    private const uint RigCraftSkillId = 99_012;
    private const int RigCraftLaborCost = 4;

    private static readonly Vector3 RigFarmOrigin = new(1000f, 1000f, 100f);

    [Before(Test)]
    public void SetUp()
    {
        // Doodad.Save() must fail FAST and deterministically headless (the
        // PlantActionsTests convention): a dead port turns the MySQL write
        // into an immediate MySqlException instead of a localhost:3306
        // attempt (which could hit a real dev MySQL).
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });

        // Leak-proofing: CropHarvestLoopRig.Seed() seeds a MOCK-dependency
        // HousingManager when no sibling created one yet. This class sorts
        // BEFORE the house-build family alphabetically; leaving the mock
        // instance registered would make their missing-only HousingManager
        // seed a no-op and the engine Build path would run on mocks. Save
        // the prior instance and restore it in TearDown.
        _previousHousingManager = typeof(Singleton<HousingManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
    }

    private static object? _previousHousingManager;

    [After(Test)]
    public void TearDown()
    {
        RestoreMovementSingletons(); // sibling suites must never observe the swap
        typeof(Singleton<HousingManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousHousingManager);
        MySQL.SetConfiguration(null); // restore default (localhost:3306)
    }

    [Test]
    public async Task EconomyDayCycle_TwoCycles_OnFixtureWorld_ReconcilesFullLedger()
    {
        SeedSurfaces();
        var (actor, session) = GameplayActorTestRig.CreateActor("m8ec-rig");
        RigWorld(session); // register the fixture world (PlantActionsTests pattern)
        GameplayActorTestRig.SetPosition(actor, RigFarmOrigin);
        GameplayActorTestRig.SetMoney(actor, 100_000);
        actor.Character.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;
        SeedMovementSingletons(); // SusManager/ModelManager — the DriveVehicle-tests pattern

        var seedMerchantObjId = SpawnMerchant(session, 1001);
        var generalMerchantObjId = SpawnMerchant(session, 1002);
        GameplayActorTestRig.SpawnCraftBench(session, actor);

        var world = new FixtureCycleWorld(seedMerchantObjId, generalMerchantObjId);
        var pump = new FixtureCyclePump(session, actor);

        var options = new EconomyDayCycleScenario.CycleOptions
        {
            SeedItemId = RigSeedItemId,
            SeedsPerCycle = RigSeedsPerCycle,
            FarmOrigin = RigFarmOrigin,
            PlotSpacing = 2f,
            CraftId = RigCraftId,
            CraftMaterialItemId = RigHarvestItemId,
            CraftMaterialAmount = 1,
            AuxiliaryMaterialItemId = RigWaterItemId,
            AuxiliaryMaterialAmount = 1,
            ProductItemId = RigProductId,
            SeedMerchantNpcTemplateId = 1001,
            GeneralMerchantNpcTemplateId = 1002,
            Mode = EconomyDayCycleScenario.DepositMode.Proceeds,
            Cycles = 2,
            RigLevel = 10,
            SeedMoney = 100_000,
            LaborTolerance = 0, // no labor regen in the unit world — exact assert
            CropMaturityTimeout = TimeSpan.FromSeconds(5),
            ActionPumpTimeout = TimeSpan.FromSeconds(10)
        };

        var sink = new EconomyDayCycleScenario.EconomyDayCycleLedger();
        var result = EconomyDayCycleScenario.Run(actor.Character, world, pump, options, sink);

        if (!result.Passed)
        {
            var bag = actor.Character.Inventory.Bag.GetItemsSnapshot();
            var warehouse = actor.Character.Inventory.Warehouse.GetItemsSnapshot();
            TestContext.Current!.OutputWriter.WriteLine(
                $"cycle FAILED at {result.FailStage} ({result.Failure}): {result.FailReason}\n" +
                string.Join("\n", result.Criteria.Select(c => $"- criterion [{c.Name}]: {(c.Passed ? "PASS" : "FAIL")} {c.Detail}")) +
                "\nRIG NOTES:\n" + string.Join("\n", result.RigNotes) +
                "\nBAG: " + string.Join(", ", bag.Select(i => $"{i.TemplateId} x{i.Count}(id {i.Id})")) +
                "\nWAREHOUSE: " + string.Join(", ", warehouse.Select(i => $"{i.TemplateId} x{i.Count}(id {i.Id})")));
        }

        await Assert.That(result.Passed).IsTrue();

        // Both cycles ran end-to-end.
        await Assert.That(result.Criteria.Any(c => c.Name == "cycles-completed" && c.Passed)).IsTrue();
        await Assert.That(sink.CyclesCompleted).IsEqualTo(2);

        // The FULL ledger reconciliation law is green.
        await Assert.That(result.Criteria.Any(c => c.Name == "currency-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "bank-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "ledger-stage-sums-reconcile" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "labor-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "seed-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "lifecycle-trace-complete" && c.Passed)).IsTrue();

        // The circuit actually CLOSED: sell proceeds landed in the bank.
        await Assert.That(sink.SellTotal).IsGreaterThan(0);
        await Assert.That(sink.DepositTotal).IsEqualTo(sink.SellTotal);
        await Assert.That(sink.EndBank).IsGreaterThan(sink.StartBank);
        await Assert.That(sink.EndBank).IsEqualTo(sink.StartBank + sink.DepositTotal);

        // Every economic stage left an observable before/after entry.
        await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("BUY-SEEDS"))).IsTrue();
        await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("HARVEST"))).IsTrue();
        await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("CRAFT"))).IsTrue();
        await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("SELL"))).IsTrue();
        await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("DEPOSIT-MONEY"))).IsTrue();
    }

    /// <summary>
    /// Fail-closed on a refused Craft: the recipe is unavailable to the actor
    /// (its auxiliary material was never acquired — the engine's bag-scope
    /// rule), so the CSExecuteCraft-equivalent pre-flight refuses the request
    /// BEFORE any engine call. The run fails closed at the CRAFT stage with a
    /// clean §17 RejectedAction reason and NO Running transition on the
    /// refused record (a refusal is not an execution). The engine's deeper
    /// learn/actability gates are not reachable headless — the actor's
    /// pre-flight gates are the fail-closed surface this rig proves.
    /// </summary>
    [Test]
    public async Task EconomyDayCycle_RefusedCraft_FailsClosedWithRejectedTrace()
    {
        SeedSurfaces();
        var (actor, session) = GameplayActorTestRig.CreateActor("m8ec-refused");
        RigWorld(session);
        GameplayActorTestRig.SetPosition(actor, RigFarmOrigin);
        GameplayActorTestRig.SetMoney(actor, 100_000);
        actor.Character.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;
        SeedMovementSingletons();

        var seedMerchantObjId = SpawnMerchant(session, 1001);
        var generalMerchantObjId = SpawnMerchant(session, 1002);
        GameplayActorTestRig.SpawnCraftBench(session, actor);

        var options = new EconomyDayCycleScenario.CycleOptions
        {
            SeedItemId = RigSeedItemId,
            SeedsPerCycle = RigSeedsPerCycle,
            FarmOrigin = RigFarmOrigin,
            CraftId = RigCraftId,
            CraftMaterialItemId = RigHarvestItemId,
            CraftMaterialAmount = 1,
            // The recipe's auxiliary material is NEVER stocked → the craft
            // gate refuses (materials not present in bag).
            AuxiliaryMaterialItemId = RigWaterItemId,
            AuxiliaryMaterialAmount = 0,
            ProductItemId = RigProductId,
            SeedMerchantNpcTemplateId = 1001,
            GeneralMerchantNpcTemplateId = 1002,
            Cycles = 1,
            LaborTolerance = 0,
            CropMaturityTimeout = TimeSpan.FromSeconds(5),
            ActionPumpTimeout = TimeSpan.FromSeconds(10)
        };

        var sink = new EconomyDayCycleScenario.EconomyDayCycleLedger();
        var result = EconomyDayCycleScenario.Run(actor.Character,
            new FixtureCycleWorld(seedMerchantObjId, generalMerchantObjId),
            new FixtureCyclePump(session, actor), options, sink);

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailStage).Contains("CRAFT");
        await Assert.That(result.Failure).IsEqualTo(ActorFailureReason.RejectedAction);

        // The refused record exists and carries NO Running transition.
        var craftRecord = result.TraceRecords
            .LastOrDefault(r => r.Action == ActorActionType.Craft);
        await Assert.That(craftRecord).IsNotNull();
        await Assert.That(craftRecord!.Result).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(craftRecord.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    /// <summary>
    /// Ledger mismatch detection (the auditable-economy assertion's negative):
    /// corrupting ANY ledger value flips the corresponding reconciliation
    /// criterion to FAIL — the law detects leakage/duplication rather than
    /// rubber-stamping. Stage sums stay green under totals-only corruption
    /// (they read the observable entries, exactly the independence the
    /// two-lane design wants), and flip when an OBSERVABLE entry is corrupted.
    /// </summary>
    [Test]
    public async Task EconomyDayCycle_CorruptedLedgerValue_FailsReconciliationCriterion()
    {
        SeedSurfaces();
        var (actor, session) = GameplayActorTestRig.CreateActor("m8ec-corrupt");
        RigWorld(session);
        GameplayActorTestRig.SetPosition(actor, RigFarmOrigin);
        GameplayActorTestRig.SetMoney(actor, 100_000);
        actor.Character.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;
        SeedMovementSingletons();

        var seedMerchantObjId = SpawnMerchant(session, 1001);
        var generalMerchantObjId = SpawnMerchant(session, 1002);
        GameplayActorTestRig.SpawnCraftBench(session, actor);

        var options = new EconomyDayCycleScenario.CycleOptions
        {
            SeedItemId = RigSeedItemId,
            SeedsPerCycle = RigSeedsPerCycle,
            FarmOrigin = RigFarmOrigin,
            CraftId = RigCraftId,
            CraftMaterialItemId = RigHarvestItemId,
            AuxiliaryMaterialItemId = RigWaterItemId,
            ProductItemId = RigProductId,
            SeedMerchantNpcTemplateId = 1001,
            GeneralMerchantNpcTemplateId = 1002,
            Cycles = 1,
            LaborTolerance = 0,
            CropMaturityTimeout = TimeSpan.FromSeconds(5),
            ActionPumpTimeout = TimeSpan.FromSeconds(10)
        };

        var sink = new EconomyDayCycleScenario.EconomyDayCycleLedger();
        var result = EconomyDayCycleScenario.Run(actor.Character,
            new FixtureCycleWorld(seedMerchantObjId, generalMerchantObjId),
            new FixtureCyclePump(session, actor), options, sink);
        await Assert.That(result.Passed).IsTrue().Because(result.Evidence());

        // Baseline: every reconcile criterion passes.
        await Assert.That(sink.ReconcileCurrency().Passed).IsTrue();
        await Assert.That(sink.ReconcileBank().Passed).IsTrue();
        await Assert.That(sink.ReconcileStageSums().Passed).IsTrue();
        await Assert.That(sink.ReconcileLabor(0).Passed).IsTrue();
        await Assert.That(sink.ReconcileSeeds().Passed).IsTrue();

        // Corrupt the SELL total → currency law breaks...
        var honestSell = sink.SellTotal;
        sink.SellTotal += 9999;
        await Assert.That(sink.ReconcileCurrency().Passed).IsFalse();
        sink.SellTotal = honestSell;

        // ...corrupt the DEPOSIT total → bank law breaks...
        sink.DepositTotal += 1;
        await Assert.That(sink.ReconcileBank().Passed).IsFalse();
        sink.DepositTotal -= 1;

        // ...corrupt a LABOR cost line → labor law breaks...
        sink.HarvestLaborCostEach += 1;
        await Assert.That(sink.ReconcileLabor(0).Passed).IsFalse();
        sink.HarvestLaborCostEach -= 1;

        // ...corrupt the SEED grant count → seed law breaks...
        sink.HarvestSeedGrants += 1;
        await Assert.That(sink.ReconcileSeeds().Passed).IsFalse();
        sink.HarvestSeedGrants -= 1;

        // ...and corrupting an OBSERVABLE entry breaks the stage-sum lane.
        var lastEntry = sink.Entries[^1];
        var tampered = new EconomyDayCycleScenario.EconomySnapshot(
            lastEntry.After.Money + 500, lastEntry.After.BankMoney, lastEntry.After.LaborPower,
            lastEntry.After.BagCounts, lastEntry.After.BankCounts);
        sink.Entries[^1] = new EconomyDayCycleScenario.LedgerEntry(lastEntry.Stage, lastEntry.Before, tampered);
        var stageSums = sink.ReconcileStageSums();
        await Assert.That(stageSums.Passed).IsFalse();
        await Assert.That(stageSums.Detail).Contains("MISMATCH");

        // Restored ledger reconciles again (the corruption was the cause).
        sink.Entries[^1] = lastEntry;
        await Assert.That(sink.ReconcileStageSums().Passed).IsTrue();
        await Assert.That(sink.ReconcileCurrency().Passed).IsTrue();
    }

    // ------------------------------------------------------------- surfaces

    private static void SeedSurfaces()
    {
        CropHarvestLoopRig.Seed();          // real potato chain (seed 15659 → crop 2259 → yield 7992)
        SeedDoodadIdManager();              // Doodad.Save() row-id allocation (missing-only init)
        GameplayActorTestRig.SeedTradeSurface(); // merchant + grades + buy/sell machinery

        // Trade fields for the seed (the CropHarvestLoop surface seeds the
        // template without them): canonical items values (15659: price 25,
        // refund 12, sellable 't').
        GameplayActorTestRig.SeedTradeItemTemplate(RigSeedItemId, 25, 12, true);

        // Fixture water (aux material, never merchant-sold — stocked).
        GameplayActorTestRig.RegisterPlainItemTemplate(RigWaterItemId);

        // Fixture craft product: stackable + sellable (canonical boiled
        // potato 16187 shape: price 120, refund 60).
        GameplayActorTestRig.SeedTradeItemTemplate(RigProductId, 120, 60, true);

        SeedFixtureCraft();                 // fixture recipe 99011: potato ×1 + water ×1 → product
    }

    /// <summary>
    /// Seeds the fixture recipe: 1 × harvested potato (7992) + 1 × fixture
    /// water (88105) → 1 × fixture product (88106) at the rig bench, skill
    /// 99012 (doodad target, labor 4). Additive to the shared CraftManager
    /// (missing-only) — the same pattern as the M3aM4 rig's SeedFixtureCraft.
    /// </summary>
    private static void SeedFixtureCraft()
    {
        GameplayActorTestRig.SeedCraftSurface(); // base craft surface (bench + skill pipeline)

        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(RigCraftId))
        {
            crafts[RigCraftId] = new Craft
            {
                Id = RigCraftId,
                SkillId = RigCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = RigHarvestItemId, Amount = 1 },
                    new CraftMaterial { ItemId = RigWaterItemId, Amount = 1 }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = RigProductId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(RigCraftSkillId))
        {
            skills[RigCraftSkillId] = new SkillTemplate
            {
                Id = RigCraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = RigCraftLaborCost,
                ActabilityGroupId = 0,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
        }
    }

    private static uint SpawnMerchant(HeadlessSession session, uint npcTemplateId)
    {
        // One shared goods pack carrying the seed (the Buy gate needs the
        // pack to SELL the item; Sell itself only needs a Merchant NPC).
        var packId = GameplayActorTestRig.SeedMerchantPack(RigSeedItemId);
        var objId = GameplayActorTestRig.SpawnMerchantNpc(session, npcTemplateId: npcTemplateId, packId: packId);
        GameplayActorTestRig.SetNpcPosition(session, objId, RigFarmOrigin);
        return objId;
    }

    /// <summary>
    /// Doodad.Save() allocates the row id via DoodadIdManager BEFORE the
    /// MySQL write; the rig points MySQL at a dead port so the write fails
    /// fast and deterministically (PlantActionsTests convention), and the id
    /// manager must be initialized to reach it (missing-only, t_6bad0654).
    /// </summary>
    private static void SeedDoodadIdManager()
    {
        var freeIdsField = typeof(AAEmu.Game.Utils.IdManager).GetField("_freeIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (freeIdsField?.GetValue(AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance) == null)
            AAEmu.Game.Core.Managers.Id.DoodadIdManager.Instance.Initialize(false);
    }

    /// <summary>
    /// Gives the fixture world a UNIQUE high-base instance id and registers
    /// it, then attaches a SpawnManager — verbatim the M3aM4 replay rig's
    /// RigWorld (unique base 0x7000_0000 so suites never collide).
    /// </summary>
    private static uint s_nextWorldId = 0x7000_0000;

    private static void RigWorld(HeadlessSession session)
    {
        typeof(WorldInstance).GetField("<Id>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(session.World.Id, session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);

        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    // ------------------------------------------------- movement singletons

    private static object? _previousSusManager;
    private static object? _previousModelManager;

    /// <summary>Verbatim the M3aM4 replay rig's singleton seeding: FinalizeTransform
    /// consults SusManager, Character.SetPosition consults ModelManager when
    /// attached to a Slave. Restored in TearDown.</summary>
    private static void SeedMovementSingletons()
    {
        _previousSusManager = typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new SusManager(WorldManager.Instance));

        _previousModelManager = typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        var modelManager = new ModelManager();
        typeof(ModelManager)
            .GetField("_modelTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<uint, ModelType>());
        typeof(ModelManager)
            .GetField("_models", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>>());
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, modelManager);
    }

    private void RestoreMovementSingletons()
    {
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousSusManager);
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, _previousModelManager);
    }

    // ------------------------------------------------------------ fixtures

    /// <summary>World adapter: merchant NPCs resolve to the rig-spawned ones.</summary>
    private sealed class FixtureCycleWorld(uint seedMerchantObjId, uint generalMerchantObjId)
        : BotScenarioRunner.IScenarioWorldAdapter
    {
        public uint ResolveNpcObjId(uint npcTemplateId)
            => npcTemplateId switch
            {
                1001 => seedMerchantObjId,
                1002 => generalMerchantObjId,
                _ => 0
            };

        public uint ResolveDoodadObjId(uint doodadTemplateId) => 0;
    }

    /// <summary>
    /// Fixture pump: drives in-flight requests deterministically (no wall
    /// clock) and applies the REAL CraftEffect once the engine queue is
    /// active (the M3aM4 rig's Drive convention); crop maturity resolves
    /// immediately because ProvisionCropAtBoundary materializes crops AT THE
    /// MATURE PHASE (the accepted Harvest-rig convention, verbatim).
    /// </summary>
    private sealed class FixtureCyclePump(HeadlessSession session, GameplayActor actor)
        : EconomyDayCycleScenario.ICyclePump
    {
        private Guid? _craftStepTraceId;

        public ActorRequest Drive(GameplayActor a, ActorRequest request, TimeSpan maxWait)
        {
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            while (!request.IsTerminal && Environment.TickCount64 < deadline)
            {
                a.Tick(TimeSpan.FromMilliseconds(20));
                if (request.Action == ActorActionType.Craft
                    && a.Character.Craft is { IsCraftQueueActive: true }
                    && _craftStepTraceId != request.TraceId)
                {
                    // The engine craft queue drains when the skill pipeline
                    // completes its step — apply the REAL CraftEffect (the
                    // exact chain CharacterCraft's CraftTask runs), ONCE PER
                    // REQUEST (multi-cycle runs issue several crafts).
                    var bench = a.Character.ParentWorld?.GetAllDoodads()
                        .FirstOrDefault(d => d.TemplateId == GameplayActorTestRig.CraftBenchTemplateId);
                    var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
                    effect.Apply(a.Character, null, bench, null,
                        new CastSkill(RigCraftSkillId, 0), new EffectSource(), null, DateTime.UtcNow);
                    _craftStepTraceId = request.TraceId;
                }

                Thread.Sleep(5);
            }

            return request;
        }

        public bool WaitForCropMaturity(Character character, uint cropObjId, TimeSpan maxWait)
        {
            // Unit-world crops materialize at the mature phase — the first
            // poll resolves harvestable.
            var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                var crop = character.ParentWorld?.GetDoodad(cropObjId);
                if (crop == null)
                    return false;
                if (M3aM4ReplayScenario.IsHarvestable(crop))
                    return true;
                Thread.Sleep(5);
            }

            return false;
        }

        public uint? ProvisionCropAtBoundary(Character character, Vector3 position)
        {
            // Verbatim the M3aM4 rig pump's persistence-boundary seam: the
            // contract Plant ran its gates but Doodad.Save()'s MySQL tail is
            // unreachable headless — materialize the in-world crop AT THE
            // MATURE PHASE through the accepted Harvest-rig fixture path.
            var world = character.ParentWorld;
            if (world == null)
                return null;
            var doodad = DoodadManager.Instance.Create(world, 0, CropHarvestLoopTests.PotatoDoodadId, character, true);
            doodad.IsPersistent = false;
            // The DoodadManager object-id mock returns one FIXED id for every
            // doodad — re-assign unique ids so every crop resolves.
            doodad.ObjId = NextCropObjId();
            doodad.Transform = character.Transform.CloneDetached(doodad);
            doodad.Transform.Local.SetPosition(position.X, position.Y, position.Z);
            doodad.PlantTime = DateTime.UtcNow;
            doodad.FuncGroupId = CropHarvestLoopTests.MaturePhase;
            // Headless registry bypass (CreateActor/PlacePackDoodad pattern).
            typeof(AAEmu.Game.Models.Game.World.GameObject)
                .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(doodad, world);
            typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
                .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(doodad.Transform, world.Id);
            world.AddObject(doodad);
            world.SpawnManager?.AddPlayerDoodad(doodad);
            return doodad.ObjId;
        }

        private static uint s_nextCropObjId = 0x8000_0000;

        private static uint NextCropObjId() => s_nextCropObjId++;
    }
}
