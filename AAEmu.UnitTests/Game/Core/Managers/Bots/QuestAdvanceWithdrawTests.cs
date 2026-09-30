using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Link-1 advance withdraw: a steady Progress quest whose uncredited
/// gather/interact objective's doodad source is known yet lies outside the
/// 25 m perception census withdraws the vacuous Advance (the step machine
/// would no-op on the steady step), so the quest-travel arm can walk the
/// actor to the spawner. Real Start/Supply-drain, credit-then-advance, and
/// KillX pursuit wakes keep landing.
/// </summary>
[NotInParallel]
public class QuestAdvanceWithdrawTests
{
    private const uint GatherQuest = 91_442;
    private const uint GatherComponent = 91_442_01;
    private const uint GatherActId = 91_442_11;
    private const uint GatherItem = 91_501;
    private const uint GatherDoodad = 91_601;
    private const uint GatherGroup = 91_701;
    private const uint GatherFunc = 91_801;
    private const int GatherNeed = 3;

    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherAct251 = 10473;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedDoodadLootInteraction(99_101, 99_301, 91_002);
        TravelIntentStore.ClearAll();
        QuestBehavior.ClearGatherMemory();
        QuestBehavior.ClearInteractMemory();
        QuestBehavior.ClearGiveUpMemory();
        SeedGatherQuestShape();
        SeedQuest251GatherAct();
        SeedBoarLootLink();
    }

    [Test]
    public async Task SteadyProgressUncreditedSourceOutsidePerception_AdvanceWithdrawsNamed_NoDispatch()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("advance-withdraw-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, QuestComponentKind.Progress, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0));

        var fixture = QuestFixtureRow.FromQuestData(GatherQuest);
        var legContext = AdvanceContext(actor, GatherQuest, fixture);
        await Assert.That(QuestBehavior.AdvanceEnter(legContext, new QuestLegWake()))
            .IsEqualTo("steady-progress-uncredited-source");

        var plan = QuestDirector.Plan(GatherQuest, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production gather plan failed: {plan.FailReason}");
        var result = QuestBehavior.Run(
            actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "advance-withdraw-1" },
            BotObservedContext.Capture(actor),
            [plan],
            (_, _) => []);

        var advance = result.LegEvidence.Single(e => e.Leg == QuestLegId.Advance);
        await Assert.That(advance.Entered).IsFalse();
        await Assert.That(advance.Detail).IsEqualTo("steady-progress-uncredited-source");
        await Assert.That(result.LegEvidence.Single(e => e.Leg == QuestLegId.Gather).Detail).IsEqualTo("gather-no-source");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AdvanceQuest)).IsFalse();
        await Assert.That(result.WorkSelected).IsFalse();
    }

    [Test]
    public async Task StartDrain_WithKnownFarSource_AdvanceStillLands()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("advance-drain-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, GatherQuest, QuestStatus.Invalid, QuestComponentKind.Start, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0));

        var fixture = QuestFixtureRow.FromQuestData(GatherQuest);
        var legContext = AdvanceContext(actor, GatherQuest, fixture);
        await Assert.That(QuestBehavior.AdvanceEnter(legContext, new QuestLegWake())).IsNull();

        var plan = QuestDirector.Plan(GatherQuest, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production gather plan failed: {plan.FailReason}");
        var result = QuestBehavior.Run(
            actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "advance-drain-1" },
            BotObservedContext.Capture(actor),
            [plan],
            (_, _) => []);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AdvanceQuest);
        await Assert.That(result.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task CreditedObjective_AdvanceStillLands()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("advance-credited-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, QuestComponentKind.Progress, objective: GatherNeed);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0));

        var fixture = QuestFixtureRow.FromQuestData(GatherQuest);
        var legContext = AdvanceContext(actor, GatherQuest, fixture);
        await Assert.That(QuestBehavior.AdvanceEnter(legContext, new QuestLegWake())).IsNull();

        var plan = QuestDirector.Plan(GatherQuest, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production gather plan failed: {plan.FailReason}");
        var result = QuestBehavior.Run(
            actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "advance-credited-1" },
            BotObservedContext.Capture(actor),
            [plan],
            (_, _) => []);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AdvanceQuest);
        await Assert.That(result.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task SpawnerInsidePerception_AdvanceStillLands()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("advance-near-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, QuestComponentKind.Progress, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(10, 0, 0));

        var fixture = QuestFixtureRow.FromQuestData(GatherQuest);
        var legContext = AdvanceContext(actor, GatherQuest, fixture);
        await Assert.That(QuestBehavior.AdvanceEnter(legContext, new QuestLegWake())).IsNull();
    }

    [Test]
    public async Task KillXPursuitWake_AdvanceEnters_PursuitStillWins()
    {
        var (actor, _) = CreatePursuingActor("advance-killx-1", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(fixture.GatherDoodadTemplate).IsEqualTo(0u);
        var legContext = AdvanceContext(actor, Quest251, fixture);
        await Assert.That(QuestBehavior.AdvanceEnter(legContext, new QuestLegWake())).IsNull();

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "advance-killx-1" });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.Request!.MoveOwner).IsEqualTo("PURSUIT_MOVE_TO_UNIT");
        var advance = result.LegEvidence.Single(e => e.Leg == QuestLegId.Advance);
        await Assert.That(advance.Entered).IsTrue();
    }

    // ------------------------------------------------------------ helpers

    private static QuestLegContext AdvanceContext(GameplayActor actor, uint questId, QuestFixtureRow fixture)
    {
        var context = BotObservedContext.Capture(actor);
        return new QuestLegContext(actor, new QuestDecisionScenario.QuestOptions(), questId, fixture, null)
        {
            Observation = context
        };
    }

    private static void StageActiveQuest(
        GameplayActor actor, uint questId, QuestStatus status, QuestComponentKind step, int objective)
    {
        var template = QuestManager.Instance.GetTemplate(questId)
            ?? throw new InvalidOperationException($"quest template {questId} not seeded");
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Status = status;
        quest.Step = step;
        quest.Objectives = [objective, 0, 0, 0, 0];
        actor.Character.Quests.ActiveQuests[questId] = quest;
        QuestFixtureRow.InvalidateAll();
    }

    private static void SeedGatherQuestShape()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(GatherQuest, out var template))
        {
            template = new QuestTemplate { Id = GatherQuest, Level = 10 };
            questTemplates[GatherQuest] = template;
        }

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(GatherComponent, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = GatherComponent,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[GatherComponent] = component;
        }
        if (!template.Components.ContainsKey(GatherComponent))
            template.Components[GatherComponent] = component;

        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ActId == GatherActId))
        {
            var act = new QuestActObjItemGather(component)
            {
                ActId = GatherActId,
                DetailId = GatherComponent,
                DetailType = nameof(QuestActObjItemGather),
                Count = GatherNeed,
                ItemId = GatherItem,
                ThisComponentObjectiveIndex = 0,
            };
            component.ActTemplates.Add(act);
        }

        SeedDoodadLootChain();
        QuestFixtureRow.InvalidateAll();
        var row = QuestFixtureRow.FromQuestData(GatherQuest);
        if (row.GatherDoodadTemplate != GatherDoodad)
            throw new InvalidOperationException($"fixture row resolved doodad {row.GatherDoodadTemplate}, expected {GatherDoodad}");
    }

    private static void SeedDoodadLootChain()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var _ = LootGameData.Instance;
        var manager = Singleton<DoodadManager>.PeekInstance;
        if (manager == null)
            return;

        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)typeof(DoodadManager)
            .GetField("_funcsByGroups", flags)!.GetValue(manager)!;
        var funcsById = (Dictionary<uint, DoodadFunc>)typeof(DoodadManager)
            .GetField("_funcsById", flags)!.GetValue(manager)!;
        var funcTemplates = (Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)typeof(DoodadManager)
            .GetField("_funcTemplates", flags)!.GetValue(manager)!;

        if (!funcsById.ContainsKey(GatherFunc))
            funcsById[GatherFunc] = new DoodadFunc
            {
                GroupId = GatherGroup,
                FuncId = GatherFunc,
                FuncKey = GatherFunc,
                FuncType = "DoodadFuncLootItem",
                NextPhase = -1,
                SkillId = 0
            };
        if (!funcsByGroups.TryGetValue(GatherGroup, out var group))
        {
            group = [];
            funcsByGroups[GatherGroup] = group;
        }
        if (group.All(f => f.FuncId != GatherFunc))
            group.Add(funcsById[GatherFunc]);

        if (!funcTemplates.TryGetValue("DoodadFuncLootItem", out var lootTemplates))
        {
            lootTemplates = [];
            funcTemplates["DoodadFuncLootItem"] = lootTemplates;
        }
        if (!lootTemplates.ContainsKey(GatherFunc))
        {
            lootTemplates[GatherFunc] = new DoodadFuncLootItem
            {
                ItemId = GatherItem,
                CountMin = 1,
                CountMax = 2,
                Percent = 10_000,
                RemainTime = 0
            };
        }

        var templates = (Dictionary<uint, DoodadTemplate>)typeof(DoodadManager)
            .GetField("_templates", flags)!.GetValue(manager)!;
        if (!templates.TryGetValue(GatherDoodad, out var template))
        {
            template = new DoodadTemplate { Id = GatherDoodad, FuncGroups = [] };
            templates[GatherDoodad] = template;
        }
        if (template.FuncGroups.All(g => g.Id != GatherGroup))
            template.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = GatherGroup,
                Almighty = GatherDoodad,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });
    }

    private static void SeedDoodadSpawner(GameplayActor actor, uint doodadTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, DoodadSpawner>)typeof(SpawnManager)
            .GetProperty("DoodadSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(9000 + spawners.Count);
        spawners[key] = new DoodadSpawner
        {
            Id = key,
            UnitId = doodadTemplateId,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
        };
    }

    private static (GameplayActor Actor, HeadlessSession Session) CreatePursuingActor(
        string name, Vector3 actorPos, Vector3 preyPos)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        actor.Character.Hp = actor.Character.MaxHp;

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
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, preyPos);
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
        return (actor, session);
    }

    private static void SeedQuest251GatherAct()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
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
                ActId = GatherAct251,
                DetailId = 616,
                DetailType = nameof(QuestActObjItemGather),
                Count = GatherNeed,
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
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
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
