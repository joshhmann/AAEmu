using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

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
///
/// v1 HAULER LEG (opt-in via <see cref="CycleOptions.Hauler"/>, default
/// false = byte-compatible v0): after CRAFT the bot crafts a TRADE PACK,
/// summons its farm wagon, boards the driver seat, LOADS the pack onto a
/// cargo point (PackVehicleService → SlaveManager.AttachDoodadAtPoint),
/// DRIVES a short route (VehicleMovementModel / CSMoveUnitPacket path),
/// UNLOADS it (RecoverItem — the CSLootOpenBagPacket pack-pickup path) and
/// SELLS it at the specialty gold trader (SpecialtyManager.SellSpecialty —
/// the exact call CSSellBackpackGoodsPacket makes). Ledger stages:
/// PACK-CRAFT / SUMMON-VEHICLE / BOARD / LOAD / DRIVE / UNBOARD / UNLOAD /
/// SELL-GOLD. Conservation extensions: pack-craft labor (recipe skill),
/// sell labor −60/pack (ChangeLabor(-60, Commerce)) and the payout formula
/// round(base × ratio% × 1.05 interest) asserted against the CREATED MAIL
/// (canonical 22 h delay) — the copper is IN TRANSIT and is never added to
/// Money, so the currency law above stays EXACT.
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

    /// <summary>농업용 달구지 소환 주문서 (farm wagon summon scroll; 4 cargo points —
    /// the m3a-m4 replay's vehicle).</summary>
    public const uint FarmWagonSummonScrollItemId = 18660;

    /// <summary>샛노란 감자 (golden potato — pack-craft material, NOT merchant-sold:
    /// stocked through the ordinary acquisition path like the aux water).</summary>
    public const uint GoldenPotatoItemId = 19887;

    /// <summary>황금 감자 꾸러미 recipe (craft 5404: golden potato ×3 → pack 26489;
    /// skill 16766 장사: 특산품 제작과 포장 — the M4-exit pack craft).</summary>
    public const uint GoldenPotatoPackCraftId = 5404;

    /// <summary>황금 감자 꾸러미 (trade pack; refund 20000, origin zone group 26,
    /// placed-pack doodad 6068 — the M4-exit pack with asserted payout math).</summary>
    public const uint GoldenPotatoPackItemId = 26489;

    /// <summary>미스티 (Solzreed gold trader — bundle 10, the M4-exit sell surface).</summary>
    public const uint GoldTraderNpcTemplateId = 10664;

    /// <summary>Specialty-sale labor per pack — SellSpecialty's ChangeLabor(-60, Commerce).</summary>
    public const int SellLaborCostPerPack = 60;

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

        // ---- HAULER (optional trade-pack + vehicle leg) --------------------
        /// <summary>Run the hauler leg after CRAFT (default false = the v0
        /// circuit, byte-compatible behavior and ledger shape).</summary>
        public bool Hauler { get; init; } = false;

        /// <summary>Trade-pack recipe (live default: golden-potato pack craft).</summary>
        public uint PackCraftId { get; init; } = GoldenPotatoPackCraftId;
        public uint PackItemTemplateId { get; init; } = GoldenPotatoPackItemId;

        /// <summary>Pack-craft material (canonical golden potato 19887 — not
        /// merchant-sold: stocked like the auxiliary water).</summary>
        public uint PackMaterialItemId { get; init; } = GoldenPotatoItemId;
        public int PackMaterialAmount { get; init; } = 3;

        /// <summary>Specialty gold trader that buys the pack.</summary>
        public uint GoldTraderNpcTemplateId { get; init; } = EconomyDayCycleScenario.GoldTraderNpcTemplateId;

        /// <summary>Placed-pack doodad template of the pack's put-down skill
        /// (canonical packs → 6068).</summary>
        public uint PlacedPackDoodadTemplateId { get; init; } = M3aM4ReplayScenario.PackPlacedDoodadTemplateId;

        /// <summary>Farm-wagon summon scroll (the REAL UseItem summon on live).</summary>
        public uint FarmWagonSummonScrollItemId { get; init; } = EconomyDayCycleScenario.FarmWagonSummonScrollItemId;

        /// <summary>Short drive leg for the loaded wagon (metres, diagonal).</summary>
        public float DriveLegDistance { get; init; } = 12f;

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

        /// <summary>Vehicle source seam (the m3a-m4 replay's exact convention):
        /// unit rigs inject their fixture slave (the fixture world carries no
        /// real summon-scroll item data); the LIVE pump returns null and the
        /// scenario drives the REAL summon path (UseItem on the scroll).</summary>
        uint? TrySummonVehicle(Character character);

        /// <summary>Persistence-boundary seam (unit worlds only, the accepted
        /// pack-rig convention): unit worlds cannot run the carried-load path's
        /// doodad-spawn tail headless, so the rig materializes a RECOVERABLE
        /// placed-pack doodad and the PLACED-load path runs the real
        /// PackVehicleService attach; the LIVE pump returns null (the carried
        /// pack loads directly through PackVehicleService). See m3a-m4-replay.</summary>
        uint? ProvisionPlacedPackDoodad(Character character);
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

        public uint? TrySummonVehicle(Character character)
            => _inner.TrySummonVehicle(character);

        public uint? ProvisionPlacedPackDoodad(Character character)
            => _inner.ProvisionPlacedPackDoodad(character);
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

        // ---- hauler leg (all zero when Hauler == false) --------------------
        public int PackCraftsCharged { get; set; }

        /// <summary>Pack recipe skill labor (canonical 16766 → 60).</summary>
        public int PackCraftLaborCostEach { get; set; }

        public int SpecialtySellsCharged { get; set; }

        /// <summary>SellSpecialty's per-pack labor (ChangeLabor(-60, Commerce)).</summary>
        public int SpecialtySellLaborCostEach { get; set; }

        /// <summary>Σ payout-mail copper actually created by the sales.</summary>
        public long SpecialtyPayoutTotal { get; set; }

        /// <summary>Σ round(base × ratio% × 1.05) — the documented payout law.</summary>
        public long ExpectedSpecialtyPayoutTotal { get; set; }

        public int DocumentedLabor =>
            PlantsCharged * PlantLaborCostEach +
            HarvestsCharged * HarvestLaborCostEach +
            CraftsCharged * CraftLaborCostEach +
            PackCraftsCharged * PackCraftLaborCostEach +
            SpecialtySellsCharged * SpecialtySellLaborCostEach;

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

        /// <summary>
        /// Hauler payout law: the created specialty-payment mail copper must
        /// equal the documented formula total EXACTLY. The payout is a delayed
        /// MAIL (canonical 22 h) — it is deliberately NOT part of
        /// <see cref="ExpectedEndMoney"/> (currency stays exact because the
        /// copper is in transit, never in Money during the run).
        /// </summary>
        public BotScenarioRunner.CriterionVerdict ReconcileSpecialtyPayout()
        {
            var ok = SpecialtyPayoutTotal == ExpectedSpecialtyPayoutTotal;
            return new BotScenarioRunner.CriterionVerdict("specialty-payout-conservation", ok,
                $"payout mails {SpecialtyPayoutTotal}c == formula {ExpectedSpecialtyPayoutTotal}c " +
                $"({SpecialtySellsCharged} sales, labor −{SpecialtySellLaborCostEach}/pack — IN TRANSIT, not Money)" +
                (ok ? "" : " — MISMATCH"));
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

            // The pack-craft material (canonical: golden potato 19887) is not
            // merchant-sold either — same stocking convention as the aux water.
            if (options.Hauler)
            {
                var packMaterialTotal = options.PackMaterialAmount * options.Cycles;
                if (packMaterialTotal > 0)
                {
                    controller.StockInventory(options.PackMaterialItemId, packMaterialTotal);
                    rigNotes.Add($"stocked {packMaterialTotal} x pack material {options.PackMaterialItemId} (not merchant-sold)");
                }

                ledger.PackCraftLaborCostEach =
                    SkillLaborCost(CraftManager.Instance.GetCraftById(options.PackCraftId)?.SkillId ?? 0);
                ledger.SpecialtySellLaborCostEach = SellLaborCostPerPack;
                rigNotes.Add($"hauler leg ON: pack craft {options.PackCraftId} → pack {options.PackItemTemplateId}, " +
                             $"gold trader {options.GoldTraderNpcTemplateId}");
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
            if (options.Hauler)
                criteria.Add(ledger.ReconcileSpecialtyPayout());

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
            stages.Add(Stage($"WALK-TO-SEED-MERCHANT-{cycle}", walkToSeedMerchant, $"merchant {seedMerchantObjId}"));
            // A warm-world merchant resolve keeps the leg Running past creation;
            // drive to terminal BEFORE reading the trace (resolved by TraceId —
            // an early AuditTrace.Last() grabs the PREVIOUS action's record).
            if (!TryDriveLegToTerminal(actor, pump, walkToSeedMerchant, options.RepositionTimeout, traceRecords,
                    out walkToSeedMerchant))
            {
                walkToSeedMerchant.Expire(ActorFailureReason.Navigation,
                    $"reposition leg exceeded its budget ({options.RepositionTimeout})");
                return Fail($"WALK-TO-SEED-MERCHANT-{cycle}", walkToSeedMerchant, "reposition to seed merchant",
                    rigNotes, stages, criteria, ledger, traceRecords);
            }
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
            stages.Add(Stage($"WALK-TO-FARM-{cycle}", walkToFarm, $"farm origin {farmOrigin}"));
            // Same warm-world discipline as the seed-merchant leg above.
            if (!TryDriveLegToTerminal(actor, pump, walkToFarm, options.RepositionTimeout, traceRecords, out walkToFarm))
            {
                walkToFarm.Expire(ActorFailureReason.Navigation,
                    $"reposition leg exceeded its budget ({options.RepositionTimeout})");
                return Fail($"WALK-TO-FARM-{cycle}", walkToFarm, "reposition to farm",
                    rigNotes, stages, criteria, ledger, traceRecords);
            }
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
            stages.Add(Stage($"WALK-TO-MERCHANT-{cycle}", walkToMerchant, $"merchant {generalMerchantObjId}"));
            // Same warm-world discipline as the seed-merchant leg above.
            if (!TryDriveLegToTerminal(actor, pump, walkToMerchant, options.RepositionTimeout, traceRecords, out walkToMerchant))
            {
                walkToMerchant.Expire(ActorFailureReason.Navigation,
                    $"reposition leg exceeded its budget ({options.RepositionTimeout})");
                return Fail($"WALK-TO-MERCHANT-{cycle}", walkToMerchant, "reposition to merchant",
                    rigNotes, stages, criteria, ledger, traceRecords);
            }
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
            stages.Add(Stage($"CRAFT-{cycle}", craft, $"craft {options.CraftId} @ bench {benchObjId}"));
            // The craft request stays Running until the engine craft queue
            // drains — drive to terminal BEFORE reading the trace (resolved by
            // TraceId; an early AuditTrace.Last() captures the PREVIOUS action).
            if (!TryDriveLegToTerminal(actor, pump, craft, options.ActionPumpTimeout, traceRecords, out craft))
            {
                craft.Expire(ActorFailureReason.Starvation,
                    $"craft queue drain exceeded its budget ({options.ActionPumpTimeout})");
                return Fail($"CRAFT-{cycle}", craft, $"craft {options.CraftId}",
                    rigNotes, stages, criteria, ledger, traceRecords);
            }
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

            // 4b. HAULER LEG (opt-in): craft a trade pack → summon + board the
            // farm wagon → load the pack onto a cargo point → drive a short
            // route → unload → sell at the specialty gold trader. Payout is a
            // delayed MAIL asserted by formula; copper stays IN TRANSIT.
            if (options.Hauler)
            {
                var haulerFailure = RunHaulerLeg(cycle, key);
                if (haulerFailure != null)
                    return haulerFailure;
            }

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

        // ---------------------------------------------------- hauler leg body
        // Local function over the same rig/stage/trace/ledger state; returns
        // null on success, the failed-closed result otherwise.

        BotScenarioRunner.ScenarioRunResult? RunHaulerLeg(int cycle, string key)
        {
            // ---- PACK-CRAFT at the same bench (EndCraft grants the pack into
            // the Backpack equipment slot via TryEquipNewBackPack).
            var packBenchObjId = ResolveNearestDoodad(character);
            if (packBenchObjId == 0)
                return Fail($"PACK-CRAFT-{cycle}", null,
                    "no world doodad in range to serve as the pack-craft bench target",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var packBench = character.ParentWorld?.GetDoodad(packBenchObjId);
            var packBenchPos = packBench?.Transform.World.Position ?? character.Transform.World.Position;
            character.Transform.Local.SetPosition(packBenchPos + new Vector3(2f, 0f, 0f));

            var beforePackCraft = EconomySnapshot.Capture(character);
            var packCraft = actor.Craft(options.PackCraftId, packBenchObjId, idempotencyKey: $"{key}-pack-craft");
            stages.Add(Stage($"PACK-CRAFT-{cycle}", packCraft, $"craft {options.PackCraftId} @ bench {packBenchObjId}"));
            if (!TryDriveLegToTerminal(actor, pump, packCraft, options.ActionPumpTimeout, traceRecords, out packCraft))
            {
                packCraft.Expire(ActorFailureReason.Starvation,
                    $"craft queue drain exceeded its budget ({options.ActionPumpTimeout})");
                return Fail($"PACK-CRAFT-{cycle}", packCraft, $"pack craft {options.PackCraftId}",
                    rigNotes, stages, criteria, ledger, traceRecords);
            }
            if (packCraft.State != ActorLifecycleState.Completed)
                return Fail($"PACK-CRAFT-{cycle}", packCraft, $"pack craft {options.PackCraftId}",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var craftedPack = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            var packGranted = craftedPack is { TemplateId: var grantedTpl } && grantedTpl == options.PackItemTemplateId;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"haul-pack-granted-{cycle}",
                packGranted,
                packGranted
                    ? $"pack {options.PackItemTemplateId} granted to Backpack slot (instance {craftedPack!.Id})"
                    : $"pack {options.PackItemTemplateId} NOT in the Backpack slot after craft"));
            if (!packGranted)
                return Fail($"PACK-CRAFT-{cycle}", packCraft, "crafted pack missing from the Backpack slot",
                    rigNotes, stages, criteria, ledger, traceRecords);

            ledger.PackCraftsCharged++;
            ledger.Entries.Add(new LedgerEntry($"PACK-CRAFT-{cycle}", beforePackCraft, EconomySnapshot.Capture(character)));

            // ---- SUMMON the farm wagon. LIVE: the real UseItem summon path on
            // the scroll; UNIT rigs inject their fixture slave through the pump
            // (the m3a-m4 vehicle-source seam).
            Slave? wagon;
            var fixtureVehicle = pump.TrySummonVehicle(character);
            if (fixtureVehicle is { } fixtureObjId)
            {
                wagon = character.ParentWorld?.SlaveManager.GetSlaveByObjId(fixtureObjId);
                rigNotes.Add($"hauler {cycle}: vehicle injected by pump (fixture slave {fixtureObjId})");
            }
            else
            {
                // GCD settle (m3a-m4 convention): back-to-back skill uses inside
                // the engine's 150ms window are refused with CooldownTime.
                Thread.Sleep(300);
                var summon = actor.UseItem(options.FarmWagonSummonScrollItemId, idempotencyKey: $"{key}-summon-wagon");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"SUMMON-VEHICLE-{cycle}", summon, $"scroll {options.FarmWagonSummonScrollItemId}"));
                if (summon.State != ActorLifecycleState.Completed)
                    return Fail($"SUMMON-VEHICLE-{cycle}", summon, "farm wagon summon",
                        rigNotes, stages, criteria, ledger, traceRecords);

                wagon = character.ParentWorld?.SlaveManager.GetActiveSlaveByOwnerObjId(character.ObjId);
            }

            if (wagon == null)
                return Fail($"VEHICLE-{cycle}", null, "farm wagon did not materialize (no owned slave in world)",
                    rigNotes, stages, criteria, ledger, traceRecords);

            // ---- BOARD the driver seat (SlaveManager.BindSlave).
            var board = actor.BoardVehicle(wagon.ObjId, AttachPointKind.Driver, idempotencyKey: $"{key}-board");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"BOARD-{cycle}", board, $"slave {wagon.ObjId}"));
            if (board.State != ActorLifecycleState.Completed)
                return Fail($"BOARD-{cycle}", board, $"slave {wagon.ObjId}",
                    rigNotes, stages, criteria, ledger, traceRecords);

            // ---- LOAD the pack onto a cargo point (PackVehicleService).
            // LIVE: carried-load path; UNIT worlds: the pump materializes a
            // RECOVERABLE placed-pack doodad first and the PLACED-load path
            // runs (the accepted pack-rig convention, verbatim from m3a-m4).
            var loadDoodadObjId = pump.ProvisionPlacedPackDoodad(character);
            var load = loadDoodadObjId is { } loadDoodadId
                ? actor.LoadPackOntoVehicle(wagon.ObjId, loadDoodadId, idempotencyKey: $"{key}-load")
                : actor.LoadPackOntoVehicle(wagon.ObjId, placedPackDoodadObjId: null, idempotencyKey: $"{key}-load");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"LOAD-{cycle}", load, $"slave {wagon.ObjId}"));
            if (load.State != ActorLifecycleState.Completed)
                return Fail($"LOAD-{cycle}", load, $"slave {wagon.ObjId}",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var cargoDoodad = FindLoadedPackDoodad(character, options.PackItemTemplateId, wagon.ObjId);
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"haul-pack-on-vehicle-{cycle}",
                cargoDoodad != null,
                cargoDoodad != null
                    ? $"pack instance {craftedPack!.Id} on cargo doodad {cargoDoodad!.ObjId} of slave {wagon.ObjId}"
                    : $"pack instance {craftedPack!.Id} NOT found on slave {wagon.ObjId} cargo"));
            ledger.Entries.Add(new LedgerEntry($"LOAD-{cycle}",
                EconomySnapshot.Capture(character), EconomySnapshot.Capture(character)));

            // ---- DRIVE the loaded wagon a short leg — the client-authored
            // movement model (CSMoveUnitPacket path); Tick advances the leg,
            // never a Transform assignment.
            var driveStart = wagon.Transform.World.Position;
            var destination = driveStart + new Vector3(options.DriveLegDistance, options.DriveLegDistance, 0f);
            var drive = actor.DriveVehicle(wagon.ObjId, destination, speed: 5f,
                timeout: options.ActionPumpTimeout, idempotencyKey: $"{key}-drive");
            stages.Add(Stage($"DRIVE-{cycle}", drive, $"slave {wagon.ObjId} → {destination}"));
            if (!TryDriveLegToTerminal(actor, pump, drive, options.ActionPumpTimeout, traceRecords, out drive))
            {
                drive.Expire(ActorFailureReason.Navigation,
                    $"drive leg exceeded its budget ({options.ActionPumpTimeout})");
                return Fail($"DRIVE-{cycle}", drive, $"slave {wagon.ObjId}",
                    rigNotes, stages, criteria, ledger, traceRecords);
            }
            if (drive.State != ActorLifecycleState.Completed)
                return Fail($"DRIVE-{cycle}", drive, $"slave {wagon.ObjId}",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var moved = Vector3.Distance(driveStart, wagon.Transform.World.Position) > 1f;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"haul-vehicle-moved-{cycle}",
                moved,
                moved
                    ? $"wagon moved {Vector3.Distance(driveStart, wagon.Transform.World.Position):F1}m (leg {options.DriveLegDistance}m)"
                    : "wagon did not move (movement model did not advance)"));

            // ---- UNBOARD (leaves the actor beside the wagon for the unload).
            var unboard = actor.UnboardVehicle(wagon.ObjId, idempotencyKey: $"{key}-unboard");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"UNBOARD-{cycle}", unboard, $"slave {wagon.ObjId}"));
            if (unboard.State != ActorLifecycleState.Completed)
                return Fail($"UNBOARD-{cycle}", unboard, $"slave {wagon.ObjId}",
                    rigNotes, stages, criteria, ledger, traceRecords);

            // ---- UNLOAD: pick the pack off the cargo point (RecoverItem — the
            // exact CSLootOpenBagPacket pack-pickup path). The cargo doodad
            // keeps its recover funcs through the load, so the pickup restores
            // the pack into the Backpack equipment slot.
            cargoDoodad = FindLoadedPackDoodad(character, options.PackItemTemplateId, wagon.ObjId);
            if (cargoDoodad == null)
                return Fail($"UNLOAD-{cycle}", null, $"loaded pack doodad on slave {wagon.ObjId} vanished before unload",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var unload = actor.PackPickup(cargoDoodad.ObjId, idempotencyKey: $"{key}-unload");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"UNLOAD-{cycle}", unload, $"cargo doodad {cargoDoodad.ObjId}"));
            if (unload.State != ActorLifecycleState.Completed)
                return Fail($"UNLOAD-{cycle}", unload, $"cargo doodad {cargoDoodad.ObjId}",
                    rigNotes, stages, criteria, ledger, traceRecords);

            craftedPack = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            var packRecovered = craftedPack is { TemplateId: var recoveredTpl } && recoveredTpl == options.PackItemTemplateId;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"haul-pack-unloaded-{cycle}",
                packRecovered,
                packRecovered
                    ? $"pack instance {craftedPack!.Id} recovered into the Backpack slot"
                    : $"pack {options.PackItemTemplateId} NOT carried after unload"));
            if (!packRecovered)
                return Fail($"UNLOAD-{cycle}", unload, "pack pickup did not restore the Backpack slot",
                    rigNotes, stages, criteria, ledger, traceRecords);
            ledger.Entries.Add(new LedgerEntry($"UNLOAD-{cycle}",
                EconomySnapshot.Capture(character), EconomySnapshot.Capture(character)));

            // ---- WALK to the gold trader.
            var goldTraderObjId = world.ResolveNpcObjId(options.GoldTraderNpcTemplateId);
            if (goldTraderObjId == 0)
                return Fail($"SELL-GOLD-{cycle}", null,
                    $"gold trader {options.GoldTraderNpcTemplateId} unresolvable in world",
                    rigNotes, stages, criteria, ledger, traceRecords);

            var walkToTrader = actor.MoveToUnit(goldTraderObjId, speed: options.RepositionSpeed,
                timeout: options.RepositionTimeout, idempotencyKey: $"{key}-walk-trader");
            stages.Add(Stage($"WALK-TO-GOLD-TRADER-{cycle}", walkToTrader, $"trader {goldTraderObjId}"));
            if (!TryDriveLegToTerminal(actor, pump, walkToTrader, options.RepositionTimeout, traceRecords, out walkToTrader))
            {
                walkToTrader.Expire(ActorFailureReason.Navigation,
                    $"reposition leg exceeded its budget ({options.RepositionTimeout})");
                return Fail($"WALK-TO-GOLD-TRADER-{cycle}", walkToTrader, "reposition to gold trader",
                    rigNotes, stages, criteria, ledger, traceRecords);
            }
            if (walkToTrader.State != ActorLifecycleState.Completed)
                return Fail($"WALK-TO-GOLD-TRADER-{cycle}", walkToTrader, "reposition to gold trader",
                    rigNotes, stages, criteria, ledger, traceRecords);

            // ---- SELL-GOLD at the specialty trader — the REAL engine sale path
            // (CSSellBackpackGoodsPacket → SpecialtyManager.SellSpecialty). The
            // contract vocabulary has no specialty-sale action yet, so the
            // scenario calls the manager DIRECTLY — the exact call the packet
            // handler makes (M4ExitIntegratedSessionTests precedent). The sale
            // consumes the pack, charges labor −60 (Commerce), and pays via a
            // DELAYED MAIL (canonical 22 h): the payout is asserted by formula
            // against the created mail and recorded as IN TRANSIT — never added
            // to Money, so the run currency law stays EXACT.
            //
            // Fidelity repair (M4Exit rig precedent): ChangeLabor(-60, Commerce)
            // indexes the Commerce actability directly; live characters always
            // carry it, headless rigs may not.
            character.Actability.Actabilities.TryAdd((uint)ActabilityType.Commerce,
                new Actability(new ActabilityTemplate { Id = (uint)ActabilityType.Commerce }));

            var mailsBefore = SpecialityMailCopper(character.Id);
            var priceRatio = SpecialtyManager.Instance.GetRatioForSpecialty(character);

            var beforeSellGold = EconomySnapshot.Capture(character);
            var basePrice = SpecialtyManager.Instance.SellSpecialty(character, goldTraderObjId);
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                $"SELL-GOLD-{cycle}", 1,
                basePrice > 0 ? "Sold" : "Refused", basePrice.ToString(),
                $"pack {options.PackItemTemplateId} @ trader {goldTraderObjId} (base {basePrice}, ratio {priceRatio}%)" +
                SpecialitySaleRefusalHint(basePrice)));
            if (basePrice == 0)
                return Fail($"SELL-GOLD-{cycle}", null,
                    $"specialty sale refused by engine (pack {options.PackItemTemplateId} @ trader {goldTraderObjId}; " +
                    "gates: level ≥ MinLevelToCraftSell, ≤ 2.5m range, bundle membership, origin-zone exclusion)",
                    rigNotes, stages, criteria, ledger, traceRecords);

            // The documented payout law (SellSpecialty, gold trader — coin id 0,
            // no ÷10000 conversion): payout == round(base × ratio% × 1.05 interest).
            var finalNoInterest = basePrice * (priceRatio / 100f);
            var expectedPayout = (long)Math.Round(finalNoInterest + finalNoInterest * 0.05f);
            var mailPayout = SpecialityMailCopper(character.Id) - mailsBefore;

            var packConsumed = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) == null;
            var payoutOk = mailPayout == expectedPayout && packConsumed;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"haul-payout-formula-{cycle}",
                payoutOk,
                payoutOk
                    ? $"payout mail {mailPayout}c == round(base {basePrice} × {priceRatio}% × 1.05) = {expectedPayout}c; " +
                      $"pack consumed; labor −{SellLaborCostPerPack}"
                    : $"payout MISMATCH: mail delta {mailPayout}c vs expected {expectedPayout}c " +
                      $"(base {basePrice}, ratio {priceRatio}%), packConsumed={packConsumed}"));
            if (!payoutOk)
                return Fail($"SELL-GOLD-{cycle}", null, "specialty payout formula violated",
                    rigNotes, stages, criteria, ledger, traceRecords);

            ledger.SpecialtySellsCharged++;
            ledger.SpecialtyPayoutTotal += mailPayout;
            ledger.ExpectedSpecialtyPayoutTotal += expectedPayout;
            ledger.Entries.Add(new LedgerEntry($"SELL-GOLD-{cycle}", beforeSellGold, EconomySnapshot.Capture(character)));

            rigNotes.Add($"hauler {cycle}: pack {options.PackItemTemplateId} crafted → loaded → driven → unloaded → sold " +
                         $"(base {basePrice}, payout mail {mailPayout}c in transit, labor −{SellLaborCostPerPack})");
            return null;
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Drives an ASYNC leg (Move / Drive / craft-queue drain) to a TERMINAL
    /// state through the pump, then appends ITS audit record resolved by
    /// TraceId — never <c>AuditTrace.Last()</c> before the terminal transition.
    /// A warm-world resolve keeps such requests Running past creation, so an
    /// early Last() throws "Sequence contains no elements" on an empty trace or
    /// captures the PREVIOUS action's record (the same fix the m3a-m4 replay's
    /// TryDriveLegToTerminal landed). Returns false when the leg exhausts its
    /// budget WITHOUT a terminal transition; the caller then fails closed with
    /// the §17 reason (never an InvalidOperationException).
    /// </summary>
    private static bool TryDriveLegToTerminal(GameplayActor actor, ICyclePump pump,
        ActorRequest request, TimeSpan maxWait,
        List<ActorAuditRecord> traceRecords, out ActorRequest driven)
    {
        driven = pump.Drive(actor, request, maxWait);
        var drivenTraceId = driven.TraceId;
        var record = actor.AuditTrace.LastOrDefault(r => r.TraceId == drivenTraceId);
        if (record != null)
            traceRecords.Add(record);
        return driven.IsTerminal;
    }

    /// <summary>Σ CopperCoins over a character's mails — the same read surface
    /// AuctionHouseScenario uses for mail money. AllPlayerMails (not
    /// GetCurrentMailList) because the specialty payout mail's RecvDate sits a
    /// canonical 22 h in the future and GetCurrentMailList only returns
    /// delivered mail.</summary>
    private static long SpecialityMailCopper(uint characterId)
        => MailManager.Instance.AllPlayerMails.Values
            .Where(m => m.Header.ReceiverId == characterId)
            .Sum(m => m.Body.CopperCoins);

    /// <summary>Human-readable gate hint appended to a refused SELL-GOLD stage.</summary>
    private static string SpecialitySaleRefusalHint(int basePrice)
        => basePrice > 0 ? "" : " — engine refusal (error-packet surface; see gates)";

    /// <summary>The pack doodad currently loaded on the slave's cargo (attached,
    /// item-linked — the exact state PackVehicleService leaves after a load).</summary>
    private static Doodad? FindLoadedPackDoodad(Character character, uint packItemTemplateId, uint slaveObjId)
        => character.ParentWorld?.GetAllDoodads()
            .FirstOrDefault(d => d.ItemTemplateId == packItemTemplateId
                                 && d.ParentObjId == slaveObjId
                                 && d.AttachPoint != AttachPointKind.None);

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
        target.PackCraftsCharged = source.PackCraftsCharged;
        target.PackCraftLaborCostEach = source.PackCraftLaborCostEach;
        target.SpecialtySellsCharged = source.SpecialtySellsCharged;
        target.SpecialtySellLaborCostEach = source.SpecialtySellLaborCostEach;
        target.SpecialtyPayoutTotal = source.SpecialtyPayoutTotal;
        target.ExpectedSpecialtyPayoutTotal = source.ExpectedSpecialtyPayoutTotal;
        target.Entries.Clear();
        target.Entries.AddRange(source.Entries);
    }
}
