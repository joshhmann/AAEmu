using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M8 C3 farmer v1 (ROADMAP M8 living-village contracts) — one owned-crop
/// cycle: owned-plot precheck → harvest → deposit yield → replant approved
/// crop → canned shortage report.
///
/// Composition only: every leg calls the EXISTING <see cref="GameplayActor"/>
/// actions (<see cref="GameplayActor.Harvest"/>, <see cref="GameplayActor.DepositItem"/>,
/// <see cref="GameplayActor.Plant"/>) unchanged, driven by an ordinary
/// <see cref="Character"/> through normal gameplay services (AGENTS.md #9).
/// No new engine path, no parallel harvest implementation — the harvest runs
/// the real <c>Doodad.Use</c> chain inside <see cref="GameplayActor.Harvest"/>.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// C1 schedules / C2 chatter integration is C5 scope. Reporting is a canned
/// record (LLM LAST): no model, no chat, no procedural text generation —
/// fixed fields only. The audit trail is the legs' own
/// <see cref="ActorAuditRecord"/> entries plus the returned result.
/// </summary>
public static class FarmerCycleScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "m8-farmer-cycle";

    /// <summary>
    /// Slice allowlist v1: potato seed only (build ruling 2 — a slice
    /// constant; data-driven allowlists are later scope, no config surface
    /// is invented here).
    /// </summary>
    public const uint ApprovedSeedItemId = 15659;

    /// <summary>Scenario parameters.</summary>
    public sealed record FarmerCycleOptions
    {
        /// <summary>Idempotency namespace for this cycle's legs.</summary>
        public string CycleId { get; init; } = "m8c3-c0";

        /// <summary>
        /// Seed to replant with. Must equal <see cref="ApprovedSeedItemId"/> —
        /// anything else holds the replant leg with a reason (never plants).
        /// </summary>
        public uint SeedItemId { get; init; } = ApprovedSeedItemId;
    }

    /// <summary>
    /// Canned shortage report (build ruling 3 — consumed from the audit
    /// trail + return value; B4 metadata is C5 surface). Fixed fields only.
    /// </summary>
    public sealed class FarmerShortageReport
    {
        public string CycleId { get; init; } = "";
        public uint CropObjId { get; init; }
        public string HarvestOutcome { get; init; } = "";
        public Dictionary<uint, int> YieldDeposited { get; init; } = new();
        public List<string> DepositHolds { get; init; } = [];
        public int SeedsBeforeHarvest { get; init; }
        public int SeedsAfterHarvest { get; init; }
        public int SeedsAfterReplant { get; init; }
        public string ReplantOutcome { get; init; } = "";
        public bool Passed { get; init; }
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class FarmerCycleResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public required FarmerShortageReport Report { get; init; }
    }

    /// <summary>
    /// Runs one farmer cycle against the given crop doodad: precheck the
    /// plot is owned, harvest it, deposit every non-seed yield stack into
    /// the bank (Warehouse — build ruling 1), replant the approved seed at
    /// the freed plot position, and report. Fail-closed at every leg: a
    /// rejection stops the cycle, records the reason, and never deletes,
    /// vends, or moves anything it should not.
    /// </summary>
    public static FarmerCycleResult Run(GameplayActor actor, uint cropObjId, FarmerCycleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        options ??= new FarmerCycleOptions();

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var yieldDeposited = new Dictionary<uint, int>();
        var depositHolds = new List<string>();
        var harvestOutcome = "";
        var replantOutcome = "";
        var seedsBeforeHarvest = 0;
        var seedsAfterHarvest = 0;
        var seedsAfterReplant = 0;

        FarmerCycleResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason)
        {
            var report = new FarmerShortageReport
            {
                CycleId = options.CycleId,
                CropObjId = cropObjId,
                HarvestOutcome = harvestOutcome,
                YieldDeposited = yieldDeposited,
                DepositHolds = depositHolds,
                SeedsBeforeHarvest = seedsBeforeHarvest,
                SeedsAfterHarvest = seedsAfterHarvest,
                SeedsAfterReplant = seedsAfterReplant,
                ReplantOutcome = replantOutcome,
                Passed = passed
            };
            return new FarmerCycleResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                Report = report
            };
        }

        var character = actor.Character;
        var world = character.ParentWorld;

        // ---- PRECHECK: the crop must exist and the plot must be owned ----
        var doodad = world?.GetDoodad(cropObjId);
        if (doodad == null)
        {
            var reason = $"doodad {cropObjId} not found in world";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-owned", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        if (!IsOwnedPlot(doodad, character))
        {
            var reason = $"no permission to harvest doodad {cropObjId} (foreign owner or no house permission)";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-owned", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-owned", true,
            $"crop {cropObjId} owned (owner {doodad.OwnerType}:{doodad.OwnerId}/{doodad.OwnerDbId})"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"crop {cropObjId}"));

        // Capture the plot position BEFORE the harvest (the final phase
        // deletes the doodad, so the position must be read up front).
        var plotPosition = doodad.Transform.World.Position;
        var bagBefore = SnapshotBag(character);
        seedsBeforeHarvest = CountIn(bagBefore, options.SeedItemId);

        // ---- HARVEST: the existing action (real Doodad.Use chain) ----
        var harvest = actor.Harvest(cropObjId, idempotencyKey: $"{options.CycleId}-harvest-{cropObjId}");
        traceRecords.Add(actor.AuditTrace.Last());
        stages.Add(Stage($"HARVEST-{cropObjId}", harvest, $"crop {cropObjId}"));
        harvestOutcome = $"{harvest.State} ({harvest.Detail})";
        if (harvest.State != ActorLifecycleState.Completed)
        {
            var reason = harvest.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("harvest-completed", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: HARVEST {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, harvest.State, harvest.Failure, reason);
            return Finish(false, "HARVEST", harvest.Failure, reason);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("harvest-completed", true,
            $"harvested doodad {cropObjId} ({harvest.Detail})"));
        var bagAfterHarvest = SnapshotBag(character);
        seedsAfterHarvest = CountIn(bagAfterHarvest, options.SeedItemId);

        // ---- DEPOSIT: every non-seed yield stack into the bank ----
        // The granted seed is replant stock, never deposited.
        var depositsOk = true;
        foreach (var (templateId, gained) in YieldGains(bagBefore, bagAfterHarvest))
        {
            if (gained <= 0 || templateId == options.SeedItemId)
                continue;
            var deposit = actor.DepositItem(templateId, idempotencyKey: $"{options.CycleId}-deposit-{templateId}");
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage($"DEPOSIT-{templateId}", deposit, $"yield {templateId} x{gained}"));
            if (deposit.State != ActorLifecycleState.Completed)
            {
                // Hold with reason — the yield stays in the bag (never
                // deleted, never vended); the cycle fails closed below.
                depositsOk = false;
                var hold = $"deposit of yield {templateId} held: {deposit.Detail}";
                depositHolds.Add(hold);
                Logger.Warn("[{Scenario}] {Cycle}: DEPOSIT hold ({Failure}) {Hold}",
                    ScenarioName, options.CycleId, deposit.Failure, hold);
                continue;
            }

            yieldDeposited[templateId] = deposit.Result is int moved ? moved : gained;
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("deposits-completed", depositsOk,
            depositsOk
                ? $"deposited {yieldDeposited.Count} yield stack(s) into the bank"
                : $"deposit holds: {string.Join("; ", depositHolds)}"));
        if (!depositsOk)
            return Finish(false, "DEPOSIT", ActorFailureReason.RejectedAction, string.Join("; ", depositHolds));

        // ---- REPLANT: the approved seed at the freed plot position ----
        var replantRan = false;
        if (options.SeedItemId != ApprovedSeedItemId)
        {
            replantOutcome = $"replant of seed {options.SeedItemId} held: not on the approved list (potato {ApprovedSeedItemId} only)";
            seedsAfterReplant = seedsAfterHarvest;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("replant-effect-once", false, replantOutcome));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("REPLANT", 0, "Held", "", replantOutcome));
            Logger.Warn("[{Scenario}] {Cycle}: REPLANT {Outcome}", ScenarioName, options.CycleId, replantOutcome);
            return Finish(false, "REPLANT", ActorFailureReason.RejectedAction, replantOutcome);
        }

        var plant = actor.Plant(options.SeedItemId, plotPosition, idempotencyKey: $"{options.CycleId}-plant");
        replantRan = true;
        traceRecords.Add(actor.AuditTrace.Last());
        stages.Add(Stage("REPLANT", plant, $"seed {options.SeedItemId}"));
        replantOutcome = $"{plant.State} ({plant.Detail})";
        seedsAfterReplant = character.Inventory.GetItemsCount(options.SeedItemId);

        if (plant.State != ActorLifecycleState.Completed && plant.State != ActorLifecycleState.Interrupted)
        {
            var reason = plant.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("replant-effect-once", false, reason));
            Logger.Warn("[{Scenario}] {Cycle}: REPLANT {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, plant.State, plant.Failure, reason);
            return Finish(false, "REPLANT", plant.Failure, reason);
        }

        // The replant must consume exactly one seed — Completed on a live
        // world (new crop ObjId), or Interrupted at the persistence boundary
        // in headless worlds (engine-true seed consumption is the backstop;
        // the locked key forbids a second consumption). Either way the seed
        // delta is the conservation proof.
        var seedsConsumed = seedsAfterHarvest - seedsAfterReplant;
        var replantOnce = seedsConsumed == 1;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("replant-effect-once", replantOnce,
            replantOnce
                ? $"replant consumed exactly one seed ({plant.State}, {plant.Detail})"
                : $"replant seed delta {seedsConsumed} != 1 ({plant.State}, {plant.Detail})"));
        criteria.Add(new BotScenarioRunner.CriterionVerdict("seed-conservation", replantOnce,
            $"seeds {seedsBeforeHarvest} → harvest → {seedsAfterHarvest} → replant → {seedsAfterReplant} (consumed {seedsConsumed})"));
        if (!replantOnce)
            return Finish(false, "REPLANT", ActorFailureReason.StateTransition,
                $"replant seed delta {seedsConsumed} != 1");

        Logger.Info("[{Scenario}] {Cycle}: PASS crop {Crop} harvested, {Stacks} stack(s) banked, seed replanted",
            ScenarioName, options.CycleId, cropObjId, yieldDeposited.Count);
        return Finish(true, "", null, "");
    }

    /// <summary>
    /// Owned-plot rule: directly character-owned by the actor, or
    /// house-bound where the house allows the actor to interact (the Plant
    /// gate precedent — <c>GetHouseAtLocation + AllowedToInteract</c>).
    /// Anything else (foreign character owner, missing/denied house) is
    /// foreign-or-occupied.
    /// </summary>
    private static bool IsOwnedPlot(Doodad doodad, Character character)
    {
        if (doodad.OwnerType == DoodadOwnerType.Character)
            return doodad.OwnerId == character.Id;
        if (doodad.OwnerType == DoodadOwnerType.Housing)
            return HousingManager.Instance.GetHouseById(doodad.OwnerDbId)?.AllowedToInteract(character) == true;
        return false;
    }

    private static Dictionary<uint, int> SnapshotBag(Character character)
    {
        var snapshot = new Dictionary<uint, int>();
        var items = character.Inventory?.Bag.Items;
        if (items == null)
            return snapshot;
        foreach (var item in items)
        {
            if (item == null)
                continue;
            snapshot.TryGetValue(item.TemplateId, out var count);
            snapshot[item.TemplateId] = count + item.Count;
        }

        return snapshot;
    }

    private static int CountIn(Dictionary<uint, int> snapshot, uint templateId)
        => snapshot.TryGetValue(templateId, out var count) ? count : 0;

    private static IEnumerable<KeyValuePair<uint, int>> YieldGains(
        Dictionary<uint, int> before, Dictionary<uint, int> after)
    {
        foreach (var (templateId, countAfter) in after)
        {
            var gained = countAfter - CountIn(before, templateId);
            if (gained > 0)
                yield return new KeyValuePair<uint, int>(templateId, gained);
        }
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
