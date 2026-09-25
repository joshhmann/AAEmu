using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// E2E-ONLY turn-in withhold seam (default-off): a Ready quest's turn-in stays
/// eligible and selectable through the whole leg, but dispatch is withheld —
/// nothing lands and the quest stays Ready/active. Default options preserve
/// the existing priority-30 turn-in behavior byte-identically.
/// Full decision-stack coverage through <see cref="QuestDecisionScenario.Run"/>
/// on the REAL headless actor — no fakes.
/// </summary>
[NotInParallel]
public class QuestTurnInWithholdTests
{
    private const uint QuestId = 91_401;
    private const uint StartComponentId = 91_402;
    private const uint ReadyComponentId = 91_403;
    private const uint OfferNpcTemplateId = 91_404;
    private const uint ReportNpcTemplateId = 91_405;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.SeedQuestDelivery(QuestId, StartComponentId, ReadyComponentId,
            OfferNpcTemplateId, ReportNpcTemplateId, level: 1);
    }

    private static (GameplayActor Actor, HeadlessSession Session) AcceptReadyQuest(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        var character = session.Character;
        character.Level = 1;
        character.Hp = character.MaxHp;
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, OfferNpcTemplateId, $"{name}-accept");
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");
        character.Quests.ActiveQuests[QuestId].Status = QuestStatus.Ready;
        return (actor, session);
    }

    [Test]
    public async Task Defaults_PreserveExistingTurnInBehavior()
    {
        var opts = new QuestDecisionScenario.QuestOptions();
        await Assert.That(opts.TurnInPriority).IsEqualTo(30);
        await Assert.That(opts.WithholdTurnIn).IsFalse();

        var (actor, session) = AcceptReadyQuest("withhold-default");
        var reporterObjId = GameplayActorTestRig.SpawnNpc(session, ReportNpcTemplateId);
        await Assert.That(reporterObjId).IsNotEqualTo(0u);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "withhold-default-1" });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.TurnInQuest);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(session.Character.Quests!.HasQuestCompleted(QuestId)).IsTrue();
    }

    [Test]
    public async Task WithholdTurnIn_SelectedProposalObservable_DispatchWithheld_QuestStaysReady()
    {
        var (actor, session) = AcceptReadyQuest("withhold-on");
        var character = session.Character;
        var reporterObjId = GameplayActorTestRig.SpawnNpc(session, ReportNpcTemplateId);
        await Assert.That(reporterObjId).IsNotEqualTo(0u);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "withhold-on-1", WithholdTurnIn = true });

        // Proposal observable: the selector still chose the turn-in.
        await Assert.That(result.WorkSelected).IsFalse();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.TurnInQuest);
        await Assert.That(result.FailStage).IsEqualTo("WITHHELD");
        await Assert.That(result.FailReason.Contains($"reporter={reporterObjId}")).IsTrue();
        // Dispatch withheld: nothing landed.
        await Assert.That(result.Request).IsNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.TurnInQuest
            or ActorActionType.TurnInDoodad or ActorActionType.AutoTurnIn)).IsFalse();
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(QuestId)).IsTrue();
        await Assert.That(character.Quests.ActiveQuests[QuestId].Status).IsEqualTo(QuestStatus.Ready);
        await Assert.That(character.Quests.HasQuestCompleted(QuestId)).IsFalse();
    }

    [Test]
    public async Task WithholdTurnIn_ProgressQuest_StillAdvances_NoTurnInProposed()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("withhold-progress");
        var character = session.Character;
        character.Level = 1;
        character.Hp = character.MaxHp;
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, OfferNpcTemplateId, "withhold-progress-accept");
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");
        // Delivery quests accept straight to Ready; force Progress to prove the
        // Ready gate still evaluates (production state machine untouched).
        character.Quests.ActiveQuests[QuestId].Status = QuestStatus.Progress;
        GameplayActorTestRig.SpawnNpc(session, ReportNpcTemplateId);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "withhold-progress-1", WithholdTurnIn = true });

        // Readiness still evaluated: a Progress quest proposes advance, never turn-in.
        await Assert.That(result.SelectedAction).IsNotEqualTo(ActorActionType.TurnInQuest);
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AdvanceQuest);
        await Assert.That(result.WorkSelected).IsTrue();
    }
}
