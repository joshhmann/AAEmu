#nullable enable

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M8 C5 soak assembly — repeat-loop driver over <see cref="VillageFullDayCycle"/>
/// (the slice-4 composer, unchanged): runs N villagers through M full day-cycles
/// with economy conservation asserted PER CYCLE plus a cumulative reconciliation
/// across the whole run (labor charged once per cycle, zero net inventory drift
/// end-to-end, bank drift == Σ evening refunds) and the M5 audit-completeness
/// law over the concatenated traces.
///
/// Composition only: the driver calls the EXISTING full-day composer unchanged
/// (each cycle in its own idempotency namespace, $"{CycleId}-d{{cycle}}"),
/// driven by ordinary <see cref="Character"/> records through normal gameplay
/// services (AGENTS.md #9). No new engine path, no parallel profession/trade
/// implementation, no schedule/resolver/arbiter changes. Fail-closed at every
/// cycle: a failed cycle stops the run and the banked state stands as the
/// failed cycle left it.
///
/// Consumables (craft mats, evening saleable output, crop maturity) are spent
/// every cycle, so the caller provides a per-cycle <c>restock</c> hook that
/// runs BEFORE the factory each cycle (including the first): restock
/// provisions from caller-owned references by cycle index, then the factory
/// resolves specs from the restocked state — specs must never embed
/// consumable identity ahead of the restock. The driver owns the loop and
/// the conservation assertions, the caller owns provisioning. The day-scale
/// run (.165) reuses this driver with a larger
/// <see cref="VillageFullDaySoakOptions.Cycles"/>; the SHORT local run proves
/// soak-readiness with a small Cycles value.
///
/// Default-OFF surface: this is a static callable with no tick subscription,
/// no bootstrap, no background work — inert unless a caller invokes it.
/// Reporting is a canned record (LLM LAST): fixed fields only.
/// </summary>
public static class VillageFullDaySoak
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key for the soak.</summary>
    public const string ScenarioName = "m8-village-fullday-soak";

    /// <summary>Soak-sweep parameters.</summary>
    public sealed record VillageFullDaySoakOptions
    {
        /// <summary>Idempotency base (cycle i runs under $"{CycleId}-d{i}").</summary>
        public string CycleId { get; init; } = "m8v5-soak";

        /// <summary>Number of full day-cycles to run (≥ 1).</summary>
        public int Cycles { get; init; } = 3;

        /// <summary>Day-sweep template (CycleId overridden per cycle).</summary>
        public VillageFullDayCycle.VillageFullDayOptions Day { get; init; } = new();
    }

    /// <summary>Per-cycle soak evidence (one full-day result, summarized).</summary>
    public sealed class SoakCycleSummary
    {
        public int Cycle { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public int LaborCharged { get; init; }
        public int LaborDelta { get; init; }
        public long MoneyDelta { get; init; }
        public long BankDelta { get; init; }
        public long Refund { get; init; }
        public int ChatterLines { get; init; }
        public int TraceRecords { get; init; }
    }

    /// <summary>Canned soak report (LLM LAST): fixed fields only.</summary>
    public sealed class VillageFullDaySoakReport
    {
        public string CycleId { get; init; } = "";
        public int CyclesRequested { get; init; }
        public int CyclesCompleted { get; init; }
        public List<SoakCycleSummary> CycleSummaries { get; init; } = [];
        public int LaborChargedTotal { get; init; }
        public int LaborDeltaTotal { get; init; }
        public long MoneyDeltaTotal { get; init; }
        public long BankDeltaTotal { get; init; }
        public long RefundTotal { get; init; }
        public int ChatterLinesTotal { get; init; }
        public int TraceRecordsTotal { get; init; }
        public bool Passed { get; init; }
    }

    /// <summary>Structured soak result — stage/criterion evidence attached.</summary>
    public sealed class VillageFullDaySoakResult
    {
        public required string Scenario { get; init; }
        public bool Passed { get; init; }
        public string FailStage { get; init; } = "";
        public ActorFailureReason? Failure { get; init; }
        public string FailReason { get; init; } = "";
        public List<BotScenarioRunner.ScenarioStageVerdict> Stages { get; init; } = [];
        public List<BotScenarioRunner.CriterionVerdict> Criteria { get; init; } = [];
        public List<ActorAuditRecord> TraceRecords { get; init; } = [];
        public required VillageFullDaySoakReport Report { get; init; }
    }

    /// <summary>
    /// Runs the soak: per cycle, restock → resolve specs → the full-day
    /// composer unchanged → per-cycle conservation gate. Fail-closed: the
    /// first failed cycle stops the run.
    /// </summary>
    public static VillageFullDaySoakResult Run(
        Func<int, IReadOnlyList<VillageFullDayCycle.FullDayVillager>> villagerFactory,
        VillageFullDaySoakOptions? options = null,
        Action<int>? restock = null,
        IVillageMovePump? pump = null)
    {
        ArgumentNullException.ThrowIfNull(villagerFactory);
        options ??= new VillageFullDaySoakOptions();

        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var summaries = new List<SoakCycleSummary>();

        VillageFullDaySoakResult Finish(bool passed, string failStage, ActorFailureReason? failure, string failReason)
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
                Report = new VillageFullDaySoakReport
                {
                    CycleId = options.CycleId,
                    CyclesRequested = options.Cycles,
                    CyclesCompleted = summaries.Count(s => s.Passed),
                    CycleSummaries = summaries,
                    LaborChargedTotal = summaries.Sum(s => s.LaborCharged),
                    LaborDeltaTotal = summaries.Sum(s => s.LaborDelta),
                    MoneyDeltaTotal = summaries.Sum(s => s.MoneyDelta),
                    BankDeltaTotal = summaries.Sum(s => s.BankDelta),
                    RefundTotal = summaries.Sum(s => s.Refund),
                    ChatterLinesTotal = summaries.Sum(s => s.ChatterLines),
                    TraceRecordsTotal = traceRecords.Count,
                    Passed = passed
                }
            };

        if (options.Cycles < 1)
        {
            const string bad = "soak has no cycles";
            criteria.Add(new BotScenarioRunner.CriterionVerdict("precheck-cycles", false, $"{bad} (Cycles={options.Cycles})"));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict("PRECHECK", 0, "Rejected", "", bad));
            Logger.Warn("[{Scenario}] {Cycle}: PRECHECK {Reason}", ScenarioName, options.CycleId, bad);
            return Finish(false, "PRECHECK", ActorFailureReason.RejectedAction, bad);
        }

        // ---- CYCLE LOOP ------------------------------------------------
        // Per-cycle deltas come from the composer entries (the same
        for (var cycle = 0; cycle < options.Cycles; cycle++)
        {
            // Restock BEFORE the factory: specs must observe restocked
            // state (fresh crop ids, banked mats) — resolving first would
            // embed the previous cycle's spent identities.
            restock?.Invoke(cycle);

            var specs = villagerFactory(cycle);
            if (specs.Count == 0)
            {
                var reason = $"cycle {cycle}: villager factory returned no villagers";
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"soak-cycle-{cycle}-passed", false, reason));
                Logger.Warn("[{Scenario}] {Cycle}: CYCLE {N} {Reason}", ScenarioName, options.CycleId, cycle, reason);
                return Finish(false, $"CYCLE-{cycle}", ActorFailureReason.RejectedAction, reason);
            }

            var cycleId = $"{options.CycleId}-d{cycle}";
            var result = VillageFullDayCycle.Run(
                specs,
                options.Day with { CycleId = cycleId },
                pump);
            stages.AddRange(result.Stages);
            criteria.AddRange(result.Criteria);
            traceRecords.AddRange(result.TraceRecords);

            // Per-cycle conservation gate: the full-day ledger must hold
            // EVERY cycle, not just on average — a cycle that passes the
            // composer but breaks conservation stops the soak.
            var required = new[]
            {
                "fullday-day-passed", "fullday-evening-passed", "fullday-labor-conserved",
                "fullday-money-conserved", "fullday-bank-reconciled", "fullday-audit-complete"
            };
            var missing = required
                .Where(name => result.Criteria.All(c => c.Name != name || !c.Passed))
                .ToList();

            var laborCharged = result.Report.Villagers.Sum(e => e.DayEntry?.CrafterResult?.LaborCharged ?? 0);
            // Crafter-scoped like the full-day law (farmer plant/harvest
            // legs charge real skill labor on live, zero in the rig).
            var laborDelta = result.Report.Villagers.Where(e => e.DayEntry?.CrafterResult is not null)
                .Sum(e => e.LaborBeforeDay - e.LaborAfterEvening);
            var moneyDelta = result.Report.Villagers.Sum(e => e.MoneyAfterEvening - e.MoneyBeforeDay);
            var bankDelta = result.Report.Villagers.Sum(e => e.BankAfterEvening - e.BankBeforeDay);
            var refund = result.Report.Villagers.Sum(e => e.EveningEntry?.Refund ?? 0);

            summaries.Add(new SoakCycleSummary
            {
                Cycle = cycle,
                Passed = result.Passed && missing.Count == 0,
                FailStage = result.FailStage,
                LaborCharged = laborCharged,
                LaborDelta = laborDelta,
                MoneyDelta = moneyDelta,
                BankDelta = bankDelta,
                Refund = refund,
                ChatterLines = result.Report.ChatterLinesTotal,
                TraceRecords = result.TraceRecords.Count
            });

            if (!result.Passed || missing.Count != 0)
            {
                var reason = !result.Passed
                    ? $"cycle {cycle} failed at {result.FailStage}: {result.FailReason}"
                    : $"cycle {cycle} broke conservation: {string.Join(", ", missing)}";
                criteria.Add(new BotScenarioRunner.CriterionVerdict($"soak-cycle-{cycle}-passed", false, reason));
                Logger.Warn("[{Scenario}] {Cycle}: CYCLE {N} {Reason}", ScenarioName, options.CycleId, cycle, reason);
                return Finish(false, result.Passed ? $"VERIFY-d{cycle}" : result.FailStage,
                    result.Failure ?? ActorFailureReason.StateTransition, reason);
            }

            criteria.Add(new BotScenarioRunner.CriterionVerdict($"soak-cycle-{cycle}-passed", true,
                $"cycle {cycle}: crafter labor Δ{laborDelta}==charged {laborCharged}, money Δ{moneyDelta}, bank Δ{bankDelta}==refund {refund}"));
            stages.Add(new BotScenarioRunner.ScenarioStageVerdict($"CYCLE-{cycle}", specs.Count, "Completed", "",
                $"full day conserved (refund {refund}c)"));
            Logger.Info("[{Scenario}] {Cycle}: CYCLE {N} PASS ({Count} villagers, refund {Refund}c)",
                ScenarioName, options.CycleId, cycle, specs.Count, refund);
        }

        // ---- CUMULATIVE RECONCILIATION ----------------------------------
        var laborChargedTotal = summaries.Sum(s => s.LaborCharged);
        var laborDeltaTotal = summaries.Sum(s => s.LaborDelta);
        var soakLaborOk = laborDeltaTotal == laborChargedTotal;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("soak-labor-conserved", soakLaborOk,
            soakLaborOk
                ? $"Σ cycle crafter labor deltas {laborDeltaTotal} == Σ charged {laborChargedTotal} across {summaries.Count} cycles"
                : $"labor MISMATCH: Σ crafter deltas {laborDeltaTotal} vs Σ charged {laborChargedTotal}"));

        var moneyDeltaTotal = summaries.Sum(s => s.MoneyDelta);
        var soakMoneyOk = moneyDeltaTotal == 0;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("soak-money-conserved", soakMoneyOk,
            soakMoneyOk
                ? $"zero net inventory drift across {summaries.Count} cycles"
                : $"inventory drift VIOLATED: Σ money deltas {moneyDeltaTotal}"));

        var bankDeltaTotal = summaries.Sum(s => s.BankDelta);
        var refundTotal = summaries.Sum(s => s.Refund);
        var soakBankOk = bankDeltaTotal == refundTotal;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("soak-bank-reconciled", soakBankOk,
            soakBankOk
                ? $"bank drift {bankDeltaTotal} == Σ evening refunds {refundTotal} across {summaries.Count} cycles"
                : $"bank reconciliation VIOLATED: drift {bankDeltaTotal} vs Σ refunds {refundTotal}"));

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
        var soakAuditOk = traceRecords.Count > 0 && incomplete.Count == 0 && rejectedRunning.Count == 0;
        criteria.Add(new BotScenarioRunner.CriterionVerdict("soak-audit-complete", soakAuditOk,
            $"concatenated records={traceRecords.Count} incompleteCompleted={incomplete.Count} " +
            $"rejectedWithRunning={rejectedRunning.Count}"));

        criteria.Add(new BotScenarioRunner.CriterionVerdict("soak-cycles-completed",
            summaries.Count == options.Cycles && summaries.All(s => s.Passed),
            $"completed {summaries.Count(s => s.Passed)}/{options.Cycles} day cycles"));

        if (criteria.Any(c => !c.Passed))
        {
            var failed = string.Join("; ", criteria.Where(c => !c.Passed).Select(c => c.Name));
            var reason = $"soak reconciliation failed: {failed}";
            Logger.Warn("[{Scenario}] {Cycle}: VERIFY {Reason}", ScenarioName, options.CycleId, reason);
            return Finish(false, "VERIFY", ActorFailureReason.StateTransition, reason);
        }

        Logger.Info("[{Scenario}] {Cycle}: PASS {Cycles} cycle(s), refund total {Refund}c, {Records} audit records",
            ScenarioName, options.CycleId, summaries.Count, refundTotal, traceRecords.Count);
        return Finish(true, "", null, "");
    }
}
