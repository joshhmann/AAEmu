using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Default pressure probe (review deliverable 9 item 4): consumes the live
/// <see cref="IPlayerBotScheduler"/> metrics plus the registry's embodied
/// count. TickManager / ActiveRegionTick probes are not yet implemented —
/// those fields stay null (no signal) until H2 lands.
/// </summary>
public sealed class SchedulerPressureProbe : IPressureProbe
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IPlayerBotScheduler _scheduler;
    private readonly IPlayerBotManager _manager;

    public SchedulerPressureProbe(IPlayerBotScheduler scheduler, IPlayerBotManager manager)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <inheritdoc />
    public PressureSample Sample()
    {
        try
        {
            var m = _scheduler.GetMetrics();
            return new PressureSample(
                WorkerUtilization: m.WorkerUtilization,
                DueQueueDepth: m.DueQueueDepth,
                EventQueueDepth: m.EventQueueDepth,
                InFlight: m.InFlight,
                AverageWakeLatencyMs: m.AverageWakeLatencyMs,
                EmbodiedCount: _manager.ActiveCount,
                TickDurationP95Ms: null,      // H2 probe not landed yet
                RegionTickDurationMs: null);  // H2 probe not landed yet
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Pressure probe read failed — returning empty sample");
            return PressureSample.Empty;
        }
    }
}
