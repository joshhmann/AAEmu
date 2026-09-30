using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestPattern = AAEmu.Game.Models.Game.Quests.Director.QuestPattern;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;
using QuestAcceptorType = AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Supplied-gather (4424 Deliver Organic Seeds): the quest BRAIN's fifth
/// objective shape, on the REAL headless actor through
/// <see cref="QuestDecisionScenario.Run"/>.
///
///   - the 4424 fixture row derives Deliver off canonical quest data: the
///     Progress gather act (2104, basil 24376 x1) matches the quest's OWN
///     Supply act (2829, basil 24376 x1) — the step machine grants the item
///     on accept, so the credit is the legitimate supply receipt (no kill,
///     no draw, no GrantItem injection);
///   - the basil resolves NO npc loot pack and NO doodad loot func (the
///     doodad-source MISS is the finding — both inversions return 0), so the
///     shape classifies Deliver, never KillX (which would fail UNPROVEN-SOURCE)
///     and never GatherDoodad (which needs a granting doodad);
///   - the plan carries Advance/Deliver/TurnIn and the catalog maps the shape
///     to its five-verb set (MoveTo/Stop/Observe/AcceptQuest/TurnInQuest — a
///     strict subset of the G9a gather set, all registry-green, no Interact);
///   - the Deliver monitor never dispatches: accept at 9789 → advance drains
///     Start/Supply into Progress (the basil lands through the real step
///     machine) → advance credits Ready → travel/report to the SPLIT 10857
///     auctioneer (first split in the chain) through the ordinary TurnIn leg.
/// </summary>
[NotInParallel]
public class QuestDeliverSuppliedTests
{
    private const uint Quest4424 = 4424;
    private const uint Basil24376 = 24376;
    private const uint GatherAct26227 = 26227;
    private const uint SupplyAct26226 = 26226;
    private const int GatherNeed = 1;
    private const uint AcceptNpc9789 = 9789;
    private const uint ReportNpc10857 = 10857;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        QuestBehavior.ClearGiveUpMemory();
        GameplayActorTestRig.RegisterPlainItemTemplate(Basil24376);
        QuestFixtureRow.InvalidateAll();
    }

    // ------------------------------------------------------------ row + plan

    [Test]
    public async Task Row4424_DerivesDeliverShape_OffCanonicalData()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4424);

        await Assert.That(fixture.QuestId).IsEqualTo(Quest4424);
        await Assert.That(fixture.GatherActId).IsEqualTo(GatherAct26227);
        await Assert.That(fixture.PreyItem).IsEqualTo(Basil24376);
        await Assert.That(fixture.Need).IsEqualTo(GatherNeed);
        await Assert.That(fixture.SupplyActId).IsEqualTo(SupplyAct26226);
        await Assert.That(fixture.SupplyItem).IsEqualTo(Basil24376);
        await Assert.That(fixture.SupplyCount).IsEqualTo(1);
        // The doodad-source MISS: no doodad grants the basil, so the shape
        // cannot be GatherDoodad — and no npc pack carries it either.
        await Assert.That(fixture.GatherDoodadTemplate).IsEqualTo(0u);
        await Assert.That(fixture.PreyTemplate).IsEqualTo(0u);
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.Deliver);
        await Assert.That(fixture.ObjectiveActType).IsEqualTo(nameof(QuestActObjItemGather));
        // First split in the chain: acceptor 9789, reporter 10857.
        await Assert.That(fixture.ReporterTemplate).IsEqualTo(ReportNpc10857);
    }

    [Test]
    public async Task Row4424_DoodadSourceMisses_BasilGrantedByNoDoodad()
    {
        // Data finding, pinned: the basil (24376) resolves no doodad loot
        // func and no npc loot pack — the supply grant is the ONLY source.
        var fixture = QuestFixtureRow.FromQuestData(Quest4424);
        await Assert.That(fixture.GatherDoodadTemplate).IsEqualTo(0u);
        await Assert.That(fixture.GatherUseSkill).IsEqualTo(0u);
        await Assert.That(fixture.PreyTemplate).IsEqualTo(0u);
        await Assert.That(fixture.PreyPack).IsEqualTo(0u);
    }

    [Test]
    public async Task Plan4424_ProductionRegistry_BuildsDeliverPlan()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4424);

        // Every verb the deliver shape needs resolves green — the subset of
        // the G9a gather set, so no new proof was needed.
        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.Deliver))
        {
            var gate = VerifiedVerbRegistry.Instance.Resolve(verbKey);
            await Assert.That(gate.IsGreen).IsTrue();
            await Assert.That(gate.GateId.Length).IsGreaterThan(0);
            await Assert.That(gate.EvidencePath.Length).IsGreaterThan(0);
        }

        var plan = QuestDirector.Plan(Quest4424, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.Deliver);
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.Deliver, QuestLegId.TurnIn
        });
        await Assert.That(plan.Leg(QuestLegId.Deliver)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.Gather)).IsNull();
        await Assert.That(plan.Leg(QuestLegId.Interact)).IsNull();
        await Assert.That(plan.Leg(QuestLegId.Combat)).IsNull();
        await Assert.That(plan.Leg(QuestLegId.UseItem)).IsNull();
    }

    [Test]
    public async Task PatternCatalog_Deliver_MapsFiveVerbs_AllRegistryGreen_NoInteract()
    {
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.Deliver)).IsEquivalentTo(new[]
        {
            "MoveTo", "Stop", "Observe", "AcceptQuest", "TurnInQuest"
        });
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.Deliver)).DoesNotContain("Interact");

        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.Deliver))
        {
            var gate = VerifiedVerbRegistry.Instance.Resolve(verbKey);
            await Assert.That(gate.IsGreen).IsTrue();
            await Assert.That(gate.GateId.Length).IsGreaterThan(0);
            await Assert.That(gate.EvidencePath.Length).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task Plan_DeliverItemMismatch_FailsUnprovenSource()
    {
        // A deliver row whose gather item is NOT the supply grant has no
        // legitimate credit path — fail closed naming the act and the item.
        var fixture = new QuestFixtureRow(Quest4424, GatherAct26227, Basil24376, GatherNeed, 0, 0, ReportNpc10857, 0)
        {
            ObjectivePattern = QuestPattern.Deliver,
            ObjectiveActType = nameof(QuestActObjItemGather),
            SupplyActId = SupplyAct26226,
            SupplyItem = 99998,
            SupplyCount = 1
        };

        var plan = QuestDirector.Plan(Quest4424, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo($"HARNESS/UNPROVEN-SOURCE quest={Quest4424} act={GatherAct26227} item={Basil24376}");
        await Assert.That(plan.Legs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PriorityLayout_DeliverAboveInteract_BelowTurnIn()
    {
        var opts = new QuestDecisionScenario.QuestOptions();
        await Assert.That(opts.ObjectiveDeliverPriority).IsEqualTo(29);
        await Assert.That(opts.ObjectiveDeliverPriority > opts.AdvancePriority).IsTrue();
        await Assert.That(opts.ObjectiveDeliverPriority < opts.TurnInPriority).IsTrue();
        await Assert.That(opts.ObjectiveDeliverPriority).IsNotEqualTo(opts.ObjectiveInteractPriority);
        await Assert.That(opts.ObjectiveDeliverPriority).IsNotEqualTo(opts.ObjectiveGatherPriority);
        await Assert.That(opts.ObjectiveDeliverPriority).IsNotEqualTo(opts.ObjectiveUseItemPriority);
        await Assert.That(opts.ObjectiveDeliverPriority).IsNotEqualTo(opts.ObjectivePursuitPriority);
        await Assert.That(opts.ObjectiveDeliverPriority).IsNotEqualTo(opts.ObjectiveCombatPriority);
        await Assert.That(opts.ObjectiveDeliverPriority).IsNotEqualTo(opts.ObjectiveLootPriority);
        await Assert.That(opts.ObjectiveDeliverPriority).IsNotEqualTo(opts.ObjectiveReturnPriority);
    }

    // ------------------------------------------------------------ leg dispatch
    //
    // The Deliver monitor never dispatches, so these wakes run through
    // QuestBehavior.Run with the production plan — proving the leg body (the
    // named supply-credit diag), the Advance drain (the real step machine
    // granting the basil), and the split turn-in (Ready → 10857, never 9789).

    [Test]
    public async Task AcceptAt9789_AdvanceDrainsSupply_BasilLandsThroughStepMachine()
    {
        // Accept auto-drains Start/Supply through the real step machine (the
        // actor's own accept path): the supply receipt (basil 24376 x1) lands
        // in the bag with no injection.
        var (actor, _) = CreateDeliveryActor("deliver-supply");
        var character = actor.Character;

        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(Quest4424)).IsTrue();
        var staged = character.Quests.ActiveQuests[Quest4424];
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Progress);
        await Assert.That(character.Inventory.GetItemsCount(Basil24376)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task SupplyReceipt_CreditsObjective_AdvanceReachesReady()
    {
        var (actor, _) = CreateDeliveryActor("deliver-credit");
        var character = actor.Character;
        var staged = character.Quests!.ActiveQuests[Quest4424]!;
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Progress);

        // The engine's own objective counter credits off the supply receipt.
        // ActId is the engine quest_acts row id (26227); DetailId is the
        // detail-table id (2104) — resolve by DetailId like the engine does.
        var gather = QuestManager.Instance.GetTemplate(Quest4424)!
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .First(a => a.DetailId == 2104);
        await Assert.That(gather.GetObjective(staged)).IsGreaterThanOrEqualTo(1);

        // One more real advance carries Progress → Ready (no kill, no draw).
        var advance = actor.AdvanceQuest(Quest4424);
        await Assert.That(advance.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Ready);
    }

    [Test]
    public async Task DeliverMonitor_NamesSupplyCredit_NeverDispatches()
    {
        var (actor, _) = CreateDeliveryActor("deliver-monitor");
        var character = actor.Character;
        var staged = character.Quests!.ActiveQuests[Quest4424]!;
        // Accept auto-drains to Progress AND the supply receipt already
        // credits the objective — one real advance carries Progress → Ready.
        // Roll back to Progress so the monitor enters on an OPEN objective.
        actor.AdvanceQuest(Quest4424);
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Ready);
        staged.Status = QuestStatus.Progress;
        staged.Step = QuestComponentKind.Progress;
        staged.Objectives = [0, 0, 0, 0, 0];

        // The monitor enters (objective open, quest Progress) but emits no
        // proposal — Advance wins the wake instead.
        var fixture = QuestFixtureRow.FromQuestData(Quest4424);
        var plan = QuestDirector.Plan(Quest4424, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production deliver plan failed: {plan.FailReason}");
        var result = QuestBehavior.Run(
            actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "deliver-monitor-1" },
            BotObservedContext.Capture(actor),
            [plan],
            (_, _) => []);

        var evidence = result.LegEvidence.Single(e => e.Leg == QuestLegId.Deliver);
        await Assert.That(evidence.Entered).IsTrue();
        await Assert.That(evidence.Emitted).IsFalse();
        await Assert.That(evidence.Detail).Contains("dispatch=monitor:reason=supply-credit");
        await Assert.That(evidence.Detail).Contains($"item={Basil24376}");
        await Assert.That(evidence.Detail).Contains($"supply={Basil24376}");
        await Assert.That(evidence.Detail).Contains("split=accept-9789-report-10857");
        // The monitor dispatched nothing: no Move/Stop/Interact audit rows.
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Move
            or ActorActionType.Stop or ActorActionType.Interact or ActorActionType.InteractWith)).IsFalse();
    }

    [Test]
    public async Task SupplyStageMonitor_HitsOnCreditedObjective_NeverDispatches()
    {
        // The Supply-stage observable: straight after accept the supply receipt
        // already credited the objective (bag 0->1 via the step machine) while
        // the quest still rests at Progress — the monitor fires its observation
        // hit (dispatch=monitor) there instead of withdrawing, so the lane sees
        // the delivery before the single advance drains Progress → Ready.
        var (actor, _) = CreateDeliveryActor("deliver-supply-hit");
        var character = actor.Character;
        var staged = character.Quests!.ActiveQuests[Quest4424]!;
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Progress);
        var gather = QuestManager.Instance.GetTemplate(Quest4424)!
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .First(a => a.DetailId == 2104);
        await Assert.That(gather.GetObjective(staged)).IsGreaterThanOrEqualTo(1);

        var fixture = QuestFixtureRow.FromQuestData(Quest4424);
        var plan = QuestDirector.Plan(Quest4424, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production deliver plan failed: {plan.FailReason}");
        var result = QuestBehavior.Run(
            actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "deliver-supply-hit-1" },
            BotObservedContext.Capture(actor),
            [plan],
            (_, _) => []);

        var evidence = result.LegEvidence.Single(e => e.Leg == QuestLegId.Deliver);
        await Assert.That(evidence.Entered).IsTrue();
        await Assert.That(evidence.Emitted).IsFalse();
        await Assert.That(evidence.Detail).Contains("dispatch=monitor:reason=objective-credited");
        await Assert.That(evidence.Detail).Contains($"item={Basil24376}");
        await Assert.That(evidence.Detail).Contains($"supply={Basil24376}");
        await Assert.That(evidence.Detail).Contains("split=accept-9789-report-10857");
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Move
            or ActorActionType.Stop or ActorActionType.Interact or ActorActionType.InteractWith)).IsFalse();
    }

    [Test]
    public async Task UnrelatedQuest_NeverFiresDeliverMonitor()
    {
        // Fail-closed: a quest whose gather item is NOT the supply grant never
        // fires the supply monitor — the emit withdraws naming unproven-source
        // instead of observing a delivery that is not there.
        GameplayActorTestRig.RegisterPlainItemTemplate(99998);
        var fixture = new QuestFixtureRow(Quest4424, GatherAct26227, Basil24376, GatherNeed, 0, 0, ReportNpc10857, 0)
        {
            ObjectivePattern = QuestPattern.Deliver,
            ObjectiveActType = nameof(QuestActObjItemGather),
            SupplyActId = SupplyAct26226,
            SupplyItem = 99998,
            SupplyCount = 1
        };
        var (actor, _) = CreateDeliveryActor("deliver-unrelated");
        var character = actor.Character;
        var staged = character.Quests!.ActiveQuests[Quest4424]!;
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Progress);

        var context = BotObservedContext.Capture(actor);
        var legContext = new QuestLegContext(actor, new QuestDecisionScenario.QuestOptions(), Quest4424, fixture, null)
        {
            Observation = context
        };
        var reason = QuestBehavior.DeliverEnter(legContext, new QuestLegWake());
        var diag = "";
        var hpBefore = -1;
        _ = QuestBehavior.DeliverEmit(legContext, ref diag, ref hpBefore);
        await Assert.That(reason).IsNull();
        await Assert.That(diag).Contains("dispatch=withdrawn:reason=unproven-source");
        await Assert.That(diag).DoesNotContain("dispatch=monitor");
    }

    [Test]
    public async Task ReadyReportsToSplit10857_NeverThe9789Giver()
    {
        var (actor, session) = CreateDeliveryActor("deliver-split");
        var character = actor.Character;
        var staged = character.Quests!.ActiveQuests[Quest4424]!;
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Progress);
        actor.AdvanceQuest(Quest4424);
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Ready);

        // Both NPCs live: the giver 9789 at one pose, the split reporter
        // 10857 at another.
        var giverObjId = session.SpawnNpc(AcceptNpc9789);
        GameplayActorTestRig.SetNpcPosition(session, giverObjId, new Vector3(2, 0, 0));
        var reporterObjId = session.SpawnNpc(ReportNpc10857);
        GameplayActorTestRig.SetNpcPosition(session, reporterObjId, new Vector3(10, 0, 0));
        GameplayActorTestRig.SetPosition(actor, new Vector3(10, 0, 0));
        var region = session.World.GetRegionByPos(new Vector3(10, 0, 0));
        if (region != null)
        {
            region.AddObject(actor.Character);
            actor.Character.Region = region;
        }
        var reporter = session.World.GetNpc(reporterObjId)!;
        if (region != null)
        {
            region.AddObject(reporter);
            reporter.Region = region;
        }

        // The ordinary TurnIn leg serves the Ready step against the row's
        // SPLIT reporter — the turn-in lands at 10857 through normal mechanics.
        var turnIn = actor.TurnInQuest(Quest4424, reporterObjId, 0);
        await Assert.That(turnIn.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(Quest4424)).IsTrue();
        await Assert.That(reporterObjId).IsNotEqualTo(giverObjId);
    }

    [Test]
    public async Task ReadyWithdrawsMonitor_Named()
    {
        var (actor, _) = CreateDeliveryActor("deliver-credited");
        var character = actor.Character;
        var staged = character.Quests!.ActiveQuests[Quest4424]!;
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Progress);
        actor.AdvanceQuest(Quest4424);
        await Assert.That(staged.Status).IsEqualTo(QuestStatus.Ready);

        var fixture = QuestFixtureRow.FromQuestData(Quest4424);
        var plan = QuestDirector.Plan(Quest4424, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production deliver plan failed: {plan.FailReason}");
        var result = QuestBehavior.Run(
            actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "deliver-credited-1" },
            BotObservedContext.Capture(actor),
            [plan],
            (_, _) => []);

        var evidence = result.LegEvidence.Single(e => e.Leg == QuestLegId.Deliver);
        await Assert.That(evidence.Entered).IsFalse();
        await Assert.That(evidence.Detail).IsEqualTo("quest-not-usable");
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// Stages 4424 active through the real engine surfaces at the 9789 giver.
    /// Start 19314 carries CompleteQuestContext(4417): the engine accept gate
    /// refuses 4424 until the potato chain is complete, so the prereq flag is
    /// the engine's own completed-flag record. Returns the actor pre-Progress
    /// (the tests drain Start/Supply through the real advances).
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateDeliveryActor(string name)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 10;
        actor.Character.Hp = actor.Character.MaxHp;
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var region = session.World.GetRegionByPos(new Vector3(0, 0, 0))
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        actor.Character.Quests!.SetCompletedQuestFlag(4417, true);
        var accept = actor.AcceptQuest(Quest4424, QuestAcceptorType.Npc, AcceptNpc9789);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");
        if (!actor.Character.Quests.ActiveQuests.ContainsKey(Quest4424))
            throw new InvalidOperationException("quest 4424 not active after accept");
        return (actor, session);
    }
}
