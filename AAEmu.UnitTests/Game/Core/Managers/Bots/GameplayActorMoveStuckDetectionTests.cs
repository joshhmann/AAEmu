using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Models;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 hardening #5 — Move stuck detection (scoped slice): a Running Move
/// leg that makes no meaningful positional progress over
/// <see cref="GameplayActor.NoProgressWindow"/> must fail fast — well
/// before its navigation budget expires — instead of silently burning the
/// whole timeout. The declaration is TimedOut(Navigation) via Expire with
/// a "stuck:" detail prefix (discriminates it from a plain budget expiry,
/// "navigation budget exceeded"), preceded by at most
/// <see cref="GameplayActor.MaxUnstickNudges"/> bounded unstick nudge legs.
///
/// Blocking model: the rig pins the character back to its start position
/// after every tick (the engine's straight-line walk advances, terrain
/// "snaps" it back) — net displacement stays zero, exactly like a blocked
/// mover.
/// </summary>
[NotInParallel]
public class GameplayActorMoveStuckDetectionTests
{
    // -- movement singleton seeding (same pattern as GameplayActorTests) --

    private static object? _previousSusManager;
    private static object? _previousModelManager;

    /// <summary>
    /// FinalizeTransform runs delta-movement analysis through SusManager;
    /// SetPosition consults ModelManager. The headless process has no DI —
    /// seed both (capture/restore around each test).
    /// </summary>
    private static void SeedMovementSingletons()
    {
        _previousSusManager = typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        typeof(Singleton<SusManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new SusManager(WorldManager.Instance));

        _previousModelManager = typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null);
        var modelManager = new ModelManager();
        typeof(ModelManager)
            .GetField("_modelTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<uint, ModelType>());
        typeof(ModelManager)
            .GetField("_models", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>>());
        typeof(Singleton<ModelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, modelManager);
    }

