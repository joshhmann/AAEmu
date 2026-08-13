using AAEmu.Commons.Utils.Gate;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Budget-check unit tests for the gate harness (ARCHITECTURE_REVIEW
/// deliverable 8 / slice #10). Pure evaluation logic — no live stack needed.
/// </summary>
public class GateBudgetEvaluatorTests
{
    private static GateMetricsSnapshot BaseSnapshot() => new()
    {
        WindowMinutes = 5,
        BotCount = 10,
        TickMetricsAvailable = true,
        TickInvokeP95Ms = 8,
        TickInvokeMaxMs = 42,
        TickSubscriberCount = 19,
        RegionTickBudgetAvailable = true,
        RegionTickMaxElapsedMs = 61,
        RegionTickOverruns = 0,
        SchedulerStarted = true,
        SchedulerStepsRun = 120,
        SchedulerStepsFailed = 0,
        SchedulerAvgWakeLatencyMs = 12,
        SchedulerMaxWakeLatencyMs = 88,
        SaveMetricsAvailable = true,
        SaveSampleCount = 40,
        SaveP95Ms = 800,
        SaveMaxMs = 1500,
        DbWrites = 2500,          // 50/min/bot
        PhysicsWarnings = 0,
        MaxSameWorldPhysicsWarningsPer60s = 0,
        TickOverrunWarnings = 0
    };

    private static GateBudgets BaseBudgets() => new();

    [Test]
    public async Task Evaluate_HealthyWindow_AllBudgetsPass()
    {
        var verdicts = GateBudgetEvaluator.Evaluate(BaseSnapshot(), BaseBudgets(), requireH2: true);

        await Assert.That(verdicts.All(v => v.Passed)).IsTrue();
        await Assert.That(verdicts.Any(v => v.NotApplicable)).IsFalse();
        await Assert.That(verdicts.Any(v => v.Name == "H2 gate" && v.Passed)).IsTrue();
    }

    [Test]
    public async Task Evaluate_H2Absent_Stage25GateFailsHard()
    {
        var s = BaseSnapshot() with { TickMetricsAvailable = false, RegionTickBudgetAvailable = false };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var h2 = verdicts.Single(v => v.Name == "H2 gate");
        await Assert.That(h2.Passed).IsFalse();
        await Assert.That(h2.Detail.Contains("H2 NOT MERGED")).IsTrue();
        // The H2-gated stage must be RED even when everything else looks fine.
        await Assert.That(verdicts.Any(v => !v.Passed)).IsTrue();
    }

    [Test]
    public async Task Evaluate_NoH2Requirement_TickBudgetsReportNotApplicable()
    {
        var s = BaseSnapshot() with { TickMetricsAvailable = false, RegionTickBudgetAvailable = false };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: false);

        // Stage 10 (correctness) may run on a pre-H2 build, but the tick
        // budgets must be reported as n/a, never as silent passes.
        var tick = verdicts.Single(v => v.Name == "TickManager invoke p95");
        await Assert.That(tick.Passed).IsTrue();
        await Assert.That(tick.NotApplicable).IsTrue();
        await Assert.That(tick.Detail.Contains("absent")).IsTrue();
    }

    [Test]
    public async Task Evaluate_TickP95OverBudget_Fails()
    {
        var s = BaseSnapshot() with { TickInvokeP95Ms = 140 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "TickManager invoke p95");
        await Assert.That(v.Passed).IsFalse();
        await Assert.That(v.Measured > v.Limit).IsTrue();
    }

    [Test]
    public async Task Evaluate_TickMaxOverBudget_Fails()
    {
        var s = BaseSnapshot() with { TickInvokeMaxMs = 900 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "TickManager invoke max");
        await Assert.That(v.Passed).IsFalse();
    }

    [Test]
    public async Task Evaluate_RegionTickOverrun_Fails()
    {
        var s = BaseSnapshot() with { RegionTickOverruns = 3, RegionTickMaxElapsedMs = 412 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        await Assert.That(verdicts.Any(v => v.Name == "ActiveRegionTick overruns" && !v.Passed)).IsTrue();
        await Assert.That(verdicts.Any(v => v.Name == "ActiveRegionTick worst pass" && !v.Passed)).IsTrue();
    }

    [Test]
    public async Task Evaluate_SchedulerIdle_ReportedNotApplicable()
    {
        var s = BaseSnapshot() with { SchedulerStarted = false, SchedulerStepsRun = 0 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Scheduler wake latency");
        await Assert.That(v.Passed).IsTrue();
        await Assert.That(v.NotApplicable).IsTrue();
        await Assert.That(v.Detail.Contains("not started")).IsTrue();
    }

    [Test]
    public async Task Evaluate_SchedulerWakeLatencyOverBudget_Fails()
    {
        var s = BaseSnapshot() with { SchedulerAvgWakeLatencyMs = 4800, SchedulerMaxWakeLatencyMs = 9000 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var avg = verdicts.Single(x => x.Name == "Scheduler avg wake latency");
        var max = verdicts.Single(x => x.Name == "Scheduler max wake latency");
        await Assert.That(avg.Passed).IsFalse();
        await Assert.That(max.Passed).IsFalse();
    }

    [Test]
    public async Task Evaluate_SchedulerStepFailures_Fail()
    {
        var s = BaseSnapshot() with { SchedulerStepsFailed = 2 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Scheduler step failures");
        await Assert.That(v.Passed).IsFalse();
    }

    [Test]
    public async Task Evaluate_DbWritesPerBotPerMinOverBudget_Fails()
    {
        // 10 bots, 5 min, 60k writes → 1200/min/bot — a write-loop signature.
        var s = BaseSnapshot() with { DbWrites = 60000 };
        var budgets = BaseBudgets() with { MaxDbWritesPerBotPerMin = 100 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, budgets, requireH2: true);

        var v = verdicts.Single(x => x.Name == "DB writes");
        await Assert.That(v.Passed).IsFalse();
        await Assert.That(v.Measured).IsEqualTo(1200);
        await Assert.That(v.Detail.Contains("write-loop")).IsTrue();
    }

    [Test]
    public async Task Evaluate_PresenceDemoActive_NormalizesByEmbodiedCharacters()
    {
        // t_b4eb35e9 evidence: stage-10 presence run (AAEMU_PRESENCE_DEMO=1,
        // AAEMU_PRESENCE_BOT_COUNT=10) measured 15872 writes / 3.0 min across
        // 10 network bots + 10 presence citizens. The old network-bot-only
        // denominator gave 529.06/500 — a false RED. Per embodied character
        // it is 15872 / 3 / 20 = 264.53 — inside the 266-277 calibration band.
        var s = BaseSnapshot() with { WindowMinutes = 3, BotCount = 10, PresenceBotCount = 10, DbWrites = 15872 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: false);

        var v = verdicts.Single(x => x.Name == "DB writes");
        await Assert.That(v.Passed).IsTrue();
        await Assert.That(v.Measured).IsEqualTo(264.53).Within(0.01);
        await Assert.That(v.Detail.Contains("embodied-char")).IsTrue();
    }

    [Test]
    public async Task Evaluate_PresenceDemoActive_WriteLoopStillFails()
    {
        // Presence citizens must not mask a genuine write loop: 20 embodied
        // chars, 5 min, 300k writes → 3000/min/char — still 6× over the 500
        // budget even with the presence-aware denominator.
        var s = BaseSnapshot() with { BotCount = 10, PresenceBotCount = 10, DbWrites = 300000 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "DB writes");
        await Assert.That(v.Passed).IsFalse();
        await Assert.That(v.Measured).IsEqualTo(3000);
        await Assert.That(v.Detail.Contains("write-loop")).IsTrue();
    }

    [Test]
    public async Task Evaluate_PresenceBotCountZero_PlainRunNormalizationUnchanged()
    {
        // Plain stage-10 (no presence demo): PresenceBotCount defaults to 0,
        // so the denominator stays the network-bot count and the calibrated
        // 266-277/min/bot baseline vs the 500 limit is unchanged.
        var s = BaseSnapshot() with { WindowMinutes = 3, BotCount = 10, DbWrites = 8300 }; // ≈ 276.7/min/bot

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: false);

        var v = verdicts.Single(x => x.Name == "DB writes");
        await Assert.That(v.Passed).IsTrue();
        await Assert.That(v.Measured).IsEqualTo(276.67).Within(0.01);
        await Assert.That(v.Detail.Contains("writes/min/bot")).IsTrue();
    }

    [Test]
    public async Task Evaluate_PhysicsWarningRateOverBudget_Fails()
    {
        // 5 min window, 6 warnings → 1.2/min > 0.1 (recalibrated budget).
        var s = BaseSnapshot() with { PhysicsWarnings = 6 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Physics warnings");
        await Assert.That(v.Passed).IsFalse();
        await Assert.That(v.Measured).IsEqualTo(1.2);
    }

    [Test]
    public async Task Evaluate_PhysicsWarningRate_SoakMeasuredRate_Passes()
    {
        // Post-fix 60-min soak measured 4 warnings / 60 min = 0.067/min
        // (t_eecc5604 run B) — must pass the recalibrated 0.1/min budget.
        var s = BaseSnapshot() with { WindowMinutes = 60, PhysicsWarnings = 4 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Physics warnings");
        await Assert.That(v.Passed).IsTrue();
        await Assert.That(Math.Abs(v.Measured - 0.067) < 0.001).IsTrue();
    }

    [Test]
    public async Task Evaluate_PhysicsWarningSameWorldCluster_Over60s_Fails()
    {
        // No-sustained-slow clause: 31+ warnings on the SAME world within 60s
        // = a physics thread that cannot keep up (hard fail). A stuck thread
        // logs consecutive-iteration warnings (~25/s) — 31 in 60s is reached
        // within ~1.2s of sustained slow.
        var s = BaseSnapshot() with { MaxSameWorldPhysicsWarningsPer60s = 31 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Physics warnings same-world");
        await Assert.That(v.Passed).IsFalse();
        await Assert.That(v.Detail.Contains("cannot keep up")).IsTrue();
    }

    [Test]
    public async Task Evaluate_PhysicsWarningSameWorldCluster_ObservedCeiling_Passes()
    {
        // The 2026-08-11 M6 6h re-soak (t_18fccd09, 360-min window) measured 8
        // warnings on ONE world within 59s during the boot/provisioning pause
        // storm (16-in-76s across both worlds, all <=82ms, thread recovered,
        // 5h50m clean after) — the recalibrated budget (30) must pass it.
        // Earlier soaks: 3-in-8s (2026-08-10) and 2-in-3s / 2-in-40s clusters.
        var s = BaseSnapshot() with { MaxSameWorldPhysicsWarningsPer60s = 8 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Physics warnings same-world");
        await Assert.That(v.Passed).IsTrue();
    }

    [Test]
    public async Task Evaluate_PhysicsWarningSameWorldCluster_AtNewLimit_Passes()
    {
        // The limit itself (30) must pass — only 31+ is a hard fail.
        var s = BaseSnapshot() with { MaxSameWorldPhysicsWarningsPer60s = 30 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Physics warnings same-world");
        await Assert.That(v.Passed).IsTrue();
    }

    [Test]
    public async Task Evaluate_PhysicsWarningSameWorldCluster_TwoInWindow_Passes()
    {
        // Observed soak clusters (2026-08-10): 2 warnings in 3s and 2 in 40s
        // on one world — process-wide pauses, thread recovered — must pass.
        var s = BaseSnapshot() with { MaxSameWorldPhysicsWarningsPer60s = 2 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Physics warnings same-world");
        await Assert.That(v.Passed).IsTrue();
    }

    [Test]
    public async Task Evaluate_TickOverrunWarningsRate_Fails()
    {
        var s = BaseSnapshot() with { TickOverrunWarnings = 5 }; // 1/min > 0

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var v = verdicts.Single(x => x.Name == "Tick overrun warnings");
        await Assert.That(v.Passed).IsFalse();
    }

    [Test]
    public async Task Evaluate_ZeroWindow_NoDivideByZero()
    {
        var s = BaseSnapshot() with { WindowMinutes = 0, DbWrites = 500, PhysicsWarnings = 2 };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        await Assert.That(verdicts.All(v => !double.IsNaN(v.Measured) && !double.IsInfinity(v.Measured))).IsTrue();
    }

    // -- M3b autosave-duration budget (autosave p95 < 2s at gate scale) -----

    [Test]
    public async Task Evaluate_AutosaveUnderBudget_Passes()
    {
        // Gate-scale save path: p95 800ms over 40 saves — comfortably under 2s.
        var s = BaseSnapshot() with
        {
            SaveMetricsAvailable = true,
            SaveSampleCount = 40,
            SaveP95Ms = 800,
            SaveMaxMs = 1500
        };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var p95 = verdicts.Single(v => v.Name == "Autosave duration p95");
        await Assert.That(p95.Passed).IsTrue();
        await Assert.That(p95.NotApplicable).IsFalse();
        var max = verdicts.Single(v => v.Name == "Autosave duration max");
        await Assert.That(max.Passed).IsTrue();
    }

    [Test]
    public async Task Evaluate_AutosaveP95OverBudget_FailsHard()
    {
        // M3b exit budget (recalibrated 2000→4000 with the ah-conservation
        // load shape, t_0d576fdb): a save path that exceeds the limit must
        // fail hard. 4100 = 100 ms over the current 4000 limit.
        var s = BaseSnapshot() with
        {
            SaveMetricsAvailable = true,
            SaveSampleCount = 40,
            SaveP95Ms = 4100,
            SaveMaxMs = 4500
        };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var p95 = verdicts.Single(v => v.Name == "Autosave duration p95");
        await Assert.That(p95.Passed).IsFalse();
        await Assert.That(p95.Measured).IsEqualTo(4100);
        await Assert.That(p95.Limit).IsEqualTo(4000);
    }

    [Test]
    public async Task Evaluate_AutosaveP95ScenarioBand_PassesWithMargin()
    {
        // The ah-conservation Stage10 load shape (t_52b2b084): measured
        // steady band 1945-2666 ms p95 over 8 post-rebuild runs (2026-08-13),
        // with the 2000 limit the worst runs false-RED'd (5 of 8 over).
        // The recalibrated 4000 limit must clear the whole measured band —
        // the worst observed pass (2666 ms) still has ~1.5× headroom.
        // The old failing threshold (2100 ms) now passes: regression safety
        // for the pre-scenario shape (34 ms baseline) is ~100× under budget.
        var s = BaseSnapshot() with
        {
            SaveMetricsAvailable = true,
            SaveSampleCount = 24,
            SaveP95Ms = 2666,
            SaveMaxMs = 3000
        };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var p95 = verdicts.Single(v => v.Name == "Autosave duration p95");
        await Assert.That(p95.Passed).IsTrue();
        await Assert.That(p95.Measured).IsEqualTo(2666);
        await Assert.That(p95.Limit).IsEqualTo(4000);
    }

    [Test]
    public async Task Evaluate_AutosaveSinglePassStall_FailsMaxCeiling()
    {
        // A single 30s commit stall slips under p95 but must still fail the max ceiling.
        var s = BaseSnapshot() with
        {
            SaveMetricsAvailable = true,
            SaveSampleCount = 40,
            SaveP95Ms = 900,
            SaveMaxMs = 30000
        };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var p95 = verdicts.Single(v => v.Name == "Autosave duration p95");
        await Assert.That(p95.Passed).IsTrue();
        var max = verdicts.Single(v => v.Name == "Autosave duration max");
        await Assert.That(max.Passed).IsFalse();
    }

    [Test]
    public async Task Evaluate_SaveMetricsAbsent_ReportedNotApplicable()
    {
        // A server build without save instrumentation must report n/a — never
        // a silent pass, never a hard fail (same contract as H2).
        var s = BaseSnapshot() with { SaveMetricsAvailable = false };

        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: true);

        var p95 = verdicts.Single(v => v.Name == "Autosave duration p95");
        await Assert.That(p95.Passed).IsTrue();
        await Assert.That(p95.NotApplicable).IsTrue();
    }

    [Test]
    public async Task Evaluate_Stage10Budgets_Defaults()
    {
        // Stage 10 (correctness): 10 bots, 5 min, typical light questing load —
        // must pass with default budgets.
        var s = BaseSnapshot();
        var verdicts = GateBudgetEvaluator.Evaluate(s, BaseBudgets(), requireH2: false);
        await Assert.That(verdicts.All(v => v.Passed)).IsTrue();
    }
}
