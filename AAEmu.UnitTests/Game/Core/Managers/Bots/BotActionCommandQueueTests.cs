using System.Numerics;
using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;
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
        public bool LeaveHeadlessCharacter(Character expected, AAEmu.Game.Core.Managers.CharacterLifecycleReason reason,
            out bool savedThisCall, Func<bool>? releaseCachedSession = null) => throw new NotSupportedException("not used by this rig");
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

        // T2: the one arming owner. This rig never enrols a bot, so the arming
        // itself is inert; ArmedBy reports the reason the production callers
        // pass.
        public string? ArmedBy { get; private set; }
        public bool Arm(string reason) { ArmedBy ??= reason; return true; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        // A1 stop owner: this rig never enrols a bot, so a disarm is inert.
        public BotHostTeardownReport Disarm(string reason) => new(reason, false, 0, 0, 0, []);
        public bool WakeAt(uint characterId, DateTime utcDue) => true;
        public bool WakeAfter(uint characterId, TimeSpan delay) => true;
        public bool IsLeased(uint characterId) => false;
        public BotEnrollmentSnapshot GetEnrollmentSnapshot(uint characterId) => new(false, "-", null, false, null, null, null, 0, 0, 0, 0, false, 0);
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

    private static Rig CreateRig(string name = "api-bot", PlayerBotState state = PlayerBotState.Active, bool cropWorld = false)
    {
        var (actor, session) = cropWorld
            ? GameplayActorHarvestTests.CreateActorOnUniqueWorld(name)
            : GameplayActorTestRig.CreateActor(name);
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

        // Adversarial setup: the bot's own last decision is still staged on the
        // actor when an operator command arrives. The injected request and its
        // audit row must NOT inherit it — an operator's action is not the bot's
        // decision, and the row must say so (command_origin). The staged seed
        // deliberately repeats the operator's own cycle-id literal below, so the
        // two provenances cannot be told apart by their text alone.
        rig.Actor.SetPendingDecision("quest.progress", "quest-v1", 2, 1, "opaque-source", 251);

        // The scheduler never steps this bot (stopped/not started) — the
        // actor's own Tick never runs, so the queue backstop must expire the
        // request: a command can never hang the actor indefinitely.
        var result = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move,
                Destination: new Vector3(50, 0, 0), Timeout: TimeSpan.FromSeconds(2),
                DecisionCycleId: "opaque-source", WakeSequence: 17));
        rig.Queue.DrainCommands();
        var request = rig.Actor.ActiveRequest!;
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(request.DecisionQuestId).IsNull();
        await Assert.That(request.DecisionGoal).IsNull();
        await Assert.That(request.CommandOrigin).IsEqualTo("control-api");
        // The caller's own join keys are the OPERATOR's provenance, not the
        // bot's decision: the API passes them to correlate its trace, so they
        // round-trip onto the request (unlike the staged decision above).
        await Assert.That(request.DecisionCycleId).IsEqualTo("opaque-source");
        await Assert.That(request.WakeSequence).IsEqualTo(17L);
        await Assert.That(result.TraceId).IsNotEqualTo(request.TraceId);

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(result.TraceId, out var snap)).IsTrue();
        await Assert.That(snap.State).IsEqualTo(nameof(ActorLifecycleState.TimedOut));
        await Assert.That(snap.Failure).IsEqualTo(nameof(ActorFailureReason.Navigation));
        await Assert.That(snap.AuditJson).IsNotNull();
        using var audit = JsonDocument.Parse(snap.AuditJson!);
        var row = audit.RootElement;
        await Assert.That(row.GetProperty("trace_id").GetGuid()).IsEqualTo(request.TraceId);
        await Assert.That(row.GetProperty("command_origin").GetString()).IsEqualTo("control-api");
        await Assert.That(row.GetProperty("decision_quest_id").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(row.GetProperty("decision_goal").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(row.GetProperty("decision_policy").ValueKind).IsEqualTo(JsonValueKind.Null);
        // The bot's staged decision vocabulary is gone, but the caller-supplied
        // join keys stay: they are the operator's provenance, and dropping them
        // would break the control plane's trace correlation.
        await Assert.That(row.GetProperty("decision_cycle_id").GetString()).IsEqualTo("opaque-source");
        await Assert.That(row.GetProperty("wake_sequence").GetInt64()).IsEqualTo(17L);
        await Assert.That(row.GetProperty("decision_seed").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(row.GetProperty("decision_candidates").GetInt32()).IsEqualTo(0);
        await Assert.That(row.GetProperty("decision_rejections").GetInt32()).IsEqualTo(0);
        await Assert.That(row.GetProperty("requested_at").GetDateTime()).IsEqualTo(request.RequestedAtUtc);

        // The actor is usable again afterwards (no wedge).
        var observe = rig.Queue.Enqueue("api-bot", new BotActionSpec(BotActionKind.Observe));
        rig.Queue.DrainCommands();
        await Assert.That(rig.Queue.TryGetSnapshot(observe.TraceId, out var o)).IsTrue();
        await Assert.That(o.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        using var after = JsonDocument.Parse(o.AuditJson!);
        await Assert.That(after.RootElement.GetProperty("decision_quest_id").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
    [Test]
    public async Task BackstopExpiry_RecordsLedgerOutcome_SameKeyRetryRefused()
    {
        // Q-ledger parity: a backstop-expired request must lock its explicit
        // key as TimedOut even though the actor's Finish never ran —
        // otherwise the same-key retry would execute instead of being
        // dedupe-refused (double-effect risk when the stalled execution had
        // already applied).
        var rig = CreateRig();

        var first = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move,
                Destination: new Vector3(50, 0, 0),
                Timeout: TimeSpan.FromSeconds(2),
                IdempotencyKey: "k-ledger"));
        rig.Queue.DrainCommands();

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(first.TraceId, out var f)).IsTrue();
        await Assert.That(f.State).IsEqualTo(nameof(ActorLifecycleState.TimedOut));

        var retry = rig.Queue.Enqueue("api-bot",
            new BotActionSpec(BotActionKind.Move,
                Destination: new Vector3(50, 0, 0), IdempotencyKey: "k-ledger"));
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(retry.TraceId, out var s)).IsTrue();
        await Assert.That(s.State).IsEqualTo(nameof(ActorLifecycleState.Rejected));
        await Assert.That(s.Detail).Contains("duplicate idempotency key");
        await Assert.That(s.Detail).Contains("k-ledger");
    }

    [Test]
    public async Task UseItem_NativeCompletionWithoutActorTicks_BackstopReportsSuccess()
    {
        var rig = CreateRig("queue-native-item-complete");
        var item = OrdinaryRecoveryTestRig.StockHeal(rig.Actor, 98561, 40, 150);
        rig.Actor.Character.Hp = 35;
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var submitted = rig.Queue.Enqueue(rig.Actor.Character.Name,
            new BotActionSpec(BotActionKind.UseItem, TargetId: item.TemplateId, IdempotencyKey: "native-item-complete"));
        rig.Queue.DrainCommands();
        var request = rig.Actor.ActiveRequest!;
        await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        rig.Clock.Advance(GameplayActor.DefaultItemUseTimeout + TimeSpan.FromSeconds(1));
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        rig.Queue.DrainCommands();

        await Assert.That(rig.Queue.TryGetSnapshot(submitted.TraceId, out var snapshot)).IsTrue();
        await Assert.That(snapshot.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(item.Count).IsEqualTo(1);
        await Assert.That(rig.Actor.Character.Hp).IsGreaterThan(35);
    }

    [Test]
    public async Task UseItem_PendingBackstopExpiry_CancelsNativeWorkAndConservesItem()
    {
        var rig = CreateRig("queue-native-item-timeout");
        var item = OrdinaryRecoveryTestRig.StockHeal(rig.Actor, 98562, 40, 300);
        rig.Actor.Character.Hp = 35;
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var submitted = rig.Queue.Enqueue(rig.Actor.Character.Name,
            new BotActionSpec(BotActionKind.UseItem, TargetId: item.TemplateId, IdempotencyKey: "native-item-timeout"));
        rig.Queue.DrainCommands();
        rig.Clock.Advance(GameplayActor.DefaultItemUseTimeout + TimeSpan.FromSeconds(1));
        rig.Queue.DrainCommands();
        await scheduler.PumpFor(rig.Actor, TimeSpan.FromMilliseconds(500));

        await Assert.That(rig.Queue.TryGetSnapshot(submitted.TraceId, out var snapshot)).IsTrue();
        await Assert.That(snapshot.State).IsEqualTo(nameof(ActorLifecycleState.TimedOut));
        await Assert.That(rig.Actor.ActiveRequest).IsNull();
        await Assert.That(item.Count).IsEqualTo(2);
        await Assert.That(rig.Actor.Character.Hp).IsEqualTo(35);
        await Assert.That(rig.Actor.UseItem(item.TemplateId, idempotencyKey: "native-item-timeout").State)
            .IsEqualTo(ActorLifecycleState.Rejected);
    }

    [Test]
    public async Task Craft_PendingNativeBackstop_CancelsExactWorkAndReleasesQueue()
    {
        var rig = CreateRig("queue-native-craft-timeout");
        GameplayActorTestRig.SeedCraftSurface();
        var bench = GameplayActorTestRig.SpawnCraftBench(rig.Session, rig.Actor);
        rig.Actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(rig.Actor, GameplayActorTestRig.CraftMaterialTemplateId, 2);
        using var fixture = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var submitted = rig.Queue.Enqueue(rig.Actor.Character.Name,
            new BotActionSpec(BotActionKind.Craft, TargetId: GameplayActorTestRig.CraftTestCraftId,
                Payload: new CraftActionParams(bench), IdempotencyKey: "native-craft-backstop"));
        rig.Queue.DrainCommands();
        rig.Clock.Advance(GameplayActor.DefaultCraftTimeout + TimeSpan.FromSeconds(1));
        rig.Queue.DrainCommands();
        await scheduler.PumpFor(rig.Actor, TimeSpan.FromMilliseconds(350));
        await Assert.That(rig.Queue.TryGetSnapshot(submitted.TraceId, out var snapshot)).IsTrue();
        await Assert.That(snapshot.State).IsEqualTo(nameof(ActorLifecycleState.TimedOut));
        await Assert.That(GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(0);
        await Assert.That(rig.Actor.Character.LaborPower).IsEqualTo((short)100);
        await Assert.That(rig.Actor.Character.Craft.IsCraftQueueActive).IsFalse();
        await Assert.That(rig.Actor.ActiveRequest).IsNull();
        await Assert.That(rig.Actor.Character.SkillTask).IsNull();
    }

    [Test]
    public async Task Craft_NativeCommitWithoutActorTicks_BackstopReportsCompleted()
    {
        var rig = CreateRig("queue-native-craft-complete");
        GameplayActorTestRig.SeedCraftSurface();
        var bench = GameplayActorTestRig.SpawnCraftBench(rig.Session, rig.Actor);
        rig.Actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(rig.Actor, GameplayActorTestRig.CraftMaterialTemplateId, 2);
        using var fixture = new GameplayActorCraftTests.NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var submitted = rig.Queue.Enqueue(rig.Actor.Character.Name,
            new BotActionSpec(BotActionKind.Craft, TargetId: GameplayActorTestRig.CraftTestCraftId,
                Payload: new CraftActionParams(bench), IdempotencyKey: "native-craft-committed"));
        rig.Queue.DrainCommands();
        await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
        rig.Clock.Advance(GameplayActor.DefaultCraftTimeout + TimeSpan.FromSeconds(1));
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        rig.Queue.DrainCommands();
        await Assert.That(rig.Queue.TryGetSnapshot(submitted.TraceId, out var snapshot)).IsTrue();
        await Assert.That(snapshot.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(rig.Actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
        await Assert.That(rig.Actor.Character.LaborPower).IsEqualTo((short)90);
        await Assert.That(rig.Actor.ActiveRequest).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Harvest_NativeQueueBackstop_CancelsPendingOrObservesCommitted(bool committed)
    {
        var previousWorld = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        try
        {
            CropHarvestLoopRig.Seed();
            var rig = CreateRig("queue-native-harvest-" + committed, cropWorld: true);
            var crop = GameplayActorHarvestTests.PlantMatureCrop(rig.Actor, rig.Session);
            rig.Actor.Character.LaborPower = 100;
            using var fixture = new GameplayActorHarvestTests.NativeHarvestSkill(150);
            using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
            var submitted = rig.Queue.Enqueue(rig.Actor.Character.Name,
                new BotActionSpec(BotActionKind.Harvest, TargetId: crop.ObjId, IdempotencyKey: "native-harvest-backstop"));
            rig.Queue.DrainCommands();
            var request = rig.Actor.ActiveRequest!;
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
            if (committed)
                await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
            rig.Clock.Advance(GameplayActor.DefaultHarvestTimeout + TimeSpan.FromSeconds(1));
            ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
            rig.Queue.DrainCommands();
            await scheduler.PumpFor(rig.Actor, TimeSpan.FromMilliseconds(350));
            await Assert.That(rig.Queue.TryGetSnapshot(submitted.TraceId, out var snapshot)).IsTrue();
            await Assert.That(snapshot.State).IsEqualTo((committed ? ActorLifecycleState.Completed : ActorLifecycleState.TimedOut).ToString());
            await Assert.That(rig.Actor.Character.LaborPower).IsEqualTo(committed ? (short)99 : (short)100);
            var yield = GameplayActorTestRig.BagCount(rig.Actor, CropHarvestLoopTests.PotatoItemId);
            await Assert.That(committed ? yield >= 2 : yield == 0).IsTrue();
            await Assert.That(rig.Actor.ActiveRequest).IsNull();
            await Assert.That(rig.Actor.Character.SkillTask).IsNull();
        }
        finally
        {
            AppConfiguration.Instance.World = previousWorld;
        }
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
