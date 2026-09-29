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
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Branch 1b quest-objective-source travel: an active non-Ready quest whose
/// gather/interact objective is not yet credited arms a QUEST_TRAVEL route to
/// the nearest spawner of the objective's doodad source — with empty
/// perception (no wake snapshot read beyond the spawner lookup). Credited
/// objectives withhold, unknown spawners resolve null with the byte-identical
/// reason, and a live route is never re-armed.
/// </summary>
[NotInParallel]
public class QuestObjectiveSourceTravelTests
{
    private const uint GatherQuest = 91_401;
    private const uint GatherComponent = 91_401_01;
    private const uint GatherActId = 91_401_11;
    private const uint GatherItem = 91_501;
    private const uint GatherDoodad = 91_601;
    private const uint GatherGroup = 91_701;
    private const uint GatherFunc = 91_801;
    private const int GatherNeed = 3;

    private static readonly MethodInfo ArmQuestTravel = typeof(BotRoamStepExecutor)
        .GetMethod("ArmQuestTravel", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock) CreateRig(
        string name = "objective-source-bot")
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();

        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            ActiveActivityProvider = _ => "quest.progress",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
        };

        return (executor, actor, runtime, clock);
    }

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedDoodadLootInteraction(99_101, 99_301, 91_002);
        TravelIntentStore.ClearAll();
        SeedGatherQuestShape();
    }

    private static bool Arm(BotRoamStepExecutor executor, PlayerBotRuntime runtime, GameplayActor actor)
    {
        _ = executor.GetOrCreateActor(actor.Character);
        var state = executor.GetBotState(runtime.CharacterId)!;
        return (bool)ArmQuestTravel.Invoke(executor, [runtime, actor, state])!;
    }

    [Test]
    public async Task ActiveGatherQuest_EmptyPerception_ArmsObjectiveSourceRoute()
    {
        var (executor, actor, runtime, _) = CreateRig("objective-arm-1");
        StageActiveQuest(actor, GatherQuest, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0));

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();

        var state = executor.GetBotState(runtime.CharacterId)!;
        var expectedReason =
            $"walking to quest objective source (doodad {GatherDoodad} 30.0m) at (30,0) from (0,0)";
        await Assert.That(state.QuestTravelReason).IsEqualTo(expectedReason);
        await Assert.That(state.QuestTravelTarget).IsNotNull();
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(30f);
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId) is { IsFinished: false }).IsTrue();
    }

    [Test]
    public async Task CreditedObjective_DoesNotArm_ByteIdenticalReason()
    {
        var (executor, actor, runtime, _) = CreateRig("objective-credited-1");
        StageActiveQuest(actor, GatherQuest, objective: GatherNeed);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0));

        await Assert.That(Arm(executor, runtime, actor)).IsFalse();

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNull();
        await Assert.That(state.QuestTravelReason).IsEqualTo(
            "no walkable quest target (nothing ready-unspawned, nothing in-band to discover)");
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull();
    }

    [Test]
    public async Task UnknownSpawner_ResolvesNull_WithByteIdenticalReason()
    {
        var (executor, actor, runtime, _) = CreateRig("objective-unknown-1");
        StageActiveQuest(actor, GatherQuest, objective: 0);

        await Assert.That(Arm(executor, runtime, actor)).IsFalse();

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNull();
        await Assert.That(state.QuestTravelReason).IsEqualTo(
            "no walkable quest target (nothing ready-unspawned, nothing in-band to discover)");
    }

    [Test]
    public async Task LiveRoute_IsNeverRearmed()
    {
        var (executor, actor, runtime, _) = CreateRig("objective-live-1");
        StageActiveQuest(actor, GatherQuest, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0));

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var first = executor.GetBotState(runtime.CharacterId)!;
        var armedRoute = executor.GetRoamRoute(runtime.CharacterId)!;

        // A live route keeps progress: the next arm refuses, reason untouched.
        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        var second = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsSameReferenceAs(armedRoute);
        await Assert.That(second.QuestTravelReason).IsEqualTo(first.QuestTravelReason);
    }

    [Test]
    public async Task SameSpawner_ArmedThrice_ThenWithholdsUnreachable()
    {
        var (executor, actor, runtime, _) = CreateRig("objective-bound-1");
        StageActiveQuest(actor, GatherQuest, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0));

        for (var i = 0; i < 3; i++)
        {
            await Assert.That(Arm(executor, runtime, actor)).IsTrue();
            var armed = executor.GetBotState(runtime.CharacterId)!;
            await Assert.That(armed.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
            // Drop the route without changing the active-quest set: the next
            // wake re-arms against the same spawner (the bound counts arms).
            armed.Path = null;
            armed.PendingLeg = null;
            armed.QuestTravelTarget = null;
            armed.PendingMoveOwner = null;
        }

        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelReason).IsEqualTo(
            $"objective-source-unreachable (doodad {GatherDoodad})");
        await Assert.That(state.QuestTravelTarget).IsNull();
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull();
    }

    // ------------------------------------------------------------ fixture

    private static void StageActiveQuest(GameplayActor actor, uint questId, int objective)
    {
        var template = QuestManager.Instance.GetTemplate(questId)
            ?? throw new InvalidOperationException($"quest template {questId} not seeded");
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
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
}
