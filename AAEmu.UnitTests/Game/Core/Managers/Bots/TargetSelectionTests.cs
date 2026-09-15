using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Target-selection parity: SetTarget resolves a live world unit, assigns
/// CurrentTarget, and rejects unknown objIds without mutating state.
/// </summary>
[NotInParallel]
public class TargetSelectionTests
{
    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
    }

    [Test]
    public async Task SetTarget_LiveNpc_AssignsCurrentTarget()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("target-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session);

        var request = actor.SetTarget(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(npcObjId);
    }

    [Test]
    public async Task SetTarget_UnknownObjId_RejectedWithoutMutation()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("target-2");

        var request = actor.SetTarget(0xDEADu);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Target && r.Result == ActorLifecycleState.Rejected)).IsTrue();
    }

    [Test]
    public async Task SetTarget_Retarget_ReplacesCurrentTarget()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("qb-target-3");
        var first = GameplayActorTestRig.SpawnNpc(session, 1001);
        var second = GameplayActorTestRig.SpawnNpc(session, 1002);

        actor.SetTarget(first);
        var request = actor.SetTarget(second);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(second);
    }
}
