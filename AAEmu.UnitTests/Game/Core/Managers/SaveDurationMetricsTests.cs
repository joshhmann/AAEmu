using AAEmu.Game.Core.Managers;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Save-duration metrics tests (M3b-4 autosave budget instrumentation): the
/// ring buffer feeding the gate's Autosave p95 verdict.
/// </summary>
public class SaveDurationMetricsTests
{
    [Test]
    public async Task Snapshot_NoSamples_AllZero()
    {
        var metrics = new SaveDurationMetrics();

        var s = metrics.Snapshot();

        await Assert.That(s.SampleCount).IsEqualTo(0);
        await Assert.That(s.P95Ms).IsEqualTo(0);
        await Assert.That(s.MaxMs).IsEqualTo(0);
    }

    [Test]
    public async Task Record_SingleSample_P95AndMaxEqualSample()
    {
        var metrics = new SaveDurationMetrics();
        metrics.Record(TimeSpan.FromMilliseconds(500));

        var s = metrics.Snapshot();

        await Assert.That(s.SampleCount).IsEqualTo(1);
        await Assert.That(s.P95Ms).IsEqualTo(500);
        await Assert.That(s.MaxMs).IsEqualTo(500);
    }

    [Test]
    public async Task Record_MixedDurations_P95AboveP50()
    {
        var metrics = new SaveDurationMetrics();
        // 9 fast saves + 1 slow save: p95 must land on the slow one.
        for (var i = 0; i < 9; i++)
            metrics.Record(TimeSpan.FromMilliseconds(100));
        metrics.Record(TimeSpan.FromMilliseconds(1900));

        var s = metrics.Snapshot();

        await Assert.That(s.SampleCount).IsEqualTo(10);
        await Assert.That(s.P95Ms).IsEqualTo(1900);
        await Assert.That(s.MaxMs).IsEqualTo(1900);
        await Assert.That(s.P50Ms).IsEqualTo(100);
    }

    [Test]
    public async Task Record_OverCapacity_RingRetainsLatest()
    {
        var metrics = new SaveDurationMetrics();
        // 1100 samples > 1024 ring capacity: the oldest 76 roll off.
        for (var i = 0; i < 1100; i++)
            metrics.Record(TimeSpan.FromMilliseconds(1));
        metrics.Record(TimeSpan.FromMilliseconds(5000));

        var s = metrics.Snapshot();

        await Assert.That(s.SampleCount).IsEqualTo(1024); // capped ring
        await Assert.That(s.MaxMs).IsEqualTo(5000);       // latest retained
    }
}
