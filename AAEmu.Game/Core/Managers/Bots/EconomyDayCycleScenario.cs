using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M8 C3+C4 precursor — Economy loop v0 (<see cref="ScenarioName"/>): the
/// FIRST repeatable closed economic circuit a bot can run. Composes the
/// EXISTING M5.1 contract actions into one day-cycle, N times:
///
///   BUY seeds (real merchant goods pack) → PLANT (real doodad) → grow
///   (engine growth timers; the pump advances them) → HARVEST (real
///   loot-pack yield) → CRAFT (real CharacterCraft) → SELL the crafted
///   product at a merchant (real CSSellItemsPacket refund path) →
///   DEPOSIT the proceeds into the bank (real CSDepositMoneyPacket path).
///
/// Canonical live ids (verified against compact.sqlite3):
///   - seed 15659 감자 씨앗 — sold by the seed merchant 8522 (pack 171),
///     plants doodad 2259 (감자);
///   - crop yield: potato 7992 (loot pack 6452: 2–4 potatoes + 1 seed back);
///   - craft 2846 삶은 감자 (Boiled Potato): water 15694 ×1 + potato ×1 →
///     boiled potato 16187 (skill 11086 요리하기, consume_lp 2, no bench
///     requirement, need_learn f);
///   - water 15694 is NOT merchant-sold — provisioned through the ordinary
///     acquisition path (StockInventory, the millet-seed convention);
///   - product 16187 is sellable (refund 60) — sold to the general merchant
///     8524 (the same merchant pair the m3a-m4-replay drives).
///
/// LEDGER: every economic stage appends an entry built ONLY from observable
/// character state (Character.Money / Money2 / LaborPower / container
/// snapshots) immediately before and after the stage — no internal counters
/// are trusted over the character record. VERIFY reconciles:
///   - currency: money_end == money_start − Σbuys + Σsells − Σdeposits
///     (+Σwithdrawals) — EXACT;
///   - bank: bank_end == bank_start + Σdeposits − Σwithdrawals — EXACT;
///   - stage sums: Σ(per-entry deltas) == overall delta (money and bank);
///   - labor: consumed == Σ documented costs ± tolerance (live regen);
///   - seeds: held == bought − planted + Σ(documented harvest seed grants);
///   - lifecycle: every Completed action carries the full transition set.
///
/// v0 scope: NO housing, NO vehicle, NO trade-pack legs (the crafted good is
/// a normal stackable item sold directly at the merchant). Sell + Deposit
/// are REAL engine paths — the two legs the M8 auditable-economy assertion
/// requires. H stays UNKNOWN: proxy/bot-functional evidence only.
/// </summary>
public static class EconomyDayCycleScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key (bridge-dispatched BEFORE template lookup).</summary>
    public const string ScenarioName = "m8-economy-cycle-v0";

    /// <summary>Default rigged copper.</summary>
    public const long DefaultSeedMoney = 100_000;

    /// <summary>Default rig labor pool (conservation measures the delta).</summary>
    public const int DefaultLaborPool = 2000;

    // --------------------------------------------------------- canonical ids

    /// <summary>감자 씨앗 (potato seed — merchant-sold by 8522).</summary>
    public const uint PotatoSeedItemId = 15659;

    /// <summary>감자 (potato — the crop yield and craft material).</summary>
    public const uint PotatoItemId = 7992;

    /// <summary>물 (water — craft auxiliary, NOT merchant-sold: stocked).</summary>
    public const uint WaterItemId = 15694;

    /// <summary>삶은 감자 (boiled potato — craft product, sellable refund 60).</summary>
    public const uint BoiledPotatoItemId = 16187;

    /// <summary>삶은 감자 recipe (craft 2846, skill 11086 요리하기, labor 2).</summary>
    public const uint BoiledPotatoCraftId = 2846;

    /// <summary>&lt;씨앗&gt; seed merchant (sells potato seed 15659).</summary>
    public const uint SeedMerchantNpcTemplateId = 8522;

    /// <summary>&lt;잡화 - 분류1&gt; general merchant (buys anything sellable).</summary>
    public const uint GeneralMerchantNpcTemplateId = 8524;

    /// <summary>How the day-cycle banks its proceeds.</summary>
    public enum DepositMode
    {
        /// <summary>Deposit exactly the cycle's sell proceeds (default).</summary>
        Proceeds,

        /// <summary>Deposit a fixed amount every cycle (<see cref="CycleOptions.FixedDepositAmount"/>).</summary>
        Fixed,

        /// <summary>Keep everything in the inventory (no bank leg).</summary>
        None
    }

    /// <summary>Cycle parameters (live defaults = canonical compact.sqlite3 ids;
    /// unit rigs inject fixture ids).</summary>
    public sealed record CycleOptions
    {
        // ---- FARM ---------------------------------------------------------
        public uint SeedItemId { get; init; } = PotatoSeedItemId;
        public int SeedsPerCycle { get; init; } = 2;

        /// <summary>Base position for the farm plot (defaults to the rig position).</summary>
        public Vector3? FarmOrigin { get; init; }

        /// <summary>Per-crop spacing on the plot (2m grid avoids placement overlap).</summary>
        public float PlotSpacing { get; init; } = 2f;

        // ---- CRAFT --------------------------------------------------------
        public uint CraftId { get; init; } = BoiledPotatoCraftId;
        public uint CraftMaterialItemId { get; init; } = PotatoItemId;
        public int CraftMaterialAmount { get; init; } = 1;

        /// <summary>Auxiliary craft material (canonical: water 15694 — not merchant-sold).</summary>
        public uint AuxiliaryMaterialItemId { get; init; } = WaterItemId;
        public int AuxiliaryMaterialAmount { get; init; } = 1;
        public uint ProductItemId { get; init; } = BoiledPotatoItemId;

        // ---- TRADE --------------------------------------------------------
        public uint SeedMerchantNpcTemplateId { get; init; } = EconomyDayCycleScenario.SeedMerchantNpcTemplateId;
        public uint GeneralMerchantNpcTemplateId { get; init; } = EconomyDayCycleScenario.GeneralMerchantNpcTemplateId;

        // ---- BANK ---------------------------------------------------------
        public DepositMode Mode { get; init; } = DepositMode.Proceeds;

        /// <summary>Fixed per-cycle deposit (<see cref="DepositMode.Fixed"/> only).</summary>
        public long FixedDepositAmount { get; init; }

        // ---- RUN SHAPE ----------------------------------------------------
        /// <summary>Number of full day cycles to run (≥ 1).</summary>
        public int Cycles { get; init; } = 1;

        // ---- RIG ----------------------------------------------------------
        public byte RigLevel { get; init; } = 10;
        public long SeedMoney { get; init; } = DefaultSeedMoney;

        /// <summary>Labor-conservation slack (live labor regen is a server timer; unit = 0).</summary>
        public int LaborTolerance { get; init; } = 12;

        // ---- TIME ---------------------------------------------------------
        public TimeSpan CropMaturityTimeout { get; init; } = TimeSpan.FromSeconds(180);
        public TimeSpan ActionPumpTimeout { get; init; } = TimeSpan.FromSeconds(60);

        // ---- REPOSITION (live path) ---------------------------------------
        /// <summary>Pace for scripted walks between plot and merchants (m/s) —
        /// the same replay pace convention as m3a-m4-replay.</summary>
        public float RepositionSpeed { get; init; } = 15f;

        /// <summary>Budget for one reposition leg.</summary>
        public TimeSpan RepositionTimeout { get; init; } = TimeSpan.FromSeconds(150);
    }

    /// <summary>
    /// Deterministic-pump seam (the m3a-m4 replay's pump shape, minus the
    /// v0-absent vehicle/pack members): LIVE advances in-flight requests on
    /// real time and waits on the engine's growth timers; UNIT rigs drive
    /// everything deterministically.
    /// </summary>
    public interface ICyclePump
    {
        ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait);

        bool WaitForCropMaturity(Character character, uint cropObjId, TimeSpan maxWait);

        /// <summary>Persistence-boundary seam (unit worlds only) — the accepted
        /// Harvest-rig convention; the LIVE pump never provisions (live plants
        /// Complete). See m3a-m4-replay.</summary>
        uint? ProvisionCropAtBoundary(Character character, Vector3 position);
    }

    /// <summary>LIVE pump: delegates to the m3a-m4 replay's live pump (identical
    /// semantics — real time, engine growth timers, no provisioning).</summary>
    public sealed class LiveCyclePump : ICyclePump
    {
        private readonly M3aM4ReplayScenario.IScenarioPump _inner = new LiveReplayPump();

        public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
            => _inner.Drive(actor, request, maxWait);

        public bool WaitForCropMaturity(Character character, uint cropObjId, TimeSpan maxWait)
            => _inner.WaitForCropMaturity(character, cropObjId, maxWait);

        public uint? ProvisionCropAtBoundary(Character character, Vector3 position)
            => _inner.ProvisionCropAtBoundary(character, position);
    }

    // ---------------------------------------------------------------- ledger

    /// <summary>
    /// One observable-state snapshot (built ONLY from Character surfaces —
    /// the ledger never trusts internal counters over these reads).
    /// </summary>
    public sealed record EconomySnapshot(
        long Money,
        long BankMoney,
        int LaborPower,
        IReadOnlyDictionary<uint, int> BagCounts,
        IReadOnlyDictionary<uint, int> BankCounts)
    {
        public static EconomySnapshot Capture(Character character)
        {
            var bag = new Dictionary<uint, int>();
            foreach (var item in character.Inventory.Bag.GetItemsSnapshot())
                bag[item.TemplateId] = bag.GetValueOrDefault(item.TemplateId) + item.Count;

            var bank = new Dictionary<uint, int>();
            foreach (var item in character.Inventory.Warehouse.GetItemsSnapshot())
                bank[item.TemplateId] = bank.GetValueOrDefault(item.TemplateId) + item.Count;

            return new EconomySnapshot(character.Money, character.Money2, character.LaborPower, bag, bank);
        }
    }

    /// <summary>Per-stage ledger entry: observable state before → after.</summary>
    public sealed record LedgerEntry(string Stage, EconomySnapshot Before, EconomySnapshot After)
    {
        public long MoneyDelta => After.Money - Before.Money;
        public long BankDelta => After.BankMoney - Before.BankMoney;
        public int LaborDelta => Before.LaborPower - After.LaborPower;
    }

    /// <summary>
    /// The explicit economy ledger for one run. Totals come from the ACTION
    /// RESULTS (the engine-charged/paid values); balances come from the
    /// character record. The Reconcile* members derive the VERIFY criteria —
    /// and double as the unit-testable reconciliation law (rig tests corrupt
    /// a value and assert the criterion fails).
    /// </summary>
    public sealed class EconomyDayCycleLedger
    {
        public List<LedgerEntry> Entries { get; } = [];

        public long StartMoney { get; set; }
        public long EndMoney { get; set; }
        public long StartBank { get; set; }
        public long EndBank { get; set; }
        public int StartLabor { get; set; }
        public int EndLabor { get; set; }

        public long BuyTotal { get; set; }
        public long SellTotal { get; set; }
        public long DepositTotal { get; set; }
        public long WithdrawTotal { get; set; }

        public int CyclesCompleted { get; set; }
        public int PlantsCharged { get; set; }
        public int PlantLaborCostEach { get; set; }
        public int HarvestsCharged { get; set; }
        public int HarvestLaborCostEach { get; set; }
        public int CraftsCharged { get; set; }
        public int CraftLaborCostEach { get; set; }
        public int HarvestSeedGrants { get; set; }
        public int SeedsPlanted { get; set; }
        public long SeedsBought { get; set; }
        public int SeedsHeldEnd { get; set; }

        public int DocumentedLabor =>
            PlantsCharged * PlantLaborCostEach +
            HarvestsCharged * HarvestLaborCostEach +
            CraftsCharged * CraftLaborCostEach;

        /// <summary>Documented currency law: end == start − buys + sells − deposits + withdrawals (exact).</summary>
        public long ExpectedEndMoney => StartMoney - BuyTotal + SellTotal - DepositTotal + WithdrawTotal;

        /// <summary>Documented bank law: end == start + deposits − withdrawals (exact).</summary>
        public long ExpectedEndBank => StartBank + DepositTotal - WithdrawTotal;

        public BotScenarioRunner.CriterionVerdict ReconcileCurrency()
        {
            var ok = EndMoney == ExpectedEndMoney;
            return new BotScenarioRunner.CriterionVerdict("currency-conservation", ok,
                $"money {EndMoney} == start {StartMoney} − buys {BuyTotal} + sells {SellTotal} " +
                $"− deposits {DepositTotal} + withdrawals {WithdrawTotal} — expected {ExpectedEndMoney}" +
                (ok ? "" : " — MISMATCH"));
        }

        public BotScenarioRunner.CriterionVerdict ReconcileBank()
        {
            var ok = EndBank == ExpectedEndBank;
            return new BotScenarioRunner.CriterionVerdict("bank-conservation", ok,
                $"bank {EndBank} == start {StartBank} + deposits {DepositTotal} − withdrawals {WithdrawTotal} " +
                $"— expected {ExpectedEndBank}" + (ok ? "" : " — MISMATCH"));
        }

        /// <summary>The stage-sum law: Σ(per-entry observable deltas) must equal the overall
        /// delta — catches a ledger entry that silently skipped a mutation.</summary>
        public BotScenarioRunner.CriterionVerdict ReconcileStageSums()
        {
            var moneySum = Entries.Sum(e => e.MoneyDelta);
            var bankSum = Entries.Sum(e => e.BankDelta);
            var moneyOk = moneySum == EndMoney - StartMoney;
            var bankOk = bankSum == EndBank - StartBank;
            return new BotScenarioRunner.CriterionVerdict("ledger-stage-sums-reconcile", moneyOk && bankOk,
                $"Σ stage money deltas {moneySum} == overall {EndMoney - StartMoney}; " +
                $"Σ stage bank deltas {bankSum} == overall {EndBank - StartBank} ({Entries.Count} entries)" +
                (moneyOk && bankOk ? "" : " — MISMATCH"));
        }

        public BotScenarioRunner.CriterionVerdict ReconcileLabor(int tolerance)
        {
            var consumed = StartLabor - EndLabor;
            var documented = DocumentedLabor;
            var ok = Math.Abs(consumed - documented) <= tolerance;
            return new BotScenarioRunner.CriterionVerdict("labor-conservation", ok,
                $"labor consumed {consumed} == documented {documented} (±{tolerance}) — " +
                $"plants {PlantsCharged} x {PlantLaborCostEach} + harvests {HarvestsCharged} x {HarvestLaborCostEach} " +
                $"+ crafts {CraftsCharged} x {CraftLaborCostEach}" + (ok ? "" : " — MISMATCH"));
        }

        /// <summary>Seed law: planted == bought AND held == bought − planted + documented grants.</summary>
        public BotScenarioRunner.CriterionVerdict ReconcileSeeds()
        {
            var expected = SeedsBought - SeedsPlanted + HarvestSeedGrants;
            var ok = SeedsHeldEnd == expected && SeedsPlanted == SeedsBought;
            return new BotScenarioRunner.CriterionVerdict("seed-conservation", ok,
                $"seeds bought {SeedsBought}, planted {SeedsPlanted}, grants {HarvestSeedGrants}, " +
                $"held {SeedsHeldEnd} == expected {expected}" + (ok ? "" : " — MISMATCH"));
        }
    }

    // ------------------------------------------------------------------ run

    /// <summary>Live entry (bridge dispatch): default options + the live pump.</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(Character character, BotScenarioRunner.IScenarioWorldAdapter world)
        => Run(character, world, new LiveCyclePump(), new CycleOptions());

    /// <summary>Live entry with explicit options.</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(
        Character character, BotScenarioRunner.IScenarioWorldAdapter world, CycleOptions options)
        => Run(character, world, new LiveCyclePump(), options);

    /// <summary>Testable core: inject the pump + options (unit rigs pass fixture ids).
    /// When <paramref name="ledgerSink"/> is non-null it receives the run's ledger
    /// (success AND failure paths) so callers can audit or corrupt-and-recheck it.</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(
        Character character, BotScenarioRunner.IScenarioWorldAdapter world,
        ICyclePump pump, CycleOptions options,
        EconomyDayCycleLedger? ledgerSink = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Cycles < 1)
        {
            var emptyLedger = new EconomyDayCycleLedger();
            ledgerSink?.CopyFrom(emptyLedger);
            return Fail("RIG", null, $"cycles must be ≥ 1 (got {options.Cycles})",
                [], [], [], emptyLedger, []);
        }

        var actor = new GameplayActor(character);
        var controller = new PlayerBotController(character);
        var rigNotes = new List<string>();
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var ledger = new EconomyDayCycleLedger();
        var farmOrigin = options.FarmOrigin ?? character.Transform.World.Position;

        try
        {
            // ------------------------------------------------ 1. RIG
            character.Level = options.RigLevel;
            character.Money = options.SeedMoney;
            character.LaborPower = DefaultLaborPool;
            rigNotes.Add($"farm origin {farmOrigin} (zone {character.Transform.ZoneId})");
            rigNotes.Add($"rig: level {options.RigLevel}, money {options.SeedMoney}, labor {DefaultLaborPool}, cycles {options.Cycles}");

            // The auxiliary craft material (canonical: water 15694) is not
            // merchant-sold — provision through the normal acquisition path
            // (the millet-seed convention from m3a-m4-replay).
            var auxTotal = options.AuxiliaryMaterialAmount * options.Cycles;
            if (auxTotal > 0)
            {
                controller.StockInventory(options.AuxiliaryMaterialItemId, auxTotal);
                rigNotes.Add($"stocked {auxTotal} x aux material {options.AuxiliaryMaterialItemId} (not merchant-sold)");
            }

            ledger.StartMoney = character.Money;
            ledger.StartBank = character.Money2;
            ledger.StartLabor = character.LaborPower;

            // Documented plant labor: the seed's use-skill ConsumeLaborPower
            // (the same value the Plant gate charges on unclaimed land).
            ledger.PlantLaborCostEach =
                SkillLaborCost(ItemManager.Instance.GetTemplate(options.SeedItemId)?.UseSkillId ?? 0);

            // ------------------------------------------------ CYCLES
            for (var cycle = 0; cycle < options.Cycles; cycle++)
            {
                var failure = RunCycle(cycle);
                if (failure != null)
                {
                    // Record the observable balances BEFORE reconciling, so
                    // the fail-closed verdict carries honest ledger criteria.
                    ledger.EndMoney = character.Money;
                    ledger.EndBank = character.Money2;
                    ledger.EndLabor = character.LaborPower;
                    failure.Criteria.Add(ledger.ReconcileCurrency());
                    failure.Criteria.Add(ledger.ReconcileBank());
                    ledgerSink?.CopyFrom(ledger);
                    return failure;
                }
            }

            ledger.EndMoney = character.Money;
            ledger.EndBank = character.Money2;
            ledger.EndLabor = character.LaborPower;
            ledger.SeedsHeldEnd = character.Inventory.GetItemsCount(options.SeedItemId);

            // ---------------------------------------------- 2. CONSERVE
            criteria.Add(new BotScenarioRunner.CriterionVerdict("cycles-completed",
                ledger.CyclesCompleted == options.Cycles,
                $"completed {ledger.CyclesCompleted}/{options.Cycles} day cycles"));

            criteria.Add(ledger.ReconcileCurrency());
            criteria.Add(ledger.ReconcileBank());
            criteria.Add(ledger.ReconcileStageSums());
            criteria.Add(ledger.ReconcileLabor(options.LaborTolerance));
            criteria.Add(ledger.ReconcileSeeds());

            var lifecycleOk = AssertTraceCompleteness(traceRecords, out var lifecycleDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("lifecycle-trace-complete", lifecycleOk, lifecycleDetail));

            var passed = criteria.All(c => c.Passed);
            ledgerSink?.CopyFrom(ledger);
            return new BotScenarioRunner.ScenarioRunResult
            {
                Template = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                RigNotes = rigNotes,
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                ActorRequests = traceRecords.Count,
                TraceRecords = traceRecords
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "{Scenario} crashed", ScenarioName);
            ledger.EndMoney = character.Money;
            ledger.EndBank = character.Money2;
            ledger.EndLabor = character.LaborPower;
            criteria.Add(ledger.ReconcileCurrency());
            criteria.Add(ledger.ReconcileBank());
            ledgerSink?.CopyFrom(ledger);
            return Fail("RUN", null, $"crash: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                rigNotes, stages, criteria, ledger, traceRecords);
        }

        // ------------------------------------------------------- cycle body
        // A local function so every leg shares the rig/stage/trace/ledger
        // state; returns null on success, the failed-closed result otherwise.

        BotScenarioRunner.ScenarioRunResult? RunCycle(int cycle)
        {
            var key = $"m8ec-c{cycle}";
            var cycleSellTotal = 0L;

            // 1. WALK to the seed merchant + BUY seeds (the circuit's cash input).
            var seedMerchantObjId = world.ResolveNpcObjId(options.SeedMerchantNpcTemplateId);
            if (seedMerchantObjId == 0)
                return Fail($"BUY-SEEDS-{cycle}", null,
                    $"seed merchant {options.SeedMerchantNpcTemplateId} unresolvable in world",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var walkToSeedMerchant = actor.MoveToUnit(seedMerchantObjId, speed: options.RepositionSpeed,
                timeout: options.RepositionTimeout, idempotencyKey: $"{key}-walk-seed-merchant");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"WALK-TO-SEED-MERCHANT-{cycle}", walkToSeedMerchant, $"merchant {seedMerchantObjId}"));
            walkToSeedMerchant = pump.Drive(actor, walkToSeedMerchant, options.RepositionTimeout);
            if (walkToSeedMerchant.State != ActorLifecycleState.Completed)
                return Fail($"WALK-TO-SEED-MERCHANT-{cycle}", walkToSeedMerchant, "reposition to seed merchant",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var beforeBuy = EconomySnapshot.Capture(character);
            var buySeeds = actor.Buy(seedMerchantObjId, options.SeedItemId, options.SeedsPerCycle,
                idempotencyKey: $"{key}-buy-seeds");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"BUY-SEEDS-{cycle}", buySeeds, $"seed {options.SeedItemId} x{options.SeedsPerCycle}"));
            if (buySeeds.State != ActorLifecycleState.Completed)
                return Fail($"BUY-SEEDS-{cycle}", buySeeds, "seed purchase", rigNotes, stages, criteria, ledger, traceRecords);
            ledger.BuyTotal += ReadLongResult(buySeeds.Result);
            ledger.SeedsBought += options.SeedsPerCycle;
            ledger.Entries.Add(new LedgerEntry($"BUY-SEEDS-{cycle}", beforeBuy, EconomySnapshot.Capture(character)));

            // 2. WALK back to the plot + PLANT (contract Plant; engine consumes the seed).
            var walkToFarm = actor.MoveTo(farmOrigin, speed: options.RepositionSpeed,
                timeout: options.RepositionTimeout, idempotencyKey: $"{key}-walk-farm");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"WALK-TO-FARM-{cycle}", walkToFarm, $"farm origin {farmOrigin}"));
            walkToFarm = pump.Drive(actor, walkToFarm, options.RepositionTimeout);
            if (walkToFarm.State != ActorLifecycleState.Completed)
                return Fail($"WALK-TO-FARM-{cycle}", walkToFarm, "reposition to farm",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var positions = PlotPositions(farmOrigin, options.SeedsPerCycle, options.PlotSpacing);
            var planted = new List<uint>();
            for (var i = 0; i < options.SeedsPerCycle; i++)
            {
                var plant = actor.Plant(options.SeedItemId, positions[i],
                    idempotencyKey: $"{key}-plant-{i}");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"PLANT-{cycle}-{i}", plant, $"seed {options.SeedItemId}"));
                var cropObjId = (uint)ReadUlongResult(plant.Result);
                if (plant.State == ActorLifecycleState.Completed && cropObjId != 0)
                {
                    planted.Add(cropObjId);
                }
                else if (plant.State == ActorLifecycleState.Interrupted)
                {
                    // Persistence-boundary interrupt (unit worlds): the
                    // contract action ran its gates; the crop must have been
                    // provisioned by the pump (the accepted Harvest-rig
                    // convention) or the cycle fails closed.
                    cropObjId = pump.ProvisionCropAtBoundary(character, positions[i]) ?? 0;
                    if (cropObjId == 0)
                        return Fail($"PLANT-{cycle}-{i}", plant,
                            $"plant {i}: persistence-boundary interrupt with no in-world crop",
                            rigNotes, stages, criteria, ledger, traceRecords);
                    planted.Add(cropObjId);
                    rigNotes.Add($"plant {cycle}/{i}: Interrupted at persistence boundary (crop {cropObjId} provisioned by pump)");
                }
                else
                {
                    return Fail($"PLANT-{cycle}-{i}", plant, $"plant {i}", rigNotes, stages, criteria, ledger, traceRecords);
                }
            }

            ledger.PlantsCharged += planted.Count;
            ledger.SeedsPlanted += planted.Count;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"farm-planted-all-{cycle}",
                planted.Count == options.SeedsPerCycle,
                $"cycle {cycle}: planted {planted.Count}/{options.SeedsPerCycle} crops"));

            // 3. GROW (engine timers via the pump) + HARVEST.
            foreach (var cropObjId in planted)
            {
                if (!pump.WaitForCropMaturity(character, cropObjId, options.CropMaturityTimeout))
                {
                    var diagCrop = character.ParentWorld?.GetDoodad(cropObjId);
                    return Fail($"GROW-{cycle}", null,
                        $"crop {cropObjId} not harvestable within {options.CropMaturityTimeout} " +
                        $"(exists={diagCrop != null}, phase={diagCrop?.FuncGroupId})",
                        rigNotes, stages, criteria, ledger, traceRecords);
                }

                var crop = character.ParentWorld?.GetDoodad(cropObjId);
                if (crop != null && ledger.HarvestLaborCostEach == 0 &&
                    M3aM4ReplayScenario.TryGetHarvestSkill(crop, out var harvestSkillId))
                {
                    ledger.HarvestLaborCostEach = SkillLaborCost(harvestSkillId);
                }
                if (crop != null)
                    ledger.HarvestSeedGrants += SeedGrantForCrop(crop, options.SeedItemId);

                var beforeHarvest = EconomySnapshot.Capture(character);
                var harvest = actor.Harvest(cropObjId, idempotencyKey: $"{key}-harvest-{cropObjId}");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"HARVEST-{cycle}-{cropObjId}", harvest, $"crop {cropObjId}"));
                if (harvest.State != ActorLifecycleState.Completed)
                    return Fail($"HARVEST-{cycle}", harvest, $"crop {cropObjId}",
                        rigNotes, stages, criteria, ledger, traceRecords);
                ledger.HarvestsCharged++;
                ledger.Entries.Add(new LedgerEntry($"HARVEST-{cycle}-{cropObjId}", beforeHarvest, EconomySnapshot.Capture(character)));
            }

            criteria.Add(new BotScenarioRunner.CriterionVerdict($"farm-harvested-all-{cycle}",
                true,
                $"cycle {cycle}: harvested all {planted.Count} crops"));

            // 4. WALK to the general merchant + CRAFT at the nearest doodad bench
            //    (ReqDoodadId == 0 recipes accept any world doodad target — the
            //    same resolution the m3a-m4 pack craft uses).
            var generalMerchantObjId = world.ResolveNpcObjId(options.GeneralMerchantNpcTemplateId);
            if (generalMerchantObjId == 0)
                return Fail($"SELL-{cycle}", null,
                    $"general merchant {options.GeneralMerchantNpcTemplateId} unresolvable in world",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var walkToMerchant = actor.MoveToUnit(generalMerchantObjId, speed: options.RepositionSpeed,
                timeout: options.RepositionTimeout, idempotencyKey: $"{key}-walk-merchant");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"WALK-TO-MERCHANT-{cycle}", walkToMerchant, $"merchant {generalMerchantObjId}"));
            walkToMerchant = pump.Drive(actor, walkToMerchant, options.RepositionTimeout);
            if (walkToMerchant.State != ActorLifecycleState.Completed)
                return Fail($"WALK-TO-MERCHANT-{cycle}", walkToMerchant, "reposition to merchant",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var benchObjId = ResolveNearestDoodad(character);
            if (benchObjId == 0)
                return Fail($"CRAFT-{cycle}", null, "no world doodad in range to serve as the craft bench target",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var bench = character.ParentWorld?.GetDoodad(benchObjId);
            var benchPos = bench?.Transform.World.Position ?? character.Transform.World.Position;
            character.Transform.Local.SetPosition(benchPos + new Vector3(2f, 0f, 0f));

            var beforeCraft = EconomySnapshot.Capture(character);
            var craft = actor.Craft(options.CraftId, benchObjId, idempotencyKey: $"{key}-craft");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"CRAFT-{cycle}", craft, $"craft {options.CraftId} @ bench {benchObjId}"));
            craft = pump.Drive(actor, craft, options.ActionPumpTimeout);
            if (craft.State != ActorLifecycleState.Completed)
                return Fail($"CRAFT-{cycle}", craft, $"craft {options.CraftId}",
                    rigNotes, stages, criteria, ledger, traceRecords);
            ledger.CraftsCharged++;
            if (ledger.CraftLaborCostEach == 0)
            {
                var craftSkillId = CraftManager.Instance.GetCraftById(options.CraftId)?.SkillId ?? 0;
                ledger.CraftLaborCostEach = SkillLaborCost(craftSkillId);
            }
            ledger.Entries.Add(new LedgerEntry($"CRAFT-{cycle}", beforeCraft, EconomySnapshot.Capture(character)));

            // 5. SELL every crafted-product stack in the bag to the general
            //    merchant (the REAL engine refund path; the stack moves to
            //    BuyBackItems — the engine-true sell-once invariant).
            var soldThisCycle = 0;
            while (FindSellableYield(character, options.ProductItemId) is { } product)
            {
                var beforeSell = EconomySnapshot.Capture(character);
                var sell = actor.Sell(generalMerchantObjId, product.Id, idempotencyKey: $"{key}-sell-{soldThisCycle}");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"SELL-{cycle}-{soldThisCycle}", sell,
                    $"product {options.ProductItemId} x{product.Count} (instance {product.Id})"));
                if (sell.State != ActorLifecycleState.Completed)
                    return Fail($"SELL-{cycle}", sell, $"product instance {product.Id}",
                        rigNotes, stages, criteria, ledger, traceRecords);
                var refund = ReadLongResult(sell.Result);
                ledger.SellTotal += refund;
                cycleSellTotal += refund;
                ledger.Entries.Add(new LedgerEntry($"SELL-{cycle}-{soldThisCycle}", beforeSell, EconomySnapshot.Capture(character)));
                soldThisCycle++;
                if (soldThisCycle > 64)
                    return Fail($"SELL-{cycle}", null, "sell loop did not converge (64+ stacks)",
                        rigNotes, stages, criteria, ledger, traceRecords);
            }

            if (soldThisCycle == 0)
                return Fail($"SELL-{cycle}", null,
                    $"no sellable product {options.ProductItemId} in bag after craft",
                    rigNotes, stages, criteria, ledger, traceRecords);

            // 6. BANK the day's proceeds (the persistence-tested leg).
            var depositAmount = options.Mode switch
            {
                DepositMode.Proceeds => cycleSellTotal,
                DepositMode.Fixed => options.FixedDepositAmount,
                _ => 0
            };
            if (depositAmount > 0)
            {
                var beforeDeposit = EconomySnapshot.Capture(character);
                var deposit = actor.DepositMoney(depositAmount, idempotencyKey: $"{key}-deposit");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"DEPOSIT-MONEY-{cycle}", deposit, $"{depositAmount}c"));
                if (deposit.State != ActorLifecycleState.Completed)
                    return Fail($"DEPOSIT-MONEY-{cycle}", deposit, "money deposit",
                        rigNotes, stages, criteria, ledger, traceRecords);
                ledger.DepositTotal += depositAmount;
                ledger.Entries.Add(new LedgerEntry($"DEPOSIT-MONEY-{cycle}", beforeDeposit, EconomySnapshot.Capture(character)));
            }

            ledger.CyclesCompleted++;
            rigNotes.Add($"cycle {cycle}: sell +{cycleSellTotal}, deposit {depositAmount}, " +
                         $"money {character.Money}, bank {character.Money2}");
            return null;
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Documented labor cost of a skill (ConsumeLaborPower), 0 when absent.</summary>
    private static int SkillLaborCost(uint skillId)
        => SkillManager.Instance.GetSkillTemplate(skillId)?.ConsumeLaborPower ?? 0;

    private static List<Vector3> PlotPositions(Vector3 origin, int count, float spacing)
    {
        var positions = new List<Vector3>(count);
        for (var i = 0; i < count; i++)
        {
            var row = i / 3;
            var col = i % 3;
            positions.Add(new Vector3(
                origin.X + col * spacing,
                origin.Y + row * spacing,
                origin.Z));
        }

        return positions;
    }

    private static ulong ReadUlongResult(object? result)
    {
        return result switch
        {
            ulong u => u,
            long l when l > 0 => (ulong)l,
            uint ui => ui,
            int i when i > 0 => (uint)i,
            _ => 0
        };
    }

    /// <summary>Result payloads are typed by the action: Buy/Sell/Deposit return
    /// numeric amounts — normalize to long.</summary>
    private static long ReadLongResult(object? result)
    {
        return result switch
        {
            long l => l,
            int i => i,
            uint ui => ui,
            ulong u when u <= long.MaxValue => (long)u,
            _ => 0
        };
    }

    /// <summary>Nearest world doodad to the actor (the ReqDoodadId=0 craft bench target).</summary>
    private static uint ResolveNearestDoodad(Character character)
    {
        var world = character.ParentWorld;
        if (world == null)
            return 0;

        uint nearest = 0;
        var distance = float.MaxValue;
        foreach (var doodad in world.GetAllDoodads())
        {
            var d = Vector3.Distance(character.Transform.World.Position, doodad.Transform.World.Position);
            if (d < distance)
            {
                distance = d;
                nearest = doodad.ObjId;
            }
        }

        return nearest;
    }

    private static Item? FindSellableYield(Character character, uint templateId)
        => character.Inventory?.Bag.Items.FirstOrDefault(i => i.TemplateId == templateId && (i.Template?.Sellable ?? false));

    /// <summary>
    /// The DOCUMENTED seed grant of a harvested crop: the seed-item rows in
    /// the loot pack its harvest chain leads into (MinAmount is the guaranteed
    /// grant; canonical potato pack 6452 returns exactly 1 seed per harvest).
    /// </summary>
    private static int SeedGrantForCrop(Doodad crop, uint seedItemId)
    {
        var funcs = DoodadManager.Instance.GetFuncsForGroup(crop.FuncGroupId);
        if (funcs == null)
            return 0;

        var grants = 0;
        foreach (var func in funcs)
        {
            if (func.FuncType != "DoodadFuncUse" || func.NextPhase <= 0)
                continue;
            var nextFuncs = DoodadManager.Instance.GetFuncsForGroup((uint)func.NextPhase);
            if (nextFuncs == null)
                continue;
            foreach (var next in nextFuncs)
            {
                if (next.FuncType != "DoodadFuncLootPack")
                    continue;
                if (DoodadManager.Instance.GetFuncTemplate(next.FuncId, next.FuncType) is not DoodadFuncLootPack lootPack)
                    continue;
                var pack = AAEmu.Game.GameData.LootGameData.Instance.GetPack(lootPack.LootPackId);
                if (pack?.Loots == null)
                    continue;
                grants += pack.Loots
                    .Where(l => l.ItemId == seedItemId)
                    .Sum(l => Math.Max(0, l.MinAmount));
            }
        }

        return grants;
    }

    /// <summary>
    /// Lifecycle correctness (the M5 audit contract): every Completed action's
    /// trace record carries the full Requested → Accepted → Running →
    /// Completed transition set; no Rejected record ever carries Running.
    /// </summary>
    private static bool AssertTraceCompleteness(List<ActorAuditRecord> records, out string detail)
    {
        var completed = records.Where(r => r.Result == ActorLifecycleState.Completed).ToList();
        var incomplete = completed
            .Where(r => r.StateChanges.Count == 0 ||
                        !r.StateChanges.Any(s => s.Contains("Requested")) ||
                        !r.StateChanges.Any(s => s.Contains("Accepted")) ||
                        !r.StateChanges.Any(s => s.Contains("Running")) ||
                        !r.StateChanges.Any(s => s.Contains("Completed")))
            .ToList();
        var rejectedRunning = records
            .Where(r => r.Result == ActorLifecycleState.Rejected && r.StateChanges.Any(s => s.Contains("Running")))
            .ToList();

        detail = $"records={records.Count} completed={completed.Count} " +
                 $"incompleteCompleted={incomplete.Count} rejectedWithRunning={rejectedRunning.Count}";
        return completed.Count >= 5 && incomplete.Count == 0 && rejectedRunning.Count == 0;
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));

    private static BotScenarioRunner.ScenarioRunResult Fail(
        string stage, ActorRequest? request, string what,
        List<string> rigNotes,
        List<BotScenarioRunner.ScenarioStageVerdict> stages,
        List<BotScenarioRunner.CriterionVerdict> criteria,
        EconomyDayCycleLedger ledger,
        List<ActorAuditRecord> traceRecords)
    {
        var reason = request?.Failure ?? ActorFailureReason.RejectedAction;
        var detail = request?.Detail ?? "";
        Logger.Warn("{Scenario} FAIL at {Stage}: {What} ({Reason}) {Detail}", ScenarioName, stage, what, reason, detail);
        return new BotScenarioRunner.ScenarioRunResult
        {
            Template = ScenarioName,
            Passed = false,
            FailStage = stage,
            Failure = reason,
            FailReason = $"{what}: {detail}",
            RigNotes = rigNotes,
            Gates = [],
            Stages = stages,
            Criteria = criteria,
            ActorRequests = traceRecords.Count,
            TraceRecords = traceRecords
        };
    }
}

