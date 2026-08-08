namespace AAEmu.Commons.Utils.Gate;

/// <summary>
/// Numeric budgets for one gate stage. Every budget is a hard fail when
/// exceeded — the gate runner asserts these after every stage window and the
/// stage is RED on the first overrun. Budgets are per-stage (density stages
/// get progressively tighter DB/physics tolerance).
/// </summary>
public sealed record GateBudgets
{
    /// <summary>TickManager invoke p95 must stay under the tick loop's own warn threshold (100ms).</summary>
    public double TickP95Ms { get; init; } = 100;

    /// <summary>Hard ceiling for a single TickManager invoke pass.</summary>
    public double TickMaxMs { get; init; } = 250;

    /// <summary>ActiveRegionTick worst pass: the H2 hard budget (100ms).</summary>
    public double RegionTickMaxElapsedMs { get; init; } = 100;

    /// <summary>Zero ActiveRegionTick overruns allowed in any stage window.</summary>
    public long MaxRegionTickOverruns { get; init; } = 0;

    /// <summary>Mean scheduler wake→start latency (ms). Enforced when the scheduler ran steps.</summary>
    public double SchedulerAvgWakeLatencyMs { get; init; } = 250;

    /// <summary>Worst scheduler wake→start latency (ms).</summary>
    public double SchedulerMaxWakeLatencyMs { get; init; } = 1000;

    /// <summary>Failed scheduler steps allowed in a window (any failure is a red flag).</summary>
    public long MaxSchedulerStepFailures { get; init; } = 0;

    /// <summary>Max DB writes per minute per embodied bot (catch AI-step-loop writes).
    /// Calibrated to the E2E rig's AutoSaveInterval 0.2 (12s saves): measured
    /// 277 writes/min/bot on the stage-10 golden-route run (2026-08-08), so 500
    /// gives ~2× headroom while a step-loop (per-step writes) lands 10-100×
    /// above. Prod cadence (AutoSaveInterval 5.0) is ~10× lower — tighten at
    /// the 100-bot profiling stage, not before.</summary>
    public double MaxDbWritesPerBotPerMin { get; init; } = 500;

    /// <summary>Max physics warnings per minute (physics thread running slow = overload signal).</summary>
    public double MaxPhysicsWarningsPerMin { get; init; } = 0;

    /// <summary>Max tick-overrun warnings per minute ("Tick took Xms" + ActiveRegionTick overruns).</summary>
    public double MaxTickOverrunWarningsPerMin { get; init; } = 0;
}

/// <summary>
/// One budget check verdict. <see cref="NotApplicable"/> marks checks whose
/// instrumentation is absent on the running server (e.g. H2 tick metrics) or
/// whose system was not exercised in the window (scheduler idle) — those are
/// reported, never silently skipped, and the stage gates on them explicitly.
/// </summary>
public sealed record BudgetVerdict(
    string Name,
    double Measured,
    double Limit,
    bool Passed,
    string Detail,
    bool NotApplicable = false)
{
    public static BudgetVerdict Ok(string name, double measured, double limit, string detail)
        => new(name, measured, limit, true, detail);

    public static BudgetVerdict Over(string name, double measured, double limit, string detail)
        => new(name, measured, limit, false, detail);

    public static BudgetVerdict Nx(string name, double measured, double limit, string detail)
        => new(name, measured, limit, true, detail, NotApplicable: true);
}

/// <summary>
/// Pure budget evaluation for the gate harness (ARCHITECTURE_REVIEW
/// deliverable 8 + deliverable 10 slice 10). No game or test dependencies —
/// snapshot in, verdicts out, fully unit-testable.
///
/// Rules:
///   - H2 gate: stages that require H2 fail hard when tick metrics are absent.
///   - Tick/region budgets are hard fails on any overrun.
///   - Scheduler budgets are enforced when steps ran; reported as n/a when the
///     scheduler never started (no citizen path wired) — never a silent pass.
///   - DB writes normalize per bot per minute.
///   - Physics/tick-overrun warning rates are hard fails when over budget.
/// </summary>
public static class GateBudgetEvaluator
{
    public static IReadOnlyList<BudgetVerdict> Evaluate(GateMetricsSnapshot s, GateBudgets b, bool requireH2)
    {
        var verdicts = new List<BudgetVerdict>();

        if (requireH2)
        {
            if (!s.TickMetricsAvailable)
            {
                verdicts.Add(new BudgetVerdict("H2 gate", 0, 0, false,
                    "H2 NOT MERGED: server build lacks TickManager duration metrics / ActiveRegionTick budget — " +
                    "first stability gate (25 bots) hard-stops until fix/h2-activeregiontick-budget lands"));
            }
            else
            {
                verdicts.Add(BudgetVerdict.Ok("H2 gate", 1, 1, "H2 present: tick metrics + region budget available"));
            }
        }

        // TickManager duration budgets.
        if (s.TickMetricsAvailable)
        {
            verdicts.Add(s.TickInvokeP95Ms <= b.TickP95Ms
                ? BudgetVerdict.Ok("TickManager invoke p95", s.TickInvokeP95Ms, b.TickP95Ms, "ms")
                : BudgetVerdict.Over("TickManager invoke p95", s.TickInvokeP95Ms, b.TickP95Ms, "ms — tick loop starvation risk"));
            verdicts.Add(s.TickInvokeMaxMs <= b.TickMaxMs
                ? BudgetVerdict.Ok("TickManager invoke max", s.TickInvokeMaxMs, b.TickMaxMs, "ms")
                : BudgetVerdict.Over("TickManager invoke max", s.TickInvokeMaxMs, b.TickMaxMs, "ms — single-pass overrun"));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("TickManager invoke p95", 0, b.TickP95Ms, "tick metrics absent on server"));
        }

