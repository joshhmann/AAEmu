#nullable enable

using NLog;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

public enum GoapTelemetryEventType
{
    GoalSelected,
    PlanGenerated,
    PlanTemplateHit,
    ActionStarted,
    ActionRunning,
    ActionSucceeded,
    ActionFailed,
    ActionInvalidated,
    InterruptTriggered,
    PrimaryIntentResumed,
    ReplanTriggered,
    GoalSatisfied,
    GoalAbandoned
}

/// <summary>
/// Structured telemetry event answering:
/// - Why did this bot choose this goal?
/// - Why did this action fail or invalidate?
/// - Why did it replan or interrupt?
/// - Why was the primary intent resumed or abandoned?
/// </summary>
public sealed record GoapTelemetryEvent(
    uint CharacterId,
    GoapTelemetryEventType EventType,
    string GoalName,
    string? ActionName,
    string Reason,
    ulong StateFlags,
    DateTime TimestampUtc,
    object? Metadata = null);

public interface IGoapTelemetrySink
{
    void Record(GoapTelemetryEvent telemetryEvent);
    IReadOnlyList<GoapTelemetryEvent> Events { get; }
}

public sealed class InMemoryGoapTelemetrySink : IGoapTelemetrySink
{
    private readonly List<GoapTelemetryEvent> _events = [];
    private readonly object _lock = new();

    public IReadOnlyList<GoapTelemetryEvent> Events
    {
        get
        {
            lock (_lock)
                return [.. _events];
        }
    }

    public void Record(GoapTelemetryEvent telemetryEvent)
    {
        lock (_lock)
            _events.Add(telemetryEvent);
    }

    public void Clear()
    {
        lock (_lock)
            _events.Clear();
    }
}
