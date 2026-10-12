using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Network.Connections;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;

using Microsoft.Extensions.Time.Testing;

using TUnit.Core;
using TUnit.Core.Interfaces;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Scheduler rig for IPlayerBotScheduler (slice #6): due-time semantics,
/// bounded worker pool, per-bot execution lease, event wakes, metrics, the
/// hard gate (one wake-scan, no per-bot anything), and — since M5 A1 — the
/// execution-boundary marshal (steps execute on the single execution
/// boundary thread, never on worker threads). Time is driven by
/// FakeTimeProvider; scan cycles are pumped manually (scan interval is set
/// to 1h so the background loop never fires mid-test), the marshal queue is
/// drained by the rig's waits, and the worker pool runs for real.
///
/// Execution boundary: every test pins the boundary to the test thread (the
/// simulated game loop); drains run on that thread. The
/// <see cref="ExecutionBoundary"/> static state is reset per test.
/// </summary>
[NotInParallel]
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
public class PlayerBotSchedulerTests
{
    /// <summary>Deterministic lifecycle seam (same shape as the manager rig).</summary>
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool Called { get; private set; }

        public bool ActivateHeadless(Character character, object? botContext)
        {
            Called = true;
            return true;
        }

        public bool Deactivate(Character character, string reason) => true;
    }

    /// <summary>
    /// Minimal live-connection stub (E2eNullSession shape at BotDriveBridge.cs:3656-3667).
    /// Packets vanish; the connection only needs to be non-null on the character.
    /// </summary>
    private sealed class NullSession : ISession
    {
        private readonly Dictionary<string, object> _attributes = [];
        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 0xE2E1;
        public Socket Socket => null!;
        public void SendPacket(byte[] packet) { }
        public void AddAttribute(string name, object attribute) => _attributes[name] = attribute;
        public object GetAttribute(string name) => _attributes.GetValueOrDefault(name)!;
        public void ClearAttribute(string name) => _attributes.Remove(name);
        public void Close() { }
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
        public ConcurrentQueue<int> StepThreadIds { get; } = [];
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
            StepThreadIds.Enqueue(Environment.CurrentManagedThreadId);

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
            // M5 A1: the boundary is AsyncLocal-scoped and the drain pins
            // itself to its own thread on every entry — no explicit pin here
            // (async test continuations may hop threads; the pin follows the
            // drain, tests that need a fixed boundary pin it explicitly).
            Manager = new PlayerBotManager(new RecordingLifecycle());
            var options = new PlayerBotSchedulerOptions
            {
                WorkerCount = workerCount,
                ScanInterval = TimeSpan.FromHours(1), // loop inert in tests; cycles pumped manually
                StepTimeout = stepTimeout ?? TimeSpan.FromSeconds(30),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false, // tests pump the marshal drain manually
            };
            Scheduler = new PlayerBotScheduler(Manager, Executor, options, Time);
            Scheduler.Start();
        }

        /// <summary>Spawns + activates a bot through the real manager registry.</summary>
        public uint AddActiveBot(uint id, string name = "bot")
        {
            // M6.2 death watch: rig bots must be ALIVE (Hp/MaxHp default to
            // 0, which is IsDead — the watch would intercept every step).
            Manager.Spawn(new Character(new UnitCustomModelParams()) { Id = id, Name = name, MaxHp = 100, Hp = 100 }, "rig");
            Manager.Activate(id, null, "rig");
            return id;
        }

        public uint AddRegisteredBot(uint id, string name = "bot")
        {
            Manager.Spawn(new Character(new UnitCustomModelParams()) { Id = id, Name = name, MaxHp = 100, Hp = 100 }, "rig");
            return id;
        }

        public void Pump() => Scheduler.RunScanCycle();

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                // M5 A1: worker threads only marshal steps to the execution
                // queue; the drain (run on the test thread — the simulated
                // game loop) is what executes them. Waits pump the drain so
                // step execution is observable from the test thread.
                Scheduler.DrainTickQueue();
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
    public async Task BotSteps_SerializeOnTheExecutionBoundary()
    {
        // M5 A1: steps execute on the single execution boundary (the game-loop
        // thread) — the world-mutating part of a bot step is SERIALIZED by
        // design. The old worker pool no longer executes steps; it only
        // marshals wakes. This test proves the new contract: three due bots
        // all run, never concurrently, on the boundary thread, with zero
        // execution-boundary violations.
        using var rig = new Rig(workerCount: 4);
        var slow = rig.AddActiveBot(1);
        var fastA = rig.AddActiveBot(2);
        var fastB = rig.AddActiveBot(3);

        rig.Executor.DelayFor = id => id == slow ? TimeSpan.FromMilliseconds(60) : TimeSpan.FromMilliseconds(20);

        rig.Scheduler.WakeAt(slow, rig.Now);
        rig.Scheduler.WakeAt(fastA, rig.Now);
        rig.Scheduler.WakeAt(fastB, rig.Now);
        rig.Pump();
        var violationsBefore = ExecutionBoundary.ViolationCount;

        await rig.WaitUntilAsync(() => rig.Executor.Finishes.Count >= 3, TimeSpan.FromSeconds(15));

        // Deterministic gates:
        // 1) No two steps ever overlapped in time — serialized execution.
        // 2) The execution-boundary assertion never fired (every step ran on
        //    the drain thread — the boundary — at the moment it executed).
        await Assert.That(rig.Executor.MaxConcurrentOverall).IsEqualTo(1);
        await Assert.That(rig.Executor.Overlaps).IsEmpty();
        await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(violationsBefore);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(3);
    }

    [Test]
    public async Task PerBotLease_NoConcurrentStepsForSameBot_WakeWhileLeasedIsHonored()
    {
        using var rig = new Rig(workerCount: 4);
        var bot = rig.AddActiveBot(1);
        rig.Executor.DelayFor = _ => TimeSpan.FromMilliseconds(150);

        rig.Scheduler.WakeAt(bot, rig.Now);
        rig.Pump();
        // The lease is held from due-pop until the drain completes the step —
        // the step now sits in the marshal queue (not yet executed).

        // A second wake arrives while the bot is leased (step queued).
        rig.Scheduler.Wake(bot);
        rig.Pump();
        await Task.Delay(50);
        // Nothing may execute concurrently: the step is still leased; the
        // wake folded into the pending wake.
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(0);
        await Assert.That(rig.Executor.MaxConcurrentPerBot.TryGetValue(bot, out var m) ? m : 0).IsEqualTo(0);

        // The drain executes the step; on completion the pending wake is
        // honored — exactly ONE re-scheduled due entry, no duplicate.
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
        await Assert.That(rig.Executor.MaxConcurrentPerBot[bot]).IsEqualTo(1);
        await Assert.That(rig.Scheduler.GetMetrics().DueQueueDepth).IsEqualTo(1);

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

    /// <summary>
    /// REGRESSION for the permanent-drop dormancy: the pre-flag executor
    /// returned null whenever a bot had nothing live, and the scheduler has no
    /// else-branch on a null delay — so one idle wake dropped that citizen for
    /// the rest of the process. With the executor's idle-wake gate ON the very
    /// same scheduler re-arms the bot on its own, with NO external wake and NO
    /// scheduler change. Driven entirely by the fake clock: no wall-clock
    /// sleeping stands in for the re-arm.
    /// </summary>
    [Test]
    public async Task RoamExecutor_IdleWakeEnabled_ReArmsItself_WithoutExternalWake()
    {
        var idleInterval = TimeSpan.FromSeconds(5);

        // A module-free arbiter keeps the rig to the executor path (no world).
        var manager = new PlayerBotManager(new RecordingLifecycle());
        var roam = new BotRoamStepExecutor { EnableIdleWake = true, IdleWakeInterval = idleInterval };
        var executor = new IdleAwareExecutor(roam);
        var scheduler = new PlayerBotScheduler(manager, executor,
            new PlayerBotSchedulerOptions
            {
                ScanInterval = TimeSpan.FromHours(1), // background loop inert; cycles pumped manually
                SubscribeToTickManager = false,
            },
            rigTime);
        scheduler.Start();

        try
        {
            manager.Spawn(new Character(new UnitCustomModelParams())
            {
                Id = 7, Name = "idle-citizen", MaxHp = 100, Hp = 100,
            }, "rig");
            manager.Activate(7, null, "rig");

            scheduler.Wake(7);
            scheduler.RunScanCycle();
            await WaitUntilAsync(scheduler, () => executor.CountStarts(7) == 1);
            await WaitUntilAsync(scheduler, () => !scheduler.IsLeased(7));

            // No external wake from here on. Only the fake clock moves.
            rigTime.Advance(idleInterval);
            scheduler.RunScanCycle();
            await WaitUntilAsync(scheduler, () => executor.CountStarts(7) == 2);

            // And again — the bot keeps re-arming itself, indefinitely.
            rigTime.Advance(idleInterval);
            scheduler.RunScanCycle();
            await WaitUntilAsync(scheduler, () => executor.CountStarts(7) == 3);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    /// <summary>
    /// The default-off arm: the SAME real roam executor through the SAME real
    /// scheduler still drops an idle citizen permanently, so the re-arm above
    /// is the flag and nothing else. (The scheduler's own null-delay contract
    /// is pinned separately by <c>ExecutorReturningNull_GoesDormant_*</c>.)
    /// </summary>
    [Test]
    public async Task RoamExecutor_IdleWakeDisabled_StaysDormant_WithoutExternalWake()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());
        var roam = new BotRoamStepExecutor { EnableIdleWake = false };
        var executor = new IdleAwareExecutor(roam);
        var scheduler = new PlayerBotScheduler(manager, executor,
            new PlayerBotSchedulerOptions
            {
                ScanInterval = TimeSpan.FromHours(1),
                SubscribeToTickManager = false,
            },
            rigTime);
        scheduler.Start();

        try
        {
            manager.Spawn(new Character(new UnitCustomModelParams())
            {
                Id = 8, Name = "dormant-citizen", MaxHp = 100, Hp = 100,
            }, "rig");
            manager.Activate(8, null, "rig");

            scheduler.Wake(8);
            scheduler.RunScanCycle();
            await WaitUntilAsync(scheduler, () => executor.CountStarts(8) == 1);
            await WaitUntilAsync(scheduler, () => !scheduler.IsLeased(8));

            // Five idle intervals with no external wake: still exactly one wake.
            rigTime.Advance(TimeSpan.FromSeconds(5));
            scheduler.RunScanCycle();
            await Task.Delay(50);
            await Assert.That(executor.CountStarts(8)).IsEqualTo(1);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    /// <summary>
    /// Counts the wakes the scheduler delivers to one bot and delegates the
    /// step itself, so a test can drive the REAL roam executor (the surface
    /// that owns the dormancy decision) through the REAL scheduler.
    /// </summary>
    private sealed class IdleAwareExecutor(BotRoamStepExecutor inner) : IBotStepExecutor
    {
        private readonly ConcurrentQueue<uint> _starts = [];

        public int CountStarts(uint botId) => _starts.Count(id => id == botId);

        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            _starts.Enqueue(bot.CharacterId);
            return inner.StepAsync(bot, cancellationToken);
        }
    }

    // Shared fake clock for the two executor-driven regression rigs above (the
    // Rig owns its own clock; these construct the scheduler by hand).
    private static readonly FakeTimeProvider rigTime = new(DateTime.UtcNow);

    /// <summary>
    /// The Rig wait loop's M5 A1 discipline: worker threads only marshal a step
    /// onto the execution-boundary queue, so the wait pumps the drain (running
    /// on this thread — the simulated game loop) or no step ever executes.
    /// </summary>
    private static async Task WaitUntilAsync(PlayerBotScheduler scheduler, Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            scheduler.DrainTickQueue();
            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("rig wait condition not met");
            await Task.Delay(5);
        }
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
    public async Task ThrowingStep_RetriesOnceThenFaults()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Executor.Throw = true;

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsFailed == 1);

        await Assert.That(rig.Scheduler.IsLeased(bot)).IsFalse();

        // First consecutive failure => one retry at ScanInterval (1h rig clock).
        rig.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 2);
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsFailed == 2);
        await Assert.That(rig.Scheduler.IsLeased(bot)).IsFalse();

        // Second consecutive failure => Faulted, nothing scheduled.
        await rig.WaitUntilAsync(() => rig.Scheduler.GetBotStepState(bot)?.LastOutcome
            == PlayerBotScheduler.BotStepOutcomeKind.Faulted);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(0);

        // Quiescence: no third start.
        rig.Time.Advance(TimeSpan.FromHours(2));
        rig.Pump();
        await Task.Delay(300);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(2);
    }

    [Test]
    public async Task FailedStep_FirstFailure_RetriesOnce()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Executor.Throw = true;

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsFailed == 1);

        // Retry succeeds: exactly one retry start, then dormant (null delay).
        rig.Executor.Throw = false;
        rig.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 2);
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsRun == 1);
        await Assert.That(rig.Scheduler.GetBotStepState(bot)?.LastOutcome)
            .IsEqualTo(PlayerBotScheduler.BotStepOutcomeKind.Ran);
    }

    [Test]
    public async Task FailedStep_SuccessResetsStreak()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Executor.Throw = true;

        // Fail once (streak 1), then retry with a fix: success resets the streak.
        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsFailed == 1);
        rig.Executor.Throw = false;
        rig.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsRun == 1);

        // Fail again (streak 1 anew — not a fault), so it retries once more.
        rig.Executor.Throw = true;
        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsFailed == 2);
        rig.Executor.Throw = false;
        rig.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 4);
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsRun == 2);

        // Four starts, two runs, never faulted.
        await Assert.That(rig.Scheduler.GetBotStepState(bot)?.LastOutcome)
            .IsEqualTo(PlayerBotScheduler.BotStepOutcomeKind.Ran);
    }

    [Test]
    public async Task FailedStep_ExternalWake_RevivesFaulted()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Executor.Throw = true;

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetMetrics().TotalStepsFailed == 1);
        rig.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Scheduler.GetBotStepState(bot)?.LastOutcome
            == PlayerBotScheduler.BotStepOutcomeKind.Faulted);
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(2);

        // An external wake revives the faulted bot: it steps again.
        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 3);
    }

    [Test]
    public async Task EnrollmentSnapshot_UnknownBot_IsNoEntry()
    {
        using var rig = new Rig();
        var snap = rig.Scheduler.GetEnrollmentSnapshot(9999);
        await Assert.That(snap.Registered).IsFalse();
        await Assert.That(snap.Disposition).IsEqualTo("no-entry");
        await Assert.That(snap.SchedulerRunning).IsTrue();
    }

    [Test]
    public async Task EnrollmentSnapshot_ActiveBotWithNoWake_IsEntryNeverDue()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        var snap = rig.Scheduler.GetEnrollmentSnapshot(bot);
        await Assert.That(snap.Registered).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(PlayerBotState.Active));
        await Assert.That(snap.ScheduledDueUtc).IsNull();
        await Assert.That(snap.Disposition).IsEqualTo("entry-never-due");
    }

    [Test]
    public async Task EnrollmentSnapshot_ScheduledBot_IsDueNeverExecuted_ThenExecuted()
    {
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Scheduler.WakeAt(bot, rig.Now + TimeSpan.FromMilliseconds(10));
        rig.Pump(); // not due yet: entry held, nothing executed
        var before = rig.Scheduler.GetEnrollmentSnapshot(bot);
        await Assert.That(before.ScheduledDueUtc).IsNotNull();
        await Assert.That(before.Disposition).IsEqualTo("due-never-executed");
        rig.Time.Advance(TimeSpan.FromMilliseconds(11));
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
        await rig.WaitUntilAsync(() => !rig.Scheduler.IsLeased(bot));
        var after = rig.Scheduler.GetEnrollmentSnapshot(bot);
        await Assert.That(after.Disposition).IsEqualTo("executed");
        await Assert.That(after.LastOutcome).IsEqualTo(nameof(PlayerBotScheduler.BotStepOutcomeKind.Ran));
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
    public async Task Metrics_ActiveWorkers_ReturnsToZeroAfterStepCompletes()
    {
        // M5 A1: steps execute synchronously on the execution boundary, so
        // ActiveWorkers is only transiently > 0 DURING a step (not observable
        // from the test thread) and is 0 after completion. This asserts the
        // post-completion state and that busy time was tracked.
        using var rig = new Rig(workerCount: 4);
        var bot = rig.AddActiveBot(1);
        rig.Executor.DelayFor = _ => TimeSpan.FromMilliseconds(20);

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);

        await Assert.That(rig.Scheduler.GetMetrics().ActiveWorkers).IsEqualTo(0);
        await Assert.That(rig.Scheduler.GetMetrics().WorkerUtilization).IsGreaterThan(0);
        await Assert.That(rig.Scheduler.GetMetrics().WorkerUtilization).IsLessThanOrEqualTo(1);
    }

    #endregion

    #region Lifecycle + configuration

    [Test]
    public async Task StopAsync_CompletedStepsPersist_NewWorkRefused()
    {
        using var rig = new Rig();
        var botA = rig.AddActiveBot(1);
        var botB = rig.AddActiveBot(2);
        rig.Executor.DelayFor = _ => TimeSpan.FromMilliseconds(10);

        rig.Scheduler.WakeAt(botA, rig.Now);
        rig.Scheduler.WakeAt(botB, rig.Now);
        rig.Pump();
        // Steps execute on the boundary drain — wait until both ran.
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(botA) == 1 && rig.Executor.CountStarts(botB) == 1);

        await rig.Scheduler.StopAsync();
        await Assert.That(rig.Executor.CountStarts(botA)).IsEqualTo(1);
        await Assert.That(rig.Executor.CountStarts(botB)).IsEqualTo(1);
        await Assert.That(rig.Scheduler.IsRunning).IsFalse();
        await Assert.That(rig.Scheduler.Wake(99)).IsFalse(); // stopped → refuse
    }

    [Test]
    public async Task StopAsync_UndrainedQueuedSteps_AreDropped_NotExecuted()
    {
        // M5 A1: steps only execute on the execution boundary (the game loop).
        // At shutdown the boundary is gone, so queued-but-undrained steps are
        // dropped instead of executing off-thread — nothing runs after stop.
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        var violationsBefore = ExecutionBoundary.ViolationCount;

        rig.Scheduler.WakeAt(bot, rig.Now);
        rig.Pump(); // scan pops the due entry; workers marshal it (async)

        await rig.Scheduler.StopAsync();
        // Unsubscribe is processed at the next tick, so the current tick may
        // still call its already-listed drain after shutdown/ordinary Stop.
        rig.Scheduler.DrainTickQueue();
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(0);
        await Assert.That(rig.Scheduler.IsRunning).IsFalse();
        await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(violationsBefore);
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

    #region M5 A1 — execution boundary (thread affinity)

    [Test]
    public async Task BotStep_ExecutesOnTheExecutionBoundaryThread()
    {
        // M5 A1 acceptance: "a debug thread-affinity assertion proves zero
        // Character/world mutation off the single execution boundary".
        // FAIL-BEFORE: with the old worker-pool execution, steps run on
        // worker threads — the assertion fires and this test is RED.
        // PASS-AFTER: steps marshal onto the boundary (the drain) — zero
        // violations.
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        var violationsBefore = ExecutionBoundary.ViolationCount;

        rig.Scheduler.Wake(bot);
        rig.Pump();
        // Drain once explicitly: pins the boundary and executes the step on
        // the drain thread. Any step that does NOT run inside a drain (e.g.
        // a regression to worker-pool execution) fires the assertion.
        rig.Scheduler.DrainTickQueue();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);

        await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(violationsBefore);
    }

    [Test]
    public async Task BotStep_OffBoundaryExecution_IsDetectedAsViolation()
    {
        // Detector control: pin the boundary to an impossible thread — even
        // the correct marshal drain is then off-boundary, so the assertion
        // MUST fire. Proves the detector itself works (this is the "fail"
        // half of the fail-before/pass-after evidence).
        using var rig = new Rig();
        ExecutionBoundary.SetExecutionThreadForTest(int.MaxValue);
        var bot = rig.AddActiveBot(1);
        var violationsBefore = ExecutionBoundary.ViolationCount;

        rig.Scheduler.Wake(bot);
        rig.Pump();
        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);

        await Assert.That(ExecutionBoundary.ViolationCount).IsGreaterThan(violationsBefore);
    }

    [Test]
    public async Task TransformWrite_InsideBotStep_OnBoundaryThread_NoViolation()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        var violationsBefore = ExecutionBoundary.ViolationCount;
        try
        {
            var pr = new PositionAndRotation();
            ExecutionBoundary.EnterBotStep();
            pr.SetPosition(1f, 2f, 3f);
            pr.SetRotationDegree(0f, 0f, 45f);
            pr.Position = new System.Numerics.Vector3(4f, 5f, 6f);
            pr.Rotation = new System.Numerics.Vector3(0f, 0f, 1f);
            ExecutionBoundary.ExitBotStep();

            await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(violationsBefore);
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
    }

    [Test]
    public async Task TransformWrite_InsideBotStep_OffBoundaryThread_Violation()
    {
        ExecutionBoundary.SetExecutionThreadForTest(int.MaxValue);
        var violationsBefore = ExecutionBoundary.ViolationCount;
        try
        {
            var pr = new PositionAndRotation();
            ExecutionBoundary.EnterBotStep();
            pr.SetPosition(1f, 2f, 3f);
            ExecutionBoundary.ExitBotStep();

            await Assert.That(ExecutionBoundary.ViolationCount).IsGreaterThan(violationsBefore);
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
    }

    [Test]
    public async Task TransformWrite_OutsideBotStep_NoViolation_AnyThread()
    {
        // Normal gameplay writes (spawning, packet handlers, loading) happen
        // on all sorts of threads and must NEVER be flagged — the write-level
        // assertion is scoped to bot-step execution.
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        var violationsBefore = ExecutionBoundary.ViolationCount;
        try
        {
            var pr = new PositionAndRotation();
            pr.SetPosition(1f, 2f, 3f); // no EnterBotStep → no assertion
            pr.Position = new System.Numerics.Vector3(4f, 5f, 6f);
            await Assert.That(ExecutionBoundary.ViolationCount).IsEqualTo(violationsBefore);
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
    }

    #endregion

    #region Gate precondition signaling (soak clearance)

    [Test]
    public async Task SchedulerLifecycle_StartedButIdle_StepCountIsZero()
    {
        // The soak-armed state: TryStart arms the scheduler but bridge-driven
        // bots are never Woken, so no step ever runs. The gate keys
        // signalsValid on (isRunning && totalStepsRun > 0) — this state must
        // report running with a zero count so the gate invalidates, never passes.
        using var rig = new Rig();

        await Assert.That(rig.Scheduler.IsRunning).IsTrue();
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(0);
    }

    [Test]
    public async Task SchedulerLifecycle_NeverStarted_IsNotRunningAndZero()
    {
        // Pre-arming state (nothing ever called Start): not running, zero
        // steps. The gate must distinguish this (scheduler down) from the
        // armed-idle state above (scheduler live, no steps yet).
        var time = new FakeTimeProvider(DateTime.UtcNow);
        var manager = new PlayerBotManager(new RecordingLifecycle());
        var scheduler = new PlayerBotScheduler(manager, new RecordingExecutor(),
            new PlayerBotSchedulerOptions { SubscribeToTickManager = false }, time);

        await Assert.That(scheduler.IsRunning).IsFalse();
        await Assert.That(scheduler.GetMetrics().TotalStepsRun).IsEqualTo(0);
    }

    [Test]
    public async Task SchedulerLifecycle_AfterDrivenStep_StepCountPositive()
    {
        // Only an actually-executed scheduler step flips the count the gate
        // precondition keys on: wake → scan → marshal → boundary drain.
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);

        rig.Scheduler.Wake(bot);
        rig.Pump();

        await rig.WaitUntilAsync(() => rig.Executor.CountStarts(bot) == 1);
        await rig.WaitUntilAsync(() => !rig.Scheduler.IsLeased(bot));

        await Assert.That(rig.Scheduler.IsRunning).IsTrue();
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(1);
    }

    #region Generation binding (P0.2 defects 4-6)

    [Test]
    public async Task Reactivation_NewGeneration_EvictsOldActor()
    {
        // Deactivate → Activate bumps the generation; an old queued step is
        // refused as StaleGeneration and never reaches the executor.
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Manager.TryGet(bot, out var rt1);
        var gen1 = rt1!.Generation;
        await Assert.That(gen1).IsEqualTo(1UL);

        rig.Scheduler.WakeAt(bot, rig.Now); // queue gen-1 step (stays leased until drained)
        rig.Pump();
        rig.Manager.Deactivate(bot, "test");
        rig.Manager.Activate(bot, null, "rig");
        rig.Manager.TryGet(bot, out var rt2);
        await Assert.That(rt2!.Generation).IsEqualTo(gen1 + 1);

        rig.Scheduler.DrainTickQueue(); // executes the stale gen-1 step
        await rig.WaitUntilAsync(() => !rig.Scheduler.IsLeased(bot));
        await Assert.That(rig.Executor.CountStarts(bot)).IsEqualTo(0);
        var outcome = rig.Scheduler.GetBotStepState(bot);
        await Assert.That(outcome!.LastOutcome).IsEqualTo(PlayerBotScheduler.BotStepOutcomeKind.StaleGeneration);
        await Assert.That(outcome!.Skipped).IsEqualTo(1);
        await Assert.That(outcome!.ConsecutiveFailures).IsEqualTo(0);
    }

    [Test]
    public async Task StaleSnapshot_GenerationMismatch_Refused()
    {
        // The enrollment snapshot carries the live generation; a caller
        // holding a pre-reactivation snapshot observes the mismatch.
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        var before = rig.Scheduler.GetEnrollmentSnapshot(bot);
        rig.Manager.Deactivate(bot, "test");
        rig.Manager.Activate(bot, null, "rig");
        var after = rig.Scheduler.GetEnrollmentSnapshot(bot);
        await Assert.That(after.Generation).IsNotEqualTo(before.Generation);
        await Assert.That(after.Generation).IsEqualTo(before.Generation + 1);
    }

    [Test]
    public async Task Remove_ThenSpawn_DrawsFreshGeneration()
    {
        // The counter is process-wide: a re-spawned bot draws a strictly
        // larger generation, so no recycled step can validate.
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Manager.TryGet(bot, out var rt1);
        var gen1 = rt1!.Generation;
        var character = rt1.Character;
        rig.Manager.Deactivate(bot, "test");
        await Assert.That(rig.Manager.Remove(bot)).IsTrue();
        await Assert.That(rig.Manager.Spawn(character, "rig")).IsTrue();
        await Assert.That(rig.Manager.Activate(bot, null, "rig")).IsTrue();
        rig.Manager.TryGet(bot, out var rt2);
        await Assert.That(rt2!.Generation).IsGreaterThan(gen1);
    }

    [Test]
    public async Task DormantMaterialize_BumpsGeneration()
    {
        // Materialize flows through Activate, so a rematerialized bot draws a
        // fresh generation (verified here through the manager path it calls).
        using var rig = new Rig();
        var bot = rig.AddActiveBot(1);
        rig.Manager.TryGet(bot, out var rt1);
        var gen1 = rt1!.Generation;
        rig.Manager.Deactivate(bot, "dormancy");
        await Assert.That(rig.Manager.Activate(bot, null, "dormant")).IsTrue();
        rig.Manager.TryGet(bot, out var rt2);
        await Assert.That(rt2!.Generation).IsEqualTo(gen1 + 1);
    }

    #endregion
    #region Live-connection guard

    [Test]
    public async Task Activate_RefusesCharacterWithLiveConnection()
    {
        var lifecycle = new RecordingLifecycle();
        var manager = new PlayerBotManager(lifecycle);
        var character = new Character(new UnitCustomModelParams()) { Id = 1, Name = "live", MaxHp = 100, Hp = 100 };
        await Assert.That(manager.Spawn(character, "rig")).IsTrue();

        var conn = new GameConnection(new NullSession()) { ActiveChar = character };
        character.Connection = conn;

        await Assert.That(manager.Activate(1, null, "rig")).IsFalse();
        await Assert.That(manager.GetDiagnostics().FailedActivations).IsEqualTo(1);
        await Assert.That(lifecycle.Called).IsFalse();
        await Assert.That(manager.TryGet(1, out var runtime)).IsTrue();
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.Registered);
    }

    [Test]
    public async Task Activate_AllowsNullConnectionCharacter()
    {
        var lifecycle = new RecordingLifecycle();
        var manager = new PlayerBotManager(lifecycle);
        var character = new Character(new UnitCustomModelParams()) { Id = 2, Name = "headless", MaxHp = 100, Hp = 100 };
        await Assert.That(manager.Spawn(character, "rig")).IsTrue();

        await Assert.That(manager.Activate(2, null, "rig")).IsTrue();
        await Assert.That(lifecycle.Called).IsTrue();
    }

    #endregion

    #endregion
}
