using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Doodad quest turn-in parity (capability-matrix gap #3): TurnInAtDoodad
/// drives the real DoReportEvents doodad branch; unresolvable doodads and
/// inactive quests refuse fail-closed.
/// </summary>
[NotInParallel]
public class DoodadTurnInTests
{
    private const uint QuestId = 90_760;
    private const uint StartComponentId = 90_761;
    private const uint ReadyComponentId = 90_762;
    private const uint OfferNpcTemplateId = 90_763;
    private const uint ReportDoodadGroupId = 90_765;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.SeedPlantSurface();
        GameplayActorTestRig.SeedQuestOffer(QuestId, StartComponentId, OfferNpcTemplateId, level: 1);
        GameplayActorTestRig.SeedQuestReportDoodad(QuestId, StartComponentId, ReadyComponentId,
            OfferNpcTemplateId, ReportDoodadGroupId, level: 1);
    }

    [Test]
    public async Task TurnInAtDoodad_ReadyQuest_CompletesThroughRealBranch()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("doodad-turnin-1");
        var character = session.Character;
        character.Level = 1;
        character.Hp = character.MaxHp;
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, OfferNpcTemplateId, "doodad-turnin-accept");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var doodadObjId = GameplayActorTestRig.SpawnGroupedDoodad(session, ReportDoodadGroupId);
        var advance = actor.AdvanceQuest(QuestId, "doodad-turnin-advance");
        await Assert.That(advance.State).IsEqualTo(ActorLifecycleState.Completed);

        var turnIn = actor.TurnInAtDoodad(QuestId, doodadObjId, -1, "doodad-turnin-1");

        await Assert.That(turnIn.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests!.HasQuestCompleted(QuestId)).IsTrue();
    }

    [Test]
    public async Task TurnInAtDoodad_InactiveQuest_RefusedStateTransition()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("doodad-turnin-2");
        var doodadObjId = GameplayActorTestRig.SpawnGroupedDoodad(session, ReportDoodadGroupId);

        var turnIn = actor.TurnInAtDoodad(QuestId, doodadObjId);

        await Assert.That(turnIn.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(turnIn.Failure).IsEqualTo(ActorFailureReason.StateTransition);
    }

    [Test]
    public async Task TurnInAtDoodad_UnresolvableDoodad_RejectedWithoutMutation()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("doodad-turnin-3");
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, OfferNpcTemplateId, "doodad-turnin-accept-3");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var turnIn = actor.TurnInAtDoodad(QuestId, 0xDEADu);

        await Assert.That(turnIn.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(turnIn.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.Character.Quests!.ActiveQuests.ContainsKey(QuestId)).IsTrue();
    }
}
