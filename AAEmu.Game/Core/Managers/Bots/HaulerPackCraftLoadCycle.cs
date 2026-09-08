using System.Linq;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Skills;

using NLog;
namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pump that drives an in-flight pack-craft request to a terminal state.
/// The composer owns the leg order and the conservation assertions; the pump
/// owns the wait strategy (headless rigs tick synchronously, live bridges
/// observe world ticks). Mirrors
/// <c>EconomyDayCycleScenario.ICyclePump</c> (C3+C4 precursor precedent).
/// </summary>
public interface IHaulCraftPump
{
    ActorRequest DriveCraft(GameplayActor actor, ActorRequest request, uint benchObjId, uint skillId, TimeSpan maxWait);
}

/// <summary>
/// M8 C4 hauler/trader v1 slice-1 — pack craft → load composer: craft a trade
/// pack through the real <see cref="GameplayActor.Craft"/> path, board the
/// cargo vehicle through the real <see cref="GameplayActor.BoardVehicle"/>
/// path, and load the pack onto its cargo point through the real
/// <see cref="GameplayActor.LoadPackOntoVehicle"/> path
/// (PackVehicleService → SlaveManager attach seam).
///
/// Composition only: every leg calls the EXISTING <see cref="GameplayActor"/>
/// actions unchanged, driven by an ordinary <see cref="Character"/> through
/// normal gameplay services (AGENTS.md #9). No new engine path, no parallel
/// pack/vehicle implementation.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Drive (route), specialty sale, deposit, return-home, vendoring, and
/// restart legs are later slices. Reporting is a canned record (LLM LAST):
/// fixed fields only. The audit trail is the legs' own
/// <see cref="ActorAuditRecord"/> entries plus the returned result.
/// </summary>
public static class HaulerPackCraftLoadCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-hauler-pack-craft-load";

    /// <summary>Scenario parameters.</summary>
    public sealed record HaulerPackCraftLoadOptions
    {
        /// <summary>Idempotency namespace for this cycle's legs.</summary>
        public string CycleId { get; init; } = "m8c4-c0";

        /// <summary>Pack recipe id (materials → trade pack, rate 100).</summary>
        public required uint PackCraftId { get; init; }

        /// <summary>Pack recipe material item id.</summary>
        public required uint PackMaterialItemId { get; init; }

        /// <summary>Pack recipe material amount per pack.</summary>
        public int PackMaterialAmount { get; init; } = 1;

        /// <summary>Craft bench doodad ObjId (recipe ReqDoodad match).</summary>
        public required uint BenchObjId { get; init; }

        /// <summary>Cargo vehicle slave ObjId.</summary>
        public required uint SlaveObjId { get; init; }

        /// <summary>Budget for driving the in-flight craft to terminal.</summary>
        public TimeSpan CraftTimeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class HaulerPackCraftLoadResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public ulong PackItemId { get; init; }
        public AttachPointKind? AttachPoint { get; init; }
        public int MaterialsConsumed { get; init; }
        public int LaborCharged { get; init; }
    }
    /// <summary>
    /// Runs one craft→load cycle: precheck a clean composition state, craft
    /// the pack, board the driver seat, load the pack onto the first free
    /// cargo point. Fail-closed at every leg: a rejection stops the cycle,
    /// records the reason, and never deletes, vends, or moves anything it
    /// should not.
    /// </summary>
    public static HaulerPackCraftLoadResult Run(GameplayActor actor, HaulerPackCraftLoadOptions options, IHaulCraftPump pump)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        HaulerPackCraftLoadResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason,
            ulong packItemId = 0, AttachPointKind? attachPoint = null, int materialsConsumed = 0, int laborCharged = 0)
        {
            return new HaulerPackCraftLoadResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,

                PackItemId = packItemId,
                AttachPoint = attachPoint,
                MaterialsConsumed = materialsConsumed,
                LaborCharged = laborCharged
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


        // ---- PRECHECK: known recipe, empty backpack slot, known vehicle ----
        var craft = CraftManager.Instance.GetCraftById(options.PackCraftId);
        if (craft == null)
        {
            var reason = $"unknown pack craft {options.PackCraftId}";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var carriedBefore = inventory?.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        if (carriedBefore != null)
        {
            var reason = $"backpack slot occupied by item {carriedBefore.Id} (template {carriedBefore.TemplateId}) — refusing to craft over a carried pack";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        var slave = character.ParentWorld?.SlaveManager.GetSlaveByObjId(options.SlaveObjId);
        if (slave == null)
        {
            var reason = $"vehicle {options.SlaveObjId} not found in world";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", true,
            $"pack craft {options.PackCraftId}, slot empty, vehicle {options.SlaveObjId} present"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"craft {options.PackCraftId}"));

        var materialsBefore = inventory.GetItemsCount(SlotType.Inventory, options.PackMaterialItemId);
        var laborBefore = character.LaborPower;
        var skillTemplate = SkillManager.Instance.GetSkillTemplate(craft.SkillId);
        var expectedLabor = skillTemplate != null ? new Skill(skillTemplate).GetLaborCost(character) : 0;
        var productTemplateId = craft.CraftProducts.Count > 0 ? craft.CraftProducts[0].ItemId : 0;

        // ---- PACK-CRAFT: the existing action (real CharacterCraft queue) ----
        var craftMark = actor.AuditTrace.Count;
        var craftRequest = actor.Craft(options.PackCraftId, options.BenchObjId, idempotencyKey: $"{options.CycleId}-craft");
        CaptureTrace(craftMark);
        stages.Add(Stage("PACK-CRAFT", craftRequest, $"craft {options.PackCraftId}"));
        if (craftRequest.State != ActorLifecycleState.Running)
        {
            // Fail-closed: missing inputs, unknown bench, low labor, busy
            // queue — the engine refused before mutating anything.
            var reason = craftRequest.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-craft-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: PACK-CRAFT {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, craftRequest.State, craftRequest.Failure, reason);
            return Finish(false, "PACK-CRAFT", craftRequest.Failure, reason);
        }
        var crafted = pump.DriveCraft(actor, craftRequest, options.BenchObjId, craft.SkillId, options.CraftTimeout);
        CaptureTrace(craftMark);
        if (crafted.State != ActorLifecycleState.Completed)
        {
            var reason = crafted.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-craft-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: PACK-CRAFT {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, crafted.State, crafted.Failure, reason);
            return Finish(false, "PACK-CRAFT", crafted.Failure, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-craft-completed", true,
            $"crafted pack via recipe {options.PackCraftId} ({crafted.Detail})"));

        var materialsAfterCraft = inventory.GetItemsCount(SlotType.Inventory, options.PackMaterialItemId);
        var materialsConsumed = materialsBefore - materialsAfterCraft;
        var materialsOk = materialsConsumed == options.PackMaterialAmount;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-craft-materials-conserved", materialsOk,
            $"materials {materialsBefore} → {materialsAfterCraft} (consumed {materialsConsumed}, expected {options.PackMaterialAmount})"));
        if (!materialsOk)
            return Finish(false, "PACK-CRAFT", ActorFailureReason.StateTransition,
                $"pack-craft material delta {materialsConsumed} != {options.PackMaterialAmount}");

        var pack = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        var productOk = pack != null && (productTemplateId == 0 || pack.TemplateId == productTemplateId);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-craft-product-granted", productOk,
            productOk
                ? $"pack {pack!.TemplateId} (instance {pack.Id}) in the backpack slot"
                : $"no pack in the backpack slot after craft {options.PackCraftId}"));
        if (!productOk)
            return Finish(false, "PACK-CRAFT", ActorFailureReason.StateTransition,
                "pack craft completed without granting the pack to the backpack slot");

        var laborAfterCraft = character.LaborPower;
        var laborCharged = laborBefore - laborAfterCraft;
        var laborOk = laborCharged == expectedLabor;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("pack-craft-labor-conserved", laborOk,
            $"labor {laborBefore} → {laborAfterCraft} (charged {laborCharged}, expected {expectedLabor})"));
        if (!laborOk)
            return Finish(false, "PACK-CRAFT", ActorFailureReason.StateTransition,
                $"pack-craft labor delta {laborCharged} != {expectedLabor}");

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
            return Finish(false, "BOARD", board.Failure, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("board-completed", true,
            $"boarded slave {options.SlaveObjId} at the driver seat"));

        var packId = pack.Id;
        var loadMark = actor.AuditTrace.Count;
        var load = actor.LoadPackOntoVehicle(options.SlaveObjId, idempotencyKey: $"{options.CycleId}-load");
        CaptureTrace(loadMark);
        stages.Add(Stage("LOAD", load, $"slave {options.SlaveObjId}"));
        if (load.State != ActorLifecycleState.Completed)
        {
            // Fail-closed: the pack must still be carried (the engine rolls
            // back half-spawned loads into the backpack slot — never delete).
            var retained = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.Id == packId;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("load-completed", false, load.Detail ?? ""));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("load-fail-closed-pack-retained", retained,
                retained
                    ? $"load refused ({load.Detail}); pack instance {packId} retained in the backpack slot"
                    : $"load refused ({load.Detail}) AND pack instance {packId} left the backpack slot"));
            Logger.Warn("[{Scenario}] {Cycle}: LOAD {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, load.State, load.Failure, load.Detail ?? "");
            return Finish(false, "LOAD", load.Failure, load.Detail ?? "", packId, null, materialsConsumed, laborCharged);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("load-completed", true,
            $"loaded pack instance {packId} onto slave {options.SlaveObjId} ({load.Detail})"));

        var slotEmpty = inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack) == null;
        var inSystem = inventory.SystemContainer.GetItemByItemId(packId) != null;
        var attached = slave.AttachedDoodads.FirstOrDefault(d => d.ItemId == packId);
        var conserved = slotEmpty && inSystem && attached != null;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("load-conservation", conserved,
            $"slot empty: {slotEmpty}; system holds {packId}: {inSystem}; attached at {(attached != null ? attached.AttachPoint.ToString() : "none")}"));
        if (!conserved)
            return Finish(false, "LOAD", ActorFailureReason.StateTransition,
                "loaded pack failed the conservation check (slot/system/attachment)",
                packId, attached?.AttachPoint, materialsConsumed, laborCharged);

        Logger.Info("[{Scenario}] {Cycle}: PASS pack {Pack} crafted and loaded onto slave {Slave} at {Point}",
            ScenarioName, options.CycleId, packId, options.SlaveObjId, attached!.AttachPoint);
        return Finish(true, "", null, "", packId, attached.AttachPoint, materialsConsumed, laborCharged);
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
