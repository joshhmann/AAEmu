using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;

namespace AAEmu.UnitTests.Game.Models.Game.Bots;

[NotInParallel]
public class BotDriveBridgeLifecycleTests
{
    private sealed class UnusedDirector : IPopulationDirector
    {
        private static Exception Unexpected() => new InvalidOperationException("Leave-save must not invoke unrelated population work.");
        public BotFidelity GetFidelity(uint characterId) => throw Unexpected();
        public FidelityTransitionResult TrySetFidelity(uint characterId, BotFidelity target, string reason) => throw Unexpected();
        public ServerPressure RefreshPressure() => throw Unexpected();
        public ServerPressure Pressure => throw Unexpected();
        public FidelityTransitionResult Wake(uint characterId, string reason) => throw Unexpected();
        public FidelityTransitionResult Sleep(uint characterId, string reason) => throw Unexpected();
        public int EmbodiedCount => throw Unexpected();
        public int EmbodiedInZone(uint zoneId) => throw Unexpected();
        public int EmbodiedOnActivity(string activity) => throw Unexpected();
        public PopulationDirectorMetrics GetMetrics() => throw Unexpected();
        public void RefreshProximityFidelity() => throw Unexpected();
    }

    private sealed class PhysicalLeave : IPlayerBotLifecycleService, ICharacterLifecycleService
    {
        public bool Refuse { get; set; }
        public Action<Character> BeforeLeave { get; set; }
        public List<(Character Character, long Money)> Saved { get; } = [];
        public HashSet<uint> RefuseIds { get; } = [];
        public bool RetirementResult { get; set; } = true;
        public List<(PlayerBotRuntime Owner, ulong Generation, string Reason)> RetirementCalls { get; } = [];
        public List<CharacterLifecycleReason> PhysicalReasons { get; } = [];

        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason)
        {
            RetirementCalls.Add((expectedOwner, expectedGeneration, reason));
            return RetirementResult;
        }

