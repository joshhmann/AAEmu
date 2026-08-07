namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Immutable server-load sample consumed by the pressure classifier
/// (spec §14, ARCHITECTURE_REVIEW deliverable 9 — observability probes).
///
/// Fields whose probes are not yet implemented are nullable and read as
/// "no signal" by the classifier (stub cleanly if not yet present, per the
/// card). The scheduler metrics (deliverable 9 item 4) ARE available from
/// slice #6 and are the primary driver today.
/// </summary>
/// <param name="WorkerUtilization">Scheduler worker pool busy ratio (0..1).</param>
/// <param name="DueQueueDepth">Bots waiting in the due-time queue.</param>
/// <param name="EventQueueDepth">Unconsumed wake events.</param>
/// <param name="InFlight">Bots leased (queued on the work channel or running).</param>
/// <param name="AverageWakeLatencyMs">Mean wake→start latency.</param>
/// <param name="EmbodiedCount">Active (embodied) bots in the registry.</param>
/// <param name="TickDurationP95Ms">TickManager invoke p95 — probe not yet landed (null = no signal).</param>
/// <param name="RegionTickDurationMs">ActiveRegionTick duration — probe not yet landed (null = no signal).</param>
public sealed record PressureSample(
    double WorkerUtilization,
    int DueQueueDepth,
    int EventQueueDepth,
    int InFlight,
    double AverageWakeLatencyMs,
    int EmbodiedCount,
    double? TickDurationP95Ms = null,
    double? RegionTickDurationMs = null)
{
    /// <summary>An empty sample: zero load everywhere, no optional signals.</summary>
    public static PressureSample Empty { get; } = new(0d, 0, 0, 0, 0d, 0, null, null);
}
