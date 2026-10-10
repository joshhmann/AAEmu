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
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// THE ARM ORDERING between the gather-spawner arm and the prey arm in
/// <c>ResolveQuestObjectiveSource</c>, pinned through the production resolver.
///
/// Measured on a 36-minute 4-bot run: quest 251's gather item (item 4058 x3)
/// reaches BOTH a doodad well and an npc loot pack, so the row derives a
/// PreyTemplate AND a non-empty GatherDoodadTemplates set. The gather arm
/// returned the nearest well UNCONDITIONALLY, so the prey arm never ran, and all
/// four bots walked ~600 m off-cluster to non-prey spawners (tally 3497/3516/
/// 7648/7673/10581) while the prey spawner stood 666 m out and the 25 m
/// perception census never saw a boar. Boar 44325 stayed alive, legal=1 and
/// uncalled for 23 minutes.
///
/// THE RULE: a row that names a PREY gives the prey arm its turn. The gather
/// arm's answer is HELD, not discarded, and returned in the prey's place only
/// when the prey arm declines for one of its own reasons (no spawner, Deliver
/// shape, prey already perceived) — so a hybrid row can never end up with no
/// source at all, and a PURE-doodad row (PreyTemplate == 0) returns from the
/// gather arm byte for byte.
///
/// Fixture ids 97_4xx / 97_5xx are unclaimed repo-wide and absent from
/// canonical data, so a shared-id collision with the process-wide QuestManager
/// singleton cannot mask a regression.
/// </summary>
[NotInParallel]
public class QuestTravelHybridGatherSourceOrderingTests
{
    // ---- hybrid fixture (block 97_4xx): one gather act whose item reaches a
    //      doodad well AND an npc loot pack, so the row derives both sources.
    private const uint HybridQuest = 97_400;
    private const uint HybridComponent = 97_401;
    private const uint HybridActId = 97_402;
    private const uint HybridItem = 97_403;
    private const uint HybridDoodad = 97_404;
    private const uint HybridGroup = 97_405;
    private const uint HybridFunc = 97_406;
    private const uint HybridPreyTemplate = 97_407;
    private const uint HybridPreyPack = 97_408;
    private const int HybridNeed = 3;

    // ---- pure-doodad fixture (block 97_5xx): the SAME shape with the loot
    //      chain absent, so no PreyTemplate derives and the gather arm must
    //      answer exactly as it always has.
    private const uint DoodadQuest = 97_500;
    private const uint DoodadComponent = 97_501;
    private const uint DoodadActId = 97_502;
    private const uint DoodadItem = 97_503;
    private const uint DoodadWell = 97_504;
    private const uint DoodadGroup = 97_505;
    private const uint DoodadFunc = 97_506;
    private const int DoodadNeed = 3;

    /// <summary>The nearer well the gather arm used to claim the wake with.</summary>
    private static readonly Vector3 HybridDoodadSpawnerPos = new(30, 0, 0);

    /// <summary>The prey spawner, past the 25 m census by a wide margin.</summary>
    private static readonly Vector3 HybridPreySpawnerPos = new(400, 0, 0);

    /// <summary>The pure-doodad well spawner, at the same 30 m the old pin uses.</summary>
    private static readonly Vector3 DoodadSpawnerPos = new(30, 0, 0);

