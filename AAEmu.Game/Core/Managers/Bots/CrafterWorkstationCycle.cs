using System.Linq;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Skills;

using NLog;
namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pump that drives an in-flight craft request to a terminal state.
/// The composer owns the leg order and the conservation assertions; the pump
/// owns the wait strategy (headless rigs tick synchronously, live bridges
/// observe world ticks). Mirrors <see cref="IHaulCraftPump"/> (C4 slice-1
/// precedent).
/// </summary>
public interface ICrafterPump
{
    ActorRequest DriveCraft(GameplayActor actor, ActorRequest request, uint benchObjId, uint skillId, TimeSpan maxWait);
}

/// <summary>
/// M8 Crafter v1 slice-1 — withdraw → workstation craft → store composer:
/// withdraw recipe materials from the bank through the real
/// <see cref="GameplayActor.WithdrawItem"/> path, craft through the real
/// <see cref="GameplayActor.Craft"/> path (CharacterCraft queue), and store
/// the product back into the bank through the real
/// <see cref="GameplayActor.DepositItem"/> path, ending in a canned shortage
/// report.
///
/// Composition only: every leg calls the EXISTING <see cref="GameplayActor"/>
/// actions unchanged, driven by an ordinary <see cref="Character"/> through
/// normal gameplay services (AGENTS.md #9). No new engine path, no parallel
/// craft/inventory implementation.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Slice-3 (trade-pack production): pack recipes (ResultsInBackpack) run the
/// same withdraw → craft legs, assert auto-equip into the Backpack slot (not
/// the bag), and skip the bank store leg — the pack stays equipped for hauler
/// pickup. Merchant purchase, sale, vendoring, chance-rate products, and
/// restart legs are later slices. Reporting is a canned record
///
/// Engine-truth notes (approved corrections): the craft leg charges materials
/// + labor only — the recipe model carries no currency cost, so no currency
/// conservation is asserted here. The engine has no workstation reservation
/// lock — "busy/occupied" surfaces as holds with reason (missing bench,
/// busy queue, short materials), never as a lock the composer invents.
/// </summary>
public static class CrafterWorkstationCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-crafter-workstation";

    /// <summary>Scenario parameters.</summary>
    public sealed record CrafterWorkstationOptions
    {
        /// <summary>Idempotency namespace for this cycle's legs.</summary>
        public string CycleId { get; init; } = "m8cw-c0";

        /// <summary>Craft recipe id (materials → product, rate 100).</summary>
        public required uint CraftId { get; init; }

        /// <summary>Craft bench doodad ObjId (recipe ReqDoodad match).</summary>
        public required uint BenchObjId { get; init; }

        /// <summary>Budget for driving the in-flight craft to terminal.</summary>
        public TimeSpan CraftTimeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>Canned shortage report (LLM LAST): fixed fields only.</summary>
    public sealed class CrafterShortageReport
    {
        public string CycleId { get; init; } = "";
        public uint CraftId { get; init; }
        public uint BenchObjId { get; init; }
        public Dictionary<uint, int> Withdrawn { get; init; } = new();
        public List<string> MissingRows { get; init; } = [];
        public List<string> Holds { get; init; } = [];
        public Dictionary<uint, int> ProductStored { get; init; } = new();
        public Dictionary<uint, int> MaterialsConsumed { get; init; } = new();
        public int LaborCharged { get; init; }
        public bool Passed { get; init; }
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class CrafterWorkstationResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public Dictionary<uint, int> MaterialsConsumed { get; init; } = new();
        public int LaborCharged { get; init; }
        public ulong PackItemId { get; init; }
        public CrafterShortageReport? Report { get; init; }
    }

    /// <summary>
    /// Runs one withdraw→craft→store cycle: precheck a clean composition
    /// state, withdraw every recipe material row from the bank, craft the
    /// product at the bench, store the product back into the bank.
    /// Fail-closed at every leg: a rejection stops the cycle, records the
    /// reason, and never deletes, vends, or moves anything it should not.
    /// </summary>
    public static CrafterWorkstationResult Run(GameplayActor actor, CrafterWorkstationOptions options, ICrafterPump pump)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var withdrawn = new Dictionary<uint, int>();
        var missingRows = new List<string>();
        var holds = new List<string>();
        var productStored = new Dictionary<uint, int>();
        var materialsConsumed = new Dictionary<uint, int>();
        CrafterWorkstationResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason,
            int laborCharged = 0, ulong packItemId = 0)
        {
            return new CrafterWorkstationResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                MaterialsConsumed = materialsConsumed,
                LaborCharged = laborCharged,
                PackItemId = packItemId,
                Report = new CrafterShortageReport
                {
                    CycleId = options.CycleId,
                    CraftId = options.CraftId,
                    BenchObjId = options.BenchObjId,
                    Withdrawn = withdrawn,
                    MissingRows = missingRows,
                    Holds = holds,
                    ProductStored = productStored,
                    MaterialsConsumed = materialsConsumed,
                    LaborCharged = laborCharged,
                    Passed = passed
                }
            };
        }

        // Audit records land on terminal transitions only (a Running craft
        // has none yet) — capture by count-mark so nothing is missed and
        // nothing is duplicated.
        void CaptureTrace(int mark)
        {
            foreach (var record in actor.AuditTrace.Skip(mark))
                traceRecords.Add(record);
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

        // ---- PRECHECK: known recipe, known bench ----
        var craft = CraftManager.Instance.GetCraftById(options.CraftId);
        if (craft == null)
        {
            var reason = $"unknown craft {options.CraftId}";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var bench = character.ParentWorld?.GetDoodad(options.BenchObjId);
        if (bench == null)
        {
            // Hold with reason: no station, no craft — never run the engine
            // step against a missing workbench.
            var reason = $"craft bench {options.BenchObjId} not found in world";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        // Slice-3 (pack production): a pack auto-equips into the Backpack
        // slot — crafting over an occupied slot refuses in the engine
        // (CanReplaceGliderInBackpackSlot), so hold here with reason before
        // moving anything.
        if (craft.ResultsInBackpack && inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) is { } incumbent)
        {
            var occupied = $"backpack slot occupied by item {incumbent.Id} (template {incumbent.TemplateId}) — refusing to craft over a carried pack";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, occupied));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", occupied));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, occupied);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, occupied);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", true,
            $"craft {options.CraftId}, bench {options.BenchObjId} present"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"craft {options.CraftId}"));

        var bagBefore = craft.CraftMaterials.ToDictionary(m => m.ItemId,
            m => inventory.GetItemsCount(SlotType.Inventory, m.ItemId));
        var bankBefore = craft.CraftProducts.Select(p => p.ItemId).Distinct().ToDictionary(id => id,
            id => inventory.GetItemsCount(SlotType.Bank, id));
        var laborBefore = character.LaborPower;
        var skillTemplate = SkillManager.Instance.GetSkillTemplate(craft.SkillId);
        var expectedLabor = skillTemplate != null ? new Skill(skillTemplate).GetLaborCost(character) : 0;

        // ---- WITHDRAW: every recipe material row out of the bank ----
        foreach (var row in craft.CraftMaterials)
        {
            var mark = actor.AuditTrace.Count;
            var withdraw = actor.WithdrawItem(row.ItemId, idempotencyKey: $"{options.CycleId}-withdraw-{row.ItemId}");
            CaptureTrace(mark);
            stages.Add(Stage($"WITHDRAW-{row.ItemId}", withdraw, $"material {row.ItemId} x{row.Amount}"));
            if (withdraw.State != ActorLifecycleState.Completed)
            {
                // Fail-closed: the bank simply lacks the row (or the bag is
                // full) — nothing was crafted, nothing was consumed. Rows
                // already withdrawn stay in the bag (never deleted).
                var missing = $"material {row.ItemId} x{row.Amount} unavailable: {withdraw.Detail}";
                missingRows.Add(missing);
                holds.Add(missing);
                criteria.Add(new BotScenarioRunner.CriterionVerdict("withdraw-completed", false, missing));
                Logger.Warn("[{Scenario}] {Cycle}: WITHDRAW {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, withdraw.State, withdraw.Failure, missing);
                return Finish(false, "WITHDRAW", withdraw.Failure, missing);
            }
            withdrawn[row.ItemId] = withdraw.Result is int moved ? moved : row.Amount;
        }

        // Shortage hold: a withdrawn stack may cover less than the recipe
        // row (bank held a partial stack) — hold with reason instead of
        // letting the engine refuse the craft step.
        foreach (var row in craft.CraftMaterials)
        {
            var inBag = inventory.GetItemsCount(SlotType.Inventory, row.ItemId);
            if (inBag < row.Amount)
            {
                var missing = $"material {row.ItemId} short: bag holds {inBag}, recipe needs {row.Amount}";
                missingRows.Add(missing);
                holds.Add(missing);
                criteria.Add(new BotScenarioRunner.CriterionVerdict("withdraw-completed", false, missing));
                Logger.Warn("[{Scenario}] {Cycle}: WITHDRAW shortage {Reason}", ScenarioName, options.CycleId, missing);
                return Finish(false, "WITHDRAW", ActorFailureReason.RejectedAction, missing);
            }
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("withdraw-completed", true,
            $"withdrew {withdrawn.Count} material row(s) from the bank"));
        var productTemplateIds = craft.CraftProducts.Select(p => p.ItemId).Distinct().ToList();
        var productBefore = productTemplateIds.ToDictionary(id => id,
            id => inventory.GetItemsCount(SlotType.Inventory, id)
                + (inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) is { } pack && pack.TemplateId == id ? pack.Count : 0));

        // ---- CRAFT: the existing action (real CharacterCraft queue) ----
        var craftMark = actor.AuditTrace.Count;
        var craftRequest = actor.Craft(options.CraftId, options.BenchObjId, idempotencyKey: $"{options.CycleId}-craft");
        CaptureTrace(craftMark);
        stages.Add(Stage("CRAFT", craftRequest, $"craft {options.CraftId}"));
        if (craftRequest.State != ActorLifecycleState.Running)
        {
            // Fail-closed: short materials, wrong bench, low labor, busy
            // queue — the engine refused before mutating anything. Withdrawn
            // rows stay in the bag (never deleted, never vended).
            var reason = craftRequest.Detail ?? "";
            holds.Add($"craft held: {reason}");
            criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: CRAFT {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, craftRequest.State, craftRequest.Failure, reason);
            return Finish(false, "CRAFT", craftRequest.Failure, reason);
        }

        var crafted = pump.DriveCraft(actor, craftRequest, options.BenchObjId, craft.SkillId, options.CraftTimeout);
        CaptureTrace(craftMark);
        if (crafted.State != ActorLifecycleState.Completed)
        {
            var reason = crafted.Detail ?? "";
            holds.Add($"craft held: {reason}");
            criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: CRAFT {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, crafted.State, crafted.Failure, reason);
            return Finish(false, "CRAFT", crafted.Failure, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-completed", true,
            $"crafted via recipe {options.CraftId} ({crafted.Detail})"));

        // Consumption is measured against everything the cycle put in the
        // bag: the pre-withdraw baseline PLUS the withdrawn rows (whole
        // stacks may exceed the recipe row — the engine consumes exactly
        // the recipe amount, so the check stays exact).
        foreach (var row in craft.CraftMaterials)
        {
            var after = inventory.GetItemsCount(SlotType.Inventory, row.ItemId);
            var consumed = bagBefore.GetValueOrDefault(row.ItemId, 0)
                + withdrawn.GetValueOrDefault(row.ItemId, 0)
                - after;
            materialsConsumed[row.ItemId] = consumed;
        }

        var materialsOk = craft.CraftMaterials.All(row =>
            materialsConsumed.GetValueOrDefault(row.ItemId, 0) == row.Amount);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-materials-conserved", materialsOk,
            string.Join("; ", craft.CraftMaterials.Select(row =>
                $"{row.ItemId}: consumed {materialsConsumed.GetValueOrDefault(row.ItemId, 0)}, expected {row.Amount}"))));
        if (!materialsOk)
            return Finish(false, "CRAFT", ActorFailureReason.StateTransition,
                "craft material deltas do not match the recipe rows");

        var grantedOk = productTemplateIds.All(id =>
            inventory.GetItemsCount(SlotType.Inventory, id)
            + (inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) is { } pack && pack.TemplateId == id ? pack.Count : 0)
            - productBefore.GetValueOrDefault(id, 0) == craft.CraftProducts.Where(p => p.ItemId == id).Sum(p => p.Amount));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-product-granted", grantedOk,
            grantedOk
                ? $"product row(s) granted: {string.Join(", ", productTemplateIds)}"
                : "craft completed without granting the recipe product rows"));
        if (!grantedOk)
            return Finish(false, "CRAFT", ActorFailureReason.StateTransition,
                "craft completed without granting the recipe product rows");

        var laborAfterCraft = character.LaborPower;
        var laborCharged = laborBefore - laborAfterCraft;
        var laborOk = laborCharged == expectedLabor;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("craft-labor-conserved", laborOk,
            $"labor {laborBefore} → {laborAfterCraft} (charged {laborCharged}, expected {expectedLabor})"));
        if (!laborOk)
            return Finish(false, "CRAFT", ActorFailureReason.StateTransition,
                $"craft labor delta {laborCharged} != {expectedLabor}");

        // ---- PACK HANDOFF (slice-3): pack recipes auto-equip into the
        // Backpack slot (TryEquipNewBackPack) — DepositItem only moves bag
        // stacks, so there is no bank store leg. The pack stays equipped
        // for hauler pickup: assert the slot grant, the empty bag row, and
        // the untouched bank row (nothing duplicated).
        if (craft.ResultsInBackpack)
        {
            var packProductId = craft.CraftProducts.Count > 0 ? craft.CraftProducts[0].ItemId : 0;
            var pack = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            var packEquipped = pack != null && (packProductId == 0 || pack.TemplateId == packProductId);
            var packInBag = packProductId != 0 ? inventory.GetItemsCount(SlotType.Inventory, packProductId) : 0;
            var packAutoEquip = packEquipped && packInBag == 0;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-auto-equip", packAutoEquip,
                packAutoEquip
                    ? $"pack {pack!.TemplateId} (instance {pack.Id}) auto-equipped in the backpack slot, bag holds 0"
                    : $"pack not auto-equipped in the backpack slot (slot: {(pack != null ? $"{pack.TemplateId} x{pack.Count}" : "empty")}, bag: {packInBag})"));
            if (!packAutoEquip)
                return Finish(false, "CRAFT", ActorFailureReason.StateTransition,
                    "pack craft completed without auto-equipping the pack into the backpack slot", laborCharged);

            var bankPackDelta = packProductId != 0
                ? inventory.GetItemsCount(SlotType.Bank, packProductId) - bankBefore.GetValueOrDefault(packProductId, 0)
                : 0;
            var handoffOk = bankPackDelta == 0;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-handoff-conserved", handoffOk,
                handoffOk
                    ? $"pack instance {pack!.Id} in the slot, materials consumed per recipe, labor {laborCharged}, bank pack delta 0 (nothing duplicated)"
                    : $"pack bank delta {bankPackDelta} != 0 — pack duplicated outside the slot"));
            if (!handoffOk)
                return Finish(false, "CRAFT", ActorFailureReason.StateTransition,
                    "pack handoff failed the conservation check (pack duplicated outside the slot)", laborCharged, pack!.Id);

            Logger.Info("[{Scenario}] {Cycle}: PASS craft {Craft} produced pack {Pack} into the backpack slot",
                ScenarioName, options.CycleId, options.CraftId, pack!.Id);
            return Finish(true, "", null, "", laborCharged, pack.Id);
        }

        // ---- STORE: the product rows back into the bank ----
        foreach (var id in productTemplateIds)
        {
            var mark = actor.AuditTrace.Count;
            var deposit = actor.DepositItem(id, idempotencyKey: $"{options.CycleId}-store-{id}");
            CaptureTrace(mark);
            stages.Add(Stage($"STORE-{id}", deposit, $"product {id}"));
            if (deposit.State != ActorLifecycleState.Completed)
            {
                // Hold with reason: the product stays in the bag (never
                // deleted, never vended); the cycle fails closed below.
                var hold = $"store of product {id} held: {deposit.Detail}";
                holds.Add(hold);
                var retained = inventory.GetItemsCount(SlotType.Inventory, id) > 0;
                criteria.Add(new BotScenarioRunner.CriterionVerdict("store-completed", false, deposit.Detail ?? ""));
                criteria.Add(new BotScenarioRunner.CriterionVerdict("store-fail-closed-product-retained", retained,
                    retained
                        ? $"store refused ({deposit.Detail}); product {id} retained in the bag"
                        : $"store refused ({deposit.Detail}) AND product {id} left the bag"));
                Logger.Warn("[{Scenario}] {Cycle}: STORE {State} ({Failure}) {Reason}",
                    ScenarioName, options.CycleId, deposit.State, deposit.Failure, hold);
                return Finish(false, "STORE", deposit.Failure, hold, laborCharged);
            }

            productStored[id] = deposit.Result is int moved ? moved : 0;
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("store-completed", true,
            $"stored {productStored.Count} product row(s) into the bank"));

        var storeOk = productTemplateIds.All(id =>
            inventory.GetItemsCount(SlotType.Bank, id) - bankBefore.GetValueOrDefault(id, 0)
            == craft.CraftProducts.Where(p => p.ItemId == id).Sum(p => p.Amount));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("store-conservation", storeOk,
            storeOk
                ? "bank deltas match the granted product rows exactly once"
                : "stored product failed the bank conservation check"));
        if (!storeOk)
            return Finish(false, "STORE", ActorFailureReason.StateTransition,
                "stored product failed the bank conservation check", laborCharged);

        Logger.Info("[{Scenario}] {Cycle}: PASS craft {Craft} stored {Rows} product row(s)",
            ScenarioName, options.CycleId, options.CraftId, productStored.Count);
        return Finish(true, "", null, "", laborCharged);
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
