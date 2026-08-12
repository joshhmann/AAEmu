using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Skills.Static;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 actor contract rig (slice #8) — the SERVER side of the spec §17 split.
///
/// These tests prove the actor executes each action correctly through the
/// REAL engine paths (Transform movement, Unit.CurrentTarget, the
/// Character.UseSkill learned-skill branch), and that the lifecycle
/// (Requested → Accepted → Running → Completed | Rejected | Interrupted |
/// TimedOut) + audit trace behave per contract. They deliberately do NOT
/// judge whether a controller chose the right action — that is the
/// behavior-test track (spec §17 split).
///
/// Every action type is proven for at least one of: accept, reject,
/// interrupt, timeout; the shared lifecycle machinery (busy gate, trace
/// emission) is proven across actions.
/// </summary>
[NotInParallel]
public class GameplayActorTests
{
    #region Observe

    [Test]
    public async Task Observe_ReturnsDirectServerState_AndEmitsAuditRecord()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("observe-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(10, 20, 30));
        actor.Character.Hp = 55;
        actor.Character.Mp = 44;

        var observation = actor.Observe();

        await Assert.That(observation.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(observation.Position).IsEqualTo(new Vector3(10, 20, 30));
        await Assert.That(observation.Hp).IsEqualTo(55);
        await Assert.That(observation.Mp).IsEqualTo(44);
        await Assert.That(observation.CurrentTargetObjId).IsEqualTo(0u);
        await Assert.That(observation.ActiveQuestIds).IsNotNull();

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Observe);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.StartedAtUtc != default).IsTrue();
        await Assert.That(record.CompletedAtUtc != default).IsTrue();
    }

    #endregion

    #region Move

    [Test]
    public async Task Move_ValidDestination_AcceptedRunningCompleted_PositionAdvancesPerTick()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var request = actor.MoveTo(new Vector3(10, 0, 0), speed: 2f);

        // MoveTo accepts synchronously and starts the leg: the returned
        // request is already Running (Requested → Accepted → Running).
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(actor.ActiveRequest).IsNotNull();
        await Assert.That(ReferenceEquals(request, actor.ActiveRequest)).IsTrue();

        // One tick: 2 units at speed 2/s.
        actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - 2f) <= 0.001f).IsTrue();

        // Keep ticking until arrival.
        var guard = 0;
        while (request.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(Math.Abs(actor.Character.Transform.World.Position.X - 10f) <= 0.001f).IsTrue();
        await Assert.That(actor.ActiveRequest).IsNull();

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
        await Assert.That(record.StateChanges.Any(s => s.Contains("Completed"))).IsTrue();
    }

    [Test]
    public async Task Move_InvalidSpeed_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-2");

        var request = actor.MoveTo(new Vector3(5, 0, 0), speed: 0f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task Move_Timeout_ExpiresWithNavigationFailure()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-3");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var request = actor.MoveTo(new Vector3(100, 0, 0), speed: 1f, timeout: TimeSpan.FromMilliseconds(100));

        actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Navigation);
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        await Assert.That(actor.AuditTrace[0].Result).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(actor.AuditTrace[0].Failure).IsEqualTo(ActorFailureReason.Navigation);
    }

    [Test]
    public async Task Stop_InterruptsRunningMove_AndCompletesItself()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-4");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        var stop = actor.Stop();

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(stop.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move && r.Result == ActorLifecycleState.Interrupted)).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Stop && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task Move_WhileBusy_RejectedWithStateTransition()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-5");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var first = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        var second = actor.MoveTo(new Vector3(5, 0, 0), speed: 1f);

        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(second.Detail?.Contains("busy")).IsTrue();
        await Assert.That(ReferenceEquals(first, actor.ActiveRequest)).IsTrue();
    }

    [Test]
    public async Task Interrupt_ByTraceId_CancelsRunningRequest_AndIsIdempotent()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-6");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);

        await Assert.That(actor.Interrupt(move.TraceId)).IsTrue();
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        // Second interrupt of the same (now terminal) trace is a no-op.
        await Assert.That(actor.Interrupt(move.TraceId)).IsFalse();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MoveToUnit_UnknownUnit_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("move-7");

        var request = actor.MoveToUnit(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    #endregion

    #region Target

    [Test]
    public async Task SetTarget_ValidUnit_CompletesAndSetsCurrentTarget()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("target-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);

        var request = actor.SetTarget(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.CurrentTarget).IsNotNull();
        await Assert.That(actor.Character.CurrentTarget!.ObjId).IsEqualTo(npcObjId);
        // Target emits exactly one audit record before the Observe query.
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        await Assert.That(actor.AuditTrace[0].Action).IsEqualTo(ActorActionType.Target);
        await Assert.That(actor.AuditTrace[0].TargetId).IsEqualTo(npcObjId);
        // Observe is itself a tracked query — it appends a second record.
        await Assert.That(actor.Observe().CurrentTargetObjId).IsEqualTo(npcObjId);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Observe);
    }

    [Test]
    public async Task SetTarget_UnknownUnit_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("target-2");

        var request = actor.SetTarget(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    #endregion

    #region Cast

    [Test]
    public async Task Cast_LearnedSkill_CompletesThroughRealEnginePath()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-1");
        // Self-cast: the character is registered in its world, so the engine
        // resolves the caster as its own target (TargetType.Self).
        var request = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(SkillResult.Success);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Cast);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        // Audit TargetId = the cast TARGET objId (self here), not the skill id.
        await Assert.That(record.TargetId).IsEqualTo(actor.ActorId);
    }

    [Test]
    public async Task Cast_UnknownSkill_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-2");

        var request = actor.Cast(123_456, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("unknown skill")).IsTrue();
    }

    [Test]
    public async Task Cast_NotLearned_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-3");
        // Forget the seeded skill (fresh rig without AddSkill).
        actor.Character.Skills.Skills.Clear();

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not learned")).IsTrue();
    }

    [Test]
    public async Task Cast_UnknownTarget_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-4");

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, 999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("cast target not found")).IsTrue();
    }

    #endregion

    #region Audit + lifecycle machinery

    [Test]
    public async Task AuditRecord_CarriesFullTraceShape()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("audit-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1001);

        actor.SetTarget(npcObjId);
        actor.Stop();

        var record = actor.AuditTrace[0];
        // {trace_id, actor_id, action, target_id, requested_at, started_at,
        //  completed_at, result, state_changes}
        await Assert.That(record.TraceId).IsNotEqualTo(Guid.Empty);
        await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Target);
        await Assert.That(record.TargetId).IsEqualTo(npcObjId);
        await Assert.That(record.RequestedAtUtc <= record.StartedAtUtc).IsTrue();
        await Assert.That(record.StartedAtUtc <= record.CompletedAtUtc).IsTrue();
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Count >= 3).IsTrue(); // Requested→Accepted→Running→Completed
    }

    [Test]
    public async Task Trace_IsBounded_NewestLast()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("audit-2");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1002);

        for (var i = 0; i < 20; i++)
        {
            actor.SetTarget(npcObjId);
            actor.Stop();
        }

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(40); // 20 targets + 20 stops
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Stop);
        await Assert.That(actor.AuditTrace[^2].Action).IsEqualTo(ActorActionType.Target);
    }

    #endregion
}