    private static readonly MethodInfo ResolveTravelTarget = typeof(BotRoamStepExecutor)
        .GetMethod("ResolveQuestTravelTarget", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ArmQuestTravel = typeof(BotRoamStepExecutor)
        .GetMethod("ArmQuestTravel", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();

        SeedDoodadWell(HybridQuest, HybridComponent, HybridActId, HybridItem, HybridDoodad, HybridGroup, HybridFunc);
        SeedNpcLootChain(HybridPreyTemplate, HybridPreyPack, HybridItem);
        SeedDoodadWell(DoodadQuest, DoodadComponent, DoodadActId, DoodadItem, DoodadWell, DoodadGroup, DoodadFunc);

        // Both shapes are ASSERTED, never assumed: the hybrid row must derive a
        // prey AND a doodad source (that is the shape the fix turns on), and the
        // pure-doodad row must derive NO prey (that is the shape the fix must
        // not touch). A fixture that stopped deriving either would silently
        // turn every test below into a proof of the wrong arm.
        QuestFixtureRow.InvalidateAll();
        var hybridRow = QuestFixtureRow.FromQuestData(HybridQuest);
        if (hybridRow.PreyTemplate != HybridPreyTemplate)
            throw new InvalidOperationException(
                $"hybrid fixture row resolved prey {hybridRow.PreyTemplate}, expected {HybridPreyTemplate}");
        if (hybridRow.GatherDoodadTemplates is not [HybridDoodad])
            throw new InvalidOperationException(
                "hybrid fixture row must derive exactly one granting doodad well");
        var doodadRow = QuestFixtureRow.FromQuestData(DoodadQuest);
        if (doodadRow.PreyTemplate != 0)
            throw new InvalidOperationException(
                $"pure-doodad fixture row resolved prey {doodadRow.PreyTemplate}, expected none");
        if (doodadRow.GatherDoodadTemplates is not [DoodadWell])
            throw new InvalidOperationException(
                "pure-doodad fixture row must derive exactly one granting doodad well");
    }

    // ================================================================
    // (1) THE FIX. A hybrid row, uncredited, prey NOT perceived: the PREY
    //     spawner wins even though the doodad well is 370 m nearer. Pre-fix
    //     this returned the 30 m well on every wake — the measured walk that
    //     carried all four bots 600 m off-cluster.
    // ================================================================
    [Test]
    public async Task HybridGatherRow_PreySpawnerWins_OverNearerDoodadWell()
    {
        var (executor, actor, runtime) = CreateRig("hybrid-prey-wins");
        StageActiveQuest(actor, HybridQuest, objective: 0);
        SeedDoodadSpawner(actor, HybridDoodad, HybridDoodadSpawnerPos);   // 30 m — used to win
        SeedNpcSpawner(actor, HybridPreyTemplate, HybridPreySpawnerPos);   // 400 m — the prey

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(HybridPreySpawnerPos.X);
        await Assert.That(resolved!.Value.X).IsNotEqualTo(HybridDoodadSpawnerPos.X);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(HybridPreySpawnerPos.X);
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
        // The arming reason names which arm answered, so it pins the ORDER,
        // not merely the position — the only place the two are distinguishable.
        await Assert.That(state.QuestTravelReason.Contains("prey")).IsTrue();
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
    }

    // ================================================================
    // (2) UNCHANGED — a PURE-doodad row still resolves its well spawner,
    //     byte for byte, even with a nearer alternative in the world. This is
    //     the behaviour the ordering rule must not disturb.
    // ================================================================
    [Test]
    public async Task PureDoodadRow_StillResolvesItsOwnWell_Unchanged()
    {
        var (executor, actor, runtime) = CreateRig("pure-doodad-unchanged");
        StageActiveQuest(actor, DoodadQuest, objective: 0);
        SeedDoodadSpawner(actor, DoodadWell, DoodadSpawnerPos);

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(DoodadSpawnerPos.X);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        var expectedReason =
            $"walking to quest objective source (doodad {DoodadWell} 30.0m) at (30,0) from (0,0)";
        await Assert.That(state.QuestTravelReason).IsEqualTo(expectedReason);
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(DoodadSpawnerPos.X);
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
    }

    // ================================================================
    // (3) FAIL-CLOSED — the held answer is not discarded. A hybrid row whose
    //     prey has NO spawner must still travel to its well, exactly as before
    //     the ordering change. Reverting the change turns this RED.
    // ================================================================
    [Test]
    public async Task HybridRow_PreyHasNoSpawner_FallsBackToTheHeldDoodadWell()
    {
        var (executor, actor, runtime) = CreateRig("hybrid-prey-no-spawner");
        StageActiveQuest(actor, HybridQuest, objective: 0);
        SeedDoodadSpawner(actor, HybridDoodad, HybridDoodadSpawnerPos);
        // NO npc spawner for HybridPreyTemplate: the prey arm declines on its
        // own guard, and the well it held back is the only source left.

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(HybridDoodadSpawnerPos.X);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        var expectedReason =
            $"walking to quest objective source (doodad {HybridDoodad} 30.0m) at (30,0) from (0,0)";
        await Assert.That(state.QuestTravelReason).IsEqualTo(expectedReason);
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(HybridDoodadSpawnerPos.X);
    }

    // ================================================================
    // (4) UNCHANGED GUARD — a PERCEIVED prey means the funnel owns that wake,
    //     so the prey arm declines and the held well is returned in its place.
    //     The ordering rule must not turn a perceived prey into a walk.
    // ================================================================
    [Test]
    public async Task HybridRow_PreyPerceived_FallsBackToTheHeldDoodadWell()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hybrid-prey-perceived");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var executor = CreateExecutorFor(actor);
        StageActiveQuest(actor, HybridQuest, objective: 0);
        SeedDoodadSpawner(actor, HybridDoodad, HybridDoodadSpawnerPos);
        SeedNpcSpawner(actor, HybridPreyTemplate, HybridPreySpawnerPos);
        // A LIVE prey inside the 25 m census. Region registration is what the
        // census actually reads, so without it the withhold would be vacuous.
        SpawnPerceivedPrey(session, HybridPreyTemplate, new Vector3(3, 0, 0));

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(HybridDoodadSpawnerPos.X);
    }

