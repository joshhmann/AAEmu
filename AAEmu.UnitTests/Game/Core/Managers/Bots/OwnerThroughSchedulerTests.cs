using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Owner + listing + running-hold THROUGH the real scheduler. Closes the gap
/// between QuestGoalOwnerTests (owner→listing→hold via direct executor calls,
/// never scheduler Wake/scan) and BotDecisionVerticalTests (scheduler→executor
/// but never the listing/goal-owner module):
///
///   scheduler Wake → BotGoalArbiterStepExecutor (the PRODUCTION
///   IBotStepExecutor binding, Program.cs) → BotGoalArbiter (owner module
///   picks from the live listing) → BotRoamStepExecutor quest leg (real
///   accept/dispatch/hold on the real headless actor).
///
/// No direct StepAsync/CanActivate calls anywhere below — every wake is a
/// scheduler Wake + RunScanCycle + DrainTickQueue pump on FakeTime. The
/// counting wrapper only counts wakes; every step delegates to production.
///
/// Seeding mirrors QuestGoalOwnerTests (SeedQuestHunt owner shape) PLUS the
/// pursuit execution shape (Progress QuestActObjItemGather + npc→pack→item
/// loot link, the QuestGoalOwnerTests.SeedPursuitShape precedent): the owner
/// picks from the eligible-offer channel, the leg dispatches the pursuit Move
/// on the gather hunt channel once accepted.
/// </summary>
[NotInParallel]
public class OwnerThroughSchedulerTests
{
    private static readonly Vector3 SpawnPos = new(32, 32, 0);
    private const uint OfferNpcTemplate = 91_780;
    private const uint PreyTemplate = 91_781;
    private const uint ReportNpcTemplate = 91_783;
    private const uint OpenQuestId = 91_791;
    private const uint SecondQuestId = 91_792;
    private const uint OpenStartComponent = 91_795;
    private const uint OpenProgressComponent = 91_796;
    private const uint OpenReadyComponent = 91_797;
    private const uint SecondStartComponent = 91_798;
    private const uint SecondProgressComponent = 91_799;
    private const uint SecondReadyComponent = 91_800;
    private const uint ThirdQuestId = 91_793;
    private const uint ThirdStartComponent = 91_803;
    private const uint ThirdProgressComponent = 91_804;
    private const uint ThirdReadyComponent = 91_805;
    private const uint GatherItemId = 91_811;
    private const uint GatherPackId = 91_812;
    private const uint JourneyQuestId = 92_360;
    private const uint JourneyStartComponent = 92_361;
    private const uint JourneyProgressComponent = 92_362;
    private const uint JourneyReadyComponent = 92_363;
    private const uint JourneyOfferNpc = 92_364;
    private const uint JourneyTalkNpc = 92_365;
    private const uint JourneyReportNpc = 92_366;
    private const uint NextJourneyQuestId = 92_367;
    private const uint NextJourneyStartComponent = 92_368;
    private const uint NextJourneyReadyComponent = 92_370;
    private const uint NextJourneyOfferNpc = 92_371;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
    }

    [Test]
    public async Task EmptyLocalListing_DiscoversDistantOffer_ThroughScheduledWakes()
    {
        // SYNTHETIC INITIAL WORLD: the ordinary quest/NPC templates and a real
        // world SpawnManager entry are seeded 60m from the healthy bot. No
        // quest, route, selected purpose, movement result or credit is staged.
        using var rig = CreateRig("owner-sched-discovery", seedSecondQuest: false,
            materializeActorState: false, distantOfferDistance: 60f);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests).IsEmpty();
        await Assert.That(rig.Executor.TryGetActor(rig.BotId)).IsNull();
        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(0u);

        // One initial wake only. Every subsequent wake must come from the
        // production executor's returned cadence, including cold-start idle.
        await Assert.That(rig.Scheduler.Wake(rig.BotId)).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();

        // Failing-before admission boundary: an empty local listing currently
        // never reaches the existing distant-offerer travel resolver.
        await Assert.That(rig.Actor.ActiveRequest).IsNotNull();
        var discoveryMove = rig.Actor.ActiveRequest!;
        await Assert.That(discoveryMove.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(discoveryMove.IsTerminal).IsFalse();
        await Assert.That(rig.Actor.Character.Transform.World.Position.X).IsGreaterThan(SpawnPos.X);
        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(0u);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests).IsEmpty();

        await rig.PumpScheduledWakeAsync();
        await Assert.That(rig.Actor.ActiveRequest).IsSameReferenceAs(discoveryMove);

        // Real actor ticks walk into perception; the actual arbiter picks and
        // the ordinary engine acceptance path runs. No helper dispatch, further
        // Wake call, teleport, or manually supplied quest credit is used.
        for (var wake = 0; wake < 500 &&
             !rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId); wake++)
            await rig.PumpScheduledWakeAsync();

        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(OpenQuestId);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();
        await Assert.That(rig.Actor.AuditTrace.Any(row =>
            row.Action == ActorActionType.AcceptQuest &&
            row.TargetId == OpenQuestId &&
            row.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task OwnerPick_FlowsThroughSchedulerWakes_HoldKeepsRequest()
    {
        using var rig = CreateRig("owner-sched-pick", seedSecondQuest: false);
        var botId = rig.BotId;

        // Wake 1 through the scheduler: the arbiter runs the owner module, the
        // module picks from the live listing, the quest leg accepts for real.
        await rig.PumpRoundAsync(1);
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(OpenQuestId);
        await Assert.That(rig.Arbiter.GetActiveActivity(botId))
            .IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(rig.Arbiter.TransitionCount).IsEqualTo(1);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();
        // The synchronous accept leaves no live request behind.
        await Assert.That(rig.Actor.ActiveRequest is not { IsTerminal: false }).IsTrue();

        // Later wakes through the scheduler: the leg dispatches the pick's
        // pursuit Move — pump until it is live (Target-then-Move takes an
        // extra wake; the bound absorbs it, the path stays scheduler-only).
        ActorRequest? held = null;
        var guard = 0;
        while (guard++ < 15)
        {
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
            if (rig.Actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move } live)
            {
                held = live;
                break;
            }
        }
        await Assert.That(held).IsNotNull();
        await Assert.That(rig.Executor.TryGetQuestRuntime(botId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(held)).IsTrue();
        var movesBefore = rig.Actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);

        // One more pumped wake: the running hold keeps the SAME request — no
        // re-selection, no re-dispatch, purpose and activity unchanged.
        await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Actor.ActiveRequest).IsSameReferenceAs(held);
        await Assert.That(questRuntime.OwnsRunningRequest(rig.Actor.ActiveRequest)).IsTrue();
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(OpenQuestId);
        await Assert.That(rig.Arbiter.GetActiveActivity(botId))
            .IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(rig.Arbiter.TransitionCount).IsEqualTo(1);
        await Assert.That(rig.Actor.AuditTrace.Count(r => r.Action == ActorActionType.Move))
            .IsEqualTo(movesBefore);
        await Assert.That(rig.Scheduler.GetMetrics().TotalStepsRun)
            .IsEqualTo((long)rig.Counting.CountStarts(botId));
    }

    [Test]
    public async Task OwnerPick_TerminalFeedsNextPick_ThroughScheduler()
    {
        using var rig = CreateRig("owner-sched-terminal", seedSecondQuest: true);
        var botId = rig.BotId;

        await rig.PumpRoundAsync(1);
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(OpenQuestId);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();

        ActorRequest? held = null;
        var guard = 0;
        while (guard++ < 15)
        {
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
            if (rig.Actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move } live)
            {
                held = live;
                break;
            }
        }
        await Assert.That(held).IsNotNull();

        // Complete the held request through the real actor path, then pump:
        // the exact terminal is consumed once and the refreshed listing feeds
        // subsequent work for the kept purpose.
        _ = rig.Actor.Stop();
        guard = 0;
        while (guard++ < 10 && rig.Actor.ActiveRequest is { IsTerminal: false })
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Actor.ActiveRequest is not { IsTerminal: false }).IsTrue();

        ActorRequest? next = null;
        guard = 0;
        while (guard++ < 15)
        {
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
            if (rig.Actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move } live
                && !ReferenceEquals(live, held))
            {
                next = live;
                break;
            }
        }
        await Assert.That(next).IsNotNull();
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(OpenQuestId);
        await Assert.That(rig.Arbiter.GetActiveActivity(botId))
            .IsEqualTo(QuestBootstrapActivityModule.ActivityName);
    }

    [Test]
    public async Task ThreeQuestChain_PicksAdvanceThroughTerminals()
    {
        using var rig = CreateRig("owner-sched-chain3", seedSecondQuest: true, seedThirdQuest: true);
        var botId = rig.BotId;
        var expected = new[] { OpenQuestId, SecondQuestId, ThirdQuestId };
        var picked = new List<uint>(capacity: 3);

        for (var step = 0; step < 3; step++)
        {
            // Pump to pick: the arbiter runs the owner module through the
            // scheduler until the next expected quest is chosen.
            var guard = 0;
            while (guard++ < 40 && rig.Module.GetChosenQuest(botId) != expected[step])
                await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
            await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(expected[step]);
            picked.Add(expected[step]);

            if (step == 0)
            {
                // Step 0 proves the full production chain: real accept, the
                // pick's pursuit Move live AND owned by the quest runtime
                // (a travel-fallback leg may hold the actor slot on early
                // wakes — the owned pursuit is the proof of dispatch), the
                // running hold keeping the SAME request, then the terminal
                // through the real actor path.
                guard = 0;
                while (guard++ < 80 && !rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId))
                    await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
                await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();

                ActorRequest? held = null;
                guard = 0;
                while (guard++ < 40)
                {
                    await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
                    if (rig.Actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move } liveMove
                        && rig.Executor.TryGetQuestRuntime(botId, out var questRuntime)
                        && questRuntime!.OwnsRunningRequest(liveMove))
                    {
                        held = liveMove;
                        break;
                    }
                }
                await Assert.That(held).IsNotNull();
                await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
                await Assert.That(rig.Actor.ActiveRequest).IsSameReferenceAs(held);

                _ = rig.Actor.Stop();
                guard = 0;
                while (guard++ < 10 && rig.Actor.ActiveRequest is { IsTerminal: false })
                    await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
                await Assert.That(rig.Actor.ActiveRequest is not { IsTerminal: false }).IsTrue();
            }

            // Terminal feedback through the rig's purpose-memory seam (the
            // QuestGoalOwnerTests.Owner_Reconsiders_OnTerminalResult
            // precedent): the completed quest is never re-picked — the next
            // pumped pick advances to the next quest in policy order.
            rig.Module.NotifyQuestTerminal(botId, expected[step], completed: true);
            await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(0u);
        }

        // All three advanced in deterministic order; none was re-picked once
        // completed (each id picked exactly once, in policy order).
        await Assert.That(picked[0]).IsEqualTo(OpenQuestId);
        await Assert.That(picked[1]).IsEqualTo(SecondQuestId);
        await Assert.That(picked[2]).IsEqualTo(ThirdQuestId);
        await Assert.That(picked.Distinct().Count()).IsEqualTo(3);

        // Post-chain: pumping further wakes never resurrects a completed
        // quest — the purpose stays on the last pick or releases, never a
        // completed id.
        for (var extra = 0; extra < 5; extra++)
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        var after = rig.Module.GetChosenQuest(botId);
        await Assert.That(after == 0 || after == ThirdQuestId).IsTrue();
    }

    [Test]
    public async Task ServerCompletion_ReachesOwner_WithoutManualNotification()
    {
        // INTEGRATED (MEDIUM B): the pick completes through the REAL engine
        // path in-rig — accept, engine hunt credit, advance to Ready, quest-leg
        // turn-in landing a CompletedQuestIds terminal through the owned path
        // (executor sink -> runtime sink -> module). NO manual
        // NotifyQuestTerminal call: the owner sees the server completion
        // through production wiring and moves on to the next quest.
        using var rig = CreateRig("owner-sched-serverdone", seedSecondQuest: true);
        rig.Executor.QuestTerminalSink = rig.Module.NotifyQuestTerminal;
        var botId = rig.BotId;

        await rig.PumpRoundAsync(1);
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(OpenQuestId);

        var guard = 0;
        while (guard++ < 80 && !rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId))
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();

        // Credit the hunt through the ENGINE event (the same path real kills
        // take: QuestManagerEvents -> OnMonsterHunt -> AddObjective), then
        // advance to Ready through the real actor path.
        for (var k = 0; k < 3; k++)
            rig.Actor.Character.Events.OnMonsterHunt(rig.Actor.Character,
                new OnMonsterHuntArgs { NpcId = PreyTemplate, Count = 1 });
        // The fixture's Progress component also carries a QuestActObjItemGather
        // (Count 3, GatherItemId) alongside the hunt: grant + engine-credit it
        // or AdvanceQuest correctly stays Progress (all acts must pass).
        GameplayActorTestRig.GiveBagItem(rig.Actor, GatherItemId, 3);
        rig.Actor.Character.Events.OnItemGather(rig.Actor.Character,
            new OnItemGatherArgs { QuestId = OpenQuestId, ItemId = GatherItemId, Count = 3 });
        var advance = rig.Actor.AdvanceQuest(OpenQuestId);
        await Assert.That(advance.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests[OpenQuestId].Status).IsEqualTo(QuestStatus.Ready);

        // Reporter in range: the quest leg's turn-in serves the Ready step;
        // pump until the SERVER marks the quest completed (owned path feeds
        // the module — never a manual NotifyQuestTerminal here).
        var reportObjId = SpawnAreaNpc(rig.Session, ReportNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        _ = reportObjId;
        guard = 0;
        while (guard++ < 40 && !rig.Actor.Character.Quests.HasQuestCompleted(OpenQuestId))
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Actor.Character.Quests.HasQuestCompleted(OpenQuestId)).IsTrue();

        // The owner saw the server completion through the owned terminal feed:
        // the completed quest is released and never re-picked — the next
        // pumped pick moves on to the second quest.
        guard = 0;
        while (guard++ < 40 && rig.Module.GetChosenQuest(botId) != SecondQuestId)
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(SecondQuestId);
    }

    [Test]
    public async Task ColdStart_ResolvesThroughNonCreatingRead()
    {
        // PRODUCTION-STARTUP PATH (MEDIUM B): fresh startup goes through the
        // NON-CREATING TryGetActor read (Program.cs:333). Before the first
        // scheduler step there is no executor state — the resolver returns
        // null (never creates), the module denies for want of a live actor,
        // and the FIRST pumped wake materializes state through the step path
        // (never through the resolver) and picks the quest.
        using var rig = CreateRig("owner-sched-coldstart", seedSecondQuest: false, materializeActorState: false);
        var botId = rig.BotId;

        await Assert.That(rig.Executor.TryGetActor(botId)).IsNull();
        await Assert.That(QuestLegActor.Resolve(botId)).IsNull();

        await rig.PumpRoundAsync(1);
        await Assert.That(rig.Executor.TryGetActor(botId)).IsSameReferenceAs(rig.Actor);
        await Assert.That(QuestLegActor.Resolve(botId)).IsSameReferenceAs(rig.Actor);
        // The first wake materializes state mid-arbitration (resolver still null
        // during the pick pass); the SECOND wake picks off the live actor.
        var guard = 0;
        while (guard++ < 10 && rig.Module.GetChosenQuest(botId) != OpenQuestId)
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(OpenQuestId);
    }

    [Test]
    public async Task OwnerPick_Interruption_Reselects_ThroughScheduler()
    {
        using var rig = CreateRig("owner-sched-interrupt", seedSecondQuest: true);
        var botId = rig.BotId;

        await rig.PumpRoundAsync(1);
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(OpenQuestId);

        ActorRequest? held = null;
        var guard = 0;
        while (guard++ < 15)
        {
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
            if (rig.Actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move } live)
            {
                held = live;
                break;
            }
        }
        await Assert.That(held).IsNotNull();

        // Interruption: the held pursuit's target dies mid-hold. The death
        // itself is a rig-provided world fact (the BotDecisionVerticalTests
        // vanish + DeadPrey listing precedent); every response below is
        // production: hold → arrival terminal → consume → refreshed listing →
        // owner reconsiders → leg accepts the next quest.
        rig.Session.World.GetNpc(rig.PreyObjId)!.Hp = 0;

        // The owner reconsiders off the refreshed listing: quest 1 lost its
        // viable hunt, quest 2 is the lowest eligible offer.
        guard = 0;
        while (guard++ < 40 && rig.Module.GetChosenQuest(botId) != SecondQuestId)
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Module.GetChosenQuest(botId)).IsEqualTo(SecondQuestId);

        // The quest leg follows through the same wakes: quest 2 is accepted
        // via the real engine path (quest 1 stays active — only its target
        // died, so this is reselect-next-work, not recovery).
        guard = 0;
        while (guard++ < 80 && !rig.Actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuestId))
            await rig.PumpRoundAsync(rig.Counting.CountStarts(botId) + 1);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(SecondQuestId)).IsTrue();
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();
    }

    [Test]
    public async Task Discovery_CompletesTalkQuest_AndChoosesNextDistantWork()
    {
        // Initial synthetic world only: an offer/talk/reporter cluster 60m
        // away and a second ordinary offer another 60m beyond it.
        using var rig = CreateRig("owner-sched-discovery-chain", seedSecondQuest: true,
            materializeActorState: false, distantOfferDistance: 60f, talkJourney: true);
        await Assert.That(rig.Scheduler.Wake(rig.BotId)).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();
        await Assert.That(rig.Arbiter.GetActiveActivity(rig.BotId))
            .IsEqualTo(QuestBootstrapActivityModule.DiscoveryActivityName);
        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(0u);

        for (var wake = 0; wake < 800 &&
             !rig.Actor.Character.Quests.ActiveQuests.ContainsKey(NextJourneyQuestId); wake++)
            await rig.PumpScheduledWakeAsync();

        // Talk credit, reporting, terminal feed and next selection all come
        // from ordinary actor/engine effects, not supplied completion events.
        await Assert.That(rig.Actor.Character.Quests.HasQuestCompleted(JourneyQuestId)).IsTrue();
        await Assert.That(rig.Actor.AuditTrace.Any(row =>
            row.Action == ActorActionType.Talk && row.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(rig.Actor.AuditTrace.Any(row =>
            row.Action == ActorActionType.TurnInQuest && row.TargetId == JourneyQuestId &&
            row.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(NextJourneyQuestId);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(NextJourneyQuestId)).IsTrue();
        await Assert.That(rig.Actor.AuditTrace.Any(row =>
            row.Action == ActorActionType.AcceptQuest && row.TargetId == NextJourneyQuestId &&
            row.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task Discovery_InterruptedMove_RedecidesAndAcceptsWithoutResumingTrace()
    {
        using var rig = CreateRig("owner-sched-discovery-interrupt", seedSecondQuest: false,
            materializeActorState: false, distantOfferDistance: 60f);
        await Assert.That(rig.Scheduler.Wake(rig.BotId)).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();
        var oldMove = rig.Actor.ActiveRequest!;
        await Assert.That(oldMove.Action).IsEqualTo(ActorActionType.Move);

        // Controlled external interruption through the public actor API.
        // The fixture never changes the request's lifecycle directly.
        await Assert.That(rig.Actor.PreemptCurrent("controlled discovery interruption")).IsTrue();
        await rig.PumpScheduledWakeAsync();
        await Assert.That(oldMove.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(rig.Actor.ActiveRequest).IsNotNull();
        await Assert.That(rig.Actor.ActiveRequest).IsNotSameReferenceAs(oldMove);
        await Assert.That(rig.Actor.ActiveRequest!.Action).IsEqualTo(ActorActionType.Move);

        for (var wake = 0; wake < 500 &&
             !rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId); wake++)
            await rig.PumpScheduledWakeAsync();
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();
        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(OpenQuestId);
    }

    [Test]
    public async Task Discovery_Reactivation_RetiresOldGenerationMove()
    {
        using var rig = CreateRig("owner-sched-discovery-generation", seedSecondQuest: false,
            materializeActorState: false, distantOfferDistance: 60f);
        await Assert.That(rig.Scheduler.Wake(rig.BotId)).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();
        var oldMove = rig.Actor.ActiveRequest!;
        var oldGeneration = rig.Scheduler.GetEnrollmentSnapshot(rig.BotId).Generation;

        // Controlled lifecycle transition, not a forced request result. The
        // existing scheduled cadence supplies the next current-generation step.
        await Assert.That(rig.Manager.Deactivate(rig.BotId, "owner-sched-rig")).IsTrue();
        await Assert.That(rig.Manager.Activate(rig.BotId, null, "owner-sched-rig")).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();

        var generation = rig.Scheduler.GetEnrollmentSnapshot(rig.BotId).Generation;
        await Assert.That(generation).IsGreaterThan(oldGeneration);
        await Assert.That(oldMove.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.Generation).IsEqualTo(generation);
        await Assert.That(rig.Actor.ActiveRequest).IsNotNull();
        await Assert.That(rig.Actor.ActiveRequest).IsNotSameReferenceAs(oldMove);
        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(0u);

        for (var wake = 0; wake < 500 &&
             !rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId); wake++)
            await rig.PumpScheduledWakeAsync();
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(OpenQuestId)).IsTrue();
    }

    [Test]
    public async Task Discovery_DeathBeforeQuestRuntime_RetiresMoveThroughScheduler()
    {
        using var rig = CreateRig("owner-sched-discovery-death", seedSecondQuest: false,
            materializeActorState: false, distantOfferDistance: 60f);
        await Assert.That(rig.Scheduler.Wake(rig.BotId)).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();
        var oldMove = rig.Actor.ActiveRequest!;
        await Assert.That(rig.Executor.TryGetQuestRuntime(rig.BotId, out _)).IsFalse();

        // Controlled world fact: the character dies. Only the actual
        // scheduler/actor death authority retires the Running request.
        rig.Actor.Character.Hp = 0;
        await rig.PumpScheduledWakeAsync();
        await Assert.That(oldMove.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(rig.Actor.ActiveRequest).IsNull();
        await Assert.That(rig.Executor.GetRoamRoute(rig.BotId)).IsNull();
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.QuestDiscoveryActive).IsFalse();
        await Assert.That(rig.Executor.TryGetQuestRuntime(rig.BotId, out _)).IsFalse();
        for (var wake = 0; wake < 8 && rig.Actor.Character.IsDead; wake++)
            await rig.PumpScheduledWakeAsync();
        await Assert.That(rig.Actor.Character.IsDead).IsFalse();
        await Assert.That(oldMove.State).IsEqualTo(ActorLifecycleState.Interrupted);
    }

    [Test]
    public async Task Discovery_LocalActionableWork_KeepsOrdinaryChosenQuest()
    {
        using var rig = CreateRig("owner-sched-discovery-local", seedSecondQuest: false,
            materializeActorState: false, talkJourney: true);
        await Assert.That(rig.Scheduler.Wake(rig.BotId)).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();

        await Assert.That(rig.Module.GetChosenQuest(rig.BotId)).IsEqualTo(JourneyQuestId);
        await Assert.That(rig.Arbiter.GetActiveActivity(rig.BotId))
            .IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(rig.Actor.Character.Quests.ActiveQuests.ContainsKey(JourneyQuestId)).IsTrue();
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.QuestDiscoveryActive).IsFalse();
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.QuestTravelTarget).IsNull();
    }

    [Test]
    public async Task Discovery_Retirement_DoesNotPreemptPartyFollowRequest()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        using var rig = CreateRig("owner-sched-discovery-foreign", seedSecondQuest: false,
            materializeActorState: false, distantOfferDistance: 60f);
        var (leader, _) = GameplayActorTestRig.CreateActor("owner-sched-discovery-leader");
        GameplayActorTestRig.JoinActorWorld(rig.Session, leader);
        var leaderPosition = SpawnPos + new Vector3(90, 0, 0);
        GameplayActorTestRig.SetPosition(leader, leaderPosition);
        var leaderRegion = rig.Session.World.GetRegionByPos(leaderPosition)!;
        leaderRegion.AddObject(leader.Character);
        leader.Character.Region = leaderRegion;

        await Assert.That(rig.Scheduler.Wake(rig.BotId)).IsTrue();
        for (var wake = 0; wake < 3; wake++)
            await rig.PumpScheduledWakeAsync();
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.QuestDiscoveryActive).IsTrue();

        // Controlled ordinary party join: retire the discovery move through
        // the public API, invite/accept through the engine, then let the actual
        // scheduler party-follow branch create its own differently tagged leg.
        await Assert.That(rig.Actor.PreemptCurrent("controlled party join")).IsTrue();
        await Assert.That(leader.PartyInvite(rig.Actor.Character.ObjId).State)
            .IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(rig.Actor.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);
        await rig.PumpScheduledWakeAsync();
        var partyMove = rig.Actor.ActiveRequest!;
        await Assert.That(partyMove.MoveOwner).IsEqualTo("OTHER:party-follow");
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.PendingLeg).IsSameReferenceAs(partyMove);
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.QuestDiscoveryActive).IsTrue();

        // A real lifecycle change retires discovery admission. Its authority
        // must not steal the live request that party follow now owns.
        await Assert.That(rig.Manager.Deactivate(rig.BotId, "owner-sched-rig")).IsTrue();
        await Assert.That(rig.Manager.Activate(rig.BotId, null, "owner-sched-rig")).IsTrue();
        await rig.PumpScheduledWakeAsync();
        await Assert.That(partyMove.IsTerminal).IsFalse();
        await Assert.That(rig.Actor.ActiveRequest).IsSameReferenceAs(partyMove);
        await Assert.That(rig.Executor.GetBotState(rig.BotId)!.QuestDiscoveryActive).IsFalse();
    }

    // ------------------------------------------------------------ rig

    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        public bool RetirementResult { get; set; } = true;
        public List<(PlayerBotRuntime Owner, ulong Generation, string Reason)> RetirementCalls { get; } = [];

        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason)
        {
            RetirementCalls.Add((expectedOwner, expectedGeneration, reason));
            return RetirementResult;
        }

        public bool ActivateHeadless(Character character, object? botContext) => true;
        public bool Deactivate(Character character, string reason) => true;
    }

    /// <summary>Count-only decorator: every wake runs the production chain.</summary>
    private sealed class CountingExecutor(IBotStepExecutor inner) : IBotStepExecutor
    {
        private readonly ConcurrentQueue<uint> _starts = [];
        public int CountStarts(uint botId) => _starts.Count(id => id == botId);
        public IBotStepExecutor Inner => inner;
        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            _starts.Enqueue(bot.CharacterId);
            return inner.StepAsync(bot, cancellationToken);
        }
    }

    private sealed class Rig : IDisposable
    {
        public required GameplayActor Actor { get; init; }
        public required HeadlessSession Session { get; init; }
        public required BotRoamStepExecutor Executor { get; init; }
        public required QuestBootstrapActivityModule Module { get; init; }
        public required BotGoalArbiter Arbiter { get; init; }
        public required CountingExecutor Counting { get; init; }
        public required PlayerBotManager Manager { get; init; }
        public required PlayerBotScheduler Scheduler { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required uint BotId { get; init; }
        public required uint PreyObjId { get; init; }

        public async Task DrainUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                Scheduler.DrainTickQueue();
                if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("owner-through-scheduler rig wait condition not met");
                await Task.Delay(5);
            }
        }

        /// <summary>One scheduler-delivered wake: advance past the live cadence, scan, drain.</summary>
        public async Task PumpRoundAsync(int targetStarts)
        {
            Time.Advance(TimeSpan.FromMilliseconds(150));
            Scheduler.Wake(BotId);
            Scheduler.RunScanCycle();
            await DrainUntilAsync(() => Counting.CountStarts(BotId) >= targetStarts);
        }

        /// <summary>Drains the initial wake event or consumes the executor's scheduled cadence.</summary>
        public async Task PumpScheduledWakeAsync()
        {
            var before = Scheduler.GetEnrollmentSnapshot(BotId);
            var completed = before.Ran + before.Skipped + before.Failed + before.TimedOut;
            if (before.ScheduledDueUtc is { } due)
            {
                var now = Time.GetUtcNow().UtcDateTime;
                if (due > now)
                    Time.Advance(due - now);
            }
            else if (completed != 0 && !before.Leased)
            {
                throw new InvalidOperationException("production cadence did not schedule the next wake");
            }

            // Wake only enqueues an event. The first scan turns that initial
            // event into due work; later scans consume the returned cadence.
            Scheduler.RunScanCycle();
            await DrainUntilAsync(() =>
            {
                var after = Scheduler.GetEnrollmentSnapshot(BotId);
                return after.Ran + after.Skipped + after.Failed + after.TimedOut > completed && !after.Leased;
            });
        }

        public void Dispose()
        {
            Scheduler.StopAsync().GetAwaiter().GetResult();
            QuestLegActor.SetResolver(null);
        }
    }

    private static Rig CreateRig(string name, bool seedSecondQuest, bool seedThirdQuest = false,
        bool materializeActorState = true, float? distantOfferDistance = null, bool talkJourney = false)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, SpawnPos);
        var region = session.World.GetRegionByPos(SpawnPos)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        actor.Character.Level = 3;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;

        if (talkJourney)
        {
            GameplayActorTestRig.SeedQuestTalkChain(JourneyQuestId, JourneyStartComponent, JourneyProgressComponent,
                JourneyReadyComponent, JourneyOfferNpc, JourneyTalkNpc, JourneyReportNpc, level: 1);
            QuestFixtureRow.Invalidate(JourneyQuestId);
            if (seedSecondQuest)
            {
                GameplayActorTestRig.SeedQuestDelivery(NextJourneyQuestId, NextJourneyStartComponent,
                    NextJourneyReadyComponent, NextJourneyOfferNpc, JourneyReportNpc, level: 1);
                QuestFixtureRow.Invalidate(NextJourneyQuestId);
            }
        }

        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, OpenStartComponent, OpenProgressComponent,
            OpenReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        SeedGatherShape(OpenQuestId, OpenProgressComponent, actId: 91_813, detailId: 91_816);
        QuestFixtureRow.Invalidate(OpenQuestId);
        if (seedSecondQuest)
        {
            GameplayActorTestRig.SeedQuestHunt(SecondQuestId, SecondStartComponent, SecondProgressComponent,
                SecondReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
            SeedGatherShape(SecondQuestId, SecondProgressComponent, actId: 91_814, detailId: 91_817);
            QuestFixtureRow.Invalidate(SecondQuestId);
        }
        if (seedThirdQuest)
        {
            GameplayActorTestRig.SeedQuestHunt(ThirdQuestId, ThirdStartComponent, ThirdProgressComponent,
                ThirdReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
            SeedGatherShape(ThirdQuestId, ThirdProgressComponent, actId: 91_815, detailId: 91_818);
            QuestFixtureRow.Invalidate(ThirdQuestId);
        }
        var offerPosition = SpawnPos + new Vector3(distantOfferDistance ?? 5f, 0, 0);
        var offerTemplate = talkJourney ? JourneyOfferNpc : OfferNpcTemplate;
        SpawnAreaNpc(session, offerTemplate, offerPosition);
        if (distantOfferDistance.HasValue)
            SeedNpcSpawner(session, offerTemplate, offerPosition);
        if (talkJourney)
        {
            SpawnAreaNpc(session, JourneyTalkNpc, offerPosition + new Vector3(2, 0, 0));
            SpawnAreaNpc(session, JourneyReportNpc, offerPosition + new Vector3(3, 0, 0));
            if (seedSecondQuest)
            {
                var nextOfferPosition = offerPosition + new Vector3(60, 0, 0);
                SpawnAreaNpc(session, NextJourneyOfferNpc, nextOfferPosition);
                SeedNpcSpawner(session, NextJourneyOfferNpc, nextOfferPosition);
            }
        }
        var preyObjId = SpawnAreaNpc(session, PreyTemplate, SpawnPos + new Vector3(8, 0, 0));

        var time = new FakeTimeProvider();

        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var arbiter = new BotGoalArbiter([module]);

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = time,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
            RoamSpeed = 2f,
            EnableWildlifeHunt = false,
            EnableWildlifeButcher = false,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            // Production wiring (Program.cs): the executor reads the arbiter's
            // live active activity per wake.
            ActiveActivityProvider = characterId => arbiter.GetActiveActivity(characterId),
        };
        if (distantOfferDistance.HasValue || talkJourney)
        {
            executor.EnableIdleWake = true;
            executor.QuestChosenProvider = module.GetChosenQuest;
            executor.QuestTerminalSink = module.NotifyQuestTerminal;
            arbiter.Register(new IdleActivityModule(executor));
        }
        // The production IBotStepExecutor binding: arbitration decorator
        // around the roam executor (one arbitration pass per wake, then the
        // ordinary step). Rig-wirable via its plain constructor.
        var decorator = new BotGoalArbiterStepExecutor(arbiter, executor, gameHourProvider: () => 12f);
        var counting = new CountingExecutor(decorator);

        var manager = new PlayerBotManager(new RecordingLifecycle());
        var scheduler = new PlayerBotScheduler(manager, counting,
            new PlayerBotSchedulerOptions
            {
                WorkerCount = 4,
                ScanInterval = TimeSpan.FromHours(1),
                StepTimeout = TimeSpan.FromSeconds(30),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                SubscribeToTickManager = false,
                PortalResolver = distantOfferDistance.HasValue || talkJourney
                    ? _ => new AAEmu.Game.Models.Game.Portal { X = 0, Y = 0, Z = 0 }
                    : null,
            },
            time);
        scheduler.Start();

        var botId = actor.Character.Id;
        // Production wiring (Program.cs:333): the executor's NON-CREATING read.
        // Resolve must not create — executor state materializes on the
        // scheduler step path (pre-first-wake route arming in production,
        // mirrored by materializeActorState below), never through the resolver.
        if (materializeActorState)
            _ = executor.GetOrCreateActor(actor.Character);
        QuestLegActor.SetResolver(executor.TryGetActor);

        manager.Spawn(actor.Character, "owner-sched-rig");
        manager.Activate(actor.Character.Id, null, "owner-sched-rig");
        return new Rig
        {
            Actor = actor,
            Session = session,
            Executor = executor,
            Module = module,
            Arbiter = arbiter,
            Counting = counting,
            Manager = manager,
            Scheduler = scheduler,
            Time = time,
            BotId = botId,
            PreyObjId = preyObjId,
        };
    }

    /// <summary>
    /// Pursuit execution shape on a fixture quest (the QuestGoalOwnerTests
    /// SeedPursuitShape precedent, parameterized): a Progress
    /// QuestActObjItemGather for <see cref="GatherItemId"/> plus the
    /// npc→pack→item loot link off <see cref="PreyTemplate"/>, so the quest
    /// leg's objective funnel resolves and the pursuit Move dispatches once
    /// the quest is accepted.
    /// </summary>
    private static void SeedGatherShape(uint questId, uint progressComponentId, uint actId, uint detailId)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        var template = questTemplates[questId];
        if (!template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == GatherItemId))
        {
            var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
                .GetField("_componentTemplates", flags)!.GetValue(manager)!;
            var component = componentTemplates[progressComponentId];
            var act = new QuestActObjItemGather(component)
            {
                ActId = actId,
                DetailId = detailId,
                DetailType = nameof(QuestActObjItemGather),
                Count = 3,
                ItemId = GatherItemId,
                ThisComponentObjectiveIndex = 1,
            };
            component.ActTemplates.Add(act);
            var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
                .GetField("_actTemplatesByDetailType", flags)!.GetValue(manager)!;
            if (!actsByType.TryGetValue(nameof(QuestActObjItemGather), out var acts))
            {
                acts = [];
                actsByType[nameof(QuestActObjItemGather)] = acts;
            }
            acts[detailId] = act;
        }

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(PreyTemplate, out var rows) || rows.All(r => r.LootPackId != GatherPackId))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = PreyTemplate,
                NpcId = PreyTemplate,
                LootPackId = GatherPackId,
                DefaultPack = true,
            });
            map[PreyTemplate] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(GatherPackId, out var pack) || pack.Loots.All(l => l.ItemId != GatherItemId))
        {
            var loot = new Loot
            {
                Id = GatherPackId,
                Group = 0,
                ItemId = GatherItemId,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = GatherPackId,
                GradeId = 0,
                AlwaysDrop = false,
            };
            packs[GatherPackId] = new LootPack
            {
                Id = GatherPackId,
                Loots = [loot],
                LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
                Groups = [],
                ActabilityGroups = [],
                GroupCount = 1,
            };
        }
    }

    /// <summary>Seeds the real spawner table read by the existing travel resolver.</summary>
    private static void SeedNpcSpawner(HeadlessSession session, uint templateId, Vector3 position)
    {
        session.World.SpawnManager ??= new SpawnManager(session.World);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!
            .GetValue(session.World.SpawnManager)!;
        var spawnerId = templateId;
        spawners[spawnerId] =
        [
            new NpcSpawner
            {
                Id = spawnerId,
                UnitId = templateId,
                Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
            },
        ];
    }

    private static uint SpawnAreaNpc(HeadlessSession session, uint templateId, Vector3 pos)
    {
        var objId = session.SpawnNpc(templateId);
        var npc = session.World.GetNpc(objId)!;
        npc.TemplateId = templateId;
        npc.Template = new NpcTemplate { Id = templateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, objId, pos);
        var region = session.World.GetRegionByPos(pos)!;
        region.AddObject(npc);
        npc.Region = region;
        return objId;
    }
}
