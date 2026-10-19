using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Seeded deterministic integration of the production arbiter, retained quest
/// owner, roam executor, quest runtime and real GameplayActor. A's source is
/// absent; no objective credit, rewards, completion or B choice is injected.
/// This proves retirement/selection, not productive quest completion or live play.
/// </summary>
[NotInParallel]
public class QuestOwnerHandoffTests
{
    private const uint FirstQuest = 97_101;
    private const uint SecondQuest = 97_102;
    private const uint IneligibleQuest = 97_103;
    private const uint Giver = 97_110;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private static readonly Vector3 Origin = new(32, 32, 0);
    private static uint s_extraNpcId = 0x97100;
    private WorldTemplate? _recoveryWorldTemplate;
    private uint[,]? _previousRecoveryZones;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestBehavior.ClearGiveUpMemory();
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        SeedBoarLootLink();
    }

    [After(Test)]
    public void TearDown()
    {
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        QuestBehavior.ClearGiveUpMemory();
        if (_recoveryWorldTemplate != null)
            _recoveryWorldTemplate.ZoneKeyByRegions = _previousRecoveryZones;
    }

    [Test]
    public async Task NewQuestPurpose_DoesNotReadOldQuestMoveTerminalAsItsOwn()
    {
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-old-quest-audit");
        await Step(steps, bot, clock);
        var prey = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        for (var wake = 0; wake < 8 && actor.ActiveRequest is not
            { IsTerminal: false, Action: ActorActionType.Move, MoveOwner: TravelLegDispatch.PursuitMoveOwner }; wake++)
            await Step(steps, bot, clock);
        await Assert.That(actor.ActiveRequest?.DecisionQuestId).IsEqualTo(FirstQuest);
        actor.PreemptCurrent("A-only navigation interruption");
        // Explicit ownership boundary fixture: no B quest or credit is supplied.
        TravelIntentStore.SetPursuitPurpose(actor.ActorId, SecondQuest);
        await Assert.That(TravelLegDispatch.MapLegOutcome(actor, prey,
            TravelLegDispatch.PursuitMoveOwner)).IsEqualTo(TravelLegOutcome.None);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnchosenTwoActiveQuests_AttributePursuitToDispatchedQuest(bool alternativeAvailable)
    {
        // Explicit two-quest setup exercises the unchosen-policy boundary;
        // this is an ownership control, not independent quest acquisition proof.
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-unchosen-attribution", gatherFollowUp: true);
        await Step(steps, bot, clock);
        await Assert.That(actor.AcceptQuest(SecondQuest, QuestAcceptorType.Npc, Giver).State).IsEqualTo(ActorLifecycleState.Completed);
        var prey = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(prey);
        // Disclosed fake source mapping gives B a distinct prey; no DB is edited.
        const uint otherTemplate = 97_120;
        var other = SpawnExtraPrey(session, Origin + new Vector3(24, 0, 0));
        session.World.GetNpc(other)!.Template = new NpcTemplate { Id = otherTemplate, Scale = 1f };
        if (alternativeAvailable) session.World.GetNpc(other)!.TemplateId = otherTemplate;
        var links = (Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc>>)typeof(ItemManager)
            .GetField("_lootPackDroppingNpc", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ItemManager.Instance)!;
        links[otherTemplate] = [new AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc
            { Id = otherTemplate, NpcId = otherTemplate, LootPackId = Pack4530 }];
        var plans = new[]
        {
            QuestDirector.Plan(FirstQuest, QuestFixtureRow.FromQuestData(FirstQuest)),
            QuestDirector.Plan(SecondQuest, QuestFixtureRow.FromQuestData(SecondQuest) with { PreyTemplate = otherTemplate })
        };
        var result = QuestBehavior.Run(actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "unchosen-two-active", ChosenQuestId = 0,
                ObjectiveTargetPriority = 0 }, BotObservedContext.Capture(actor), plans, (_, _) => []);
        links.Remove(otherTemplate);
        await Assert.That(result.Request?.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.Request?.DecisionQuestId).IsEqualTo(FirstQuest);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var intent,
            TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(intent.PursuitQuestId).IsEqualTo(FirstQuest);
        await Assert.That(intent.TargetObjId).IsEqualTo(result.Request!.TargetId);
        actor.PreemptCurrent("unchosen ownership cleanup");
    }

    [Test]
    public async Task ExhaustedQuestA_IndependentQuestB_CanTryTheSamePrey()
    {
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-same-prey-followup", gatherFollowUp: true);
        await Step(steps, bot, clock);
        var prey = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        for (var wake = 0; wake < 8 && actor.ActiveRequest is not
            { IsTerminal: false, Action: ActorActionType.Move, MoveOwner: TravelLegDispatch.PursuitMoveOwner }; wake++)
            await Step(steps, bot, clock);
        await Assert.That(actor.ActiveRequest?.DecisionQuestId).IsEqualTo(FirstQuest);
        actor.PreemptCurrent("A-only blocked approach");
        TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
            TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner, Origin,
            session.World.GetNpc(prey)!.Transform.World.Position);
        var bMoved = false;
        for (var wake = 0; wake < 35 && !bMoved; wake++)
        {
            await Step(steps, bot, clock);
            bMoved = actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move,
                MoveOwner: TravelLegDispatch.PursuitMoveOwner } request
                && request.DecisionQuestId == SecondQuest && request.TargetId == prey;
        }
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(SecondQuest);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsTrue();
        await Assert.That(bMoved).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var bIntent,
            TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(bIntent.RepathCount).IsEqualTo(0);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(SecondQuest)).IsFalse();
        actor.PreemptCurrent("same-prey followup cleanup");
    }

    [Test]
    public async Task RememberedFailedPrey_PreserveUsefulThirdPrey_AndChangedApproach()
    {
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-third-prey", eligibleFollowUp: false);
        await Step(steps, bot, clock);
        var first = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        var second = SpawnExtraPrey(session, Origin + new Vector3(24, 0, 0));
        var third = SpawnExtraPrey(session, Origin + new Vector3(30, 0, 0));
        foreach (var target in new[] { first, second })
        {
            TravelLegDispatch.EnsureJourney(actor, target, session.World.GetNpc(target)!.Transform.World.Position,
                3, 2, TravelLegDispatch.PursuitMoveOwner);
            TravelIntentStore.SetPursuitPurpose(actor.ActorId, FirstQuest);
            TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
                TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner, Origin,
                session.World.GetNpc(target)!.Transform.World.Position);
        }
        actor.Character.CurrentTarget = session.World.GetNpc(second);
        var row = QuestFixtureRow.FromQuestData(FirstQuest);
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, first)).IsTrue();
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, second)).IsTrue();
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "third", row).SelectedObjId).IsEqualTo(third);
        GameplayActorTestRig.SetNpcPosition(session, first, Origin + new Vector3(22, 0, 0));
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, first)).IsFalse();
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, second)).IsTrue();
        GameplayActorTestRig.SetPosition(actor, Origin - new Vector3(4, 0, 0));
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, second)).IsFalse();
        actor.PreemptCurrent("third-prey cleanup");
    }

    [Test]
    public async Task FailedApproachCapacity_DoesNotEvictIntoRetries_ChangedActorCanReconsider()
    {
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-prey-capacity", eligibleFollowUp: false);
        await Step(steps, bot, clock);
        for (var index = 0; index < TravelIntentStore.FailedApproachBound + 1; index++)
        {
            var target = SpawnExtraPrey(session, Origin + new Vector3(18 + index, 0, 0));
            TravelLegDispatch.EnsureJourney(actor, target, session.World.GetNpc(target)!.Transform.World.Position,
                3, 2, TravelLegDispatch.PursuitMoveOwner);
            TravelIntentStore.SetPursuitPurpose(actor.ActorId, FirstQuest);
            TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
                TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner, Origin,
                session.World.GetNpc(target)!.Transform.World.Position);
            await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, target)).IsTrue();
        }
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var intent,
            TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(intent.FailedApproaches.Count).IsEqualTo(TravelIntentStore.FailedApproachBound);
        GameplayActorTestRig.SetPosition(actor, Origin - new Vector3(4, 0, 0));
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, intent.TargetObjId)).IsFalse();
        actor.PreemptCurrent("failure-capacity cleanup");
    }

    [Test]
    public async Task TwoOwnerFailedPrey_EarnFiniteGiveUp_WithoutOscillatingOrCreditingQuest()
    {
        var (actor, module, executor, steps, bot, clock, _, session) =
            CreateRig("owner-two-exhausted-prey", eligibleFollowUp: false);
        await Step(steps, bot, clock);
        var first = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        var second = SpawnExtraPrey(session, Origin + new Vector3(24, 0, 0));
        var preyIds = new HashSet<uint> { first, second };
        var failedTargets = new List<uint>();
        QuestGiveUp? earned = null;

        for (var wake = 0; wake < 80 && earned == null; wake++)
        {
            // Disclosed blocked-approach fixture: the world and both living prey
            // stay at the same poses. The owner still chooses every target.
            GameplayActorTestRig.SetPosition(actor, Origin);
            await Step(steps, bot, clock);
            executor.TryGetQuestRuntime(bot.CharacterId, out var runtime);
            earned = runtime?.LastResult?.GiveUps.FirstOrDefault(q => q.QuestId == FirstQuest);
            if (earned?.Threshold == 0) earned = null;
            if (earned != null) break;

            var selected = actor.Character.CurrentTarget?.ObjId ?? 0;
            if (!preyIds.Contains(selected)
                || !TravelIntentStore.TryGet(actor.ActorId, out var intent,
                    TravelLegDispatch.PursuitMoveOwner)
                || intent.TargetObjId != selected
                || intent.Terminal != TravelTerminal.None
                || actor.ActiveRequest is not
                    { IsTerminal: false, Action: ActorActionType.Move,
                      MoveOwner: TravelLegDispatch.PursuitMoveOwner })
                continue;

            // Signal failure only for the request the quest owner actually
            // dispatched to its own current prey. Neither prey is assigned by
            // the fixture, and no objective, item or quest credit is injected.
            actor.PreemptCurrent("blocked approach fixture");
            TravelIntentStore.SetPursuitPurpose(actor.ActorId, FirstQuest);
            TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
                TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner,
                actor.Character.Transform.World.Position,
                session.World.GetNpc(selected)!.Transform.World.Position);
            failedTargets.Add(selected);
        }

        await Assert.That(failedTargets.Distinct().Count()).IsEqualTo(2);
        await Assert.That(earned.HasValue).IsTrue();
        await Assert.That(earned!.Value.Wakes).IsEqualTo(10);
        await Assert.That(earned.Value.Reason).Contains($"quest={FirstQuest} stage=PURSUIT");
        await Assert.That(actor.Character.Quests.ActiveQuests[FirstQuest].Objectives[0]).IsEqualTo(0);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsNotEqualTo(FirstQuest);
        actor.PreemptCurrent("two-prey fixture cleanup");
    }

    [Test]
    public async Task ExhaustedApproach_InsideExistingRangedCombatBand_RemainsUseful()
    {
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-exhausted-ranged");
        await Step(steps, bot, clock);
        actor.Character.Ability1 = AbilityType.Wild;
        var prey = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(prey);
        TravelLegDispatch.EnsureJourney(actor, prey, Origin + new Vector3(18, 0, 0), 3, 2,
            TravelLegDispatch.PursuitMoveOwner);
        TravelIntentStore.SetPursuitPurpose(actor.ActorId, FirstQuest);
        TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
            TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner,
            Origin, Origin + new Vector3(18, 0, 0));
        await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character,
            actor.Character.GetDistanceTo(session.World.GetNpc(prey)!, true))).IsTrue();
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, prey)).IsFalse();
        await Assert.That(BotOpportunityListing.List(actor).ActiveWork.Any(q => q.QuestId == FirstQuest
            && q.Reason.EndsWith("-source-perceived", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExhaustedPursuit_UnchangedPoseStaysDeferred_ChangedApproachReadmits(bool moveActor)
    {
        var (actor, module, _, steps, bot, clock, _, session) = CreateRig("owner-exhausted-readmit", eligibleFollowUp: false);
        await Step(steps, bot, clock);
        var prey = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(prey);
        TravelLegDispatch.EnsureJourney(actor, prey, Origin + new Vector3(18, 0, 0), 3, 2,
            TravelLegDispatch.PursuitMoveOwner);
        TravelIntentStore.SetPursuitPurpose(actor.ActorId, FirstQuest);
        TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
            TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner,
            Origin, Origin + new Vector3(18, 0, 0));
        for (var wake = 0; wake < 15; wake++) await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsNotEqualTo(FirstQuest);
        var attempts = actor.AuditTrace.Count(r => r.DecisionQuestId == FirstQuest);
        for (var wake = 0; wake < 6; wake++) await Step(steps, bot, clock);
        await Assert.That(actor.AuditTrace.Count(r => r.DecisionQuestId == FirstQuest)).IsEqualTo(attempts);
        if (moveActor)
            GameplayActorTestRig.SetPosition(actor, Origin + new Vector3(-4, 0, 0));
        else
            GameplayActorTestRig.SetNpcPosition(session, prey, Origin + new Vector3(22, 0, 0));
        for (var wake = 0; wake < 6 && module.GetChosenQuest(bot.CharacterId) != FirstQuest; wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        actor.PreemptCurrent("readmission test cleanup");
    }

    [Test]
    public async Task ExhaustedPursuit_PrefersObservedAlternative_AndContactRemainsUseful()
    {
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-exhausted-alternative");
        await Step(steps, bot, clock);
        var stuck = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        var alternative = SpawnExtraPrey(session, Origin + new Vector3(24, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(stuck);
        var row = QuestFixtureRow.FromQuestData(FirstQuest);
        var snapshot = BotObservedContext.Capture(actor);
        TravelLegDispatch.EnsureJourney(actor, stuck, Origin + new Vector3(18, 0, 0), 3, 2,
            TravelLegDispatch.PursuitMoveOwner);
        // Under-budget commitment remains valid.
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "live", row).SelectedObjId).IsEqualTo(stuck);
        TravelIntentStore.SetPursuitPurpose(actor.ActorId, FirstQuest);
        TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
            TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner);
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "exhausted", row).SelectedObjId).IsEqualTo(alternative);
        GameplayActorTestRig.SetNpcPosition(session, stuck, Origin + new Vector3(2, 0, 0));
        await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, stuck)).IsFalse();
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "contact", row).SelectedObjId).IsEqualTo(stuck);
        // Another target's failure never demotes this actor's valid commitment.
        TravelIntentStore.Disarm(actor.ActorId, TravelLegDispatch.PursuitMoveOwner);
    }

    [Test]
    public async Task ExhaustedOutOfReachPursuit_EarnsFiniteGiveUp_WithoutCompletingQuest()
    {
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-exhausted-pursuit");
        await Step(steps, bot, clock);
        var prey = SpawnPrey(session, Origin + new Vector3(18, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(prey);
        // Disclosed rig input: exercise the owner's reaction to the existing
        // authoritative movement terminal, rather than fake a walking success.
        TravelLegDispatch.EnsureJourney(actor, prey, Origin + new Vector3(18, 0, 0), 3, 2,
            TravelLegDispatch.PursuitMoveOwner);
        TravelIntentStore.SetPursuitPurpose(actor.ActorId, FirstQuest);
        TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
            TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner);
        QuestGiveUp? earned = null;
        for (var wake = 0; wake < 15 && earned == null; wake++)
        {
            await Step(steps, bot, clock);
            executor.TryGetQuestRuntime(bot.CharacterId, out var runtime);
            earned = runtime?.LastResult?.GiveUps.FirstOrDefault(q => q.QuestId == FirstQuest);
            if (earned?.Threshold == 0) earned = null;
        }
        await Assert.That(earned.HasValue).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(FirstQuest)).IsTrue();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        for (var wake = 0; wake < 4 && module.GetChosenQuest(bot.CharacterId) != SecondQuest; wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(SecondQuest);
    }

    [Test]
    public async Task EarnedGiveUp_ReleasesOwner_IndependentlyAcceptsSameNpcQuest()
    {
        var (actor, module, executor, steps, bot, clock, giver, _) = CreateRig("owner-handoff");
        await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(FirstQuest)).IsTrue();
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Any(q => q.QuestId == FirstQuest && q.Leg == "wait")).IsTrue();
        await Assert.That(listing.BlockedWork.Any(q => q.QuestId == FirstQuest)).IsFalse();
        await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == SecondQuest && q.SourceObjId == giver)).IsTrue();
        await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == IneligibleQuest)).IsFalse();

        QuestGiveUp? earned = null;
        for (var wake = 0; wake < 15 && earned == null; wake++)
        {
            await Step(steps, bot, clock);
            executor.TryGetQuestRuntime(bot.CharacterId, out var runtime);
            var verdicts = runtime?.LastResult?.GiveUps ?? [];
            if (verdicts.Any(q => q.QuestId == FirstQuest))
                earned = verdicts.First(q => q.QuestId == FirstQuest);
        }
        await Assert.That(earned.HasValue).IsTrue();
        await Assert.That(earned!.Value.Wakes).IsEqualTo(10);
        await Assert.That(earned.Value.Reason).StartsWith($"GIVE-UP/UNRESOLVED-TARGET quest={FirstQuest}");
        var advances = actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest && r.TargetId == FirstQuest);
        Console.WriteLine($"HANDOFF earned={earned.Value.Reason} chosen={module.GetChosenQuest(bot.CharacterId)} advances={advances}");

        for (var wake = 0; wake < 4 && !actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest); wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(SecondQuest);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(FirstQuest)).IsTrue();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AcceptQuest && r.TargetId == SecondQuest)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest && r.TargetId == FirstQuest)).IsEqualTo(advances);
        await Assert.That(actor.AuditTrace.Where(r => r.Action == ActorActionType.AcceptQuest && r.TargetId == SecondQuest)
            .All(r => r.DecisionQuestId == SecondQuest)).IsTrue();
        // Old A verdicts cannot release the newly retained B, and a verdict
        // below its existing threshold cannot retire otherwise useful work.
        module.NotifyQuestGiveUp(bot.CharacterId, earned.Value);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(SecondQuest);
        module.NotifyQuestGiveUp(bot.CharacterId, new QuestGiveUp(SecondQuest, "under-budget", 1, 10));
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(SecondQuest);
        await Assert.That(actor.AcceptQuest(IneligibleQuest, QuestAcceptorType.Npc, Giver).State).IsEqualTo(ActorLifecycleState.Rejected);
    }

    [Test]
    public async Task UnderBudgetInterruption_RetainsOwner_ResumesWithFreshRequest()
    {
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-interrupted");
        await Step(steps, bot, clock);
        SpawnPrey(session);
        for (var wake = 0; wake < 6 && actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move }; wake++)
            await Step(steps, bot, clock);
        var interrupted = actor.ActiveRequest!;
        await Assert.That(interrupted is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(interrupted.DecisionQuestId).IsEqualTo(FirstQuest);
        await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var runtime)).IsTrue();
        await Assert.That(runtime!.OwnsRunningRequest(interrupted)).IsTrue();
        var position = actor.Character.Transform.World.Position;
        await Assert.That(actor.Interrupt(interrupted.TraceId)).IsTrue();
        await Assert.That(interrupted.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Step(steps, bot, clock);
        await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(position);
        for (var wake = 0; wake < 6 && actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move }; wake++)
            await Step(steps, bot, clock);
        var fresh = actor.ActiveRequest!;
        await Assert.That(fresh is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(fresh.TraceId).IsNotEqualTo(interrupted.TraceId);
        await Assert.That(fresh.DecisionQuestId).IsEqualTo(FirstQuest);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(runtime.LastResult!.GiveUps).IsEmpty();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == interrupted.TraceId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AcceptQuest && r.TargetId == SecondQuest)).IsFalse();
    }

    [Test]
    [Arguments(20f)]
    [Arguments(55f)]
    public async Task UnchangedFacts_DoNotRetryRetiredQuest_SourceReturnAllowsFreshChoice(float range)
    {
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-source-return", eligibleFollowUp: false);
        // With B legally unavailable, source return can reconsider A without
        // stealing another retained, now-actionable goal.
        for (var wake = 0; wake < 16; wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsFalse();
        var retiredRequests = actor.AuditTrace.Count(r => r.DecisionQuestId == FirstQuest);
        for (var wake = 0; wake < 16; wake++)
            await Step(steps, bot, clock);
        await Assert.That(actor.AuditTrace.Count(r => r.DecisionQuestId == FirstQuest)).IsEqualTo(retiredRequests);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsNotEqualTo(FirstQuest);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        SpawnPrey(session, Origin + new Vector3(range, 0, 0));
        for (var wake = 0; wake < 6 && module.GetChosenQuest(bot.CharacterId) != FirstQuest; wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var runtime)).IsTrue();
        await Assert.That(runtime!.LastResult!.GiveUps).IsEmpty();
        await Assert.That(actor.AuditTrace.Count(r => r.DecisionQuestId == FirstQuest)
            + (actor.ActiveRequest?.DecisionQuestId == FirstQuest ? 1 : 0)).IsGreaterThan(retiredRequests);
    }

    [Test]
    public async Task AllSupportedWorkDeferred_AllowsOrdinaryDiscoveryWithoutErasingQuests()
    {
        var (actor, module, _, steps, bot, clock, _, _) = CreateRig("owner-deferred-discovery", eligibleFollowUp: false);
        for (var wake = 0; wake < 32; wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(FirstQuest)).IsTrue();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        var decision = module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });
        Console.WriteLine($"DEFERRED-DISCOVERY allowed={decision.CanActivate} activity={decision.ActivityName} reason={decision.WhyNot}");
        await Assert.That(decision.CanActivate).IsTrue();
        await Assert.That(decision.ActivityName).IsEqualTo(QuestBootstrapActivityModule.DiscoveryActivityName);
    }

    [Test]
    public async Task DeferredWork_DiscoveryWaitsForMovementThenWalksToEligibleGiver()
    {
        var (actor, module, executor, steps, bot, clock, giver, session) = CreateRig(
            "owner-discovery-walk", eligibleFollowUp: false);
        for (var wake = 0; wake < 32; wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
        var retiredAdvances = actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest
            && r.DecisionQuestId == FirstQuest);

        // Disclosed world fixtures: the same giver is now distant and B's
        // ordinary level requirement is satisfied. No quest/target is assigned.
        var destination = Origin + new Vector3(80, 0, 0);
        GameplayActorTestRig.SetNpcPosition(session, giver, destination);
        GameplayActorTestRig.SeedQuestComponentLevelRequirement(97_204, 1);
        SeedNpcSource(actor, Origin + new Vector3(20, 0, 0));
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(actor.Character.ParentWorld!.SpawnManager)!;
        spawners[97_310] = [new NpcSpawner { Id = 97_310, UnitId = Giver,
            Position = new WorldSpawnPosition { X = destination.X, Y = destination.Y, Z = destination.Z } }];

        // A finite unrelated move is a running-action preservation control.
        // Actor.Tick completes ordinary movement; no terminal is injected.
        actor.BroadcastMovement = false;
        var foreignMove = actor.MoveTo(Origin + new Vector3(0, 4, 0), speed: 5f);
        await Assert.That(foreignMove.IsTerminal).IsFalse();
        for (var wake = 0; wake < 3; wake++)
            await Step(steps, bot, clock);
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(foreignMove);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsFalse();
        for (var tick = 0; tick < 100 && !foreignMove.IsTerminal; tick++)
            actor.Tick(TimeSpan.FromMilliseconds(100));
        await Assert.That(foreignMove.State).IsEqualTo(ActorLifecycleState.Completed);

        await Step(steps, bot, clock);
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move,
            MoveOwner: "QUEST_TRAVEL", DecisionQuestId: null }).IsTrue();
        await Assert.That(executor.GetRoamRoute(bot.CharacterId)!.Waypoints.Last()).IsEqualTo(destination);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        for (var wake = 0; wake < 400 && !actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest); wake++)
        {
            actor.Tick(TimeSpan.FromMilliseconds(100));
            await Step(steps, bot, clock);
        }
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(SecondQuest);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(IneligibleQuest)).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AcceptQuest
            && r.TargetId == SecondQuest && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest
            && r.DecisionQuestId == FirstQuest)).IsEqualTo(retiredAdvances);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        actor.PreemptCurrent("discovery movement regression teardown");
    }

    [Test]
    public async Task NoEligibleFollowUp_StopsRetryingWithoutFakeCompletion()
    {
        var (actor, module, _, steps, bot, clock, _, _) = CreateRig("owner-no-followup", eligibleFollowUp: false);
        for (var wake = 0; wake < 16; wake++)
            await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
        var advances = actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest && r.TargetId == FirstQuest);
        await Assert.That(advances).IsEqualTo(10);
        for (var wake = 0; wake < 16; wake++)
            await Step(steps, bot, clock);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest && r.TargetId == FirstQuest)).IsEqualTo(advances);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(FirstQuest)).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsFalse();
    }

    [Test]
    public async Task RemoteNpcGatherSource_WithdrawsNoOpAdvance_TravelsAndRetriesUnderSameOwner()
    {
        // Synthetic quest/loot chain, region, remote spawner and clock are
        // setup. Production arbitration and real requests perform acceptance,
        // withdrawal, travel and interruption; no objective credit is supplied.
        var (actor, module, executor, steps, bot, clock, _, _) = CreateRig("owner-remote-source");
        var source = Origin + new Vector3(80, 0, 0);
        SeedNpcSource(actor, source);
        await Step(steps, bot, clock);
        await Assert.That(actor.Character.Quests.ActiveQuests[FirstQuest].Status).IsEqualTo(QuestStatus.Progress);
        var row = QuestFixtureRow.FromQuestData(FirstQuest);
        await Assert.That(row.GatherActId).IsNotEqualTo(0u);
        await Assert.That(row.PreyTemplate).IsEqualTo(Boar3475);
        await Assert.That(QuestBehavior.HasGatherTemplates(row)).IsFalse();
        var advances = actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest);

        await Step(steps, bot, clock);
        await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var runtime)).IsTrue();
        var advance = runtime!.LastResult!.LegEvidence.Single(e => e.Leg == QuestLegId.Advance);
        await Assert.That(advance.Entered).IsFalse();
        await Assert.That(advance.Detail).IsEqualTo("steady-progress-uncredited-source");
        await Assert.That(runtime.LastResult.GiveUps).IsEmpty();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest)).IsEqualTo(advances);
        var interrupted = actor.ActiveRequest!;
        await Assert.That(interrupted is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(interrupted.MoveOwner).IsEqualTo("QUEST_TRAVEL");
        await Assert.That(interrupted.DecisionQuestId).IsEqualTo(FirstQuest);
        await Assert.That(executor.GetRoamRoute(bot.CharacterId)!.Waypoints.Last()).IsEqualTo(source);
        try
        {
            await Step(steps, bot, clock);
            await Assert.That(actor.ActiveRequest).IsSameReferenceAs(interrupted);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Assert.That(actor.Interrupt(interrupted.TraceId)).IsTrue();
            // The existing stale-route guard consumes the interrupted route
            // on this wake; replanning issues a fresh route on the next one.
            await Step(steps, bot, clock);
            await Assert.That(actor.ActiveRequest is { IsTerminal: false }).IsFalse();
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Step(steps, bot, clock);
            var fresh = actor.ActiveRequest!;
            await Assert.That(fresh is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
            await Assert.That(fresh.TraceId).IsNotEqualTo(interrupted.TraceId);
            await Assert.That(fresh.MoveOwner).IsEqualTo("QUEST_TRAVEL");
            await Assert.That(fresh.DecisionQuestId).IsEqualTo(FirstQuest);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Assert.That(runtime.LastResult!.GiveUps).IsEmpty();
            await Assert.That(actor.AuditTrace.Count(r => r.TraceId == interrupted.TraceId)).IsEqualTo(1);
            await Assert.That(actor.AuditTrace.Single(r => r.TraceId == interrupted.TraceId).Result)
                .IsEqualTo(ActorLifecycleState.Interrupted);
            await Step(steps, bot, clock);
            await Assert.That(actor.ActiveRequest).IsSameReferenceAs(fresh);
            await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest)).IsEqualTo(advances);
            await Assert.That(actor.Character.Quests.ActiveQuests[FirstQuest].Objectives[0]).IsEqualTo(0);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsFalse();
        }
        finally
        {
            actor.PreemptCurrent("remote-source regression teardown");
        }
    }

    [Test]
    public async Task RepeatedStuckQuestTravel_RecoversOnceWithoutCompletingQuest()
    {
        var (actor, module, executor, steps, bot, clock, _, _) = CreateRig("owner-stuck-recovery", groundHeight: 1f);
        SeedRecoveryZone(actor);
        var start = Origin + new Vector3(0, 0, 1);
        GameplayActorTestRig.SetPosition(actor, start);
        var source = start + new Vector3(80, 0, 0);
        SeedNpcSource(actor, source);
        actor.NoProgressWindow = TimeSpan.FromMilliseconds(200);
        actor.MaxUnstickNudges = 1;
        await Step(steps, bot, clock);
        await Step(steps, bot, clock);
        var first = actor.ActiveRequest!;
        var waypoint = first.Destination!.Value;
        var recovered = false;
        for (var wake = 0; wake < 24; wake++)
        {
            // Disclosed blocked-ground fixture, not an actual collision engine.
            GameplayActorTestRig.SetPosition(actor, start);
            await Step(steps, bot, clock);
            if (Vector3.Distance(actor.Character.Transform.World.Position, waypoint) < 0.5f)
            {
                recovered = true;
                break;
            }
        }

        await Assert.That(recovered).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(actor.Character.Quests.ActiveQuests[FirstQuest].Objectives[0]).IsEqualTo(0);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(first.Failure).IsEqualTo(ActorFailureReason.Navigation);
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == first.TraceId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move
            && r.Result == ActorLifecycleState.TimedOut && r.Detail!.Contains("stuck"))).IsGreaterThanOrEqualTo(2);
        await Assert.That(executor.GetRoamRoute(bot.CharacterId)).IsNull();
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.CurrentMoveOwner).IsEqualTo("QUEST_STUCK_RECOVERY");
        var state = executor.GetBotState(bot.CharacterId)!;
        var quest = actor.Character.Quests.ActiveQuests[FirstQuest];
        await Assert.That(state.QuestStuckRecoveries.TryGetValue(quest, out var recovery)).IsTrue();
        await Assert.That(recovery!.Used).IsTrue();
        var auditCount = actor.AuditTrace.Count;
        await Step(steps, bot, clock);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(actor.AuditTrace.Skip(auditCount).Any(r => r.Action == ActorActionType.AdvanceQuest
            && r.TraceId != first.TraceId && r.DecisionQuestId == FirstQuest)).IsTrue();
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        // Explicit source relocation models changed reference availability. Ordinary
        // selection discovers the new source; the same quest cannot teleport again.
        var recoveredAt = actor.Character.Transform.World.Position;
        SeedNpcSource(actor, recoveredAt + new Vector3(80, 0, 0));
        await Step(steps, bot, clock);
        var fresh = actor.ActiveRequest!;
        await Assert.That(fresh is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsTrue();
        await Assert.That(fresh.TraceId).IsNotEqualTo(first.TraceId);
        await Assert.That(fresh.DecisionQuestId).IsEqualTo(FirstQuest);
        var recoveryUtc = state.LastQuestStuckRecoveryUtc;
        var giveUps = new List<QuestGiveUp>();
        for (var wake = 0; wake < 60; wake++)
        {
            GameplayActorTestRig.SetPosition(actor, recoveredAt);
            await Step(steps, bot, clock);
            await Assert.That(Vector3.Distance(actor.Character.Transform.World.Position, recoveredAt)).IsLessThan(2f);
            executor.TryGetQuestRuntime(bot.CharacterId, out var observedRuntime);
            giveUps.AddRange((observedRuntime?.LastResult?.GiveUps ?? []).Where(g => !giveUps.Contains(g)));
        }
        Console.WriteLine($"STUCK-END chosen={module.GetChosenQuest(bot.CharacterId)} hp={actor.Character.Hp} active={actor.Character.Quests.ActiveQuests.ContainsKey(FirstQuest)} arms={state.QuestObjectiveSourceArms} moves={actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)} giveups={string.Join(';', giveUps.Select(g => g.Reason))}");
        await Assert.That(state.LastQuestStuckRecoveryUtc).IsEqualTo(recoveryUtc);
        await Assert.That(giveUps.Any(g => g.QuestId == FirstQuest)).IsTrue();
        var earned = giveUps.First(g => g.QuestId == FirstQuest);
        await Assert.That(earned.Wakes).IsEqualTo(10);
        await Assert.That(earned.Threshold).IsEqualTo(10);
        await Assert.That(earned.Reason).StartsWith("GIVE-UP/UNRESOLVED-TARGET");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
        var moves = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);
        for (var wake = 0; wake < 10; wake++)
            await Step(steps, bot, clock);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(moves);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        actor.PreemptCurrent("stuck recovery regression teardown");
    }

    [Test]
    [Arguments("dead")]
    [Arguments("combat")]
    [Arguments("autoattack")]
    [Arguments("attached")]
    [Arguments("disabled-position")]
    [Arguments("foreign-request")]
    [Arguments("changed-owner")]
    [Arguments("reaccepted-quest")]
    [Arguments("changed-status")]
    [Arguments("changed-world")]
    [Arguments("stale-generation")]
    [Arguments("cooldown")]
    [Arguments("unknown-ground")]
    [Arguments("nonfinite-ground")]
    [Arguments("unsafe-height")]
    [Arguments("water")]
    [Arguments("unknown-zone")]
    [Arguments("different-zone")]
    public async Task StuckQuestRecovery_UnsafeOrStaleStateDoesNotRelocate(string guard)
    {
        var ground = 1f;
        var (actor, _, executor, steps, bot, clock, _, _) = CreateRig("recovery-" + guard,
            groundProvider: (_, _) => ground);
        SeedRecoveryZone(actor);
        var start = Origin + new Vector3(0, 0, 1);
        GameplayActorTestRig.SetPosition(actor, start);
        SeedNpcSource(actor, start + new Vector3(80, 0, 0));
        actor.NoProgressWindow = TimeSpan.FromMilliseconds(200);
        await Step(steps, bot, clock);
        await Step(steps, bot, clock);
        var state = executor.GetBotState(bot.CharacterId)!;
        var first = FailQuestMove(actor, state, start);
        await Assert.That(executor.HandleStuckQuestTravel(bot, state, clock.GetUtcNow().UtcDateTime)).IsFalse();
        var second = IssueAndFailQuestMove(executor, actor, state, start);
        ActorRequest? foreign = null;
        var originalQuest = actor.Character.Quests.ActiveQuests[FirstQuest];
        switch (guard)
        {
            case "dead": actor.Character.Hp = 0; break;
            case "combat": actor.Character.IsInBattle = true; break;
            case "autoattack": actor.Character.IsAutoAttack = true; break;
            case "attached": actor.Character.AttachedPoint = AAEmu.Game.Models.Game.DoodadObj.Static.AttachPointKind.Passenger0; break;
            case "disabled-position": actor.Character.DisabledSetPosition = true; break;
            case "foreign-request": foreign = actor.MoveTo(start + new Vector3(3, 0, 0)); break;
            case "changed-owner": executor.QuestChosenProvider = _ => SecondQuest; break;
            case "reaccepted-quest": actor.Character.Quests.ActiveQuests[FirstQuest] = new Quest(actor.Character)
                { TemplateId = FirstQuest, Status = QuestStatus.Progress, Step = QuestComponentKind.Progress, Objectives = [0, 0, 0, 0, 0] }; break;
            case "changed-status": originalQuest.Status = QuestStatus.Ready; break;
            case "changed-world": typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(actor.Character.Transform, actor.Character.ParentWorld.Id + 1u); break;
            case "stale-generation": state.Generation++; break;
            case "cooldown": state.LastQuestStuckRecoveryUtc = clock.GetUtcNow().UtcDateTime; break;
            case "unknown-ground": ground = 0; break;
            case "nonfinite-ground": ground = float.NaN; break;
            case "unsafe-height": ground = 10; break;
            case "water": actor.Character.ParentWorld.Water.OceanLevel = 2; break;
            case "unknown-zone": typeof(Transform).GetField("_zoneId", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(actor.Character.Transform, 0u); break;
            case "different-zone": actor.Character.ParentWorld.Template.ZoneKeyByRegions[
                (int)(second.Destination!.Value.X / WorldManager.REGION_SIZE),
                (int)(second.Destination!.Value.Y / WorldManager.REGION_SIZE)] = 8; break;
        }
        var before = actor.Character.Transform.World.Position;
        _ = executor.HandleStuckQuestTravel(bot, state, clock.GetUtcNow().UtcDateTime);
        await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(before);
        await Assert.That(actor.CurrentMoveOwner == "QUEST_STUCK_RECOVERY").IsFalse();
        await Assert.That(originalQuest.Objectives[0]).IsEqualTo(0);
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == first.TraceId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == second.TraceId)).IsEqualTo(1);
        if (foreign != null)
        {
            await Assert.That(actor.ActiveRequest).IsSameReferenceAs(foreign);
            await Assert.That(foreign.State).IsEqualTo(ActorLifecycleState.Running);
        }
        actor.PreemptCurrent("negative stuck recovery teardown");
    }

    [Test]
    public async Task StuckQuestRecovery_SameTerminalCannotCountTwice_AndFailureWindowExpires()
    {
        var (actor, _, executor, steps, bot, clock, _, _) = CreateRig("recovery-dedup", groundHeight: 1f);
        SeedRecoveryZone(actor);
        var start = Origin + new Vector3(0, 0, 1);
        GameplayActorTestRig.SetPosition(actor, start);
        SeedNpcSource(actor, start + new Vector3(80, 0, 0));
        actor.NoProgressWindow = TimeSpan.FromMilliseconds(200);
        await Step(steps, bot, clock);
        await Step(steps, bot, clock);
        var state = executor.GetBotState(bot.CharacterId)!;
        var first = FailQuestMove(actor, state, start);
        var now = clock.GetUtcNow().UtcDateTime;
        await Assert.That(executor.HandleStuckQuestTravel(bot, state, now)).IsFalse();
        await Assert.That(executor.HandleStuckQuestTravel(bot, state, now)).IsFalse();
        var quest = actor.Character.Quests.ActiveQuests[FirstQuest];
        await Assert.That(state.QuestStuckRecoveries.GetValue(quest, _ => throw new Exception()).Failures).IsEqualTo(1);
        var second = IssueAndFailQuestMove(executor, actor, state, start);
        await Assert.That(second.TraceId).IsNotEqualTo(first.TraceId);
        await Assert.That(executor.HandleStuckQuestTravel(bot, state,
            now + BotRoamStepExecutor.QuestStuckRecoveryCooldown + TimeSpan.FromSeconds(1))).IsFalse();
        await Assert.That(state.QuestStuckRecoveries.GetValue(quest, _ => throw new Exception()).Failures).IsEqualTo(1);
        await Assert.That(state.LastQuestStuckRecoveryUtc).IsNull();
    }

    [Test]
    [Arguments("nan")]
    [Arguments("infinite")]
    [Arguments("negative")]
    [Arguments("world-boundary")]
    [Arguments("too-far")]
    [Arguments("already-there")]
    [Arguments("missing-dimensions")]
    [Arguments("missing-zone-grid")]
    [Arguments("height-gap")]
    public async Task QuestRecoveryDestination_UnknownOrInvalidLocationIsRefused(string invalid)
    {
        var (actor, _, executor, _, _, _, _, _) = CreateRig("recovery-position-" + invalid, groundHeight: 1);
        SeedRecoveryZone(actor);
        var start = Origin + new Vector3(0, 0, 1);
        GameplayActorTestRig.SetPosition(actor, start);
        var target = start + new Vector3(25, 0, 0);
        var template = actor.Character.ParentWorld.Template;
        var cells = template.CellX;
        try
        {
            switch (invalid)
            {
                case "nan": target.X = float.NaN; break;
                case "infinite": target.Y = float.PositiveInfinity; break;
                case "negative": target.X = -0.1f; break;
                case "world-boundary": target.X = template.CellX * WorldManager.CELL_SIZE; break;
                case "too-far": target = start + new Vector3(76, 0, 0); break;
                case "already-there": target = start; break;
                case "missing-dimensions": template.CellX = 0; break;
                case "missing-zone-grid": template.ZoneKeyByRegions = null; break;
                case "height-gap": target.Z = 20; break;
            }
            await Assert.That(executor.TryResolveQuestRecoveryPosition(actor.Character, target, out _)).IsFalse();
            await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(start);
        }
        finally { template.CellX = cells; }
    }

    [Test]
    [Arguments(0f, 2f, true)]
    [Arguments(5f, 0f, false)]
    public async Task QuestRecoveryFailureCounter_DistinguishesSidewaysDisplacementFromApproach(float dx, float dy, bool shouldRecover)
    {
        var (actor, _, executor, steps, bot, clock, _, _) = CreateRig("recovery-approach", groundHeight: 1);
        SeedRecoveryZone(actor);
        var start = Origin + new Vector3(0, 0, 1);
        GameplayActorTestRig.SetPosition(actor, start);
        SeedNpcSource(actor, start + new Vector3(60, 0, 0));
        actor.NoProgressWindow = TimeSpan.FromMilliseconds(200);
        await Step(steps, bot, clock);
        await Step(steps, bot, clock);
        var state = executor.GetBotState(bot.CharacterId)!;
        _ = FailQuestMove(actor, state, start);
        await Assert.That(executor.HandleStuckQuestTravel(bot, state, clock.GetUtcNow().UtcDateTime)).IsFalse();
        var moved = start + new Vector3(dx, dy, 0);
        GameplayActorTestRig.SetPosition(actor, moved);
        _ = IssueAndFailQuestMove(executor, actor, state, moved);
        await Assert.That(executor.HandleStuckQuestTravel(bot, state, clock.GetUtcNow().UtcDateTime)).IsEqualTo(shouldRecover);
        await Assert.That(state.LastQuestStuckRecoveryUtc.HasValue).IsEqualTo(shouldRecover);
        actor.PreemptCurrent("approach control teardown");
    }

    [Test]
    public async Task QuestRecovery_InterruptedMoveDoesNotCountAsStuckFailure()
    {
        var (actor, module, executor, steps, bot, clock, _, _) = CreateRig("recovery-interrupted", groundHeight: 1);
        SeedRecoveryZone(actor);
        var start = Origin + new Vector3(0, 0, 1);
        GameplayActorTestRig.SetPosition(actor, start);
        SeedNpcSource(actor, start + new Vector3(80, 0, 0));
        await Step(steps, bot, clock);
        await Step(steps, bot, clock);
        var state = executor.GetBotState(bot.CharacterId)!;
        var move = actor.ActiveRequest!;
        await Assert.That(actor.Interrupt(move.TraceId)).IsTrue();
        await Assert.That(executor.HandleStuckQuestTravel(bot, state, clock.GetUtcNow().UtcDateTime)).IsFalse();
        await Assert.That(state.LastQuestStuckRecoveryUtc).IsNull();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == move.TraceId)).IsEqualTo(1);
    }

    private static ActorRequest FailQuestMove(GameplayActor actor, BotRoamStepExecutor.BotRoamState state, Vector3 pinned)
    {
        var request = state.PendingLeg!;
        for (var tick = 0; tick < 16 && !request.IsTerminal; tick++)
        {
            GameplayActorTestRig.SetPosition(actor, pinned);
            actor.Tick(TimeSpan.FromMilliseconds(200));
        }
        if (request.State != ActorLifecycleState.TimedOut || request.Failure != ActorFailureReason.Navigation)
            throw new InvalidOperationException("fixture did not produce an ordinary stuck Navigation terminal");
        return request;
    }

    private static ActorRequest IssueAndFailQuestMove(BotRoamStepExecutor executor, GameplayActor actor,
        BotRoamStepExecutor.BotRoamState state, Vector3 pinned)
    {
        // Component seam: ordinary owned Move dispatcher/actor tick, not a second scheduler.
        state.PendingLeg = (ActorRequest)typeof(BotRoamStepExecutor)
            .GetMethod("IssueRouteMove", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(executor, [actor, state, state.Path!.CurrentTarget])!;
        return FailQuestMove(actor, state, pinned);
    }

    [Test]
    public async Task FarFirstPreySource_TravelChoosesNearest_RegardlessOfSpawnerOrder()
    {
        var (actor, module, executor, steps, bot, clock, _, _) = CreateRig("owner-source-order");
        var near = Origin + new Vector3(80, 0, 0);
        var far = Origin + new Vector3(1400, 0, 0);
        SeedNpcSources(actor, far, near);
        await Step(steps, bot, clock);
        try
        {
            await Step(steps, bot, clock);
            await Assert.That(actor.ActiveRequest is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsTrue();
            await Assert.That(executor.GetRoamRoute(bot.CharacterId)!.Waypoints.Last()).IsEqualTo(near);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            var first = actor.ActiveRequest!;
            await Assert.That(actor.Interrupt(first.TraceId)).IsTrue();
            SeedNpcSources(actor, near, far);
            await Step(steps, bot, clock);
            await Step(steps, bot, clock);
            await Assert.That(executor.GetRoamRoute(bot.CharacterId)!.Waypoints.Last()).IsEqualTo(near);
            await Assert.That(actor.ActiveRequest!.TraceId).IsNotEqualTo(first.TraceId);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Assert.That(actor.Character.Quests.ActiveQuests[FirstQuest].Objectives[0]).IsEqualTo(0);
        }
        finally
        {
            actor.PreemptCurrent("source-order regression teardown");
        }
    }

    [Test]
    public async Task LivingQuestPreyBeyondInteractionRange_IsSelectedBeforeRemoteSpawnTravel()
    {
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-prey-scan");
        SeedNpcSources(actor, Origin + new Vector3(1400, 0, 0));
        await Step(steps, bot, clock);
        var far = SpawnPrey(session, Origin + new Vector3(70, 0, 0));
        var near = SpawnExtraPrey(session, Origin + new Vector3(55, 0, 0));
        try
        {
            var snapshot = BotObservedContext.Capture(actor);
            await Assert.That(snapshot.NearbyNpcObjIds.Contains(near)).IsFalse();
            var row = QuestFixtureRow.FromQuestData(FirstQuest);
            var selected = QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "scan-regression", row);
            await Assert.That(selected.SelectedObjId).IsEqualTo(near);
            await Assert.That(selected.SelectedObjId).IsNotEqualTo(far);
            await Step(steps, bot, clock);
            await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(near);
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Assert.That(actor.ActiveRequest is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsFalse();
        }
        finally
        {
            actor.PreemptCurrent("prey-scan regression teardown");
        }
    }

    [Test]
    [Arguments("dead")]
    [Arguments("hidden")]
    [Arguments("wrong-template")]
    [Arguments("friendly")]
    [Arguments("outside-scan")]
    public async Task UnsuitablePrey_DoesNotSuppressNearestSpawnTravel(string reason)
    {
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-prey-" + reason);
        var source = Origin + new Vector3(200, 0, 0);
        SeedNpcSources(actor, Origin + new Vector3(1400, 0, 0), source);
        await Step(steps, bot, clock);
        var prey = SpawnPrey(session, Origin + new Vector3(reason == "outside-scan" ? 101 : 55, 0, 0));
        var npc = session.World.GetNpc(prey)!;
        switch (reason)
        {
            case "dead": npc.Hp = 0; break;
            case "hidden": npc.IsVisible = false; break;
            case "wrong-template": npc.TemplateId = 97_999; break;
            case "friendly":
                actor.Character.Faction = null;
                npc.Faction = new SystemFaction { Id = FactionsEnum.Friendly };
                await Assert.That(CombatDecisionTree.IsHostileTarget(actor.Character, npc)).IsFalse();
                break;
        }
        try
        {
            var snapshot = BotObservedContext.Capture(actor);
            var row = QuestFixtureRow.FromQuestData(FirstQuest);
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "negative", row).SelectedObjId).IsEqualTo(0u);
            await Assert.That(QuestBehavior.HasPerceivedLivingPrey(actor, snapshot, Boar3475)).IsFalse();
            await Step(steps, bot, clock);
            await Assert.That(executor.GetRoamRoute(bot.CharacterId)!.Waypoints.Last()).IsEqualTo(source);
            await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo("QUEST_TRAVEL");
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Assert.That(actor.Character.Quests.ActiveQuests[FirstQuest].Objectives[0]).IsEqualTo(0);
        }
        finally
        {
            actor.PreemptCurrent("unsuitable-prey regression teardown");
        }
    }

    [Test]
    public async Task WiderLivingScan_IsBoundedAndCopied_WithoutWideningInteractionOrCorpseCensus()
    {
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-scan-bounds");
        var boundary = SpawnPrey(session, Origin + new Vector3(99, 0, 0));
        var edge = SpawnExtraPrey(session, Origin + new Vector3(100, 0, 0));
        var outside = SpawnExtraPrey(session, Origin + new Vector3(101, 0, 0));
        var dead = SpawnExtraPrey(session, Origin + new Vector3(55, 0, 0));
        session.World.GetNpc(dead)!.Hp = 0;
        await Assert.That(actor.Observe().NearbyLivingNpcObjIds).IsEmpty();
        await Step(steps, bot, clock);
        var observation = actor.Observe();
        await Assert.That(observation.NearbyLivingNpcObjIds.Contains(boundary)).IsTrue();
        await Assert.That(observation.NearbyLivingNpcObjIds.Contains(outside)).IsFalse();
        await Assert.That(observation.NearbyLivingNpcObjIds.Contains(edge)).IsFalse();
        await Assert.That(observation.NearbyLivingNpcObjIds.Contains(dead)).IsFalse();
        await Assert.That(observation.NearbyNpcObjIds.Contains(boundary)).IsFalse();
        await Assert.That(observation.NearbyNpcObjIds.Contains(dead)).IsFalse();
        var snapshot = BotObservedContext.From(observation);
        ((List<uint>)observation.NearbyLivingNpcObjIds).Clear();
        await Assert.That(snapshot.NearbyLivingNpcObjIds.Contains(boundary)).IsTrue();
        var row = QuestFixtureRow.FromQuestData(FirstQuest);
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "boundary", row).SelectedObjId).IsEqualTo(boundary);
        GameplayActorTestRig.SetNpcPosition(session, boundary, Origin + new Vector3(101, 0, 0));
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "moved-out", row).SelectedObjId).IsEqualTo(0u);
        await Assert.That(QuestBehavior.HasPerceivedLivingPrey(actor, snapshot, Boar3475)).IsFalse();
    }

    [Test]
    public async Task ValidCommittedPrey_IsRetainedUntilInvalid_ThenNearestSuitablePreyWins()
    {
        var (actor, _, _, steps, bot, clock, _, session) = CreateRig("owner-scan-commitment");
        await Step(steps, bot, clock);
        var farther = SpawnPrey(session, Origin + new Vector3(70, 0, 0));
        var nearer = SpawnExtraPrey(session, Origin + new Vector3(55, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(farther);
        var row = QuestFixtureRow.FromQuestData(FirstQuest);
        var snapshot = BotObservedContext.Capture(actor);
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "commit", row).SelectedObjId).IsEqualTo(farther);
        session.World.GetNpc(farther)!.Hp = 0;
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character, snapshot, "reselect", row).SelectedObjId).IsEqualTo(nearer);
    }

    [Test]
    public async Task SpawnFallback_RejectsInvalidAndWrongTemplates_UsesStableTieAndOnlyOwnWorld()
    {
        var (actor, _, _, _, _, _, _, _) = CreateRig("owner-spawn-controls");
        var left = Origin + new Vector3(-80, 0, 0);
        var right = Origin + new Vector3(80, 0, 0);
        SeedNpcSources(actor, new Vector3(float.NaN, 0, 0), right, left,
            new Vector3(0, 0, float.PositiveInfinity));
        var world = actor.Character.ParentWorld!;
        var sources = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        sources[97_301] = [new NpcSpawner { Id = 97_301, UnitId = 97_999,
            Position = new WorldSpawnPosition { X = Origin.X + 1, Y = Origin.Y, Z = 0 } }];
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, Boar3475, Origin)).IsEqualTo((Vector3?)left);
        sources[97_300].Reverse();
        // This test edits the spawner lists in place through reflection, which
        // the game never does (it only adds groups or clears the table), so it
        // tells the bots' spawner index to rebuild.
        BotSpawnerIndex.Invalidate();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, Boar3475, Origin)).IsEqualTo((Vector3?)left);
        // The reporter's legacy first-match lookup is deliberately unchanged.
        await Assert.That(QuestBehavior.TryNpcSpawnerPosition(world, 97_999)).IsEqualTo((Vector3?)(Origin + new Vector3(1, 0, 0)));
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 0, Origin)).IsNull();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, Boar3475, new Vector3(float.NaN, 0, 0))).IsNull();
        var (other, _) = GameplayActorTestRig.CreateActor("other-world-source");
        SeedNpcSources(other, Origin + new Vector3(1, 0, 0));
        sources[97_300].Clear();
        BotSpawnerIndex.Invalidate();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, Boar3475, Origin)).IsNull();
        SeedNpcSources(actor, new Vector3(float.MaxValue, 0, 0));
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, Boar3475, Origin)).IsNull();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(other.Character.ParentWorld!, Boar3475, Origin)).IsNotNull();
    }

    [Test]
    public async Task MissingNpcGatherSource_AdvanceStillEntersBelowBudget()
    {
        var (actor, module, executor, steps, bot, clock, _, _) = CreateRig("owner-source-missing");
        await Step(steps, bot, clock);
        await Step(steps, bot, clock);
        await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var runtime)).IsTrue();
        await Assert.That(runtime!.LastResult!.LegEvidence.Single(e => e.Leg == QuestLegId.Advance).Entered).IsTrue();
        await Assert.That(runtime.LastResult.GiveUps).IsEmpty();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsFalse();
    }

    [Test]
    public async Task PerceivedLivingPrey_WithRemoteSource_KeepsObjectiveLegOwnership()
    {
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-source-perceived");
        SeedNpcSource(actor, Origin + new Vector3(80, 0, 0));
        await Step(steps, bot, clock);
        SpawnPrey(session);
        try
        {
            await Step(steps, bot, clock);
            await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var runtime)).IsTrue();
            await Assert.That(runtime!.LastResult!.LegEvidence.Single(e => e.Leg == QuestLegId.Advance).Entered).IsTrue();
            await Assert.That(runtime.LastResult.GiveUps).IsEmpty();
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Assert.That(actor.ActiveRequest is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsFalse();
        }
        finally
        {
            actor.PreemptCurrent("perceived-source regression teardown");
        }
    }

    [Test]
    public async Task HybridPerceivedGrantingDoodad_WithRemotePrey_LeavesAdvanceAllowedAndTravelIdle()
    {
        // Synthetic additive Doodad source for the existing NPC-loot item.
        // Both sources derive from actual seeded mappings; the Doodad joins
        // the region census. No fixture row or plan classification is overridden.
        // A hybrid still derives the ordinary KillX plan. This control proves
        // existing Advance/travel ownership only, not production Gather selection.
        const uint doodadTemplate = 97_411;
        const uint doodadGroup = 97_412;
        const uint doodadFunc = 97_413;
        var (actor, module, executor, steps, bot, clock, _, session) = CreateRig("owner-source-hybrid");
        SeedNpcSource(actor, Origin + new Vector3(80, 0, 0));
        GameplayActorTestRig.SeedDoodadLootInteraction(doodadGroup, doodadFunc, Meat4058);
        var doodad = GameplayActorTestRig.SpawnGatherDoodad(session, doodadTemplate,
            doodadGroup, doodadFunc, Meat4058, Origin + new Vector3(2, 0, 0));
        QuestFixtureRow.InvalidateAll();
        try
        {
            await Step(steps, bot, clock);
            var row = QuestFixtureRow.FromQuestData(FirstQuest);
            await Assert.That(row.PreyTemplate).IsEqualTo(Boar3475);
            await Assert.That(row.GatherDoodadTemplates.Contains(doodadTemplate)).IsTrue();
            var context = new QuestLegContext(actor, new QuestDecisionScenario.QuestOptions(),
                FirstQuest, row, null) { Observation = BotObservedContext.Capture(actor) };
            await Assert.That(QuestBehavior.ResolveGatherDoodad(context, row).ObjId).IsEqualTo(doodad);
            await Assert.That(QuestBehavior.AdvanceEnter(context, new QuestLegWake())).IsNull();
            await Step(steps, bot, clock);
            await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var runtime)).IsTrue();
            await Assert.That(runtime!.LastResult!.LegEvidence.Single(e => e.Leg == QuestLegId.Advance).Entered).IsTrue();
            await Assert.That(runtime.LastResult.GiveUps).IsEmpty();
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(FirstQuest);
            await Assert.That(actor.ActiveRequest is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsFalse();
        }
        finally
        {
            actor.PreemptCurrent("hybrid-source regression teardown");
            // Remove only this test's item-source inverse entry so later
            // NPC-only fixtures still derive no granting Doodad for item4058.
            var templates = (System.Collections.IDictionary)typeof(DoodadManager)
                .GetField("_funcTemplates", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(DoodadManager.Instance)!;
            var lootTemplates = (System.Collections.IDictionary)templates["DoodadFuncLootItem"]!;
            lootTemplates.Remove(doodadFunc);
            QuestFixtureRow.InvalidateAll();
        }
    }

    [Test]
    public async Task RepeatedIdenticalOwnLegFailure_AtTheBound_AbandonsTheQuest_AndTheBandMovesOn()
    {
        // The quest module acts on the outcome the executor itself reported
        // through T4's channel (LastLegOutcome): the same leg failing the same
        // way three times makes it abandon that quest with an observable reason
        // instead of re-electing the same dead-end wake. The rig rejects the
        // request the executor issued (the idiom BotLegOutcomePublishTests uses);
        // the outcome is still built by the real executor from the real leg's
        // terminal state, and no verdict is injected.
        var rig = CreateRigCore("owner-leg-giveup");
        SeedNpcSources(rig.Actor, Origin + new Vector3(80, 0, 0));
        await Step(rig.Steps, rig.Bot, rig.Clock);
        await Step(rig.Steps, rig.Bot, rig.Clock);
        await Assert.That(rig.Module.GetChosenQuest(rig.Bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(rig.Actor.ActiveRequest is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsTrue();

        var rejected = 0;
        var chosenAfterFailure = new List<uint>();
        var armingReason = (string?)null;
        for (var wake = 0; wake < 16 && armingReason == null; wake++)
        {
            if (rig.Actor.ActiveRequest is { IsTerminal: false } request)
            {
                request.Reject(ActorFailureReason.Navigation, "no path to the waypoint");
                rejected++;
            }
            await Step(rig.Steps, rig.Bot, rig.Clock);
            chosenAfterFailure.Add(rig.Module.GetChosenQuest(rig.Bot.CharacterId));
            var reason = rig.Steps.GetLastDecision(rig.Bot.CharacterId)?.Reason;
            if (reason != null && reason.Contains("gave up on", StringComparison.Ordinal))
                armingReason = reason;
        }

        await Assert.That(rejected).IsGreaterThanOrEqualTo(3);
        await Assert.That(armingReason).IsNotNull();
        // Bounded and observable: the reason names the count, the bound and the failure.
        await Assert.That(armingReason!).Contains("after 3 failed legs");
        await Assert.That(armingReason!).Contains("no path to the waypoint");
        await Assert.That(armingReason!).Contains(FirstQuest.ToString());
        // Below the bound the same quest stayed chosen - the give-up is not a
        // first-failure panic.
        await Assert.That(chosenAfterFailure.Take(2).All(id => id == FirstQuest)).IsTrue();
        await Assert.That(rig.Module.GetChosenQuest(rig.Bot.CharacterId)).IsNotEqualTo(FirstQuest);
        // The abandonment is a purpose decision, not a quest deletion: the quest
        // record stays exactly as the world gave it.
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(FirstQuest)).IsTrue();

        // The band moves on: another quest is taken rather than the same dead end,
        // and the abandoned quest is never chosen again.
        for (var wake = 0; wake < 6 && rig.Module.GetChosenQuest(rig.Bot.CharacterId) != SecondQuest; wake++)
        {
            await Step(rig.Steps, rig.Bot, rig.Clock);
            await Assert.That(rig.Module.GetChosenQuest(rig.Bot.CharacterId)).IsNotEqualTo(FirstQuest);
        }

        await Assert.That(rig.Module.GetChosenQuest(rig.Bot.CharacterId)).IsEqualTo(SecondQuest);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuest)).IsTrue();
        await Assert.That(rig.Actor.Character.Quests.HasQuestCompleted(FirstQuest)).IsFalse();
    }

    [Test]
    public async Task DifferentLegFailureDetails_NeverAccumulateIntoAGiveUp()
    {
        // The bound counts the SAME failure repeating. A leg that fails a
        // different way each wake is five separate facts, so the quest stays
        // chosen and no give-up is reported - the policy cannot turn unrelated
        // transient failures into an abandonment.
        var rig = CreateRigCore("owner-leg-different-details");
        SeedNpcSources(rig.Actor, Origin + new Vector3(80, 0, 0));
        await Step(rig.Steps, rig.Bot, rig.Clock);
        await Step(rig.Steps, rig.Bot, rig.Clock);
        await Assert.That(rig.Module.GetChosenQuest(rig.Bot.CharacterId)).IsEqualTo(FirstQuest);
        await Assert.That(rig.Actor.ActiveRequest is { IsTerminal: false, MoveOwner: "QUEST_TRAVEL" }).IsTrue();

        var rejected = 0;
        // Five refusals, each with its own detail: well under the quest runtime's
        // own ten-wake unresolved-target budget, so only this policy could give up.
        for (var wake = 0; wake < 5; wake++)
        {
            if (rig.Actor.ActiveRequest is { IsTerminal: false } request)
                request.Reject(ActorFailureReason.Navigation, $"no path to waypoint {++rejected}");
            await Step(rig.Steps, rig.Bot, rig.Clock);
            var reason = rig.Steps.GetLastDecision(rig.Bot.CharacterId)?.Reason ?? "";
            await Assert.That(reason.Contains("gave up on", StringComparison.Ordinal)).IsFalse();
        }

        await Assert.That(rejected).IsGreaterThanOrEqualTo(4);
        await Assert.That(rig.Module.GetChosenQuest(rig.Bot.CharacterId)).IsEqualTo(FirstQuest);
    }

    [Test]
    public async Task AbandonedQuest_IsDeferredThroughTheExistingOwner_AndReleasedWhenItsFactsChange()
    {
        // The give-up hands the abandoned ACCEPTED quest to the module's existing
        // deferral owner - the same owner the quest runtime's own give-up verdicts
        // use - so the quest's OWN facts decide when it may be tried again, and a
        // quest with no Quest record is the only thing remembered per generation.
        // This is the mechanism the two tests above cannot see (both mechanisms
        // produce the same election), so it is asserted on the deferral state and
        // on the release firing when the quest's facts change.
        var rig = CreateRigCore("owner-leg-giveup-deferral");
        SeedNpcSources(rig.Actor, Origin + new Vector3(80, 0, 0));
        await Step(rig.Steps, rig.Bot, rig.Clock);
        await Step(rig.Steps, rig.Bot, rig.Clock);

        var armingReason = (string?)null;
        for (var wake = 0; wake < 16 && armingReason == null; wake++)
        {
            if (rig.Actor.ActiveRequest is { IsTerminal: false } request)
                request.Reject(ActorFailureReason.Navigation, "no path to the waypoint");
            await Step(rig.Steps, rig.Bot, rig.Clock);
            var reason = rig.Steps.GetLastDecision(rig.Bot.CharacterId)?.Reason;
            if (reason != null && reason.Contains("gave up on", StringComparison.Ordinal))
                armingReason = reason;
        }

        await Assert.That(armingReason).IsNotNull();
        // The deferral owner recorded the give-up for the retained quest, with the
        // module's own verdict as its reason.
        var deferred = DeferredQuests(rig.Module, rig.Bot.CharacterId);
        await Assert.That(deferred.ContainsKey(FirstQuest)).IsTrue();
        await Assert.That(deferred[FirstQuest]).Contains("GIVE-UP/LEG-FAILED");
        // An accepted quest is the deferral owner's business, NOT the generation-scoped
        // block: if it also landed there, the release above would be invisible and the
        // quest would stay unpickable for the rest of the generation.
        await Assert.That(GenerationAbandoned(rig.Module, rig.Bot.CharacterId).Contains(FirstQuest)).IsFalse();

        // The release rule is the quest's own facts, not the bot's generation: one
        // objective changing releases the deferral on the next election, so the
        // quest can be tried again without waiting for a lifecycle change.
        rig.Actor.Character.Quests.ActiveQuests[FirstQuest].Objectives[0] = 1;
        await Step(rig.Steps, rig.Bot, rig.Clock);
        await Assert.That(DeferredQuests(rig.Module, rig.Bot.CharacterId).ContainsKey(FirstQuest)).IsFalse();
        await Assert.That(GenerationAbandoned(rig.Module, rig.Bot.CharacterId).Contains(FirstQuest)).IsFalse();
    }

    /// <summary>
    /// Reads the module's generation-scoped abandonment set for one bot: the quest
    /// ids given up on with no <c>Quest</c> record to defer (white-box, like the
    /// deferral read above).
    /// </summary>
    private static HashSet<uint> GenerationAbandoned(QuestBootstrapActivityModule module, uint characterId)
    {
        var field = typeof(QuestBootstrapActivityModule)
            .GetField("_legAbandoned", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var abandoned = (System.Collections.IDictionary)field.GetValue(module)!;
        if (!abandoned.Contains(characterId))
            return [];
        var perBot = abandoned[characterId]!;
        var quests = (System.Collections.IEnumerable)perBot.GetType()
            .GetProperty("Quests")!.GetValue(perBot)!;
        var result = new HashSet<uint>();
        foreach (var quest in quests)
            result.Add((uint)quest);

        return result;
    }

    /// <summary>
    /// Reads the module's deferral owner for one bot (white-box, like the executor
    /// state reads this rig already does): quest id to the give-up reason recorded
    /// with it.
    /// </summary>
    private static Dictionary<uint, string> DeferredQuests(QuestBootstrapActivityModule module, uint characterId)
    {
        var field = typeof(QuestBootstrapActivityModule)
            .GetField("_deferrals", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var deferrals = (System.Collections.IDictionary)field.GetValue(module)!;
        var result = new Dictionary<uint, string>();
        if (!deferrals.Contains(characterId))
            return result;
        var perBot = deferrals[characterId]!;
        var quests = (System.Collections.IEnumerable)perBot.GetType()
            .GetProperty("Quests")!.GetValue(perBot)!;
        foreach (var pair in quests)
        {
            var pairType = pair.GetType();
            var key = (uint)pairType.GetProperty("Key")!.GetValue(pair)!;
            var deferredQuest = pairType.GetProperty("Value")!.GetValue(pair)!;
            var outcome = deferredQuest.GetType().GetProperty("Outcome")!.GetValue(deferredQuest)!;
            result[key] = (string)outcome.GetType().GetProperty("Reason")!.GetValue(outcome)!;
        }

        return result;
    }

    private static void SeedNpcSource(GameplayActor actor, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        const uint sourceId = 97_300;
        spawners[sourceId] = [new NpcSpawner
        {
            Id = sourceId, UnitId = Boar3475,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z }
        }];
    }

    private static void SeedNpcSources(GameplayActor actor, params Vector3[] positions)
    {
        SeedNpcSource(actor, positions[0]);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(actor.Character.ParentWorld!.SpawnManager)!;
        spawners[97_300] = positions.Select((p, i) => new NpcSpawner
        {
            Id = (uint)(97_300 + i), UnitId = Boar3475,
            Position = new WorldSpawnPosition { X = p.X, Y = p.Y, Z = p.Z }
        }).ToList();
        BotSpawnerIndex.Invalidate(); // overwrites a group in place (see the spawn fallback test)
    }

    private static uint SpawnPrey(HeadlessSession session, Vector3? at = null)
    {
        var objId = session.SpawnNpc(Boar3475);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        npc.Hp = npc.MaxHp = 100;
        npc.IsVisible = true;
        var position = at ?? Origin + new Vector3(20, 0, 0);
        GameplayActorTestRig.SetNpcPosition(session, objId, position);
        var region = session.World.GetRegionByPos(position)!;
        region.AddObject(npc);
        npc.Region = region;
        return objId;
    }

    // SpawnNpc deduplicates by template; use the existing multiple-candidate
    // rig pattern for a distinct second NPC, never overwrite the first one.
    private static uint SpawnExtraPrey(HeadlessSession session, Vector3 position)
    {
        var npc = new Npc
        {
            ObjId = s_extraNpcId++, TemplateId = Boar3475,
            Template = new NpcTemplate { Id = Boar3475, Scale = 1f },
            Hp = 100, MaxHp = 100, IsVisible = true
        };
        npc.Transform.Local.SetPosition(position);
        session.World.AddObject(npc);
        var region = session.World.GetRegionByPos(position)!;
        region.AddObject(npc);
        npc.Region = region;
        return npc.ObjId;
    }

    private void SeedRecoveryZone(GameplayActor actor)
    {
        // Explicit fixture zone grid and dry nonzero ground. No live map alteration.
        var template = actor.Character.ParentWorld.Template;
        _recoveryWorldTemplate = template;
        _previousRecoveryZones = template.ZoneKeyByRegions;
        var zones = new uint[template.CellX * WorldManager.SECTORS_PER_CELL,
            template.CellY * WorldManager.SECTORS_PER_CELL];
        for (var x = 0; x < zones.GetLength(0); x++)
        for (var y = 0; y < zones.GetLength(1); y++)
            zones[x, y] = 7;
        template.ZoneKeyByRegions = zones;
        typeof(Transform).GetField("_zoneId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, 7u);
    }

    private static async Task Step(BotGoalArbiterStepExecutor steps, PlayerBotRuntime bot, FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await steps.StepAsync(bot, CancellationToken.None);
    }

    /// <summary>Full rig identity, including the arbiter the projection drops.</summary>
    private sealed record QuestRig(GameplayActor Actor, QuestBootstrapActivityModule Module,
        BotRoamStepExecutor Executor, BotGoalArbiter Arbiter, BotGoalArbiterStepExecutor Steps,
        PlayerBotRuntime Bot, FakeTimeProvider Clock, uint GiverObjId, HeadlessSession Session);

    private static (GameplayActor Actor, QuestBootstrapActivityModule Module, BotRoamStepExecutor Executor,
        BotGoalArbiterStepExecutor Steps, PlayerBotRuntime Bot, FakeTimeProvider Clock, uint GiverObjId, HeadlessSession Session) CreateRig(string name, bool eligibleFollowUp = true, float groundHeight = 0f, Func<Vector3, uint, float>? groundProvider = null, bool gatherFollowUp = false)
    {
        var rig = CreateRigCore(name, eligibleFollowUp, groundHeight, groundProvider, gatherFollowUp);
        return (rig.Actor, rig.Module, rig.Executor, rig.Steps, rig.Bot, rig.Clock, rig.GiverObjId, rig.Session);
    }

    private static QuestRig CreateRigCore(string name, bool eligibleFollowUp = true, float groundHeight = 0f,
        Func<Vector3, uint, float>? groundProvider = null, bool gatherFollowUp = false)
    {
        GameplayActorTestRig.SeedQuestHunt(FirstQuest, 97_201, 97_202, 97_203, Giver, Boar3475, Giver, level: 1);
        var component = QuestManager.Instance.GetTemplate(FirstQuest)!.GetComponents(QuestComponentKind.Progress).Single();
        component.ActTemplates.Clear();
        var gather = new QuestActObjItemGather(component)
        {
            ActId = 97_202, DetailId = 97_202, DetailType = nameof(QuestActObjItemGather),
            ItemId = Meat4058, Count = 3, ThisComponentObjectiveIndex = 0
        };
        component.ActTemplates.Add(gather);
        var acts = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
            .GetField("_actTemplatesByDetailType", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(QuestManager.Instance)!;
        if (!acts.TryGetValue(nameof(QuestActObjItemGather), out var gathers))
            acts[nameof(QuestActObjItemGather)] = gathers = [];
        gathers[gather.DetailId] = gather;
        GameplayActorTestRig.SeedQuestHunt(SecondQuest, 97_204, 97_205, 97_206, Giver, Boar3475, Giver, level: 1);
        if (gatherFollowUp)
        {
            var next = QuestManager.Instance.GetTemplate(SecondQuest)!.GetComponents(QuestComponentKind.Progress).Single();
            next.ActTemplates.Clear();
            var nextGather = new QuestActObjItemGather(next)
            {
                ActId = 97_205, DetailId = 97_205, DetailType = nameof(QuestActObjItemGather),
                ItemId = Meat4058, Count = 3, ThisComponentObjectiveIndex = 0
            };
            next.ActTemplates.Add(nextGather);
            gathers[nextGather.DetailId] = nextGather;
        }
        GameplayActorTestRig.SeedQuestComponentLevelRequirement(97_204, eligibleFollowUp ? 1u : 20u);
        GameplayActorTestRig.SeedQuestHunt(IneligibleQuest, 97_207, 97_208, 97_209, Giver, Boar3475, Giver, level: 20);
        GameplayActorTestRig.SeedQuestComponentLevelRequirement(97_207, 20);
        QuestFixtureRow.InvalidateAll();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 3;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        GameplayActorTestRig.SetPosition(actor, Origin);
        var region = session.World.GetRegionByPos(Origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        var giver = session.SpawnNpc(Giver);
        var npc = session.World.GetNpc(giver)!;
        npc.Template = new NpcTemplate { Id = Giver, Scale = 1f };
        npc.Hp = npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, giver, Origin + new Vector3(2, 0, 0));
        region.AddObject(npc);
        npc.Region = region;
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var arbiter = new BotGoalArbiter([module]);
        var clock = new FakeTimeProvider();
        var executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor, TimeProvider = clock, ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false, EnableWildlifeButcher = false, GroundHeightProvider = groundProvider ?? ((_, _) => groundHeight),
            ActiveActivityProvider = arbiter.GetActiveActivity, NearbyDoodadProvider = (_, _) => [],
            QuestChosenProvider = module.GetChosenQuest, QuestTerminalSink = module.NotifyQuestTerminal,
            QuestGiveUpSink = module.NotifyQuestGiveUp,
            // Production parity (Program.cs): the executor's leg outcome goes back
            // to the arbiter, which hands it to the module that owned the activity.
            LegOutcomeSink = arbiter.ReportLegOutcome
        };
        executor.GetOrCreateActor(actor.Character);
        QuestLegActor.SetResolver(executor.TryGetActor);
        return new QuestRig(actor, module, executor, arbiter, new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f),
            new PlayerBotRuntime(actor.Character, name), clock, giver, session);
    }

    private static void SeedBoarLootLink()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || rows.All(r => r.LootPackId != Pack4530))
        {
            rows ??= [];
            rows.Add(new AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc
            {
                Id = Boar3475,
                NpcId = Boar3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Boar3475] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, AAEmu.Game.Models.Game.Items.Loots.LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(Pack4530, out var pack) || pack.Loots.All(l => l.ItemId != Meat4058))
        {
            var loot = new AAEmu.Game.Models.Game.Items.Loots.Loot
            {
                Id = Pack4530,
                Group = 0,
                ItemId = Meat4058,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = Pack4530,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[Pack4530] = new AAEmu.Game.Models.Game.Items.Loots.LootPack
                {
                    Id = Pack4530,
                    Loots = [loot],
                    LootsByGroupNo = new Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.Loot>> { [0] = [loot] },
                    Groups = [],
                    ActabilityGroups = [],
                    GroupCount = 1,
                };
            }
            else
            {
                pack.Loots.Add(loot);
                if (pack.LootsByGroupNo.TryGetValue(0, out var group))
                    group.Add(loot);
                else
                    pack.LootsByGroupNo[0] = [loot];
            }
        }

        QuestFixtureRow.InvalidateAll();
    }
}
