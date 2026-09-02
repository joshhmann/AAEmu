using System.Reflection;
using System.Text.Json;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using Microsoft.Extensions.Time.Testing;

using BotDriveBridge = AAEmu.Game.Models.Game.Bots.BotDriveBridge;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

/// <summary>
/// A5 physics telemetry rig — bounded per-iteration telemetry must be
/// default-safe (disabled ⇒ no samples, no log spam), aggregate
/// deterministically into the snapshot the E2E bridge reads
/// (<c>metrics.physics</c>), reset its window at each periodic log so
/// SampleCount/workload maxima match the reported window, stay consistent
/// when the bridge snapshots concurrently with the physics thread's writes,
/// and normalize zero/negative/NaN/±Infinity config values to safe finite
/// bounds (returning a copy, never mutating the caller's shared config).
/// </summary>
public class PhysicsTelemetryTests
{
    private static PhysicsTelemetryConfig EnabledConfig(double samplePeriodSeconds = 60, double slowMs = 100)
        => new() { Enabled = true, SamplePeriodSeconds = samplePeriodSeconds, SlowIterationMs = slowMs };

    [Test]
    public async Task Disabled_RecordsNothing_SnapshotUnavailable()
    {
        var telemetry = new PhysicsTelemetry(new PhysicsTelemetryConfig { Enabled = false }, "test_world");

        // Act — record even though disabled
        telemetry.Record(loopGapMs: 500, sleepOvershootMs: 10, stepMs: 5, broadcastMs: 3,
            pendingActions: 1, bodies: 2, ships: 3, forces: 4);

        var snap = telemetry.Snapshot();

        await Assert.That(snap.Available).IsFalse()
            .Because("disabled telemetry must report unavailable (no samples recorded)");
        await Assert.That(snap.SampleCount).IsEqualTo(0);
        await Assert.That(snap.LoopGapMaxMs).IsEqualTo(0);
        await Assert.That(snap.BodiesMax).IsEqualTo(0);
    }

    [Test]
    public async Task Enabled_RecordsSamples_AggregatesPercentilesAndMax()
    {
        var telemetry = new PhysicsTelemetry(EnabledConfig(), "test_world");

        // Act — record a deterministic sequence of loop gaps: 10, 20, 30, 40, 50
        for (var i = 1; i <= 5; i++)
        {
            telemetry.Record(loopGapMs: i * 10, sleepOvershootMs: i, stepMs: i * 0.5, broadcastMs: i * 0.25,
                pendingActions: i, bodies: i * 2, ships: i, forces: i * 3);
        }

        var snap = telemetry.Snapshot();

        await Assert.That(snap.Available).IsTrue();
        await Assert.That(snap.SampleCount).IsEqualTo(5);
        // Sorted gaps: 10,20,30,40,50 → p50=30, p95=50, max=50
        await Assert.That(snap.LoopGapP50Ms).IsEqualTo(30);
        await Assert.That(snap.LoopGapP95Ms).IsEqualTo(50);
        await Assert.That(snap.LoopGapMaxMs).IsEqualTo(50);
        // Workload counts are max over the window
        await Assert.That(snap.PendingActionsMax).IsEqualTo(5);
        await Assert.That(snap.BodiesMax).IsEqualTo(10);
        await Assert.That(snap.ShipsMax).IsEqualTo(5);
        await Assert.That(snap.ForcesMax).IsEqualTo(15);
    }

    [Test]
    public async Task Enabled_NoSamplesYet_SnapshotUnavailable()
    {
        var telemetry = new PhysicsTelemetry(EnabledConfig(), "test_world");

        var snap = telemetry.Snapshot();

        await Assert.That(snap.Available).IsFalse()
            .Because("an enabled telemetry with zero samples must not report a fabricated snapshot");
        await Assert.That(snap.SampleCount).IsEqualTo(0);
    }

    [Test]
    public async Task Enabled_StepAndBroadcast_AggregateIndependently()
    {
        var telemetry = new PhysicsTelemetry(EnabledConfig(), "test_world");

        // Step durations 1..4, broadcast durations 10..40 (independent rings)
        for (var i = 1; i <= 4; i++)
        {
            telemetry.Record(loopGapMs: 0, sleepOvershootMs: 0, stepMs: i, broadcastMs: i * 10,
                pendingActions: 0, bodies: 0, ships: 0, forces: 0);
        }

        var snap = telemetry.Snapshot();

        // Sorted steps: 1,2,3,4 → p50=2 (index 1), max=4
        await Assert.That(snap.StepP50Ms).IsEqualTo(2);
        await Assert.That(snap.StepMaxMs).IsEqualTo(4);
        // Sorted broadcasts: 10,20,30,40 → p50=20 (index 1), max=40
        await Assert.That(snap.BroadcastP50Ms).IsEqualTo(20);
        await Assert.That(snap.BroadcastMaxMs).IsEqualTo(40);
    }

