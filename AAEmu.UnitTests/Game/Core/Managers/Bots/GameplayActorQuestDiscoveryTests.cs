using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// PB-002 vertical slice — the quest-DISCOVERY perception primitive on the
/// IGameplayActor surface: <see cref="IGameplayActor.DiscoverQuests"/>,
/// answering "which quests does THIS nearby NPC/doodad offer ME right now"
/// through REAL engine paths only.
///
/// Evidence chain under test:
///  - Offer linkage: Start components carrying QuestActConAcceptNpc /
///    QuestActConAcceptDoodad acts (the data-driven rows the client's quest
///    markers come from — quest_components.npc_id is almost always empty).
///    The engine's own C2G entry (CSStartQuestContextPacket) dispatches on
///    exactly two world-target branches: npcObjId / doodadObjId.
///  - Availability filter: the REAL CharacterQuests.AddQuest pre-condition
///    chain — active duplicate, supply-item gate, every Start component's
///    unit_reqs via UnitRequirementsGameData.CanComponentRun (the engine's
///    level/race/chain gate), completed non-repeatable. Discovery must be
///    FAIL-CLOSED EQUAL to that accept path: everything surfaced here must
///    accept, everything the gate refuses stays invisible.
///  - PLAYER_MODE range discipline: the engine has NO server-side range
///    gate on CSStartQuestContextPacket, so the contract applies its own
///    Interact-range boundary (MaxQuestDiscoverRange).
///
/// Headless rig: synthetic quest templates in the fixture-id range seeded
/// additively into whatever QuestManager instance is established (never
/// replacing singletons); unit_reqs Level rows seeded into
/// UnitRequirementsGameData the same way.
/// </summary>
[NotInParallel]
public class GameplayActorQuestDiscoveryTests
{
    [Test]
    public async Task DiscoverQuests_NpcWithOffer_ReturnsOfferThatTheRealGateAccepts()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-discover-1");
        GameplayActorTestRig.SeedQuestOffer(GameplayActorTestRig.DiscoveryQuestId,
            GameplayActorTestRig.DiscoveryComponentId, GameplayActorTestRig.DiscoveryNpcTemplateId);
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryNpcTemplateId);

        var request = actor.DiscoverQuests(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (QuestDiscoveryResult)request.Result!;
        await Assert.That(result.TargetObjId).IsEqualTo(npcObjId);
        await Assert.That(result.AcceptorType).IsEqualTo(QuestAcceptorType.Npc);
        await Assert.That(result.AcceptorTemplateId).IsEqualTo(GameplayActorTestRig.DiscoveryNpcTemplateId);
        await Assert.That(result.Offerings.Count).IsEqualTo(1);
        var offering = result.Offerings[0];
        await Assert.That(offering.QuestId).IsEqualTo(GameplayActorTestRig.DiscoveryQuestId);
        await Assert.That(offering.Level).IsEqualTo((byte)10); // quest_contexts.LEVEL display value

        // Fail-closed equality proof #1: everything discovery surfaces is
        // accepted by the REAL engine gate (CharacterQuests.AddQuest).
        var accept = actor.AcceptQuest(GameplayActorTestRig.DiscoveryQuestId,
            QuestAcceptorType.Npc, GameplayActorTestRig.DiscoveryNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        // Full audit record shape (every action emits one) — the discovery
        // record is AuditTrace[0]; the accept leg appended its own.
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.DiscoverQuests);
        await Assert.That(record.TargetId).IsEqualTo(npcObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task DiscoverQuests_LevelGate_BelowMinLevel_QuestInvisible_AndRealGateRefuses()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-discover-2");
        GameplayActorTestRig.SeedQuestOffer(GameplayActorTestRig.DiscoveryLevelGatedQuestId,
            GameplayActorTestRig.DiscoveryLevelGatedComponentId, GameplayActorTestRig.DiscoveryGatedNpcTemplateId);
        GameplayActorTestRig.SeedQuestComponentLevelRequirement(
            GameplayActorTestRig.DiscoveryLevelGatedComponentId, minLevel: 20);
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryGatedNpcTemplateId);

        var request = actor.DiscoverQuests(npcObjId);

        // Level 1 bot: the unit_reqs Level gate hides the quest from
        // discovery…
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (QuestDiscoveryResult)request.Result!;
        await Assert.That(result.Offerings.Any(o => o.QuestId == GameplayActorTestRig.DiscoveryLevelGatedQuestId)).IsFalse();

        // …and the SAME gate makes the real accept path refuse it — the
        // discovery filter is exactly the engine's own precondition.
        var accept = actor.AcceptQuest(GameplayActorTestRig.DiscoveryLevelGatedQuestId,
            QuestAcceptorType.Npc, GameplayActorTestRig.DiscoveryGatedNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Rejected);
    }

    [Test]
    public async Task DiscoverQuests_LevelGate_AtMinLevel_QuestAppears()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-discover-3");
        GameplayActorTestRig.SeedQuestOffer(GameplayActorTestRig.DiscoveryLevelGatedQuestId,
            GameplayActorTestRig.DiscoveryLevelGatedComponentId, GameplayActorTestRig.DiscoveryGatedNpcTemplateId);
        GameplayActorTestRig.SeedQuestComponentLevelRequirement(
            GameplayActorTestRig.DiscoveryLevelGatedComponentId, minLevel: 20);
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryGatedNpcTemplateId);

        actor.Character.Level = 20; // rig gotcha: MaxHp derives from Level
        actor.Character.Hp = actor.Character.MaxHp;

        var request = actor.DiscoverQuests(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (QuestDiscoveryResult)request.Result!;
        await Assert.That(result.Offerings.Count).IsEqualTo(1);
        await Assert.That(result.Offerings[0].QuestId).IsEqualTo(GameplayActorTestRig.DiscoveryLevelGatedQuestId);
    }

    [Test]
    public async Task DiscoverQuests_NpcWithoutOffers_CompletesEmpty()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-discover-4");
        var npcObjId = session.SpawnNpc(90_799); // no seeded offers for this template

        var request = actor.DiscoverQuests(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (QuestDiscoveryResult)request.Result!;
        await Assert.That(result.Offerings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DiscoverQuests_UnknownWorldTarget_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("pb002-discover-5");

        var request = actor.DiscoverQuests(0xDEAD); // resolves to neither an NPC nor a doodad

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task DiscoverQuests_TargetOutOfRange_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-discover-6");
        GameplayActorTestRig.SeedQuestOffer(GameplayActorTestRig.DiscoveryQuestId,
            GameplayActorTestRig.DiscoveryComponentId, GameplayActorTestRig.DiscoveryNpcTemplateId);
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new System.Numerics.Vector3(500, 500, 0));

        var request = actor.DiscoverQuests(npcObjId);

        // PLAYER_MODE: beyond interaction range nothing may be surfaced —
        // refused outright instead of returning a filtered (leaky) list.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("interaction range")).IsTrue();
    }

    [Test]
    public async Task DiscoverQuests_AlreadyActiveQuest_NotRediscovered()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-discover-7");
        GameplayActorTestRig.SeedQuestOffer(GameplayActorTestRig.DiscoveryQuestId,
            GameplayActorTestRig.DiscoveryComponentId, GameplayActorTestRig.DiscoveryNpcTemplateId);
        var npcObjId = session.SpawnNpc(GameplayActorTestRig.DiscoveryNpcTemplateId);

        await Assert.That(actor.DiscoverQuests(npcObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        var accept = actor.AcceptQuest(GameplayActorTestRig.DiscoveryQuestId,
            QuestAcceptorType.Npc, GameplayActorTestRig.DiscoveryNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        // Fail-closed equality proof #2: once the quest is ACTIVE, the same
        // duplicate-active precondition AddQuest applies hides it again.
        var request = actor.DiscoverQuests(npcObjId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (QuestDiscoveryResult)request.Result!;
        await Assert.That(result.Offerings.Any(o => o.QuestId == GameplayActorTestRig.DiscoveryQuestId)).IsFalse();
    }

    [Test]
    public async Task DiscoverQuests_DoodadBoard_OffersWithDoodadAcceptor()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("pb002-discover-8");
        GameplayActorTestRig.SeedQuestOffer(GameplayActorTestRig.DiscoveryDoodadQuestId,
            GameplayActorTestRig.DiscoveryDoodadComponentId, GameplayActorTestRig.DiscoveryDoodadTemplateId,
            doodad: true);
        var doodadObjId = session.SpawnDoodad(GameplayActorTestRig.DiscoveryDoodadTemplateId);

        var request = actor.DiscoverQuests(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (QuestDiscoveryResult)request.Result!;
        await Assert.That(result.AcceptorType).IsEqualTo(QuestAcceptorType.Doodad);
        await Assert.That(result.AcceptorTemplateId).IsEqualTo(GameplayActorTestRig.DiscoveryDoodadTemplateId);
        await Assert.That(result.Offerings.Count).IsEqualTo(1);
        await Assert.That(result.Offerings[0].QuestId).IsEqualTo(GameplayActorTestRig.DiscoveryDoodadQuestId);

        // The returned acceptor triple feeds straight into the real gate.
        var accept = actor.AcceptQuest(GameplayActorTestRig.DiscoveryDoodadQuestId,
            QuestAcceptorType.Doodad, GameplayActorTestRig.DiscoveryDoodadTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
    }
}
