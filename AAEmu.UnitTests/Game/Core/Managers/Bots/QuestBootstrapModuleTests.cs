using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// Copper-bootstrap quest driver: the QuestBootstrap module gate matrix +
/// the QuestDecisionScenario advance/accept routing through the existing
/// actor actions (canonical 254 delivery chain via the pilot rig).
/// </summary>
[NotInParallel]
public class QuestBootstrapModuleTests
{
    private uint _nextObjId = 0x73000;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
    }

    private static PlayerBotRuntime NewBot(GameplayActor actor)
        => new(actor.Character, "quest-bootstrap-tests");

    private static BotActivityContext ContextFor(PlayerBotRuntime bot, string? active = null)
        => new() { Bot = bot, GameHour = 12f, ActiveActivity = active };

    private static QuestBootstrapActivityModule EnabledModule(Func<ServerPressure>? pressure = null)
        => new(new QuestBootstrapModuleOptions { Enabled = true }, pressure);

    private static void JoinActorRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        if (region != null)
        {
            region.AddObject(character);
            character.Region = region;
        }
    }

    private uint SpawnHubNpc(HeadlessSession session, uint templateId, Vector3 position)
    {
        var npc = new Npc
        {
            ObjId = _nextObjId++,
            TemplateId = templateId,
            Hp = 100,
            MaxHp = 100,
            Template = new NpcTemplate { Id = templateId, Scale = 1f }
        };
        session.World.AddObject(npc);
        npc.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region != null)
        {
            region.AddObject(npc);
            npc.Region = region;
        }
        return npc.ObjId;
    }

    // ------------------------------------------------------------ gate matrix

    [Test]
    public async Task Priority_Is58_BetweenFarmAndContest()
    {
        await Assert.That(new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions()).Priority).IsEqualTo(58);
    }

    [Test]
    public async Task CanActivate_DisabledGate_Denies()
    {
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = false });
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-off");
        GameplayActorTestRig.SetMoney(actor, 0);

        await Assert.That(module.CanActivate(ContextFor(NewBot(actor))).CanActivate).IsFalse();
    }

    [Test]
    public async Task CanActivate_BrokeQuestless_AllowsNamedActivity()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-broke");
        GameplayActorTestRig.SetMoney(actor, 0);

        var first = EnabledModule().CanActivate(ContextFor(NewBot(actor)));
        var second = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(first.CanActivate).IsTrue();
        await Assert.That(first.ActivityName).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(second.ActivityName).IsEqualTo(first.ActivityName);
    }

    [Test]
    public async Task CanActivate_DeadBot_Denies()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-dead");
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.Hp = 0;

        var decision = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("dead");
    }

    [Test]
    public async Task CanActivate_InBattle_Denies()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-battle");
        GameplayActorTestRig.SetMoney(actor, 0);
        actor.Character.IsInBattle = true;

        var decision = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("battle");
    }

    [Test]
    public async Task CanActivate_HighPressure_Denies()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-pressure");
        GameplayActorTestRig.SetMoney(actor, 0);

        var decision = EnabledModule(() => ServerPressure.High).CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).Contains("pressure");
    }

    [Test]
    public async Task CanActivate_ActiveQuestAllows_DespiteRichCopper()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-active-rich");
        actor.Character.Level = 3;
        actor.Character.Hp = actor.Character.MaxHp;
        GameplayActorTestRig.SetMoney(actor, 10_000);
        var accept = actor.AcceptQuest(LevelingLoopScenario.SeedQuestDeliveryId,
            QuestAcceptorType.Npc, LevelingLoopScenario.SeedOffererNpcTemplateId,
            "qb-active-rich-accept");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var decision = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsTrue();
        await Assert.That(decision.ActivityName).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
    }

    [Test]
    public async Task CanActivate_RichQuestless_Denies()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-rich");
        GameplayActorTestRig.SetMoney(actor, 10_000);

        var decision = EnabledModule().CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
    }

    [Test]
    public async Task Arbitrate_QuestBeatsNeedsAndRoam_BelowContest()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-arbiter");
        actor.Character.Level = 3;
        actor.Character.Hp = actor.Character.MaxHp;
        GameplayActorTestRig.SetMoney(actor, 0);
        var accept = actor.AcceptQuest(LevelingLoopScenario.SeedQuestDeliveryId,
            QuestAcceptorType.Npc, LevelingLoopScenario.SeedOffererNpcTemplateId,
            "qb-arbiter-accept");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var bot = NewBot(actor);
        var quest = EnabledModule();
        var needs = new NeedsProbe();
        var roam = new RoamProbe();
        var contest = new ContestProbe();
        var arbiter = new BotGoalArbiter([contest, quest, needs, roam]);

        var result = arbiter.Arbitrate(bot, gameHour: 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.ModuleName).IsEqualTo("QuestBootstrap");
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("quest.progress");
        await Assert.That(roam.Activations, "lower-priority roam must never activate").IsEqualTo(0);
        await Assert.That(needs.Activations, "lower-priority needs must never activate").IsEqualTo(0);
        await Assert.That(contest.Activations, "denied contest must never activate").IsEqualTo(0);
    }

    private sealed class RoamProbe : IBotActivityModule
    {
        public string Name => "PresenceRoam";
        public int Priority => 50;
        public int Activations { get; private set; }
        public BotActivityDecision CanActivate(BotActivityContext context) => BotActivityDecision.Allow("presence.roam");
        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity("presence.roam", Name);
        }
    }

    private sealed class NeedsProbe : IBotActivityModule
    {
        public string Name => "NeedsFarm";
        public int Priority => 55;
        public int Activations { get; private set; }
        public BotActivityDecision CanActivate(BotActivityContext context) => BotActivityDecision.Allow("needs.farm");
        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity("needs.farm", Name);
        }
    }

    private sealed class ContestProbe : IBotActivityModule
    {
        public string Name => "FishingContest";
        public int Priority => 60;
        public int Activations { get; private set; }
        public BotActivityDecision CanActivate(BotActivityContext context) => BotActivityDecision.Deny("contest not open");
        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity("fishing.contest", Name);
        }
    }

    [Test]
    public async Task Activate_ReturnsStableActivityIdentity()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-activate");

        var activity = EnabledModule().Activate(ContextFor(NewBot(actor)));

        await Assert.That(activity.Name).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(activity.ModuleName).IsEqualTo("QuestBootstrap");
    }

    // ------------------------------------------------------------ scenario legs

    [Test]
    public async Task Run_NoQuestsNoNpcs_FailsClosedAtDecide()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("qb-empty");
        GameplayActorTestRig.SetMoney(actor, 0);

        var result = QuestDecisionScenario.Run(actor, (_, _) => []);

        await Assert.That(result.WorkSelected).IsFalse();
        await Assert.That(result.FailStage).IsEqualTo("DECIDE");
    }

    [Test]
    public async Task Run_NearbyOfferer_AcceptsLowestInBand()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("qb-accept");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        GameplayActorTestRig.SetMoney(actor, 0);
        var offererObjId = SpawnHubNpc(session, LevelingLoopScenario.SeedOffererNpcTemplateId, new Vector3(2, 0, 0));

        var result = QuestDecisionScenario.Run(actor,
            (me, radius) => session.World.GetAllNpcs(),
            new QuestDecisionScenario.QuestOptions { CycleId = "qb-accept-1" });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AcceptQuest);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(LevelingLoopScenario.SeedQuestDeliveryId)).IsTrue();
    }

    [Test]
    public async Task Run_ActiveQuest_AdvancesStepMachine()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("qb-advance");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        var accept = actor.AcceptQuest(LevelingLoopScenario.SeedQuestDeliveryId,
            QuestAcceptorType.Npc, LevelingLoopScenario.SeedOffererNpcTemplateId,
            "qb-advance-accept");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "qb-advance-1" });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AdvanceQuest);
        await Assert.That(result.Request?.State).IsEqualTo(ActorLifecycleState.Completed);
    }
    [Test]
    public async Task Run_Quest4438_AcceptAdvanceTurnIn_GrantsDesign()
    {
        // Quest 4438 "논두렁 밭고랑" (L7, accept/report NPC 9789, delivery leg,
        // supply component 19404 grants design 15596): its start component
        // 19356 carries unit_reqs kind-31 CompleteQuestContext(4439), so the
        // full spine 4415→4479→4417→4424→4439→4438 is a live-E2E walk; here the
        // prereq completion is the engine's own completed-flag record and the
        // test proves accept→advance→turn-in→design through normal mechanics.
        const uint Quest4438 = 4438;
        const uint Quest4439 = 4439;
        const uint Npc9789 = 9789;
        const uint Design15596 = 15596;
        var (actor, session) = GameplayActorTestRig.CreateActor("qb-4438");
        var character = session.Character;
        character.Level = 7;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        GameplayActorTestRig.SetMoney(actor, 0);
        character.Quests!.SetCompletedQuestFlag(Quest4439, true);
        GameplayActorTestRig.RegisterPlainItemTemplate(Design15596);
        var npcObjId = session.SpawnNpc(Npc9789);
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new Vector3(2, 0, 0));

        // Wake 1: discover + accept 4438 from NPC 9789.
        await Assert.That(AAEmu.Game.Core.Managers.QuestManager.Instance.GetTemplate(Quest4438) != null).IsTrue();
        await Assert.That(AAEmu.Game.Core.Managers.QuestManager.Instance.GetQuestsOfferedByNpc(Npc9789).Count).IsGreaterThan(0);
        var probe = actor.DiscoverQuests(npcObjId, "qb-4438-probe");
        await Assert.That(probe.State).IsEqualTo(ActorLifecycleState.Completed);
        var probeCount = probe.Result is QuestDiscoveryResult pr ? pr.Offerings.Count : -1;
        await Assert.That(probeCount).IsGreaterThan(0);
        var accept = QuestDecisionScenario.Run(actor,
            (me, radius) => session.World.GetAllNpcs(),
            new QuestDecisionScenario.QuestOptions { CycleId = "qb-4438-1", BandMax = 10 });
        await Assert.That(accept.WorkSelected).IsTrue();
        await Assert.That(character.Quests!.ActiveQuests.ContainsKey(Quest4438)).IsTrue();

        // Wake 2+: advance drains start/supply into progress, then turn in at
        // 9789 until the design lands (bounded — delivery-style, no kills).
        for (var wake = 2; wake <= 6; wake++)
        {
            _ = QuestDecisionScenario.Run(actor, (me, radius) => session.World.GetAllNpcs(),
                new QuestDecisionScenario.QuestOptions { CycleId = $"qb-4438-{wake}", BandMax = 10 });
            if (character.Quests!.HasQuestCompleted(Quest4438))
                break;
        }

        await Assert.That(character.Quests!.HasQuestCompleted(Quest4438)).IsTrue();
        await Assert.That(character.Inventory.GetItemsCount(Design15596)).IsGreaterThan(0);
    }

    // ------------------------------------------------------------ executor quest leg (0a)

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateQuestLegRig(
        (GameplayActor Actor, HeadlessSession Session) rigged,
        string activity,
        Func<Character, float, IEnumerable<Npc>>? nearbyNpcs = null)
    {
        var (actor, _) = rigged;
        var runtime = new PlayerBotRuntime(actor.Character, "quest-leg");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            EnableWildlifeHunt = true,
            EnableWildlifeButcher = true,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            ActiveActivityProvider = _ => activity,
            NearbyNpcProvider = nearbyNpcs,
            NearbyDoodadProvider = (_, _) => [],
            UnitResolver = (c, id) => c.ParentWorld?.GetUnit(id),
            DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
        };
        return (executor, actor, runtime, clock);
    }

    [Test]
    public async Task StepAsync_QuestActiveAndAdvanceLands_SetsQuestLegFlag()
    {
        var rigged = GameplayActorTestRig.CreateActor("qb-leg-advance");
        var (actor, session) = rigged;
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        var accept = actor.AcceptQuest(LevelingLoopScenario.SeedQuestDeliveryId,
            QuestAcceptorType.Npc, LevelingLoopScenario.SeedOffererNpcTemplateId,
            "qb-leg-advance-accept");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var (executor, _, runtime, clock) = CreateQuestLegRig(rigged, "quest.progress",
            nearbyNpcs: (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.QuestLegActive ?? false).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AdvanceQuest
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task StepAsync_QuestActive_PreemptsWildlifeAcquisitionAndRoute()
    {
        var rigged = GameplayActorTestRig.CreateActor("qb-leg-preempt");
        var (actor, session) = rigged;
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var accept = actor.AcceptQuest(LevelingLoopScenario.SeedQuestDeliveryId,
            QuestAcceptorType.Npc, LevelingLoopScenario.SeedOffererNpcTemplateId,
            "qb-leg-preempt-accept");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var wildlife = new Npc
        {
            ObjId = 0x71001,
            Hp = 100,
            MaxHp = 100,
            Faction = new SystemFaction { Id = (FactionsEnum)115 }
        };
        wildlife.Transform.Local.SetPosition(new Vector3(2, 0, 0));
        var (executor, _, runtime, clock) = CreateQuestLegRig(rigged, "quest.progress",
            nearbyNpcs: (_, _) => [wildlife]);
        executor.SetRoamRoute(runtime.Character,
            new BotPath([new Vector3(50f, 0f, 0f)], BotPath.LoopMode.Loop));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.QuestLegActive ?? false).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.TargetNpcObjId ?? 1u).IsEqualTo(0u);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move)).IsFalse();
    }

    [Test]
    public async Task StepAsync_QuestInactive_DoesNotSetFlag_AndNeedsLegUnaffected()
    {
        var rigged = GameplayActorTestRig.CreateActor("qb-leg-idle");
        var (actor, session) = rigged;
        JoinActorRegion(session);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var (executor, _, runtime, clock) = CreateQuestLegRig(rigged, "presence.roam",
            nearbyNpcs: (_, _) => []);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.QuestLegActive ?? true).IsFalse();
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.NeedsLegActive ?? true).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AdvanceQuest)).IsFalse();
    }
}
