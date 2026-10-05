using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// ACCEPTED vs APPLIED on the quest advance — the transport-vs-acceptance split.
///
/// <c>AdvanceQuest</c> completes when the step machine ran for the wake, which is
/// a fact about TRANSPORT. It is not a fact about the WORLD: a quest whose
/// objective cannot be credited still runs, still completes, and leaves its step
/// and status byte-identical. Both rows read <c>Completed</c>, so the terminal
/// alone cannot tell a landed advance from a declined one — which is what let an
/// unservable quest spin on emitting success rows forever.
///
/// The vocabulary pins the two facts apart: Accepted (dispatch confirmed) and
/// Applied (world state verified changed, read from the LIVE quest after the
/// call, never inferred from the return value). Fixture mirrors
/// GameplayActorAdvanceQuestHonestyTests — the same synthesized unservable quest
/// and the same real engine credit event.
/// </summary>
[NotInParallel]
public class QuestAdvanceOutcomeTests
{
    /// <summary>Synthesized quest (not canonical data): the unservable Progress shape (see the honesty suite).</summary>
    private const uint StuckQuestId = 95_101;
    private const uint StuckStartComponentId = 95_102;
    private const uint StuckProgressComponentId = 95_103;
    private const uint StuckReadyComponentId = 95_104;
    private const uint OfferNpcTemplateId = 95_105;
    private const uint HuntPreyTemplateId = 95_106;
    private const uint ReportNpcTemplateId = 95_107;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.SeedQuestHunt(StuckQuestId, StuckStartComponentId, StuckProgressComponentId,
            StuckReadyComponentId, OfferNpcTemplateId, HuntPreyTemplateId, ReportNpcTemplateId,
            huntCount: 1, level: 1);
    }

    /// <summary>
    /// THE PIN: a declined advance is ACCEPTED (the step machine ran) and NOT
    /// APPLIED (the world did not move). Both rows carry the identical Completed
    /// terminal, so the token — not the terminal — is what distinguishes them.
    /// </summary>
    [Test]
    public async Task DeclinedAdvance_IsAcceptedButNotApplied()
    {
        var (actor, _) = AcceptIntoProgress("adv-outcome-decline");

        var request = actor.AdvanceQuest(StuckQuestId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).Contains(QuestAdvanceOutcome.From(accepted: true, applied: false).Token);
        await Assert.That(request.Detail).Contains("not-applied");
        // The negative token must never read as a landed one to a substring scan.
        await Assert.That(request.Detail).DoesNotContain(" accepted applied");
    }

    /// <summary>
    /// A real advance is BOTH: accepted (it ran) and applied (the world state was
    /// verified changed). Applied is read off the live quest AFTER the call.
    /// </summary>
    [Test]
    public async Task RealAdvance_IsAcceptedAndApplied()
    {
        var (actor, quest) = AcceptIntoProgress("adv-outcome-success");

        // Credit the hunt objective through the REAL engine event, then drain.
        actor.Character.Events.OnMonsterHunt(actor.Character, new OnMonsterHuntArgs
        {
            NpcId = HuntPreyTemplateId,
            Count = 1
        });

        var request = actor.AdvanceQuest(StuckQuestId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Detail).Contains(QuestAdvanceOutcome.From(accepted: true, applied: true).Token);
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    /// <summary>
    /// The token vocabulary itself: two independent facts, never one verdict, and
    /// a declined row is greppable without ever containing the landed token.
    /// </summary>
    [Test]
    public async Task Token_NamesBothFactsIndependently()
    {
        await Assert.That(QuestAdvanceOutcome.From(accepted: true, applied: true).Token).IsEqualTo("accepted applied");
        await Assert.That(QuestAdvanceOutcome.From(accepted: true, applied: false).Token).IsEqualTo("accepted not-applied");
        // Not-applied is a real, expected outcome, not a defect.
        var declined = new QuestAdvanceOutcome(Accepted: true, Applied: false);
        await Assert.That(declined.Accepted).IsTrue();
        await Assert.That(declined.Applied).IsFalse();
    }

    // ------------------------------------------------------------- fixture

    /// <summary>Accepts the stuck quest and drains Start -> Progress (premise asserted, not assumed).</summary>
    private static (GameplayActor Actor, Quest Quest) AcceptIntoProgress(string name)
    {
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        var accept = actor.AcceptQuest(StuckQuestId, QuestAcceptorType.Npc, OfferNpcTemplateId);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");

        var quest = actor.Character.Quests.ActiveQuests[StuckQuestId];
        if (quest.Step != QuestComponentKind.Progress)
            throw new InvalidOperationException($"expected Progress, got {quest.Step}");
        return (actor, quest);
    }
}
