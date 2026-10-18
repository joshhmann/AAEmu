using System.Numerics;
using System.Reflection;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// A unit-relative quest leg (pursuit, return) is budgeted for the distance it
/// was issued for. T10 live runs 4 and 6: the flat 10 s budget walked ~45 m, so
/// every chase beyond that ended TimedOut/Navigation mid-walk.
/// </summary>
[NotInParallel]
public class QuestUnitLegBudgetTests
{
    [Test]
    public async Task UnitLegBudget_NearTarget_KeepsThePursuitBudget()
    {
        await Assert.That(QuestBehavior.UnitLegBudget(5f)).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task UnitLegBudget_BeyondOnePursuitLeg_CoversTheWholeChase()
    {
        // 70 m: the run-4 chase that died ~2 m short of a ~45 m reach, and more.
        var budget = QuestBehavior.UnitLegBudget(70f);
        await Assert.That(budget.TotalSeconds * 4.5).IsGreaterThan(70d);
        await Assert.That(budget).IsGreaterThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task UnitLegBudget_FarTarget_IsCappedAtTheCorpseApproachBudget()
    {
        await Assert.That(QuestBehavior.UnitLegBudget(5000f)).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    [Arguments("quest.objective-pursuit")]
    [Arguments("quest.return")]
    public async Task Dispatch_UnitLegToAFarNpc_IssuesTheDistanceSizedBudget(string goal)
    {
        // Real actor and world NPC 70 m away; the issued MoveToUnit request is
        // what must carry the longer budget, not just the helper.
        var (actor, session) = GameplayActorTestRig.CreateActor("leg-budget-" + goal.Replace('.', '-'));
        TravelIntentStore.ClearAll();
        var here = new Vector3(100f, 100f, 10f);
        actor.Character.Transform.Local.SetPosition(here);
        var npcObjId = GameplayActorTestRig.SpawnNpc(session);
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, here + new Vector3(70f, 0f, 0f));
        var proposal = new BotDecisionProposal(
            goal: goal,
            action: ActorActionType.Move,
            targetId: npcObjId,
            expectedPostcondition: new BotProposalPostcondition("test unit leg", _ => true),
            idempotencyKey: "leg-budget-" + goal,
            timeout: TimeSpan.FromSeconds(30),
            rationale: "far unit leg",
            policyVersion: "test");

        var dispatch = typeof(QuestBehavior).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Static)!;
        var request = (ActorRequest)dispatch.Invoke(null, [actor, proposal])!;

        await Assert.That(request.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(request.Timeout).IsEqualTo(QuestBehavior.UnitLegBudget(70f));
        await Assert.That(request.Timeout!.Value).IsGreaterThan(TimeSpan.FromSeconds(10));
        TravelIntentStore.ClearAll();
    }
}
