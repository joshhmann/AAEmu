using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
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
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, Boar3475, Origin)).IsEqualTo((Vector3?)left);
        // The reporter's legacy first-match lookup is deliberately unchanged.
        await Assert.That(QuestBehavior.TryNpcSpawnerPosition(world, 97_999)).IsEqualTo((Vector3?)(Origin + new Vector3(1, 0, 0)));
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, 0, Origin)).IsNull();
        await Assert.That(QuestBehavior.TryNearestNpcPreySpawnerPosition(world, Boar3475, new Vector3(float.NaN, 0, 0))).IsNull();
        var (other, _) = GameplayActorTestRig.CreateActor("other-world-source");
        SeedNpcSources(other, Origin + new Vector3(1, 0, 0));
        sources[97_300].Clear();
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

    private static async Task Step(BotGoalArbiterStepExecutor steps, PlayerBotRuntime bot, FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await steps.StepAsync(bot, CancellationToken.None);
    }

    private static (GameplayActor Actor, QuestBootstrapActivityModule Module, BotRoamStepExecutor Executor,
        BotGoalArbiterStepExecutor Steps, PlayerBotRuntime Bot, FakeTimeProvider Clock, uint GiverObjId, HeadlessSession Session) CreateRig(string name, bool eligibleFollowUp = true)
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
            EnableWildlifeHunt = false, EnableWildlifeButcher = false, GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = arbiter.GetActiveActivity, NearbyDoodadProvider = (_, _) => [],
            QuestChosenProvider = module.GetChosenQuest, QuestTerminalSink = module.NotifyQuestTerminal,
            QuestGiveUpSink = module.NotifyQuestGiveUp
        };
        executor.GetOrCreateActor(actor.Character);
        QuestLegActor.SetResolver(executor.TryGetActor);
        return (actor, module, executor, new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f),
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
