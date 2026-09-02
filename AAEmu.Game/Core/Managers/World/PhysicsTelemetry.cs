using System.Diagnostics;
using AAEmu.Game.Models.Game;

using NLog;

namespace AAEmu.Game.Core.Managers.World;

/// <summary>
/// Immutable snapshot of per-iteration physics telemetry over the sampled window.
/// Read by the E2E bridge metrics surface (<c>metrics.physics</c>) for the A5
/// stall investigation. All values are percentiles/max over the current window.
/// </summary>
public sealed class PhysicsTelemetrySnapshot
{
    public bool Available { get; init; }
    public long SampleCount { get; init; }

    // Wall-clock loop gap (inter-iteration time) — the direct measure of
    // physics-thread descheduling (Mode A/B stall signature).
    public double LoopGapP50Ms { get; init; }
    public double LoopGapP95Ms { get; init; }
    public double LoopGapMaxMs { get; init; }

    // Sleep overshoot: how much the Thread.Sleep overran its target step time.
    public double SleepOvershootP50Ms { get; init; }
    public double SleepOvershootP95Ms { get; init; }
    public double SleepOvershootMaxMs { get; init; }

    // PhysicsWorld.Step wall-clock (the actual physics work).
    public double StepP50Ms { get; init; }
    public double StepP95Ms { get; init; }
    public double StepMaxMs { get; init; }

    // Broadcast (SendUpdatedMovementData) wall-clock, when measurable.
    public double BroadcastP50Ms { get; init; }
    public double BroadcastP95Ms { get; init; }
    public double BroadcastMaxMs { get; init; }

    // Workload counts sampled per iteration (max over the window).
    public int PendingActionsMax { get; init; }
    public int BodiesMax { get; init; }
    public int ShipsMax { get; init; }
    public int ForcesMax { get; init; }
}

