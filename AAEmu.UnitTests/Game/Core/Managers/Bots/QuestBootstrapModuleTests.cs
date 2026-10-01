using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
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
    // ------------------------------------------------------------ quest-251 advance drive
    // The advance-leg tests drive staged quest 251 (KillX, Progress, 0/3
    // credited): its plan resolves and its advance genuinely lands. Quest 254
    // (the old drive) goes Ready on accept — the AdvanceEnter gate deliberately
    // withholds advance on Ready quests, so 254 can no longer prove the
    // advance path. Seeders mirror QuestAdvanceWithdrawTests (idempotent).
    private const uint AdvanceQuestId = 251;
    private const uint AdvanceQuestGatherActId = 10473;
    private const uint BoarTemplateId = 3475;
    private const uint BoarLootPackId = 4530;
    private const uint BoarMeatItemId = 4058;

    private static void SeedAdvanceQuestShape()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(AdvanceQuestId, out var template))
        {
            template = new QuestTemplate { Id = AdvanceQuestId, Level = 2 };
            questTemplates[AdvanceQuestId] = template;
        }
        if (template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == BoarMeatItemId))
        {
            SeedAdvanceQuestLootLink();
            return;
        }
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        const uint progressComponentId = 2510901;
        if (!componentTemplates.TryGetValue(progressComponentId, out var component))
        {
            component = new QuestComponentTemplate(template) { Id = progressComponentId, KindId = QuestComponentKind.Progress };
            componentTemplates[progressComponentId] = component;
        }
        if (!template.Components.ContainsKey(progressComponentId))
            template.Components[progressComponentId] = component;
        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ItemId == BoarMeatItemId))
        {
            var act = new QuestActObjItemGather(component)
            {
                ActId = AdvanceQuestGatherActId,
                DetailId = 616,
                DetailType = nameof(QuestActObjItemGather),
                Count = 3,
                ItemId = BoarMeatItemId,
            };
            component.ActTemplates.Add(act);
            var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
                .GetField("_actTemplatesByDetailType", flags)!.GetValue(manager)!;
            if (!actsByType.TryGetValue(nameof(QuestActObjItemGather), out var acts))
            {
                acts = [];
                actsByType[nameof(QuestActObjItemGather)] = acts;
            }
            acts[616] = act;
        }
        SeedAdvanceQuestLootLink();
    }

    private static void SeedAdvanceQuestLootLink()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(BoarTemplateId, out var rows) || rows.All(r => r.LootPackId != BoarLootPackId))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = BoarTemplateId,
                NpcId = BoarTemplateId,
                LootPackId = BoarLootPackId,
                DefaultPack = true,
            });
            map[BoarTemplateId] = rows;
        }
        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(BoarLootPackId, out var pack) || pack.Loots.All(l => l.ItemId != BoarMeatItemId))
        {
            var loot = new Loot
            {
                Id = BoarLootPackId,
                Group = 0,
                ItemId = BoarMeatItemId,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = BoarLootPackId,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[BoarLootPackId] = new LootPack
                {
                    Id = BoarLootPackId,
                    Loots = [loot],
                    LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
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

    private static void StageAdvanceQuest(GameplayActor actor)
    {
        SeedAdvanceQuestShape();
        var template = QuestManager.Instance.GetTemplate(AdvanceQuestId)
            ?? throw new InvalidOperationException($"quest template {AdvanceQuestId} not seeded");
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
        quest.Objectives = [0, 0, 0, 0, 0];
        actor.Character.Quests.ActiveQuests[AdvanceQuestId] = quest;
        QuestFixtureRow.InvalidateAll();
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
        // Quest 254 (the original drive) lands Ready on accept, and the
        // AdvanceEnter gate withholds advance on Ready quests — so the
        // advance path is driven on staged quest 251 (Progress, uncredited),
        // whose advance genuinely lands.
        var (actor, session) = GameplayActorTestRig.CreateActor("qb-advance");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        StageAdvanceQuest(actor);

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
        // Staged quest 251 (Progress, uncredited): its advance genuinely
        // lands — quest 254 goes Ready on accept and can no longer drive this.
        var rigged = GameplayActorTestRig.CreateActor("qb-leg-advance");
        var (actor, session) = rigged;
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        StageAdvanceQuest(actor);
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
        // Staged quest 251 (Progress, uncredited): its advance genuinely
        // lands, so the quest leg owns the wake and preempts hunt + route.
        var rigged = GameplayActorTestRig.CreateActor("qb-leg-preempt");
        var (actor, session) = rigged;
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        JoinActorRegion(session);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageAdvanceQuest(actor);
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