        // ActiveRegionTick budget (H2).
        if (s.RegionTickBudgetAvailable)
        {
            verdicts.Add(s.RegionTickMaxElapsedMs <= b.RegionTickMaxElapsedMs
                ? BudgetVerdict.Ok("ActiveRegionTick worst pass", s.RegionTickMaxElapsedMs, b.RegionTickMaxElapsedMs, "ms")
                : BudgetVerdict.Over("ActiveRegionTick worst pass", s.RegionTickMaxElapsedMs, b.RegionTickMaxElapsedMs, "ms — over 100ms budget"));
            verdicts.Add(s.RegionTickOverruns <= b.MaxRegionTickOverruns
                ? BudgetVerdict.Ok("ActiveRegionTick overruns", s.RegionTickOverruns, b.MaxRegionTickOverruns, "passes over budget")
                : BudgetVerdict.Over("ActiveRegionTick overruns", s.RegionTickOverruns, b.MaxRegionTickOverruns, "passes over budget"));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("ActiveRegionTick worst pass", 0, b.RegionTickMaxElapsedMs, "region tick stats absent on server"));
        }

        // Scheduler budgets — enforced only when the scheduler actually ran steps.
        if (s.SchedulerStarted && s.SchedulerStepsRun > 0)
        {
            verdicts.Add(s.SchedulerAvgWakeLatencyMs <= b.SchedulerAvgWakeLatencyMs
                ? BudgetVerdict.Ok("Scheduler avg wake latency", s.SchedulerAvgWakeLatencyMs, b.SchedulerAvgWakeLatencyMs, "ms")
                : BudgetVerdict.Over("Scheduler avg wake latency", s.SchedulerAvgWakeLatencyMs, b.SchedulerAvgWakeLatencyMs, "ms"));
            verdicts.Add(s.SchedulerMaxWakeLatencyMs <= b.SchedulerMaxWakeLatencyMs
                ? BudgetVerdict.Ok("Scheduler max wake latency", s.SchedulerMaxWakeLatencyMs, b.SchedulerMaxWakeLatencyMs, "ms")
                : BudgetVerdict.Over("Scheduler max wake latency", s.SchedulerMaxWakeLatencyMs, b.SchedulerMaxWakeLatencyMs, "ms"));
            verdicts.Add(s.SchedulerStepsFailed <= b.MaxSchedulerStepFailures
                ? BudgetVerdict.Ok("Scheduler step failures", s.SchedulerStepsFailed, b.MaxSchedulerStepFailures, "steps")
                : BudgetVerdict.Over("Scheduler step failures", s.SchedulerStepsFailed, b.MaxSchedulerStepFailures, "steps threw"));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("Scheduler wake latency", 0, b.SchedulerAvgWakeLatencyMs,
                "scheduler not started / no steps in window (citizen path not wired) — budget not exercisable"));
        }

        // DB pressure: normalized per bot per minute.
        verdicts.Add(s.DbWritesPerBotPerMin <= b.MaxDbWritesPerBotPerMin
            ? BudgetVerdict.Ok("DB writes", s.DbWritesPerBotPerMin, b.MaxDbWritesPerBotPerMin, "writes/min/bot")
            : BudgetVerdict.Over("DB writes", s.DbWritesPerBotPerMin, b.MaxDbWritesPerBotPerMin, "writes/min/bot — write-loop risk"));

        // Warning rates from the game log.
        verdicts.Add(s.PhysicsWarningsPerMin <= b.MaxPhysicsWarningsPerMin
            ? BudgetVerdict.Ok("Physics warnings", s.PhysicsWarningsPerMin, b.MaxPhysicsWarningsPerMin, "warnings/min")
            : BudgetVerdict.Over("Physics warnings", s.PhysicsWarningsPerMin, b.MaxPhysicsWarningsPerMin, "warnings/min — physics thread running slow"));
        verdicts.Add(s.TickOverrunWarningsPerMin <= b.MaxTickOverrunWarningsPerMin
            ? BudgetVerdict.Ok("Tick overrun warnings", s.TickOverrunWarningsPerMin, b.MaxTickOverrunWarningsPerMin, "warnings/min")
            : BudgetVerdict.Over("Tick overrun warnings", s.TickOverrunWarningsPerMin, b.MaxTickOverrunWarningsPerMin, "warnings/min — world tick over budget"));

        return verdicts;
    }
}