    [Test]
    public async Task WindowReset_AfterPeriod_SampleCountAndMaximaMatchNewWindow()
    {
        var clock = new FakeTimeProvider();
        var telemetry = new PhysicsTelemetry(EnabledConfig(samplePeriodSeconds: 60), "test_world",
            targetPhysicsTps: 25f, timeProvider: clock);

        // Window 1 — loop gaps 10..50, workload maxima 1..5
        for (var i = 1; i <= 5; i++)
        {
            telemetry.Record(loopGapMs: i * 10, sleepOvershootMs: 0, stepMs: 0, broadcastMs: 0,
                pendingActions: i, bodies: i, ships: i, forces: i);
        }

        // Cross the 60s period boundary — the next Record logs the completed
        // window (including itself) and resets the window.
        clock.Advance(TimeSpan.FromSeconds(61));
        telemetry.Record(loopGapMs: 100, sleepOvershootMs: 0, stepMs: 0, broadcastMs: 0,
            pendingActions: 9, bodies: 9, ships: 9, forces: 9);

        // The reset happened on the record above; the new window is empty until
        // the next record. Record one sample in the new window.
        telemetry.Record(loopGapMs: 200, sleepOvershootMs: 0, stepMs: 0, broadcastMs: 0,
            pendingActions: 3, bodies: 3, ships: 3, forces: 3);

        var snap = telemetry.Snapshot();

        // The window was reset: only the post-reset sample is in the window.
        await Assert.That(snap.Available).IsTrue();
        await Assert.That(snap.SampleCount).IsEqualTo(1)
            .Because("after the periodic log the window resets; SampleCount must describe only the new window");
        await Assert.That(snap.LoopGapMaxMs).IsEqualTo(200);
        await Assert.That(snap.LoopGapP50Ms).IsEqualTo(200);
        await Assert.That(snap.PendingActionsMax).IsEqualTo(3)
            .Because("workload maxima must reset with the window, not stay cumulative");
        await Assert.That(snap.BodiesMax).IsEqualTo(3);
        await Assert.That(snap.ShipsMax).IsEqualTo(3);
        await Assert.That(snap.ForcesMax).IsEqualTo(3);
    }