/// <summary>Extension seam so a caller-provided sink adopts the run's ledger
/// (kept trivially correct: field-by-field copy).</summary>
internal static class EconomyDayCycleLedgerExtensions
{
    public static void CopyFrom(this EconomyDayCycleScenario.EconomyDayCycleLedger target,
        EconomyDayCycleScenario.EconomyDayCycleLedger source)
    {
        target.StartMoney = source.StartMoney;
        target.EndMoney = source.EndMoney;
        target.StartBank = source.StartBank;
        target.EndBank = source.EndBank;
        target.StartLabor = source.StartLabor;
        target.EndLabor = source.EndLabor;
        target.BuyTotal = source.BuyTotal;
        target.SellTotal = source.SellTotal;
        target.DepositTotal = source.DepositTotal;
        target.WithdrawTotal = source.WithdrawTotal;
        target.CyclesCompleted = source.CyclesCompleted;
        target.PlantsCharged = source.PlantsCharged;
        target.PlantLaborCostEach = source.PlantLaborCostEach;
        target.HarvestsCharged = source.HarvestsCharged;
        target.HarvestLaborCostEach = source.HarvestLaborCostEach;
        target.CraftsCharged = source.CraftsCharged;
        target.CraftLaborCostEach = source.CraftLaborCostEach;
        target.HarvestSeedGrants = source.HarvestSeedGrants;
        target.SeedsPlanted = source.SeedsPlanted;
        target.SeedsBought = source.SeedsBought;
        target.SeedsHeldEnd = source.SeedsHeldEnd;
        target.Entries.Clear();
        target.Entries.AddRange(source.Entries);
    }
}
