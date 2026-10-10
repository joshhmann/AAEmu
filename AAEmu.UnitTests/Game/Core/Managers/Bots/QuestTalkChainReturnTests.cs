using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using QuestPattern = AAEmu.Game.Models.Game.Quests.Director.QuestPattern;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Talk-chain return gate (Leg-1 fix): a talk-only quest whose reporter is
/// unspawned must still walk (travel fallback) and land its turn-in once the
/// reporter materializes. Before the fix the TalkChain plan carried no Return
/// leg, so a Ready talk-only quest with an unspawned reporter could never be
/// walked to or dialogued — TurnInEmit withdrew every wake.
/// </summary>
[NotInParallel]
public class QuestTalkChainReturnTests
{
    // Fixture block 94_05x: free repo-wide and absent from canonical
    // quest_contexts / quest_components / npcs.
    private const uint QuestId = 94_050;
    private const uint StartComponentId = 94_051;
    private const uint ProgressComponentId = 94_052;
    private const uint ReadyComponentId = 94_053;
    private const uint OfferNpc = 94_054;
    private const uint TalkNpc = 94_055;
    private const uint ReporterNpc = 94_056;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        GameplayActorTestRig.SeedQuestTalkChain(QuestId, StartComponentId, ProgressComponentId,
            ReadyComponentId, OfferNpc, TalkNpc, ReporterNpc, level: 1);
    }

    [Test]
    public async Task TalkChainPlan_CarriesReturn_AheadOfTalkAndTurnIn()
    {
        var fixture = QuestFixtureRow.FromQuestData(QuestId);
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.TalkChain);
        await Assert.That(fixture.TalkNpcTemplate).IsEqualTo(TalkNpc);
        await Assert.That(fixture.ReporterTemplate).IsEqualTo(ReporterNpc);

        var plan = QuestDirector.Plan(QuestId, fixture);
        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.Return, QuestLegId.Talk, QuestLegId.TurnIn
        });
    }

    [Test]
    public async Task TalkOnlyQuest_AcceptTalkReady_TurnInLands()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("talkchain-return");
        var character = session.Character;
        character.Level = 1;
        character.Hp = character.MaxHp;

        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, OfferNpc, "talkchain-accept");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        // Spawn the objective npc inside the Talk engine gate alongside the actor.
        var talkObjId = session.SpawnNpc(TalkNpc);
        JoinRegion(session, actor, talkObjId, new Vector3(3, 0, 0));

        // Wake 1: settle (Stop) then Wake 2: Talk — the stop-before-talk discipline.
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talkchain-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talkchain-2" });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Talk);
        await Assert.That(actor.Character.Quests.ActiveQuests[QuestId].Status).IsEqualTo(QuestStatus.Ready);

        // The reporter is unspawned: no leg may dispatch, and the Return leg
        // must name the loss so the travel fallback owns the walk.
        var unspawned = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talkchain-3" });
        await Assert.That(unspawned.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(unspawned.SelectedAction == ActorActionType.Stop).IsFalse();
        await Assert.That(unspawned.SelectedAction == ActorActionType.InteractNpc).IsFalse();
        await Assert.That(unspawned.SelectedAction == ActorActionType.TurnInQuest).IsFalse();
        var returnDetail = unspawned.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail;
        await Assert.That(returnDetail).Contains("reason=reporter-lost");

        // The reporter materializes in range: Stop, then InteractNpc dialogue
        // sets CurrentTarget, then the converged TurnInQuest lands.
        var reporterObjId = session.SpawnNpc(ReporterNpc);
        JoinRegion(session, actor, reporterObjId, new Vector3(5, 0, 0));

        var stop = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talkchain-4" });
        await Assert.That(stop.SelectedAction).IsEqualTo(ActorActionType.Stop);

        var dialogue = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talkchain-5" });
        await Assert.That(dialogue.SelectedAction).IsEqualTo(ActorActionType.InteractNpc);
        await Assert.That(dialogue.Request!.TargetId).IsEqualTo(reporterObjId);

        var turnIn = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talkchain-6" });
        await Assert.That(turnIn.SelectedAction).IsEqualTo(ActorActionType.TurnInQuest);
        await Assert.That(turnIn.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(QuestId)).IsTrue();
    }

    private static void JoinRegion(HeadlessSession session, GameplayActor actor, uint npcObjId, Vector3 offset)
    {
        var npc = session.World.GetNpc(npcObjId)!;
        var pos = actor.Character.Transform.World.Position + offset;
        npc.Transform.Local.SetPosition(pos);
        var npcRegion = session.World.GetRegionByPos(pos);
        if (npcRegion != null)
        {
            npcRegion.AddObject(npc);
            npc.Region = npcRegion;
        }
        var actorRegion = session.World.GetRegionByPos(actor.Character.Transform.World.Position);
        if (actorRegion != null)
        {
            actorRegion.AddObject(actor.Character);
            actor.Character.Region = actorRegion;
        }
    }
}
