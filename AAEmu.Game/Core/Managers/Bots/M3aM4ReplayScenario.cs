using System.Numerics;
using System.Text;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// BACKTRACK Phase 2 (t_b4f455b0) — M3a contract + M4 economic/navigation
/// replay. The deferred M3a (contract) and M4 (economic/navigation) gates
/// replayed as a scripted, headless route driven through the M5.1 + B1
/// CONTRACT ACTIONS ONLY on a provisioned bot:
///
///   farm (Plant → growth → Harvest) → craft (Craft, pack recipe) →
///   pack (PutDown → PackPickup) → vehicle (UseItem summon →
///   BoardVehicle → LoadPackOntoVehicle → DriveVehicle → UnboardVehicle) →
///   trade (Buy seeds + certificate, Sell spare yield) →
///   bank (Deposit/Withdraw money + item round trips)
///
/// Every stage fires the REAL engine paths the corresponding packets drive
/// (CSCreateDoodadPacket / Doodad.Use(caster, harvestSkill) /
/// CSExecuteCraft / CSStartSkillPacket SkillItem branch / RecoverItem /
/// CSBindSlavePacket / PackVehicleService / CSMoveUnitPacket movement model /
/// CSBuyItemsPacket / CSSellItemsPacket / CSDepositMoneyPacket +
/// CSSwapItemsPacket). No new engine behavior: the scenario drives existing
/// contract actions only (target-lock: no direct DB, no reflection, no GM,
/// no direct Transform/ZoneId assignment by the ACTIONS — the rig/adapter
/// positions the actor exactly like the LiveScenarioWorldAdapter does).
///
/// The run asserts CONSERVATION from the trace records + server state:
///   - pack instance accounted exactly once across (Backpack slot ∪ System
///     container ∪ placed-pack doodad ∪ vehicle cargo doodad) — no
///     duplication, no loss;
///   - seeded item templates: Σ(consumed as documented + held now) ==
///     Σ(seeded) — plant consumes one seed per crop, craft consumes its
///     documented materials, harvest yields are documented sources;
///   - currency: money_end == money_start − Σ(buy cost) + Σ(sell refund)
///     ± bank round trip (net zero) — the documented sink/source set;
///   - labor: consumed == Σ documented costs (plant seed-skill labor +
///     craft skill labor + per-crop harvest skill labor) within a
///     documented tolerance (labor regen is a live-server timer outside
///     the run's control; unit rigs run with tolerance 0);
///   - lifecycle: every Completed action's audit record carries the full
///     Requested → Accepted → Running → Completed transition set, and no
///     Rejected record ever carries Running.
///
/// H stays UNKNOWN: this is proxy/bot-functional evidence — Josh's feel
/// verdicts are never derived from scripted actors (ledger t_547ef82d;
/// SCORECARD H dimension = actual player only).
/// </summary>
public static class M3aM4ReplayScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key (registered in <see cref="BotScenarioTemplates"/>).</summary>
    public const string ScenarioName = "m3a-m4-replay";

    /// <summary>The 26488 pack's placed-pack doodad template (put_down_backpack_effects).</summary>
    public const uint PackPlacedDoodadTemplateId = 6068;

    /// <summary>Default rigged copper (seed money for the run).</summary>
    public const long DefaultSeedMoney = 100_000;

    /// <summary>Default rig labor pool (max short — conservation measures the delta).</summary>
    public const short DefaultLaborPool = 2000;

    /// <summary>Pack recipe labor cost: skill 16766 (장사: 특산품 제작과 포장), consume_lp 60.</summary>
    public const int PackCraftLaborCost = 60;

    /// <summary>Route parameters (live defaults = canonical compact.sqlite3 ids;
    /// unit rigs inject fixture ids).</summary>
    public sealed record ReplayOptions
    {
        // ---- FARM ---------------------------------------------------------
        /// <summary>기장 씨앗 (millet seed; rigged — not merchant-sold).</summary>
        public uint MilletSeedItemId { get; init; } = 15648;
        public int MilletSeedCount { get; init; } = 4;

        /// <summary>양귀비 씨앗 (poppy seed; bought from the seed merchant via Buy).</summary>
        public uint PoppySeedItemId { get; init; } = 15680;
        public int PoppySeedCount { get; init; } = 6;

        /// <summary>Base position for the farm plot (defaults to the rig position).</summary>
        public Vector3? FarmOrigin { get; init; }

        /// <summary>Per-crop spacing on the plot (2m grid avoids placement overlap).</summary>
        public float PlotSpacing { get; init; } = 2f;

        // ---- CRAFT --------------------------------------------------------
        /// <summary>특산품: 황금 평원 마취제 (craft 5403 → pack 26488).</summary>
        public uint PackCraftId { get; init; } = 5403;
        public uint MilletMaterialItemId { get; init; } = 19909;  // 색이 좋은 기장
        public int MilletMaterialAmount { get; init; } = 3;
        public uint PoppyMaterialItemId { get; init; } = 8009;    // 양귀비
        public int PoppyMaterialAmount { get; init; } = 10;
        public uint CertificateItemId { get; init; } = 4747;      // 특산품 품질 인증서 (잡화 merchant)

        // ---- PACK ---------------------------------------------------------
        /// <summary>황금 평원 마취제 (trade pack; put_down → placed doodad 6068).</summary>
        public uint PackItemTemplateId { get; init; } = 26488;

        /// <summary>Placed-pack doodad template the put-down spawns (pack 26488 → 6068).</summary>
        public uint PlacedPackDoodadTemplateId { get; init; } = PackPlacedDoodadTemplateId;

        // ---- VEHICLE ------------------------------------------------------
        /// <summary>농업용 달구지 소환 주문서 (farm wagon summon scroll; slave 60, 4 cargo points).</summary>
        public uint FarmWagonSummonScrollItemId { get; init; } = 18660;
        public float DriveLegDistance { get; init; } = 12f;

        // ---- TRADE --------------------------------------------------------
        /// <summary>&lt;씨앗&gt; seed merchant (sells poppy seeds).</summary>
        public uint SeedMerchantNpcTemplateId { get; init; } = 8522;
        /// <summary>&lt;잡화 - 분류1&gt; general merchant (sells the certificate; buys anything sellable).</summary>
        public uint GeneralMerchantNpcTemplateId { get; init; } = 8524;

        // ---- BANK ---------------------------------------------------------
        public long BankRoundTripAmount { get; init; } = 5_000;

        // ---- RIG ----------------------------------------------------------
        public byte RigLevel { get; init; } = 10;   // pack craft level gate (MinLevelToCraftSell)
        public long SeedMoney { get; init; } = DefaultSeedMoney;

        /// <summary>Labor-conservation slack (live labor regen is a server timer; unit = 0).</summary>
        public int LaborTolerance { get; init; } = 12;

        // ---- TIME ---------------------------------------------------------
        public TimeSpan CropMaturityTimeout { get; init; } = TimeSpan.FromSeconds(180);
        public TimeSpan ActionPumpTimeout { get; init; } = TimeSpan.FromSeconds(60);

        // ---- REPOSITION (live path) ---------------------------------------
        /// <summary>Pace for the scripted walk back to the farm plot (m/s).
        /// The seed merchant's spawner can sit ~1.3 km from the plot on the
        /// live world, and the harvest gate needs the actor within
        /// MaxInteractRange of every crop. The leg runs the REAL MoveTo
        /// contract action + Tick integration; a replay pace keeps the live
        /// hook inside its 420 s budget. Unit worlds are co-located — the
        /// leg completes instantly there ("already at destination").</summary>
        public float RepositionSpeed { get; init; } = 15f;

        /// <summary>Budget for one reposition leg (the request's own timeout
        /// AND the pump's Drive window — 1.3 km at the replay pace plus lag
        /// headroom).</summary>
        public TimeSpan FarmRepositionTimeout { get; init; } = TimeSpan.FromSeconds(150);
    }

    /// <summary>
    /// World-adaptation seam: how in-flight requests are driven and how crop
    /// maturity advances. The LIVE pump sleeps real time (growth timers fire
    /// on the game loop; the E2E stack boosts World.GrowthRate); unit rigs
    /// drive the engine's scheduled tasks deterministically (no wall clock).
    /// </summary>
    public interface IScenarioPump
    {
        /// <summary>Advances an in-flight request until terminal or timeout.</summary>
        ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait);

        /// <summary>Waits until the crop's current phase is harvestable (loot-linked
        /// interaction func) or the timeout expires.</summary>
        bool WaitForCropMaturity(Character character, uint cropObjId, TimeSpan maxWait);

        /// <summary>
        /// Vehicle source seam: unit rigs inject their fixture slave (the
        /// fixture world carries no real summon-scroll item data, so the real
        /// UseItem summon path cannot run there); the LIVE pump returns null
        /// and the scenario drives the REAL summon path (UseItem on the
        /// summon scroll) before resolving the owned slave.
        /// </summary>
        uint? TrySummonVehicle(Character character);

        /// <summary>
        /// Persistence-boundary seam (unit worlds only): the contract Plant
        /// runs its full gate chain (labor + seed consumption + engine
        /// placement attempt) but the Doodad.Save() tail cannot reach MySQL,
        /// so the request lands Interrupted with the crop never spawned. The
        /// rig pump materializes the in-world crop through the fixture plant
        /// path (the accepted Harvest-rig convention) so the grow/harvest
        /// chain runs REAL engine paths; the LIVE pump returns null (live
        /// plants Complete — this member is never called on the live path).
        /// </summary>
        uint? ProvisionCropAtBoundary(Character character, Vector3 position);

        /// <summary>
        /// Persistence-boundary seam (unit worlds only): the contract PutDown
        /// moves the pack into the System container (the engine anti-dupe
        /// invariant) but the placed-pack doodad spawn tail needs the doodad
        /// template in the shared DoodadManager — the accepted pack-rig
        /// surface deliberately leaves it absent so sibling tests rely on
        /// Create() returning null. The rig pump materializes the placed
        /// doodad through the SAME fixture helper the pack tests use
        /// (PlacePackDoodad — func surface only, zero sibling impact) so the
        /// PackPickup/RecoverItem chain runs REAL engine paths; the LIVE pump
        /// returns null (live put-down spawns the doodad via MySQL).
        /// </summary>
        uint? ProvisionPlacedPackDoodad(Character character);
    }

    /// <summary>Loot-producing func types (mirror of the actor's own set — the
    /// harvestability probe reads the same data the Harvest action reads).</summary>
    private static readonly string[] LootFuncTypes =
    [
        "DoodadFuncLootPack",
        "DoodadFuncLootItem",
        "DoodadFuncHarvest",
        "DoodadFuncCropHarvest",
        "DoodadFuncFruitPick",
        "DoodadFuncCerealHarvest"
    ];

    // ------------------------------------------------------------------ run

    /// <summary>Live entry (bridge dispatch): default options + the live pump.</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(Character character, BotScenarioRunner.IScenarioWorldAdapter world)
        => Run(character, world, new LiveReplayPump(), new ReplayOptions());

    /// <summary>Testable core: inject the pump + options (unit rigs pass fixture ids).</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(
        Character character, BotScenarioRunner.IScenarioWorldAdapter world,
        IScenarioPump pump, ReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(options);

        var actor = new GameplayActor(character);
        var controller = new PlayerBotController(character);
        var rigNotes = new List<string>();
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        // Conservation state (read across the run, asserted at VERIFY).
        var farm = new FarmState();
        var craftState = new CraftState();
        var tradeState = new TradeState();
        var bankState = new BankState();

        try
        {
            // ------------------------------------------------ 1. RIG
            character.Level = options.RigLevel;
            character.Money = options.SeedMoney;
            character.LaborPower = DefaultLaborPool;
            var farmOrigin = options.FarmOrigin ?? character.Transform.World.Position;
            rigNotes.Add($"farm origin {farmOrigin} (zone {character.Transform.ZoneId})");
            rigNotes.Add($"rig: level {options.RigLevel}, money {options.SeedMoney}, labor {DefaultLaborPool}");

            // Millet seeds are not merchant-sold — provision through the normal
            // acquisition path (the same StockInventory surface the templates
            // use; AcquireDefaultItem).
            controller.StockInventory(options.MilletSeedItemId, options.MilletSeedCount);
            farm.MilletSeedsSeeded = options.MilletSeedCount;
            rigNotes.Add($"stocked {options.MilletSeedCount} x seed {options.MilletSeedItemId} (millet, not merchant-sold)");

            // The farm-wagon summon scroll is not merchant-sold either (no
            // merchant_goods row for 18660) — provision it the same way so
            // the LIVE summon path runs the REAL UseItem on the real scroll.
            // Unit worlds never reach this path (the rig pump injects the
            // fixture slave instead).
            controller.StockInventory(options.FarmWagonSummonScrollItemId, 1);
            rigNotes.Add($"stocked 1 x farm wagon summon scroll {options.FarmWagonSummonScrollItemId} (not merchant-sold)");

            // ------------------------------------------------ 2. FARM
            // 2a. Buy the poppy seeds from the seed merchant (M5.1 Buy — the
            // trade surface is part of the route; the seed purchase feeds the farm).
            var seedMerchantObjId = world.ResolveNpcObjId(options.SeedMerchantNpcTemplateId);
            if (seedMerchantObjId == 0)
                return Fail("BUY-SEEDS", null, $"seed merchant {options.SeedMerchantNpcTemplateId} unresolvable in world",
                    rigNotes, stages, criteria, traceRecords);

            // Walk into shop range (3 m): the live adapter only teleports to
            // the spawner when the merchant is NOT yet spawned — a merchant
            // already in the world resolves to its objId with no teleport,
            // and the actor can be anywhere. The MoveToUnit leg is a no-op
            // right after a teleport (same position); unit worlds are
            // co-located. Either way the Buy gate is deterministic.
            var walkToSeedMerchant = actor.MoveToUnit(seedMerchantObjId, speed: options.RepositionSpeed,
                timeout: options.FarmRepositionTimeout, idempotencyKey: "m3a4-walk-to-seed-merchant");
            stages.Add(Stage("WALK-TO-SEED-MERCHANT", walkToSeedMerchant, $"merchant {seedMerchantObjId}"));
            // A warm-world merchant resolves without a teleport, so the leg
            // starts async (Running): audit records append ONLY at terminal
            // states, so the trace is read after the drive (an early Last()
            // throws "Sequence contains no elements" or grabs the previous
            // action's record).
            if (!TryDriveLegToTerminal(actor, pump, walkToSeedMerchant,
                    options.FarmRepositionTimeout, traceRecords, out walkToSeedMerchant))
            {
                walkToSeedMerchant.Expire(ActorFailureReason.Navigation,
                    $"reposition leg exceeded its budget ({options.FarmRepositionTimeout})");
                return Fail("WALK-TO-SEED-MERCHANT", walkToSeedMerchant, "reposition to seed merchant",
                    rigNotes, stages, criteria, traceRecords);
            }
            if (walkToSeedMerchant.State != ActorLifecycleState.Completed)
                return Fail("WALK-TO-SEED-MERCHANT", walkToSeedMerchant, "reposition to seed merchant",
                    rigNotes, stages, criteria, traceRecords);

            var buySeeds = actor.Buy(seedMerchantObjId, options.PoppySeedItemId, options.PoppySeedCount,
                idempotencyKey: "m3a4-buy-poppy-seeds");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("BUY-SEEDS", buySeeds, $"poppy seed {options.PoppySeedItemId} x{options.PoppySeedCount}"));
            if (buySeeds.State != ActorLifecycleState.Completed)
                return Fail("BUY-SEEDS", buySeeds, "poppy seed purchase", rigNotes, stages, criteria, traceRecords);
            tradeState.AddBuy(options.PoppySeedItemId, options.PoppySeedCount, ReadLongResult(buySeeds.Result));
            farm.PoppySeedsSeeded = options.PoppySeedCount;

            // 2b. Plant the plot (contract Plant; seed consumed by the engine).
            var plantPositions = PlotPositions(farmOrigin, options.MilletSeedCount + options.PoppySeedCount, options.PlotSpacing);
            for (var i = 0; i < options.MilletSeedCount; i++)
            {
                var plant = actor.Plant(options.MilletSeedItemId, plantPositions[i],
                    idempotencyKey: $"m3a4-plant-millet-{i}");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"PLANT-MILLET-{i}", plant, $"seed {options.MilletSeedItemId}"));
                // The engine's persistence tail (Doodad.Save) is the only unit
                // tests cannot reach; on the LIVE path MySQL is reachable and
                // the request Completes. Interrupted-at-boundary is accepted
                // only when the in-world crop materializes (the pump's
                // fixture plant path — the accepted Harvest-rig convention;
                // the LIVE pump never provisions because live plants
                // Complete) — verified by the caller via the crop objId.
                var cropObjId = (uint)ReadUlongResult(plant.Result);
                if (plant.State == ActorLifecycleState.Completed && cropObjId != 0)
                {
                    farm.Planted.Add(cropObjId);
                }
                else if (plant.State == ActorLifecycleState.Interrupted)
                {
                    cropObjId = pump.ProvisionCropAtBoundary(character, plantPositions[i]) ?? 0;
                    if (cropObjId == 0)
                        return Fail("PLANT", plant, $"millet plant {i}: persistence-boundary interrupt with no in-world crop",
                            rigNotes, stages, criteria, traceRecords);
                    farm.Planted.Add(cropObjId);
                    rigNotes.Add($"plant {i}: Interrupted at persistence boundary (contract action ran; crop {cropObjId} provisioned by pump)");
                }
                else
                {
                    return Fail("PLANT", plant, $"millet plant {i}", rigNotes, stages, criteria, traceRecords);
                }
            }

            // Documented plant labor: the seed's use-skill ConsumeLaborPower
            // (the same value the Plant gate charges on unclaimed land).
            farm.PlantLaborCost = SkillLaborCost(ItemManager.Instance.GetTemplate(options.MilletSeedItemId)?.UseSkillId ?? 0);

            var poppyBase = options.MilletSeedCount;
            for (var i = 0; i < options.PoppySeedCount; i++)
            {
                var plant = actor.Plant(options.PoppySeedItemId, plantPositions[poppyBase + i],
                    idempotencyKey: $"m3a4-plant-poppy-{i}");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"PLANT-POPPY-{i}", plant, $"seed {options.PoppySeedItemId}"));
                var cropObjId = (uint)ReadUlongResult(plant.Result);
                if (plant.State == ActorLifecycleState.Completed && cropObjId != 0)
                {
                    farm.Planted.Add(cropObjId);
                }
                else if (plant.State == ActorLifecycleState.Interrupted)
                {
                    cropObjId = pump.ProvisionCropAtBoundary(character, plantPositions[poppyBase + i]) ?? 0;
                    if (cropObjId == 0)
                        return Fail("PLANT", plant, $"poppy plant {i}: persistence-boundary interrupt with no in-world crop",
                            rigNotes, stages, criteria, traceRecords);
                    farm.Planted.Add(cropObjId);
                    rigNotes.Add($"poppy plant {i}: Interrupted at persistence boundary (contract action ran; crop {cropObjId} provisioned by pump)");
                }
                else
                {
                    return Fail("PLANT", plant, $"poppy plant {i}", rigNotes, stages, criteria, traceRecords);
                }
            }

            criteria.Add(new BotScenarioRunner.CriterionVerdict("farm-plot-planted",
                farm.Planted.Count == options.MilletSeedCount + options.PoppySeedCount,
                $"planted {farm.Planted.Count} crops (millet {options.MilletSeedCount} + poppy {options.PoppySeedCount})"));

            // 2c. Reposition to the plot. The seed purchase resolved through
            // the merchant spawner (the live adapter's test-control seam can
            // place the actor ~1.3 km from the farm), and the harvest gate
            // requires the actor within MaxInteractRange of every crop. Walk
            // the REAL MoveTo contract action back to the farm origin — the
            // whole plot sits within a few meters of it. Unit worlds are
            // co-located, so the leg completes instantly.
            var walkToFarm = actor.MoveTo(farmOrigin, speed: options.RepositionSpeed,
                timeout: options.FarmRepositionTimeout, idempotencyKey: "m3a4-walk-to-farm");
            stages.Add(Stage("WALK-TO-FARM", walkToFarm, $"farm origin {farmOrigin}"));
            // Same warm-world discipline as the seed-merchant leg above:
            // drive to terminal BEFORE reading the trace; timeout without a
            // terminal transition fails closed (§17 navigation).
            if (!TryDriveLegToTerminal(actor, pump, walkToFarm,
                    options.FarmRepositionTimeout, traceRecords, out walkToFarm))
            {
                walkToFarm.Expire(ActorFailureReason.Navigation,
                    $"reposition leg exceeded its budget ({options.FarmRepositionTimeout})");
                return Fail("WALK-TO-FARM", walkToFarm, "reposition to farm",
                    rigNotes, stages, criteria, traceRecords);
            }
            if (walkToFarm.State != ActorLifecycleState.Completed)
                return Fail("WALK-TO-FARM", walkToFarm, "reposition to farm",
                    rigNotes, stages, criteria, traceRecords);

            // 2d. Growth (engine timers; the pump advances them), then harvest.
            foreach (var cropObjId in farm.Planted)
            {
                if (!pump.WaitForCropMaturity(character, cropObjId, options.CropMaturityTimeout))
                {
                    var diagCrop = character.ParentWorld?.GetDoodad(cropObjId);
                    var diagFuncs = diagCrop != null
                        ? DoodadManager.Instance.GetFuncsForGroup(diagCrop.FuncGroupId)
                        : null;
                    var diagPhaseFuncs = diagCrop != null
                        ? DoodadManager.Instance.GetPhaseFunc(diagCrop.FuncGroupId)
                        : null;
                    return Fail("GROW", null,
                        $"crop {cropObjId} not harvestable within {options.CropMaturityTimeout} — " +
                        $"exists={diagCrop != null} phase={diagCrop?.FuncGroupId} " +
                        $"groupFuncs={(diagFuncs == null ? "null" : diagFuncs.Count.ToString())} " +
                        $"phaseFuncs={(diagPhaseFuncs == null ? "null" : diagPhaseFuncs.Count.ToString())}",
                        rigNotes, stages, criteria, traceRecords);
                }

                // Documented harvest labor: the crop's data-driven harvest
                // skill ConsumeLaborPower (the same value the engine charges).
                var crop = character.ParentWorld?.GetDoodad(cropObjId);
                if (crop != null && TryGetHarvestSkill(crop, out var harvestSkillId) && farm.HarvestLaborCost == 0)
                    farm.HarvestLaborCost = SkillLaborCost(harvestSkillId);

                // Documented harvest seed grant: the canonical potato loot
                // pack 6452 carries a seed row (group 3: 15659 ×1 — verified
                // real loots data), so every harvest returns a seed. The
                // seed-conservation law accounts it as a documented source.
                if (crop != null)
                    farm.HarvestSeedGrants += SeedGrantForCrop(crop, options);

                var harvest = actor.Harvest(cropObjId, idempotencyKey: $"m3a4-harvest-{cropObjId}");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage($"HARVEST-{cropObjId}", harvest, $"crop {cropObjId}"));
                if (harvest.State != ActorLifecycleState.Completed)
                    return Fail("HARVEST", harvest, $"crop {cropObjId}", rigNotes, stages, criteria, traceRecords);
                farm.Harvested++;
            }

            criteria.Add(new BotScenarioRunner.CriterionVerdict("farm-harvested-all",
                farm.Harvested == farm.Planted.Count,
                $"harvested {farm.Harvested}/{farm.Planted.Count} crops"));

            // ------------------------------------------------ 3. CRAFT
            // 3a. Buy the quality certificate from the general merchant.
            var generalMerchantObjId = world.ResolveNpcObjId(options.GeneralMerchantNpcTemplateId);
            if (generalMerchantObjId == 0)
                return Fail("BUY-CERTIFICATE", null, $"general merchant {options.GeneralMerchantNpcTemplateId} unresolvable in world",
                    rigNotes, stages, criteria, traceRecords);

            // Same deterministic shop-range leg as the seed purchase: the
            // live adapter resolves an already-spawned merchant without a
            // teleport (observed live: the general merchant spawns near the
            // seed merchant's spawner while the actor is away at the farm —
            // the Buy gate then fails at 3 m). Walk into range; no-op when
            // the resolve teleported (same position).
            var walkToMerchant = actor.MoveToUnit(generalMerchantObjId, speed: options.RepositionSpeed,
                timeout: options.FarmRepositionTimeout, idempotencyKey: "m3a4-walk-to-merchant");
            stages.Add(Stage("WALK-TO-MERCHANT", walkToMerchant, $"merchant {generalMerchantObjId}"));
            // Same warm-world discipline as the seed-merchant leg above:
            // drive to terminal BEFORE reading the trace; timeout without a
            // terminal transition fails closed (§17 navigation).
            if (!TryDriveLegToTerminal(actor, pump, walkToMerchant,
                    options.FarmRepositionTimeout, traceRecords, out walkToMerchant))
            {
                walkToMerchant.Expire(ActorFailureReason.Navigation,
                    $"reposition leg exceeded its budget ({options.FarmRepositionTimeout})");
                return Fail("WALK-TO-MERCHANT", walkToMerchant, "reposition to merchant",
                    rigNotes, stages, criteria, traceRecords);
            }
            if (walkToMerchant.State != ActorLifecycleState.Completed)
                return Fail("WALK-TO-MERCHANT", walkToMerchant, "reposition to merchant",
                    rigNotes, stages, criteria, traceRecords);

            var buyCert = actor.Buy(generalMerchantObjId, options.CertificateItemId, 1,
                idempotencyKey: "m3a4-buy-certificate");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("BUY-CERTIFICATE", buyCert, $"certificate {options.CertificateItemId}"));
            if (buyCert.State != ActorLifecycleState.Completed)
                return Fail("BUY-CERTIFICATE", buyCert, "certificate purchase", rigNotes, stages, criteria, traceRecords);
            tradeState.AddBuy(options.CertificateItemId, 1, ReadLongResult(buyCert.Result));

            // 3b. Materials check (the craft gate pre-flights the bag; the
            // scenario surfaces the counts so a shortage is a clean criterion).
            var milletHeld = character.Inventory.GetItemsCount(options.MilletMaterialItemId);
            var poppyHeld = character.Inventory.GetItemsCount(options.PoppyMaterialItemId);
            var materialsOk = milletHeld >= options.MilletMaterialAmount && poppyHeld >= options.PoppyMaterialAmount;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-materials-harvested",
                materialsOk,
                materialsOk
                    ? $"materials present: millet {milletHeld}/{options.MilletMaterialAmount}, poppy {poppyHeld}/{options.PoppyMaterialAmount}"
                    : $"materials SHORT: millet {milletHeld}/{options.MilletMaterialAmount}, poppy {poppyHeld}/{options.PoppyMaterialAmount}"));

            // 3c. Bench: the pack recipe's skill targets doodads; ReqDoodadId is 0
            // (any world doodad is a valid craft target — the engine only checks
            // the template when ReqDoodadId > 0). Resolve the nearest doodad to
            // the actor, stand within the 4m skill range, and craft.
            var benchObjId = ResolveNearestDoodad(character, out var benchDistance);
            if (benchObjId == 0)
                return Fail("CRAFT", null, "no world doodad in range to serve as the craft bench target",
                    rigNotes, stages, criteria, traceRecords);

            // Position beside the bench (the rig/adapter teleport surface — same
            // facility the LiveScenarioWorldAdapter uses to reach spawners).
            var bench = character.ParentWorld?.GetDoodad(benchObjId);
            var benchPos = bench?.Transform.World.Position ?? character.Transform.World.Position;
            character.Transform.Local.SetPosition(benchPos + new Vector3(2f, 0f, 0f));

            var craft = actor.Craft(options.PackCraftId, benchObjId, idempotencyKey: "m3a4-craft-pack");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("CRAFT", craft, $"craft {options.PackCraftId} @ bench {benchObjId}"));
            craft = pump.Drive(actor, craft, options.ActionPumpTimeout);
            if (craft.State != ActorLifecycleState.Completed)
                return Fail("CRAFT", craft, $"craft {options.PackCraftId}", rigNotes, stages, criteria, traceRecords);
            craftState.Crafted = true;

            // Documented craft labor: the recipe's skill ConsumeLaborPower
            // (the same value the craft gate pre-flights and EndCraft charges).
            var craftSkillId = CraftManager.Instance.GetCraftById(options.PackCraftId)?.SkillId ?? 0;
            craftState.CraftLaborCost = SkillLaborCost(craftSkillId);

            // The pack must sit in the Backpack equipment slot (EndCraft grants
            // trade packs there — the pack conservation starts at this instance).
            var packItem = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            var packGranted = packItem is { TemplateId: var t } && t == options.PackItemTemplateId;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-pack-granted",
                packGranted,
                packGranted ? $"pack {options.PackItemTemplateId} granted to Backpack slot (instance {packItem!.Id})"
                            : $"pack {options.PackItemTemplateId} NOT in Backpack slot (craft product mismatch)"));
            craftState.PackInstanceId = packItem?.Id ?? 0;

            // ------------------------------------------------ 4. PACK
            // 4a. Put down the carried pack (engine spawns the placed-pack
            // doodad 6068). The engine refuses placement inside public-farm
            // subzones and on house plots without permission — the craft
            // bench area in a town can sit in either (observed live:
            // "put-down did not take effect (engine refused placement)").
            // Stand at the nearest spot that clears BOTH gates (engine READ
            // APIs only — no state is mutated by the probe), facing +X so
            // the effect's 1m-in-front placement point is deterministic.
            var placementSpot = FindFreePlacementSpot(character, rigNotes);
            character.Transform.Local.SetPosition(placementSpot);
            character.Transform.Local.SetRotation(0f, 0f, 0f);

            var putDown = actor.PutDown(options.PackItemTemplateId, idempotencyKey: "m3a4-putdown");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("PUTDOWN", putDown, $"pack {options.PackItemTemplateId}"));
            if (putDown.State != ActorLifecycleState.Completed)
                return Fail("PUTDOWN", putDown, $"pack {options.PackItemTemplateId}", rigNotes, stages, criteria, traceRecords);

            var placedDoodad = FindPlacedPackDoodad(character, options.PackItemTemplateId, options.PlacedPackDoodadTemplateId);
            if (placedDoodad == null && pump.ProvisionPlacedPackDoodad(character) is { } provisionedObjId)
            {
                // Unit-world persistence boundary: the put-down landed at the
                // System-container move (the engine anti-dupe invariant) but
                // the doodad spawn tail is unreachable headless. The pump
                // materialized the placed doodad through the accepted pack-rig
                // fixture; the LIVE pump never provisions (real spawn via
                // MySQL). The pickup below still drives the REAL RecoverItem
                // engine path.
                placedDoodad = character.ParentWorld?.GetDoodad(provisionedObjId);
                rigNotes.Add($"placed-pack doodad provisioned by pump ({provisionedObjId}) — unit world cannot persist the put-down spawn tail");
            }
            var placed = placedDoodad != null;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-placed",
                placed,
                placed ? $"placed-pack doodad {placedDoodad!.ObjId} (template {placedDoodad.TemplateId}, item {placedDoodad.ItemId})"
                       : $"no placed-pack doodad for template {options.PackItemTemplateId} found"));

            // 4b. Pick the pack back up (RecoverItem — the exact CSLootOpenBagPacket pack path).
            if (placedDoodad == null)
                return Fail("PACKPICKUP", null, $"placed-pack doodad for template {options.PackItemTemplateId} not found",
                    rigNotes, stages, criteria, traceRecords);
            var pickUp = actor.PackPickup(placedDoodad.ObjId, idempotencyKey: "m3a4-packpickup");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("PACKPICKUP", pickUp, $"placed doodad {placedDoodad.ObjId}"));
            if (pickUp.State != ActorLifecycleState.Completed)
                return Fail("PACKPICKUP", pickUp, $"placed doodad {placedDoodad.ObjId}", rigNotes, stages, criteria, traceRecords);

            // ------------------------------------------------ 5. VEHICLE
            // 5a. Summon the farm wagon: the REAL path is UseItem on the
            // summon scroll (fixture worlds inject the vehicle through the
            // pump — they carry no real summon-scroll item data).
            Slave? wagon = null;
            var fixtureVehicle = pump.TrySummonVehicle(character);
            if (fixtureVehicle is { } fixtureObjId)
            {
                wagon = character.ParentWorld?.SlaveManager.GetSlaveByObjId(fixtureObjId);
                rigNotes.Add($"vehicle injected by pump (fixture slave {fixtureObjId}) — real summon scroll path not exercised in this world");
            }
            else
            {
                // GCD settle: the engine's 150ms skill GCD window rejects
                // back-to-back skill uses with SkillResult.CooldownTime (the
                // put-down / pickup skill uses land milliseconds before this
                // summon). A real client paces itself; the scripted actor
                // waits out the window explicitly.
                Thread.Sleep(300);

                var summon = actor.UseItem(options.FarmWagonSummonScrollItemId, idempotencyKey: "m3a4-summon-wagon");
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage("USE-SUMMON-SCROLL", summon, $"scroll {options.FarmWagonSummonScrollItemId}"));
                if (summon.State != ActorLifecycleState.Completed)
                    return Fail("USE-SUMMON-SCROLL", summon, "farm wagon summon", rigNotes, stages, criteria, traceRecords);

                wagon = ResolveOwnedSlave(character, out var wagonNote);
                rigNotes.Add(wagonNote);
            }

            if (wagon == null)
                return Fail("VEHICLE", null, "farm wagon did not materialize (no owned slave in world)",
                    rigNotes, stages, criteria, traceRecords);

            // 5b. Board the driver seat (SlaveManager.BindSlave).
            var board = actor.BoardVehicle(wagon.ObjId, AttachPointKind.Driver, idempotencyKey: "m3a4-board");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("BOARD-VEHICLE", board, $"slave {wagon.ObjId}"));
            if (board.State != ActorLifecycleState.Completed)
                return Fail("BOARD-VEHICLE", board, $"slave {wagon.ObjId}", rigNotes, stages, criteria, traceRecords);

            // 5c. Load the pack onto a cargo point (PackVehicleService). LIVE:
            // the carried-load path (pack in the Backpack slot — the real
            // doodad factory materializes the cargo pack via MySQL). UNIT
            // worlds: the carried path needs the placed-pack doodad template
            // the shared pack-rig surface deliberately leaves absent, so the
            // pump re-materializes the placed doodad (re-placing the pack
            // through the REAL PutDown contract action first — the unit
            // world's put-down boundary) and the PLACED-load path runs the
            // real PackVehicleService attach.
            var loadDoodadObjId = pump.ProvisionPlacedPackDoodad(character);
            var load = loadDoodadObjId is { } loadDoodadId
                ? actor.LoadPackOntoVehicle(wagon.ObjId, loadDoodadId, idempotencyKey: "m3a4-load-pack")
                : actor.LoadPackOntoVehicle(wagon.ObjId, placedPackDoodadObjId: null, idempotencyKey: "m3a4-load-pack");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("LOAD-PACK-ONTO-VEHICLE", load, $"slave {wagon.ObjId}"));
            if (load.State != ActorLifecycleState.Completed)
                return Fail("LOAD-PACK-ONTO-VEHICLE", load, $"slave {wagon.ObjId}", rigNotes, stages, criteria, traceRecords);

            // The pack instance is now on the wagon's cargo doodad.
            var cargoDoodad = FindLoadedPackDoodad(character, options.PackItemTemplateId, wagon.ObjId);
            craftState.OnVehicleCargo = cargoDoodad != null;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-loaded-on-vehicle",
                craftState.OnVehicleCargo,
                craftState.OnVehicleCargo ? $"pack instance {craftState.PackInstanceId} on cargo doodad {cargoDoodad!.ObjId} of slave {wagon.ObjId}"
                                          : $"pack instance {craftState.PackInstanceId} NOT found on slave {wagon.ObjId} cargo"));

            // 5d. Drive the loaded wagon a short leg (client-authored movement
            // model; Tick advances the leg — never a Transform assignment).
            var start = wagon.Transform.World.Position;
            var destination = start + new Vector3(options.DriveLegDistance, options.DriveLegDistance, 0f);
            var drive = actor.DriveVehicle(wagon.ObjId, destination, speed: 5f,
                timeout: options.ActionPumpTimeout, idempotencyKey: "m3a4-drive");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("DRIVE-VEHICLE", drive, $"slave {wagon.ObjId} → {destination}"));
            drive = pump.Drive(actor, drive, options.ActionPumpTimeout);
            if (drive.State != ActorLifecycleState.Completed)
                return Fail("DRIVE-VEHICLE", drive, $"slave {wagon.ObjId}", rigNotes, stages, criteria, traceRecords);

            var moved = Vector3.Distance(start, wagon.Transform.World.Position) > 1f;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("vehicle-moved",
                moved,
                moved ? $"wagon moved {Vector3.Distance(start, wagon.Transform.World.Position):F1}m (leg {options.DriveLegDistance}m)"
                      : "wagon did not move (movement model did not advance)"));

            // 5e. Unboard (also exercises the unboard action + leaves no residue).
            var unboard = actor.UnboardVehicle(wagon.ObjId, idempotencyKey: "m3a4-unboard");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("UNBOARD-VEHICLE", unboard, $"slave {wagon.ObjId}"));
            if (unboard.State != ActorLifecycleState.Completed)
                return Fail("UNBOARD-VEHICLE", unboard, $"slave {wagon.ObjId}", rigNotes, stages, criteria, traceRecords);

            // ------------------------------------------------ 6. BANK
            // The bank round trips run BEFORE the sell: the harvest yield
            // merges into ONE stack (max-stack potato), and the Sell action
            // moves the WHOLE stack to BuyBackItems — a sell-first order
            // would leave nothing in the bag to exercise the item deposit.
            // Order is otherwise irrelevant: the currency conservation math
            // accounts buys/sells/deposits/withdrawals in any order.
            var deposit = actor.DepositMoney(options.BankRoundTripAmount, idempotencyKey: "m3a4-deposit-money");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("DEPOSIT-MONEY", deposit, $"{options.BankRoundTripAmount}c"));
            if (deposit.State != ActorLifecycleState.Completed)
                return Fail("DEPOSIT-MONEY", deposit, "money deposit", rigNotes, stages, criteria, traceRecords);
            bankState.DepositAmount = options.BankRoundTripAmount;

            var withdraw = actor.WithdrawMoney(options.BankRoundTripAmount, idempotencyKey: "m3a4-withdraw-money");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("WITHDRAW-MONEY", withdraw, $"{options.BankRoundTripAmount}c"));
            if (withdraw.State != ActorLifecycleState.Completed)
                return Fail("WITHDRAW-MONEY", withdraw, "money withdrawal", rigNotes, stages, criteria, traceRecords);
            bankState.WithdrawAmount = options.BankRoundTripAmount;

            var depositItem = actor.DepositItem(options.MilletMaterialItemId, idempotencyKey: "m3a4-deposit-item");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("DEPOSIT-ITEM", depositItem, $"item {options.MilletMaterialItemId}"));
            if (depositItem.State != ActorLifecycleState.Completed)
                return Fail("DEPOSIT-ITEM", depositItem, "item deposit", rigNotes, stages, criteria, traceRecords);

            var withdrawItem = actor.WithdrawItem(options.MilletMaterialItemId, idempotencyKey: "m3a4-withdraw-item");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("WITHDRAW-ITEM", withdrawItem, $"item {options.MilletMaterialItemId}"));
            if (withdrawItem.State != ActorLifecycleState.Completed)
                return Fail("WITHDRAW-ITEM", withdrawItem, "item withdrawal", rigNotes, stages, criteria, traceRecords);

            // ------------------------------------------------ 7. TRADE
            // Sell a spare harvest yield back to the general merchant
            // (the sellable stack — the engine moves it to BuyBackItems
            // and pays the refund; refund = template.Refund × grade multiplier).
            var sellableItem = FindSellableYield(character, options.MilletMaterialItemId);
            if (sellableItem == null)
                return Fail("SELL", null, $"no sellable {options.MilletMaterialItemId} yield to sell",
                    rigNotes, stages, criteria, traceRecords);

            var sell = actor.Sell(generalMerchantObjId, sellableItem.Id, idempotencyKey: "m3a4-sell-yield");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("SELL", sell, $"item {sellableItem.Id} (template {options.MilletMaterialItemId})"));
            if (sell.State != ActorLifecycleState.Completed)
                return Fail("SELL", sell, $"item {sellableItem.Id}", rigNotes, stages, criteria, traceRecords);
            tradeState.AddSell(sellableItem.TemplateId, sellableItem.Count, ReadLongResult(sell.Result));

            // ---------------------------------------------- 8. CONSERVE
            // Pack instance accounting across every state the pack can occupy.
            var packInstanceOk = AssertPackInstanceConserved(character, craftState, options, out var packDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-conserved", packInstanceOk, packDetail));

            // Seeded item templates: Σ(consumed-as-documented + held) == Σ(seeded).
            var seedConservation = AssertSeedConservation(character, farm, options, out var seedDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("seed-conservation", seedConservation, seedDetail));

            // Currency: money_end == seed − buys + sells (bank round trip nets zero).
            var currencyOk = AssertCurrencyConservation(character, tradeState, bankState, options, out var currencyDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("currency-conservation", currencyOk, currencyDetail));

            // Labor: consumed == Σ documented costs (± tolerance).
            var laborOk = AssertLaborConservation(character, farm, craftState, options, out var laborDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("labor-conservation", laborOk, laborDetail));

            // Lifecycle: full transition sets on every Completed record.
            var lifecycleOk = AssertTraceCompleteness(traceRecords, out var lifecycleDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("lifecycle-trace-complete", lifecycleOk, lifecycleDetail));

            var passed = criteria.All(c => c.Passed);
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
            Logger.Error(ex, "m3a-m4 economic replay crashed");
            return Fail("RUN", null, $"crash: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", rigNotes, stages, criteria, traceRecords);
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Drives an async reposition leg (MoveToUnit / Move) to a TERMINAL
    /// state through the pump, then appends its audit record.
    ///
    /// Audit records append only at terminal states (the actor's Finish
    /// path): on a WARM world the merchant resolve keeps the request
    /// Running past creation, so reading <c>AuditTrace.Last()</c> before
    /// the drive throws "Sequence contains no elements" (empty trace) or
    /// captures the previous action's record. Returns false when the leg
    /// exhausts its budget WITHOUT a terminal transition; the caller then
    /// fails closed with the §17 navigation reason (never an
    /// InvalidOperationException).
    /// </summary>
    private static bool TryDriveLegToTerminal(GameplayActor actor, IScenarioPump pump,
        ActorRequest request, TimeSpan maxWait,
        List<ActorAuditRecord> traceRecords, out ActorRequest driven)
    {
        driven = pump.Drive(actor, request, maxWait);
        var traceId = driven.TraceId;
        var record = driven.IsTerminal
            ? actor.AuditTrace.LastOrDefault(r => r.TraceId == traceId)
            : null;
        if (record == null)
            return false;
        traceRecords.Add(record);
        return true;
    }

    /// <summary>
    /// Harvestability probe (mirror of the actor's data-driven resolution):
    /// the crop's CURRENT phase carries a DoodadFuncUse whose skill advances
    /// into a loot phase. Used by the pumps to wait for maturity and by the
    /// labor-conservation read (the harvest skill's own ConsumeLaborPower).
    /// </summary>
    public static bool IsHarvestable(Doodad doodad)
        => TryGetHarvestSkill(doodad, out _);

    public static bool TryGetHarvestSkill(Doodad doodad, out uint harvestSkillId)
    {
        harvestSkillId = 0;
        if (doodad.ParentWorld == null)
            return false;
        var funcs = DoodadManager.Instance.GetFuncsForGroup(doodad.FuncGroupId);
        if (funcs == null)
            return false;
        foreach (var func in funcs)
        {
            if (func.FuncType != "DoodadFuncUse" || func.SkillId == 0)
                continue;
            if (func.NextPhase <= 0)
                continue;
            var nextFuncs = DoodadManager.Instance.GetFuncsForGroup((uint)func.NextPhase);
            if (nextFuncs != null && nextFuncs.Any(f => LootFuncTypes.Contains(f.FuncType)))
            {
                harvestSkillId = func.SkillId;
                return true;
            }
        }

        return false;
    }

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

    /// <summary>Result payloads are typed by the action: Buy returns long,
    /// Sell/DepositItem/WithdrawItem return int — normalize to long.</summary>
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

    /// <summary>Nearest doodad to the actor (the craft bench target for ReqDoodadId=0 recipes).</summary>
    private static uint ResolveNearestDoodad(Character character, out float distance)
    {
        distance = float.MaxValue;
        var world = character.ParentWorld;
        if (world == null)
            return 0;

        uint nearest = 0;
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

    /// <summary>
    /// Nearest spot to the actor whose stand point AND the pack put-down
    /// point 1m in front of it clear the engine's placement gates:
    /// public-farm subzone exclusion (PublicFarmManager.InPublicFarm) and
    /// house-plot permission (HousingManager.GetHouseAtLocation — a fresh
    /// rig owns no houses). Scans a deterministic spiral of candidate
    /// positions (rings out to 50 m, 8 compass points per ring). Rejection
    /// telemetry is appended to the rig notes so a live failure names the
    /// gate (farm vs house) instead of a bare engine refusal. Unit worlds
    /// have no subzone/housing managers — the probe falls back to the
    /// actor's current position there.
    /// </summary>
    private static Vector3 FindFreePlacementSpot(Character character, List<string> rigNotes)
    {
        var world = character.ParentWorld;
        var origin = character.Transform.World.Position;
        if (world == null)
            return origin;

        var farmRejects = 0;
        var houseRejects = 0;
        try
        {
            foreach (var radius in new[] { 0f, 3f, 6f, 9f, 12f, 15f, 20f, 25f, 30f, 40f, 50f })
            {
                foreach (var (dx, dy) in new[]
                         {
                             (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f),
                             (0.707f, 0.707f), (0.707f, -0.707f), (-0.707f, 0.707f), (-0.707f, -0.707f)
                         })
                {
                    var stand = origin + new Vector3(dx * radius, dy * radius, 0f);
                    var ahead = stand + new Vector3(1f, 0f, 0f); // yaw 0 → +X
                    if (PublicFarmManager.Instance.InPublicFarm(world.Template, stand) ||
                        PublicFarmManager.Instance.InPublicFarm(world.Template, ahead))
                    {
                        farmRejects++;
                        continue;
                    }
                    if (HousingManager.Instance.GetHouseAtLocation(stand.X, stand.Y) != null ||
                        HousingManager.Instance.GetHouseAtLocation(ahead.X, ahead.Y) != null)
                    {
                        houseRejects++;
                        continue;
                    }
                    rigNotes.Add($"placement spot {stand} (rejects: {farmRejects} farm, {houseRejects} house)");
                    return stand;
                }
            }
        }
        catch
        {
            // Subzone/housing managers unavailable (unit worlds) — fall back
            // to the current position; the engine gates behave as before.
        }

        rigNotes.Add($"placement fallback to actor position {origin} (rejects: {farmRejects} farm, {houseRejects} house)");
        return origin;
    }

    private static Doodad? FindPlacedPackDoodad(Character character, uint packItemTemplateId, uint placedDoodadTemplateId)
        => character.ParentWorld?.GetAllDoodads()
            .FirstOrDefault(d => d.ItemTemplateId == packItemTemplateId && d.TemplateId == placedDoodadTemplateId);

    private static Doodad? FindLoadedPackDoodad(Character character, uint packItemTemplateId, uint slaveObjId)
        => character.ParentWorld?.GetAllDoodads()
            .FirstOrDefault(d => d.ItemTemplateId == packItemTemplateId
                                 && d.ParentObjId == slaveObjId
                                 && d.AttachPoint != AttachPointKind.None);

    /// <summary>The actor's own active slave (the summoned farm wagon).</summary>
    private static Slave? ResolveOwnedSlave(Character character, out string note)
    {
        var world = character.ParentWorld;
        if (world == null)
        {
            note = "no world — cannot resolve owned slave";
            return null;
        }

        var slave = world.SlaveManager.GetActiveSlaveByOwnerObjId(character.ObjId);
        if (slave != null)
        {
            note = $"resolved owned slave {slave.ObjId} (template {slave.TemplateId})";
            return slave;
        }

        note = "no active slave with owner == actor in the world";
        return null;
    }

    private static Item? FindSellableYield(Character character, uint templateId)
        => character.Inventory?.Bag.Items.FirstOrDefault(i => i.TemplateId == templateId && (i.Template?.Sellable ?? false));

    // ------------------------------------------------------------- asserts

    /// <summary>
    /// Pack instance conservation: the crafted pack instance must be reachable
    /// in EXACTLY ONE logical location — either carried (Backpack slot / bag)
    /// or in the world (the System-container item + its doodad link are the
    /// SAME instance: the engine moves the item into the System container and
    /// the placed/loaded doodad references it — one "world pack", never a
    /// duplicate) — at every stage of the route. No loss, no duplication.
    /// </summary>
    private static bool AssertPackInstanceConserved(Character character, CraftState state, ReplayOptions options, out string detail)
    {
        if (!state.Crafted || state.PackInstanceId == 0)
        {
            detail = $"pack not crafted (crafted={state.Crafted})";
            return false;
        }

        var instanceId = state.PackInstanceId;

        // Carried: the Backpack equipment slot or any non-System container.
        var carried = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == instanceId;
        var inBag = false;
        foreach (var container in character.Inventory._itemContainers.Values)
        {
            if (container.ContainerType == SlotType.System)
                continue;
            if (container.GetItemsSnapshot().Any(i => i.Id == instanceId))
                inBag = true;
        }

        // World pack: the System-container item and/or a doodad item link
        // (placed pack or wagon cargo) — one logical location.
        var inSystem = character.Inventory.SystemContainer.GetItemsSnapshot().Any(i => i.Id == instanceId);
        var doodadLinked = (character.ParentWorld?.GetAllDoodads() ?? [])
            .Any(d => d.ItemId == instanceId);

        var holderCount = (carried || inBag ? 1 : 0) + (inSystem || doodadLinked ? 1 : 0);
        detail = $"pack instance {instanceId} accounted in: " +
                 $"{(carried || inBag ? "carried (Backpack slot/bag) " : "")}" +
                 $"{(inSystem || doodadLinked ? $"world (System container: {inSystem}, doodad links: {doodadLinked})" : "")}" +
                 (holderCount == 1 ? "" : " — DUPLICATED OR LOST");
        return holderCount == 1;
    }

    /// <summary>
    /// Seed conservation: every seeded/bought seed instance is consumed by
    /// exactly ONE planted crop (the engine consumes one seed per
    /// CreatePlayerDoodad), every crop is harvested, and the bag's seed
    /// count obeys the conservation law:
    ///
    ///     held == seeded + Σ(document harvest seed grants) − consumed
    ///
    /// The harvest grant is data-driven (the crop's loot-linked phase's
    /// DoodadFuncLootPack rows granting the seed item — canonical potato
    /// pack 6452 returns 1 seed per harvest), so a missing or duplicated
    /// seed is caught exactly: no phantom crops, no lost seeds.
    /// </summary>
    private static bool AssertSeedConservation(Character character, FarmState farm, ReplayOptions options, out string detail)
    {
        // Distinct seed templates (the rig may alias millet/poppy to one item).
        var seedItems = new HashSet<uint> { options.MilletSeedItemId, options.PoppySeedItemId };
        var seededTotal = options.MilletSeedCount + options.PoppySeedCount;
        var held = seedItems.Sum(id => character.Inventory.GetItemsCount(id));
        var consumed = farm.Planted.Count;
        var expected = seededTotal + farm.HarvestSeedGrants - consumed;

        var ok = held == expected && consumed == seededTotal && farm.Harvested == farm.Planted.Count;

        detail = $"seeded {seededTotal}, consumed {consumed}, planted {farm.Planted.Count}, harvested {farm.Harvested}, " +
                 $"harvest seed grants {farm.HarvestSeedGrants}, held {held} == expected {expected}";
        return ok;
    }

    /// <summary>
    /// The crop's DOCUMENTED seed grant: the seed item rows in the loot
    /// pack its harvest chain leads into (DoodadFuncUse → loot-linked phase
    /// → DoodadFuncLootPack rows). MinAmount is the guaranteed grant; the
    /// canonical potato pack grants exactly 1 seed per harvest.
    /// </summary>
    private static int SeedGrantForCrop(Doodad crop, ReplayOptions options)
    {
        var seedItems = new HashSet<uint> { options.MilletSeedItemId, options.PoppySeedItemId };
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
                    .Where(l => seedItems.Contains(l.ItemId))
                    .Sum(l => Math.Max(0, l.MinAmount));
            }
        }

        return grants;
    }

    /// <summary>
    /// Currency conservation: money_end == seed − Σ(buy costs) + Σ(sell
    /// refunds) − deposit + withdraw (the bank round trip nets zero; the buy
    /// costs / sell refunds are the values the engine charged/paid, returned
    /// in the action results).
    /// </summary>
    private static bool AssertCurrencyConservation(Character character, TradeState trade, BankState bank, ReplayOptions options, out string detail)
    {
        var expected = options.SeedMoney - trade.BuyTotal + trade.SellTotal - bank.DepositAmount + bank.WithdrawAmount;
        var actual = character.Money;

        detail = $"money {actual} == seed {options.SeedMoney} − buys {trade.BuyTotal} + sells {trade.SellTotal} " +
                 $"(bank round trip {bank.DepositAmount}/{bank.WithdrawAmount} nets 0) — expected {expected}";
        return actual == expected;
    }

    /// <summary>
    /// Labor conservation: labor consumed == Σ documented costs (per-seed
    /// plant skill labor + per-crop harvest skill labor + the pack craft
    /// labor), within the options tolerance (live labor regen).
    /// </summary>
    private static bool AssertLaborConservation(Character character, FarmState farm, CraftState craft, ReplayOptions options, out string detail)
    {
        var consumed = DefaultLaborPool - character.LaborPower;
        var documented = farm.DocumentedLaborCost + craft.CraftLaborCost;
        var delta = Math.Abs(consumed - documented);

        detail = $"labor consumed {consumed} == documented {documented} (±{options.LaborTolerance}) — " +
                 $"plants {farm.Planted.Count} x {farm.PlantLaborCost} + harvests {farm.Harvested} x {farm.HarvestLaborCost} + craft {craft.CraftLaborCost}";
        return delta <= options.LaborTolerance;
    }

    /// <summary>
    /// Lifecycle correctness: every Completed action's trace record carries
    /// the full Requested → Accepted → Running → Completed transition set;
    /// no Rejected record carries Running (a refusal is not an execution).
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
        return completed.Count >= 12 && incomplete.Count == 0 && rejectedRunning.Count == 0;
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
        List<ActorAuditRecord> traceRecords)
    {
        var reason = request?.Failure ?? ActorFailureReason.RejectedAction;
        var detail = request?.Detail ?? "";
        Logger.Warn("m3a-m4 replay FAIL at {Stage}: {What} ({Reason}) {Detail}", stage, what, reason, detail);
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

    // ------------------------------------------------------------- state

    private sealed class FarmState
    {
        public int MilletSeedsSeeded { get; set; }
        public int PoppySeedsSeeded { get; set; }
        public List<uint> Planted { get; } = [];
        public int Harvested { get; set; }
        public int PlantLaborCost { get; set; }
        public int HarvestLaborCost { get; set; }
        public int HarvestSeedGrants { get; set; }
        public int DocumentedLaborCost => Planted.Count * PlantLaborCost + Harvested * HarvestLaborCost;
    }

    private sealed class CraftState
    {
        public bool Crafted { get; set; }
        public ulong PackInstanceId { get; set; }
        public bool OnVehicleCargo { get; set; }
        public int CraftLaborCost { get; set; }
    }

    private sealed class TradeState
    {
        public long BuyTotal { get; private set; }
        public long SellTotal { get; private set; }
        public int Buys { get; private set; }
        public int Sells { get; private set; }

        public void AddBuy(uint itemTemplateId, int count, long cost) { BuyTotal += cost; Buys++; }

        public void AddSell(uint itemTemplateId, int count, long refund) { SellTotal += refund; Sells++; }
    }

    private sealed class BankState
    {
        public long DepositAmount { get; set; }
        public long WithdrawAmount { get; set; }
    }
}

