using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Quest-surface workstream — the TALK-CREDIT contract action on the
/// IGameplayActor surface: <see cref="IGameplayActor.Talk"/>, closing the
/// leveling loop's top missing-objective gap (QuestActObjTalk /
/// QuestActObjTalkNpcGroup).
///
/// Evidence chain under test:
///  - REAL packet path: the client's CSQuestTalkMadePacket (CSOffsets
///    0x0da) handler calls QuestManager.DoTalkMadeEvents(char, char,
///    npcObjId, questContextId, questCompId, questActId); Talk fires that
///    exact call once per active quest context whose template carries a
///    talk-family objective — the per-quest-dialog packets a real client
///    sends. Credit filtering is the ENGINE's own: OnTalkMade matches
///    NpcId, OnTalkNpcGroupMade matches _groupNpcs membership.
///  - Fail-closed refusals (PartyInvite/InteractWith precedent):
///    unresolvable npcObjId and out-of-interaction-range targets are
///    Rejected pre-flight; a talk producing NO observable quest delta
///    (no active talk objective credits the NPC) is Rejected instead of
///    reported as success.
///  - Audit record shape: every request emits its full lifecycle record.
///
/// Headless rig: synthetic quest templates (fixture range 90_7xx) seeded
/// additively into whatever QuestManager instance is established.
/// </summary>
[NotInParallel]
public class GameplayActorTalkTests
{
    [Test]
    public async Task Talk_CreditsActiveTalkObjective_CompletesQuestThroughRealPipeline()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-talk-1");
        GameplayActorTestRig.SeedQuestTalkObjective(GameplayActorTestRig.DiscoveryTalkQuestId,
            GameplayActorTestRig.DiscoveryTalkStartComponentId, GameplayActorTestRig.DiscoveryTalkProgressComponentId,
            offerNpcTemplateId: GameplayActorTestRig.DiscoveryNpcTemplateId,
            objectiveNpcTemplateId: GameplayActorTestRig.DiscoveryTalkNpcTemplateId);
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryTalkNpcTemplateId);

        var accept = actor.AcceptQuest(GameplayActorTestRig.DiscoveryTalkQuestId,
            QuestAcceptorType.Npc, GameplayActorTestRig.DiscoveryNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(
            GameplayActorTestRig.DiscoveryTalkQuestId)).IsTrue();

        var request = actor.Talk(npcObjId);

        // Completed with an observable-delta payload (InteractWith precedent).
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (TalkResult)request.Result!;
        await Assert.That(result.NpcTemplateId).IsEqualTo(GameplayActorTestRig.DiscoveryTalkNpcTemplateId);
        await Assert.That(result.ObservedChanges.Any(c => c.Contains(
            GameplayActorTestRig.DiscoveryTalkQuestId.ToString()))).IsTrue();

        // The single-objective quest advanced through the REAL step machine:
        // talk credit → Progress acts pass → no Ready step → completed+dropped.
        await Assert.That(actor.Character.Quests.HasQuestCompleted(
            GameplayActorTestRig.DiscoveryTalkQuestId)).IsTrue();

        // Full audit record shape.
        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Talk);
        await Assert.That(record.TargetId).IsEqualTo(npcObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Talk_TalkNpcGroupObjective_CreditsThroughGroupFanout()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-talk-2");
        GameplayActorTestRig.SeedQuestTalkObjective(GameplayActorTestRig.DiscoveryTalkGroupQuestId,
            GameplayActorTestRig.DiscoveryTalkGroupStartComponentId, GameplayActorTestRig.DiscoveryTalkGroupProgressComponentId,
            offerNpcTemplateId: GameplayActorTestRig.DiscoveryNpcTemplateId,
            objectiveNpcTemplateId: GameplayActorTestRig.DiscoveryTalkNpcTemplateId,
            npcGroupId: GameplayActorTestRig.DiscoveryTalkGroupNpcGroupId);
        GameplayActorTestRig.SeedNpcTalkGroup(GameplayActorTestRig.DiscoveryTalkGroupNpcGroupId,
            GameplayActorTestRig.DiscoveryTalkNpcTemplateId);
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryTalkNpcTemplateId);

        var accept = actor.AcceptQuest(GameplayActorTestRig.DiscoveryTalkGroupQuestId,
            QuestAcceptorType.Npc, GameplayActorTestRig.DiscoveryNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.Talk(npcObjId);

        // DoTalkMadeEvents' own group fan-out (_groupNpcs lookup) credits the
        // QuestActObjTalkNpcGroup act — the same path real packets ride.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(
            GameplayActorTestRig.DiscoveryTalkGroupQuestId)).IsTrue();
    }

    [Test]
    public async Task Talk_NoActiveTalkObjectiveForNpc_RejectedAsVoid()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-talk-3");
        // An NPC of an unrelated template — no seeded quest talks to it.
        var strangerObjId = session.SpawnNpc(90_798);

        var request = actor.Talk(strangerObjId);

        // Fail-closed: a void talk is refused, never reported as success.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("no active talk objective")).IsTrue();
    }

    [Test]
    public async Task Talk_OutOfRange_RejectedPreFlight()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-talk-4");
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryTalkNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new System.Numerics.Vector3(500, 500, 0));

        var request = actor.Talk(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("interaction range")).IsTrue();
    }

    [Test]
    public async Task Talk_UnresolvableNpcObjId_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("pb002-talk-5");

        var request = actor.Talk(0xDEAD);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("not found in world")).IsTrue();
    }
}
