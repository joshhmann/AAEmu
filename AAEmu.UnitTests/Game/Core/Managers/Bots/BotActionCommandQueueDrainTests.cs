using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class BotActionCommandQueueDrainTests
{
    // Supply one registered actor only. Dispatch, Observe, audit and snapshots
    // use the real queue/executor/actor; no terminal result is supplied by the rig.
    private sealed class SeededManager(PlayerBotRuntime runtime) : IPlayerBotManager
    {
        public bool TryGet(uint characterId, out PlayerBotRuntime? found)
        {
            found = characterId == runtime.CharacterId ? runtime : null;
            return found != null;
        }

        public IReadOnlyList<PlayerBotRuntime> GetAll() => [runtime];
        public IReadOnlyList<PlayerBotRuntime> GetActive() => [runtime];
        public int Count => 1;
        public int ActiveCount => 1;
        public PlayerBotDiagnostics GetDiagnostics() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
        public bool Spawn(Character character, string owner) => throw new NotSupportedException();
        public bool Activate(uint characterId, object? botContext, string owner) => throw new NotSupportedException();
        public bool Deactivate(uint characterId, string reason) => throw new NotSupportedException();
        public bool Remove(uint characterId) => throw new NotSupportedException();
        public bool LeaveHeadlessCharacter(Character expected, CharacterLifecycleReason reason,
            out bool savedThisCall, Func<bool>? releaseCachedSession = null) => throw new NotSupportedException();
    }

    [Before(Test)]
    public void PinBoundary()
        => ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);

    [After(Test)]
    public void UnpinBoundary()
        => ExecutionBoundary.ResetForTest();

    [Test]
    public async Task DrainCommands_OverBudget_RetainsAndCompletesEveryAcceptedCommand()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("drain-budget-bot");
        var runtime = new PlayerBotRuntime(actor.Character, "drain-budget-rig") { State = PlayerBotState.Active };
        var executor = new BotRoamStepExecutor { ActorFactory = _ => actor };
        var options = new BotActionQueueOptions { SubscribeToTickManager = false };
        var queue = new BotActionCommandQueue(new SeededManager(runtime),
            Mock.Of<IPlayerBotScheduler>().Object, executor, options);
        var commands = new List<BotActionEnqueueResult>();
        for (var i = 0; i <= options.MaxCommandsPerDrain; i++)
            commands.Add(queue.Enqueue(actor.Character.Name, new BotActionSpec(BotActionKind.Observe)));

        // Perform all drains before asynchronous assertions so execution stays
        // on the pinned boundary thread, exactly as with a game-loop tick.
        queue.DrainCommands();
        var firstPassCompleted = commands.Count(command =>
            queue.TryGetSnapshot(command.TraceId, out var snapshot)
            && snapshot.State == nameof(ActorLifecycleState.Completed));
        var firstPassQueued = queue.GetStats().Queued;
        var pendingFound = queue.TryGetSnapshot(commands[^1].TraceId, out var pending);
        queue.DrainCommands();
        var completed = commands.Select(command =>
        {
            queue.TryGetSnapshot(command.TraceId, out var snapshot);
            return snapshot;
        }).ToArray();
        var lastAudit = actor.AuditTrace.Last();
        queue.DrainCommands();

        await Assert.That(commands.All(command => command.Ok)).IsTrue();
        await Assert.That(firstPassCompleted).IsEqualTo(options.MaxCommandsPerDrain);
        await Assert.That(firstPassQueued).IsEqualTo(1);
        await Assert.That(pendingFound).IsTrue();
        await Assert.That(pending.State).IsEqualTo(nameof(ActorLifecycleState.Requested));
        await Assert.That(pending.AuditJson).IsNull();
        foreach (var snapshot in completed)
        {
            await Assert.That(snapshot.State).IsEqualTo(nameof(ActorLifecycleState.Completed));
            await Assert.That(snapshot.AuditJson).IsNotNull();
            await Assert.That(snapshot.Result).IsAssignableTo<ActorObservation>();
            await Assert.That(((ActorObservation)snapshot.Result!).ActorId).IsEqualTo(actor.ActorId);
        }
        await Assert.That(queue.GetStats().Queued).IsEqualTo(0);
        await Assert.That(actor.AuditTrace.Last()).IsEqualTo(lastAudit);
    }
}
