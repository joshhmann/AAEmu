using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// PlayerBotController adaptation evidence (M5 slice #8): the pilot
/// controller (real quest-engine driver) is exposed through the
/// IGameplayActor contract via <see cref="PlayerBotControllerAdapter"/>.
///
/// The adapter composes — the pilot controller is untouched. The contract
/// surface (Observe/Move/Stop/Target/Cast + lifecycle + audit) delegates to
/// the actor over the SAME Character the quest-drive methods use, so
/// behavior layers speak one vocabulary and every action lands on the actor
/// lifecycle. Quest-drive surface delegates unchanged to the engine paths.
/// </summary>
[NotInParallel]
public class PlayerBotControllerAdapterTests
{
    private static (PlayerBotControllerAdapter Adapter, PlayerBotController Controller, HeadlessSession Session) CreateAdaptedBot()
    {
        GameplayActorTestRig.Seed();
        var session = HeadlessSession.Create(0x41AD, "adapter-bot", 1);
        var character = session.Character;
        character.ObjId = GameplayActorTestRig.ActorObjId;
        session.World.AddObject(character);
        character.Skills = new CharacterSkills(character);
        character.Actability = new CharacterActability(character);
        character.Skills.AddSkill(new SkillTemplate { Id = GameplayActorTestRig.TestSkillId }, 1, false);
        var controller = new PlayerBotController(character);
        return (new PlayerBotControllerAdapter(controller), controller, session);
    }

    [Test]
    public async Task Adapter_SharesCharacter_WithPilotController()
    {
        var (adapter, controller, _) = CreateAdaptedBot();

        await Assert.That(ReferenceEquals(controller.Character, adapter.Character)).IsTrue();
        await Assert.That(adapter.ActorId).IsEqualTo(controller.Character.ObjId);
    }

    [Test]
    public async Task Adapter_ExposesContractSurface_MoveCompletesThroughActor()
    {
        var (adapter, _, _) = CreateAdaptedBot();
        adapter.Character.Transform.Local.SetPosition(new Vector3(0, 0, 0));

        var request = adapter.MoveTo(new Vector3(4, 0, 0), speed: 2f);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        var guard = 0;
        while (request.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            adapter.Tick(TimeSpan.FromSeconds(1));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(Math.Abs(adapter.Character.Transform.World.Position.X - 4f) <= 0.001f).IsTrue();
        // Audit flowed through the actor surface.
        await Assert.That(adapter.AuditTrace.Any(
            r => r.Action == ActorActionType.Move && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task Adapter_ExposesContractSurface_TargetAndCastDelegate()
    {
        var (adapter, _, session) = CreateAdaptedBot();
        var npcObjId = session.SpawnNpc(1005);

        var targetReq = adapter.SetTarget(npcObjId);
        await Assert.That(targetReq.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(adapter.Character.CurrentTarget?.ObjId).IsEqualTo(npcObjId);

        var castReq = adapter.Cast(GameplayActorTestRig.TestSkillId, GameplayActorTestRig.ActorObjId);
        await Assert.That(castReq.State).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Adapter_QuestSurface_DelegatesToEnginePaths()
    {
        var (adapter, controller, _) = CreateAdaptedBot();

        // Unknown quest: the real engine gate refuses (no crash, no fake state).
        var accepted = adapter.TryAcceptQuest(9_999_999, QuestAcceptorType.Npc, 0);

        await Assert.That(accepted).IsFalse();
        await Assert.That(adapter.IsActive(9_999_999)).IsFalse();
        await Assert.That(adapter.HasCompleted(9_999_999)).IsFalse();
        await Assert.That(ReferenceEquals(controller, adapter.Controller)).IsTrue();
    }

    [Test]
    public async Task Adapter_Stop_InterruptsActiveMove_ThroughContract()
    {
        var (adapter, _, _) = CreateAdaptedBot();
        adapter.Character.Transform.Local.SetPosition(new Vector3(0, 0, 0));

        var move = adapter.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        var stop = adapter.Stop();

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(stop.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(adapter.ActiveRequest).IsNull();
    }
}
