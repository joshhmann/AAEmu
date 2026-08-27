using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Rig for the control-plane lifecycle queue (M5 stage 3, t_7b6d7a4b): the
/// enqueue-only path from the API into bot execution.
///
/// Proves the contract:
///  - enqueue returns a trace id immediately; execution happens on the
///    drain (the test thread stands in for the execution boundary);
///  - the queue drives the SAME actor instance the scheduler's step
///    executor ticks (shared-actor proof via a real BotRoamStepExecutor);
///  - full lifecycle per command (Requested → … → terminal, with the B1
///    audit record);
///  - single-writer: a second API command against a busy actor is
///    Rejected(StateTransition); a world-internal request (roam leg) is
///    preempted via actor.Interrupt;
///  - no-wedge backstop: a request that nothing ticks expires on its own;
///  - crash isolation: an undrained command executes later — no world lock
///    is held by any caller.
/// </summary>
[NotInParallel]
public class BotActionCommandQueueTests
{
    // ------------------------------------------------------------- rig

    private sealed class FakeManager : IPlayerBotManager
    {
        private readonly Dictionary<uint, PlayerBotRuntime> _registry = [];

        public bool TryGet(uint characterId, out PlayerBotRuntime? runtime)
        {
            runtime = _registry.GetValueOrDefault(characterId);
            return runtime != null;
        }

        public IReadOnlyList<PlayerBotRuntime> GetAll() => [.. _registry.Values];

        public PlayerBotRuntime Seed(Character character, PlayerBotState state = PlayerBotState.Active, string owner = "rig")
        {
            var runtime = new PlayerBotRuntime(character, owner) { State = state };
            _registry[character.Id] = runtime;
            return runtime;
        }

        public bool Spawn(Character character, string owner) => throw new NotSupportedException("not used by this rig");
        public bool Activate(uint characterId, object? botContext, string owner) => throw new NotSupportedException("not used by this rig");
        public bool Deactivate(uint characterId, string reason) => throw new NotSupportedException("not used by this rig");
        public bool Remove(uint characterId) => throw new NotSupportedException("not used by this rig");
        public IReadOnlyList<PlayerBotRuntime> GetActive() => _registry.Values.Where(r => r.State == PlayerBotState.Active).ToList();
        public int Count => _registry.Count;
        public int ActiveCount => _registry.Values.Count(r => r.State == PlayerBotState.Active);
        public PlayerBotDiagnostics GetDiagnostics() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed class FakeScheduler : IPlayerBotScheduler
    {
        public List<uint> Wakes { get; } = [];

        public bool Wake(uint characterId) { Wakes.Add(characterId); return true; }
        public void Start() { }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool WakeAt(uint characterId, DateTime utcDue) => true;
        public bool WakeAfter(uint characterId, TimeSpan delay) => true;
        public bool IsLeased(uint characterId) => false;
        public PlayerBotSchedulerMetrics GetMetrics() => new(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0);
        public int WorkerCount => 4;
        public bool IsRunning => true;
    }

    private sealed class Rig
    {
        public required GameplayActor Actor { get; init; }
        public required HeadlessSession Session { get; init; }
        public required PlayerBotRuntime Runtime { get; init; }
        public required FakeManager Manager { get; init; }
        public required FakeScheduler Scheduler { get; init; }
        public required BotRoamStepExecutor Executor { get; init; }
        public required BotActionCommandQueue Queue { get; init; }
        public required FakeTimeProvider Clock { get; init; }
    }

    private static Rig CreateRig(string name = "api-bot", PlayerBotState state = PlayerBotState.Active)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var manager = new FakeManager();
        var runtime = manager.Seed(actor.Character, state);
        var scheduler = new FakeScheduler();
        var clock = new FakeTimeProvider();

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            // 0 = "no heightmap data" → the step-3a clamp no-ops (the rig has
            // no WorldManager heightmap seeded; same seam the roam executor
            // tests inject).
            GroundHeightProvider = (_, _) => 0f
        };

        var queue = new BotActionCommandQueue(
            manager, scheduler, executor,
            options: new BotActionQueueOptions { SubscribeToTickManager = false },
            timeProvider: clock);

