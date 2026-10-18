using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.StaticValues;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Vertical decision-path proof: the REAL scheduler drives the REAL roam executor
/// driving the REAL headless actor. No RecordingExecutor, no fake actors.
///
/// Each test signs a bot up through the real manager (Spawn + Activate), arms
/// real state, and pumps real scheduler scan cycles (RunScanCycle + boundary
/// drain) while asserting wake verdicts on the live actor (CurrentTarget,
/// ActiveRequest, AuditTrace) — covering the five mandated behaviors:
/// delayed actions, disappearing opportunities, refusals, interruptions, and
/// subsequent choices. The counting wrapper only counts wakes and delegates
/// every step to the production executor.
/// </summary>
[NotInParallel]
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
public class BotDecisionVerticalTests
{
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool RetirementResult { get; set; } = true;
        public List<(PlayerBotRuntime Owner, ulong Generation, string Reason)> RetirementCalls { get; } = [];

        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason)
        {
            RetirementCalls.Add((expectedOwner, expectedGeneration, reason));
            return RetirementResult;
        }

        public bool ActivateHeadless(Character character, object? botContext) => true;
        public bool Deactivate(Character character, string reason) => true;
    }

    /// <summary>Count-only decorator: every step runs the REAL inner executor.</summary>
    private sealed class CountingExecutor(BotRoamStepExecutor inner) : IBotStepExecutor
    {
        private readonly ConcurrentQueue<uint> _starts = [];
        public int CountStarts(uint botId) => _starts.Count(id => id == botId);
        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            _starts.Enqueue(bot.CharacterId);
            return inner.StepAsync(bot, cancellationToken);
        }
    }

    private sealed class Rig : IDisposable
    {
        public required GameplayActor Actor { get; init; }
        public required BotRoamStepExecutor Executor { get; init; }
        public required CountingExecutor Counting { get; init; }
        public required PlayerBotManager Manager { get; init; }
        public required PlayerBotScheduler Scheduler { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required uint BotId { get; init; }

        public async Task DrainUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                Scheduler.DrainTickQueue();
                if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("vertical rig wait condition not met");
                await Task.Delay(5);
            }
        }

        /// <summary>One scheduler-delivered wake: advance past the live cadence, scan, drain.</summary>
        public async Task PumpRoundAsync(int targetStarts)
        {
            Time.Advance(TimeSpan.FromMilliseconds(150));
            Scheduler.Wake(BotId);
            Scheduler.RunScanCycle();
            await DrainUntilAsync(() => Counting.CountStarts(BotId) >= targetStarts);
        }

        public void Dispose() => Scheduler.StopAsync().GetAwaiter().GetResult();
    }

    private static Rig CreateRig(string name, Func<BotRoamStepExecutor, BotRoamStepExecutor>? configure = null)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var time = new FakeTimeProvider(DateTime.UtcNow);

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = time,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
        };
        if (configure != null)
            executor = configure(executor);

        var manager = new PlayerBotManager(new RecordingLifecycle());
        var counting = new CountingExecutor(executor);
        var scheduler = new PlayerBotScheduler(manager, counting,
            new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1),
                StepTimeout = TimeSpan.FromSeconds(30),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false,
            },
            time);
        scheduler.Start();

        manager.Spawn(actor.Character, "rig");
        manager.Activate(actor.Character.Id, null, "rig");
        return new Rig
        {
            Actor = actor,
            Executor = executor,
            Counting = counting,
            Manager = manager,
            Scheduler = scheduler,
            Time = time,
            BotId = actor.Character.Id,
        };
    }

    [Test]
    public async Task SchedulerDeliversDelayedRoamMove_CompletesOverMultipleWakes_SameActor()
    {
        using var rig = CreateRig("vert-delay-1");
        // Turn-one generation binding evicts any pre-turn armed state, so the
        // route is armed AFTER the first wake (the production order: Activate
        // then arm, with the first step binding the live generation).
        await rig.PumpRoundAsync(1);
        rig.Executor.SetRoamRoute(rig.Actor.Character,
            new BotPath([new Vector3(10, 0, 0), new Vector3(20, 0, 0), new Vector3(30, 0, 0)], BotPath.LoopMode.Loop));

        var guard = 0;
        while (guard++ < 300)
        {
            await rig.PumpRoundAsync(rig.Counting.CountStarts(rig.BotId) + 1);
            if (rig.Actor.AuditTrace.Any(r => r.Action == ActorActionType.Move && r.Result is ActorLifecycleState.Completed or ActorLifecycleState.Interrupted))
                break;
        }

        // Delayed action: the leg took several scheduler-delivered wakes, all on one actor.
        await Assert.That(rig.Counting.CountStarts(rig.BotId)).IsGreaterThanOrEqualTo(3);
        await Assert.That(rig.Actor.AuditTrace.Any(r => r.Action == ActorActionType.Move && r.Result is ActorLifecycleState.Completed or ActorLifecycleState.Interrupted)).IsTrue();
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.Actor).IsSameReferenceAs(rig.Actor);
        await Assert.That(rig.Actor.Character.Transform.World.Position.X).IsGreaterThan(5f);
        await Assert.That(rig.Actor.AuditTrace.Count(r => r.Action == ActorActionType.Move && r.Result == ActorLifecycleState.Rejected)).IsEqualTo(0);
        await Assert.That(rig.Actor.AuditTrace.Count(r => r.Action == ActorActionType.Move && r.Result == ActorLifecycleState.TimedOut)).IsEqualTo(0);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo((long)rig.Counting.CountStarts(rig.BotId));
    }

    [Test]
    public async Task VanishingWildlife_NextSchedulerWakes_DropTargetAndResumeRoam()
    {
        var vanished = false;
        var wildlife = new Npc
        {
            ObjId = 0x7B001,
            Hp = 100,
            MaxHp = 100,
            Faction = new SystemFaction { Id = (FactionsEnum)115 },
        };
        wildlife.Transform.Local.SetPosition(new Vector3(2, 0, 0));

        using var rig = CreateRig("vert-vanish-1");
        // Hunt seams on the live executor (same shape as the executor-level hunt rig).
        var hunt = new BotRoamStepExecutor
        {
            ActorFactory = _ => rig.Actor,
            TimeProvider = rig.Time,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
            RoamSpeed = 2.5f,
            HuntChaseSpeed = 4.5f,
            HuntPerceptionRadius = 20f,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = true,
            NearbyNpcProvider = (_, _) => vanished ? [] : [wildlife],
            UnitResolver = (_, id) => id == wildlife.ObjId && !vanished ? wildlife : null,
        };
        hunt.SetRoamRoute(rig.Actor.Character, new BotPath([new Vector3(50, 0, 0)], BotPath.LoopMode.Loop));

        // Rebuild the rig around the hunt-configured executor (same real actor, manager shape).
        var counting = new CountingExecutor(hunt);
        var scheduler = new PlayerBotScheduler(rig.Manager, counting,
            new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false,
            },
            rig.Time);
        scheduler.Start();
        var botId = rig.BotId;
        try
        {
            // Wakes 1+: the bot engages the live opportunity through scheduler delivery.
            var engaged = false;
            for (var round = 1; round <= 30 && !engaged; round++)
            {
                rig.Time.Advance(TimeSpan.FromMilliseconds(150));
                scheduler.Wake(botId);
                scheduler.RunScanCycle();
                var deadline = Stopwatch.StartNew();
                while (counting.CountStarts(botId) < round)
                {
                    scheduler.DrainTickQueue();
                    if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("engage wait not met");
                    await Task.Delay(5);
                }
                engaged = rig.Actor.Character.CurrentTarget?.ObjId == wildlife.ObjId;
            }
            await Assert.That(engaged).IsTrue();

            // The opportunity vanishes mid-fight.
            vanished = true;
            wildlife.Hp = 0;

            // Subsequent scheduler wakes: drop the stale target and resume the patrol leg.
            var resumed = false;
            for (var round = 1; round <= 30 && !resumed; round++)
            {
                rig.Time.Advance(TimeSpan.FromMilliseconds(150));
                scheduler.Wake(botId);
                scheduler.RunScanCycle();
                var prior = counting.CountStarts(botId);
                var deadline = Stopwatch.StartNew();
                while (counting.CountStarts(botId) < prior + 1)
                {
                    scheduler.DrainTickQueue();
                    if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("resume wait not met");
                    await Task.Delay(5);
                }
                resumed = rig.Actor.Character.CurrentTarget == null;
            }
            await Assert.That(resumed).IsTrue();
            await Assert.That(hunt.GetBotState(botId)!.Actor).IsSameReferenceAs(rig.Actor);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Test]
    public async Task BusyRefusal_RetryAfterCompletion_SucceedsOnSharedActor()
    {
        using var rig = CreateRig("vert-refuse-1");
        var queue = new BotActionCommandQueue(rig.Manager, rig.Scheduler, rig.Executor,
            options: new BotActionQueueOptions { SubscribeToTickManager = false },
            timeProvider: rig.Time);

        var first = queue.Enqueue("vert-refuse-1",
            new BotActionSpec(BotActionKind.Move, Destination: new Vector3(6, 0, 0), Payload: new MoveActionParams(2f)));
        queue.DrainCommands();
        await Assert.That(queue.TryGetSnapshot(first.TraceId, out var running)).IsTrue();
        await Assert.That(running.State).IsEqualTo(nameof(ActorLifecycleState.Running));

        // Refusal: a second command against the busy actor is rejected, not queued behind it.
        var refused = queue.Enqueue("vert-refuse-1", new BotActionSpec(BotActionKind.Observe));
        queue.DrainCommands();
        await Assert.That(queue.TryGetSnapshot(refused.TraceId, out var rej)).IsTrue();
        await Assert.That(rej.State).IsEqualTo(nameof(ActorLifecycleState.Rejected));
        await Assert.That(rej.Failure).IsEqualTo(nameof(ActorFailureReason.StateTransition));
        await Assert.That(rej.Detail!.Contains("busy")).IsTrue();

        // Drive the first leg to completion through scheduler-delivered wakes.
        var guard = 0;
        while (guard++ < 200)
        {
            await rig.PumpRoundAsync(rig.Counting.CountStarts(rig.BotId) + 1);
            queue.DrainCommands();
            if (queue.TryGetSnapshot(first.TraceId, out var done) && done.State == nameof(ActorLifecycleState.Completed))
                break;
        }
        await Assert.That(queue.TryGetSnapshot(first.TraceId, out var finished)).IsTrue();
        await Assert.That(finished.State).IsEqualTo(nameof(ActorLifecycleState.Completed));

        // Subsequent choice: the same command now lands on the same actor.
        var retry = queue.Enqueue("vert-refuse-1",
            new BotActionSpec(BotActionKind.Move, Destination: new Vector3(0.5f, 0, 0), Payload: new MoveActionParams(2f)));
        queue.DrainCommands();
        guard = 0;
        while (guard++ < 200)
        {
            await rig.PumpRoundAsync(rig.Counting.CountStarts(rig.BotId) + 1);
            queue.DrainCommands();
            if (queue.TryGetSnapshot(retry.TraceId, out var rdone) && rdone.State == nameof(ActorLifecycleState.Completed))
                break;
        }
        await Assert.That(queue.TryGetSnapshot(retry.TraceId, out var retried)).IsTrue();
        await Assert.That(retried.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(retried.ActorId).IsEqualTo(rig.Actor.ActorId);
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.Actor).IsSameReferenceAs(rig.Actor);
    }

    [Test]
    public async Task ForeignPreemption_ThroughScheduler_NoReissue_BotKeepsStepping()
    {
        using var rig = CreateRig("vert-preempt-1");
        // Turn-one generation binding evicts pre-turn armed state: stabilize
        // the live generation with a first wake, then arm the QUEST_TRAVEL
        // route and let one wake issue the travel leg.
        await rig.PumpRoundAsync(1);
        rig.Executor.SetRoamRoute(rig.Actor.Character, BotPath.PathTo(new Vector3(100, 0, 0)));
        rig.Executor.GetBotState(rig.BotId)!.PendingMoveOwner = "QUEST_TRAVEL";
        await rig.PumpRoundAsync(rig.Counting.CountStarts(rig.BotId) + 1);
        await Assert.That(rig.Actor.ActiveRequest).IsNotNull();
        await Assert.That(rig.Actor.ActiveRequest!.Action).IsEqualTo(ActorActionType.Move);
        rig.Actor.PreemptCurrent("quest pursuit retrack");
        rig.Actor.SetPendingMoveOwner("PURSUIT_MOVE_TO_UNIT");
        _ = rig.Actor.MoveTo(new Vector3(-100, 0, 0), 4.5f, TimeSpan.FromSeconds(30));
        var runsBefore = rig.Scheduler.GetMetrics().TotalStepsRun;

        for (var i = 0; i < 4; i++)
            await rig.PumpRoundAsync(rig.Counting.CountStarts(rig.BotId) + 1);

        // No reissue against the running preemptor, route superseded, bot still stepping.
        var moves = rig.Actor.AuditTrace.Where(r => r.Action == ActorActionType.Move).ToList();
        await Assert.That(moves.Count).IsEqualTo(1);
        await Assert.That(moves[0].Result).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(moves.Any(r => r.Result == ActorLifecycleState.Rejected)).IsFalse();
        await Assert.That(rig.Executor.GetRoamRoute(rig.BotId)).IsNull();
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsGreaterThan(runsBefore);

        // The preemptor settles: the next wakes must NOT resume the stale walk.
        _ = rig.Actor.Stop();
        await rig.PumpRoundAsync(rig.Counting.CountStarts(rig.BotId) + 1);
        await Assert.That(rig.Actor.ActiveRequest).IsNull();
        await Assert.That(rig.Actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(2);
    }

    [Test]
    public async Task RealExecutorThrow_RetriesOnceThenFaults()
    {
        using var rig = CreateRig("vert-fault-1", e =>
        {
            e.EnableWildlifeHunt = true;
            return e;
        });
        // Production failure input: the world-lookup seam throws, so the REAL
        // executor step fails and the scheduler disposition takes over.
        var failing = new BotRoamStepExecutor
        {
            ActorFactory = _ => rig.Actor,
            TimeProvider = rig.Time,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = true,
            NearbyNpcProvider = (_, _) => throw new InvalidOperationException("simulated world lookup failure"),
        };
        var counting = new CountingExecutor(failing);
        var scheduler = new PlayerBotScheduler(rig.Manager, counting,
            new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false,
            },
            rig.Time);
        scheduler.Start();
        var botId = rig.BotId;
        try
        {
            scheduler.Wake(botId);
            scheduler.RunScanCycle();
            var deadline = Stopwatch.StartNew();
            while (scheduler.GetMetrics().TotalStepsFailed < 1)
            {
                scheduler.DrainTickQueue();
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("first failure not observed");
                await Task.Delay(5);
            }

            // One retry at the scan interval, then faulted — exactly two production steps.
            rig.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1));
            scheduler.RunScanCycle();
            deadline.Restart();
            while (counting.CountStarts(botId) < 2)
            {
                scheduler.DrainTickQueue();
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("retry not observed");
                await Task.Delay(5);
            }
            deadline.Restart();
            while (scheduler.GetBotStepState(botId)?.LastOutcome != PlayerBotScheduler.BotStepOutcomeKind.Faulted)
            {
                scheduler.DrainTickQueue();
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("fault not observed");
                await Task.Delay(5);
            }
            await Assert.That(scheduler.GetMetrics().TotalStepsRun).IsEqualTo(0);

            rig.Time.Advance(TimeSpan.FromHours(2));
            scheduler.RunScanCycle();
            await Task.Delay(300);
            scheduler.DrainTickQueue();
            await Assert.That(counting.CountStarts(botId)).IsEqualTo(2);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }
}
