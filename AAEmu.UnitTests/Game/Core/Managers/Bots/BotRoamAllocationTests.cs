using System.Diagnostics;
using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills.Buffs;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Scheduler-roam tick-path allocation regression tests (soak stage-1 engine
/// finding (c) — heap churn under roam activity). The soak drove 10 citizens
/// through real PlayerBotScheduler wakes and watched RSS climb ~2GB+ within
/// minutes while the scheduler-disabled baseline stayed flat. This pin makes
/// the per-wake/per-tick allocation cost of that loop measurable and bounded:
///
///   - one steady-state roam STEP (BotRoamStepExecutor.StepAsync: issue leg /
///     tick actor / ground clamp / throttled broadcast), and
///   - one zero-buff movement REMOVE-ON sweep (Buffs.TriggerRemoveOn — the
///     VehicleMovementModel.RemoveEffects call on EVERY walk tick, bots and
///     real clients alike).
///
/// Measurement seam: GC.GetAllocatedBytesForCurrentThread() deltas around a
/// warm, repeated simulated-wake loop (the RegionBroadcastAllocationTests A2
/// convention). Budgets are per-operation so machine speed cannot flake them.
/// </summary>
[NotInParallel]
public class BotRoamAllocationTests
{
    private const int WarmupSteps = 300;
    private const int MeasuredSteps = 2_000;

    /// <summary>Per-step budget (bytes allocated per scheduler wake/roam step).</summary>
    // Post-fix measured 488B/step (pre-fix: 789B). The bot-layer churn is
    // fixed (per-apply packet construction skipped, throttled broadcast kept);
    // the remaining budget is dominated by Transform.FinalizeTransform
    // (_lastFinalizePos clone + AddVisibleObject) — an ENGINE path shared with
    // real player movement, queued as its own follow-up card.
    private const long MaxBytesPerRoamStep = 512;

    /// <summary>Budget for a zero-buff TriggerRemoveOn sweep (must be effectively free).</summary>
    private const long MaxBytesPerZeroBuffRemoveOnSweep = 64;

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateRig(
        string name = "alloc-bot")
    {
        // No config JSON is loaded in unit tests: AppConfiguration.Instance.World
        // is null by default (GameplayActorTestRig convention) — the real
        // ApplyUnitMove path needs World.MOTD headless.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();

        BotRoamStepExecutor executor = new()
        {
            GroundHeightProvider = static (_, _) => 0f, // no heightmap data — clamp skipped (rig shape)
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f
        };

        return (executor, actor, runtime, clock);
    }

    /// <summary>Steps the executor once (one simulated scheduler wake).</summary>
    private static TimeSpan? Step(
        BotRoamStepExecutor executor, PlayerBotRuntime runtime, FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(100));
        return executor.StepAsync(runtime, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    [Test]
    public async Task RoamLoop_SteadyStateWalk_BoundedAllocationPerStep()
    {
        var (executor, _, runtime, clock) = CreateRig("alloc-roam");

        var route = new BotPath(
        [
            new Vector3(50, 0, 0),
            new Vector3(100, 0, 0),
            new Vector3(100, 50, 0),
            new Vector3(50, 50, 0)
        ], BotPath.LoopMode.Loop);
        executor.SetRoamRoute(runtime.Character, route);

        // Warm up: JIT tiering, leg lifecycle shapes, audit-trace ring fill,
        // broadcast throttle state.
        for (var i = 0; i < WarmupSteps; i++)
            Step(executor, runtime, clock);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredSteps; i++)
            Step(executor, runtime, clock);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var perStep = allocated / MeasuredSteps;
        await Assert.That(perStep < MaxBytesPerRoamStep)
            .IsTrue()
            .Because($"steady-state roam steps must stay under {MaxBytesPerRoamStep}B/wake " +
                     $"(soak finding (c): heap churn under roam activity); saw {perStep}B/step");
    }

    [Test]
    public async Task Buffs_TriggerRemoveOn_ZeroEffects_EffectivelyAllocationFree()
    {
        // Every walk tick calls RemoveEffects → TriggerRemoveOn(Move) with a
        // non-zero velocity (BuildCharacterMove always carries velocity while
        // walking). With ZERO active buffs the sweep must be free — the old
        // code snapshot-copied the empty effect list twice per tick.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor("alloc-removeon");
        var buffs = actor.Character.Buffs;

        // Warm.
        for (var i = 0; i < 1_000; i++)
            buffs.TriggerRemoveOn(BuffRemoveOn.Move);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
            buffs.TriggerRemoveOn(BuffRemoveOn.Move);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated < MaxBytesPerZeroBuffRemoveOnSweep)
            .IsTrue()
            .Because("a zero-buff TriggerRemoveOn sweep must be allocation-free " +
                     "(old code: two list snapshots per call); saw " + allocated + " bytes");
    }
}
