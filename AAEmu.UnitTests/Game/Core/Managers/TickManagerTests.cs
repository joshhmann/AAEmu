using System.Diagnostics;

using AAEmu.Game.Core.Managers;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// TickManager dispatch mechanism tests — the #1491 starvation mechanism:
/// a synchronous subscriber blocks Invoke (and therefore the tick loop caller),
/// an async subscriber does not. This is why ActiveRegionTick must be async.
/// </summary>
public class TickManagerTests
{
    [Test]
    public async Task SyncSubscriber_SlowWork_BlocksInvokeCaller()
    {
        var tm = new TickManager();
        tm.OnTick.Subscribe(_ => Thread.Sleep(300), TimeSpan.FromMilliseconds(1));

        var sw = Stopwatch.StartNew();
        tm.OnTick.Invoke();
        sw.Stop();

        await Assert.That(sw.ElapsedMilliseconds >= 250)
            .IsTrue()
            .Because($"a synchronous slow subscriber blocks the tick loop caller; actual {sw.ElapsedMilliseconds}ms");
    }

    [Test]
    public async Task AsyncSubscriber_SlowWork_DoesNotBlockInvokeCaller()
    {
        var tm = new TickManager();
        tm.OnTick.Subscribe(_ => Thread.Sleep(300), TimeSpan.FromMilliseconds(1), useAsync: true);

        var sw = Stopwatch.StartNew();
        tm.OnTick.Invoke();
        sw.Stop();

        await Assert.That(sw.ElapsedMilliseconds < 100)
            .IsTrue()
            .Because($"an async subscriber must dispatch without blocking Invoke; actual {sw.ElapsedMilliseconds}ms");
    }

    [Test]
    public async Task Invoke_RecordsDurationMetrics_P50P95Max()
    {
        var tm = new TickManager();

        // Fast subscriber (1ms rate) — 20 invokes so the ring has samples
        tm.OnTick.Subscribe(_ => { }, TimeSpan.FromMilliseconds(1), name: "fast");
        for (var i = 0; i < 20; i++)
            tm.OnTick.Invoke();

        var snapshot = tm.GetTickMetrics();

        await Assert.That(snapshot.InvokeSampleCount).IsGreaterThanOrEqualTo(20)
            .Because("every Invoke() must record a duration sample");
        await Assert.That(snapshot.InvokeP50Ms).IsGreaterThanOrEqualTo(0)
            .Because("p50 must be a non-negative duration");
        await Assert.That(snapshot.InvokeMaxMs).IsGreaterThanOrEqualTo(snapshot.InvokeP95Ms)
            .Because("max must be >= p95");
        await Assert.That(snapshot.InvokeP95Ms).IsGreaterThanOrEqualTo(snapshot.InvokeP50Ms)
            .Because("p95 must be >= p50");
    }

    [Test]
    public async Task Subscriber_RecordsPerSubscriberDuration_WithName()
    {
        var tm = new TickManager();
        var asyncDone = new TaskCompletionSource();
        tm.OnTick.Subscribe(_ => Thread.Sleep(10), TimeSpan.Zero, useAsync: false, name: "slow-sync");
        tm.OnTick.Subscribe(_ => asyncDone.TrySetResult(), TimeSpan.Zero, useAsync: true, name: "fast-async");

        tm.OnTick.Invoke();
        await Task.WhenAny(asyncDone.Task, Task.Delay(2000));
        await Task.Delay(50); // let finally block in async wrapper record metrics

        var snapshot = tm.GetTickMetrics();

        await Assert.That(snapshot.Subscribers).ContainsKey("slow-sync")
            .Because("sync subscriber durations must be recorded under their name");
        await Assert.That(snapshot.Subscribers).ContainsKey("fast-async")
            .Because("async subscriber durations must be recorded under their name");
        await Assert.That(snapshot.Subscribers["slow-sync"].SampleCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(snapshot.Subscribers["slow-sync"].MaxMs).IsGreaterThanOrEqualTo(5)
            .Because($"the 10ms slow subscriber must show up in per-subscriber max; got {snapshot.Subscribers["slow-sync"].MaxMs}ms");
    }

    [Test]
    public async Task SubscriberCount_ReflectsLiveSubscriptions()
    {
        var tm = new TickManager();
        TickManager.TickEventHandler.OnTickEvent ev = _ => { };

        tm.OnTick.Subscribe(ev, TimeSpan.FromSeconds(1), name: "one");
        tm.OnTick.Invoke(); // flush the add queue
        await Assert.That(tm.GetTickMetrics().SubscriberCount).IsEqualTo(1);

        tm.OnTick.Subscribe(_ => { }, TimeSpan.FromSeconds(2), name: "two");
        tm.OnTick.Invoke();
        await Assert.That(tm.GetTickMetrics().SubscriberCount).IsEqualTo(2);

        tm.OnTick.UnSubscribe(ev);
        tm.OnTick.Invoke(); // flush the remove queue
        await Assert.That(tm.GetTickMetrics().SubscriberCount).IsEqualTo(1)
            .Because("unsubscribed handlers must leave the subscriber count");
    }
}