        public bool ActivateHeadless(Character character, object botContext) => true;
        public bool Deactivate(Character character, string reason)
        {
            if (Refuse || RefuseIds.Contains(character.Id))
                return false;
            SaveAndLeave(character);
            return true;
        }
        void ICharacterLifecycleService.Deactivate(Character character, CharacterLifecycleReason reason)
        {
            PhysicalReasons.Add(reason);
            if (Refuse || RefuseIds.Contains(character.Id))
                throw new InvalidOperationException("physical save refused");
            SaveAndLeave(character);
        }
        public void ActivateHuman(GameConnection connection, Character character)
            => throw new NotSupportedException("This fixture never activates network characters.");
        void ICharacterLifecycleService.ActivateHeadless(Character character, BotContext botContext)
            => throw new NotSupportedException("This fixture adopts an existing native test character.");
        private void SaveAndLeave(Character character)
        {
            BeforeLeave?.Invoke(character);
            lock (Saved) Saved.Add((character, character.Money));
            character.ParentWorld.RemoveObject(character);
            if (ReferenceEquals(WorldManager.Instance.GetCharacterByObjId(character.ObjId), character))
                WorldManager.Instance.TryRemoveCharacter(character.ObjId);
        }
    }

    private sealed class SpendingStep : IBotStepExecutor
    {
        public int Calls { get; private set; }
        public Task<TimeSpan?> StepAsync(PlayerBotRuntime runtime, CancellationToken cancellationToken)
        {
            Calls++;
            runtime.Character.Money -= 75;
            return Task.FromResult<TimeSpan?>(null);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly IServiceProvider _previous = SingletonContainer.ServiceProvider;
        private readonly ServiceProvider _provider;
        private readonly List<HeadlessSession> _extraSessions = [];
        public HeadlessSession Session { get; }
        public Character Character => Session.Character;
        public PhysicalLeave Lifecycle { get; } = new();
        public PlayerBotManager Manager { get; }
        public SpendingStep Step { get; } = new();
        public PlayerBotScheduler Scheduler { get; }
        public BotAdminService Admin { get; }
        public ConcurrentDictionary<string, HeadlessSession> Sessions { get; }
        public string Key => Character.Name.NormalizeName();

        public Rig(bool registered = true)
        {
            Session = GameplayActorTestRig.CreateActor("Leave" + Guid.NewGuid().ToString("N")).Session;
            Character.Money = 850;
            Manager = new PlayerBotManager(Lifecycle, Lifecycle);
            if (registered)
            {
                Manager.Spawn(Character, "native-adapter-control");
                Manager.Activate(Character.Id, null, "native-adapter-control");
            }
            Scheduler = new PlayerBotScheduler(Manager, Step, new PlayerBotSchedulerOptions
            {
                ScanInterval = TimeSpan.FromHours(1), SubscribeToTickManager = false,
                ShutdownTimeout = TimeSpan.FromSeconds(3),
            });
            Scheduler.Start();
            var executor = new BotRoamStepExecutor();
            Admin = new BotAdminService(Manager, Scheduler, new UnusedDirector(), executor);
            _provider = new ServiceCollection().AddSingleton<IPlayerBotManager>(Manager)
                .AddSingleton<IPlayerBotScheduler>(Scheduler).AddSingleton(executor).BuildServiceProvider();
            SingletonContainer.ServiceProvider = _provider;
            Sessions = (ConcurrentDictionary<string, HeadlessSession>)typeof(BotDriveBridge)
                .GetField("PersistentBotSessions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            if (!Sessions.TryAdd(Key, Session))
                throw new InvalidOperationException("Native test session identity collided.");
        }

        public Character AddForeignSameId(bool register)
        {
            var foreign = GameplayActorTestRig.CreateActor("Foreign" + Guid.NewGuid().ToString("N")).Session;
            _extraSessions.Add(foreign);
            foreign.Character.Id = Character.Id;
            foreign.Character.Money = 555;
            if (register)
            {
                Manager.Spawn(foreign.Character, "foreign-owner");
                Manager.Activate(foreign.Character.Id, null, "foreign-owner");
            }
            return foreign.Character;
        }

        public async Task DrainFollowingWake()
        {
            Scheduler.Wake(Character.Id);
            Scheduler.RunScanCycle();
            var deadline = Stopwatch.StartNew();
            while (Scheduler.IsLeased(Character.Id) && deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                Scheduler.DrainTickQueue();
                await Task.Delay(1);
            }
            await Assert.That(Scheduler.IsLeased(Character.Id)).IsFalse();
        }

        public void Dispose()
        {
            Scheduler.StopAsync().GetAwaiter().GetResult();
            ((ICollection<KeyValuePair<string, HeadlessSession>>)Sessions)
                .Remove(new KeyValuePair<string, HeadlessSession>(Key, Session));
            foreach (var session in _extraSessions.Append(Session))
            {
                session.World.RemoveObject(session.Character);
                if (ReferenceEquals(WorldManager.Instance.GetCharacterByObjId(session.Character.ObjId), session.Character))
                    WorldManager.Instance.TryRemoveCharacter(session.Character.ObjId);
            }
            SingletonContainer.ServiceProvider = _previous;
            _provider.Dispose();
        }
    }

    /// <summary>
    /// Activation is memory-only fixture setup: GameplayActorTestRig already
    /// created an ordinary Character in a real WorldInstance. Retirement and
    /// leave are the production adapter, never the recording lifecycle's ack.
    /// </summary>
    private sealed class FixtureActivation(PlayerBotLifecycleAdapter adapter) : IPlayerBotLifecycleService
    {
        public bool ActivateHeadless(Character character, object botContext) => true;
        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason)
            => adapter.TryRetireNativeWork(expectedOwner, expectedGeneration, reason);
        public bool Deactivate(Character character, string reason) => adapter.Deactivate(character, reason);
    }

    private sealed class NativeRetirementRig : IDisposable
    {
        public HeadlessSession Session { get; } =
            GameplayActorTestRig.CreateActor("Retire" + Guid.NewGuid().ToString("N")).Session;
        public Character Character => Session.Character;
        public FakeTimeProvider Time { get; } = new(DateTime.UtcNow);
        public PhysicalLeave Physical { get; } = new();
        public BotRoamStepExecutor Executor { get; }
        public PlayerBotLifecycleAdapter Adapter { get; }
        public PlayerBotManager Manager { get; }
        public PlayerBotScheduler Scheduler { get; }
        public BotAdminService Admin { get; }
        public PlayerBotRuntime Owner { get; }
        public GameplayActor Actor => (GameplayActor)Executor.GetBotState(Character.Id)!.Actor;
        private readonly object _tickQueue;
        private readonly PropertyInfo _queueCount;

        public NativeRetirementRig(TimeSpan? retirementTimeout = null)
        {
            Character.Money = 850;
            Character.Transform.Local.SetPosition(Vector3.Zero);
            // Use the production ActorFactory. No actor/request pointers are
            // injected: the first real scheduler wake creates/binds the Actor.
            Executor = new BotRoamStepExecutor
            {
                TimeProvider = Time, EnableIdleWake = false, EnableWildlifeHunt = false,
                ActiveCadence = TimeSpan.FromMilliseconds(100),
                GroundHeightProvider = (_, _) => 0f,
            };
            PlayerBotScheduler scheduler = null!;
            Adapter = new PlayerBotLifecycleAdapter(Physical, new Lazy<PlayerBotScheduler>(() => scheduler));
            Manager = new PlayerBotManager(new FixtureActivation(Adapter), Physical);
            if (!Manager.Spawn(Character, "native-retirement") || !Manager.Activate(Character.Id, null, "native-retirement"))
                throw new InvalidOperationException("Native retirement fixture enrollment failed.");
            Manager.TryGet(Character.Id, out var owner);
            Owner = owner!;
            scheduler = new PlayerBotScheduler(Manager, Executor, new PlayerBotSchedulerOptions
            {
                ScanInterval = TimeSpan.FromHours(1), SubscribeToTickManager = false,
                ShutdownTimeout = retirementTimeout ?? TimeSpan.FromSeconds(5),
            }, Time);
            Scheduler = scheduler;
            Scheduler.Start();
            Admin = new BotAdminService(Manager, Scheduler, new UnusedDirector(), Executor);
            // Read-only queue observation coordinates a queued-before-retire
            // schedule; no new production hook or private-state writes.
            _tickQueue = typeof(PlayerBotScheduler).GetField("_tickQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Scheduler)!;
            _queueCount = _tickQueue.GetType().GetProperty("Count")!;
        }

        public int Queued => (int)_queueCount.GetValue(_tickQueue)!;

        public static Task<T> OffBoundary<T>(Func<T> operation) => Task.Run(() =>
        {
            ExecutionBoundary.ResetForTest();
            return operation();
        });

        public static T OnManualBoundary<T>(Func<T> operation)
        {
            ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
            try { return operation(); }
            finally { ExecutionBoundary.ResetForTest(); }
        }

        public static async Task WaitWithoutDrain(Func<bool> ready)
        {
            var deadline = Stopwatch.StartNew();
            while (!ready())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(3))
                    throw new TimeoutException("Native retirement schedule did not reach its rendezvous.");
                await Task.Delay(1);
            }
        }

        public async Task DrainUntil(Func<bool> ready)
        {
            await WaitWithoutDrain(() =>
            {
                Scheduler.DrainTickQueue();
                return ready();
            });
        }

        public async Task QueueOrdinaryWake()
        {
            Time.Advance(TimeSpan.FromMilliseconds(150));
            Scheduler.Wake(Character.Id);
            Scheduler.RunScanCycle();
            await WaitWithoutDrain(() => Queued > 0);
        }

        public async Task BindActor()
        {
            await QueueOrdinaryWake();
            await DrainUntil(() => !Scheduler.IsLeased(Character.Id));
        }

        public async Task<ActorRequest> StartRoam()
        {
            await BindActor();
            Executor.SetRoamRoute(Character, new BotPath([new Vector3(100, 0, 0)], BotPath.LoopMode.Loop));
            await QueueOrdinaryWake();
            await DrainUntil(() => !Scheduler.IsLeased(Character.Id));
            var move = Actor.ActiveRequest;
            await Assert.That(move).IsNotNull();
            await Assert.That(move!.Action).IsEqualTo(ActorActionType.Move);
            await Assert.That(move.MoveOwner).IsEqualTo("ROAM");
            await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(Actor.IsNativeWorkQuiescent).IsFalse();
            return move;
        }

        public void Dispose()
        {
            Scheduler.StopAsync().GetAwaiter().GetResult();
            Session.World.RemoveObject(Character);
            if (ReferenceEquals(WorldManager.Instance.GetCharacterByObjId(Character.ObjId), Character))
                WorldManager.Instance.TryRemoveCharacter(Character.ObjId);
            ExecutionBoundary.ResetForTest();
        }
    }

    [Test]
    public async Task AdminRemove_RealRoamRetiredBeforeOnePhysicalSave_QueuedWakeCannotRearm()
    {
        using var rig = new NativeRetirementRig();
        await rig.BindActor();
        GameplayActorTestRig.SeedMerchantPack(GameplayActorTestRig.BuyItemTemplateId);
        var merchant = GameplayActorTestRig.SpawnMerchantNpc(rig.Session);
        var beforeItems = GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.BuyItemTemplateId);
        var purchase = NativeRetirementRig.OnManualBoundary(() =>
            rig.Actor.Buy(merchant, GameplayActorTestRig.BuyItemTemplateId, count: 3,
                idempotencyKey: "committed-before-normal-remove"));
        await Assert.That(purchase.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(rig.Character.Money).IsEqualTo(700L);
        await Assert.That(GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.BuyItemTemplateId)).IsEqualTo(beforeItems + 3);
        rig.Executor.SetRoamRoute(rig.Character, new BotPath([new Vector3(100, 0, 0)], BotPath.LoopMode.Loop));
        await rig.QueueOrdinaryWake();
        await rig.DrainUntil(() => !rig.Scheduler.IsLeased(rig.Character.Id));
        var actor = rig.Actor;
        var move = actor.ActiveRequest!;
        await Assert.That(move.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(move.MoveOwner).IsEqualTo("ROAM");
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(actor.IsNativeWorkQuiescent).IsFalse();
        await Assert.That(rig.Character.Transform.World.Position.X).IsGreaterThan(0f);
        await rig.QueueOrdinaryWake(); // physically marshalled, not merely a future due time
        var stepsBeforeLeave = rig.Scheduler.GetMetrics().TotalStepsRun;
        var positionBeforeLeave = rig.Character.Transform.World.Position;
        ActorLifecycleState? stateAtSave = null;
        var exactTerminalAtSave = false;
        var quiescentAtSave = false;
        var activeAtSave = true;
        var sameActorAtSave = false;
        long? moneyAtSave = null;
        int? itemsAtSave = null;
        rig.Physical.BeforeLeave = character =>
        {
            stateAtSave = move.State;
            exactTerminalAtSave = actor.AuditTrace.Any(x => x.TraceId == move.TraceId && x.Result == ActorLifecycleState.Interrupted);
            quiescentAtSave = actor.IsNativeWorkQuiescent;
            activeAtSave = actor.ActiveRequest != null;
            sameActorAtSave = ReferenceEquals(rig.Actor, actor) && ReferenceEquals(character, actor.Character);
            moneyAtSave = character.Money;
            itemsAtSave = GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId);
        };
        var remove = NativeRetirementRig.OffBoundary(() => rig.Admin.Remove(rig.Character.Id.ToString()));
        await NativeRetirementRig.WaitWithoutDrain(() => remove.IsCompleted || rig.Queued >= 2);
        var waitedForBoundary = !remove.IsCompleted;
        var syncReleased = await Task.Run(() =>
        {
            if (!Monitor.TryEnter(rig.Owner.Sync, TimeSpan.FromSeconds(1)))
                return false;
            try { return rig.Owner.State == PlayerBotState.Deactivating; }
            finally { Monitor.Exit(rig.Owner.Sync); }
        });
        await rig.DrainUntil(() => remove.IsCompleted);
        var result = await remove;

        // Expected before-source RED: physical leave observes this exact ROAM
        // still Running, not its retained real Interrupted terminal.
        await Assert.That(stateAtSave).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(exactTerminalAtSave && quiescentAtSave && sameActorAtSave).IsTrue();
        await Assert.That(activeAtSave).IsFalse();
        await Assert.That(waitedForBoundary && syncReleased).IsTrue();
        await Assert.That(result.Success).IsTrue();
        await Assert.That(rig.Physical.Saved).HasCount(1);
        await Assert.That(moneyAtSave).IsEqualTo(700L);
        await Assert.That(itemsAtSave).IsEqualTo(beforeItems + 3);
        await Assert.That(rig.Physical.Saved[0].Money).IsEqualTo(700L);
        await Assert.That(rig.Physical.RetirementCalls).IsEmpty(); // the production adapter/scheduler acknowledged
        await Assert.That(rig.Manager.TryGet(rig.Character.Id, out _)).IsFalse();
        await Assert.That(rig.Executor.GetRoamRoute(rig.Character.Id)).IsNull();
        await Assert.That(rig.Scheduler.IsLeased(rig.Character.Id)).IsFalse();
        for (var i = 0; i < 5; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            rig.Scheduler.Wake(rig.Character.Id);
            rig.Scheduler.RunScanCycle();
            await rig.DrainUntil(() => !rig.Scheduler.IsLeased(rig.Character.Id));
            NativeRetirementRig.OnManualBoundary(() => { actor.Tick(TimeSpan.FromSeconds(1)); return true; });
        }
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(stepsBeforeLeave);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsSkipped).IsGreaterThanOrEqualTo(1L);
        await Assert.That(rig.Actor).IsSameReferenceAs(actor);
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.IsNativeWorkQuiescent).IsTrue();
        await Assert.That(rig.Character.Transform.World.Position).IsEqualTo(positionBeforeLeave);
        await Assert.That(actor.AuditTrace.Count(x => x.Action == ActorActionType.Move)).IsEqualTo(1);
        await Assert.That(actor.FindByKey("committed-before-normal-remove")!.TraceId).IsEqualTo(purchase.TraceId);
        await Assert.That(actor.FindByKey("committed-before-normal-remove")!.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(rig.Character.Money).IsEqualTo(700L);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.BuyItemTemplateId)).IsEqualTo(beforeItems + 3);
        await Assert.That(rig.Physical.Saved).HasCount(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NormalLeave_RealRoamOnPinnedManualBoundaryRetiresInlineBeforeSave(bool headlessLeave)
    {
        using var rig = new NativeRetirementRig();
        var move = await rig.StartRoam();
        var interruptedAtSave = false;
        rig.Physical.BeforeLeave = _ => interruptedAtSave = move.State == ActorLifecycleState.Interrupted
            && rig.Actor.ActiveRequest == null && rig.Actor.IsNativeWorkQuiescent;

        var saved = false;
        var left = NativeRetirementRig.OnManualBoundary(() => headlessLeave
            ? rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Disconnect, out saved)
            : rig.Manager.Deactivate(rig.Character.Id, "dormancy-inline"));

        await Assert.That(left && interruptedAtSave).IsTrue();
        await Assert.That(rig.Physical.Saved).HasCount(1);
        await Assert.That(saved).IsEqualTo(headlessLeave);
        if (headlessLeave)
        {
            await Assert.That(rig.Manager.TryGet(rig.Character.Id, out _)).IsFalse();
            await Assert.That(rig.Physical.PhysicalReasons).IsEquivalentTo(new[] { CharacterLifecycleReason.Disconnect });
        }
        else
            await Assert.That(rig.Owner.State).IsEqualTo(PlayerBotState.Deactivated);
        await Assert.That(rig.Queued).IsEqualTo(0);
    }

    [Test]
    public async Task Deactivate_RealRoamRetirementTimeoutAbandonsQueuedAckWithoutLateSaveOrCancellation()
    {
        using var rig = new NativeRetirementRig(TimeSpan.FromMilliseconds(100));
        var move = await rig.StartRoam();
        var leave = NativeRetirementRig.OffBoundary(() => rig.Manager.Deactivate(rig.Character.Id, "no-drain"));
        await NativeRetirementRig.WaitWithoutDrain(() => rig.Queued > 0 || leave.IsCompleted);
        await Assert.That(await leave.WaitAsync(TimeSpan.FromSeconds(2))).IsFalse();
        await Assert.That(rig.Physical.Saved).IsEmpty();
        await Assert.That(rig.Owner.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(rig.Manager.Activate(rig.Character.Id, null, "retry")).IsFalse();
        await Assert.That(rig.Manager.Remove(rig.Character.Id)).IsFalse();

        rig.Scheduler.DrainTickQueue(); // the already-abandoned retirement must not execute
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(rig.Actor.ActiveRequest).IsSameReferenceAs(move);
        await Assert.That(rig.Physical.Saved).IsEmpty();
        await rig.QueueOrdinaryWake();
        await rig.DrainUntil(() => !rig.Scheduler.IsLeased(rig.Character.Id));
        await Assert.That(rig.Physical.Saved).IsEmpty();
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun).IsEqualTo(2L);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Deactivate_RealRoamStoppedOrPendingStopFailsRetirementWithoutPhysicalSave(bool stopBeforeEnqueue)
    {
        using var rig = new NativeRetirementRig();
        var move = await rig.StartRoam();
        if (stopBeforeEnqueue)
            await rig.Scheduler.StopAsync();
        var leave = NativeRetirementRig.OffBoundary(() => rig.Manager.Deactivate(rig.Character.Id, "stopped-boundary"));
        if (!stopBeforeEnqueue)
        {
            await NativeRetirementRig.WaitWithoutDrain(() => rig.Queued > 0 || leave.IsCompleted);
            await rig.Scheduler.StopAsync();
        }

        await Assert.That(await leave.WaitAsync(TimeSpan.FromSeconds(2))).IsFalse();
        rig.Scheduler.DrainTickQueue();
        await Assert.That(rig.Physical.Saved).IsEmpty();
        await Assert.That(rig.Owner.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(rig.Actor.ActiveRequest).IsSameReferenceAs(move);
    }

    [Test]
    public async Task Deactivate_RealRoamConcurrentEnqueueAndStopCompletesFailureWithoutWaitingForTimeout()
    {
        using var rig = new NativeRetirementRig();
        var move = await rig.StartRoam();
        using var start = new ManualResetEventSlim();
        var leave = NativeRetirementRig.OffBoundary(() =>
        {
            start.Wait();
            return rig.Manager.Deactivate(rig.Character.Id, "enqueue-stop-race");
        });
        var stop = Task.Run(async () =>
        {
            start.Wait();
            await rig.Scheduler.StopAsync();
        });
        start.Set();

        await Task.WhenAll(leave, stop).WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(await leave).IsFalse();
        await Assert.That(rig.Physical.Saved).IsEmpty();
        await Assert.That(rig.Owner.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Deactivate_StaleExecutorGenerationOrCharacterNeverCancelsOrSavesForeignWork(bool foreignCharacter)
    {
        using var rig = new NativeRetirementRig();
        HeadlessSession foreignSession = null;
        var character = rig.Character;
        if (foreignCharacter)
        {
            foreignSession = GameplayActorTestRig.CreateActor("ForeignRetire" + Guid.NewGuid().ToString("N")).Session;
            character = foreignSession.Character;
            character.Id = rig.Character.Id;
            // Keep the manager's actual world identity intact. The stale actor
            // remains embodied in its own real world, not the expected world.
            WorldManager.Instance.TryRemoveCharacter(character.ObjId);
        }
        try
        {
            // Public executor setup creates genuine stale native work: either
            // the same Character at generation 0 or a foreign Character at the
            // exact current generation. No actor/cache pointers are injected.
            var staleRuntime = new PlayerBotRuntime(character, "old-activation")
                { Generation = foreignCharacter ? rig.Owner.Generation : 0 };
            NativeRetirementRig.OnManualBoundary(() => rig.Executor.StepAsync(staleRuntime, CancellationToken.None).GetAwaiter().GetResult());
            rig.Executor.SetRoamRoute(character, new BotPath([new Vector3(100, 0, 0)], BotPath.LoopMode.Loop));
            rig.Time.Advance(TimeSpan.FromMilliseconds(150));
            NativeRetirementRig.OnManualBoundary(() => rig.Executor.StepAsync(staleRuntime, CancellationToken.None).GetAwaiter().GetResult());
            var staleActor = rig.Actor;
            var move = staleActor.ActiveRequest!;
            await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(rig.Executor.GetBotState(character.Id)!.Generation == rig.Owner.Generation).IsEqualTo(foreignCharacter);

            var left = NativeRetirementRig.OnManualBoundary(() => rig.Manager.Deactivate(rig.Character.Id, "stale-executor"));

            await Assert.That(left).IsFalse();
            await Assert.That(rig.Physical.Saved).IsEmpty();
            await Assert.That(rig.Owner.State).IsEqualTo(PlayerBotState.DeactivationFailed);
            await Assert.That(staleActor.ActiveRequest).IsSameReferenceAs(move);
            await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
            await Assert.That(staleActor.IsNativeWorkQuiescent).IsFalse();
        }
        finally
        {
            if (foreignSession != null)
                foreignSession.World.RemoveObject(foreignSession.Character);
        }
    }

    [Test]
    public async Task Retirement_StaleOwnerCharacterAndGenerationCannotCancelLiveEmbodiment()
    {
        using var rig = new NativeRetirementRig();
        var move = await rig.StartRoam();
        var leave = NativeRetirementRig.OffBoundary(() => rig.Manager.Deactivate(rig.Character.Id, "exact-owner"));
        await NativeRetirementRig.WaitWithoutDrain(() => rig.Queued > 0 || leave.IsCompleted);
        await Assert.That(leave.IsCompleted).IsFalse();
        var impostor = new PlayerBotRuntime(rig.Character, "stale-runtime")
            { Generation = rig.Owner.Generation, State = PlayerBotState.Deactivating };
        var foreign = new Character(new UnitCustomModelParams()) { Id = rig.Character.Id, Name = "foreign-character" };
        var wrongCharacter = new PlayerBotRuntime(foreign, "foreign")
            { Generation = rig.Owner.Generation, State = PlayerBotState.Deactivating };
        var refused = NativeRetirementRig.OnManualBoundary(() =>
            !rig.Adapter.TryRetireNativeWork(impostor, impostor.Generation, "stale-owner")
            && !rig.Adapter.TryRetireNativeWork(wrongCharacter, wrongCharacter.Generation, "stale-character")
            && !rig.Adapter.TryRetireNativeWork(rig.Owner, rig.Owner.Generation + 1, "stale-generation"));

        await Assert.That(refused).IsTrue();
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(rig.Actor.ActiveRequest).IsSameReferenceAs(move);
        await Assert.That(rig.Physical.Saved).IsEmpty();
        await rig.DrainUntil(() => leave.IsCompleted);
        await Assert.That(await leave).IsTrue();
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(rig.Physical.Saved).HasCount(1);
    }

    [Test]
    [Arguments(false, CharacterLifecycleReason.Disconnect)]
    [Arguments(true, CharacterLifecycleReason.Shutdown)]
    public async Task LeaveHeadlessCharacter_RetiresBeforeSuppliedOrdinaryPhysicalReason(bool registered, CharacterLifecycleReason reason)
    {
        using var rig = new Rig(registered);
        var retiredAtSave = false;
        rig.Lifecycle.BeforeLeave = _ => retiredAtSave = rig.Lifecycle.RetirementCalls.Count == 1;

        var left = rig.Manager.LeaveHeadlessCharacter(rig.Character, reason, out var saved);

        await Assert.That(left && saved && retiredAtSave).IsTrue();
        await Assert.That(rig.Lifecycle.PhysicalReasons).IsEquivalentTo(new[] { reason });
        await Assert.That(rig.Lifecycle.RetirementCalls[0].Owner.Character).IsSameReferenceAs(rig.Character);
        await Assert.That(rig.Lifecycle.Saved).HasCount(1);
    }

    private static JsonDocument Invoke(object target, object command)
        => JsonDocument.Parse((string)target.GetType().GetMethod("HandleCommand",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [JsonSerializer.Serialize(command)])!);

    [Test]
    public async Task AdminRemove_ThenCachedBridgeCleanup_DoesNotSaveOrSpendAgain()
    {
        using var rig = new Rig();
        await Assert.That(rig.Admin.Remove(rig.Character.Id.ToString()).Success).IsTrue();

        using var response = Invoke(BotDriveBridge.Instance, new { cmd = "deactivate", bot = rig.Character.Name });
        await Assert.That(response.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(response.RootElement.GetProperty("data").GetProperty("saved").GetBoolean()).IsFalse();
        await rig.DrainFollowingWake();

        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        await Assert.That(rig.Lifecycle.Saved[0].Money).IsEqualTo(850L);
        await Assert.That(rig.Character.Money).IsEqualTo(850L);
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
        await Assert.That(rig.Sessions.ContainsKey(rig.Key)).IsFalse();
        await Assert.That(WorldManager.Instance.GetCharacterById(rig.Character.Id)).IsNull();
        await Assert.That(rig.Session.World.GetUnit(rig.Character.ObjId)).IsNull();
        await Assert.That(rig.Step.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task NativeHost_ThenBridgeCacheCleanup_LeavesExactlyOnceWithoutFollowingStep()
    {
        using var rig = new Rig();
        using var left = Invoke(BotProvisioningControlHost.Instance,
            new { cmd = "deactivate", characterId = rig.Character.Id, reason = "Logout" });
        await Assert.That(left.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(left.RootElement.GetProperty("data").GetProperty("saved").GetBoolean()).IsTrue();
        using var cache = Invoke(BotDriveBridge.Instance, new { cmd = "deactivate", bot = rig.Character.Name });
        await Assert.That(cache.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(cache.RootElement.GetProperty("data").GetProperty("saved").GetBoolean()).IsFalse();
        await rig.DrainFollowingWake();

        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        await Assert.That(rig.Character.Money).IsEqualTo(850L);
        await Assert.That(WorldManager.Instance.GetCharacterById(rig.Character.Id)).IsNull();
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
        await Assert.That(rig.Step.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task OrdinaryUnregisteredLiveCharacter_UsesPhysicalLeaveAndPreservesMoney()
    {
        using var rig = new Rig(registered: false);
        await Assert.That(ReferenceEquals(WorldManager.Instance.GetCharacterById(rig.Character.Id), rig.Character)).IsTrue();

        var left = rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Logout, out var saved);

        await Assert.That(left).IsTrue();
        await Assert.That(saved).IsTrue();
        await rig.DrainFollowingWake();
        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        await Assert.That(rig.Lifecycle.Saved[0].Money).IsEqualTo(850L);
        await Assert.That(rig.Character.Money).IsEqualTo(850L);
        await Assert.That(WorldManager.Instance.GetCharacterById(rig.Character.Id)).IsNull();
        await Assert.That(rig.Session.World.GetUnit(rig.Character.ObjId)).IsNull();
        await Assert.That(rig.Step.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentOrdinaryUnregisteredLeaves_CannotSaveTheDelayedLoserOverANewOwner()
    {
        using var rig = new Rig(registered: false);
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var physicalEntries = 0;
        rig.Lifecycle.BeforeLeave = _ =>
        {
            if (Interlocked.Increment(ref physicalEntries) == 1)
            {
                firstEntered.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("First leave was not released.");
            }
            else if (!releaseSecond.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Second leave was not released.");
        };
        var firstLeft = false;
        var firstSaved = false;
        var secondLeft = false;
        var secondSaved = false;
        Exception firstFailure = null, secondFailure = null;
        var first = new Thread(() =>
        {
            try { firstLeft = rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Logout, out firstSaved); }
            catch (Exception ex) { firstFailure = ex; }
        });
        var second = new Thread(() =>
        {
            secondStarted.Set();
            try { secondLeft = rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Logout, out secondSaved); }
            catch (Exception ex) { secondFailure = ex; }
        });
        Character foreign = null;
        bool entered, started, blocked, firstJoined, secondJoined;
        first.Start();
        try
        {
            entered = firstEntered.Wait(TimeSpan.FromSeconds(3));
            second.Start();
            started = secondStarted.Wait(TimeSpan.FromSeconds(3));
            blocked = SpinWait.SpinUntil(() => (second.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3));
            releaseFirst.Set();
            firstJoined = first.Join(TimeSpan.FromSeconds(3));
            foreign = rig.AddForeignSameId(register: true);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            secondJoined = second.Join(TimeSpan.FromSeconds(3));
        }
        await Assert.That(entered && started && blocked && firstJoined && secondJoined).IsTrue();
        await Assert.That(firstFailure).IsNull();
        await Assert.That(secondFailure).IsNull();
        await Assert.That(firstLeft && firstSaved).IsTrue();
        await Assert.That(secondLeft || secondSaved).IsFalse();
        await Assert.That(physicalEntries).IsEqualTo(1);
        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        await Assert.That(rig.Lifecycle.Saved[0].Money).IsEqualTo(850L);
        await Assert.That(ReferenceEquals(WorldManager.Instance.GetCharacterById(foreign.Id), foreign)).IsTrue();
        await Assert.That(rig.Manager.TryGet(foreign.Id, out var current)).IsTrue();
        await Assert.That(ReferenceEquals(current!.Character, foreign)).IsTrue();
        await Assert.That(foreign.Money).IsEqualTo(555L);
    }

    [Test]
    public async Task OrdinaryUnregisteredLeave_ExcludesLateRegistrationBeforeItsPhysicalSaveCompletes()
    {
        using var rig = new Rig(registered: false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.Lifecycle.BeforeLeave = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Ordinary leave was not released.");
        };
        var left = false;
        var saved = false;
        Exception failure = null;
        var worker = new Thread(() =>
        {
            try { left = rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Logout, out saved); }
            catch (Exception ex) { failure = ex; }
        });
        bool reachedPhysicalSave, registered, activated = false, joined;
        worker.Start();
        try
        {
            reachedPhysicalSave = entered.Wait(TimeSpan.FromSeconds(3));
            registered = rig.Manager.Spawn(rig.Character, "late-registration-control");
            if (registered) activated = rig.Manager.Activate(rig.Character.Id, null, "late-registration-control");
        }
        finally
        {
            release.Set();
            joined = worker.Join(TimeSpan.FromSeconds(3));
        }
        await rig.DrainFollowingWake();
        await Assert.That(reachedPhysicalSave && joined).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(left && saved).IsTrue();
        await Assert.That(registered || activated).IsFalse();
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(0);
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        await Assert.That(rig.Lifecycle.Saved[0].Money).IsEqualTo(850L);
        await Assert.That(rig.Character.Money).IsEqualTo(850L);
        await Assert.That(WorldManager.Instance.GetCharacterById(rig.Character.Id)).IsNull();
        await Assert.That(rig.Step.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ForeignWorldOrRegistryReplacement_IsNotSavedRemovedOrSpent(bool registered)
    {
        using var rig = new Rig();
        await Assert.That(rig.Admin.Remove(rig.Character.Id.ToString()).Success).IsTrue();
        var foreign = rig.AddForeignSameId(registered);

        using var response = Invoke(BotDriveBridge.Instance, new { cmd = "deactivate", bot = rig.Character.Name });

        await Assert.That(response.RootElement.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(rig.Sessions.TryGetValue(rig.Key, out var cached)).IsTrue();
        await Assert.That(ReferenceEquals(cached, rig.Session)).IsTrue();
        await Assert.That(ReferenceEquals(WorldManager.Instance.GetCharacterById(foreign.Id), foreign)).IsTrue();
        await Assert.That(foreign.Money).IsEqualTo(555L);
        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        if (registered)
        {
            await Assert.That(rig.Manager.TryGet(foreign.Id, out var runtime)).IsTrue();
            await Assert.That(ReferenceEquals(runtime!.Character, foreign)).IsTrue();
            await Assert.That(rig.Manager.ActiveCount).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedLeaveOrCachedReplacement_RetainsCacheAndQuiescedOwner(bool replacement)
    {
        using var rig = new Rig();
        rig.Lifecycle.Refuse = true;
        using var response = Invoke(BotDriveBridge.Instance, replacement
            ? new { cmd = "provision", bot = rig.Character.Name, fresh = true }
            : (object)new { cmd = "deactivate", bot = rig.Character.Name });

        await Assert.That(response.RootElement.GetProperty("ok").GetBoolean()).IsFalse();
        await rig.DrainFollowingWake();
        await Assert.That(rig.Sessions.TryGetValue(rig.Key, out var cached)).IsTrue();
        await Assert.That(ReferenceEquals(cached, rig.Session)).IsTrue();
        await Assert.That(rig.Manager.TryGet(rig.Character.Id, out var retained)).IsTrue();
        await Assert.That(retained!.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(rig.Manager.ActiveCount).IsEqualTo(0);
        await Assert.That(ReferenceEquals(WorldManager.Instance.GetCharacterById(rig.Character.Id), rig.Character)).IsTrue();
        await Assert.That(rig.Character.Money).IsEqualTo(850L);
        await Assert.That(rig.Lifecycle.Saved).IsEmpty();
        await Assert.That(rig.Step.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartyConsumer_PreservesNativePrimary_AndEveryCleanupFailure(bool bothRefuse)
    {
        using var rig = new Rig();
        var other = GameplayActorTestRig.CreateActor("OtherLeave" + Guid.NewGuid().ToString("N")).Session;
        try
        {
            other.Character.Money = 333;
            rig.Manager.Spawn(other.Character, "party-member");
            rig.Manager.Activate(other.Character.Id, null, "party-member");
            // An actually absent native target rejects PartyInvite before any scenario timer/provisioning.
            other.World.RemoveObject(other.Character);
            if (ReferenceEquals(WorldManager.Instance.GetCharacterByObjId(other.Character.ObjId), other.Character))
                WorldManager.Instance.TryRemoveCharacter(other.Character.ObjId);
            rig.Lifecycle.RefuseIds.Add(rig.Character.Id);
            if (bothRefuse) rig.Lifecycle.RefuseIds.Add(other.Character.Id);
            using var root = JsonDocument.Parse("{}");
            var actualResponse = (string)typeof(BotDriveBridge)
                .GetMethod("RunPartyFollowAssistScenario", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(BotDriveBridge.Instance, [root.RootElement, rig.Character.Name, other.Character.Name, 1850u, new[] { rig.Session, other }])!;
            using var response = JsonDocument.Parse(actualResponse);
            await Assert.That(response.RootElement.GetProperty("ok").GetBoolean()).IsFalse();
            var primary = response.RootElement.GetProperty("primary");
            await Assert.That(primary.GetProperty("ok").GetBoolean()).IsFalse();
            await Assert.That(response.RootElement.GetProperty("cleanupErrors").GetArrayLength()).IsEqualTo(bothRefuse ? 2 : 1);
            await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(bothRefuse ? 0 : 1);
            if (!bothRefuse)
            {
                await Assert.That(ReferenceEquals(rig.Lifecycle.Saved[0].Character, other.Character)).IsTrue();
                await Assert.That(rig.Lifecycle.Saved[0].Money).IsEqualTo(333L);
                await Assert.That(rig.Manager.TryGet(other.Character.Id, out _)).IsFalse();
            }
            else
            {
                await Assert.That(rig.Manager.TryGet(other.Character.Id, out var otherOwner)).IsTrue();
                await Assert.That(otherOwner!.State).IsEqualTo(PlayerBotState.DeactivationFailed);
            }
            await Assert.That(rig.Manager.ActiveCount).IsEqualTo(0);
            await rig.DrainFollowingWake();
            await Assert.That(rig.Step.Calls).IsEqualTo(0);
            await Assert.That(rig.Character.Money).IsEqualTo(850L);
            await Assert.That(other.Character.Money).IsEqualTo(333L);
        }
        finally
        {
            other.World.RemoveObject(other.Character);
            if (ReferenceEquals(WorldManager.Instance.GetCharacterByObjId(other.Character.ObjId), other.Character))
                WorldManager.Instance.TryRemoveCharacter(other.Character.ObjId);
        }
    }

    [Test]
    public async Task OrdinaryLeave_HoldsExactOwnerThroughRealCacheReleaseAndRejectsReentrantLeave()
    {
        using var rig = new Rig(registered: false);
        var reentrantLeft = true;
        var reentrantSaved = true;
        rig.Lifecycle.BeforeLeave = _ =>
            reentrantLeft = rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Logout, out reentrantSaved);
        var registered = true;
        var activated = true;
        var removed = true;
        var released = false;
        var left = rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Logout, out var saved, () =>
        {
            registered = rig.Manager.Spawn(rig.Character, "late-cache-release");
            activated = rig.Manager.Activate(rig.Character.Id, null, "late-cache-release");
            removed = rig.Manager.Remove(rig.Character.Id);
            released = ((ICollection<KeyValuePair<string, HeadlessSession>>)rig.Sessions)
                .Remove(new KeyValuePair<string, HeadlessSession>(rig.Key, rig.Session));
            return released;
        });
        await Assert.That(left && saved && released).IsTrue();
        await Assert.That(reentrantLeft || reentrantSaved || registered || activated || removed).IsFalse();
        await rig.DrainFollowingWake();
        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        await Assert.That(rig.Lifecycle.Saved[0].Money).IsEqualTo(850L);
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
        await Assert.That(rig.Sessions.ContainsKey(rig.Key)).IsFalse();
        await Assert.That(WorldManager.Instance.GetCharacterById(rig.Character.Id)).IsNull();
        await Assert.That(rig.Step.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task CapturedDelayedLeave_AfterActualCacheRelease_DoesNotRetainAPhantomOwnerOrSaveAgain()
    {
        using var rig = new Rig(registered: false);
        var captured = rig.Session; // Caller B captured this exact session before caller A completed.
        using var first = Invoke(BotDriveBridge.Instance, new { cmd = "deactivate", bot = rig.Character.Name });
        await Assert.That(first.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(first.RootElement.GetProperty("data").GetProperty("saved").GetBoolean()).IsTrue();
        await Assert.That(rig.Sessions.ContainsKey(rig.Key)).IsFalse();
        var release = typeof(BotDriveBridge).GetMethod("ReleaseExactPersistentSession", BindingFlags.Static | BindingFlags.NonPublic)!;
        var delayed = rig.Manager.LeaveHeadlessCharacter(captured.Character, CharacterLifecycleReason.Logout,
            out var delayedSaved, () => (bool)release.Invoke(null, [rig.Key, captured])!);
        await Assert.That(delayed).IsTrue();
        await Assert.That(delayedSaved).IsFalse();
        await Assert.That(rig.Manager.Count).IsEqualTo(0);
        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
        await Assert.That(rig.Lifecycle.Saved[0].Money).IsEqualTo(850L);
        await Assert.That(rig.Character.Money).IsEqualTo(850L);
        var replacement = GameplayActorTestRig.CreateActor("Readmit" + Guid.NewGuid().ToString("N")).Session;
        try
        {
            replacement.Character.Id = rig.Character.Id;
            replacement.Character.Name = rig.Character.Name;
            replacement.Character.Money = 555;
            await Assert.That(rig.Manager.Spawn(replacement.Character, "future-owner")).IsTrue();
            await Assert.That(rig.Manager.Activate(replacement.Character.Id, null, "future-owner")).IsTrue();
            await Assert.That(rig.Sessions.TryAdd(rig.Key, replacement)).IsTrue();
            using var next = Invoke(BotDriveBridge.Instance, new { cmd = "deactivate", bot = replacement.Character.Name });
            await Assert.That(next.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
            await Assert.That(next.RootElement.GetProperty("data").GetProperty("saved").GetBoolean()).IsTrue();
            await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(2); // One physical leave per distinct embodiment, never the delayed stale caller.
            await Assert.That(ReferenceEquals(rig.Lifecycle.Saved[1].Character, replacement.Character)).IsTrue();
            await Assert.That(rig.Lifecycle.Saved[1].Money).IsEqualTo(555L);
            await Assert.That(replacement.Character.Money).IsEqualTo(555L);
            await Assert.That(rig.Manager.Count).IsEqualTo(0);
            await Assert.That(rig.Sessions.ContainsKey(rig.Key)).IsFalse();
            await Assert.That(WorldManager.Instance.GetCharacterById(replacement.Character.Id)).IsNull();
            await rig.DrainFollowingWake();
            await Assert.That(rig.Step.Calls).IsEqualTo(0);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, HeadlessSession>>)rig.Sessions)
                .Remove(new KeyValuePair<string, HeadlessSession>(rig.Key, replacement));
            replacement.World.RemoveObject(replacement.Character);
            if (ReferenceEquals(WorldManager.Instance.GetCharacterByObjId(replacement.Character.ObjId), replacement.Character))
                WorldManager.Instance.TryRemoveCharacter(replacement.Character.ObjId);
        }
    }

    [Test]
    public async Task OrdinaryPhysicalRefusal_RetainsExactCacheAndQuiescedReservation()
    {
        using var rig = new Rig(registered: false);
        rig.Lifecycle.Refuse = true;
        using var response = Invoke(BotDriveBridge.Instance, new { cmd = "deactivate", bot = rig.Character.Name });
        await Assert.That(response.RootElement.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(rig.Manager.TryGet(rig.Character.Id, out var retained)).IsTrue();
        await Assert.That(retained!.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(rig.Sessions.TryGetValue(rig.Key, out var cached)).IsTrue();
        await Assert.That(ReferenceEquals(cached, rig.Session)).IsTrue();
        await Assert.That(rig.Manager.Activate(rig.Character.Id, null, "must-not-resume")).IsFalse();
        await rig.DrainFollowingWake();
        await Assert.That(rig.Lifecycle.Saved).IsEmpty();
        await Assert.That(rig.Character.Money).IsEqualTo(850L);
        await Assert.That(ReferenceEquals(WorldManager.Instance.GetCharacterById(rig.Character.Id), rig.Character)).IsTrue();
        await Assert.That(rig.Step.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task ReplacementWhileWaitingForSync_RechecksExactRegistryAndCharacter()
    {
        using var rig = new Rig();
        rig.Manager.TryGet(rig.Character.Id, out var old);
        var left = true;
        var saved = true;
        Exception failure = null;
        using var started = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            started.Set();
            try
            {
                left = rig.Manager.LeaveHeadlessCharacter(rig.Character, CharacterLifecycleReason.Logout, out saved);
            }
            catch (Exception ex) { failure = ex; }
        });
        Character foreign;
        bool entered, blockedOnSync, deactivated, removed;
        Monitor.Enter(old!.Sync);
        try
        {
            worker.Start();
            entered = started.Wait(TimeSpan.FromSeconds(3));
            blockedOnSync = SpinWait.SpinUntil(() => (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(3));
            deactivated = rig.Manager.Deactivate(rig.Character.Id, "replace-after-save");
            removed = rig.Manager.Remove(rig.Character.Id);
            foreign = rig.AddForeignSameId(register: true);
        }
        finally { Monitor.Exit(old.Sync); }
        await Assert.That(worker.Join(TimeSpan.FromSeconds(3))).IsTrue();
        await Assert.That(entered).IsTrue();
        await Assert.That(blockedOnSync).IsTrue();
        await Assert.That(deactivated).IsTrue();
        await Assert.That(removed).IsTrue();

        await Assert.That(failure).IsNull();
        await Assert.That(left).IsFalse();
        await Assert.That(saved).IsFalse();
        await Assert.That(rig.Manager.TryGet(foreign.Id, out var current)).IsTrue();
        await Assert.That(ReferenceEquals(current!.Character, foreign)).IsTrue();
        await Assert.That(ReferenceEquals(WorldManager.Instance.GetCharacterById(foreign.Id), foreign)).IsTrue();
        await Assert.That(foreign.Money).IsEqualTo(555L);
        await Assert.That(rig.Lifecycle.Saved.Count).IsEqualTo(1);
    }
}