    // ================================================================
    // (5) UNCHANGED GUARD — a CREDITED objective contributes no source at
    //     all, whichever shape the row is. The held answer is only ever read
    //     on the uncredited path, so this stays withhold-not-fallback.
    // ================================================================
    [Test]
    public async Task HybridRow_Credited_NeverArms()
    {
        var (executor, actor, runtime) = CreateRig("hybrid-credited");
        StageActiveQuest(actor, HybridQuest, objective: HybridNeed);
        SeedDoodadSpawner(actor, HybridDoodad, HybridDoodadSpawnerPos);
        SeedNpcSpawner(actor, HybridPreyTemplate, HybridPreySpawnerPos);

        var resolved = Resolve(actor);

        await Assert.That(resolved).IsNull();
        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.QuestTravelTarget).IsNull();
    }

    // ================================================================
    // Harness
    // ================================================================
    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime) CreateRig(string name)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        return (CreateExecutorFor(actor), actor, runtime);
    }

    /// <summary>An executor bound to an actor the caller already built (region-registered).</summary>
    private static BotRoamStepExecutor CreateExecutorFor(GameplayActor actor) => new()
    {
        ActorFactory = _ => actor,
        TimeProvider = new FakeTimeProvider(),
        BroadcastInterval = TimeSpan.FromMilliseconds(200),
        ActiveCadence = TimeSpan.FromMilliseconds(100),
        RoamSpeed = 2f,
        ActiveActivityProvider = _ => "quest.progress",
        NearbyNpcProvider = (_, _) => [],
        NearbyDoodadProvider = (_, _) => [],
    };

    /// <summary>Resolves through the production travel resolver itself.</summary>
    private static Vector3? Resolve(GameplayActor actor)
    {
        var band = BotRoamStepExecutor.ResolveOfferBand(
            levelBand: false, headroom: 5, actor.Character.Level);
        return (Vector3?)ResolveTravelTarget.Invoke(null, [actor, band]);
    }

    private static bool Arm(BotRoamStepExecutor executor, PlayerBotRuntime runtime, GameplayActor actor)
    {
        _ = executor.GetOrCreateActor(actor.Character);
        var state = executor.GetBotState(runtime.CharacterId)!;
        var band = typeof(BotRoamStepExecutor)
            .GetMethod("OfferBandFor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(executor, [actor])!;
        return (bool)ArmQuestTravel.Invoke(executor, [runtime, actor, state, band, "quest.progress"])!;
    }

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

    private static void SeedNpcSpawner(GameplayActor actor, uint npcTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(97_900 + spawners.Count);
        spawners[key] =
        [
            new NpcSpawner
            {
                Id = key,
                UnitId = npcTemplateId,
                Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
            }
        ];
    }

    private static void SeedDoodadSpawner(GameplayActor actor, uint doodadTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, DoodadSpawner>)typeof(SpawnManager)
            .GetProperty("DoodadSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(97_950 + spawners.Count);
        spawners[key] = new DoodadSpawner
        {
            Id = key,
            UnitId = doodadTemplateId,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
        };
    }

    // ================================================================
    // Fixtures
    // ================================================================

    /// <summary>
    /// A gather act plus a doodad well whose loot funcs grant that act's item —
    /// the half of the hybrid shape the gather arm reads.
    /// </summary>
    private static void SeedDoodadWell(uint questId, uint componentId, uint actId, uint itemId,
        uint doodadTemplateId, uint groupId, uint funcId)
    {
        SeedGatherAct(questId, componentId, actId, itemId);

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var _ = LootGameData.Instance;

        // The rig owns the DoodadManager singleton and seeds the whole func
        // surface (func row, group membership, DoodadFuncLootItem template).
        // All that is left for a granting WELL is the doodad TEMPLATE that binds
        // the func group to a template id — which is exactly what
        // QuestFixtureRow.ResolveDoodadSources inverts to reach the item.
        GameplayActorTestRig.SeedDoodadLootInteraction(groupId, funcId, itemId);
        var manager = DoodadManager.Instance;

        var templates = (Dictionary<uint, DoodadTemplate>)typeof(DoodadManager)
            .GetField("_templates", flags)!.GetValue(manager)!;
        if (!templates.TryGetValue(doodadTemplateId, out var template))
        {
            template = new DoodadTemplate { Id = doodadTemplateId, FuncGroups = [] };
            templates[doodadTemplateId] = template;
        }
        if (template.FuncGroups.All(g => g.Id != groupId))
            template.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = groupId,
                Almighty = doodadTemplateId,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });
    }

    /// <summary>
    /// The npc loot chain for the same item: the row's PreyTemplate derives
    /// from it. Absent for the pure-doodad fixture, which is exactly what
    /// keeps that row's PreyTemplate at 0.
    /// </summary>
    private static void SeedNpcLootChain(uint preyTemplate, uint preyPack, uint itemId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var _ = LootGameData.Instance;

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(preyTemplate, out var rows) || rows.All(r => r.LootPackId != preyPack))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = preyTemplate,
                NpcId = preyTemplate,
                LootPackId = preyPack,
                DefaultPack = true,
            });
            map[preyTemplate] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(preyPack, out var pack) || pack.Loots.All(l => l.ItemId != itemId))
        {
            var loot = new Loot
            {
                Id = preyPack,
                Group = 0,
                ItemId = itemId,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = preyPack,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[preyPack] = new LootPack
                {
                    Id = preyPack,
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
                {
                    group.Add(loot);
                }
                else
                {
                    pack.LootsByGroupNo[0] = [loot];
                }
            }
        }
    }

    /// <summary>
    /// A Progress gather act naming the item and nothing else — no highlight
    /// doodad, no alias — so the ONLY sources are the doodad well and the npc
    /// loot pack seeded alongside it.
    /// </summary>
    private static void SeedGatherAct(uint questId, uint componentId, uint actId, uint itemId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var template = SeedQuestTemplate(questId);

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(componentId, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = componentId,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[componentId] = component;
        }
        if (!template.Components.ContainsKey(componentId))
            template.Components[componentId] = component;

        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ActId == actId))
        {
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            {
                ActId = actId,
                DetailId = componentId,
                DetailType = nameof(QuestActObjItemGather),
                Count = HybridNeed,
                ItemId = itemId,
                ThisComponentObjectiveIndex = 0,
            });
        }
    }

    /// <summary>The quest template, created on first use (idempotent).</summary>
    private static QuestTemplate SeedQuestTemplate(uint questId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(QuestManager.Instance)!;
        if (!questTemplates.TryGetValue(questId, out var template))
        {
            template = new QuestTemplate { Id = questId, Level = 10 };
            questTemplates[questId] = template;
        }
        return template;
    }

    private static void JoinToRegion(GameplayActor actor, HeadlessSession session, Vector3 position)
    {
        var region = session.World.GetRegionByPos(position)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
    }

    private static void SpawnPerceivedPrey(HeadlessSession session, uint npcTemplateId, Vector3 npcPos)
    {
        var region = session.World.GetRegionByPos(npcPos)
            ?? throw new InvalidOperationException("rig world has no region at the npc position");
        var objId = session.SpawnNpc(npcTemplateId);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate { Id = npcTemplateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, objId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
    }
}