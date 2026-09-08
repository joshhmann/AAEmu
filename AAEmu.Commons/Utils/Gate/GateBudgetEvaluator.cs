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

    /// <summary>Max DB writes per minute per embodied character (catch AI-step-loop writes).
    /// Calibrated to the E2E rig's AutoSaveInterval 0.2 (12s saves): measured
    /// 277 writes/min/bot on the stage-10 golden-route run (2026-08-08), so 500
    /// gives ~2× headroom while a step-loop (per-step writes) lands 10-100×
    /// above. Prod cadence (AutoSaveInterval 5.0) is ~10× lower — tighten at
    /// the 100-bot profiling stage, not before.
    /// The denominator is the snapshot's embodied-character count, not the
    /// stage's network-bot count: when the presence demo is active
    /// (AAEMU_PRESENCE_DEMO=1), BotPresenceCoordinator adds scheduler-stepping
    /// citizens that persist at the SAME save cadence, so their writes are
    /// normal, load — not a write loop. Without the presence-aware denominator
    /// a stage-10 presence run false-REDs (measured 529.06/500 on network bots
    /// only; 264.53/500 per embodied char — t_b4eb35e9, 2026-08-09).</summary>
    public double MaxDbWritesPerBotPerMin { get; init; } = 500;

    /// <summary>Max physics warnings per minute (physics thread running slow = overload signal).
    /// Calibrated to the 2026-08-10 soak measurements (t_eecc5604 RCA): the detector is
    /// upstream stock (PR #1253) measuring the WALL-CLOCK inter-iteration gap on a physics
    /// thread that sleeps ~40ms and steps a zero-body world — any >65ms deschedule (GC pause,
    /// host CPU contention, timer jitter) trips it regardless of physics load. Measured 0.031/min
    /// on the 6h soak (11 warnings, pre-GC-fix) and 0.067/min post-fix (4 warnings / 60 min, one
    /// 3s background-GC burst) with 0 crash/disconnect/region-overrun in both windows, so 0.1
    /// gives ~1.5-3.3× headroom; a genuine overload fires 10-100× the measured rate and is
    /// still caught. The same-world 60s clause below catches sustained slow (a world that
    /// cannot keep up logs consecutive-iteration warnings). Tighten only when ship-heavy
    /// milestones make physics latency a real gate signal.</summary>
    public double MaxPhysicsWarningsPerMin { get; init; } = 0.1;

    /// <summary>No-sustained-slow clause: max warnings on the SAME world within any 60s window.
    /// The boot/provisioning phase of a 6h soak logs process-wide pause storms (GC STW + host
    /// jitter) on BOTH physics threads as they catch up: measured 3-in-8s (soak #1, 2026-08-10)
    /// and 8-in-59s per world / 16-in-76s across worlds (soak #2, 2026-08-11 — one 75s storm,
    /// all ≤82ms, thread recovered, 0 crash/disconnect/overrun, 5h50m clean after), so 30 gives
    /// ~3.75× headroom over the observed ceiling while a world whose physics thread genuinely
    /// cannot keep up logs consecutive-iteration warnings (~25/s) and trips within ~1.2s of
    /// sustained slow. The 0.1/min rate budget independently catches any stall &gt;90s
    /// (25/min ≫ 0.1/min). Hard fail at 31+.</summary>
    public long MaxPhysicsWarningsSameWorldPer60s { get; init; } = 30;

    /// <summary>Max tick-overrun warnings per minute ("Tick took Xms" + ActiveRegionTick overruns).</summary>
    public double MaxTickOverrunWarningsPerMin { get; init; } = 0;

    /// <summary>Autosave (SaveManager.DoSave) p95 duration in ms. M3b gate-scale
    /// budget: two homesteads + 25 embodied bots must autosave under budget so
    /// the save path can't kill M8-scale worlds later.
    /// Recalibrated 2000 → 4000 (2026-08-13, t_0d576fdb): the Stage10 load
    /// shape now includes the ah-conservation auction scenario (t_52b2b084),
    /// whose 25-actor fleet (characters/items/mail/lots) is forced through
    /// every save pass — the bridge `save` trigger dirties all houses and
    /// calls DoSave(true) (saveAllCharacters=true), and pass cost scales with
    /// in-world character state. Measured 8 post-rebuild Stage10 runs:
    /// steady band 1945–2666 ms p95 (543 ms when the fleet isn't in its
    /// active phase), 4 of 8 over the old 2000 limit. 4000 gives ~1.5×
    /// headroom over the worst measured pass; the plain (no-scenario) shape
    /// measured 547 ms (~7.3× margin; 34 ms is the pre-scenario baseline).
    /// AuctionManager.Save
    /// persists only dirty lots (REPLACE INTO) — not a write loop, so the
    /// cost is fleet state, not auction-write amplification.</summary>
    public double AutosaveP95Ms { get; init; } = 4000;

    /// <summary>Autosave worst single pass ceiling (ms). A one-off 30s commit
    /// stall would slip under p95 but still freeze the world tick — hard fail.</summary>
    public double AutosaveMaxMs { get; init; } = 10000;
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
/// Rules:
///   - H2 gate: stages that require H2 fail hard when tick metrics are absent.
///   - Tick/region budgets are hard fails on any overrun.
///   - Scheduler budgets are enforced when steps ran (totalStepsRun &gt; 0);
///     reported as n/a when the scheduler never stepped — never a silent pass.
///   - Physics budgets are enforced when physics telemetry sampled
///     (physics.available); reported as n/a without samples — never a PASS
///     on zero rows.
///   - DB writes normalize per bot per minute — enforced only with a scoped
///     instrumented counter (DbWritesAvailable); otherwise n/a, never PASS.
///   - Physics/tick-overrun warning rates count window deltas only (never
///     cumulative logs) outside warmup blinds, normalized by steady-state
///     minutes; warmup-blind exclusions are recorded on the snapshot.
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

        // DB pressure: normalized per embodied character per minute (network
        // bots + presence-demo citizens — both persist at the same save
        // cadence; presence citizens are load, not a write loop, t_b4eb35e9).
        // Gated on a SCOPED instrumented counter: the server-global SHOW
        // GLOBAL STATUS fallback includes setup/unrelated traffic and must
        // never feed verdicts — without it the budget reports n/a
        // (INVALID/unasserted, never PASS).
        var writesUnit = s.PresenceBotCount > 0 ? "writes/min/embodied-char" : "writes/min/bot";
        if (s.DbWritesAvailable)
        {
            verdicts.Add(s.DbWritesPerBotPerMin <= b.MaxDbWritesPerBotPerMin
                ? BudgetVerdict.Ok("DB writes", s.DbWritesPerBotPerMin, b.MaxDbWritesPerBotPerMin, writesUnit)
                : BudgetVerdict.Over("DB writes", s.DbWritesPerBotPerMin, b.MaxDbWritesPerBotPerMin, writesUnit + " — write-loop risk"));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("DB writes", 0, b.MaxDbWritesPerBotPerMin,
                "DB write-volume source is server-global / unscoped — INVALID, not asserted until the scoped instrumented counter lands"));
        }

        // Autosave duration (M3b gate-scale budget): p95 < 2s, hard max ceiling.
        if (s.SaveMetricsAvailable)
        {
            verdicts.Add(s.SaveP95Ms <= b.AutosaveP95Ms
                ? BudgetVerdict.Ok("Autosave duration p95", s.SaveP95Ms, b.AutosaveP95Ms, $"ms over {s.SaveSampleCount} saves")
                : BudgetVerdict.Over("Autosave duration p95", s.SaveP95Ms, b.AutosaveP95Ms, $"ms over {s.SaveSampleCount} saves — save path too slow at gate scale"));
            verdicts.Add(s.SaveMaxMs <= b.AutosaveMaxMs
                ? BudgetVerdict.Ok("Autosave duration max", s.SaveMaxMs, b.AutosaveMaxMs, "ms worst pass")
                : BudgetVerdict.Over("Autosave duration max", s.SaveMaxMs, b.AutosaveMaxMs, "ms worst pass — single-pass stall"));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("Autosave duration p95", 0, b.AutosaveP95Ms, "save metrics absent on server"));
        }

        // Warning rates from the game log (window deltas only — never
        // cumulative logs; warmup-blind lines are recorded on the snapshot,
        // never counted here). Physics verdicts require sampled telemetry
        // (gate-the-gates): without physics.available they report n/a, never
        // PASS on zero rows.
        var physicsWarmupNote = s.WarmupExcludedPhysicsWarnings > 0
            ? $" (+{s.WarmupExcludedPhysicsWarnings} warmup-blind excluded, not counted)"
            : "";
        if (s.PhysicsAvailable)
        {
            verdicts.Add(s.PhysicsWarningsPerMin <= b.MaxPhysicsWarningsPerMin
                ? BudgetVerdict.Ok("Physics warnings", s.PhysicsWarningsPerMin, b.MaxPhysicsWarningsPerMin, "warnings/min" + physicsWarmupNote)
                : BudgetVerdict.Over("Physics warnings", s.PhysicsWarningsPerMin, b.MaxPhysicsWarningsPerMin, "warnings/min — physics thread running slow" + physicsWarmupNote));
            verdicts.Add(s.MaxSameWorldPhysicsWarningsPer60s <= b.MaxPhysicsWarningsSameWorldPer60s
                ? BudgetVerdict.Ok("Physics warnings same-world", s.MaxSameWorldPhysicsWarningsPer60s, b.MaxPhysicsWarningsSameWorldPer60s, "warnings in 60s on one world" + physicsWarmupNote)
                : BudgetVerdict.Over("Physics warnings same-world", s.MaxSameWorldPhysicsWarningsPer60s, b.MaxPhysicsWarningsSameWorldPer60s, "warnings in 60s on one world — physics thread cannot keep up (no-sustained-slow)" + physicsWarmupNote));
        }
        else
        {
            verdicts.Add(BudgetVerdict.Nx("Physics warnings", 0, b.MaxPhysicsWarningsPerMin,
                "physics telemetry absent / no samples in window — budget not exercisable" + physicsWarmupNote));
            verdicts.Add(BudgetVerdict.Nx("Physics warnings same-world", 0, b.MaxPhysicsWarningsSameWorldPer60s,
                "physics telemetry absent / no samples in window — budget not exercisable" + physicsWarmupNote));
        }
        var tickWarmupNote = s.WarmupExcludedTickOverruns > 0
            ? $" (+{s.WarmupExcludedTickOverruns} warmup-blind excluded, not counted)"
            : "";
        verdicts.Add(s.TickOverrunWarningsPerMin <= b.MaxTickOverrunWarningsPerMin
            ? BudgetVerdict.Ok("Tick overrun warnings", s.TickOverrunWarningsPerMin, b.MaxTickOverrunWarningsPerMin, "warnings/min" + tickWarmupNote)
            : BudgetVerdict.Over("Tick overrun warnings", s.TickOverrunWarningsPerMin, b.MaxTickOverrunWarningsPerMin, "warnings/min — world tick over budget" + tickWarmupNote));

        return verdicts;
    }
}
