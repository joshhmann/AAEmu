using System.Collections.Concurrent;
using System.Diagnostics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Scheduler rig for IPlayerBotScheduler (slice #6): due-time semantics,
/// bounded worker pool, per-bot execution lease, event wakes, metrics, and
/// the hard gate (one wake-scan, no per-bot anything). Time is driven by
/// FakeTimeProvider; scan cycles are pumped manually (scan interval is set
/// to 1h so the background loop never fires mid-test), and the worker pool
/// runs for real — slow bots are real delays, which is exactly what the
/// "a slow bot never blocks others" gate needs to prove.
/// </summary>
public class PlayerBotSchedulerTests
{
    /// <summary>Deterministic lifecycle seam (same shape as the manager rig).</summary>
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool ActivateHeadless(Character character, object? botContext) => true;

        public bool Deactivate(Character character, string reason) => true;
    }

    /// <summary>
    /// Step executor rig: records every call (start/end, wall-clock), tracks
    /// per-bot max concurrency, records which bots' steps overlapped each
    /// other in time (deterministic — no wall-clock comparison), supports
    /// per-bot simulated durations and a per-bot next-wake delay to return.
    /// </summary>
    private sealed class RecordingExecutor : IBotStepExecutor
    {
        public TimeSpan? NextDelay { get; set; } // returned by every step when set
        public Func<uint, TimeSpan?>? DelayFor { get; set; } // per-bot simulated step duration
        public bool Throw { get; set; }
        public bool IgnoreCancellation { get; set; }

        public ConcurrentQueue<(uint BotId, DateTime StartedUtc)> Starts { get; } = [];
        public ConcurrentQueue<(uint BotId, DateTime FinishedUtc)> Finishes { get; } = [];
        public ConcurrentDictionary<uint, int> MaxConcurrentPerBot { get; } = [];
        public int MaxConcurrentOverall;
        public ConcurrentQueue<(uint A, uint B)> Overlaps { get; } = [];

        private readonly ConcurrentDictionary<uint, byte> _running = [];

        public async Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _concurrentOverall);
            UpdateMax(ref MaxConcurrentOverall, concurrent);
            var botConcurrent = ConcurrentPerBot(bot.CharacterId, +1);
            MaxConcurrentPerBot.AddOrUpdate(bot.CharacterId, botConcurrent, (_, existing) => Math.Max(existing, botConcurrent));
            Starts.Enqueue((bot.CharacterId, DateTime.UtcNow));

            // Deterministic overlap record: every other bot running right now
            // is one this step executed concurrently with.
            _running.TryAdd(bot.CharacterId, 0);
            foreach (var other in _running.Keys)
            {
                if (other != bot.CharacterId)
                    Overlaps.Enqueue((bot.CharacterId, other));
            }

            try
            {
                var duration = DelayFor?.Invoke(bot.CharacterId) ?? TimeSpan.Zero;
                if (duration > TimeSpan.Zero)
                {
                    if (IgnoreCancellation)
                        await Task.Delay(duration);
                    else
                        await Task.Delay(duration, cancellationToken);
                }

                if (Throw)
                    throw new InvalidOperationException("simulated step failure");

                return NextDelay;
            }
            finally
            {
                _running.TryRemove(bot.CharacterId, out _);
                Finishes.Enqueue((bot.CharacterId, DateTime.UtcNow));
                Interlocked.Decrement(ref _concurrentOverall);
                ConcurrentPerBot(bot.CharacterId, -1);
            }
        }

        private int _concurrentOverall;
        private readonly ConcurrentDictionary<uint, int> _botConcurrent = [];

        private int ConcurrentPerBot(uint botId, int delta)
        {
            while (true)
            {
                var current = _botConcurrent.GetOrAdd(botId, 0);
                var updated = current + delta;
                if (_botConcurrent.TryUpdate(botId, updated, current))
                    return updated;
            }
        }

        private static void UpdateMax(ref int field, int candidate)
        {
            int current;
            while (candidate > (current = Volatile.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, candidate, current) == current)
                    return;
            }
        }

        public int CountStarts(uint botId) => Starts.Count(s => s.BotId == botId);
    }

    private sealed class Rig : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(DateTime.UtcNow);

        /// <summary>UTC now from the fake clock (TimeProvider returns DateTimeOffset).</summary>
        public DateTime Now => Time.GetUtcNow().UtcDateTime;
        public RecordingExecutor Executor { get; } = new();
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }

        public Rig(int workerCount = 4, TimeSpan? stepTimeout = null)
        {
            Manager = new PlayerBotManager(new RecordingLifecycle());
            var options = new PlayerBotSchedulerOptions
            {
                WorkerCount = workerCount,
                ScanInterval = TimeSpan.FromHours(1), // loop inert in tests; cycles pumped manually
                StepTimeout = stepTimeout ?? TimeSpan.FromSeconds(30),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            };
            Scheduler = new PlayerBotScheduler(Manager, Executor, options, Time);
            Scheduler.Start();
        }

        /// <summary>Spawns + activates a bot through the real manager registry.</summary>
        public uint AddActiveBot(uint id, string name = "bot")
        {
            Manager.Spawn(new Character(new UnitCustomModelParams()) { Id = id, Name = name }, "rig");
            Manager.Activate(id, null, "rig");
            return id;
        }

        public uint AddRegisteredBot(uint id, string name = "bot")
        {
            Manager.Spawn(new Character(new UnitCustomModelParams()) { Id = id, Name = name }, "rig");
            return id;
        }

        public void Pump() => Scheduler.RunScanCycle();

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("rig wait condition not met");
                await Task.Delay(5);
            }
        }

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();
    }

    #region Due-time semantics

    [Test]
    public async Task WakeAt_NotDue_DoesNotExecute()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        rig.Scheduler.WakeAt(bot, rig.Now + TimeSpan.FromMilliseconds(50));
        rig.Pump(); // nothing due yet

        await Assert.That(rig.Executor.Starts).IsEmpty();
        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(metrics.DueQueueDepth).IsEqualTo(1);
        await Assert.That(metrics.TotalStepsRun).IsEqualTo(0);
    }

    [Test]
    public async Task WakeAt_Due_ExecutesExactlyOnce_AndReleasesLease()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        rig.Scheduler.WakeAt(bot, rig.Now + TimeSpan.FromMilliseconds(10));
        rig.Pump(); // not due
        rig.Time.Advance(TimeSpan.FromMilliseconds(11));
        rig.Pump(); // due now

        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
        // Lease releases only when the step COMPLETES — wait for release
        // before asserting post-completion state (load-safe).
        await rig.WaitUntilAsync(() => !rig.Scheduler.IsLeased(bot));

        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(1);
        await Assert.That(rig.Scheduler.IsLeased(bot)).IsFalse();
        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(metrics.DueQueueDepth).IsEqualTo(0);
        await Assert.That(metrics.TotalStepsRun).IsEqualTo(1);
        await Assert.That(metrics.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyDueBots_AreProcessed_PerCycle()
    {
        using var rig = new Rig();
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        var botC = rig.AddActiveBot(3);

        rig.Scheduler.WakeAt(botA, rig.Now + TimeSpan.FromMilliseconds(10));
        rig.Scheduler.WakeAt(botB, rig.Now + TimeSpan.FromMilliseconds(50));
        rig.Scheduler.WakeAt(botC, rig.Now + TimeSpan.FromMilliseconds(100));

        rig.Time.Advance(TimeSpan.FromMilliseconds(11));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(botA) == 1);
        await Assert.That(rig.Executor.CountStarts(botB)).IsEqualTo(0);
        await Assert.That(rig.Executor.CountStarts(botC)).IsEqualTo(0);

        rig.Time.Advance(TimeSpan.FromMilliseconds(40));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(botB) == 1);
        await Assert.That(rig.Executor.CountStarts(botA)).IsEqualTo(1);
        await Assert.That(rig.Executor.CountStarts(botC)).IsEqualTo(0);

        rig.Time.Advance(TimeSpan.FromMilliseconds(50));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(botC) == 1);
        await Assert.That(rig.Executor.CountStarts(botA)).IsEqualTo(1);
        await Assert.That(rig.Executor.CountStarts(botB)).IsEqualTo(1);

        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(metrics.DueQueueDepth).IsEqualTo(0);
        await Assert.That(metrics.TotalDuePopped).IsEqualTo(3);
        await Assert.That(metrics.LastCycleDue).IsEqualTo(1);
        await Assert.That(metrics.MaxCycleDue).IsEqualTo(1);
    }

    [Test]
    public async Task Wake_EventQueue_RunsDormantBot_OnNextCycle()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        rig.Scheduler.Wake(bot); // event, not a scheduled due
        rig.Pump();

        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(1);
        await Assert.That(rig.Scheduler.GetMetrics().EventQueueDepth).IsEqualTo(0);
    }

    [Test]
    public async Task WakeAfter_SchedulesRelativeToNow()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        rig.Scheduler.WakeAfter(bot, TimeSpan.FromMilliseconds(20));
        rig.Time.Advance(TimeSpan.FromMilliseconds(19));
        rig.Pump();
        await Assert.That(rig.Executor.Starts).IsEmpty();

        rig.Time.Advance(TimeSpan.FromMilliseconds(2));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
    }

    #endregion

    #region Bounded pool + per-bot lease

    [Test]
    public async Task SlowBot_DoesNotBlockOthers()
    {
        using var rig = new Rig(workerCount: 4);
        var slow = rig.AddActiveBot(1);
        var fastA = rig.AddActiveBot(2);
        var fastB = rig.AddActiveBot(3);

        rig.Executor.DelayFor = id => id == slow ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromMilliseconds(25);

        rig.Scheduler.WakeAt(slow, rig.Now);
        rig.Scheduler.WakeAt(fastA, rig.Now);
        rig.Scheduler.WakeAt(fastB, rig.Now);
        rig.Pump();

        await rig.WaitUntilAsync(() => rig.Executor.Finishes.Count >= 3, TimeSpan.FromSeconds(15));

        // Deterministic gates (no wall-clock comparison — load-immune):
        // 1) The slow bot's step overlapped at least one fast bot's step in
        //    time — a serialized scheduler can never produce an overlap.
        // 2) ≥2 steps ran concurrently overall.
        var slowOverlapsFast = rig.Executor.Overlaps.Any(o =>
            (o.A == slow && (o.B == fastA || o.B == fastB)) ||
            (o.B == slow && (o.A == fastA || o.A == fastB)));
        await Assert.That(slowOverlapsFast).IsTrue();
        await Assert.That(rig.Executor.MaxConcurrentOverall).IsGreaterThanOrEqualTo(2);
        await Assert.That(rig.Scheduler.GetMetrics().WorkerUtilization).IsGreaterThan(0);
    }

    [Test]
    public async Task PerBotLease_NoConcurrentStepsForSameBot_WakeWhileLeasedIsHonored()
    {
        using var rig = new Rig(workerCount: 4);
        var bot = rig.AddActiveBot(1);
        rig.Executor.DelayFor = _ => TimeSpan.FromMilliseconds(150);

        rig.Scheduler.WakeAt(bot, rig.Now);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);

        // A second wake arrives while the first step is still in flight.
        rig.Scheduler.Wake(bot);
        rig.Pump();
        await Task.Delay(50);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(1); // still only one concurrent step
        await Assert.That(rig.Executor.MaxConcurrentPerBot[bot]).IsEqualTo(1);

        // After the first step completes, the pending wake is honored — the
        // re-scheduled due entry is popped by the next scan cycle.
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().DueQueueDepth == 1);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 2);
        await Assert.That(rig.Executor.MaxConcurrentPerBot[bot]).IsEqualTo(1);
        await rig.WaitUntilAsync(() => !rig.Scheduler.IsLeased(bot));
    }

    [Test]
    public async Task DuplicateSchedules_SameCycle_ExecuteOnce()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        rig.Scheduler.WakeAt(bot, rig.Now + TimeSpan.FromMilliseconds(5));
        rig.Scheduler.WakeAt(bot, rig.Now + TimeSpan.FromMilliseconds(5));
        rig.Time.Advance(TimeSpan.FromMilliseconds(6));
        rig.Pump();

        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
        await Task.Delay(100);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(1);
    }

    #endregion

    #region Cadence: next-delay vs dormant

    [Test]
    public async Task ExecutorReturningDelay_ReschedulesBot()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Executor.NextDelay = TimeSpan.FromMilliseconds(30);

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
        // Reschedule happens on step COMPLETION — wait for the lease to
        // release so the +30ms due entry exists before we advance time.
        await rig.WaitUntilAsync(() => !rig.Scheduler.IsLeased(bot));

        rig.Time.Advance(TimeSpan.FromMilliseconds(31));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 2);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(2);
    }

    [Test]
    public async Task ExecutorReturningNull_GoesDormant_UntilExternalWake()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Executor.NextDelay = null; // dormant

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);

        rig.Time.Advance(TimeSpan.FromSeconds(5));
        rig.Pump();
        await Task.Delay(50);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(1); // no auto re-run

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 2);
    }

    #endregion

    #region Registry consumption + failures

    [Test]
    public async Task DeactivatedBot_StepSkipped_NotExecuted()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        rig.Scheduler.WakeAt(bot, rig.Now);
        rig.Manager.Deactivate(bot, "rig test");
        rig.Pump();

        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsSkipped == 1);
        await Assert.That(rig.Executor.Starts).IsEmpty();
        await Assert.That(rig.Scheduler.IsLeased(bot)).IsFalse();
        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(metrics.TotalStepsRun).IsEqualTo(0);
        await Assert.That(metrics.DueQueueDepth).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownBot_StepSkipped_NotExecuted()
    {
        using var rig = new Rig();

        rig.Scheduler.WakeAt(9999, rig.Now);
        rig.Pump();

        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsSkipped == 1);
        await Assert.That(rig.Executor.Starts).IsEmpty();
    }

    [Test]
    public async Task ThrowingStep_CountedAsFailed_LeaseReleased_NoReschedule()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Executor.Throw = true;

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsFailed == 1);

        await Assert.That(rig.Scheduler.IsLeased(bot)).IsFalse();
        await Task.Delay(100);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(1); // no auto retry spin
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(0);
    }

    [Test]
    public async Task StepTimeout_CountsTimeout_ReleasesLease()
    {
        using var rig = new Rig(stepTimeout: TimeSpan.FromMilliseconds(50));
        var bot = rig.AddActiveBot(1);
        rig.Executor.DelayFor = _ => TimeSpan.FromSeconds(5);
        rig.Executor.IgnoreCancellation = true; // executor ignores the token — scheduler must enforce

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsTimedOut == 1);

        await Assert.That(rig.Scheduler.IsLeased(bot)).IsFalse();
        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(metrics.TotalStepsRun).IsEqualTo(0);
        await Assert.That(metrics.ActiveWorkers).IsEqualTo(0);
    }

    #endregion

    #region Metrics

    [Test]
    public async Task Metrics_WakeLatency_QueueDepth_DuePerCycle()
    {
        using var rig = new Rig();
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);

        // Both due 50ms in the past when the cycle pops them → latency ≈ 50ms each.
        rig.Scheduler.WakeAt(botA, rig.Now - TimeSpan.FromMilliseconds(50));
        rig.Scheduler.WakeAt(botB, rig.Now - TimeSpan.FromMilliseconds(50));
        await Assert.That(rig.Scheduler.GetMetrics().DueQueueDepth).IsEqualTo(2);

        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsRun == 2);

        var metrics = rig.Scheduler.GetMetrics();
        await Assert.That(metrics.TotalDuePopped).IsEqualTo(2);
        await Assert.That(metrics.LastCycleDue).IsEqualTo(2);
        await Assert.That(metrics.MaxCycleDue).IsEqualTo(2);
        await Assert.That(metrics.TotalWakeLatencyMs).IsGreaterThanOrEqualTo(100); // 2 × ~50ms
        await Assert.That(metrics.AverageWakeLatencyMs).IsGreaterThanOrEqualTo(50);
        await Assert.That(metrics.MaxWakeLatencyMs).IsGreaterThanOrEqualTo(50);
        await Assert.That(metrics.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task Metrics_WorkerUtilization_TracksBusyTime()
    {
        using var rig = new Rig(workerCount: 4);
        var bot = rig.AddActiveBot(1);
        rig.Executor.DelayFor = _ => TimeSpan.FromMilliseconds(200);

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.Finishes.Any());

        await Assert.That(rig.Scheduler.GetMetrics().WorkerUtilization).IsGreaterThan(0);
        await Assert.That(rig.Scheduler.GetMetrics().WorkerUtilization).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Metrics_ActiveWorkers_TracksConcurrentSteps()
    {
        using var rig = new Rig(workerCount: 4);
        var bot = rig.AddActiveBot(1);
        rig.Executor.DelayFor = _ => TimeSpan.FromMilliseconds(200);

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);

        await Assert.That(rig.Scheduler.GetMetrics().ActiveWorkers).IsGreaterThanOrEqualTo(1);
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().ActiveWorkers == 0);
    }

    #endregion

    #region Lifecycle + configuration

    [Test]
    public async Task StopAsync_DrainsQueuedSteps_Gracefully()
    {
        using var rig = new Rig();
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        rig.Executor.DelayFor = _ => TimeSpan.FromMilliseconds(50);

        rig.Scheduler.WakeAt(botA, rig.Now);
        rig.Scheduler.WakeAt(botB, rig.Now);
        rig.Pump();

        await rig.Scheduler.StopAsync();
        await Assert.That(rig.Executor.CountStarts(botA)).IsEqualTo(1);
        await Assert.That(rig.Executor.CountStarts(botB)).IsEqualTo(1);
        await Assert.That(rig.Scheduler.IsRunning).IsFalse();
        await Assert.That(rig.Scheduler.Wake(99)).IsFalse(); // stopped → refuse
    }

    [Test]
    public async Task StopWithoutStart_IsNoOp()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());
        var scheduler = new PlayerBotScheduler(manager, new RecordingExecutor());
        await scheduler.StopAsync();
        await Assert.That(scheduler.IsRunning).IsFalse();
    }

    [Test]
    public async Task WorkerCount_ClampedToSpecBounds()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());

        var low = new PlayerBotScheduler(manager, new RecordingExecutor(), new PlayerBotSchedulerOptions { WorkerCount = 2 });
        await Assert.That(low.WorkerCount).IsEqualTo(4);

        var high = new PlayerBotScheduler(manager, new RecordingExecutor(), new PlayerBotSchedulerOptions { WorkerCount = 16 });
        await Assert.That(high.WorkerCount).IsEqualTo(8);

        var dflt = new PlayerBotScheduler(manager, new RecordingExecutor());
        await Assert.That(dflt.WorkerCount).IsEqualTo(4);
    }

    #endregion
}