/// <summary>
/// LIVE world pump (bridge dispatch path): in-flight requests advance on the
/// game loop — the pump only ticks the actor's own movement/drive legs and
/// waits on real time for the engine's scheduled tasks (craft queue drain,
/// crop growth timers). The E2E stack boosts World.GrowthRate so a full crop
/// cycle completes within the maturity timeout; production rates would take
/// hours and are out of scope for a scripted replay.
/// </summary>
public sealed class LiveReplayPump : M3aM4ReplayScenario.IScenarioPump
{
    /// <summary>Pump cadence for movement/drive legs and craft-queue waits.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    public ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan maxWait)
    {
        var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
        while (!request.IsTerminal && Environment.TickCount64 < deadline)
        {
            actor.Tick(TickInterval);
            Thread.Sleep(TickInterval);
        }

        return request;
    }

    public bool WaitForCropMaturity(Character character, uint cropObjId, TimeSpan maxWait)
    {
        var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var crop = character.ParentWorld?.GetDoodad(cropObjId);
            if (crop == null)
                return false; // crop vanished — the harvest stage will surface it
            if (M3aM4ReplayScenario.IsHarvestable(crop))
                return true;
            Thread.Sleep(250);
        }

        return false;
    }

    public uint? TrySummonVehicle(Character character) => null; // live: real UseItem summon path

    public uint? ProvisionCropAtBoundary(Character character, Vector3 position) => null; // live: plants Complete

    public uint? ProvisionPlacedPackDoodad(Character character) => null; // live: put-down spawns it via MySQL
}
