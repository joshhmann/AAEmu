using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;

using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Models;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// PB-001 tests for the public routed-navigation contract.  GeoData-backed
/// waypoint planning is covered by BaiNavigationRigTests; these tests cover
/// the actor lifecycle and the direct-leg behavior available in the headless
/// actor rig.
/// </summary>
[NotInParallel]
public class GameplayActorNavigateTests
{
    [Test]
    public async Task Navigate_AlreadyAtDestination_CompletesImmediately()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("navigate-at-destination");
        var destination = new Vector3(100, 200, 30);
        GameplayActorTestRig.SetPosition(actor, destination);

        var request = actor.NavigateTo(destination, speed: 5f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("already at destination");
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(request.StateChanges).Contains("Accepted (navigate)");
        await Assert.That(request.StateChanges.Any(change => change.StartsWith("Running"))).IsTrue();
        await Assert.That(request.StateChanges.Any(change => change.StartsWith("Completed"))).IsTrue();
    }

    [Test]
    public async Task Navigate_NonFiniteDestination_RejectsWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("navigate-invalid-destination");

        var request = actor.NavigateTo(new Vector3(float.NaN, 0, 0), speed: 5f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task Navigate_NonPositiveSpeed_RejectsWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("navigate-invalid-speed");

        var request = actor.NavigateTo(new Vector3(50, 50, 0), speed: -1f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task Navigate_WithoutGeoData_UsesDirectLegAndCompletes()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("navigate-direct-fallback");
        GameplayActorTestRig.SetPosition(actor, Vector3.Zero);
        session.World.Template.GeoData = null;
        actor.BroadcastMovement = false;

        var destination = new Vector3(10, 0, 0);
        var request = actor.NavigateTo(destination, speed: 2f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(ReferenceEquals(request, actor.ActiveRequest)).IsTrue();

        var guard = 0;
        while (request.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("arrived");
        await Assert.That(Vector3.Distance(actor.Character.Transform.World.Position, destination)).IsLessThanOrEqualTo(0.001f);
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(request.StateChanges.Any(change => change.StartsWith("Running"))).IsTrue();
        await Assert.That(request.StateChanges.Any(change => change.StartsWith("Completed"))).IsTrue();
    }

    [Test]
    public async Task Navigate_StopInterruptsNavigationAndCleansUp()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("navigate-stop");
        GameplayActorTestRig.SetPosition(actor, Vector3.Zero);
        actor.BroadcastMovement = false;

        var request = actor.NavigateTo(new Vector3(500, 0, 0), speed: 5f);
        actor.Tick(TimeSpan.FromSeconds(1));
        var positionAtStop = actor.Character.Transform.World.Position;

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(positionAtStop.X).IsGreaterThan(0f);

        var stopRequest = actor.Stop();
        actor.Tick(TimeSpan.FromSeconds(10));

        await Assert.That(stopRequest.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(stopRequest.Detail).IsEqualTo("stopped");
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(request.Detail).IsEqualTo("stop requested");
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(positionAtStop);
    }

    [Test]
    public async Task NavigateToUnit_TargetNotFound_RejectsWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("nav-unit-not-found");

        var request = actor.NavigateToUnit(99999, speed: 5f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).IsEqualTo("RejectedAction: target unit not found");
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task NavigateToUnit_AlreadyAtUnit_CompletesImmediately()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("nav-unit-already-there");
        var targetPosition = new Vector3(25, 30, 10);
        var npcObjId = GameplayActorTestRig.SpawnNpc(session);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Transform.World.Position = targetPosition;
        GameplayActorTestRig.SetPosition(actor, targetPosition);

        var request = actor.NavigateToUnit(npcObjId, speed: 5f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("already at destination");
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task NavigateToUnit_WithoutGeoData_NavigatesToUnitPositionAndCompletes()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("nav-unit-direct");
        GameplayActorTestRig.SetPosition(actor, Vector3.Zero);
        session.World.Template.GeoData = null;
        actor.BroadcastMovement = false;

        var targetPosition = new Vector3(10, 0, 0);
        var npcObjId = GameplayActorTestRig.SpawnNpc(session);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Transform.World.Position = targetPosition;

        var request = actor.NavigateToUnit(npcObjId, speed: 2f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(ReferenceEquals(request, actor.ActiveRequest)).IsTrue();

        var guard = 0;
        while (request.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).IsEqualTo("arrived");
        await Assert.That(Vector3.Distance(actor.Character.Transform.World.Position, targetPosition)).IsLessThanOrEqualTo(0.001f);
        await Assert.That(actor.ActiveRequest).IsNull();
    }
}