    [Test]
    public async Task WindowReset_NoSamplesInNewWindow_SnapshotUnavailable()
    {
        var clock = new FakeTimeProvider();
        var telemetry = new PhysicsTelemetry(EnabledConfig(samplePeriodSeconds: 60), "test_world",
            targetPhysicsTps: 25f, timeProvider: clock);

        telemetry.Record(loopGapMs: 10, sleepOvershootMs: 0, stepMs: 0, broadcastMs: 0,
            pendingActions: 1, bodies: 1, ships: 1, forces: 1);

        // Cross the period boundary; the periodic log fires (including this
        // record) and resets the window.
        clock.Advance(TimeSpan.FromSeconds(61));
        telemetry.Record(loopGapMs: 20, sleepOvershootMs: 0, stepMs: 0, broadcastMs: 0,
            pendingActions: 2, bodies: 2, ships: 2, forces: 2);

        // The reset happened on the record above; the new window is empty.
        var snap = telemetry.Snapshot();
        await Assert.That(snap.Available).IsFalse()
            .Because("after the periodic log resets the window, a snapshot with no new samples must be unavailable");
        await Assert.That(snap.SampleCount).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_SnapshotWhileRecording_IsConsistent()
    {
        var telemetry = new PhysicsTelemetry(EnabledConfig(), "test_world");

        // Writer thread mimics the physics thread; reader thread mimics the bridge.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                i++;
                telemetry.Record(loopGapMs: i % 50, sleepOvershootMs: i % 5, stepMs: i % 3, broadcastMs: i % 2,
                    pendingActions: i % 7, bodies: i % 11, ships: i % 3, forces: i % 5);
            }
        });

        var reader = Task.Run(() =>
        {
            var snapshots = new List<PhysicsTelemetrySnapshot>();
            while (!cts.IsCancellationRequested)
            {
                snapshots.Add(telemetry.Snapshot());
            }
            return snapshots;
        });

        await Task.WhenAll(writer, reader);
        var snaps = await reader;

        // Every snapshot must be internally consistent: no torn reads, no
        // negative durations, maxima >= percentiles, and SampleCount > 0 once
        // samples exist.
        await Assert.That(snaps).IsNotEmpty();
        foreach (var snap in snaps)
        {
            if (!snap.Available)
                continue;
            await Assert.That(snap.SampleCount).IsGreaterThan(0);
            await Assert.That(snap.LoopGapMaxMs).IsGreaterThanOrEqualTo(snap.LoopGapP95Ms);
            await Assert.That(snap.LoopGapP95Ms).IsGreaterThanOrEqualTo(snap.LoopGapP50Ms);
            await Assert.That(snap.StepMaxMs).IsGreaterThanOrEqualTo(0);
            await Assert.That(snap.BroadcastMaxMs).IsGreaterThanOrEqualTo(0);
            await Assert.That(snap.PendingActionsMax).IsGreaterThanOrEqualTo(0);
            await Assert.That(snap.BodiesMax).IsGreaterThanOrEqualTo(0);
            await Assert.That(snap.ShipsMax).IsGreaterThanOrEqualTo(0);
            await Assert.That(snap.ForcesMax).IsGreaterThanOrEqualTo(0);
        }
    }

    [Test]
    public async Task ConfigNormalize_ZeroAndNegativeValues_ClampedToSafeBounds()
    {
        var config = new PhysicsTelemetryConfig
        {
            Enabled = true,
            SamplePeriodSeconds = 0,
            SlowIterationMs = -50
        };

        var normalized = config.Normalize();

        await Assert.That(normalized.SamplePeriodSeconds).IsEqualTo(PhysicsTelemetryConfig.MinSamplePeriodSeconds)
            .Because("a zero sample period must be clamped up to the minimum (1s) so the periodic log can fire");
        await Assert.That(normalized.SlowIterationMs).IsEqualTo(0)
            .Because("a negative slow-iteration threshold must be clamped to 0 (never negative)");
    }

    [Test]
    public async Task ConfigNormalize_ExtremeValues_ClampedToMaxPeriod()
    {
        var config = new PhysicsTelemetryConfig
        {
            Enabled = true,
            SamplePeriodSeconds = 1_000_000,
            SlowIterationMs = 1_000_000
        };

        var normalized = config.Normalize();

        await Assert.That(normalized.SamplePeriodSeconds).IsEqualTo(PhysicsTelemetryConfig.MaxSamplePeriodSeconds)
            .Because("an extreme sample period must be clamped down to the maximum (3600s) so ring-capacity arithmetic stays bounded");
        await Assert.That(normalized.SlowIterationMs).IsEqualTo(PhysicsTelemetryConfig.MaxSlowIterationMs)
            .Because("an extreme slow-iteration threshold must be clamped down to the bounded maximum so the normalized result is always finite");
    }

    [Test]
    public async Task ConfigNormalize_DisabledDefault_Preserved()
    {
        var config = new PhysicsTelemetryConfig(); // Enabled = false, 60s, 100ms

        var normalized = config.Normalize();

        await Assert.That(normalized.Enabled).IsFalse()
            .Because("normalization must preserve the disabled-by-default posture");
        await Assert.That(normalized.SamplePeriodSeconds).IsEqualTo(60);
        await Assert.That(normalized.SlowIterationMs).IsEqualTo(100);
    }

    [Test]
    public async Task ConfigNormalize_NaNValues_ReplacedWithSafeDefaults()
    {
        var config = new PhysicsTelemetryConfig
        {
            Enabled = true,
            SamplePeriodSeconds = double.NaN,
            SlowIterationMs = double.NaN
        };

        var normalized = config.Normalize();

        await Assert.That(normalized.SamplePeriodSeconds).IsEqualTo(60)
            .Because("NaN sample period must be replaced with the default (60s) — Math.Clamp alone would pass NaN through and poison ring-capacity arithmetic");
        await Assert.That(normalized.SlowIterationMs).IsEqualTo(100)
            .Because("NaN slow-iteration threshold must be replaced with the default (100ms)");
        await Assert.That(normalized.Enabled).IsTrue()
            .Because("normalization must preserve the enabled flag");
    }

    [Test]
    public async Task ConfigNormalize_InfinityValues_ClampedToBounds()
    {
        var config = new PhysicsTelemetryConfig
        {
            Enabled = true,
            SamplePeriodSeconds = double.PositiveInfinity,
            SlowIterationMs = double.NegativeInfinity
        };

        var normalized = config.Normalize();

        await Assert.That(normalized.SamplePeriodSeconds).IsEqualTo(PhysicsTelemetryConfig.MaxSamplePeriodSeconds)
            .Because("+Infinity sample period must be clamped to the maximum (3600s) so ring-capacity arithmetic stays bounded");
        await Assert.That(normalized.SlowIterationMs).IsEqualTo(0)
            .Because("-Infinity slow-iteration threshold must be clamped to 0 (never negative)");
    }

    [Test]
    public async Task ConfigNormalize_PositiveInfinitySlowThreshold_ClampedToBoundedMax()
    {
        var config = new PhysicsTelemetryConfig
        {
            Enabled = true,
            SamplePeriodSeconds = 60,
            SlowIterationMs = double.PositiveInfinity
        };

        var normalized = config.Normalize();

        await Assert.That(double.IsFinite(normalized.SlowIterationMs)).IsTrue()
            .Because("+Infinity slow-iteration threshold must be normalized to a finite value — it would otherwise survive the < 0 check and poison the WARN/DEBUG decision");
        await Assert.That(normalized.SlowIterationMs).IsEqualTo(PhysicsTelemetryConfig.MaxSlowIterationMs)
            .Because("+Infinity slow-iteration threshold must be clamped to the bounded maximum");
    }

    [Test]
    public async Task ConfigNormalize_ReturnsCopy_DoesNotMutateCaller()
    {
        var config = new PhysicsTelemetryConfig
        {
            Enabled = true,
            SamplePeriodSeconds = 0,
            SlowIterationMs = -50
        };

        var normalized = config.Normalize();

        // The caller's shared config instance must be untouched — the bridge
        // and the physics thread may both hold the same AppConfiguration object.
        await Assert.That(config.SamplePeriodSeconds).IsEqualTo(0)
            .Because("Normalize must return a new copy and never mutate the caller's shared config");
        await Assert.That(config.SlowIterationMs).IsEqualTo(-50);
        await Assert.That(normalized.SamplePeriodSeconds).IsEqualTo(PhysicsTelemetryConfig.MinSamplePeriodSeconds);
        await Assert.That(normalized.SlowIterationMs).IsEqualTo(0);
    }

    [Test]
    public async Task NaNPeriod_ConstructsWithDefaultCapacity_AndRecords()
    {
        // NaN period must be normalized to the 60s default before capacity
        // arithmetic — constructing must not throw or allocate unboundedly.
        var telemetry = new PhysicsTelemetry(new PhysicsTelemetryConfig
        {
            Enabled = true,
            SamplePeriodSeconds = double.NaN,
            SlowIterationMs = double.NaN
        }, "test_world", targetPhysicsTps: 25f);

        telemetry.Record(loopGapMs: 10, sleepOvershootMs: 1, stepMs: 2, broadcastMs: 3,
            pendingActions: 1, bodies: 2, ships: 3, forces: 4);

        var snap = telemetry.Snapshot();
        await Assert.That(snap.Available).IsTrue();
        await Assert.That(snap.SampleCount).IsEqualTo(1);
        await Assert.That(snap.LoopGapMaxMs).IsEqualTo(10);
    }

    [Test]
    public async Task ExtremePeriod_ConstructsBoundedRings_AndRecords()
    {
        // A period at the max (3600s) must construct bounded rings (≈90k
        // samples at 25 TPS) and record without throwing or allocating unboundedly.
        var telemetry = new PhysicsTelemetry(EnabledConfig(samplePeriodSeconds: 3600), "test_world",
            targetPhysicsTps: 25f);

        telemetry.Record(loopGapMs: 10, sleepOvershootMs: 1, stepMs: 2, broadcastMs: 3,
            pendingActions: 1, bodies: 2, ships: 3, forces: 4);

        var snap = telemetry.Snapshot();
        await Assert.That(snap.Available).IsTrue();
        await Assert.That(snap.SampleCount).IsEqualTo(1);
        await Assert.That(snap.LoopGapMaxMs).IsEqualTo(10);
    }

    [Test]
    public async Task BridgeMetricsCommand_ExposesPhysicsTelemetry_DisabledReportsUnavailable()
    {
        // The E2E bridge "metrics" command must expose the physics telemetry
        // surface (metrics.physics). With telemetry disabled (the default),
        // the response must be honest: available=false, never a fabricated snapshot.
        var handleCommand = typeof(BotDriveBridge).GetMethod("HandleCommand",
            BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(handleCommand).IsNotNull();

        var response = (string)handleCommand!.Invoke(BotDriveBridge.Instance, ["{\"cmd\":\"metrics\"}"]);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue()
            .Because("the metrics command must succeed");
        var data = root.GetProperty("data");
        await Assert.That(data.TryGetProperty("physics", out var physics)).IsTrue()
            .Because("the metrics surface must expose the physics telemetry key");
        await Assert.That(physics.ValueKind).IsEqualTo(JsonValueKind.Object)
            .Because("the physics section must be present as an object");
        await Assert.That(physics.GetProperty("available").GetBoolean()).IsFalse()
            .Because("with telemetry disabled (default) the physics section must report available=false — never a fabricated snapshot");
    }
}
