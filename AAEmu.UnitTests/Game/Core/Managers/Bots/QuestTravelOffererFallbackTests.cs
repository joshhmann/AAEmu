using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The OFFERER arm of <c>ResolveQuestTravelTarget</c> — arm 2, the only arm
/// that walks a bot toward a quest GIVER.
///
/// The defect: arm 2 was gated on <c>ActiveQuests.Count == 0</c>, so ONE active
/// quest this world cannot serve (measured on a 30-minute 4-bot run: quest 251's
/// gather objective rejects all 12 candidate NPCs, rejects=[template=12]) made
/// the arm structurally unreachable forever. The bot circled its 27 m presence
/// ring while the nearest real spawned offerer sat 1331 m away, because no
/// other arm in the resolver walks toward a giver.
///
/// The fix: the gate is "no active quest HAS a walkable target". Arm 1 (a Ready
/// quest's unspawned reporter) still sits strictly ABOVE arm 2 and returns the
/// moment it resolves one, so reaching arm 2 proves arm 1 declined. Arm 1b (an
/// uncredited objective's doodad source) does NOT return: it defers, and the
/// tail walks to the NEARER of arm 1b's source and arm 2's nearest in-band
/// offerer. These tests pin that order, that comparison, and the fail-closed
/// tail.
///
/// Fixture ids 94_0xx are unclaimed repo-wide and absent from canonical
/// quest_contexts / quest_components / npcs / quest_act_con_accept_npcs
/// (verified against compact.sqlite3), so a shared-id collision with the
/// process-wide QuestManager singleton cannot mask a regression.
/// </summary>
[NotInParallel]
public class QuestTravelOffererFallbackTests
{
    // ---- fixture ids (block 94_0xx: free repo-wide + absent from canonical DB)
    private const uint OfferQuest = 94_001;        // level-3 quest offered by OffererNpc
    private const uint OfferComponent = 94_002;
    private const uint OffererNpc = 94_003;        // a REAL spawned, in-band offerer

    private const uint StuckQuest = 94_010;        // the active quest that is stuck
    private const uint StuckComponent = 94_011;
    private const uint StuckAcceptNpc = 94_012;     // Start acceptor, spawned nowhere

    private const uint ReporterQuest = 94_020;     // the Ready quest of the priority test
    private const uint ReporterStartComponent = 94_021;
    private const uint ReporterStartNpc = 94_022;
    private const uint ReporterReadyComponent = 94_023;
    private const uint ReporterNpc = 94_024;       // unspawned reporter WITH a spawner

    private const uint GatherQuest = 94_030;       // arm-1b priority quest
    private const uint GatherComponent = 94_031;
    private const uint GatherActId = 94_032;
    private const uint GatherItem = 94_033;
    private const uint GatherDoodad = 94_034;
    private const uint GatherGroup = 94_035;
    private const uint GatherFunc = 94_036;
    private const int GatherNeed = 3;

    private const byte InBandLevel = 3;            // inside the frozen 1..9 band
    private const uint FarNpc = 94_040;            // a spawned template carrying no offer

    private static readonly MethodInfo ArmQuestTravel = typeof(BotRoamStepExecutor)
        .GetMethod("ArmQuestTravel", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ResolveQuestTravelTarget = typeof(BotRoamStepExecutor)
        .GetMethod("ResolveQuestTravelTarget", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedDoodadLootInteraction(94_501, 94_502, 94_503);
        TravelIntentStore.ClearAll();

        GameplayActorTestRig.SeedQuestOffer(OfferQuest, OfferComponent, OffererNpc, level: InBandLevel);
        GameplayActorTestRig.SeedQuestOffer(StuckQuest, StuckComponent, StuckAcceptNpc, level: InBandLevel);
        GameplayActorTestRig.SeedQuestDelivery(ReporterQuest, ReporterStartComponent, ReporterReadyComponent,
            ReporterStartNpc, ReporterNpc, level: InBandLevel);
        SeedGatherQuestShape();
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime) CreateRig(
        string name)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        BotRoamStepExecutor executor = new()
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
        return (executor, actor, runtime);
    }

    /// <summary>Arms through the production entry point (guard, counters and all).</summary>
    private static bool Arm(BotRoamStepExecutor executor, PlayerBotRuntime runtime, GameplayActor actor)
    {
        _ = executor.GetOrCreateActor(actor.Character);
        var state = executor.GetBotState(runtime.CharacterId)!;
        return (bool)ArmQuestTravel.Invoke(executor, [runtime, actor, state])!;
    }

