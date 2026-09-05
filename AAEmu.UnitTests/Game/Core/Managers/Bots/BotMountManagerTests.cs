using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class BotMountManagerTests
{
    [Test]
    public async Task EnsureMounted_SpawnsAndMountsSteed()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-bot-mount-1");
        var character = session.Character;

        await Assert.That(BotMountManager.IsMounted(character)).IsFalse();

        // Act: ensure mounted
        var mounted = BotMountManager.EnsureMounted(actor);

        await Assert.That(mounted).IsTrue();
        await Assert.That(BotMountManager.IsMounted(character)).IsTrue();

        var mate = session.World.MateManager.GetIsMounted(character.ObjId, out _);
        await Assert.That(mate).IsNotNull();
        await Assert.That(mate!.OwnerObjId).IsEqualTo(character.ObjId);
    }

    [Test]
    public async Task EnsureMounted_AlreadyMounted_IsIdempotent()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-bot-mount-2");
        var character = session.Character;

        var mounted1 = BotMountManager.EnsureMounted(actor);
        await Assert.That(mounted1).IsTrue();

        // Second call should succeed idempotently
        var mounted2 = BotMountManager.EnsureMounted(actor);
        await Assert.That(mounted2).IsTrue();
        await Assert.That(BotMountManager.IsMounted(character)).IsTrue();
    }

    [Test]
    public async Task EnsureDismounted_DismountsRider()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-bot-mount-3");
        var character = session.Character;

        BotMountManager.EnsureMounted(actor);
        await Assert.That(BotMountManager.IsMounted(character)).IsTrue();

        // Act: dismount
        var dismounted = BotMountManager.EnsureDismounted(actor);

        await Assert.That(dismounted).IsTrue();
        await Assert.That(BotMountManager.IsMounted(character)).IsFalse();
    }

    [Test]
    public async Task ApplyCharacterMove_SynchronizesMateTransformWhenMounted()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("test-bot-mount-4");
        var character = session.Character;
        var start = new Vector3(100f, 100f, 10f);
        character.SetPosition(start.X, start.Y, start.Z, 0, 0, 0);

        BotMountManager.EnsureMounted(actor);
        var mate = session.World.MateManager.GetIsMounted(character.ObjId, out _)!;

        // Drive a short move leg while mounted (10m)
        var destination = new Vector3(110f, 100f, 10f);
        var req = actor.MoveTo(destination, BotMountManager.MountedTravelSpeed);
        await Assert.That(req.State).IsEqualTo(ActorLifecycleState.Running);

        var guard = 0;
        while (!req.IsTerminal && guard++ < 100)
        {
            actor.Tick(TimeSpan.FromMilliseconds(100));
        }

        await Assert.That(req.State).IsNotEqualTo(ActorLifecycleState.Rejected);

        // Both rider and steed positions must synchronize to destination.
        // Arrival completes inside the actor's arrival radius (the eased
        // profile stops the leg as soon as flat distance <= 0.5 m instead
        // of landing exactly on the point like the legacy constant step).
        await Assert.That(req.Detail).IsEqualTo("arrived");
        await Assert.That(Math.Abs(character.Transform.World.Position.X - 110f) <= GameplayActor.ArrivalRadius).IsTrue();
        await Assert.That(Vector3.Distance(character.Transform.World.Position, destination)).IsLessThan(1.0f);
        await Assert.That(Vector3.Distance(mate.Transform.World.Position, destination)).IsLessThan(1.0f);
    }
}