    [After(Test)]
    public void RestoreMovementSingletons()
    {
        if (_previousSusManager != null)
            typeof(Singleton<SusManager>)
                .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, _previousSusManager);
        if (_previousModelManager != null)
            typeof(Singleton<ModelManager>)
                .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null, _previousModelManager);
        _previousSusManager = null;
        _previousModelManager = null;
    }

    [Test]
    public async Task Move_BlockedMover_FailsFastWithStuckNavigationBeforeBudgetExpiry()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("stuck-1");
        SeedMovementSingletons();
        actor.NoProgressWindow = TimeSpan.FromSeconds(1);
        actor.MaxUnstickNudges = 0; // fail on the FIRST stuck declaration

        var start = new Vector3(512, 512, 0);
        GameplayActorTestRig.SetPosition(actor, start);

        // 30 s budget — a stuck declaration MUST arrive long before expiry.
        var request = actor.MoveTo(new Vector3(612, 512, 0), speed: 5f,
            timeout: TimeSpan.FromSeconds(30));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);

        var ticks = 0;
        while (ticks++ < 200)
        {
            actor.Tick(TimeSpan.FromMilliseconds(250));
            if (request.IsTerminal)
                break;
            GameplayActorTestRig.SetPosition(actor, start); // terrain blocks: snap back
        }

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Navigation);
        // Caller-visible discriminator: "stuck", NOT the plain budget-expiry
        // wording.
        await Assert.That(request.Detail!).Contains("stuck");
        await Assert.That(request.Detail!.Contains("navigation budget exceeded")).IsFalse();
        // Fail-fast beats timeout: declared ≤5 s into a 30 s budget.
        await Assert.That(request.Elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(actor.ActiveRequest).IsNull();

        var record = actor.AuditTrace[^1];
        await Assert.That(record.TraceId).IsEqualTo(request.TraceId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(record.Failure).IsEqualTo(ActorFailureReason.Navigation);
    }

    [Test]
    public async Task Move_StuckDetectionDisabled_RidesToFullTimeout()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("stuck-2");
        SeedMovementSingletons();
        // Zero disables the seam entirely — legacy behavior byte-for-byte.
        actor.NoProgressWindow = TimeSpan.Zero;

        var start = new Vector3(512, 512, 0);
        GameplayActorTestRig.SetPosition(actor, start);

        var request = actor.MoveTo(new Vector3(612, 512, 0), speed: 5f,
            timeout: TimeSpan.FromMilliseconds(500));

        var ticks = 0;
        while (ticks++ < 100)
        {
            actor.Tick(TimeSpan.FromMilliseconds(100));
            if (request.IsTerminal)
                break;
            GameplayActorTestRig.SetPosition(actor, start);
        }

        // The leg rode its FULL budget to the plain navigation expiry.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Navigation);
        await Assert.That(request.Elapsed).IsGreaterThan(TimeSpan.FromMilliseconds(500));
        await Assert.That(request.Detail!).Contains("navigation budget exceeded");
        await Assert.That(request.Detail!.Contains("stuck")).IsFalse();
    }

    [Test]
    public async Task Move_UnstickNudge_ResumesAndCompletesWhenUnblocked()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("stuck-3");
        SeedMovementSingletons();
        actor.NoProgressWindow = TimeSpan.FromMilliseconds(500);
        actor.MaxUnstickNudges = 1;

        var start = new Vector3(512, 512, 0);
        GameplayActorTestRig.SetPosition(actor, start);

        var request = actor.MoveTo(new Vector3(612, 512, 0), speed: 5f);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);

        // Phase 1: block briefly — the bounded nudge fires (window 0.5 s
        // reached on the 5th 100 ms tick), the request stays Running
        // (recovery, not failure).
        for (var i = 0; i < 5; i++)
        {
            actor.Tick(TimeSpan.FromMilliseconds(100));
            GameplayActorTestRig.SetPosition(actor, start);
        }
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);

        // Phase 2: the blocker is gone — the leg resumes through the
        // recovery waypoint and completes normally.
        var guard = 0;
        while (request.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 400)
            actor.Tick(TimeSpan.FromMilliseconds(100));

        await Assert.That(request.Detail).IsEqualTo("arrived");
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var position = actor.Character.Transform.World.Position;
        await Assert.That(Math.Abs(position.X - 612f)).IsLessThan(0.5f);
        await Assert.That(Math.Abs(position.Y - 512f)).IsLessThan(0.5f);
        await Assert.That(actor.AuditTrace.Any(r => r.Result == ActorLifecycleState.TimedOut)).IsFalse();
    }

    [Test]
    public async Task Move_NormalArrival_WithStuckDetectionEnabled_StillCompletes()
    {
        // Regression: defaults (window 2.5 s, one nudge) must not perturb a
        // healthy leg — every tick displaces over the arrival radius, so no
        // stuck state is ever reached.
        var (actor, _) = GameplayActorTestRig.CreateActor("stuck-4");
        SeedMovementSingletons();
        GameplayActorTestRig.SetPosition(actor, new Vector3(512, 512, 0));

        var request = actor.MoveTo(new Vector3(552, 512, 0), speed: 5f);
        for (var i = 0; i < 12 && !request.IsTerminal; i++)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("arrived");
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - 552f)).IsLessThan(0.5f);
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == request.TraceId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace[0].Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Move_SecondStuckDeclaration_AfterNudge_FailsTheLeg()
    {
        // One nudge did not help (still pinned) — the NEXT stuck
        // declaration fails the request; the recovery budget is bounded.
        var (actor, _) = GameplayActorTestRig.CreateActor("stuck-5");
        SeedMovementSingletons();
        actor.NoProgressWindow = TimeSpan.FromMilliseconds(400);
        actor.MaxUnstickNudges = 1;

        var start = new Vector3(512, 512, 0);
        GameplayActorTestRig.SetPosition(actor, start);

        var request = actor.MoveTo(new Vector3(612, 512, 0), speed: 5f,
            timeout: TimeSpan.FromSeconds(30));

        var ticks = 0;
        while (ticks++ < 200)
        {
            actor.Tick(TimeSpan.FromMilliseconds(200));
            if (request.IsTerminal)
                break;
            GameplayActorTestRig.SetPosition(actor, start);
        }

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Navigation);
        await Assert.That(request.Detail!).Contains("stuck");
        // Window 0.4 s × 2 declarations « the 30 s budget: still fail-fast.
        await Assert.That(request.Elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(5));
    }
}