    /// <summary>
    /// Resolves through the production resolver itself — the gate under test.
    /// </summary>
    private static Vector3? Resolve(GameplayActor actor)
    {
        var band = BotRoamStepExecutor.ResolveOfferBand(
            levelBand: false, headroom: 5, actor.Character.Level);
        return (Vector3?)ResolveQuestTravelTarget.Invoke(null, [actor, band]);
    }

    // ================================================================
    // (1) No active quests — arm 2 behaves EXACTLY as before the fix.
    // ================================================================
    [Test]
    public async Task NoActiveQuest_WalksToNearestInBandOfferer_UnchangedBehaviour()
    {
        var (executor, actor, runtime) = CreateRig("offerer-empty-1");
        await Assert.That(actor.Character.Quests.ActiveQuests).IsEmpty();
        SeedNpcSpawner(actor, OffererNpc, new Vector3(60, 0, 0));
        SeedNpcSpawner(actor, FarNpc, new Vector3(5, 0, 0)); // nearer, but offers nothing walkable

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(60f);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNotNull();
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(60f);
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
    }

    // ================================================================
    // (2) An active quest WITH a reachable reporter — arm 1 wins and arm 2
    //     is never reached. A nearer walkable offerer sits 5 m away, so if the
    //     arms were reordered this fixture would fail: the priority claim is
    //     load-bearing, not incidental.
    // ================================================================
    [Test]
    public async Task ActiveQuest_WithReachableReporter_ArmOneWins_ArmTwoNotReached()
    {
        var (executor, actor, runtime) = CreateRig("offerer-priority-1");
        StageActiveQuest(actor, ReporterQuest, QuestStatus.Ready);
        SeedNpcSpawner(actor, ReporterNpc, new Vector3(40, 0, 0)); // the reporter's spawner
        SeedNpcSpawner(actor, OffererNpc, new Vector3(5, 0, 0));   // a nearer walkable offerer

        // Arm 1's precondition really holds: the reporter is not spawned.
        await Assert.That(actor.Character.ParentWorld!.GetNpcByTemplateId(ReporterNpc)).IsNull();

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(40f); // the reporter, NOT the 5 m offerer

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(40f);
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
    }

