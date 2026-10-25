using System.Numerics;
using System.Reflection;
using System.Text.Json;
using AAEmu.Commons.Utils;
using AAEmu.Game.GameData;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

// Native behavior and ordinary kill-credit rig. Acceptance, poses, spawned NPCs,
// manual damage and reference-loader maps are disclosed fixture inputs.
// This does not establish earned walking/combat, loot, live autonomy or completion.
[NotInParallel]
public class QuestHuntObjectiveBehaviorTests
{
    private object? _previousQuestManager;

    [Before(Test)]
    public void LoadIsolatedCanonicalQuests()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        var field = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _previousQuestManager = field.GetValue(null);
        var fresh = new QuestManager(Mock.Of<ITaskManager>().Object,
            Mock.Of<AAEmu.Game.Core.Managers.World.IZoneManager>().Object);
        field.SetValue(null, fresh);
        fresh.Load();
        QuestFixtureRow.InvalidateAll();
    }

    [After(Test)]
    public void RestoreQuestManager()
    {
        typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, _previousQuestManager);
        QuestFixtureRow.InvalidateAll();
    }

    [Test]
    public async Task CanonicalGroupHunt_WorldReadmission_OrdinaryActionsAndKillCredit()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestBehavior.ClearGiveUpMemory();
        QuestLegActor.ClearAll();
        var (actor, session) = GameplayActorTestRig.CreateActor("private-next266-probe");
        actor.Character.Level = 20;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        var origin = new Vector3(32, 32, 0);
        GameplayActorTestRig.SetPosition(actor, origin);
        var region = session.World.GetRegionByPos(origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        uint Spawn(uint template, Vector3 position)
        {
            var id = session.SpawnNpc(template);
            var npc = session.World.GetNpc(id)!;
            typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc.Transform, session.World.Id);
            typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc, session.World);
            npc.Template = new NpcTemplate { Id = template, Scale = 1f };
            npc.Hp = npc.MaxHp = 100;
            npc.IsVisible = true;
            GameplayActorTestRig.SetNpcPosition(session, id, position);
            region.AddObject(npc);
            npc.Region = region;
            return id;
        }
        Spawn(3520, origin + new Vector3(2, 0, 0));
        session.World.SpawnManager ??= new SpawnManager(session.World);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session.World.SpawnManager)!;
        spawners[98666] = [new NpcSpawner { Id = 98666, UnitId = 3494,
            Position = new WorldSpawnPosition { X = 182, Y = 32, Z = 0 } }];
        // Exact canonical 8130 source: template1221/group1882/func1777/detail655.
        // Existing helper supplies native manager maps; overwrite its simplified
        // func/detail with the verified canonical row. No world doodad/credit.
        var doodadSingletonField = typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousDoodadManager = doodadSingletonField.GetValue(null);
        if (previousDoodadManager == null)
            GameplayActorTestRig.SeedGatherDoodadSource(1221, 1882, 655, 8130);
        var doodads = DoodadManager.Instance;
        var sourceFields = new[] { "_templates", "_allFuncGroups", "_funcsByGroups", "_funcsById", "_funcTemplates" }
            .Select(n => typeof(DoodadManager).GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!).ToArray();
        var sourceBefore = sourceFields.Select(f => f.GetValue(doodads)).ToArray();
        // Existing empty headless rig maps are isolated before helper writes.
        for (var i = 0; i < sourceFields.Length; i++)
            sourceFields[i].SetValue(doodads, Activator.CreateInstance(sourceFields[i].FieldType));
        GameplayActorTestRig.SeedGatherDoodadSource(1221, 1882, 655, 8130);
        var nativeFunc = new DoodadFunc { GroupId = 1882, FuncId = 655, FuncKey = 1777,
            FuncType = "DoodadFuncLootItem", NextPhase = 1889, SkillId = 0 };
        ((Dictionary<uint, List<DoodadFunc>>)sourceFields[2].GetValue(doodads)!)[1882] = [nativeFunc];
        ((Dictionary<uint, DoodadFunc>)sourceFields[3].GetValue(doodads)!).Clear();
        ((Dictionary<uint, DoodadFunc>)sourceFields[3].GetValue(doodads)!)[1777] = nativeFunc;
        ((Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)sourceFields[4].GetValue(doodads)!)["DoodadFuncLootItem"][655]
            = new DoodadFuncLootItem { ItemId = 8130, CountMin = 1, CountMax = 1, Percent = 10000, RemainTime = 100000 };
        var lootSingleton = typeof(Singleton<LootGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalLoot = lootSingleton.GetValue(null);
        _ = LootGameData.Instance; // Direct DoodadFuncLootItem has no pack rows.
        // Non-objective loot is omitted in this headless kill-credit fixture.
        // Empty native caches permit ordinary death; no quest item is inserted.
        var npcMapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldNpcMap = npcMapField.GetValue(ItemManager.Instance);
        npcMapField.SetValue(ItemManager.Instance, new Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc>>());
        var packField = typeof(LootGameData).GetField("_lootPacks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldPacks = packField.GetValue(LootGameData.Instance);
        packField.SetValue(LootGameData.Instance, new Dictionary<uint, AAEmu.Game.Models.Game.Items.Loots.LootPack>());

        Console.WriteLine("PROBE266 LOADER " + (Singleton<LootGameData>.PeekInstance != null));
        QuestFixtureRow.InvalidateAll();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var giveUps = new List<string>();
        BotGoalArbiter? arbiter = null;
        var executor = new BotRoamStepExecutor {
            ActiveActivityProvider = id => arbiter?.GetActiveActivity(id),
            ActorFactory = _ => actor, TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false, EnableWildlifeButcher = false,
            GroundHeightProvider = (_, _) => 0f, NearbyDoodadProvider = (_, _) => [],
            QuestChosenProvider = module.GetChosenQuest, QuestTerminalSink = module.NotifyQuestTerminal,
            QuestGiveUpSink = (id, result) => { giveUps.Add(result.ToString()!); module.NotifyQuestGiveUp(id, result); }
        };
        var presence = new PresenceRoamActivityModule(new BotScheduleOptions { Enabled = false },
            new RoamRouteScheduleBehavior(executor, (_, _) => 0f), executor, PlayerBotMetadata.Empty);
        arbiter = new BotGoalArbiter([module, presence]);
        executor.GetOrCreateActor(actor.Character);
        QuestLegActor.SetResolver(executor.TryGetActor);
        var bot = new PlayerBotRuntime(actor.Character, "private-next266-probe");
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);
        BotActivityContext Context() => new() { Bot = bot, GameHour = 12, ActiveActivity = arbiter.GetActiveActivity(bot.CharacterId) };
        void Record(string phase) => Console.WriteLine("PROBE266 " + JsonSerializer.Serialize(new {
            phase, listing = BotOpportunityListing.List(actor), decision = module.CanActivate(Context()),
            chosen = module.GetChosenQuest(bot.CharacterId), activity = arbiter.GetActiveActivity(bot.CharacterId),
            request = actor.ActiveRequest?.TraceId, action = actor.ActiveRequest?.Action.ToString(),
            owner = actor.ActiveRequest?.MoveOwner, purpose = actor.ActiveRequest?.DecisionQuestId,
            terminal = actor.ActiveRequest?.IsTerminal, giveUps = giveUps.ToArray()
        }));
        async Task Step() { clock.Advance(TimeSpan.FromMilliseconds(100)); await steps.StepAsync(bot, CancellationToken.None); }
        try
        {
            Record("before-scripted-accept");
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
            var accepted = actor.AcceptQuest(266, QuestAcceptorType.Npc, 3520);
            await Assert.That(accepted.State).IsEqualTo(ActorLifecycleState.Completed);
            var quest = actor.Character.Quests.ActiveQuests[266];
            Console.WriteLine("PROBE266 GROUPS " + JsonSerializer.Serialize(QuestDirector.ReadObjectiveGroups(quest).Select(g => new { g.ComponentId, g.Active, g.Credited, Remaining = g.Remaining.Select(a => new { a.ActId, Type = a.GetType().Name, a.DetailId }).ToArray() }).ToArray()));
            var initial = BotOpportunityListing.List(actor);
            await Assert.That(initial.ActiveWork.Any(w => w.QuestId == 266 && w.Leg == "hunt")).IsTrue();
            await Assert.That(initial.ViableHunts.Any(h => h.QuestId == 266)).IsFalse();
            Record("accepted-no-prey-source-configured");
            // A configured location is unknown availability, but useful to
            // inspect through ordinary quest travel instead of patrol.
            await Assert.That(module.CanActivate(Context()).CanActivate).IsTrue();
            await Step();
            Record("native-known-source-search");
            await Assert.That(actor.ActiveRequest?.MoveOwner).IsEqualTo("QUEST_TRAVEL");
            await Assert.That(actor.ActiveRequest?.DecisionQuestId).IsEqualTo(266u);
            // Let the existing acceleration reach its requested 5m/s before
            // checking cruise movement through ordinary integration.
            actor.Tick(TimeSpan.FromMilliseconds(100));
            actor.Tick(TimeSpan.FromMilliseconds(100));
            var walkStart = actor.Character.Transform.World.Position;
            actor.Tick(TimeSpan.FromMilliseconds(100)); // Flat fixture, no live route claim.
            await Assert.That(Vector3.Distance(walkStart, actor.Character.Transform.World.Position)).IsGreaterThan(0.45f);
            await Assert.That(actor.Character.Quests.ActiveQuests[266].Objectives.All(x => x == 0)).IsTrue();
            var oldRequest = actor.ActiveRequest;
            var busy = BotOpportunityListing.List(actor);
            await Assert.That(busy.QuestOffersUnknown).IsTrue();
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
            await Step();
            Record("busy-retention-next-wake");
            await Assert.That(ReferenceEquals(oldRequest, actor.ActiveRequest)).IsTrue();
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
            actor.Stop(); // fixture boundary: next observation sees independently seeded changed world.
            var prey = Spawn(3494, origin + new Vector3(18, 0, 0));
            Record("changed-world-prey-appeared");
            var selectedPlan = QuestDirector.PlanWake(BotObservedContext.Capture(actor), 266, actor.Character).Single();
            await Assert.That(selectedPlan.Pattern).IsEqualTo(AAEmu.Game.Models.Game.Quests.Director.QuestPattern.KillX);
            await Assert.That(selectedPlan.Fixture.HuntActId).IsEqualTo(26549u);
            await Assert.That(selectedPlan.Fixture.GatherActId).IsEqualTo(0u);
            await Assert.That(BotOpportunityListing.List(actor).ViableHunts.Any(h => h.QuestId == 266 && h.TargetObjId == prey)).IsTrue();
            await Assert.That(module.CanActivate(Context()).CanActivate).IsTrue();
            for (var wake = 0; wake < 8 && actor.ActiveRequest is not { IsTerminal: false, Action: ActorActionType.Move, MoveOwner: TravelLegDispatch.PursuitMoveOwner }; wake++) await Step();
            Record("changed-world-native-pursuit");
            await Assert.That(actor.ActiveRequest?.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
            await Assert.That(actor.ActiveRequest?.DecisionQuestId).IsEqualTo(266u);
            await Assert.That(actor.ActiveRequest?.TargetId).IsEqualTo(prey);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(266)).IsFalse();
            await Assert.That(giveUps.Count).IsEqualTo(0);
            var hunt = quest.Template.GetComponents(QuestComponentKind.Progress)
                .SelectMany(c => c.ActTemplates).Single(a => a.ActId == 26549);
            await Assert.That(hunt.GetObjective(quest)).IsEqualTo(0);
            // Pose change and manual native damage/ticks are fixture inputs,
            // not an earned-walk or earned-combat claim.
            var interrupted = actor.ActiveRequest!;
            executor.TryGetQuestRuntime(bot.CharacterId, out var beforeInterruption);
            var previousProgress = beforeInterruption!.LastProgressAt;
            actor.Stop();
            GameplayActorTestRig.SetPosition(actor, origin + new Vector3(16, 0, 0));
            clock.Advance(TimeSpan.FromSeconds(6));
            await Step();
            executor.TryGetQuestRuntime(bot.CharacterId, out var consumed);
            await Assert.That(interrupted.State).IsEqualTo(ActorLifecycleState.Interrupted);
            await Assert.That(consumed!.Status).IsEqualTo(BotBehaviorStatus.Failed);
            await Assert.That(consumed.LastProgressAt).IsEqualTo(previousProgress);
            await Assert.That(actor.ActiveRequest is { IsTerminal: false }).IsFalse();
            await Assert.That(actor.AuditTrace.Count(a => a.TraceId == interrupted.TraceId
                && a.Result == ActorLifecycleState.Interrupted)).IsEqualTo(1);
            for (var wake = 0; wake < 8 && !actor.AuditTrace.Any(a => a.Action == ActorActionType.AutoAttack); wake++)
            {
                clock.Advance(TimeSpan.FromSeconds(6)); // Honor the existing suggested wake, no real wait.
                await Step();
            }
            Record("contact-after-bounded-wakes");
            executor.TryGetQuestRuntime(bot.CharacterId, out var contactRuntime);
            Console.WriteLine("HUNT CONTACT " + JsonSerializer.Serialize(new { Battle = actor.Character.IsInBattle,
                Clock = clock.GetUtcNow(), ActorPosition = actor.Character.Transform.World.Position,
                RuntimeStatus = contactRuntime?.Status.ToString(), RuntimePhase = contactRuntime?.CurrentPhase,
                Trace = actor.AuditTrace.TakeLast(12).Select(a => new { a.Action, a.Result, a.Detail }).ToArray() }));
            await Assert.That(actor.AuditTrace.Any(a => a.Action == ActorActionType.AutoAttack
                && a.Result == ActorLifecycleState.Completed)).IsTrue();
            await Assert.That(actor.Character.AutoAttackTask).IsNotNull();
            actor.Stop();
            var victim = session.World.GetNpc(prey)!;
            victim.ReduceCurrentHp(actor.Character, 60);
            victim.OnDamageReceived(actor.Character, 60);
            victim.ReduceCurrentHp(actor.Character, 40);
            victim.OnDamageReceived(actor.Character, 40);
            await Assert.That(victim.Hp).IsEqualTo(0);
            await Assert.That(hunt.GetObjective(quest)).IsEqualTo(1);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(266)).IsFalse();
            await Assert.That(quest.Objectives.All(x => x < 20)).IsTrue();
            var progressFunnel = QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "hunt-credit-ordinary", selectedPlan.Fixture);
            await Assert.That(progressFunnel.HaveCount).IsEqualTo(1);
            await Assert.That(progressFunnel.NeedCount).IsEqualTo(5);
            await Assert.That(progressFunnel.Relevance).IsTrue();
            await Assert.That(QuestObjectiveTargetSelector.IsRelevant(
                BotObservedContext.Capture(actor), selectedPlan.Fixture)).IsFalse(); // No authoritative Character.
            var unknown = selectedPlan.Fixture with { HuntActId = uint.MaxValue };
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "unknown-native-act", unknown).SelectedObjId).IsEqualTo(0u);
            var other = Spawn(3475, origin + new Vector3(17, 0, 0));
            var rejected = QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "wrong-group-and-dead", selectedPlan.Fixture);
            await Assert.That(rejected.SelectedObjId).IsEqualTo(0u);
            await Assert.That(rejected.Candidates.Single(c => c.ObjId == other).QuestRelevant).IsFalse();
            await Assert.That(rejected.Candidates.Single(c => c.ObjId == prey).CombatLegal).IsFalse();
            // Canonical266 is scored: 20 points per hunt credit, score100.
            // Four additional scripted native deaths reach ordinary readiness.
            // Count20 is not twenty required kills; no counter/status is set.
            session.World.RemoveObject(victim);
            region.RemoveObject(victim);
            for (var kill = 1; kill < 5; kill++)
            {
                var next = Spawn(3494, origin + new Vector3(18, 0, 0));
                var npc = session.World.GetNpc(next)!;
                npc.ReduceCurrentHp(actor.Character, 60);
                npc.OnDamageReceived(actor.Character, 60);
                npc.ReduceCurrentHp(actor.Character, 40);
                npc.OnDamageReceived(actor.Character, 40);
                await Assert.That(hunt.GetObjective(quest)).IsEqualTo(kill + 1);
                session.World.RemoveObject(npc);
                region.RemoveObject(npc);
            }
            await Assert.That(quest.GetQuestObjectiveStatus() >= QuestObjectiveStatus.QuestComplete).IsTrue();
            var credited = QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "scored-hunt-ready", selectedPlan.Fixture);
            await Assert.That(credited.HaveCount).IsEqualTo(5);
            await Assert.That(credited.Relevance).IsFalse();
            await Assert.That(credited.SelectedObjId).IsEqualTo(0u);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(266)).IsFalse();
            actor.Character.Quests.ActiveQuests.TryRemove(266, out _);
            await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
                BotObservedContext.Capture(actor), "no-active-quest", selectedPlan.Fixture).Relevance).IsFalse();

        }
        finally
        {
            npcMapField.SetValue(ItemManager.Instance, oldNpcMap);
            packField.SetValue(LootGameData.Instance, oldPacks);
            lootSingleton.SetValue(null, originalLoot);
            actor.PreemptCurrent("private probe cleanup");
            QuestLegActor.ClearAll(); QuestLegActor.SetResolver(null); QuestBehavior.ClearGiveUpMemory();
            for (var i = 0; i < sourceFields.Length; i++) sourceFields[i].SetValue(doodads, sourceBefore[i]);
            doodadSingletonField.SetValue(null, previousDoodadManager);
            QuestFixtureRow.InvalidateAll();
        }
    }
    [Test]
    public async Task CanonicalSingleNpcHunt_OnlyNativeTemplateAndUncreditedActQualify()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestFixtureRow.InvalidateAll();
        var (actor, session) = GameplayActorTestRig.CreateActor("single-hunt-control");
        var origin = new Vector3(32, 32, 0);
        GameplayActorTestRig.SetPosition(actor, origin);
        var region = session.World.GetRegionByPos(origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        // Canonical60: act807, NPC611, count1. Accepted/Progress state is
        // fixture setup; this does not establish eligibility or discovery.
        var quest = new Quest(QuestManager.Instance.GetTemplate(60), actor.Character)
            { Step = QuestComponentKind.Progress, Status = QuestStatus.Progress };
        actor.Character.Quests.ActiveQuests[60] = quest;
        var act = quest.Template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).Single(a => a.ActId == 807);
        var fixture = QuestFixtureRow.FromQuestData(60, act);
        uint Spawn(uint template, float distance)
        {
            var id = session.SpawnNpc(template);
            var npc = session.World.GetNpc(id)!;
            typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc.Transform, session.World.Id);
            typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc, session.World);
            npc.Template = new NpcTemplate { Id = template, Scale = 1f };
            npc.Hp = npc.MaxHp = 100;
            npc.IsVisible = true;
            GameplayActorTestRig.SetNpcPosition(session, id, origin + new Vector3(distance, 0, 0));
            region.AddObject(npc);
            npc.Region = region;
            return id;
        }
        var wanted = Spawn(611, 8);
        var other = Spawn(3475, 2);
        var funnel = QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "single-hunt-native", fixture);
        await Assert.That(funnel.ObjectiveResolved).IsTrue();
        await Assert.That(funnel.SourceResolved).IsTrue();
        await Assert.That(funnel.SelectedObjId).IsEqualTo(wanted);
        await Assert.That(funnel.Candidates.Single(c => c.ObjId == other).QuestRelevant).IsFalse();
        var mismatch = fixture with { HuntNpcTemplate = 3475 };
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "single-hunt-wrong-facts", mismatch).SelectedObjId).IsEqualTo(0u);
        // Explicit native event input, not a killed-NPC claim. Checks that
        // credited ordinary state withdraws the target on the next observation.
        QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, session.World.GetNpc(wanted)!);
        await Assert.That(act.GetObjective(quest)).IsEqualTo(1);
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "single-hunt-credited", fixture).Relevance).IsFalse();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(60)).IsFalse();
    }

    [Test]
    public async Task CanonicalScoredHunt_FiveCreditsRemainUsefulBeforeNativeReadiness()
    {
        GameplayActorTestRig.Seed();
        var (actor, session) = GameplayActorTestRig.CreateActor("scored-hunt-control");
        actor.Character.Level = 40;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        var origin = new Vector3(32, 32, 0);
        GameplayActorTestRig.SetPosition(actor, origin);
        var region = session.World.GetRegionByPos(origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        // Canonical2990 has no Progress component unit requirements.
        // Accepted Progress/level40 and native event inputs are fixture setup.
        var quest = new Quest(QuestManager.Instance.GetTemplate(2990), actor.Character)
            { Step = QuestComponentKind.Progress, Status = QuestStatus.Progress };
        actor.Character.Quests.ActiveQuests[2990] = quest;
        var act = quest.Template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).Single(a => a.ActId == 18801);
        var fixture = QuestFixtureRow.FromQuestData(2990, act);
        var id = session.SpawnNpc(1250);
        var npc = session.World.GetNpc(id)!;
        typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc.Transform, session.World.Id);
        typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc, session.World);
        npc.Template = new NpcTemplate { Id = 1250, Scale = 1f };
        npc.Hp = npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, id, origin + new Vector3(8, 0, 0));
        region.AddObject(npc);
        npc.Region = region;
        for (var credit = 0; credit < 5; credit++)
            QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, npc);
        await Assert.That(act.GetObjective(quest)).IsEqualTo(5);
        await Assert.That(quest.GetQuestObjectiveStatus()).IsEqualTo(QuestObjectiveStatus.NotReady);
        var funnel = QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "scored-five-is-twenty-five-points", fixture);
        await Assert.That(funnel.Relevance).IsTrue();
        await Assert.That(funnel.SelectedObjId).IsEqualTo(id);
        await Assert.That(funnel.NeedCount).IsEqualTo(20);
        await Assert.That(QuestDirector.PlanWake(BotObservedContext.Capture(actor), 2990, actor.Character)
            .Single().Fixture.HuntActId).IsEqualTo(18801u);
        var listing = BotOpportunityListing.List(actor);
        Console.WriteLine("SCORED LIST " + JsonSerializer.Serialize(new {
            Listing = listing, Around = WorldManager.GetAround<Npc>(actor.Character, 25).Select(n => n.ObjId).ToArray(),
            Groups = QuestDirector.ReadObjectiveGroups(quest).Select(g => new { g.ComponentId, g.Active, g.Credited,
                Acts = g.Remaining.Select(a => a.ActId).ToArray() }).ToArray() }));
        await Assert.That(listing.ViableHunts.Any(h =>
            h.QuestId == 2990 && h.TargetObjId == id && h.HaveCount == 5 && h.NeedCount == 20)).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(4003)).IsTrue(); // Ordinary kill starter.
        await Assert.That(listing.ViableHunts.Count(h => h.QuestId == 2990 && h.TargetObjId == id)).IsEqualTo(1);
        await Assert.That(listing.ViableHunts.Count(h => h.QuestId == 4003 && h.TargetObjId == id)).IsEqualTo(1);
        var component = quest.QuestSteps[QuestComponentKind.Progress].Components[13078];
        component.OverrideObjectiveCompleted = true; // Ordinary override fixture control.
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "scored-own-component-withdrawn", fixture).Relevance).IsFalse();
        component.OverrideObjectiveCompleted = false;
        for (var credit = 5; credit < 20; credit++)
            QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, npc);
        await Assert.That(act.GetObjective(quest)).IsEqualTo(20);
        await Assert.That(quest.GetQuestObjectiveStatus() >= QuestObjectiveStatus.QuestComplete).IsTrue();
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "scored-native-ready", fixture).Relevance).IsFalse();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(2990)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CorpseBlockedHunt_TravelsToAnotherSourceOrWaitsWithoutRearming(bool allBlocked)
    {
        GameplayActorTestRig.Seed();
        using var definitions = new NpcSpawnerSearchTestRig();
        QuestBehavior.ClearGiveUpMemory();
        QuestLegActor.ClearAll();
        TravelIntentStore.ClearAll();
        var (actor, session) = GameplayActorTestRig.CreateActor("corpse-source-travel");
        actor.Character.Level = 20;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        var origin = new Vector3(32, 32, 0);
        GameplayActorTestRig.SetPosition(actor, origin);
        var region = session.World.GetRegionByPos(origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        // Accepted quest and authored sources are setup; no kill, loot or credit is supplied.
        var quest = new Quest(QuestManager.Instance.GetTemplate(266), actor.Character)
            { Step = QuestComponentKind.Progress, Status = QuestStatus.Progress };
        actor.Character.Quests.ActiveQuests[266] = quest;
        var act = quest.Template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).Single(a => a.ActId == 26549);
        var fixture = QuestFixtureRow.FromQuestData(266, act);
        session.World.SpawnManager = new SpawnManager(session.World);
        var far = origin + new Vector3(250, 0, 0);
        foreach (var (pos, blocked) in new[] { (origin, true), (far, allBlocked) })
        {
            var variants = definitions.Add(session.World, pos);
            var active = variants.Single(s => s.SpawnerId == 3348);
            if (blocked)
                active.SpawnedNpcs[active.SpawnerId] = [new Npc { TemplateId = 3451, Hp = 0 }];
        }
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var executor = new BotRoamStepExecutor {
            ActorFactory = _ => actor, TimeProvider = clock,
            ActiveActivityProvider = _ => "quest.progress", QuestChosenProvider = _ => 266,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false, EnableWildlifeButcher = false,
            GroundHeightProvider = (_, _) => 0f, NearbyDoodadProvider = (_, _) => []
        };
        var bot = new PlayerBotRuntime(actor.Character, "corpse-source-travel");
        executor.GetOrCreateActor(actor.Character);
        QuestLegActor.SetResolver(executor.TryGetActor);
        try
        {
            for (var wake = 0; wake < 4; wake++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await executor.StepAsync(bot, CancellationToken.None);
            }
            await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character, fixture)).IsTrue();
            await Assert.That(executor.GetBotState(bot.CharacterId)!.QuestTravelTarget)
                .IsEqualTo(allBlocked ? (Vector3?)null : far);
            if (allBlocked)
                await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(0);
            await Assert.That(actor.Character.Quests.ActiveQuests[266]).IsSameReferenceAs(quest);
            await Assert.That(act.GetObjective(quest)).IsEqualTo(0);
        }
        finally
        {
            actor.PreemptCurrent("corpse source test cleanup");
            QuestLegActor.ClearAll(); QuestLegActor.SetResolver(null);
            QuestBehavior.ClearGiveUpMemory(); TravelIntentStore.ClearAll();
        }
    }

    [Test]
    public async Task CanonicalKnownHuntSource_ReferenceControlsAndEarnedReadmission()
    {
        GameplayActorTestRig.Seed();
        var (actor, session) = GameplayActorTestRig.CreateActor("known-hunt-controls");
        actor.Character.Level = 20;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        var origin = new Vector3(32, 32, 0);
        GameplayActorTestRig.SetPosition(actor, origin);
        var region = session.World.GetRegionByPos(origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        // Accepted Progress/known coordinates are fixture setup. No living
        // target is created until the explicit changed-world readmission step.
        var quest = new Quest(QuestManager.Instance.GetTemplate(266), actor.Character)
            { Step = QuestComponentKind.Progress, Status = QuestStatus.Progress };
        actor.Character.Quests.ActiveQuests[266] = quest;
        var act = quest.Template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).Single(a => a.ActId == 26549);
        var fixture = QuestFixtureRow.FromQuestData(266, act);
        session.World.SpawnManager = new SpawnManager(session.World);
        var map = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session.World.SpawnManager)!;
        void Source(uint key, uint template, Vector3 position) => map[key] = [new NpcSpawner {
            Id = key, UnitId = template, Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z } }];
        Source(98701, 3475, origin + new Vector3(2, 0, 0)); // Wrong group, even though nearest.
        await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character, fixture)).IsFalse();
        Source(98702, 3494, new Vector3(float.NaN, 32, 0));
        await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character, fixture)).IsFalse();
        map.Clear();
        // Another world's matching coordinate cannot establish this world's search.
        var (_, other) = GameplayActorTestRig.CreateActor("foreign-hunt-world");
        other.World.SpawnManager = new SpawnManager(other.World);
        var otherMap = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(other.World.SpawnManager)!;
        otherMap[98703] = [new NpcSpawner { Id = 98703, UnitId = 3494,
            Position = new WorldSpawnPosition { X = 182, Y = 32, Z = 0 } }];
        await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character, fixture)).IsFalse();
        Source(98704, 3494, origin + new Vector3(150, 0, 0));
        Source(98705, 3451, origin + new Vector3(200, 0, 0));
        await Assert.That(QuestBehavior.TryNearestHuntNpcSpawnerPosition(session.World, fixture, origin,
            out var sourceTemplate, out var sourcePosition)).IsTrue();
        await Assert.That(sourceTemplate).IsEqualTo(3494u);
        await Assert.That(sourcePosition).IsEqualTo(origin + new Vector3(150, 0, 0));
        await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character, fixture)).IsTrue();
        await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character,
            fixture with { HuntActId = uint.MaxValue })).IsFalse();
        var component = quest.QuestSteps[QuestComponentKind.Progress].Components[7252];
        component.OverrideObjectiveCompleted = true;
        await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character, fixture)).IsFalse();
        component.OverrideObjectiveCompleted = false;
        QuestLegActor.Stage(actor.Character.Id, actor);
        try
        {
            var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
            var bot = new PlayerBotRuntime(actor.Character, "known-hunt-controls");
            var context = new BotActivityContext { Bot = bot, GameHour = 12, ActiveActivity = null };
            await Assert.That(module.CanActivate(context).CanActivate).IsTrue();
            await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(266u);
            var listing = BotOpportunityListing.List(actor);
            await Assert.That(listing.ViableHunts.Count).IsEqualTo(0);
            await Assert.That(listing.ActiveWork.Single(w => w.QuestId == 266).Reason
                .EndsWith("-search-location-known", StringComparison.Ordinal)).IsTrue();
            // Existing finite-retirement signal is fixture input; static
            // coordinates alone must not readmit an earned give-up.
            module.NotifyQuestGiveUp(actor.Character.Id,
                new QuestGiveUp(266, "GIVE-UP/UNRESOLVED-TARGET/no-selected-prey", 10, 10));
            _ = module.CanActivate(context);
            await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(0u);
            var id = session.SpawnNpc(3494); // Explicit actual world availability change.
            var npc = session.World.GetNpc(id)!;
            typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc.Transform, session.World.Id);
            typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc, session.World);
            npc.Template = new NpcTemplate { Id = 3494, Scale = 1f };
            npc.Hp = npc.MaxHp = 100;
            npc.IsVisible = true;
            GameplayActorTestRig.SetNpcPosition(session, id, origin + new Vector3(8, 0, 0));
            region.AddObject(npc);
            npc.Region = region;
            await Assert.That(module.CanActivate(context).CanActivate).IsTrue();
            await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(266u);
            await Assert.That(QuestBehavior.HasPerceivedLivingPrey(actor, BotObservedContext.Capture(actor), fixture)).IsTrue();
            // Native event credit inputs, no kill/loot/completion claim here.
            for (var credit = 0; credit < 5; credit++)
                QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, npc);
            await Assert.That(quest.GetQuestObjectiveStatus() >= QuestObjectiveStatus.QuestComplete).IsTrue();
            await Assert.That(QuestBehavior.HasKnownHuntSearch(actor.Character, fixture)).IsFalse();
            var leg = new QuestLegContext(actor, new QuestDecisionScenario.QuestOptions(), 266,
                QuestFixtureRow.FromQuestData(266), null) { Observation = BotObservedContext.Capture(actor) };
            await Assert.That(QuestBehavior.HasUncreditedSourceOutsidePerception(leg, leg.Fixture)).IsFalse();
            await Assert.That(actor.Character.Quests.HasQuestCompleted(266)).IsFalse();
        }
        finally
        {
            QuestLegActor.ClearAll();
            QuestLegActor.SetResolver(null);
            QuestBehavior.ClearGiveUpMemory();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CanonicalHunt_NonzeroGroundAndElevatedPrey_FiniteRecovery(bool elevated)
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestBehavior.ClearGiveUpMemory();
        QuestLegActor.ClearAll();
        var (actor, session) = GameplayActorTestRig.CreateActor("private-next266-probe");
        actor.Character.Level = 20;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        var origin = new Vector3(32, 32, 10);
        GameplayActorTestRig.SetPosition(actor, origin);
        var region = session.World.GetRegionByPos(origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        uint Spawn(uint template, Vector3 position)
        {
            var id = session.SpawnNpc(template);
            var npc = session.World.GetNpc(id)!;
            typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc.Transform, session.World.Id);
            typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(npc, session.World);
            npc.Template = new NpcTemplate { Id = template, Scale = 1f };
            npc.Hp = npc.MaxHp = 100;
            npc.IsVisible = true;
            GameplayActorTestRig.SetNpcPosition(session, id, position);
            region.AddObject(npc);
            npc.Region = region;
            return id;
        }
        Spawn(3520, origin + new Vector3(2, 0, 0));
        session.World.SpawnManager ??= new SpawnManager(session.World);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session.World.SpawnManager)!;
        spawners[98666] = [new NpcSpawner { Id = 98666, UnitId = 3494,
            Position = new WorldSpawnPosition { X = 182, Y = 32, Z = 0 } }];
        // Exact canonical 8130 source: template1221/group1882/func1777/detail655.
        // Existing helper supplies native manager maps; overwrite its simplified
        // func/detail with the verified canonical row. No world doodad/credit.
        var doodadSingletonField = typeof(Singleton<DoodadManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousDoodadManager = doodadSingletonField.GetValue(null);
        if (previousDoodadManager == null)
            GameplayActorTestRig.SeedGatherDoodadSource(1221, 1882, 655, 8130);
        var doodads = DoodadManager.Instance;
        var sourceFields = new[] { "_templates", "_allFuncGroups", "_funcsByGroups", "_funcsById", "_funcTemplates" }
            .Select(n => typeof(DoodadManager).GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!).ToArray();
        var sourceBefore = sourceFields.Select(f => f.GetValue(doodads)).ToArray();
        // Existing empty headless rig maps are isolated before helper writes.
        for (var i = 0; i < sourceFields.Length; i++)
            sourceFields[i].SetValue(doodads, Activator.CreateInstance(sourceFields[i].FieldType));
        GameplayActorTestRig.SeedGatherDoodadSource(1221, 1882, 655, 8130);
        var nativeFunc = new DoodadFunc { GroupId = 1882, FuncId = 655, FuncKey = 1777,
            FuncType = "DoodadFuncLootItem", NextPhase = 1889, SkillId = 0 };
        ((Dictionary<uint, List<DoodadFunc>>)sourceFields[2].GetValue(doodads)!)[1882] = [nativeFunc];
        ((Dictionary<uint, DoodadFunc>)sourceFields[3].GetValue(doodads)!).Clear();
        ((Dictionary<uint, DoodadFunc>)sourceFields[3].GetValue(doodads)!)[1777] = nativeFunc;
        ((Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)sourceFields[4].GetValue(doodads)!)["DoodadFuncLootItem"][655]
            = new DoodadFuncLootItem { ItemId = 8130, CountMin = 1, CountMax = 1, Percent = 10000, RemainTime = 100000 };
        var lootSingleton = typeof(Singleton<LootGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalLoot = lootSingleton.GetValue(null);
        _ = LootGameData.Instance; // Direct DoodadFuncLootItem has no pack rows.
        // Non-objective loot is omitted in this headless kill-credit fixture.
        // Empty native caches permit ordinary death; no quest item is inserted.
        var npcMapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldNpcMap = npcMapField.GetValue(ItemManager.Instance);
        npcMapField.SetValue(ItemManager.Instance, new Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc>>());
        var packField = typeof(LootGameData).GetField("_lootPacks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldPacks = packField.GetValue(LootGameData.Instance);
        packField.SetValue(LootGameData.Instance, new Dictionary<uint, AAEmu.Game.Models.Game.Items.Loots.LootPack>());

        Console.WriteLine("PROBE266 LOADER " + (Singleton<LootGameData>.PeekInstance != null));
        QuestFixtureRow.InvalidateAll();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var giveUps = new List<string>();
        BotGoalArbiter? arbiter = null;
        var executor = new BotRoamStepExecutor {
            ActiveActivityProvider = id => arbiter?.GetActiveActivity(id),
            ActorFactory = _ => actor, TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false, EnableWildlifeButcher = false,
            GroundHeightProvider = (_, _) => 10f, NearbyDoodadProvider = (_, _) => [],
            QuestChosenProvider = module.GetChosenQuest, QuestTerminalSink = module.NotifyQuestTerminal,
            QuestGiveUpSink = (id, result) => { giveUps.Add(result.ToString()!); module.NotifyQuestGiveUp(id, result); }
        };
        var presence = new PresenceRoamActivityModule(new BotScheduleOptions { Enabled = false },
            new RoamRouteScheduleBehavior(executor, (_, _) => 10f), executor, PlayerBotMetadata.Empty);
        arbiter = new BotGoalArbiter([module, presence]);
        executor.GetOrCreateActor(actor.Character);
        QuestLegActor.SetResolver(executor.TryGetActor);
        var bot = new PlayerBotRuntime(actor.Character, "private-next266-probe");
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);
        BotActivityContext Context() => new() { Bot = bot, GameHour = 12, ActiveActivity = arbiter.GetActiveActivity(bot.CharacterId) };
        void Record(string phase) => Console.WriteLine("PROBE266 " + JsonSerializer.Serialize(new {
            phase, listing = BotOpportunityListing.List(actor), decision = module.CanActivate(Context()),
            chosen = module.GetChosenQuest(bot.CharacterId), activity = arbiter.GetActiveActivity(bot.CharacterId),
            request = actor.ActiveRequest?.TraceId, action = actor.ActiveRequest?.Action.ToString(),
            owner = actor.ActiveRequest?.MoveOwner, purpose = actor.ActiveRequest?.DecisionQuestId,
            terminal = actor.ActiveRequest?.IsTerminal, giveUps = giveUps.ToArray()
        }));
        async Task Step() { clock.Advance(TimeSpan.FromMilliseconds(100)); await steps.StepAsync(bot, CancellationToken.None); }
        try
        {
            Record("before-scripted-accept");
            await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(266u);
            var accepted = actor.AcceptQuest(266, QuestAcceptorType.Npc, 3520);
            await Assert.That(accepted.State).IsEqualTo(ActorLifecycleState.Completed);
            var quest = actor.Character.Quests.ActiveQuests[266];
            Console.WriteLine("PROBE266 GROUPS " + JsonSerializer.Serialize(QuestDirector.ReadObjectiveGroups(quest).Select(g => new { g.ComponentId, g.Active, g.Credited, Remaining = g.Remaining.Select(a => new { a.ActId, Type = a.GetType().Name, a.DetailId }).ToArray() }).ToArray()));
            var prey = Spawn(3451, origin + new Vector3(2, 0, elevated ? 10 : 0));
            for (var wake = 0; wake < 1200; wake++)
            {
                await Step();
                if (!elevated && actor.AuditTrace.Any(a => a.Action == ActorActionType.AutoAttack)) break;
                if (elevated && giveUps.Count != 0) break;
            }
            Record(elevated ? "elevated-final" : "ground-final");
            var attacked = actor.AuditTrace.Any(a => a.Action == ActorActionType.AutoAttack && a.Result == ActorLifecycleState.Completed);
            Console.WriteLine("VERTICAL-PROBE " + JsonSerializer.Serialize(new { elevated, attacked,
                actorPos = actor.Character.Transform.World.Position.ToString(),
                preyPos = session.World.GetNpc(prey)!.Transform.World.Position.ToString(),
                flat = AAEmu.Game.Utils.MathUtil.CalculateDistance(actor.Character.Transform.World.Position, session.World.GetNpc(prey)!.Transform.World.Position, false),
                nativeDistance = actor.Character.GetDistanceTo(session.World.GetNpc(prey), true),
                chosen = module.GetChosenQuest(bot.CharacterId), giveUps = giveUps.ToArray(),
                terminals = actor.AuditTrace.Where(a => a.Action == ActorActionType.Move).Select(a => new { a.Result, a.Detail }).ToArray() }));
            await Assert.That(attacked).IsEqualTo(!elevated);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(266)).IsFalse();
            if (elevated)
            {
                await Assert.That(giveUps.Count).IsGreaterThan(0);
                await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsNotEqualTo(266u);
            }
        }
        finally
        {
            npcMapField.SetValue(ItemManager.Instance, oldNpcMap);
            packField.SetValue(LootGameData.Instance, oldPacks);
            lootSingleton.SetValue(null, originalLoot);
            actor.PreemptCurrent("private probe cleanup");
            QuestLegActor.ClearAll(); QuestLegActor.SetResolver(null); QuestBehavior.ClearGiveUpMemory();
            for (var i = 0; i < sourceFields.Length; i++) sourceFields[i].SetValue(doodads, sourceBefore[i]);
            doodadSingletonField.SetValue(null, previousDoodadManager);
            QuestFixtureRow.InvalidateAll();
        }
    }

}
