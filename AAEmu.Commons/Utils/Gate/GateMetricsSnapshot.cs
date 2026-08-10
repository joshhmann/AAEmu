using System.Text.Json.Serialization;

namespace AAEmu.Commons.Utils.Gate;

/// <summary>
/// Immutable metrics snapshot collected by the gate harness for one stage
/// window. Pure data — no game dependencies, so the budget evaluator is
/// unit-testable from AAEmu.UnitTests without a live stack.
/// </summary>
public sealed record GateMetricsSnapshot
{
    /// <summary>Window length in minutes (wall clock, used to normalize rates).</summary>
    public double WindowMinutes { get; init; }

    /// <summary>Bots embodied in this stage (rate budgets normalize per bot).</summary>
    public int BotCount { get; init; }

    /// <summary>
    /// Scheduler-stepping presence-demo citizens additionally embodied in the
    /// server (AAEMU_PRESENCE_DEMO=1 — BotPresenceCoordinator). They write to
    /// the DB at the same save cadence as network bots, so the DB-write budget
    /// normalizes by <see cref="EmbodiedCharacterCount"/> when they are active
    /// (t_b4eb35e9: stage-10 presence run measured 529.06/min/bot on the old
    /// network-bot-only denominator — a false RED; the same writes are
    /// 264.53/min/embodied-char, inside the 266-277 calibration band). 0 = the
    /// presence demo is not active and plain per-bot normalization applies.
    /// </summary>
    public int PresenceBotCount { get; init; }

    // -- TickManager (H2 metrics surface) --
    public double TickInvokeP95Ms { get; init; }
    public double TickInvokeMaxMs { get; init; }
    public int TickSubscriberCount { get; init; }
    /// <summary>True when the running server exposes H2 tick metrics (the stage-25 gate).</summary>
    public bool TickMetricsAvailable { get; init; }

    // -- ActiveRegionTick (H2 budget surface) --
    public bool RegionTickBudgetAvailable { get; init; }
    /// <summary>Worst ActiveRegionTick pass elapsed ms observed in the window.</summary>
    public double RegionTickMaxElapsedMs { get; init; }
    /// <summary>Count of ActiveRegionTick passes that overran the 100ms budget.</summary>
    public long RegionTickOverruns { get; init; }

    // -- PlayerBotScheduler --
    public bool SchedulerStarted { get; init; }
    public long SchedulerStepsRun { get; init; }
    public long SchedulerStepsFailed { get; init; }
    public double SchedulerAvgWakeLatencyMs { get; init; }
    public double SchedulerMaxWakeLatencyMs { get; init; }

    // -- DB pressure (MySQL Com_* delta over the window) --
    public long DbWrites { get; init; }

    // -- Physics warning rate (game log scan over the window) --
    public long PhysicsWarnings { get; init; }

    /// <summary>Most warnings on a single world within any 60s window (no-sustained-slow clause).</summary>
    public long MaxSameWorldPhysicsWarningsPer60s { get; init; }

    // -- Tick overrun warnings (game log scan over the window) --
    public long TickOverrunWarnings { get; init; }

    // -- Convenience rates --
    /// <summary>Total embodied characters the DB-write rate normalizes by (network bots + presence citizens).</summary>
    [JsonIgnore]
    public int EmbodiedCharacterCount => BotCount + PresenceBotCount;
    [JsonIgnore]
    public double DbWritesPerMin => WindowMinutes > 0 ? DbWrites / WindowMinutes : 0;
    [JsonIgnore]
    public double DbWritesPerBotPerMin => WindowMinutes > 0 && EmbodiedCharacterCount > 0 ? DbWrites / WindowMinutes / EmbodiedCharacterCount : 0;
    [JsonIgnore]
    public double PhysicsWarningsPerMin => WindowMinutes > 0 ? PhysicsWarnings / WindowMinutes : 0;
    [JsonIgnore]
    public double TickOverrunWarningsPerMin => WindowMinutes > 0 ? TickOverrunWarnings / WindowMinutes : 0;
}
