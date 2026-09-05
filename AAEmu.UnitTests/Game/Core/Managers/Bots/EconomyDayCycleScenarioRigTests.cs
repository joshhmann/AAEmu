using System.Numerics;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Trading;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
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
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
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

    // ---- hauler-leg fixture ids -----------------------------------------
    private const uint RigPackItemId = GameplayActorTestRig.CargoPackTemplateId;            // 264_901 (fixture auto-equip trade pack)
    private const uint RigPlacedPackDoodadId = GameplayActorTestRig.CargoPackDoodadTemplateId; // 290_902 (registered placed template)
    private const uint RigPackCraftId = 99_013;                                 // fixture pack recipe: potato ×1 → pack
    private const uint RigPackCraftSkillId = 99_014;
    private const int RigPackCraftLaborCost = 10;
    private const uint RigGoldTraderTemplateId = 1003;                          // fixture specialty gold trader
    private const uint RigZoneKey = 142;                                        // target zone key (group ≠ origin)
    private const uint RigZoneGroup = 5;
    private const uint RigPackOriginGroup = 26;                                 // the fixture pack's origin zone group
    private const int RigBundleProfit = 12800;                                  // specialty_bundle_items profit
    private const int RigBundleRatioStatic = 3785;                              // specialty_bundle_items ratio
    private const int RigBundleRefund = 20000;                                  // items.refund for the fixture pack
    // Worked canonical math (SpecialtyManagerTests §sale-math shape):
    //   base = floor(12800 × 3785 / 1000) + 20000 = floor(48448.0) + 20000 = 68448
    //   payout @ fresh-manager max ratio 130% + 5% interest:
    //     round(68448 × 1.30) = 88982.4 → +5% = round(93431.52) = 93432
    private const int ExpectedHaulBasePrice = 68448;
    private const int ExpectedHaulPayoutGold = 93_432;

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

    /// <summary>
    /// Hauler leg happy path (hauler=true): the day cycle extends past CRAFT
    /// with PACK-CRAFT (fixture pack recipe through the real CharacterCraft →
    /// CraftEffect chain; the pack lands in the Backpack slot), SUMMON (the
    /// pump injects the fixture cargo slave — the m3a-m4 vehicle-source
    /// convention; rig NPCs can't survive a real summon scroll here), BOARD,
    /// LOAD (the FULL real PackVehicleService carried-pack chain headless:
    /// System-container move → DoodadManager.Create → cargo-point snap →
    /// Spawn — possible because this fixture registers the cargo placed
    /// template with a Start recover phase), DRIVE (real movement model),
    /// UNBOARD, UNLOAD (real RecoverItem pickup back into the Backpack slot)
    /// and SELL-GOLD at the fixture specialty gold trader through the REAL
    /// SpecialtyManager.SellSpecialty (the exact call CSSellBackpackGoodsPacket
    /// makes — M4ExitIntegratedSessionTests precedent).
    ///
    /// Conservation: labor EXACT with tolerance 0 — farm plant/harvest/craft
    /// PLUS pack-craft skill labor PLUS −60/pack sale labor; currency/bank law
    /// UNCHANGED by the hauler leg (payout copper goes to a delayed MAIL, not
    /// Money); the payout-formula criterion asserts the created mail equals
    /// round(base × ratio% × 1.05) exactly.
    /// </summary>
    [Test]
    public async Task EconomyDayCycle_HaulerLeg_OnFixtureWorld_ConservesLedgerWithLaborMinus60()
    {
        // Singleton discipline (SpecialtyManagerTests pattern): the hauler leg
        // drives the REAL Zone/Specialty/Mail/Name/Character managers — swap in
        // fixtures and restore afterwards so sibling suites never observe it.
        var previousZone = GetSingleton<ZoneManager>();
        var previousSpecialty = GetSingleton<SpecialtyManager>();
        var previousMail = GetSingleton<MailManager>();
        var previousName = GetSingleton<NameManager>();
        var previousCharacterManager = GetSingleton<CharacterManager>();
        object? previousItemIdManager = null;
        HashSet<ulong>? allItemIdsBefore = null;
        var specialtyConfig = AppConfiguration.Instance.Specialty;
        var previousMinLevel = specialtyConfig.MinLevelToCraftSell;

        try
        {
            // Base surfaces BEFORE capturing prior state (missing-only seeds).
            SeedSurfaces();
            previousItemIdManager = GetField(ItemManager.Instance, "itemIdManager")
                ?? typeof(ItemManager)
                    .GetField("<itemIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(ItemManager.Instance);
            // Leak-proofing (t_3c33557d discipline): ItemManager._allItems is
            // process-wide and never cleared — earlier tests in this class
            // leave their items registered under the same 0x01000000+ id
            // range our hauler id mock restarts at. Snapshot the live keys so
            // TearDown can drop everything this leg created.
            allItemIdsBefore =
            [
                .. ((System.Collections.Concurrent.ConcurrentDictionary<ulong, Item>)typeof(ItemManager)
                    .GetField("_allItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(ItemManager.Instance)!).Keys
            ];
            SeedHaulerSurfaces();
            SeedFixturePackCraft();

            var (actor, session) = GameplayActorTestRig.CreateActor("m8ec-haul");
            RigWorld(session);
            GameplayActorTestRig.SetPosition(actor, RigFarmOrigin);
            GameplayActorTestRig.SetMoney(actor, 100_000);
            actor.Character.LaborPower = EconomyDayCycleScenario.DefaultLaborPool;
            SeedMovementSingletons();

            // Commerce actability (ChangeLabor(-60, Commerce) indexes it directly —
            // the M4ExitIntegratedSessionTests setup convention).
            actor.Character.Actability.Actabilities[(uint)ActabilityType.Commerce] =
                new Actability(new ActabilityTemplate { Id = (uint)ActabilityType.Commerce });

            // Target zone BEFORE the run (GetRatioForSpecialty / origin-zone gate
            // resolve the zone GROUP on every read).
            actor.Character.Transform.ZoneId = RigZoneKey;

            // Register the hauler in the seeded NameManager — MailManager.Send's
            // receiver verification (name AND id must match) gates the payout mail
            // (the SpecialtyManagerTests SeedNameManagerNames convention).
            SetField(NameManager.Instance, "_characterIds",
                new Dictionary<uint, string> { [actor.Character.Id] = actor.Character.Name });
            SetField(NameManager.Instance, "_characterNames",
                new Dictionary<string, uint> { [actor.Character.Name] = actor.Character.Id });

            var seedMerchantObjId = SpawnMerchant(session, 1001);
            var generalMerchantObjId = SpawnMerchant(session, 1002);
            var goldTraderObjId = SpawnGoldTrader(session);
            GameplayActorTestRig.SpawnCraftBench(session, actor);

            var world = new FixtureCycleWorld(seedMerchantObjId, generalMerchantObjId, goldTraderObjId);
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
                Hauler = true,
                PackCraftId = RigPackCraftId,
                PackItemTemplateId = RigPackItemId,
                PackMaterialItemId = RigHarvestItemId, // potato ×1 → pack (fixture recipe below)
                PackMaterialAmount = 1,
                GoldTraderNpcTemplateId = RigGoldTraderTemplateId,
                PlacedPackDoodadTemplateId = RigPlacedPackDoodadId,
                Mode = EconomyDayCycleScenario.DepositMode.Proceeds,
                Cycles = 1,
                RigLevel = 10,
                SeedMoney = 100_000,
                LaborTolerance = 0, // no labor regen in the unit world — exact assert
                CropMaturityTimeout = TimeSpan.FromSeconds(5),
                ActionPumpTimeout = TimeSpan.FromSeconds(10),
                RepositionTimeout = TimeSpan.FromSeconds(15)
            };

            var sink = new EconomyDayCycleScenario.EconomyDayCycleLedger();
            var result = EconomyDayCycleScenario.Run(actor.Character, world, pump, options, sink);

            if (!result.Passed)
            {
                var bag = actor.Character.Inventory.Bag.GetItemsSnapshot();
                var diag =
                    $"hauler FAILED at {result.FailStage} ({result.Failure}): {result.FailReason}\n" +
                    string.Join("\n", result.Criteria.Select(c => $"- criterion [{c.Name}]: {(c.Passed ? "PASS" : "FAIL")} {c.Detail}")) +
                    "\nRIG NOTES:\n" + string.Join("\n", result.RigNotes) +
                    "\nBAG: " + string.Join(", ", bag.Select(i => $"{i.TemplateId} x{i.Count}(id {i.Id})"));
                Console.WriteLine(diag);
                TestContext.Current!.OutputWriter.WriteLine(diag);
            }

            await Assert.That(result.Passed).IsTrue();

            // The full v0 ledger law stays green AND the hauler criteria pass.
            foreach (var name in new[]
                     {
                         "cycles-completed", "currency-conservation", "bank-conservation",
                         "ledger-stage-sums-reconcile", "labor-conservation", "seed-conservation",
                         "lifecycle-trace-complete", "specialty-payout-conservation"
                     })
            {
                await Assert.That(result.Criteria.Any(c => c.Name == name && c.Passed)).IsTrue();
            }

            // The hauler route actually ran, stage by stage: ledger entries for
            // the economic legs (pack craft / load / unload / specialty sale)
            // and stage verdicts for the pure-movement legs.
            await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("PACK-CRAFT"))).IsTrue();
            await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("LOAD"))).IsTrue();
            await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("UNLOAD"))).IsTrue();
            await Assert.That(sink.Entries.Any(e => e.Stage.StartsWith("SELL-GOLD"))).IsTrue();
            // Pure-movement legs (no currency impact): the fixture pump injects
            // the slave, so SUMMON-VEHICLE is live-path-only and never fires here.
            foreach (var prefix in new[] { "BOARD", "DRIVE", "UNBOARD" })
                await Assert.That(result.Stages.Any(s => s.Stage.StartsWith(prefix))).IsTrue();

            // Payout formula asserted against the created mail — exact.
            await Assert.That(sink.SpecialtySellsCharged).IsEqualTo(1);
            await Assert.That(sink.ExpectedSpecialtyPayoutTotal).IsEqualTo(ExpectedHaulPayoutGold);
            await Assert.That(sink.SpecialtyPayoutTotal).IsEqualTo(ExpectedHaulPayoutGold);

            // Labor conservation includes the −60/pack sale charge (exact, tolerance 0):
            // documented = plants + harvests + craft(4) + pack craft(10) + sell(60).
            await Assert.That(sink.PackCraftLaborCostEach).IsEqualTo(RigPackCraftLaborCost);
            await Assert.That(sink.SpecialtySellLaborCostEach).IsEqualTo(60);
            var consumedLabor = EconomyDayCycleScenario.DefaultLaborPool - sink.EndLabor;
            await Assert.That(consumedLabor).IsEqualTo(sink.DocumentedLabor);
            await Assert.That(sink.DocumentedLabor).IsGreaterThan(
                sink.CraftsCharged * sink.CraftLaborCostEach + 60); // hauler added real charges

            // Currency law untouched by the hauler leg (payout IN TRANSIT): the
            // money delta still equals only buys/sells/deposits of the v0 legs.
            await Assert.That(sink.EndMoney)
                .IsEqualTo(sink.StartMoney - sink.BuyTotal + sink.SellTotal - sink.DepositTotal);
        }
        finally
        {
            SetSingleton(typeof(Singleton<ZoneManager>), previousZone);
            SetSingleton(typeof(Singleton<SpecialtyManager>), previousSpecialty);
            SetSingleton(typeof(Singleton<MailManager>), previousMail);
            SetSingleton(typeof(Singleton<NameManager>), previousName);
            SetSingleton(typeof(Singleton<CharacterManager>), previousCharacterManager);
            SetItemManagerField(previousItemIdManager);
            // Drop the items this leg registered in the shared _allItems so
            // sibling suites never observe them (the id-range collision this
            // class's own ordering exposed — see allItemIdsBefore above).
            if (allItemIdsBefore != null)
            {
                var allItems = (System.Collections.Concurrent.ConcurrentDictionary<ulong, Item>)typeof(ItemManager)
                    .GetField("_allItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(ItemManager.Instance)!;
                foreach (var createdId in allItems.Keys)
                    if (!allItemIdsBefore.Contains(createdId))
                        allItems.TryRemove(createdId, out _);
            }
            RestoreMovementSingletons();
            specialtyConfig.MinLevelToCraftSell = previousMinLevel;
        }
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

    /// <summary>
    /// Seeds the fixture PACK recipe: 1 × harvested potato (7992) → 1 ×
    /// fixture auto-equip trade pack (264901) at the rig bench, skill 99014
    /// (doodad target, labor 10). Additive to the shared CraftManager
    /// (missing-only) — the same pattern as SeedFixtureCraft above.
    /// </summary>
    private static void SeedFixturePackCraft()
    {
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;
        if (!crafts.ContainsKey(RigPackCraftId))
        {
            crafts[RigPackCraftId] = new Craft
            {
                Id = RigPackCraftId,
                SkillId = RigPackCraftSkillId,
                ReqDoodadId = GameplayActorTestRig.CraftBenchTemplateId,
                ActabilityLimit = 0,
                CraftMaterials =
                [
                    new CraftMaterial { ItemId = RigHarvestItemId, Amount = 1 }
                ],
                CraftProducts =
                [
                    new CraftProduct { ItemId = RigPackItemId, Amount = 1, Rate = 100 }
                ]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;
        if (!skills.ContainsKey(RigPackCraftSkillId))
        {
            skills[RigPackCraftSkillId] = new SkillTemplate
            {
                Id = RigPackCraftSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 100,
                ConsumeLaborPower = RigPackCraftLaborCost,
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
    /// Spawns the fixture specialty GOLD trader (specialty_coin_id 0) 1 m in
    /// front of the farm origin, in the target zone group — the exact
    /// PlaceTrader shape SpecialtyManagerTests uses (rig NPCs can't come from
    /// real spawners headless).
    /// </summary>
    private static uint SpawnGoldTrader(HeadlessSession session)
    {
        // Same spawn path as the rig merchants (HeadlessSession.SpawnNpc),
        // then swap in a specialty-trader template (specialty_coin_id 0 =
        // gold trader — the PlaceTrader shape SpecialtyManagerTests uses).
        var objId = session.SpawnNpc(RigGoldTraderTemplateId);
        var npc = session.World.GetNpc(objId);
        if (npc != null)
        {
            npc.Template = new NpcTemplate { Id = RigGoldTraderTemplateId, SpecialtyCoinId = 0 };
            npc.Transform.ZoneId = RigZoneKey;
            npc.Transform.Local.SetPosition(RigFarmOrigin + new Vector3(1f, 0f, 0f));
        }

        return objId;
    }

    // ---------------------------------------------- singleton swap helpers
    // (the SpecialtyManagerTests helper set — Get/Set via Singleton<T>'s
    // instance field + private-field reflection)

    private static object? GetSingleton<T>() where T : class
        => typeof(Singleton<T>).GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null);

    private static void SetSingleton(Type singletonBase, object? instance)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, instance);

    private static object? GetField(object target, string fieldName)
        => target.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?.GetValue(target);

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static void SetItemManagerField(object? value)
    {
        var idField = typeof(ItemManager).GetField("<itemIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                      ?? typeof(ItemManager).GetField("itemIdManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        idField?.SetValue(ItemManager.Instance, value);
    }

    // ------------------------------------------------- hauler-leg surfaces

    /// <summary>
    /// Seeds everything the REAL specialty sale needs headless (the
    /// SpecialtyManagerTests fixture set, trimmed to one trader + one bundle):
    /// zone groups for the origin-zone gate, a fresh SpecialtyManager whose
    /// bundle maps the fixture pack, Name/Mail/Character managers so the
    /// payout mail verifies and sends, Item.Coins for the gold-payout path,
    /// an incrementing item-id source (packs are single-instance and resolved
    /// BY id), the level-10 gate, and the cargo placed-pack doodad phase.
    /// </summary>
    private static void SeedHaulerSurfaces()
    {
        // The dedicated cargo-pack surface (pack 264901 + put-down skill +
        // registered placed doodad template + slave cargo attach points).
        GameplayActorTestRig.SeedCargoPackSurface();

        AppConfiguration.Instance.Specialty.MinLevelToCraftSell = 10; // canonical tooltip gate

        // Incrementing item ids (the M3a-3 trap): pack crafting, the cargo
        // doodad link and RecoverItem all resolve the pack instance BY id.
        // The counter base must sit ABOVE every id already registered in the
        // shared _allItems (earlier tests in this class consume the same
        // 0x01000000 range while ItemManager.GetNewId never dedupes) — a
        // fresh restart collides and Create() returns null, refusing grants.
        var idField = typeof(ItemManager).GetField("<itemIdManager>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                      ?? typeof(ItemManager).GetField("itemIdManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (idField != null)
        {
            var existingItems = (System.Collections.Concurrent.ConcurrentDictionary<ulong, Item>)typeof(ItemManager)
                .GetField("_allItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(ItemManager.Instance)!;
            var nextId = 0x01000000u; // ItemIdManager.FirstId — real engine range
            foreach (var existingId in existingItems.Keys)
                if (existingId is > 0 and <= uint.MaxValue && existingId >= nextId)
                    nextId = (uint)existingId + 1;
            var mock = Mock.Of<IItemIdManager>();
            mock.GetNextId().Returns(() => nextId++);
            idField.SetValue(ItemManager.Instance, mock.Object);
        }

        // Fixture pack trade fields: refund feeds the base price; the origin
        // zone group arms the canonical same-zone exclusion (group 26 ≠ 5).
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;
        var packTemplate = templates[RigPackItemId];
        packTemplate.Refund = RigBundleRefund;
        packTemplate.SpecialtyZoneId = RigPackOriginGroup;

        // Coins template (MailForSpeciality.FinalizeForSeller early-returns
        // without it — the seller mail would never verify its receiver).
        templates.TryAdd(Item.Coins, new ItemTemplate
        {
            Id = Item.Coins,
            Name = "Coins",
            MaxCount = 1,
            FixedGrade = 0,
            Gradable = false
        });

        // The cargo placed-pack doodad gets a START phase carrying the generic
        // recover skill — after PackVehicleService's InitDoodad the loaded
        // cargo doodad is recoverable, which is what the UNLOAD leg's real
        // PackPickup (RecoverItem / 11361) requires. Missing-only per surface
        // (the shared SeedCargoPackSurface registers the template bare).
        SeedDoodadIdManager();
        var funcGroupId = RigPlacedPackDoodadId + 10;
        var funcId = RigPlacedPackDoodadId + 20;
        GameplayActorTestRig.SeedRecoverablePackDoodad(funcGroupId, funcId);
        var doodadTemplates = (Dictionary<uint, DoodadTemplate>)typeof(DoodadManager)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(DoodadManager.Instance)!;
        if (!doodadTemplates.TryGetValue(RigPlacedPackDoodadId, out var placedPackTemplate))
        {
            placedPackTemplate = new DoodadTemplate { Id = RigPlacedPackDoodadId };
            doodadTemplates[RigPlacedPackDoodadId] = placedPackTemplate;
        }
        var allFuncGroups = (Dictionary<uint, DoodadFuncGroups>)typeof(DoodadManager)
            .GetField("_allFuncGroups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(DoodadManager.Instance)!;
        var startGroup = new DoodadFuncGroups { Id = funcGroupId, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start };
        if (allFuncGroups == null)
        {
            // The fixture DoodadManager seed never assigns this registry —
            // our flow resolves phases through Template.FuncGroups + the
            // per-group func lists, so a fresh dictionary is enough.
            allFuncGroups = [];
            SetField(DoodadManager.Instance, "_allFuncGroups", allFuncGroups);
        }
        allFuncGroups.TryAdd(funcGroupId, startGroup);
        placedPackTemplate.FuncGroups ??= [];
        if (placedPackTemplate.FuncGroups.All(g => g.Id != funcGroupId))
            placedPackTemplate.FuncGroups.Add(startGroup);

        // ---- zone surface: target zone key → group 5 (≠ pack origin 26).
        SetSingleton(typeof(Singleton<ZoneManager>), new ZoneManager(Mock.Of<IWorldManager>().Object));
        SetField(ZoneManager.Instance, "_zoneIdToKey", new Dictionary<uint, uint>());
        SetField(ZoneManager.Instance, "_conflicts", new Dictionary<ushort, ZoneConflict>());
        SetField(ZoneManager.Instance, "_groupBannedTags", new Dictionary<uint, ZoneGroupBannedTag>());
        // Empty climate elems keep InitDoodad's growth-bonus probe off the real
        // climate surface headless (GetClimatesByZone iterates it).
        SetField(ZoneManager.Instance, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
        SetField(ZoneManager.Instance, "_zones", new Dictionary<uint, Zone>
        {
            [RigZoneKey] = new() { Id = 1, ZoneKey = RigZoneKey, GroupId = RigZoneGroup }
        });
        SetField(ZoneManager.Instance, "_groups", new Dictionary<uint, ZoneGroup>
        {
            [RigZoneGroup] = new() { Id = RigZoneGroup },
            [RigPackOriginGroup] = new() { Id = RigPackOriginGroup }
        });

        // ---- specialty surface: one gold trader + one bundle row for the
        // fixture pack (the exact seed shape of SpecialtyManagerTests).
        var specialtyManager = new SpecialtyManager();
        SetField(specialtyManager, "_specialties", new Dictionary<uint, Specialty>());
        SetField(specialtyManager, "_specialtyBundleItems", new Dictionary<uint, SpecialtyBundleItem>());
        SetField(specialtyManager, "_specialtyNpc", new Dictionary<uint, SpecialtyNpc>
        {
            [RigGoldTraderTemplateId] = new()
            {
                Id = 1, Name = "test-gold-trader",
                NpcId = RigGoldTraderTemplateId,
                SpecialtyBundleId = 77
            }
        });
        SetField(specialtyManager, "_specialtyBundleItemsMapped",
            new Dictionary<uint, Dictionary<uint, SpecialtyBundleItem>>
            {
                [RigPackItemId] = new()
                {
                    [77] = new SpecialtyBundleItem
                    {
                        Id = 1,
                        ItemId = RigPackItemId,
                        SpecialtyBundleId = 77,
                        Profit = RigBundleProfit,
                        Ratio = RigBundleRatioStatic,
                        Item = packTemplate
                    }
                }
            });
        SetField(specialtyManager, "_priceRatios", new Dictionary<uint, Dictionary<uint, double>>());
        SetField(specialtyManager, "_soldPackAmountInTick", new Dictionary<uint, Dictionary<uint, int>>());
        SetSingleton(typeof(Singleton<SpecialtyManager>), specialtyManager);

        // ---- name/mail/character surfaces (ORDER MATTERS: the mail manager
        // holds a direct reference to the seeded NameManager instance).
        var nameManager = new NameManager();
        SetField(nameManager, "_characterIds", new Dictionary<uint, string>());
        SetField(nameManager, "_characterNames", new Dictionary<string, uint>());
        SetField(nameManager, "_characterAccounts", new Dictionary<uint, uint>());
        SetSingleton(typeof(Singleton<NameManager>), nameManager);

        var mailIdMock = Mock.Of<IMailIdManager>();
        var nextMailId = 1u;
        mailIdMock.GetNextId().Returns(() => nextMailId++);
        var mailManager = new MailManager(
            mailIdMock.Object,
            NameManager.Instance,
            Mock.Of<IItemManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        SetField(mailManager, "_allPlayerMails", new Dictionary<long, AAEmu.Game.Models.Game.Mails.BaseMail>());
        SetField(mailManager, "_deletedMailIds", new List<long>());
        SetSingleton(typeof(Singleton<MailManager>), mailManager);

        var characterManager = new CharacterManager(
            Mock.Of<IWorldManager>().Object,
            Mock.Of<IAccountManager>().Object,
            NameManager.Instance,
            Mock.Of<ICharacterIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IHousingManager>().Object,
            Mock.Of<IFamilyManager>().Object,
            MailManager.Instance,
            Mock.Of<ITaskManager>().Object);
        SetField(characterManager, "_expertLimits", new Dictionary<int, ExpertLimit>
        {
            [0] = new() { UpLimit = int.MaxValue }
        });
        SetSingleton(typeof(Singleton<CharacterManager>), characterManager);
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

    /// <summary>World adapter: merchant NPCs resolve to the rig-spawned ones
    /// (1001 seed / 1002 general); the hauler gold trader resolves at 1003.</summary>
    private sealed class FixtureCycleWorld(
        uint seedMerchantObjId, uint generalMerchantObjId, uint goldTraderObjId = 0)
        : BotScenarioRunner.IScenarioWorldAdapter
    {
        public uint ResolveNpcObjId(uint npcTemplateId)
            => npcTemplateId switch
            {
                1001 => seedMerchantObjId,
                1002 => generalMerchantObjId,
                1003 => goldTraderObjId,
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
                    // REQUEST (multi-cycle runs issue several crafts). The
                    // CastSkill id mirrors the request's recipe skill (the
                    // hauler pack recipe carries its own skill); EndCraft
                    // itself charges the QUEUE's craft skill labor.
                    var bench = a.Character.ParentWorld?.GetAllDoodads()
                        .FirstOrDefault(d => d.TemplateId == GameplayActorTestRig.CraftBenchTemplateId);
                    var effect = new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart };
                    effect.Apply(a.Character, null, bench, null,
                        new CastSkill(CraftSkillFor(request.TargetId), 0), new EffectSource(), null, DateTime.UtcNow);
                    _craftStepTraceId = request.TraceId;
                }

                Thread.Sleep(5);
            }

            return request;
        }

        private static uint CraftSkillFor(uint craftId)
            => craftId == RigPackCraftId ? RigPackCraftSkillId : RigCraftSkillId;

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

        /// <summary>
        /// Vehicle-source seam (the m3a-m4 rig convention): the fixture world
        /// carries no real summon-scroll item data, so the rig injects its
        /// cargo slave and the LIVE hook exercises the real UseItem path.
        /// </summary>
        public uint? TrySummonVehicle(Character character)
            => GameplayActorTestRig.SummonCargoSlave(session, actor,
                GameplayActorTestRig.SlaveObjId + 0x200).ObjId;

        /// <summary>
        /// Returns null — deliberately. Unlike the m3a-m4 pack surface (whose
        /// placed-pack doodad template is deliberately unregistered), this
        /// fixture's cargo pack doodad template IS registered with a Start
        /// func group carrying the generic recover skill (see
        /// SeedHaulerCargoDoodadPhase), so the CARRIED-load path runs the full
        /// real PackVehicleService chain headless: System-container move →
        /// DoodadManager.Create → cargo-point snap → InitDoodad → Spawn. The
        /// slave-persistence arm is skipped because the fixture slave carries
        /// no SummoningItem (the same gate PackVehicleService applies).
        /// </summary>
        public uint? ProvisionPlacedPackDoodad(Character character) => null;

        private static uint s_nextCropObjId = 0x8000_0000;

        private static uint NextCropObjId() => s_nextCropObjId++;
    }
}
