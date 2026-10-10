using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
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
using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// E-66 production wiring: the two missing plugs.
/// (a) <see cref="QuestLegActor.SetResolver"/> is wired in the production
/// composition root to the executor's non-creating read — resolving with a
/// live executor returns its actor, and null when the bot has no state
/// (never creates).
/// (b) Landed quest terminals flow into the owning module's completed-memory
/// through the owned path (executor sink → runtime sink → module) — a quest
/// completed through the real quest leg is never re-picked, while non-quest
/// terminals (owned Move holds) feed nothing.
/// </summary>
[NotInParallel]
public class QuestTerminalWiringTests
{
    private static readonly Vector3 SpawnPos = new(32, 32, 0);
    private const uint OfferNpcTemplate = 91_830;
    private const uint ReportNpcTemplate = 91_833;
    private const uint FirstQuestId = 91_831;
    private const uint SecondQuestId = 91_832;
    private const uint FirstStartComponent = 91_834;
    private const uint FirstProgressComponent = 91_835;
    private const uint FirstReadyComponent = 91_836;
    private const uint SecondStartComponent = 91_837;
    private const uint SecondProgressComponent = 91_838;
    private const uint SecondReadyComponent = 91_839;

    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    [After(Test)]
    public void TearDown()
    {
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    [Test]
    public async Task ProductionResolver_ReturnsLiveActor_NullWhenNoState()
    {
        var (actor, _) = CreateAreaActor("wire-resolver");
        BotRoamStepExecutor executor = new() { ActorFactory = _ => actor };

        // The production factory shape (Program.cs): the non-creating read.
        QuestLegActor.SetResolver(executor.TryGetActor);

        // No state yet: null, and the actor's bot is still unknown.
        await Assert.That(QuestLegActor.Resolve(actor.Character.Id)).IsNull();

        _ = executor.GetOrCreateActor(actor.Character);

        await Assert.That(QuestLegActor.Resolve(actor.Character.Id)).IsSameReferenceAs(actor);
        await Assert.That(QuestLegActor.Resolve(actor.Character.Id + 10_000)).IsNull();
    }

    [Test]
    public async Task ProductionResolver_TryGetActor_NeverCreatesState()
    {
        var (actor, _) = CreateAreaActor("wire-nocreate");
        BotRoamStepExecutor executor = new() { ActorFactory = _ => actor };

        // A read for an unknown bot must not materialize executor state.
        await Assert.That(executor.TryGetActor(0x0E66u)).IsNull();
        await Assert.That(executor.GetBotState(0x0E66u)).IsNull();
    }

    [Test]
    public async Task TerminalFeed_CompletesThroughOwnedPath_ModuleMovesOn()
    {
        var (actor, session) = CreateAreaActor("wire-terminal");
        GameplayActorTestRig.SeedQuestHunt(FirstQuestId, FirstStartComponent, FirstProgressComponent,
            FirstReadyComponent, OfferNpcTemplate, Boar3475, ReportNpcTemplate, huntCount: 1, level: 1);
        GameplayActorTestRig.SeedQuestHunt(SecondQuestId, SecondStartComponent, SecondProgressComponent,
            SecondReadyComponent, OfferNpcTemplate, Boar3475, ReportNpcTemplate, huntCount: 1, level: 1);
        QuestFixtureRow.Invalidate(FirstQuestId);
        QuestFixtureRow.Invalidate(SecondQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        var runtime = new PlayerBotRuntime(actor.Character, "wire-terminal");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            EnableWildlifeHunt = false,
            EnableWildlifeButcher = false,
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => QuestBootstrapActivityModule.ActivityName,
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
        };

        // Production shape: the module reads the listing off the executor's
        // LIVE actor; landed terminals feed the module's completed-memory.
        // The executor hosts state once the scheduler steps the bot (first
        // wake materializes it); the resolver stays null before that.
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        QuestLegActor.SetResolver(executor.TryGetActor);
        executor.QuestTerminalSink = module.NotifyQuestTerminal;
        _ = executor.GetOrCreateActor(actor.Character);
        var bot = new PlayerBotRuntime(actor.Character, "wire-terminal");
        var context = new BotActivityContext { Bot = bot, GameHour = 12f };
        _ = module.CanActivate(context);
        var first = module.GetChosenQuest(actor.Character.Id);
        await Assert.That(first).IsEqualTo(FirstQuestId);

        // Complete the pick through the REAL engine path: accept, credit the
        // hunt objective through the engine event, advance to Ready.
        var accept = actor.AcceptQuest(first, QuestAcceptorType.Npc, OfferNpcTemplate);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        actor.Character.Events.OnMonsterHunt(actor.Character, new OnMonsterHuntArgs { NpcId = Boar3475, Count = 1 });
        var advance = actor.AdvanceQuest(first);
        await Assert.That(advance.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests[first].Status).IsEqualTo(QuestStatus.Ready);

        // The reporter in range: the quest leg's turn-in serves the Ready step.
        SpawnAreaNpc(session, ReportNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        // The leg may first close toward the reporter (return Move) before
        // the turn-in lands — pump until the quest completes (bounded).
        for (var wake = 0; wake < 6 && !actor.Character.Quests.HasQuestCompleted(first); wake++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);
        }

        await Assert.That(actor.Character.Quests.HasQuestCompleted(first)).IsTrue();
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.LastResult!.CompletedQuestIds.Contains(first)).IsTrue();

        // The terminal fed the module through the owned path (no direct
        // NotifyQuestTerminal call above): the completed quest is never
        // re-picked — the next pick moves on to the second quest.
        var next = module.CanActivate(new BotActivityContext
        {
            Bot = bot,
            GameHour = 12f,
            ActiveActivity = QuestBootstrapActivityModule.ActivityName
        });
        await Assert.That(next.CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(SecondQuestId);
    }

    [Test]
    public async Task MoveTerminal_NeverFeedsModule()
    {
        // Owned Running Move holds land and consume through the same runtime
        // path, but movement never completes quests: the sink stays silent.
        var (executor, actor, runtime, clock) = CreatePursuitRig("wire-move");
        var fed = new List<(uint CharacterId, uint QuestId, bool Completed)>();
        executor.QuestTerminalSink = (characterId, questId, completed) =>
            fed.Add((characterId, questId, completed));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false }).IsTrue();

