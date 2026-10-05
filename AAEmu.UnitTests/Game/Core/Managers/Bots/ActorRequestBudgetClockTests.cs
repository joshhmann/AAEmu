using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The monotonic BUDGET clock on <see cref="ActorRequest"/>.
///
/// The defect this pins: <c>Elapsed</c> grew only when the caller's
/// <c>GameplayActor.Tick</c> handed the request a delta, so a stalled scheduler
/// froze a timeout budget that wall-clock had already blown through — a watchdog
/// that cannot expire is not a watchdog. The budget is now measured against
/// <see cref="ActorRequest.BudgetElapsed"/>, the greater of the accumulated tick
/// deltas and the request's own injected clock.
///
/// Both sources are pinned, because taking the greater is only honest if NEITHER
/// can be lost: a stalled scheduler still expires on the clock, and a synthetic
/// delta faster than real time still expires on the deltas.
/// </summary>
[NotInParallel]
public class ActorRequestBudgetClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A request that never started measures zero on both sources — a clock that
    /// was never stamped contributes nothing rather than a fabricated age.
    /// </summary>
    [Test]
    public async Task BudgetElapsed_BeforeStart_IsZeroOnBothSources()
    {
        var clock = new FakeTimeProvider(T0);
        var request = NewRequest(clock);
        clock.Advance(TimeSpan.FromHours(1));

        await Assert.That(request.BudgetElapsed).IsEqualTo(TimeSpan.Zero);
        await Assert.That(request.Elapsed).IsEqualTo(TimeSpan.Zero);
    }

    /// <summary>The clock reads as soon as execution begins, even with no tick delta ever added.</summary>
    [Test]
    public async Task BudgetElapsed_FollowsTheInjectedClockWithNoTickDelta()
    {
        var clock = new FakeTimeProvider(T0);
        var request = NewRequest(clock);
        request.Accept("running");
        clock.Advance(TimeSpan.FromSeconds(7));

        // Elapsed (the caller's deltas) is untouched — the scheduler sent nothing.
        await Assert.That(request.Elapsed).IsEqualTo(TimeSpan.Zero);
        await Assert.That(request.BudgetElapsed).IsEqualTo(TimeSpan.FromSeconds(7));
    }

    /// <summary>
    /// The normal path is unchanged: a caller that drives synthetic deltas
    /// faster than the clock still measures its own deltas, because the budget
    /// takes the GREATER of the two sources, never their sum.
    /// </summary>
    [Test]
    public async Task BudgetElapsed_KeepsTheLargerOfDeltasAndClock()
    {
        var clock = new FakeTimeProvider(T0);
        var request = NewRequest(clock);
        request.Accept("accepted");
        request.Start("running"); // deltas only accumulate once the request is Running
        request.AddElapsed(TimeSpan.FromSeconds(9));

        // Deltas lead (9 s > 4 s) and are used as-is — not 13 s.
        await Assert.That(request.BudgetElapsed).IsEqualTo(TimeSpan.FromSeconds(9));

        // Now the clock leads (30 s > 9 s) and takes over.
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(request.BudgetElapsed).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// A FROZEN SCHEDULER — a tick that hands out no delta at all — still expires
    /// a request whose injected clock has passed its budget. This is the whole
    /// point of the seam: without it the budget would sit at zero forever.
    /// </summary>
    [Test]
    public async Task FrozenScheduler_StillExpiresPastBudget()
    {
        var clock = new FakeTimeProvider(T0);
        var (_, session) = GameplayActorTestRig.CreateActor("budget-frozen-clock");
        var actor = new GameplayActor(session.Character) { TimeProvider = clock };
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);

        var budget = TimeSpan.FromSeconds(5);
        var request = actor.MoveTo(new Vector3(900f, 900f, 20f), speed: 2f, timeout: budget);
        await Assert.That(request.IsTerminal).IsFalse();

        // The scheduler stalls: wall-clock passes the budget, the tick delta is zero.
        clock.Advance(TimeSpan.FromSeconds(30));
        actor.Tick(TimeSpan.Zero);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Navigation);
    }

    /// <summary>
    /// The production-shaped path is untouched: with the default system clock a
    /// caller that ticks real deltas past its budget still expires exactly as
    /// before — the seam only adds a second, non-reducing source.
    /// </summary>
    [Test]
    public async Task TickDeltaPath_StillExpiresOnItsOwnDeltas()
    {
        var (_, session) = GameplayActorTestRig.CreateActor("budget-delta-path");
        var actor = new GameplayActor(session.Character);
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);

        var budget = TimeSpan.FromSeconds(5);
        var request = actor.MoveTo(new Vector3(900f, 900f, 20f), speed: 2f, timeout: budget);
        await Assert.That(request.IsTerminal).IsFalse();

        // Real delta accumulation, no injected clock: 2 s + 2 s + 2 s crosses the
        // 5 s budget exactly as it did before the seam existed.
        actor.Tick(TimeSpan.FromSeconds(2));
        actor.Tick(TimeSpan.FromSeconds(2));
        await Assert.That(request.IsTerminal).IsFalse();
        actor.Tick(TimeSpan.FromSeconds(2));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Navigation);
    }

    // ------------------------------------------------------------- fixture

    private static ActorRequest NewRequest(TimeProvider clock)
        => new(ActorActionType.Move, targetId: 0, destination: null, skillId: 0, timeout: TimeSpan.FromSeconds(5))
        {
            Clock = clock
        };
}