    /// <summary>
    /// Arm 1b is NOT an absolute priority. An active quest whose uncredited
    /// gather objective DOES resolve a doodad source loses that source to a
    /// NEARER walkable offerer — this is the fix: arm 1b used to return the
    /// instant it resolved, so the giver arm never ran. Arm 1 is skipped (the
    /// quest is Progress, not Ready), which isolates the arm-1b-vs-arm-2
    /// comparison. The 500 m / 30 m separation is deliberately wide so the
    /// assertion cannot pass by a rounding accident.
    /// </summary>
    [Test]
    public async Task ActiveQuest_WithResolvableObjectiveSource_YieldsToNearerOfferer()
    {
        var (executor, actor, runtime) = CreateRig("offerer-obj-1");
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(500, 0, 0)); // the objective source
        SeedNpcSpawner(actor, OffererNpc, new Vector3(30, 0, 0));       // a nearer walkable offerer

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(30f); // the 30 m offerer, NOT the 500 m source

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(30f);
        // Armed as a plain quest target, not through the objective-source branch:
        // the target is no longer the source, so the 3-arm cap does not apply.
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsFalse();
    }

    // ================================================================
    // (2b) THE REGRESSION PIN, other direction: when arm 1b's source is the
    //      NEARER of the two, it still wins. Nothing about the fix may change
    //      this — same resolvers, same bodies, only the comparison is new.
    // ================================================================
    [Test]
    public async Task ActiveQuest_WithNearerObjectiveSource_KeepsObjectiveSourceOverFartherOfferer()
    {
        var (executor, actor, runtime) = CreateRig("offerer-obj-near-1");
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(30, 0, 0)); // the objective source
        SeedNpcSpawner(actor, OffererNpc, new Vector3(500, 0, 0));     // a farther walkable offerer

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(30f); // the source, NOT the 500 m offerer

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(30f);
        // Still armed THROUGH the objective-source branch, cap and all.
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
    }

    /// <summary>
    /// Arm 1 keeps its absolute priority over BOTH later arms regardless of
    /// distance: the Ready quest's reporter wins even with a far nearer
    /// objective source (on a second active quest) and a far nearer offerer.
    /// </summary>
    [Test]
    public async Task ReadyReporter_BeatsNearerObjectiveSourceAndNearerOfferer()
    {
        var (executor, actor, runtime) = CreateRig("offerer-arm1-abs-1");
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, objective: 0);  // arm 1b resolves 900 m out
        StageActiveQuest(actor, ReporterQuest, QuestStatus.Ready);                 // arm 1 resolves 600 m out
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(900, 0, 0));
        SeedNpcSpawner(actor, ReporterNpc, new Vector3(600, 0, 0));
        SeedNpcSpawner(actor, OffererNpc, new Vector3(30, 0, 0));                  // nearest of all three

        // Arm 1's own precondition really holds: the reporter is not spawned.
        await Assert.That(actor.Character.ParentWorld!.GetNpcByTemplateId(ReporterNpc)).IsNull();

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(600f); // the reporter — not 900 m, not 30 m

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.QuestTravelTarget!.Value.X).IsEqualTo(600f);
    }

    /// <summary>
    /// Only the objective source resolves (no walkable offerer anywhere):
    /// today's behaviour exactly, through the objective-source branch.
    /// </summary>
    [Test]
    public async Task ObjectiveSourceOnly_NoOffererAnywhere_KeepsObjectiveSource()
    {
        var (executor, actor, runtime) = CreateRig("offerer-obj-only-1");
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(500, 0, 0));
        SeedNpcSpawner(actor, FarNpc, new Vector3(5, 0, 0)); // nearer, but offers nothing walkable

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(500f);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(500f);
        await Assert.That(state.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
    }

    /// <summary>
    /// The other single-candidate direction: only the offerer resolves (no
    /// spawner for the objective source) — the arm-1b scan runs and declines,
    /// and arm 2 answers exactly as before the fix.
    /// </summary>
    [Test]
    public async Task OffererOnly_NoObjectiveSourceSpawner_KeepsOfferer()
    {
        var (executor, actor, runtime) = CreateRig("offerer-only-1");
        StageActiveQuest(actor, GatherQuest, QuestStatus.Progress, objective: 0);
        // GatherDoodad deliberately has NO spawner, so arm 1b cannot resolve.
        SeedNpcSpawner(actor, OffererNpc, new Vector3(60, 0, 0));

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(60f);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.QuestTravelTarget!.Value.X).IsEqualTo(60f);
    }

    // ================================================================
    // (3) THE FIX: an active quest whose reporter is unspawned AND has no
    //     spawner, and whose objective source does NOT resolve. Arm 2 now
    //     runs and returns the nearest in-band offerer.
    // ================================================================
    [Test]
    public async Task StuckActiveQuest_NoReporterNoObjectiveSource_FallsThroughToOfferer()
    {
        var (executor, actor, runtime) = CreateRig("offerer-stuck-1");
        StageActiveQuest(actor, StuckQuest, QuestStatus.Progress);
        // NON-VACUITY PRECONDITION, asserted before the resolver runs: the bot
        // really does hold an active quest. The old gate was
        // `ActiveQuests.Count == 0`, so this count is exactly what made it
        // unreachable — a regression to the old condition turns this whole
        // test red on the very next line (Resolve would return null), which is
        // what makes it a real proof rather than a passing tautology.
        await Assert.That(actor.Character.Quests.ActiveQuests.Count).IsEqualTo(1);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(StuckQuest)).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests[StuckQuest].Status)
            .IsEqualTo(QuestStatus.Progress);
        SeedNpcSpawner(actor, OffererNpc, new Vector3(60, 0, 0));
        SeedNpcSpawner(actor, FarNpc, new Vector3(5, 0, 0)); // nearer, offers nothing walkable

        // Both earlier arms genuinely declined: the quest is not Ready (arm 1
        // skipped it), it names no gather/interact doodad source (arm 1b
        // declined), and the offerer is a legal in-band target.
        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(60f);
        await Assert.That(resolved!.Value.Y).IsEqualTo(0f);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(StuckQuest)).IsTrue();

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNotNull();
        await Assert.That(state.QuestTravelTarget!.Value.X).IsEqualTo(60f);
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId) is { IsFinished: false }).IsTrue();
    }

    /// <summary>
    /// A Ready quest whose reporter is unspawned but has NO spawner also
    /// declines arm 1 — so the fallback reaches it for the ready shape too,
    /// not only for a Progress quest.
    /// </summary>
    [Test]
    public async Task ReadyQuest_ReporterUnspawnedWithNoSpawner_FallsThroughToOfferer()
    {
        var (executor, actor, runtime) = CreateRig("offerer-ready-stuck-1");
        StageActiveQuest(actor, ReporterQuest, QuestStatus.Ready);
        // ReporterNpc deliberately has NO spawner; the only walkable offerer is
        // 60 m out. Pre-fix this returned null forever.
        SeedNpcSpawner(actor, OffererNpc, new Vector3(60, 0, 0));

        var resolved = Resolve(actor);
        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value.X).IsEqualTo(60f);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.QuestTravelTarget!.Value.X).IsEqualTo(60f);
    }

    // ================================================================
    // (4) Fail-closed: an active quest and nothing walkable anywhere.
    // ================================================================
    [Test]
    public async Task StuckActiveQuest_NothingWalkableAnywhere_FailsClosedWithSameReason()
    {
        var (executor, actor, runtime) = CreateRig("offerer-stuck-null-1");
        StageActiveQuest(actor, StuckQuest, QuestStatus.Progress);
        // A spawner exists but carries no walkable offer — the offer scan finds
        // nothing and must fail closed rather than walk to it.
        SeedNpcSpawner(actor, FarNpc, new Vector3(3, 0, 0));

        await Assert.That(Resolve(actor)).IsNull();
        await Assert.That(Arm(executor, runtime, actor)).IsFalse();

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNull();
        await Assert.That(state.QuestTravelReason).IsEqualTo(
            "no walkable quest target (nothing ready-unspawned, nothing in-band to discover)");
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull();
    }

    /// <summary>
    /// The other fail-closed branch: a world with no spawner tables at all
    /// must not crash the wake even now that arm 2 is reachable with an active
    /// quest in the set.
    /// </summary>
    [Test]
    public async Task StuckActiveQuest_NoSpawnerTables_FailsClosedWithoutThrowing()
    {
        var (executor, actor, runtime) = CreateRig("offerer-stuck-nospawn-1");
        StageActiveQuest(actor, StuckQuest, QuestStatus.Progress);
        // The rig never installs a SpawnManager for this character, so
        // world.SpawnManager stays null and GetAllSpawners is never called.
        await Assert.That(actor.Character.ParentWorld!.SpawnManager).IsNull();

        await Assert.That(Resolve(actor)).IsNull();
        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.QuestTravelTarget).IsNull();
    }

    // ================================================================
    // Fixtures
    // ================================================================

    private static void StageActiveQuest(GameplayActor actor, uint questId, QuestStatus status, int objective = 0)
    {
        var template = QuestManager.Instance.GetTemplate(questId)
            ?? throw new InvalidOperationException($"quest template {questId} not seeded");
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Status = status;
        quest.Step = status == QuestStatus.Ready ? QuestComponentKind.Ready : QuestComponentKind.Progress;
        quest.Objectives = [objective, 0, 0, 0, 0];
        actor.Character.Quests.ActiveQuests[questId] = quest;
        QuestFixtureRow.InvalidateAll();
    }

    /// <summary>
    /// Injects one NPC spawner into the character's own world SpawnManager —
    /// the same surface TrySpawnerPosition and the arm-2 offer scan read.
    /// </summary>
    private static void SeedNpcSpawner(GameplayActor actor, uint npcTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(94_600 + spawners.Count);
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
        var key = (uint)(94_700 + spawners.Count);
        spawners[key] = new DoodadSpawner
        {
            Id = key,
            UnitId = doodadTemplateId,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
        };
    }

    /// <summary>
    /// Seeds a gather-only quest (Start accept act + a Progress gather act) so
    /// arm 1b's resolver finds a real doodad source. Mirrors
    /// QuestObjectiveSourceTravelTests' shape with ids from this class's block.
    /// </summary>
    private static void SeedGatherQuestShape()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var questTemplates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate>)
            typeof(QuestManager).GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(GatherQuest, out var template))
        {
            template = new AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate { Id = GatherQuest, Level = 10 };
            questTemplates[GatherQuest] = template;
        }

        var componentTemplates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.QuestComponentTemplate>)
            typeof(QuestManager).GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(GatherComponent, out var component))
        {
            component = new AAEmu.Game.Models.Game.Quests.QuestComponentTemplate(template)
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
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            {
                ActId = GatherActId,
                DetailId = GatherComponent,
                DetailType = nameof(QuestActObjItemGather),
                Count = GatherNeed,
                ItemId = GatherItem,
                ThisComponentObjectiveIndex = 0,
            });
        }

        SeedDoodadLootChain();
        QuestFixtureRow.InvalidateAll();
        var row = QuestFixtureRow.FromQuestData(GatherQuest);
        if (row.GatherDoodadTemplate != GatherDoodad)
            throw new InvalidOperationException(
                $"fixture row resolved doodad {row.GatherDoodadTemplate}, expected {GatherDoodad}");
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
        if (!templates.TryGetValue(GatherDoodad, out var doodadTemplate))
        {
            doodadTemplate = new DoodadTemplate { Id = GatherDoodad, FuncGroups = [] };
            templates[GatherDoodad] = doodadTemplate;
        }
        if (doodadTemplate.FuncGroups.All(g => g.Id != GatherGroup))
        {
            doodadTemplate.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = GatherGroup,
                Almighty = GatherDoodad,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });
        }
    }
}