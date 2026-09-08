using AAEmu.Game.Models.Game.Bots;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M8 C5 day integration slice-1 (ROADMAP M8 living-village contracts) —
/// two-villager profession half-day: the C1 phase machine (pure
/// <see cref="BotScheduleResolver"/> sweep over game-clock hours, never
/// TimeManager) dispatches each villager's Work ticks to the existing C3/C4
/// slice composers (<see cref="FarmerCycleScenario"/> for farmers,
/// <see cref="CrafterWorkstationCycle"/> for crafters), then reconciles a
/// combined ledger and the M5 audit-completeness law over the concatenated
/// traces.
///
/// Composition only: every profession leg calls the EXISTING slice composers
/// unchanged, driven by ordinary <see cref="Character"/> records through
/// normal gameplay services (AGENTS.md #9). No new engine path, no parallel
/// profession implementation, no schedule/resolver/arbiter changes.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Restarts, hauler legs, chatter integration, and scale-up are later slices.
/// Reporting is a canned record (LLM LAST): fixed fields only. The audit
/// trail is the legs' own <see cref="ActorAuditRecord"/> entries plus the
/// returned result.
/// </summary>
public static class VillageDayCycle
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the slice.</summary>
    public const string ScenarioName = "m8-village-day-s1";

    /// <summary>Which existing slice composer a villager's Work ticks dispatch to.</summary>
    public enum VillageProfession : byte
    {
        Farmer = 0,
        Crafter = 1
    }

    /// <summary>One villager's day parameters.</summary>
    public sealed record VillagerSpec
    {
        /// <summary>Stable villager name (idempotency namespace segment).</summary>
        public required string Name { get; init; }

        /// <summary>Which slice composer owns this villager's Work ticks.</summary>
        public required VillageProfession Profession { get; init; }

        /// <summary>The villager's ordinary character actor.</summary>
        public required GameplayActor Actor { get; init; }

        /// <summary>Daily anchors for the phase sweep (default: template work 08-18).</summary>
        public BotDailyAnchors Anchors { get; init; } = BotDailyAnchors.Template;

        /// <summary>Farmer only: the owned mature crop doodad ObjId (0 = unbound).</summary>
        public uint CropObjId { get; init; }

        /// <summary>Farmer only: seed to replant (must be the slice allowlist).</summary>
        public uint FarmerSeedItemId { get; init; } = FarmerCycleScenario.ApprovedSeedItemId;

        /// <summary>Crafter only: workstation options (CycleId is namespaced per villager).</summary>
        public CrafterWorkstationCycle.CrafterWorkstationOptions? CrafterOptions { get; init; }

        /// <summary>Crafter only: pump driving the in-flight craft to terminal.</summary>
        public ICrafterPump? CrafterPump { get; init; }
    }

    /// <summary>Day-sweep parameters.</summary>
    public sealed record VillageDayOptions
    {
        /// <summary>Idempotency namespace for this day's legs.</summary>
        public string CycleId { get; init; } = "m8v1-c0";

        /// <summary>First swept game hour (default 06: end of rest).</summary>
        public float StartHour { get; init; } = 6f;

        /// <summary>Last swept game hour (default 14: mid-work half-day).</summary>
        public float EndHour { get; init; } = 14f;

        /// <summary>Sweep step in game hours (default 0.25).</summary>
        public float StepHours { get; init; } = 0.25f;
    }

    /// <summary>Per-villager day evidence.</summary>
    public sealed class VillagerDayEntry
    {
        public required string Name { get; init; }
        public VillageProfession Profession { get; init; }
        public List<BotSchedulePhase> PhasesVisited { get; init; } = [];
        public int WorkTicks { get; init; }
        public int NonWorkTicks { get; init; }
        public List<string> Holds { get; init; } = [];
        public int LaborBefore { get; init; }
        public int LaborAfter { get; init; }
        public FarmerCycleScenario.FarmerCycleResult? FarmerResult { get; init; }
        public CrafterWorkstationCycle.CrafterWorkstationResult? CrafterResult { get; init; }
    }

    /// <summary>Canned village report (LLM LAST): fixed fields only.</summary>
    public sealed class VillageDayReport
    {
        public string CycleId { get; init; } = "";
        public List<VillagerDayEntry> Villagers { get; init; } = [];
        public bool Passed { get; init; }
    }

    /// <summary>Structured run result — stage/criterion evidence attached.</summary>
    public sealed class VillageDayResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public required VillageDayReport Report { get; init; }
    }

    /// <summary>
    /// Runs one village half-day: sweep game hours per villager through the
    /// resolver, run each villager's profession leg ONCE on its first Work
    /// tick, hold (never dispatch) on every non-Work tick, then reconcile
    /// the combined ledger. Fail-closed at every leg: a rejected profession
    /// leg stops that villager, records the reason, and never touches the
    /// other villagers' state.
    /// </summary>
    public static VillageDayResult Run(IReadOnlyList<VillagerSpec> villagers, VillageDayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(villagers);
        options ??= new VillageDayOptions();

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var entries = new List<VillagerDayEntry>();

        VillageDayResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason)
            => new()
            {
                Scenario = ScenarioName,
                Passed = passed,
                FailStage = failStage,
                Failure = failure,
                FailReason = failReason,
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                Report = new VillageDayReport
                {
                    CycleId = options.CycleId,
                    Villagers = entries,
                    Passed = passed
                }
            };

        if (villagers.Count == 0)
        {
            const string empty = "village has no villagers";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-roster", false, empty));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", empty));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, empty);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, empty);
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-roster", true,
            $"village roster: {string.Join(", ", villagers.Select(v => $"{v.Name}/{v.Profession}"))}"));
        stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", villagers.Count, "Completed", "", "roster bound"));

        // ---- SWEEP: resolve + dispatch per villager ----
        foreach (var spec in villagers)
        {
            var laborBefore = spec.Actor.Character.LaborPower;
            var phasesVisited = new List<BotSchedulePhase>();
            var holds = new List<string>();
            var workTicks = 0;
            var nonWorkTicks = 0;
            var legRan = false;
            FarmerCycleScenario.FarmerCycleResult? farmerResult = null;
            CrafterWorkstationCycle.CrafterWorkstationResult? crafterResult = null;
            BotSchedulePhase? previous = null;

            for (var hour = options.StartHour; hour <= options.EndHour + 0.0001f; hour += options.StepHours)
            {
                var phase = BotScheduleResolver.Resolve(spec.Anchors, hour, previous);
                if (previous is null || phase != previous)
                {
                    phasesVisited.Add(phase);
                    stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                        $"PHASE-{spec.Name}-{phase}", 1, $"h={hour:F2}", phase.ToString(),
                        $"villager {spec.Name} entered {phase} at game hour {hour:F2}"));
                    previous = phase;
                }

                if (phase == BotSchedulePhase.Work)
                {
                    workTicks++;
                    if (legRan)
                        continue;
                    legRan = true;

                    if (spec.Profession == VillageProfession.Farmer)
                    {
                        if (spec.CropObjId == 0)
                        {
                            holds.Add($"WORK held for {spec.Name}: no crop bound");
                            Logger.Warn("[{Scenario}] {Cycle}: WORK hold {Name} (no crop bound)",
                                ScenarioName, options.CycleId, spec.Name);
                            continue;
                        }

                        farmerResult = FarmerCycleScenario.Run(spec.Actor, spec.CropObjId,
                            new FarmerCycleScenario.FarmerCycleOptions
                            {
                                CycleId = $"{options.CycleId}-{spec.Name}-farm",
                                SeedItemId = spec.FarmerSeedItemId
                            });
                        foreach (var stage in farmerResult.Stages)
                            stages.Add(stage);
                        foreach (var record in farmerResult.TraceRecords)
                            traceRecords.Add(record);
                        stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                            $"WORK-{spec.Name}", farmerResult.Stages.Count,
                            farmerResult.Passed ? "Completed" : farmerResult.FailStage,
                            farmerResult.Scenario, farmerResult.FailReason));
                    }
                    else
                    {
                        if (spec.CrafterOptions is null || spec.CrafterPump is null)
                        {
                            holds.Add($"WORK held for {spec.Name}: no workstation options/pump bound");
                            Logger.Warn("[{Scenario}] {Cycle}: WORK hold {Name} (no workstation bound)",
                                ScenarioName, options.CycleId, spec.Name);
                            continue;
                        }

                        crafterResult = CrafterWorkstationCycle.Run(spec.Actor,
                            new CrafterWorkstationCycle.CrafterWorkstationOptions
                            {
                                CycleId = $"{options.CycleId}-{spec.Name}-craft",
                                CraftId = spec.CrafterOptions.CraftId,
                                BenchObjId = spec.CrafterOptions.BenchObjId,
                                CraftTimeout = spec.CrafterOptions.CraftTimeout
                            },
                            spec.CrafterPump);
                        foreach (var stage in crafterResult.Stages)
                            stages.Add(stage);
                        foreach (var record in crafterResult.TraceRecords)
                            traceRecords.Add(record);
                        stages.Add(new BotScenarioRunner.ScenarioStageVerdict(
                            $"WORK-{spec.Name}", crafterResult.Stages.Count,
                            crafterResult.Passed ? "Completed" : crafterResult.FailStage,
                            crafterResult.Scenario, crafterResult.FailReason));
                    }
                }
                else
                {
                    nonWorkTicks++;
                }
            }

            if (workTicks == 0)
                holds.Add($"held for {spec.Name}: no Work phase in the {options.StartHour:F1}->{options.EndHour:F1} sweep; zero profession legs");

            entries.Add(new VillagerDayEntry
            {
                Name = spec.Name,
                Profession = spec.Profession,
                PhasesVisited = phasesVisited,
                WorkTicks = workTicks,
                NonWorkTicks = nonWorkTicks,
                Holds = holds,
                LaborBefore = laborBefore,
                LaborAfter = spec.Actor.Character.LaborPower,
                FarmerResult = farmerResult,
                CrafterResult = crafterResult
            });
        }

        // ---- VERIFY: dispatch + combined ledger + audit ----
        var dispatchNotes = new List<string>();
        var dispatchOk = true;
        foreach (var (spec, entry) in villagers.Zip(entries))
        {
            var ranMatchingLeg = (spec.Profession, entry.FarmerResult, entry.CrafterResult) switch
            {
                (VillageProfession.Farmer, not null, null)
                    when entry.FarmerResult.Scenario == FarmerCycleScenario.ScenarioName => true,
                (VillageProfession.Crafter, null, not null)
                    when entry.CrafterResult.Scenario == CrafterWorkstationCycle.ScenarioName => true,
                _ => entry.WorkTicks == 0 && entry.FarmerResult is null && entry.CrafterResult is null
            };
            if (!ranMatchingLeg)
            {
                dispatchOk = false;
                dispatchNotes.Add($"{spec.Name}: workTicks={entry.WorkTicks} but no matching {spec.Profession} leg");
            }
            else
            {
                dispatchNotes.Add($"{spec.Name}: workTicks={entry.WorkTicks}, " +
                    (entry.FarmerResult is not null || entry.CrafterResult is not null ? "matching leg ran once" : "held (no Work)"));
            }
        }

        criteria.Add(new BotScenarioRunner.CriterionVerdict("dispatch-correct", dispatchOk,
            string.Join("; ", dispatchNotes)));

        var laborDelta = entries.Where(e => e.CrafterResult is not null).Sum(e => e.LaborBefore - e.LaborAfter);
        var laborCharged = entries.Where(e => e.CrafterResult is not null).Sum(e => e.CrafterResult!.LaborCharged);
        var laborOk = laborDelta == laborCharged;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("village-labor-conserved", laborOk,
            entries.Any(e => e.CrafterResult is not null)
                ? $"crafter labor deltas {laborDelta} == charged {laborCharged}"
                : "no crafter legs ran; labor law vacuous"));

        var passedFarmers = entries.Where(e => e.FarmerResult?.Passed == true).ToList();
        var seedsOk = passedFarmers.All(e =>
            e.FarmerResult!.Report.SeedsAfterHarvest - e.FarmerResult.Report.SeedsAfterReplant == 1);
        criteria.Add(new BotScenarioRunner.CriterionVerdict("village-farmer-seeds-conserved", seedsOk,
            passedFarmers.Count == 0
                ? "no passed farmer legs; seed law vacuous"
                : string.Join("; ", passedFarmers.Select(e =>
                    $"{e.Name}: seeds {e.FarmerResult!.Report.SeedsBeforeHarvest}->" +
                    $"{e.FarmerResult.Report.SeedsAfterHarvest}->" +
                    $"{e.FarmerResult.Report.SeedsAfterReplant}"))));

        var legsRan = entries.Any(e => e.FarmerResult is not null || e.CrafterResult is not null);
        var incomplete = traceRecords
            .Where(r => r.Result == ActorLifecycleState.Completed)
            .Where(r => r.StateChanges.Count == 0 ||
                        !r.StateChanges.Any(s => s.Contains("Requested")) ||
                        !r.StateChanges.Any(s => s.Contains("Accepted")) ||
                        !r.StateChanges.Any(s => s.Contains("Running")) ||
                        !r.StateChanges.Any(s => s.Contains("Completed")))
            .ToList();
        var rejectedRunning = traceRecords
            .Where(r => r.Result == ActorLifecycleState.Rejected && r.StateChanges.Any(s => s.Contains("Running")))
            .ToList();
        var auditOk = !legsRan
            ? traceRecords.Count == 0
            : traceRecords.Count > 0 && incomplete.Count == 0 && rejectedRunning.Count == 0;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("village-audit-complete", auditOk,
            $"records={traceRecords.Count} incompleteCompleted={incomplete.Count} " +
            $"rejectedWithRunning={rejectedRunning.Count}"));

        var failedLegs = entries
            .Where(e => (e.FarmerResult is { Passed: false }) || (e.CrafterResult is { Passed: false }))
            .ToList();
        criteria.Add(new BotScenarioRunner.CriterionVerdict("village-fail-closed", true,
            failedLegs.Count == 0
                ? "every profession leg completed"
                : $"held legs fail closed: {string.Join("; ", failedLegs.Select(e => $"{e.Name}/{e.FarmerResult?.FailStage ?? e.CrafterResult!.FailStage}"))}"));

        if (failedLegs.Count > 0)
        {
            var first = failedLegs[0];
            var subStage = first.FarmerResult?.FailStage ?? first.CrafterResult!.FailStage;
            var subFailure = first.FarmerResult?.Failure ?? first.CrafterResult!.Failure;
            var reason = $"{first.Name} {first.Profession} leg failed closed at {subStage}";
            Logger.Warn("[{Scenario}] {Cycle}: WORK {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, $"WORK-{first.Name}", subFailure, reason);
        }

        if (criteria.Any(c => !c.Passed))
        {
            var failed = string.Join("; ", criteria.Where(c => !c.Passed).Select(c => c.Name));
            var reason = $"village ledger failed verification: {failed}";
            Logger.Warn("[{Scenario}] {Cycle}: VERIFY {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "VERIFY", ActorFailureReason.StateTransition, reason);
        }

        Logger.Info("[{Scenario}] {Cycle}: PASS {Count} villager(s), {Records} audit records",
            ScenarioName, options.CycleId, entries.Count, traceRecords.Count);
        return Finish(true, "", null, "");
    }
}
