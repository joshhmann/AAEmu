using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Scheduler-seam rig for the M5 actor (slice #8): GameplayActorStepExecutor
/// proves the actor adapts into the IBotStepExecutor seam the scheduler owns
/// (one actor Tick per wake, live requests keep the scan cadence, idle bots
/// go dormant). The scheduler's per-bot lease guarantees single-writer
/// access, so this rig drives steps sequentially — exactly one worker.
/// </summary>
[NotInParallel]
public class GameplayActorStepExecutorTests
{
    private static (GameplayActorStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateRig(
        string name = "step-exec-bot")
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();

        GameplayActorStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock
        };

        return (executor, actor, runtime, clock);
    }

    [Test]
    public async Task Step_LiveMove_ReturnsCadence_AndAdvancesPosition()
    {
        var (executor, actor, runtime, clock) = CreateRig("step-1");

        var move = actor.MoveTo(new Vector3(4, 0, 0), speed: 2f);

        // First step: elapsed = one cadence (100ms) → 0.2 units.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var next = await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(next).IsNotNull();
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - 0.2f) <= 0.01f).IsTrue();
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);

        // Keep stepping to arrival (speed 2/s, 4 units → ~20 steps at 100ms).
        var guard = 0;
        while (next is not null && guard++ < 100)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            next = await executor.StepAsync(runtime, CancellationToken.None);
        }

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Completed);
        // Arrival completes within the actor's ArrivalRadius (0.5f) — the
        // final tick completes without overstepping the destination.
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - 4f) <= 0.5f).IsTrue();
    }

    [Test]
    public async Task Step_IdleActor_ReturnsNull_Dormant()
    {
        var (executor, _, runtime, _) = CreateRig("step-2");

        var next = await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task Step_AfterCompletion_GoesDormant()
    {
        var (executor, actor, runtime, clock) = CreateRig("step-3");

        var move = actor.MoveTo(new Vector3(0.1f, 0, 0), speed: 2f); // near arrival
        var guard = 0;
        TimeSpan? next = TimeSpan.Zero;
        while (next is not null && guard++ < 50)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            next = await executor.StepAsync(runtime, CancellationToken.None);
        }

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task Step_InterruptedMove_GoesDormant()
    {
        var (executor, actor, runtime, _) = CreateRig("step-4");

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        await Assert.That(actor.Interrupt(move.TraceId)).IsTrue();

        var next = await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(next).IsNull();
    }

    [Test]
    public async Task Step_Cancelled_ThrowsOperationCanceled()
    {
        var (executor, _, runtime, _) = CreateRig("step-5");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.That(async () => await executor.StepAsync(runtime, cts.Token))
            .Throws<OperationCanceledException>();
    }
}
