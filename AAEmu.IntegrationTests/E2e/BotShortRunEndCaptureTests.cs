using System.Text.Json;
using Xunit;

namespace AAEmu.IntegrationTests.E2e;

public class BotShortRunEndCaptureTests
{
    private static readonly Guid StopId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Scheduler() => Json(new { privateEndPreparation = true, schedulerRunning = true,
        endCaptureHeld = true, id = 7, actorObjId = 9, enrollmentGeneration = 3 });
    private static JsonElement Enqueued() => Json(new { success = true, action = "Stop", trace_id = StopId });
    private static JsonElement Terminal() => Json(new { action = "Stop", trace_id = StopId, actor_id = 9,
        state = "Completed", completed_at = At });
    private static JsonElement Copy(double x = 0) => Json(new { id = 7, actorCharId = 7, actorObjId = 9,
        enrollmentGeneration = 3, enrollmentRegistered = true, enrollmentSchedulerRunning = true, enrollmentLeased = true,
        endPreparationIdle = true, liveState = (string?)null, snapshotStartedAtUtc = At.AddSeconds(1),
        observedAtUtc = At.AddSeconds(2), x });
    private static JsonElement Change(JsonElement value, string name, object? replacement)
    {
        var fields = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        fields[name] = Json(replacement!);
        return Json(fields);
    }

    [Fact]
    public async Task MovingCharacter_FreezeBeforeStopDiffers_OrderedCapturePreservesExactPosition()
    {
        // Supplied moving fixture, not a live world. Reproduces the E157 order:
        // freezing first cannot conserve XYZ while movement continues until Stop.
        var position = 14996.6045;
        var oldEnd = Copy(position);
        var events = new List<string>();
        var polls = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await BotShortRunEndCapture.CaptureAsync(7, 9, 3,
            () => { events.Add("bot-held"); return Task.FromResult(Scheduler()); },
            () => { events.Add("stop-queued"); return Task.FromResult(Enqueued()); },
            _ =>
            {
                // A step already on the game thread may finish after the scan
                // stops. Only the later Stop terminal establishes the barrier.
                if (polls++ == 0)
                {
                    position -= 1.23;
                    return Task.FromResult(Change(Terminal(), "state", "Requested"));
                }
                events.Add("stop-completed");
                return Task.FromResult(Terminal());
            },
            () => { events.Add("full-live-copy"); return Task.FromResult(Copy(position)); }, deadline.Token);
        var ordinaryReleasePosition = position;
        Assert.NotEqual(oldEnd.GetProperty("x").GetDouble(), ordinaryReleasePosition);
        Assert.Equal(result.Copy.GetProperty("x").GetDouble(), ordinaryReleasePosition);
        Assert.Equal(new[] { "bot-held", "stop-queued", "stop-completed", "full-live-copy" }, events);
    }

    [Theory]
    [InlineData("privateEndPreparation", false)]
    [InlineData("schedulerRunning", false)]
    [InlineData("endCaptureHeld", false)]
    [InlineData("id", 8)]
    [InlineData("actorObjId", 10)]
    [InlineData("enrollmentGeneration", 4)]
    public async Task SchedulerReceipt_Unqualified_DoesNotDispatchStop(string field, object value)
    {
        var dispatched = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => BotShortRunEndCapture.CaptureAsync(7, 9, 3,
            () => Task.FromResult(Change(Scheduler(), field, value)),
            () => { dispatched = true; return Task.FromResult(Enqueued()); },
            _ => Task.FromResult(Terminal()), () => Task.FromResult(Copy()), CancellationToken.None));
        Assert.False(dispatched);
    }

    [Theory]
    [InlineData("state", "Failed")]
    [InlineData("state", "Interrupted")]
    [InlineData("action", "Move")]
    [InlineData("actor_id", 10)]
    [InlineData("trace_id", "20000000-0000-0000-0000-000000000001")]
    public async Task StopReceipt_Unqualified_DoesNotCaptureEnd(string field, object value)
    {
        var captured = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => BotShortRunEndCapture.CaptureAsync(7, 9, 3,
            () => Task.FromResult(Scheduler()), () => Task.FromResult(Enqueued()),
            _ => Task.FromResult(Change(Terminal(), field, value)),
            () => { captured = true; return Task.FromResult(Copy()); }, CancellationToken.None));
        Assert.False(captured);
    }

    [Theory]
    [InlineData("enrollmentGeneration", 4)]
    [InlineData("actorCharId", 8)]
    [InlineData("enrollmentRegistered", false)]
    [InlineData("enrollmentSchedulerRunning", false)]
    [InlineData("enrollmentLeased", false)]
    [InlineData("endPreparationIdle", false)]
    [InlineData("liveState", "Running")]
    [InlineData("snapshotStartedAtUtc", "2026-10-10T23:59:59Z")]
    public async Task FinalCopy_Unqualified_HasNoImmutableEnd(string field, object value)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => BotShortRunEndCapture.CaptureAsync(7, 9, 3,
            () => Task.FromResult(Scheduler()), () => Task.FromResult(Enqueued()),
            _ => Task.FromResult(Terminal()), () => Task.FromResult(Change(Copy(), field, value)), CancellationToken.None));
    }

    [Fact]
    public async Task StopNeverCompletes_BoundedDeadline_DoesNotCaptureEnd()
    {
        var captured = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BotShortRunEndCapture.CaptureAsync(7, 9, 3,
            () => Task.FromResult(Scheduler()), () => Task.FromResult(Enqueued()),
            _ => Task.FromResult(Change(Terminal(), "state", "Running")),
            () => { captured = true; return Task.FromResult(Copy()); }, deadline.Token));
        Assert.False(captured);
    }
}