        return new Rig
        {
            Actor = actor,
            Session = session,
            Runtime = runtime,
            Manager = manager,
            Scheduler = scheduler,
            Executor = executor,
            Queue = queue,
            Clock = clock
        };
    }

    [Before(Test)]
    public void PinBoundary()
        => ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);

    [After(Test)]
    public void UnpinBoundary()
        => ExecutionBoundary.ResetForTest();

    // ------------------------------------------------------------- enqueue

    [Test]
    public async Task Enqueue_UnknownBot_Refused()
    {
        var rig = CreateRig();

        var result = rig.Queue.Enqueue("nobody", new BotActionSpec(BotActionKind.Observe));

        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Error).Contains("unknown bot");
        await Assert.That(rig.Queue.GetStats().Queued).IsEqualTo(0);
    }

    [Test]
    public async Task Enqueue_ByNumericId_Resolves()
    {
        var rig = CreateRig("api-bot-2");
        var id = rig.Runtime.CharacterId.ToString();

        var result = rig.Queue.Enqueue(id, new BotActionSpec(BotActionKind.Observe));

        await Assert.That(result.Ok).IsTrue();
        await Assert.That(result.CharacterId).IsEqualTo(rig.Runtime.CharacterId);
    }

    // ------------------------------------------------------- full lifecycle

    [Test]
    public async Task Observe_EnqueueThenDrain_CompletedWithObservation()
    {
        var rig = CreateRig();

        var result = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        await Assert.That(result.Ok).IsTrue();

        // Before the drain: Requested, no execution (enqueue-only proof).
        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var before)).IsTrue();
        await Assert.That(before.State).IsEqualTo(nameof(ActorLifecycleState.Requested));
        await Assert.That(before.AuditJson).IsNull();

        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var after)).IsTrue();
        await Assert.That(after.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(after.AuditJson).IsNotNull();
        await Assert.That(after.Result).IsAssignableTo<ActorObservation>();
        await Assert.That(((ActorObservation)after.Result!).ActorId).IsEqualTo(rig.Actor.ActorId);

        // The B1 audit record on the actor matches the entry's embedded JSON.
        await Assert.That(rig.Actor.AuditTrace).HasCount().EqualTo(1);
        await Assert.That(rig.Actor.AuditTrace[0].Action).IsEqualTo(ActorActionType.Observe);
        await Assert.That(rig.Actor.AuditTrace[0].Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Stop_EnqueueThenDrain_FullTransitionLog()
    {
        var rig = CreateRig();

        var result = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Stop));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(snap.StateChanges[0]).IsEqualTo("Requested");
        await Assert.That(snap.StateChanges[^1]).Contains("Completed");
    }

    // ------------------------------------------------------- shared actor

    [Test]
    public async Task Move_CompletesThroughSchedulerSteps_SharedActorProof()
    {
        var rig = CreateRig();

        var result = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move,
                Destination: new Vector3(4, 0, 0),
                Payload: new MoveActionParams(2f)));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var running)).IsTrue();
        await Assert.That(running.State).IsEqualTo(nameof(ActorLifecycleState.Running));
        // The queue woke the scheduler so the actor keeps being ticked.
        await Assert.That(rig.Scheduler.Wakes).Contains(rig.Runtime.CharacterId);

        // The scheduler's executor steps the SAME actor the queue drove —
        // the leg advances through the ordinary Transform.
        TimeSpan? next = TimeSpan.Zero;
        var guard = 0;
        while (next is not null && guard++ < 100)
        {
            rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
            next = await rig.Executor.StepAsync(rig.Runtime, CancellationToken.None);
        }

        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var done)).IsTrue();
        await Assert.That(done.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(done.AuditJson).IsNotNull();
        await Assert.That(Math.Abs(rig.Actor.Character.Transform.World.Position.X - 4f) <= 0.5f).IsTrue();
    }

    // ------------------------------------------------------- single-writer

    [Test]
    public async Task SecondApiCommand_WhileFirstRunning_BusyRejected()
    {
        var rig = CreateRig();

        var first = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move, Destination: new Vector3(50, 0, 0)));
        rig.Queue.DrainCommands();

        var second = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Observe));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(first.TraceId, out var f)).IsTrue();
        await Assert.That(f.State).IsEqualTo(nameof(ActorLifecycleState.Running));

        await Assert.That(rig.Queue.TryGetSnapshot(second.TraceId, out var s)).IsTrue();
        await Assert.That(s.State).IsEqualTo(nameof(ActorLifecycleState.Rejected));
        await Assert.That(s.Failure).IsEqualTo(nameof(ActorFailureReason.StateTransition));
        await Assert.That(s.Detail).Contains("busy");
    }

    [Test]
    public async Task Command_PreemptsWorldInternalRoamLeg()
    {
        var rig = CreateRig();

        // A world-internal request, exactly like a roam leg: issued by the
        // executor/scenario on the actor while the API is not involved.
        var leg = rig.Actor.MoveTo(new Vector3(50, 0, 0));
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(rig.Actor.ActiveRequest is { IsTerminal: false }).IsTrue();

        // A control-plane command must land deterministically — the leg is
        // interrupted (audited) and the command executes.
        var result = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.Completed));

        // The interrupted leg emitted its terminal audit record.
        var legRecord = rig.Actor.AuditTrace.FirstOrDefault(r => r.Action == ActorActionType.Move);
        await Assert.That(legRecord).IsNotNull();
        await Assert.That(legRecord!.Result).IsEqualTo(ActorLifecycleState.Interrupted);
    }

    // -------------------------------------------------------- idempotency

    [Test]
    public async Task SameIdempotencyKeyTwice_SecondIsDedupeRejected()
    {
        var rig = CreateRig();

        var first = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move, Destination: new Vector3(0.1f, 0, 0), IdempotencyKey: "k-1"));
        rig.Queue.DrainCommands();
        await Assert.That(rig.Queue.TryGetSnapshot(first.TraceId, out var f)).IsTrue();
        await Assert.That(f.State).IsEqualTo(nameof(ActorLifecycleState.Completed));

        var second = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move, Destination: new Vector3(0.1f, 0, 0), IdempotencyKey: "k-1"));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(second.TraceId, out var s)).IsTrue();
        await Assert.That(s.State).IsEqualTo(nameof(ActorLifecycleState.Rejected));
        await Assert.That(s.Detail).Contains("duplicate idempotency key");
        await Assert.That(s.Detail).Contains("k-1");
    }

    // ------------------------------------------------- no-wedge / timeout

    [Test]
    public async Task Move_NoTicks_BackstopExpiresCleanly()
    {
        var rig = CreateRig();

        // The scheduler never steps this bot (stopped/not started) — the
        // actor's own Tick never runs, so the queue backstop must expire the
        // request: a command can never hang the actor indefinitely.
        var result = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move,
                Destination: new Vector3(50, 0, 0), Timeout: TimeSpan.FromSeconds(2)));
        rig.Queue.DrainCommands();

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.TimedOut));
        await Assert.That(snap.Failure).IsEqualTo(nameof(ActorFailureReason.Navigation));
        await Assert.That(snap.AuditJson).IsNotNull();

        // The actor is usable again afterwards (no wedge).
        var observe = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        rig.Queue.DrainCommands();
        await Assert.That(rig.Queue.TryGetSnapshot(observe.TraceId, out var o)).IsTrue();
        await Assert.That(o.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
    }

    [Test]
    public async Task Enqueue_NoDrain_ThenDrainLater_Executes()
    {
        var rig = CreateRig();

        // The caller disconnected right after the POST — the command sits in
        // the queue (Requested) and executes when the boundary next drains.
        var result = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var before)).IsTrue();
        await Assert.That(before.State).IsEqualTo(nameof(ActorLifecycleState.Requested));

        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var after)).IsTrue();
        await Assert.That(after.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
    }

    // ---------------------------------------------------------- interrupt

    [Test]
    public async Task Interrupt_CancelsRunningApiCommand()
    {
        var rig = CreateRig();

        var move = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move, Destination: new Vector3(50, 0, 0)));
        rig.Queue.DrainCommands();

        var interrupt = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Interrupt, Payload: new InterruptActionParams(move.TraceId)));
        rig.Queue.DrainCommands();

        // The running command ended Interrupted; the interrupt command itself
        // completed with result true.
        await Assert.That(rig.Queue.TryGetSnapshot(move.TraceId, out var m)).IsTrue();
        await Assert.That(m.State).IsEqualTo(nameof(ActorLifecycleState.Interrupted));

        await Assert.That(rig.Queue.TryGetSnapshot(interrupt.TraceId, out var i)).IsTrue();
        await Assert.That(i.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(i.Result).IsEqualTo(true);
    }

    // -------------------------------------------------------------- trace

    [Test]
    public async Task TraceFor_ReturnsNewestFirst()
    {
        var rig = CreateRig();

        var a = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        var b = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Stop));
        var c = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        rig.Queue.DrainCommands();

        var trace = rig.Queue.TraceFor(rig.Runtime.CharacterId);

        await Assert.That(trace).HasCount().EqualTo(3);
        await Assert.That(trace[0].TraceId).IsEqualTo(c.TraceId);
        await Assert.That(trace[2].TraceId).IsEqualTo(a.TraceId);
        await Assert.That(trace[0].AuditJson).IsNotNull();
    }

    // ------------------------------------------------- registry consumption

    [Test]
    public async Task Drain_BotNotActive_RejectedCleanly()
    {
        var rig = CreateRig(state: PlayerBotState.Deactivated);

        var result = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        await Assert.That(result.Ok).IsTrue(); // enqueue accepts; the drain validates

        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.Rejected));
        await Assert.That(snap.Failure).IsEqualTo(nameof(ActorFailureReason.StateTransition));
        await Assert.That(snap.Detail).Contains("not registered or not active");
    }

    // ------------------------------------------------------- engine refusal

    [Test]
    public async Task Cast_UnknownSkill_RejectedWithTaxonomy()
    {
        var rig = CreateRig();

        var result = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Cast, TargetId: 123, SkillId: 999_999));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.Rejected));
        await Assert.That(snap.Failure).IsEqualTo(nameof(ActorFailureReason.RejectedAction));
        await Assert.That(snap.Detail).Contains("unknown skill");
    }

    // ------------------------------------------------------ M5.1 craft seam

    [Test]
    public async Task Craft_EnqueueThenDrain_ExecutesThroughActorRealEnginePath()
    {
        // The M5.1 replay seam: a Craft command on the queue is executed by
        // the actor through the REAL engine craft path (CharacterCraft.Craft
        // → CraftEffect → EndCraft), independent of any controller.
        var rig = CreateRig();
        GameplayActorTestRig.SeedCraftSurface();
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(rig.Session, rig.Actor);
        rig.Actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(rig.Actor, GameplayActorTestRig.CraftMaterialTemplateId, 2);

        var result = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Craft, TargetId: GameplayActorTestRig.CraftTestCraftId,
                Payload: new CraftActionParams(benchObjId)));
        rig.Queue.DrainCommands();

        // The engine accepted the step — the command is Running.
        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.Running));
        await Assert.That(snap.Action).IsEqualTo(nameof(ActorActionType.Craft));

        // Engine-side completion + the actor Tick the scheduler would drive.
        GameplayActorTestRig.CompleteCraftStep(rig.Actor, benchObjId);
        rig.Actor.Tick(TimeSpan.Zero);

        await Assert.That(rig.Actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Craft);
        await Assert.That(rig.Actor.AuditTrace[^1].Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task Navigate_EnqueueThenDrain_ExecutesThroughActorNavigateTo()
    {
        var rig = CreateRig();
        var destination = new Vector3(4, 0, 0);

        var result = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Navigate, Destination: destination, Payload: new MoveActionParams(2f)));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.Running));
        await Assert.That(snap.Action).IsEqualTo(nameof(BotActionKind.Navigate));

        // Step through scheduler executor
        TimeSpan? next = TimeSpan.Zero;
        var guard = 0;
        while (next is not null && guard++ < 100)
        {
            rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
            next = await rig.Executor.StepAsync(rig.Runtime, CancellationToken.None);
        }

        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var done)).IsTrue();
        await Assert.That(done.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(done.AuditJson).IsNotNull();
        await Assert.That(Math.Abs(rig.Actor.Character.Transform.World.Position.X - 4f) <= 0.5f).IsTrue();
    }

    [Test]
    public async Task NewMcpActions_EnqueueAndDispatchThroughSharedActorBoundary()
    {
        var cases = new (BotActionKind Kind, uint TargetId, object? Payload)[]
        {
            (BotActionKind.DiscoverQuests, 0xDEADu, null),
            (BotActionKind.DiscoverSelfQuests, 0u, null),
            (BotActionKind.InteractWith, 0xDEADu, null),
            (BotActionKind.Talk, 0xDEADu, null),
            (BotActionKind.Equip, 0xDEADu, null),
            (BotActionKind.DepositMoney, 0u, new MoneyActionParams(100)),
            (BotActionKind.WithdrawMoney, 0u, new MoneyActionParams(100)),
            (BotActionKind.DepositItem, 0xDEADu, null),
            (BotActionKind.WithdrawItem, 0xDEADu, null),
            (BotActionKind.Plant, 0xDEADu, new PlantActionParams()),
            (BotActionKind.Harvest, 0xDEADu, null),
            (BotActionKind.Buy, 0xDEADu, new BuyActionParams(1, 1)),
            (BotActionKind.Sell, 0xDEADu, new SellActionParams(1)),
            (BotActionKind.PackPickup, 0xDEADu, null),
            (BotActionKind.PutDown, 0xDEADu, null),
            (BotActionKind.LoadPackOntoVehicle, 0xDEADu, new LoadPackOntoVehicleActionParams()),
            (BotActionKind.BoardVehicle, 0xDEADu, new BoardVehicleActionParams()),
            (BotActionKind.UnboardVehicle, 0u, null),
            (BotActionKind.DriveVehicle, 0xDEADu, new DriveVehicleActionParams(System.Numerics.Vector3.Zero)),
        };

        foreach (var (kind, targetId, payload) in cases)
        {
            var rig = CreateRig($"mcp-{kind}");
            var result = rig.Queue.Enqueue(rig.Actor.Character.Name, new BotActionSpec(kind, TargetId: targetId, Payload: payload));
            await Assert.That(result.TraceId).IsNotEqualTo(Guid.Empty);

            rig.Queue.DrainCommands();

            await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snapshot)).IsTrue();
            await Assert.That(snapshot.Action).IsEqualTo(kind.ToString());
            await Assert.That(snapshot.State is nameof(ActorLifecycleState.Completed)
                or nameof(ActorLifecycleState.Rejected)).IsTrue();
        }
    }

    [Test]
    public async Task DepositAndWithdrawMoney_EnqueueAndDrain_TransfersCopperAccurately()
    {
        var rig = CreateRig("money-bot");
        rig.Actor.Character.Money = 500;
        rig.Actor.Character.Money2 = 200;

        // Deposit 300 copper
        var depResult = rig.Queue.Enqueue(rig.Actor.Character.Name,
            new BotActionSpec(BotActionKind.DepositMoney, Payload: new MoneyActionParams(300)));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(depResult.TraceId, out var depSnap)).IsTrue();
        await Assert.That(depSnap.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(rig.Actor.Character.Money).IsEqualTo(200);
        await Assert.That(rig.Actor.Character.Money2).IsEqualTo(500);

        // Withdraw 150 copper
        var withResult = rig.Queue.Enqueue(rig.Actor.Character.Name,
            new BotActionSpec(BotActionKind.WithdrawMoney, Payload: new MoneyActionParams(150)));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(withResult.TraceId, out var withSnap)).IsTrue();
        await Assert.That(withSnap.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(rig.Actor.Character.Money).IsEqualTo(350);
        await Assert.That(rig.Actor.Character.Money2).IsEqualTo(350);
    }
}
