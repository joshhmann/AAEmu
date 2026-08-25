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
    // Regression guard for the 615a645c9 roam-churn win (-38%/wake; pre-fix
    // steady state was ~789B/step, post-fix ~488B). The budget must tolerate
    // legitimate JIT variance while still catching a real churn regression,
    // which is orders of magnitude larger than that variance.
    //
    // Boundary-flake evidence (2026-08-25): with MaxBytesPerRoamStep = 512
    // (zero margin under the strict '<' comparison) the same byte-identical
    // sources PASSED the full gate at 09:37 and FAILED deterministically
    // (>10 consecutive runs) from ~10:11 — isolated and full-suite, Debug
    // and Release, and on the parent commit rebuilt in a clean worktree;
    // DOTNET_TieredCompilation=0 measured 537B/step. The measured total
    // tracks JIT compilation strategy/timing, not code changes.
    //
    // 768B = observed cross-mode max (537B) × ~1.4 headroom, and still ~3%
    // below the PRE-FIX level (~789B): a real regression back toward the
    // old churn blows past this budget by a wide margin.
    private const long MaxBytesPerRoamStep = 768;

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
