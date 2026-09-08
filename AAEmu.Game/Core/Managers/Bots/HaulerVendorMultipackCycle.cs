using System.Linq;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

using NLog;
namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pump that drives in-flight craft and drive requests to a terminal state.
/// The composer owns the leg order and the conservation assertions; the pump
/// owns the tick strategy (headless rigs tick synchronously, live bridges
/// observe world ticks). Combines the slice-1 <see cref="IHaulCraftPump"/>
/// and slice-2 <see cref="IHaulDrivePump"/> shapes.
/// </summary>
public interface IHaulVendorPump
{
    ActorRequest DriveCraft(GameplayActor actor, ActorRequest request, uint benchObjId, uint skillId, TimeSpan maxWait);
    ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan pollBudget);
}

/// <summary>
/// M8 C4 hauler/trader v1 slice-4 — inventory-full vendoring trip + multi-pack
/// cycle composer: audit a near-full bag, vendor ONLY
/// <see cref="BotBagManager.IsTrash"/>-classified junk at the home merchant
/// through the real <see cref="GameplayActor.Sell"/> path (quest items,
/// essential consumables, unsellables, and craft materials are never sold —
/// the never-delete invariant), board the cargo vehicle, craft TWO trade
/// packs through the real <see cref="GameplayActor.Craft"/> path, load both
/// onto cargo points through the real
/// <see cref="GameplayActor.LoadPackOntoVehicle"/> path, drive both to the
/// gold trader through the real <see cref="GameplayActor.DriveVehicle"/>
/// path, unload and sell each pack through the real
/// <see cref="GameplayActor.PackPickup"/> / <see cref="GameplayActor.SellSpecialty"/>
/// paths with per-pack payout proof, bank the combined proceeds through the
/// real <see cref="GameplayActor.DepositMoney"/> path, and drive the empty
/// wagon home.
///
/// Composition only: every leg calls the EXISTING <see cref="GameplayActor"/>
/// actions unchanged, driven by an ordinary <see cref="Character"/> through
/// normal gameplay services (AGENTS.md #9). No new engine path, no parallel
/// pack/vehicle/economy implementation. Slices 1–3 stay standalone: this
/// cycle re-drives their ACTION paths (not the slice composers) because the
/// multi-pack interleaving (craft→load→craft→load, unload→sell→unload→sell —
/// the Backpack slot holds one pack) has no single-pack equivalent.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Restart legs are later slices. Reporting is a canned record (LLM LAST):
/// fixed fields only. The audit trail is the legs' own
/// <see cref="ActorAuditRecord"/> entries plus the returned result.
///
/// Trip note (honest): the vendor merchant lives at the home base (the farm
/// vendor — the M3aM4 rig convention), so the vendoring trip is an errand
/// leg (audit→sell→verify), not a drive: slice-2 already pins driving, and
/// this cycle's two wagon drives (laden outbound, empty return) prove
/// movement here.
///
/// Proceeds note (honest — the slice-3 canonical 1.2 mail delay): each
/// specialty payout travels as reward MAIL (in transit ~22 h, never
/// inventory money), and no TakeMail actor action exists yet, so the DEPOSIT
/// leg banks the combined proceeds FIGURE from operating cash (bank delta ==
/// combined payout, inventory delta == figure − vendor revenue banked).
/// Mail-take → deposit of the very same copper is a later slice. The ledger
/// law pins the no-dup guarantee instead: mailΔ + bankΔ + (moneyΔ −
/// vendorRevenue) == combined payout.
/// </summary>
public static class HaulerVendorMultipackCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-hauler-vendor-multipack";

    /// <summary>Sale labor per pack — SellSpecialty's ChangeLabor(-60, Commerce).</summary>
    public const int SellLaborCostPerPack = 60;

    /// <summary>Scenario parameters.</summary>
    public sealed record HaulerVendorMultipackOptions
    {
        /// <summary>Idempotency namespace for this cycle's legs.</summary>
        public string CycleId { get; init; } = "m8c4-v0";

        /// <summary>Cargo vehicle slave ObjId (boarded during the cycle).</summary>
        public required uint SlaveObjId { get; init; }

        /// <summary>General merchant NPC ObjId for the vendoring trip.</summary>
        public required uint VendorMerchantObjId { get; init; }

        /// <summary>Specialty gold trader NPC ObjId (packs sold within 2.5 m).</summary>
        public required uint GoldTraderObjId { get; init; }

        /// <summary>Pack recipe id (materials → trade pack, rate 100).</summary>
        public required uint PackCraftId { get; init; }

        /// <summary>Pack recipe material item id.</summary>
        public required uint PackMaterialItemId { get; init; }

        /// <summary>Pack recipe material amount per pack.</summary>
        public int PackMaterialAmount { get; init; } = 1;

        /// <summary>Number of packs for the multi-pack cycle (v1: exactly 2).</summary>
        public int PackCount { get; init; } = 2;

        /// <summary>Craft bench doodad ObjId (recipe ReqDoodad match).</summary>
        public required uint BenchObjId { get; init; }

        /// <summary>Route destination for the laden drive (gold trader).</summary>
        public required Vector3 TraderDestination { get; init; }

        /// <summary>Home destination for the empty return drive.</summary>
        public required Vector3 Home { get; init; }

        /// <summary>Drive speed in units/second.</summary>
        public float Speed { get; init; } = 10f;

        /// <summary>Budget for driving each in-flight craft to terminal.</summary>
        public TimeSpan CraftTimeout { get; init; } = TimeSpan.FromSeconds(10);

        /// <summary>Navigation budget per drive leg (expiry → TimedOut(Navigation)).</summary>
        public TimeSpan DriveTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Wall-clock budget for driving each in-flight drive to terminal.</summary>
        public TimeSpan PumpBudget { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class HaulerVendorMultipackResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public List<ulong> PackItemIds { get; init; } = [];
        public int VendorSold { get; init; }
        public long VendorRevenue { get; init; }
        public List<int> BasePrices { get; init; } = [];
        public List<long> Payouts { get; init; } = [];
        public long BankDeposited { get; init; }
        public int LaborCharged { get; init; }
        public Vector3 ArrivalPosition { get; init; }
        public float DistanceRemaining { get; init; }
    }

    /// <summary>
    /// Runs one vendor→2-pack cycle: precheck the composition state (known
    /// vehicle, vendor merchant, gold trader, recipe, bench, 2× materials,
    /// empty slot, empty cargo, near-full bag with trash), sell the trash,
    /// board, craft→load twice, drive laden to the trader, unload→sell
    /// twice with per-pack proof, bank the combined proceeds, drive home
    /// empty. Fail-closed at every leg: a rejection stops the cycle, records
    /// the reason, and never deletes, duplicates, or moves anything it
    /// should not.
    /// </summary>
    public static HaulerVendorMultipackResult Run(GameplayActor actor, HaulerVendorMultipackOptions options, IHaulVendorPump pump)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        HaulerVendorMultipackResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason,
            List<ulong>? packItemIds = null, int vendorSold = 0, long vendorRevenue = 0,
            List<int>? basePrices = null, List<long>? payouts = null, long bankDeposited = 0, int laborCharged = 0,
            Vector3 arrivalPosition = default, float distanceRemaining = 0f)
        {
            return new HaulerVendorMultipackResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                PackItemIds = packItemIds ?? [],
                VendorSold = vendorSold,
                VendorRevenue = vendorRevenue,
                BasePrices = basePrices ?? [],
                Payouts = payouts ?? [],
                BankDeposited = bankDeposited,
                LaborCharged = laborCharged,
                ArrivalPosition = arrivalPosition,
                DistanceRemaining = distanceRemaining
            };
        }

        // Audit records land on terminal transitions only — capture by
        // count-mark so nothing is missed and nothing is duplicated.
        void CaptureTrace(int mark)
        {
            foreach (var record in actor.AuditTrace.Skip(mark))
                traceRecords.Add(record);
        }

        if (options.PackCount != 2)
        {
            const string badCount = "v1 supports exactly 2 packs";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, badCount));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", badCount));
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, badCount);
        }

        var character = actor.Character;
        var inventory = character.Inventory;
        if (inventory == null)
        {
            const string noInventory = "character has no inventory";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, noInventory));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", noInventory));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, noInventory);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, noInventory);
        }

        // ---- PRECHECK: known vehicle, vendor merchant, gold trader, recipe,
        // bench, 2× materials, empty slot, empty cargo, near-full bag ----
        var vehicle = character.ParentWorld?.GetBaseUnit(options.SlaveObjId) as Slave;
        if (vehicle == null)
        {
            var reason = $"vehicle {options.SlaveObjId} not found in world";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var vendor = character.ParentWorld?.GetNpc(options.VendorMerchantObjId);
        if (vendor?.Template == null || !vendor.Template.Merchant)
        {
            var reason = $"vendor merchant {options.VendorMerchantObjId} not found or not a merchant";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var trader = character.ParentWorld?.GetNpc(options.GoldTraderObjId);
        if (trader?.Template == null)
        {
            var reason = $"gold trader {options.GoldTraderObjId} not found or has no template";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var craft = CraftManager.Instance.GetCraftById(options.PackCraftId);
        if (craft == null)
        {
            var reason = $"unknown pack craft {options.PackCraftId}";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        if (character.ParentWorld?.GetDoodad(options.BenchObjId) == null)
        {
            var reason = $"craft bench {options.BenchObjId} not found in world";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var materialsNeeded = options.PackMaterialAmount * options.PackCount;
        var materialsBefore = inventory.GetItemsCount(SlotType.Inventory, options.PackMaterialItemId);
        if (materialsBefore < materialsNeeded)
        {
            var reason = $"need {materialsNeeded}× material {options.PackMaterialItemId} for {options.PackCount} packs (have {materialsBefore})";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        if (inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) != null)
        {
            var reason = "backpack slot occupied — refusing to craft over a carried pack";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        if (vehicle.AttachedDoodads.Any(d => d.ItemId > 0))
        {
            var reason = $"vehicle {options.SlaveObjId} cargo not empty — refusing to load over incumbents";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        // The inventory-full trigger: the bag must be near-full with
        // classified trash, or the vendoring trip has no cause.
        var openingAudit = BotBagManager.AuditBag(character);
        if (!openingAudit.IsNearFull || openingAudit.TrashItems.Count == 0)
        {
            var reason = $"bag not near-full with trash (free {openingAudit.FreeSlots}, trash {openingAudit.TrashItems.Count}) — no vendoring trip needed";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", true,
            $"vehicle {options.SlaveObjId}, vendor {options.VendorMerchantObjId}, trader {options.GoldTraderObjId}, " +
            $"craft {options.PackCraftId} ×{options.PackCount}, bag near-full (free {openingAudit.FreeSlots}, trash {openingAudit.TrashItems.Count})"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"craft {options.PackCraftId} ×{options.PackCount}"));

        var moneyBefore = character.Money;
        var bankBefore = character.Money2;
        var laborBefore = character.LaborPower;
        var mailsBefore = SpecialtyMailCopper(character.Id);
        var freeSlotsBefore = openingAudit.FreeSlots;

        // Snapshot every non-trash bag row: the never-delete invariant pins
        // each of them after the vendor leg (materials are asserted again
        // after the crafts — consumed exactly, never lost).
        var trashIds = openingAudit.TrashItems.Select(t => t.Id).ToHashSet();
        var keptBefore = inventory.Bag.GetItemsSnapshot()
            .Where(i => i != null && !trashIds.Contains(i.Id))
            .ToDictionary(i => i.Id, i => (i.TemplateId, i.Count));
        var expectedRevenue = openingAudit.TrashItems.Sum(t => (long)t.Template.Refund * t.Count);

        // ---- VENDOR: sell ONLY IsTrash-classified rows at the merchant.
        // Fail-closed: a refused row stops the trip; unsold trash stays in
        // the bag and every kept row must be untouched (never deleted).
        var vendorSold = 0;
        var vendorRevenue = 0L;
        var vendorOk = true;
        string vendorFailReason = "";
        ActorFailureReason? vendorFailure = null;
        var trashSnapshot = openingAudit.TrashItems.ToList();
        for (var vi = 0; vi < trashSnapshot.Count; vi++)
        {
            var trash = trashSnapshot[vi];
            // Re-resolve: a duplicate-key rerun must refuse, never re-sell.
            if (inventory.Bag.GetItemByItemId(trash.Id) == null)
            {
                vendorOk = false;
                vendorFailReason = $"trash item {trash.Id} already gone — refusing to re-sell";
                vendorFailure = ActorFailureReason.StateTransition;
                break;
            }
            var sellMark = actor.AuditTrace.Count;
            var sell = actor.Sell(options.VendorMerchantObjId, trash.Id, idempotencyKey: $"{options.CycleId}-vendor-{vi}");
            CaptureTrace(sellMark);
            stages.Add(Stage($"VENDOR-{vi + 1}", sell, $"trash {trash.TemplateId} (instance {trash.Id})"));
            if (sell.State != ActorLifecycleState.Completed)
            {
                vendorOk = false;
                vendorFailReason = sell.Detail ?? "";
                vendorFailure = sell.Failure;
                break;
            }
            vendorSold++;
            vendorRevenue += sell.Result is int revenue ? revenue : 0;
        }

        // The never-delete invariant: every kept row present with its count,
        // whether the trip completed or held.
        var keptIntact = keptBefore.All(kv =>
            inventory.Bag.GetItemByItemId(kv.Key) is { TemplateId: var tpl, Count: var count }
            && tpl == kv.Value.TemplateId && count == kv.Value.Count);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("vendor-never-delete", keptIntact,
            keptIntact
                ? $"{keptBefore.Count} non-trash row(s) intact (quest/essential/unsellable/materials never sold)"
                : "a non-trash bag row was sold, lost, or changed by the vendor leg"));
        if (!keptIntact)
            return Finish(false, "VENDOR", ActorFailureReason.StateTransition,
                "vendor leg touched a non-trash row", [], vendorSold, vendorRevenue);

        if (!vendorOk)
        {
            var unsoldIntact = trashSnapshot.Skip(vendorSold)
                .All(t => inventory.Bag.GetItemByItemId(t.Id) != null);
            var reason = vendorFailReason;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("vendor-completed", false, reason));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("vendor-hold-unsold-retained", unsoldIntact,
                unsoldIntact
                    ? $"trip held ({reason}); {trashSnapshot.Count - vendorSold} unsold trash row(s) retained"
                    : $"trip held ({reason}) AND sold-state trash left the bag"));
            Logger.Warn("[{Scenario}] {Cycle}: VENDOR {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "VENDOR", vendorFailure, reason, [], vendorSold, vendorRevenue);
        }

        var revenueOk = vendorSold == trashSnapshot.Count && vendorRevenue == expectedRevenue;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("vendor-completed", true,
            $"sold {vendorSold} trash row(s) for {vendorRevenue}c"));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("vendor-revenue", revenueOk,
            revenueOk
                ? $"revenue {vendorRevenue}c == Σ refund × count ({expectedRevenue}c)"
                : $"revenue MISMATCH: {vendorRevenue}c vs expected {expectedRevenue}c ({vendorSold}/{trashSnapshot.Count} sold)"));
        if (!revenueOk)
            return Finish(false, "VENDOR", ActorFailureReason.StateTransition,
                "vendor revenue violated", [], vendorSold, vendorRevenue);

        var freeSlotsAfterVendor = inventory.Bag.FreeSlotCount;
        var freedOk = freeSlotsAfterVendor - freeSlotsBefore >= vendorSold;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("vendor-freed-slots", freedOk,
            $"free slots {freeSlotsBefore} → {freeSlotsAfterVendor} (freed ≥ {vendorSold} sold stacks)"));
        if (!freedOk)
            return Finish(false, "VENDOR", ActorFailureReason.StateTransition,
                "vendor leg did not free the sold stacks", [], vendorSold, vendorRevenue);

        // ---- BOARD: the existing action (real SlaveManager.BindSlave) ----
        var boardMark = actor.AuditTrace.Count;
        var board = actor.BoardVehicle(options.SlaveObjId, AttachPointKind.Driver, idempotencyKey: $"{options.CycleId}-board");
        CaptureTrace(boardMark);
        stages.Add(Stage("BOARD", board, $"slave {options.SlaveObjId}"));
        if (board.State != ActorLifecycleState.Completed)
        {
            var reason = board.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("board-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: BOARD {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, board.State, board.Failure, reason);
            return Finish(false, "BOARD", board.Failure, reason, [], vendorSold, vendorRevenue);
        }
        criteria.Add(new BotScenarioRunner.CriterionVerdict("board-completed", true,
            $"boarded slave {options.SlaveObjId} at the driver seat"));

        // ---- CRAFT×2 → LOAD×2: interleaved (the slot holds one pack, so
        // each pack loads before the next crafts). Per-pack proof each leg.
        var packItemIds = new List<ulong>();
        var skillTemplate = SkillManager.Instance.GetSkillTemplate(craft.SkillId);
        var expectedCraftLabor = skillTemplate != null ? new Skill(skillTemplate).GetLaborCost(character) : 0;
        for (var pi = 0; pi < options.PackCount; pi++)
        {
            var craftMark = actor.AuditTrace.Count;
            var craftRequest = actor.Craft(options.PackCraftId, options.BenchObjId, idempotencyKey: $"{options.CycleId}-craft-{pi + 1}");
            CaptureTrace(craftMark);
            stages.Add(Stage($"PACK-CRAFT-{pi + 1}", craftRequest, $"craft {options.PackCraftId}"));
            if (craftRequest.State != ActorLifecycleState.Running)
            {
                var reason = craftRequest.Detail ?? "";
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"pack-craft-{pi + 1}-completed", false, reason));
                Logger.Warn("[{Scenario}] {Cycle}: PACK-CRAFT-{Pack} {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, pi + 1, craftRequest.State, craftRequest.Failure, reason);
                return Finish(false, $"PACK-CRAFT-{pi + 1}", craftRequest.Failure, reason,
                    packItemIds, vendorSold, vendorRevenue);
            }
            var crafted = pump.DriveCraft(actor, craftRequest, options.BenchObjId, craft.SkillId, options.CraftTimeout);
            CaptureTrace(craftMark);
            if (crafted.State != ActorLifecycleState.Completed)
            {
                var reason = crafted.Detail ?? "";
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"pack-craft-{pi + 1}-completed", false, reason));
                Logger.Warn("[{Scenario}] {Cycle}: PACK-CRAFT-{Pack} {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, pi + 1, crafted.State, crafted.Failure, reason);
                return Finish(false, $"PACK-CRAFT-{pi + 1}", crafted.Failure, reason,
                    packItemIds, vendorSold, vendorRevenue);
            }
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"pack-craft-{pi + 1}-completed", true,
                $"crafted pack {pi + 1}/{options.PackCount} ({crafted.Detail})"));

            var pack = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            if (pack == null)
                return Finish(false, $"PACK-CRAFT-{pi + 1}", ActorFailureReason.StateTransition,
                    $"pack {pi + 1} completed without granting the backpack slot",
                    packItemIds, vendorSold, vendorRevenue);
            var packId = pack.Id;
            packItemIds.Add(packId);

            var loadMark = actor.AuditTrace.Count;
            var load = actor.LoadPackOntoVehicle(options.SlaveObjId, idempotencyKey: $"{options.CycleId}-load-{pi + 1}");
            CaptureTrace(loadMark);
            stages.Add(Stage($"LOAD-{pi + 1}", load, $"pack {packId} → slave {options.SlaveObjId}"));
            if (load.State != ActorLifecycleState.Completed)
            {
                // Fail-closed: the pack must still be carried (never deleted).
                var retained = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == packId;
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"load-{pi + 1}-completed", false, load.Detail ?? ""));
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"load-{pi + 1}-fail-closed-pack-retained", retained,
                    retained
                        ? $"load refused ({load.Detail}); pack instance {packId} retained in the backpack slot"
                        : $"load refused ({load.Detail}) AND pack instance {packId} left the backpack slot"));
                Logger.Warn("[{Scenario}] {Cycle}: LOAD-{Pack} {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, pi + 1, load.State, load.Failure, load.Detail ?? "");
                return Finish(false, $"LOAD-{pi + 1}", load.Failure, load.Detail ?? "",
                    packItemIds, vendorSold, vendorRevenue);
            }

            var attached = vehicle.AttachedDoodads.FirstOrDefault(d => d.ItemId == packId);
            var loadOk = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) == null
                && inventory.SystemContainer.GetItemByItemId(packId) != null
                && attached != null;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"load-{pi + 1}-completed", true,
                $"loaded pack instance {packId} onto slave {options.SlaveObjId} ({load.Detail})"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"load-{pi + 1}-conservation", loadOk,
                $"pack {packId} at {(attached != null ? attached.AttachPoint.ToString() : "none")}"));
            if (!loadOk)
                return Finish(false, $"LOAD-{pi + 1}", ActorFailureReason.StateTransition,
                    $"loaded pack {pi + 1} failed the conservation check",
                    packItemIds, vendorSold, vendorRevenue);
        }

        // Distinct cargo points: the two packs must not share a snap.
        var points = packItemIds
            .Select(id => vehicle.AttachedDoodads.FirstOrDefault(d => d.ItemId == id)?.AttachPoint)
            .ToList();
        var pointsOk = points.All(p => p != null && p != AttachPointKind.None) && points.Distinct().Count() == packItemIds.Count;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("load-distinct-cargo-points", pointsOk,
            pointsOk
                ? $"packs aboard at {string.Join(", ", points)}"
                : "packs share or miss a cargo point"));
        if (!pointsOk)
            return Finish(false, "LOAD-2", ActorFailureReason.StateTransition,
                "packs not on distinct cargo points", packItemIds, vendorSold, vendorRevenue);

        var materialsAfterCraft = inventory.GetItemsCount(SlotType.Inventory, options.PackMaterialItemId);
        var materialsConsumed = materialsBefore - materialsAfterCraft;
        var materialsOk = materialsConsumed == materialsNeeded;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-craft-materials-conserved", materialsOk,
            $"materials {materialsBefore} → {materialsAfterCraft} (consumed {materialsConsumed}, expected {materialsNeeded})"));
        if (!materialsOk)
            return Finish(false, "PACK-CRAFT-2", ActorFailureReason.StateTransition,
                $"material delta {materialsConsumed} != {materialsNeeded}", packItemIds, vendorSold, vendorRevenue);

        // ---- DRIVE laden to the trader (real movement model, both aboard).
        var driveOk = DriveLeg(actor, pump, options, vehicle, inventory, packItemIds,
            options.TraderDestination, "DRIVE", stages, criteria, traceRecords, Logger,
            out var driveFailure, out var driveReason);
        if (!driveOk)
            return Finish(false, "DRIVE", driveFailure, driveReason, packItemIds, vendorSold, vendorRevenue,
                [], [], 0, 0, vehicle.Transform.World.Position,
                MathUtil.CalculateDistance(vehicle.Transform.World.Position, options.TraderDestination, false));

        // ---- UNLOAD→SELL per pack (interleaved — the slot holds one pack).
        // Fidelity repair (slice-3 precedent): ChangeLabor(-60, Commerce)
        // indexes the Commerce actability directly.
        character.Actability.Actabilities.TryAdd((uint)ActabilityType.Commerce,
            new Actability(new ActabilityTemplate { Id = (uint)ActabilityType.Commerce }));

        var basePrices = new List<int>();
        var payouts = new List<long>();
        for (var pi = 0; pi < packItemIds.Count; pi++)
        {
            var id = packItemIds[pi];
            var cargoDoodad = vehicle.AttachedDoodads.FirstOrDefault(d => d.ItemId == id);
            if (cargoDoodad == null)
                return Finish(false, $"UNLOAD-{pi + 1}", ActorFailureReason.StateTransition,
                    $"pack instance {id} left slave {options.SlaveObjId} before unload",
                    packItemIds, vendorSold, vendorRevenue, basePrices, payouts);

            var unloadMark = actor.AuditTrace.Count;
            var unload = actor.PackPickup(cargoDoodad.ObjId, idempotencyKey: $"{options.CycleId}-unload-{pi + 1}");
            CaptureTrace(unloadMark);
            stages.Add(Stage($"UNLOAD-{pi + 1}", unload, $"cargo doodad {cargoDoodad.ObjId}"));
            if (unload.State != ActorLifecycleState.Completed)
            {
                var stillAttached = vehicle.AttachedDoodads.Any(d => d.ItemId == id);
                var stillOwned = inventory.SystemContainer.GetItemByItemId(id) != null;
                var retained = stillAttached && stillOwned;
                var reason = unload.Detail ?? "";
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"unload-{pi + 1}-completed", false, reason));
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"unload-{pi + 1}-fail-closed-pack-retained", retained,
                    retained
                        ? $"unload refused ({reason}); pack instance {id} retained aboard"
                        : $"unload refused ({reason}) AND pack instance {id} left the slave"));
                Logger.Warn("[{Scenario}] {Cycle}: UNLOAD-{Pack} {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, pi + 1, unload.State, unload.Failure, reason);
                return Finish(false, $"UNLOAD-{pi + 1}", unload.Failure, reason,
                    packItemIds, vendorSold, vendorRevenue, basePrices, payouts);
            }

            if (inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id != id)
                return Finish(false, $"UNLOAD-{pi + 1}", ActorFailureReason.StateTransition,
                    $"pack instance {id} NOT carried after unload",
                    packItemIds, vendorSold, vendorRevenue, basePrices, payouts);
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"unload-{pi + 1}-pack-recovered", true,
                $"pack instance {id} recovered into the Backpack slot"));

            // The documented payout law needs the ratio BEFORE each sale.
            var priceRatio = SpecialtyManager.Instance.GetRatioForSpecialty(character);

            var sellMark = actor.AuditTrace.Count;
            var sell = actor.SellSpecialty(options.GoldTraderObjId, idempotencyKey: $"{options.CycleId}-sell-{pi + 1}");
            CaptureTrace(sellMark);
            stages.Add(Stage($"SELL-{pi + 1}", sell, $"pack {id} @ trader {options.GoldTraderObjId}"));
            if (sell.State != ActorLifecycleState.Completed)
            {
                // Fail-closed hold: pack still carried, no proceeds for it.
                // The sibling pack already sold stays sold (completed legs
                // are never rolled back); the unsold pack must be retained.
                var retained = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == id;
                var reason = sell.Detail ?? "";
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"sell-{pi + 1}-completed", false, reason));
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"sell-{pi + 1}-hold-pack-retained", retained,
                    retained
                        ? $"sale refused ({reason}); pack instance {id} retained in the backpack slot"
                        : $"sale refused ({reason}) AND pack instance {id} left the backpack slot"));
                Logger.Warn("[{Scenario}] {Cycle}: SELL-{Pack} {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, pi + 1, sell.State, sell.Failure, reason);
                return Finish(false, $"SELL-{pi + 1}", sell.Failure, reason,
                    packItemIds, vendorSold, vendorRevenue, basePrices, payouts);
            }

            var basePrice = sell.Result is int salePrice ? salePrice : 0;
            basePrices.Add(basePrice);

            // Payout law (gold trader, coin id 0): round(base × ratio% × 1.05).
            // Measured per sale against the created mails (per-pack proof).
            var mailsBeforeSale = mailsBefore + payouts.Sum();
            var payout = SpecialtyMailCopper(character.Id) - mailsBeforeSale;
            var finalNoInterest = basePrice * (priceRatio / 100f);
            var expectedPayout = (long)Math.Round(finalNoInterest + finalNoInterest * 0.05f);
            var packConsumed = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) == null;
            var payoutOk = payout == expectedPayout && payout > 0 && packConsumed;
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"sell-{pi + 1}-completed", true, sell.Detail ?? "sold"));
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"sell-{pi + 1}-payout-formula", payoutOk,
                payoutOk
                    ? $"pack {id}: mail {payout}c == round(base {basePrice} × {priceRatio}% × 1.05); consumed"
                    : $"pack {id} MISMATCH: mail {payout}c vs expected {expectedPayout}c (base {basePrice}, ratio {priceRatio}%), consumed={packConsumed}"));
            if (!payoutOk)
                return Finish(false, $"SELL-{pi + 1}", ActorFailureReason.StateTransition,
                    $"pack {pi + 1} payout formula violated",
                    packItemIds, vendorSold, vendorRevenue, basePrices, payouts);
            payouts.Add(payout);
        }

        var laborAfterSell = character.LaborPower;
        var laborCharged = laborBefore - laborAfterSell;
        var expectedLabor = expectedCraftLabor * options.PackCount + SellLaborCostPerPack * payouts.Count;
        var laborOk = laborCharged == expectedLabor;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("labor-conserved", laborOk,
            $"labor {laborBefore} → {laborAfterSell} (charged {laborCharged}, expected {expectedLabor} = craft {expectedCraftLabor}×{options.PackCount} + sell {SellLaborCostPerPack}×{payouts.Count})"));
        if (!laborOk)
            return Finish(false, "SELL-2", ActorFailureReason.StateTransition,
                $"labor delta {laborCharged} != {expectedLabor}",
                packItemIds, vendorSold, vendorRevenue, basePrices, payouts);

        // ---- DEPOSIT the combined proceeds figure (slice-3 proceeds note).
        var combinedPayout = payouts.Sum();
        var depositMark = actor.AuditTrace.Count;
        var deposit = actor.DepositMoney(combinedPayout, idempotencyKey: $"{options.CycleId}-deposit");
        CaptureTrace(depositMark);
        stages.Add(Stage("DEPOSIT", deposit, $"{combinedPayout}c"));
        if (deposit.State != ActorLifecycleState.Completed)
        {
            var reason = deposit.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("deposit-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: DEPOSIT {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, deposit.State, deposit.Failure, reason);
            return Finish(false, "DEPOSIT", deposit.Failure, reason,
                packItemIds, vendorSold, vendorRevenue, basePrices, payouts, 0, laborCharged);
        }

        var moneyAfterDeposit = character.Money;
        var bankAfterDeposit = character.Money2;
        var bankDelta = bankAfterDeposit - bankBefore;
        var moneyDelta = moneyAfterDeposit - moneyBefore;
        // Read plainly: inventory gained vendorRevenue, then banked combinedPayout.
        var depositOk = bankDelta == combinedPayout && moneyDelta == vendorRevenue - combinedPayout;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("deposit-completed", true, deposit.Detail ?? "deposited"));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("deposit-conservation", depositOk,
            depositOk
                ? $"bank {bankBefore} → {bankAfterDeposit} (Δ {bankDelta} == combined {combinedPayout}c); inventory Δ {moneyDelta} (vendor +{vendorRevenue}c, banked −{combinedPayout}c)"
                : $"bank conservation VIOLATED: bank Δ {bankDelta} vs combined {combinedPayout}c, inventory Δ {moneyDelta}"));
        if (!depositOk)
            return Finish(false, "DEPOSIT", ActorFailureReason.StateTransition,
                "bank conservation violated",
                packItemIds, vendorSold, vendorRevenue, basePrices, payouts, bankDelta, laborCharged);

        // The no-dup law across the money legs (vendor revenue accounted):
        // mailΔ + bankΔ + (moneyΔ − vendorRevenue) == combined payout.
        var ledgerOk = payouts.Sum() + bankDelta + (moneyDelta - vendorRevenue) == combinedPayout && bankDelta == combinedPayout;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("full-leg-ledger", ledgerOk,
            ledgerOk
                ? $"mail Δ {payouts.Sum()}c + bank Δ {bankDelta}c + (inventory Δ {moneyDelta}c − vendor {vendorRevenue}c) == combined {combinedPayout}c"
                : "ledger MISMATCH across vendor/sell/deposit"));
        if (!ledgerOk)
            return Finish(false, "DEPOSIT", ActorFailureReason.StateTransition,
                "full-leg ledger violated",
                packItemIds, vendorSold, vendorRevenue, basePrices, payouts, bankDelta, laborCharged);

        // ---- RETURN-HOME empty (real movement model, the slice-2 shape).
        var returnOk = DriveLeg(actor, pump, options, vehicle, inventory, packItemIds,
            options.Home, "RETURN-HOME", stages, criteria, traceRecords, Logger,
            out var returnFailure, out var returnReason);
        if (!returnOk)
            return Finish(false, "RETURN-HOME", returnFailure, returnReason,
                packItemIds, vendorSold, vendorRevenue, basePrices, payouts, bankDelta, laborCharged,
                vehicle.Transform.World.Position,
                MathUtil.CalculateDistance(vehicle.Transform.World.Position, options.Home, false));

        // The wagon comes home empty: both packs consumed, and the return
        // never touches cargo — any attachment is a defect.
        var stragglers = vehicle.AttachedDoodads.Count(d => d.ItemId > 0);
        var emptyOk = stragglers == 0;
        var arrival = vehicle.Transform.World.Position;
        var flat = MathUtil.CalculateDistance(arrival, options.Home, false);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("return-home-wagon-empty", emptyOk,
            emptyOk
                ? $"wagon {options.SlaveObjId} home empty at {arrival}"
                : $"{stragglers} pack(s) still attached after the sales"));
        if (!emptyOk)
            return Finish(false, "RETURN-HOME", ActorFailureReason.StateTransition,
                "pack(s) still attached after the sales",
                packItemIds, vendorSold, vendorRevenue, basePrices, payouts, bankDelta, laborCharged, arrival, flat);

        Logger.Info("[{Scenario}] {Cycle}: PASS vended {Trash} rows ({Revenue}c), sold {Packs} packs (base {Base}, payout {Payout}c, banked), wagon {Slave} home",
            ScenarioName, options.CycleId, vendorSold, vendorRevenue, payouts.Count,
            string.Join("+", basePrices), combinedPayout, options.SlaveObjId);
        return Finish(true, "", null, "", packItemIds, vendorSold, vendorRevenue, basePrices, payouts, bankDelta, laborCharged, arrival, flat);
    }

    /// <summary>
    /// One drive leg through the existing action (real client-authored
    /// movement model): pump Running to terminal, expire a stalled pump with
    /// the §17 Navigation reason (the slice-2 close-in idiom, never reroute),
    /// then prove arrival and pack conservation. Shared by the laden
    /// outbound and the empty return.
    /// </summary>
    private static bool DriveLeg(GameplayActor actor, IHaulVendorPump pump, HaulerVendorMultipackOptions options,
        Slave vehicle, AAEmu.Game.Models.Game.Char.Inventory inventory, List<ulong> packItemIds,
        Vector3 destination, string stageName,
        List<BotScenarioRunner.ScenarioStageVerdict> stages, List<BotScenarioRunner.CriterionVerdict> criteria,
        List<ActorAuditRecord> traceRecords, Logger logger,
        out ActorFailureReason? failure, out string reason)
    {
        failure = null;
        reason = "";

        var attachedBefore = vehicle.AttachedDoodads.Where(d => d.ItemId > 0).Select(d => d.ItemId).ToList();

        var driveMark = actor.AuditTrace.Count;
        var drive = actor.DriveVehicle(options.SlaveObjId, destination, options.Speed,
            options.DriveTimeout, idempotencyKey: $"{options.CycleId}-{stageName.ToLowerInvariant()}");
        foreach (var record in actor.AuditTrace.Skip(driveMark))
            traceRecords.Add(record);
        stages.Add(Stage(stageName, drive, $"slave {options.SlaveObjId} → {destination}"));
        if (drive.State == ActorLifecycleState.Running)
        {
            drive = pump.Drive(actor, drive, options.PumpBudget);
            foreach (var record in actor.AuditTrace.Skip(driveMark))
                traceRecords.Add(record);
            if (!drive.IsTerminal)
            {
                _ = drive.Expire(ActorFailureReason.Navigation, $"drive leg exceeded its budget ({options.PumpBudget})");
                foreach (var record in actor.AuditTrace.Skip(driveMark))
                    traceRecords.Add(record);
            }
        }

        if (drive.State != ActorLifecycleState.Completed)
        {
            // Fail-closed hold: every previously attached pack still
            // attached (never deleted, never detached).
            var retained = attachedBefore.Count(id =>
                vehicle.AttachedDoodads.Any(d => d.ItemId == id)
                && inventory.SystemContainer.GetItemByItemId(id) != null);
            var holdOk = retained == attachedBefore.Count;
            reason = drive.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"{stageName.ToLowerInvariant()}-completed", false, reason));
            criteria.Add(new BotScenarioRunner.CriterionVerdict($"{stageName.ToLowerInvariant()}-hold-pack-retained", holdOk,
                holdOk
                    ? $"held ({reason}); {retained} pack(s) still attached"
                    : $"held ({reason}) AND {attachedBefore.Count - retained} pack(s) left the slave"));
            logger.Warn("[{Scenario}] {Cycle}: {Stage} {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, stageName, drive.State, drive.Failure, reason);
            failure = drive.Failure;
            return false;
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict($"{stageName.ToLowerInvariant()}-completed", true, drive.Detail ?? "arrived"));

        var arrival = vehicle.Transform.World.Position;
        var flat = MathUtil.CalculateDistance(arrival, destination, false);
        var zGap = Math.Abs(destination.Z - arrival.Z);
        var arrived = flat <= GameplayActor.ArrivalRadius && zGap <= GameplayActor.ArrivalRadius;
        criteria.Add(new BotScenarioRunner.CriterionVerdict($"{stageName.ToLowerInvariant()}-arrival", arrived,
            arrived
                ? $"arrived at {arrival} (remaining flat {flat:0.###})"
                : $"short of destination: remaining flat {flat:0.###}, z gap {zGap:0.###}"));
        if (!arrived)
        {
            reason = $"drive completed without reaching the destination (remaining flat {flat:0.###})";
            failure = ActorFailureReason.StateTransition;
            return false;
        }

        var conserved = attachedBefore.Count(id =>
            vehicle.AttachedDoodads.Any(d => d.ItemId == id)
            && inventory.SystemContainer.GetItemByItemId(id) != null);
        var conservedOk = conserved == attachedBefore.Count;
        criteria.Add(new BotScenarioRunner.CriterionVerdict($"{stageName.ToLowerInvariant()}-pack-conserved", conservedOk,
            conservedOk
                ? $"{conserved} pack(s) still attached after the drive"
                : $"{attachedBefore.Count - conserved} pack(s) left the slave during the drive"));
        if (!conservedOk)
        {
            reason = "pack(s) left the vehicle during the drive";
            failure = ActorFailureReason.StateTransition;
            return false;
        }

        return true;
    }

    /// <summary>Σ CopperCoins over a character's mails — the slice-3 read
    /// surface (AllPlayerMails, not GetCurrentMailList: the payout mail's
    /// RecvDate sits ~22 h in the future).</summary>
    private static long SpecialtyMailCopper(uint characterId)
        => MailManager.Instance.AllPlayerMails.Values
            .Where(m => m.Header.ReceiverId == characterId)
            .Sum(m => m.Body.CopperCoins);

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