/// <summary>
/// Bounded, low-overhead per-iteration physics telemetry for the A5 stall
/// investigation. Disabled by default (<see cref="PhysicsTelemetryConfig.Enabled"/>);
/// when disabled no samples are recorded and no log lines are emitted.
///
/// Each physics iteration records a fixed set of scalar samples into bounded
/// rings sized to cover the configured sample period at the target physics TPS
/// (so a full window never wraps before the periodic log). A periodic aggregate
/// log line is emitted at most once per
/// <see cref="PhysicsTelemetryConfig.SamplePeriodSeconds"/>: WARN when the
/// window's max loop gap exceeds <see cref="PhysicsTelemetryConfig.SlowIterationMs"/>,
/// DEBUG otherwise. After each periodic log the window is reset — rings,
/// sample count and workload maxima all describe exactly the reported window.
///
/// Thread-safety: the physics thread writes via <see cref="Record"/> and the
/// bridge reads via <see cref="Snapshot"/> from another thread; both take the
/// same lock (uncontended in practice — the physics thread is the only writer).
/// No per-iteration INFO spam; no unbounded allocations.
/// </summary>
internal sealed class PhysicsTelemetry
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly PhysicsTelemetryConfig _config;
    private readonly string _worldName;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    private readonly SampleRing _loopGap;
    private readonly SampleRing _sleepOvershoot;
    private readonly SampleRing _step;
    private readonly SampleRing _broadcast;

    private int _pendingActionsMax;
    private int _bodiesMax;
    private int _shipsMax;
    private int _forcesMax;

    private long _samples;
    private long _windowStartTimestamp;

    public PhysicsTelemetry(PhysicsTelemetryConfig config, string worldName, float targetPhysicsTps = 25f, TimeProvider timeProvider = null)
    {
        _config = config.Normalize();
        _worldName = worldName;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Ring capacity covers the configured sample period at the target TPS
        // (plus margin) so a full window never wraps before the periodic log.
        // The config is normalized (period clamped to [1, 3600]s) so this
        // arithmetic is bounded: max ≈ 3600 × 25 + 64 ≈ 90k samples/ring.
        var capacity = Math.Max(1024, (int)Math.Ceiling(_config.SamplePeriodSeconds * Math.Max(1f, targetPhysicsTps)) + 64);
        _loopGap = new SampleRing(capacity);
        _sleepOvershoot = new SampleRing(capacity);
        _step = new SampleRing(capacity);
        _broadcast = new SampleRing(capacity);

        _windowStartTimestamp = _timeProvider.GetTimestamp();
    }

    public bool Enabled => _config.Enabled;

    /// <summary>
    /// Records one physics iteration. Cheap when disabled (single bool check).
    /// </summary>
    public void Record(
        double loopGapMs,
        double sleepOvershootMs,
        double stepMs,
        double broadcastMs,
        int pendingActions,
        int bodies,
        int ships,
        int forces)
    {
        if (!_config.Enabled)
            return;

        lock (_lock)
        {
            _loopGap.Add(loopGapMs);
            _sleepOvershoot.Add(sleepOvershootMs);
            _step.Add(stepMs);
            _broadcast.Add(broadcastMs);

            if (pendingActions > _pendingActionsMax) _pendingActionsMax = pendingActions;
            if (bodies > _bodiesMax) _bodiesMax = bodies;
            if (ships > _shipsMax) _shipsMax = ships;
            if (forces > _forcesMax) _forcesMax = forces;

            _samples++;

            // Periodic aggregate — at most once per SamplePeriodSeconds.
            if (_timeProvider.GetElapsedTime(_windowStartTimestamp).TotalSeconds >= _config.SamplePeriodSeconds)
            {
                var snap = SnapshotLocked();
                if (snap.LoopGapMaxMs > _config.SlowIterationMs)
                {
                    Logger.Warn(
                        "Physics telemetry [{0}]: samples={1} loopGap p50={2:F1} p95={3:F1} max={4:F1}ms | sleepOvershoot max={5:F1}ms | step p95={6:F1} max={7:F1}ms | broadcast p95={8:F1} max={9:F1}ms | pendingActions={10} bodies={11} ships={12} forces={13}",
                        _worldName, snap.SampleCount,
                        snap.LoopGapP50Ms, snap.LoopGapP95Ms, snap.LoopGapMaxMs,
                        snap.SleepOvershootMaxMs,
                        snap.StepP95Ms, snap.StepMaxMs,
                        snap.BroadcastP95Ms, snap.BroadcastMaxMs,
                        snap.PendingActionsMax, snap.BodiesMax, snap.ShipsMax, snap.ForcesMax);
                }
                else
                {
                    Logger.Debug(
                        "Physics telemetry [{0}]: samples={1} loopGap p50={2:F1} p95={3:F1} max={4:F1}ms | sleepOvershoot max={5:F1}ms | step p95={6:F1} max={7:F1}ms | broadcast p95={8:F1} max={9:F1}ms | pendingActions={10} bodies={11} ships={12} forces={13}",
                        _worldName, snap.SampleCount,
                        snap.LoopGapP50Ms, snap.LoopGapP95Ms, snap.LoopGapMaxMs,
                        snap.SleepOvershootMaxMs,
                        snap.StepP95Ms, snap.StepMaxMs,
                        snap.BroadcastP95Ms, snap.BroadcastMaxMs,
                        snap.PendingActionsMax, snap.BodiesMax, snap.ShipsMax, snap.ForcesMax);
                }

                ResetWindowLocked();
            }
        }
    }

    /// <summary>
    /// Snapshot of the current window. Returns <see cref="PhysicsTelemetrySnapshot.Available"/>
    /// = false when disabled or no samples have been recorded in the current window.
    /// </summary>
    public PhysicsTelemetrySnapshot Snapshot()
    {
        lock (_lock)
        {
            return SnapshotLocked();
        }
    }

    private PhysicsTelemetrySnapshot SnapshotLocked()
    {
        if (!_config.Enabled || _samples == 0)
            return new PhysicsTelemetrySnapshot { Available = false };

        var (_, gapP50, gapP95, _, gapMax) = _loopGap.Summarize();
        var (_, sleepP50, sleepP95, _, sleepMax) = _sleepOvershoot.Summarize();
        var (_, stepP50, stepP95, _, stepMax) = _step.Summarize();
        var (_, bcastP50, bcastP95, _, bcastMax) = _broadcast.Summarize();

        return new PhysicsTelemetrySnapshot
        {
            Available = true,
            SampleCount = _samples,
            LoopGapP50Ms = gapP50,
            LoopGapP95Ms = gapP95,
            LoopGapMaxMs = gapMax,
            SleepOvershootP50Ms = sleepP50,
            SleepOvershootP95Ms = sleepP95,
            SleepOvershootMaxMs = sleepMax,
            StepP50Ms = stepP50,
            StepP95Ms = stepP95,
            StepMaxMs = stepMax,
            BroadcastP50Ms = bcastP50,
            BroadcastP95Ms = bcastP95,
            BroadcastMaxMs = bcastMax,
            PendingActionsMax = _pendingActionsMax,
            BodiesMax = _bodiesMax,
            ShipsMax = _shipsMax,
            ForcesMax = _forcesMax
        };
    }

    private void ResetWindowLocked()
    {
        _loopGap.Clear();
        _sleepOvershoot.Clear();
        _step.Clear();
        _broadcast.Clear();
        _pendingActionsMax = 0;
        _bodiesMax = 0;
        _shipsMax = 0;
        _forcesMax = 0;
        _samples = 0;
        _windowStartTimestamp = _timeProvider.GetTimestamp();
    }
}