        for (var i = 0; i < 200 && !owned.IsTerminal; i++)
            actor.Tick(TimeSpan.FromMilliseconds(100));
        await Assert.That(owned.State).IsEqualTo(ActorLifecycleState.Completed);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(fed).IsEmpty();
    }

    // ------------------------------------------------------------ rig

    private static (GameplayActor Actor, HeadlessSession Session) CreateAreaActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, SpawnPos);
        var region = session.World.GetRegionByPos(SpawnPos)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        actor.Character.Level = 3;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        return (actor, session);
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

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock)
        CreatePursuitRig(string name)
    {
        SeedQuest251GatherAct();
        SeedBoarLootLink();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        actor.Character.Hp = actor.Character.MaxHp;
        var actorPos = new Vector3(0, 0, 0);
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var preyObjId = session.SpawnNpc(Boar3475);
        var prey = session.World.GetNpc(preyObjId)!;
        prey.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        prey.Hp = 100;
        prey.MaxHp = 100;
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, new Vector3(10, 0, 0));
        region.AddObject(prey);
        prey.Region = region;

        actor.Character.Quests.ActiveQuests[Quest251] = new Quest(actor.Character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
        actor.Character.CurrentTarget = prey;

        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            RoamSpeed = 2f,
            EnableWildlifeHunt = true,
            EnableWildlifeButcher = true,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => "quest.progress",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
            UnitResolver = (c, id) => c.ParentWorld?.GetUnit(id),
            DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
        };
        return (executor, actor, runtime, clock);
    }

    private static void SeedQuest251GatherAct()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(Quest251, out var template))
        {
            template = new QuestTemplate { Id = Quest251, Level = 2 };
            questTemplates[Quest251] = template;
        }
        if (template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == Meat4058))
            return;

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        const uint progressComponentId = 2510901;
        if (!componentTemplates.TryGetValue(progressComponentId, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = progressComponentId,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[progressComponentId] = component;
        }
        if (!template.Components.ContainsKey(progressComponentId))
            template.Components[progressComponentId] = component;
        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ItemId == Meat4058))
        {
            var act = new QuestActObjItemGather(component)
            {
                ActId = 10473,
                DetailId = 616,
                DetailType = nameof(QuestActObjItemGather),
                Count = 3,
                ItemId = Meat4058,
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
    }

    private static void SeedBoarLootLink()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || rows.All(r => r.LootPackId != Pack4530))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = Boar3475,
                NpcId = Boar3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Boar3475] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(Pack4530, out var pack) || pack.Loots.All(l => l.ItemId != Meat4058))
        {
            var loot = new Loot
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
                packs[Pack4530] = new LootPack
                {
                    Id = Pack4530,
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
}
