using System.Linq;
using System.Numerics;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

using NLog;
namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pump that drives an in-flight drive request to a terminal state.
/// The composer owns the leg order and the conservation assertions; the pump
/// owns the tick strategy (headless rigs tick synchronously, live bridges
/// observe world ticks). Mirrors
/// <c>EconomyDayCycleScenario.ICyclePump</c> (C3+C4 precursor precedent) and
/// the slice-1 <see cref="IHaulCraftPump"/> shape.
/// </summary>
public interface IHaulDrivePump
{
    ActorRequest Drive(GameplayActor actor, ActorRequest request, TimeSpan pollBudget);
}

/// <summary>
/// M8 C4 hauler/trader v1 slice-2 — drive-route composer: drive the loaded
/// cargo vehicle through the real <see cref="GameplayActor.DriveVehicle"/>
/// path (driver-seat preflight, already-there short-circuit, client-authored
/// vehicle movement model) from A to B, holding — never blindly rerouting —
/// with the spec §17 taxonomy reason when the leg cannot complete.
///
/// Composition only: the single DRIVE leg calls the EXISTING
/// <see cref="GameplayActor.DriveVehicle"/> action unchanged, driven by an
/// ordinary <see cref="Character"/> through normal gameplay services
/// (AGENTS.md #9). No new engine path, no parallel movement implementation.
/// Builds on slice-1 (<see cref="HaulerPackCraftLoadCycle"/>): the pack is
/// already loaded on the vehicle and the actor already holds the driver seat
/// when this cycle runs; boarding and loading are NOT re-driven here.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Specialty sale, deposit, return-home, vendoring, and restart legs are
/// later slices. Reporting is a canned record (LLM LAST): fixed fields only.
/// The audit trail is the leg's own <see cref="ActorAuditRecord"/> entries
/// plus the returned result.
///
/// Blocked-route semantics (v1 — hold, don't reroute): the movement model is
/// straight-line lerp with no pathfinding, so a blocked or unreachable leg
/// surfaces as the engine's own budget expiry —
/// TimedOut(<see cref="ActorFailureReason.Navigation"/>) via
/// <see cref="ActorTimeoutPolicy.ReasonFor"/> (the DriveVehicle-contract
/// precedent). The cycle surfaces that reason at DRIVE, verifies every
/// previously attached pack is still attached (never deleted, never
/// detached), and stops. A pump that returns while the request is still
/// Running is expired the same way (the EconomyDayCycle drive-leg close-in
/// idiom).
/// </summary>
public static class HaulerDriveRouteCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-hauler-drive-route";

    /// <summary>Scenario parameters.</summary>
    public sealed record HaulerDriveRouteOptions
    {
        /// <summary>Idempotency namespace for this cycle's leg.</summary>
        public string CycleId { get; init; } = "m8c4-d0";

        /// <summary>Loaded cargo vehicle slave ObjId (driver seat already held).</summary>
        public required uint SlaveObjId { get; init; }

        /// <summary>Route destination (single A→B leg; chained routes are a later slice).</summary>
        public required Vector3 Destination { get; init; }

        /// <summary>Drive speed in units/second.</summary>
        public float Speed { get; init; } = 10f;

        /// <summary>Navigation budget for the drive leg (expiry → TimedOut(Navigation)).</summary>
        public TimeSpan DriveTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Wall-clock budget for driving the in-flight request to terminal.</summary>
        public TimeSpan PumpBudget { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class HaulerDriveRouteResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public uint SlaveObjId { get; init; }
        public Vector3 ArrivalPosition { get; init; }
        public float DistanceRemaining { get; init; }
        public int PacksRetained { get; init; }
    }

    /// <summary>
    /// Runs one drive-route cycle: precheck the composition state (known
    /// vehicle, driver seat held, sane leg), drive the vehicle to the
    /// destination through the existing DriveVehicle path, verify arrival
    /// and pack conservation. Fail-closed at every leg: a rejection or a
    /// budget expiry holds the route, records the taxonomy reason, and never
    /// detaches, deletes, or moves the cargo it should not.
    /// </summary>
    public static HaulerDriveRouteResult Run(GameplayActor actor, HaulerDriveRouteOptions options, IHaulDrivePump pump)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        HaulerDriveRouteResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason,
            Vector3 arrivalPosition = default, float distanceRemaining = 0f, int packsRetained = 0)
        {
            return new HaulerDriveRouteResult
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                SlaveObjId = options.SlaveObjId,
                ArrivalPosition = arrivalPosition,
                DistanceRemaining = distanceRemaining,
                PacksRetained = packsRetained
            };
        }

        // Audit records land on terminal transitions only (a Running drive
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

        // ---- PRECHECK: known vehicle (the engine's own GetBaseUnit
        // resolution), driver seat held, sane leg ----
        var vehicle = character.ParentWorld?.GetBaseUnit(options.SlaveObjId) as Slave;
        if (vehicle == null)
        {
            var reason = $"vehicle {options.SlaveObjId} not found in world";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, reason);
        }

        if (!vehicle.AttachedCharacters.TryGetValue(AttachPointKind.Driver, out var seated) || !ReferenceEquals(seated, character))
        {
            var reason = $"not in driver seat of vehicle {options.SlaveObjId} — board first";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, reason));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", reason));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "PRECHECK", ActorFailureReason.StateTransition, reason);
        }

        if (options.Speed <= 0f)
        {
            const string badSpeed = "speed must be positive";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, badSpeed));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", badSpeed));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, badSpeed);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, badSpeed);
        }

        if (!options.Destination.IsFinite())
        {
            const string badDestination = "destination must be finite";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", false, badDestination));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", badDestination));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, badDestination);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, badDestination);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-clean", true,
            $"vehicle {options.SlaveObjId} present, driver seat held, destination {options.Destination}"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Completed", "", $"slave {options.SlaveObjId}"));

        var positionBefore = vehicle.Transform.World.Position;
        var attachedBefore = vehicle.AttachedDoodads.Select(d => d.ItemId).ToList();

        // Packs attached before the leg, still attached AND still owned by
        // the actor's System container afterwards (drive never touches
        // cargo — any loss is a defect, never an expected cost).
        int CountRetained()
        {
            var retained = 0;
            foreach (var id in attachedBefore)
            {
                if (vehicle.AttachedDoodads.Any(d => d.ItemId == id)
                    && inventory.SystemContainer.GetItemByItemId(id) != null)
                    retained++;
            }
            return retained;
        }

        // ---- DRIVE: the existing action (real client-authored movement model) ----
        var driveMark = actor.AuditTrace.Count;
        var drive = actor.DriveVehicle(options.SlaveObjId, options.Destination, options.Speed,
            options.DriveTimeout, idempotencyKey: $"{options.CycleId}-drive");
        CaptureTrace(driveMark);
        stages.Add(Stage("DRIVE", drive, $"slave {options.SlaveObjId} → {options.Destination}"));
        // The engine short-circuits the already-there leg to Completed
        // synchronously (full lifecycle, no movement) — the pump only runs
        // when the leg actually started.
        var shortCircuit = drive.State == ActorLifecycleState.Completed;
        if (drive.State == ActorLifecycleState.Running)
        {
            drive = pump.Drive(actor, drive, options.PumpBudget);
            CaptureTrace(driveMark);
            if (!drive.IsTerminal)
            {
                // The pump refused to advance the leg (blocked route with no
                // progress path) — hold with the §17 Navigation reason, the
                // EconomyDayCycle drive-leg close-in idiom. Never reroute blindly.
                _ = drive.Expire(ActorFailureReason.Navigation, $"drive leg exceeded its budget ({options.PumpBudget})");
                CaptureTrace(driveMark);
            }
        }

        if (drive.State != ActorLifecycleState.Completed)
        {
            // Fail-closed hold: the taxonomy reason stands, and every
            // previously attached pack must still be attached (never deleted).
            var retained = CountRetained();
            var holdOk = retained == attachedBefore.Count;
            var reason = drive.Detail ?? "";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("drive-completed", false, reason));
            criteria.Add(new BotScenarioRunner.CriterionVerdict("drive-hold-pack-retained", holdOk,
                holdOk
                    ? $"held ({reason}); {retained} pack(s) still attached to slave {options.SlaveObjId}"
                    : $"held ({reason}) AND {attachedBefore.Count - retained} pack(s) left slave {options.SlaveObjId}"));
            Logger.Warn("[{Scenario}] {Cycle}: DRIVE {State} ({Failure}) {Reason}",
                ScenarioName, options.CycleId, drive.State, drive.Failure, reason);
            var rest = MathUtil.CalculateDistance(vehicle.Transform.World.Position, options.Destination, false);
            return Finish(false, "DRIVE", drive.Failure, reason, vehicle.Transform.World.Position, rest, retained);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("drive-completed", true, drive.Detail ?? "arrived"));

        if (shortCircuit)
        {
            // The no-op leg must move nothing: the engine never re-entered
            // the movement model, so the vehicle is exactly where it was.
            var unmoved = vehicle.Transform.World.Position == positionBefore;
            criteria.Add(new BotScenarioRunner.CriterionVerdict("drive-already-there", unmoved,
                unmoved
                    ? $"already at destination; vehicle unmoved at {positionBefore}"
                    : $"already-there short-circuit moved the vehicle {positionBefore} → {vehicle.Transform.World.Position}"));
            if (!unmoved)
                return Finish(false, "DRIVE", ActorFailureReason.StateTransition,
                    "already-there drive moved the vehicle",
                    vehicle.Transform.World.Position,
                    MathUtil.CalculateDistance(vehicle.Transform.World.Position, options.Destination, false),
                    CountRetained());
        }

        var arrival = vehicle.Transform.World.Position;
        var flat = MathUtil.CalculateDistance(arrival, options.Destination, false);
        var zGap = Math.Abs(options.Destination.Z - arrival.Z);
        var arrived = flat <= GameplayActor.ArrivalRadius && zGap <= GameplayActor.ArrivalRadius;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("drive-arrival", arrived,
            arrived
                ? $"arrived at {arrival} (remaining flat {flat:0.###})"
                : $"short of destination: remaining flat {flat:0.###}, z gap {zGap:0.###}"));
        if (!arrived)
            return Finish(false, "DRIVE", ActorFailureReason.StateTransition,
                $"drive completed without reaching the destination (remaining flat {flat:0.###})",
                arrival, flat, CountRetained());

        var conserved = CountRetained();
        var conservedOk = conserved == attachedBefore.Count;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("drive-pack-conserved", conservedOk,
            conservedOk
                ? $"{conserved} pack(s) still attached to slave {options.SlaveObjId} after the drive"
                : $"{attachedBefore.Count - conserved} pack(s) left slave {options.SlaveObjId} during the drive"));
        if (!conservedOk)
            return Finish(false, "DRIVE", ActorFailureReason.StateTransition,
                "pack(s) left the vehicle during the drive",
                arrival, flat, conserved);

        Logger.Info("[{Scenario}] {Cycle}: PASS drove slave {Slave} to {Destination} ({Packs} pack(s) retained)",
            ScenarioName, options.CycleId, options.SlaveObjId, options.Destination, conserved);
        return Finish(true, "", null, "", arrival, flat, conserved);
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, ActorRequest request, string note)
        => new(name, request == null ? 0 : 1,
            request?.State.ToString() ?? "n/a",
            request?.Result?.ToString() ?? "",
            note + (request?.Detail is { Length: > 0 } d ? $" — {d}" : ""));
}
