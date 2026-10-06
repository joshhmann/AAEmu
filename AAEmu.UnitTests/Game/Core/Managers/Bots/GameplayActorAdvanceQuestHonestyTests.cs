using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// AdvanceQuest MESSAGE-honesty contract (unattended lane journey finding).
///
/// The defect was never the terminal — it was the WORDING. The advance leg
/// discarded RunCurrentStep()'s bool and interpolated step/status AFTER the
/// call into "quest N advanced (step Progress, status Progress)", so an
/// unservable quest produced 36,543 success rows whose quest state was
/// byte-identical before and after. The row claimed an advancement that never
/// happened.
///
/// The TERMINAL is deliberately unchanged: Completed means "the step machine
/// ran to completion for this wake", not "the world advanced". A quest whose
/// objective cannot be credited still ran, and the scheduler/executor/scenario
/// callers depend on that stable contract. So these tests pin the MESSAGE:
///
///   1. A declined, state-preserving advance still COMPLETES the request
///      (terminal untouched) but its Detail must say "did not advance", must
///      name the ACTUAL step/status, and must NOT contain "advanced".
///   2. A real advance COMPLETES and its Detail carries the before→after
///      transition, not just a bare final state.
///   3. The not-active pre-flight refusal is unchanged: StateTransition,
///      "not active", never reaches Running, the step machine is not invoked.
///   4. One wake issues exactly ONE AdvanceQuest request — no retry, no
///      re-dispatch. The leg still RUNS (RunCurrentStep is invoked); this is a
///      reporting fix, not a behaviour-removal fix.
///
/// Pure unit level: real rig-seeded QuestManager templates, real
/// CharacterQuests.AddQuest, real quest.RunCurrentStep. No wall-clock, no
/// mocks. SQLite-only (copied compact.sqlite3); the rig's one MySQL call
/// (ContainerIdManager.Initialize) fails closed to a local counter, so this
/// class cannot reset a shared E2E database.
/// </summary>
[NotInParallel]
public class GameplayActorAdvanceQuestHonestyTests
{
    /// <summary>
    /// Synthesized quest (not canonical data): Start -> Progress with a
    /// monster-hunt objective nothing credits -> Ready, reported to
    /// <see cref="ReportNpcTemplateId"/>. The Progress step's RunComponents
    /// therefore returns false forever — the unservable shape the live run
    /// spun on (quest 251, rejects=[template=12]).
    /// </summary>
    private const uint StuckQuestId = 95_001;
    private const uint StuckStartComponentId = 95_002;
    private const uint StuckProgressComponentId = 95_003;
    private const uint StuckReadyComponentId = 95_004;
    private const uint OfferNpcTemplateId = 95_005;
    private const uint HuntPreyTemplateId = 95_006;
    private const uint ReportNpcTemplateId = 95_007;
    private const uint UnknownQuestId = 95_011;

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
    /// Accepts the stuck quest and drains Start -> Progress. The premise is
    /// ASSERTED, not assumed, so a fixture regression surfaces as this
    /// test's own failure rather than a false pass on the wrong step.
    /// </summary>
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

    [Test]
    public async Task AdvanceQuest_Decline_CompletesRequest_ButSaysItDidNotAdvance()
    {
        var (actor, quest) = AcceptIntoProgress("adv-honest-decline");

        var request = actor.AdvanceQuest(StuckQuestId);

        // REGRESSION: the terminal is Completed by contract ("the step machine
        // ran to completion for this wake"), so it must NOT flip to Rejected.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Failure).IsNull();

        // The MESSAGE is the fix: it states the non-advancement and names the
        // ACTUAL step/status...
        await Assert.That(request.Detail).Contains("did not advance");
        await Assert.That(request.Detail).Contains($"{QuestComponentKind.Progress}");
        await Assert.That(request.Detail).Contains($"{QuestStatus.Progress}");
        // ...and the old dishonest phrase is gone from the row entirely.
        await Assert.That(request.Detail).DoesNotContain("advanced (");
    }

    [Test]
    public async Task AdvanceQuest_Decline_LeavesQuestStateUnchanged()
    {
        var (actor, quest) = AcceptIntoProgress("adv-honest-nomutate");

        var declined = actor.AdvanceQuest(StuckQuestId);
        await Assert.That(declined.State).IsEqualTo(ActorLifecycleState.Completed);

        // The leg still RAN — the decline is the step machine's verdict, and
        // the actor reports it rather than mutating or skipping the attempt.
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Progress);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Progress);
    }

    [Test]
    public async Task AdvanceQuest_RealAdvance_Completes_AndReportsBeforeToAfterTransition()
    {
        var (actor, quest) = AcceptIntoProgress("adv-honest-success");
        var beforeStep = quest.Step;
        var beforeStatus = quest.Status;

        // Satisfy the hunt objective through the REAL engine credit event
        // (QuestActObjMonsterHunt.OnMonsterHunt -> AddObjective), then drain.
        actor.Character.Events.OnMonsterHunt(actor.Character, new OnMonsterHuntArgs
        {
            NpcId = HuntPreyTemplateId,
            Count = 1
        });

        var request = actor.AdvanceQuest(StuckQuestId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Failure).IsNull();
        // The message carries BOTH the pre-call and post-call state, so a
        // reader can see the transition rather than inferring one.
        await Assert.That(request.Detail).Contains($"{beforeStep}");
        await Assert.That(request.Detail).Contains($"{beforeStatus}");
        await Assert.That(request.Detail).Contains($"{QuestComponentKind.Ready}");
        await Assert.That(request.Detail).Contains($"{QuestStatus.Ready}");
        await Assert.That(request.Detail).Contains("->");
        await Assert.That(quest.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    [Test]
    public async Task AdvanceQuest_NotActive_StillRefusesStateTransition()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("adv-honest-inactive");

        var request = actor.AdvanceQuest(UnknownQuestId);

        // The ONLY refusal the actor still raises: the pre-flight gate. This is
        // a lifecycle refusal (the quest is not active), distinct from a step
        // machine decline, and it is unchanged by the message fix.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail).Contains("not active");
        await Assert.That(request.Detail).Contains("(cannot advance)");
        // Pre-flight refusal: the request never reached Running, so the step
        // machine was never invoked.
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task AdvanceQuest_Decline_IssuesExactlyOneRequest_NoRetry()
    {
        var (actor, _) = AcceptIntoProgress("adv-honest-single");

        var traceBefore = actor.AuditTrace.Count;
        var request = actor.AdvanceQuest(StuckQuestId);

        // ONE wake, ONE decision: exactly one audit record was appended — the
        // decline spawns no second attempt, no re-dispatch, no retry row.
        await Assert.That(actor.AuditTrace.Count - traceBefore).IsEqualTo(1);
        await Assert.That(actor.ActiveRequest).IsNull();

        var advanceRows = 0;
        foreach (var record in actor.AuditTrace)
            if (record.Action == ActorActionType.AdvanceQuest)
                advanceRows++;
        await Assert.That(advanceRows).IsEqualTo(1);

        // And that one row is this terminal, not a success plus a retry.
        var last = actor.AuditTrace[^1];
        await Assert.That(last.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(last.TraceId).IsEqualTo(request.TraceId);
    }
}