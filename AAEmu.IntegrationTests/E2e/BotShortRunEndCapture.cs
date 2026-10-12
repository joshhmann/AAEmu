using System.Text.Json;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Final preparation for the isolated single-bot observer. Does not credit
/// gameplay, save the character, release it, or clear earlier evidence faults.
/// </summary>
internal static class BotShortRunEndCapture
{
    internal sealed record Result(Guid StopTraceId, JsonElement Scheduler, JsonElement Stop, JsonElement Copy);

    internal static async Task<Result> CaptureAsync(uint characterId, uint actorId, long generation,
        Func<Task<JsonElement>> stopScheduler, Func<Task<JsonElement>> enqueueStop,
        Func<Guid, Task<JsonElement>> pollStop, Func<Task<JsonElement>> capture,
        CancellationToken cancellationToken)
    {
        Require(characterId != 0 && actorId != 0 && generation >= 0, "Owned END identity unavailable");
        var scheduler = await stopScheduler().WaitAsync(cancellationToken);
        Require(Bool(scheduler, "privateEndPreparation") == true && Bool(scheduler, "schedulerRunning") == false
            && UInt(scheduler, "id") == characterId && UInt(scheduler, "actorObjId") == actorId
            && Long(scheduler, "enrollmentGeneration") == generation, "Isolated scheduler stop not proved");
        var queued = await enqueueStop().WaitAsync(cancellationToken);
        Require(Bool(queued, "success") == true && Text(queued, "action") == "Stop"
            && Guid.TryParse(Text(queued, "trace_id"), out _), "Ordinary Stop enqueue not proved");
        var stopId = Guid.Parse(Text(queued, "trace_id")!);
        Require(stopId != Guid.Empty, "Ordinary Stop trace empty");
        JsonElement stop;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stop = await pollStop(stopId).WaitAsync(cancellationToken);
            Require(Text(stop, "trace_id") == stopId.ToString("D") && Text(stop, "action") == "Stop",
                "Ordinary Stop poll identity changed");
            var state = Text(stop, "state");
            if (state == "Completed") break;
            Require(state is "Requested" or "Accepted" or "Running", "Ordinary Stop failed");
            await Task.Delay(100, cancellationToken);
        }
        Require(UInt(stop, "actor_id") == actorId && Time(stop, "completed_at") is { },
            "Owned ordinary Stop terminal unavailable");
        var copy = await capture().WaitAsync(cancellationToken);
        Require(UInt(copy, "id") == characterId && UInt(copy, "actorCharId") == characterId
            && UInt(copy, "actorObjId") == actorId && Long(copy, "enrollmentGeneration") == generation
            && Bool(copy, "enrollmentRegistered") == true && Bool(copy, "enrollmentSchedulerRunning") == false,
            "Prepared END enrollment changed");
        Require(Bool(copy, "endPreparationIdle") == true && Text(copy, "liveState") is null or "Completed" or "Interrupted" or "Rejected" or "TimedOut",
            "Prepared END still has running work");
        Require(Time(copy, "snapshotStartedAtUtc") is { } begin && begin >= Time(stop, "completed_at")
            && Time(copy, "observedAtUtc") is { } end && end >= begin, "Prepared END precedes Stop terminal");
        return new(stopId, scheduler, stop, copy);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static uint? UInt(JsonElement value, string name) => value.TryGetProperty(name, out var v)
        && v.TryGetUInt32(out var n) ? n : null;
    private static long? Long(JsonElement value, string name) => value.TryGetProperty(name, out var v)
        && v.TryGetInt64(out var n) ? n : null;
    private static bool? Bool(JsonElement value, string name) => value.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static DateTime? Time(JsonElement value, string name) => Text(value, name) is { } s
        && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
}
