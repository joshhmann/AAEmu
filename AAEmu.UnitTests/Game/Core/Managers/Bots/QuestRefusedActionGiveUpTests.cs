using System.Reflection;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Quests.Director;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Populated-area run 3: ten bots retried a quest-261 item use the engine
/// refused ("UrkTargetNpc") about 3,400 times each, because a refused quest
/// action was recorded but never counted. The runtime now gives the quest up
/// after the SAME refusal repeats, through the ordinary give-up sink.
/// </summary>
[NotInParallel]
public class QuestRefusedActionGiveUpTests
{
    private const string Refusal = "item 8129 use refused by engine: UrkTargetNpc";

    private static ActorRequest Refused(uint quest = 261, string detail = Refusal)
    {
        // The engine accepts the use, then refuses it (the actor's ordinary
        // lifecycle: Requested -> Accepted -> Rejected).
        var request = new ActorRequest(ActorActionType.UseItem, 8129, null, 0, null, decisionQuestId: quest);
        request.Accept("use item");
        request.Reject(ActorFailureReason.RejectedAction, detail);
        return request;
    }

    [Test]
    public async Task Fixture_IsARealRejectedRequest()
    {
        await Assert.That(Refused().State).IsEqualTo(ActorLifecycleState.Rejected);
    }

    private static void Count(BotBehaviorRuntime runtime, IGameplayActor actor, ActorRequest request)
        => typeof(BotBehaviorRuntime).GetMethod("CountRefusal", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [actor, request, new QuestDecisionScenario.QuestOptions()]);

    [Test]
    public async Task SameRefusalFiveTimes_GivesTheQuestUpOnceWithTheRefusalAsReason()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("refused-giveup");
        var runtime = new BotBehaviorRuntime();
        var giveUps = new List<QuestGiveUp>();
        runtime.QuestGiveUpSink = (_, giveUp) => giveUps.Add(giveUp);

        for (var i = 0; i < 4; i++)
            Count(runtime, actor, Refused());
        await Assert.That(giveUps).IsEmpty();

        Count(runtime, actor, Refused());
        await Assert.That(giveUps).HasCount(1);
        await Assert.That(giveUps[0].QuestId).IsEqualTo(261u);
        await Assert.That(giveUps[0].Reason).Contains("ACTION-REFUSED");
        await Assert.That(giveUps[0].Reason).Contains("UrkTargetNpc");
    }

    [Test]
    public async Task DifferentRefusalsOrQuests_RestartTheCount()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("refused-reset");
        var runtime = new BotBehaviorRuntime();
        var giveUps = new List<QuestGiveUp>();
        runtime.QuestGiveUpSink = (_, giveUp) => giveUps.Add(giveUp);

        for (var i = 0; i < 4; i++)
            Count(runtime, actor, Refused());
        Count(runtime, actor, Refused(detail: "a different refusal"));
        for (var i = 0; i < 3; i++)
            Count(runtime, actor, Refused());
        Count(runtime, actor, Refused(quest: 266));
        await Assert.That(giveUps).IsEmpty();
    }

    [Test]
    public async Task RequestWithoutAQuest_IsNeverCounted()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("refused-noquest");
        var runtime = new BotBehaviorRuntime();
        var giveUps = new List<QuestGiveUp>();
        runtime.QuestGiveUpSink = (_, giveUp) => giveUps.Add(giveUp);

        for (var i = 0; i < 10; i++)
            Count(runtime, actor, Refused(quest: 0));
        await Assert.That(giveUps).IsEmpty();
    }
}
