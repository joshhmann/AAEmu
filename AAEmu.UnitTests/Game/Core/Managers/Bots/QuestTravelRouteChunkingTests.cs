using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.World;
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
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Quest-travel ROUTE CHUNKING — both arms of <c>ArmQuestTravel</c>.
///
/// The defect: quest travel dispatched a SINGLE unwalkable leg
/// (<c>BotPath.PathTo(target)</c> — one waypoint). Measured on a 30-minute
/// 4-bot unattended run, destinations ranged 25.9-904.0 m from the bot's LIVE
/// position and 57 of 101 travel legs (56%) died
/// <c>result=TimedOut failure=Navigation elapsed=60.1s detail=navigation budget
/// exceeded</c>, re-dispatching the identical destination every 60 s; the one
/// 31.8 m leg completed. At <c>RoamSpeed</c> 2.5 m/s with a 60 s
/// <c>RoamLegTimeout</c> the honest envelope of a single leg is far below the
/// 150 m product, so the fix subdivides the ROUTE — it does not move, retune
/// or extend the budget.
///
/// These tests are hermetic: no live world, no wall-clock, no shared DB. The
/// leg-length bounds are asserted as PROPERTIES of the emitted route (every
/// leg <= chunk, last leg shorter, total == straight-line distance), and the
/// per-arm wiring is asserted through the production entry point
/// (<c>ArmQuestTravel</c>) against a headless rig actor.
///
/// Fixture ids 96_0xx are unclaimed repo-wide and absent from canonical
/// quest_contexts / npcs, so a shared-id collision with the process-wide
/// QuestManager singleton cannot mask a regression.
/// </summary>
[NotInParallel]
public class QuestTravelRouteChunkingTests
{
    // ---- fixture ids (block 96_0xx: free repo-wide + absent from canonical DB)
    private const uint GatherQuest = 96_001;
    private const uint GatherComponent = 96_002;
    private const uint GatherActId = 96_003;
    private const uint GatherItem = 96_004;
    private const uint GatherDoodad = 96_005;
    private const uint GatherGroup = 96_006;
    private const uint GatherFunc = 96_007;
    private const int GatherNeed = 3;

    private const uint OffererNpc = 96_010;   // a REAL spawned, in-band quest offerer

    /// <summary>Production leg constants the envelope is derived from (RoamSpeed default, RoamLegTimeout default).</summary>
    private const float ProductionSpeed = 2.5f;
    private static readonly TimeSpan ProductionLegTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The chunk the production defaults produce: 2.5 m/s x 60 s x
    /// <see cref="BotPath.TravelChunkEnvelopeMargin"/> (0.5) = 75 m. Stated as
    /// a literal on purpose — it is the contract the arm tests assert against,
    /// and it fails loudly if the margin is ever retuned.
    /// </summary>
    private const float ProductionChunkMeters = 75f;

    private const float ArrivalRadius = BotPath.ArrivalRadiusDefault;

    private static readonly MethodInfo ArmQuestTravel = typeof(BotRoamStepExecutor)
        .GetMethod("ArmQuestTravel", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ResolveObjectiveSource = typeof(BotRoamStepExecutor)
        .GetMethod("ResolveQuestObjectiveSource", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedDoodadLootInteraction(96_501, 96_502, 96_503);
        TravelIntentStore.ClearAll();
        SeedGatherQuestShape();
        GameplayActorTestRig.SeedQuestOffer(96_020, 96_021, OffererNpc, level: 3);
    }

    // =================================================================
    // (A) The derived chunk itself.
    // =================================================================

    /// <summary>
    /// The chunk is DERIVED from RoamSpeed x RoamLegTimeout, not hardcoded:
    /// a rig that lowers the speed or shortens the budget must get a shorter
    /// chunk, and the derivation must stay exact (no drift).
    /// </summary>
    [Test]
    public async Task ChunkMeters_IsDerivedFromRoamSpeedAndLegTimeout()
    {
        await Assert.That(new BotRoamStepExecutor().QuestTravelChunkMeters)
            .IsEqualTo(ProductionChunkMeters);
        await Assert.That(new BotRoamStepExecutor { RoamSpeed = 20f }.QuestTravelChunkMeters)
            .IsEqualTo(ProductionChunkMeters);
        await Assert.That(new BotRoamStepExecutor { RoamSpeed = 2.5f, RoamLegTimeout = TimeSpan.FromSeconds(30) }.QuestTravelChunkMeters)
            .IsEqualTo(37.5f);
        // Historical walking fixtures retain their 75 m budget.
        await Assert.That(new BotRoamStepExecutor { RoamSpeed = ProductionSpeed }.QuestTravelChunkMeters)
            .IsEqualTo(ProductionChunkMeters);
        await Assert.That(new BotRoamStepExecutor { RoamSpeed = 2f }.QuestTravelChunkMeters)
            .IsEqualTo(60f);
        await Assert.That(new BotRoamStepExecutor { RoamLegTimeout = TimeSpan.FromSeconds(30) }.QuestTravelChunkMeters)
            .IsEqualTo(75f);
        // Degenerate configuration fails closed to "no chunk" — which the route
        // builder reads as "emit the ordinary single-leg route", never as an
        // infinite or zero-length subdivision.
        await Assert.That(new BotRoamStepExecutor { RoamLegTimeout = TimeSpan.Zero }.QuestTravelChunkMeters)
            .IsEqualTo(0f);
    }

    // =================================================================
    // (B) The route geometry (pure BotPath — no engine state at all).
    // =================================================================

    /// <summary>
    /// A destination beyond the walkable envelope becomes a MULTI-WAYPOINT
    /// route and every leg it emits fits inside one leg's honest reach.
    /// </summary>
    [Test]
    public async Task FarDestination_ProducesMultiWaypointRoute_WithEveryLegInsideTheEnvelope()
    {
        var from = new Vector3(0, 0, 0);
        var target = new Vector3(904f, 0, 0); // the worst measured leg of the 30-min run
        var envelope = ProductionChunkMeters;

        var route = BotPath.PathTo(from, target, envelope, envelope);

        await Assert.That(route.Mode).IsEqualTo(BotPath.LoopMode.Once);
        // ceil(904 / 75) = 13 waypoints: 12 full legs + the 4 m remainder.
        await Assert.That(route.Waypoints.Count).IsEqualTo(13);
        AssertLegsWithin(route, from, envelope);
    }

    /// <summary>
    /// Inside the envelope: exactly ONE waypoint — the unwalkable-leg
    /// behaviour, pinned. 31.8 m was the one leg that completed live, so the
    /// near case must not change shape.
    /// </summary>
    [Test]
    public async Task NearDestination_StaysASingleWaypoint_UnchangedBehaviour()
    {
        var from = new Vector3(0, 0, 0);
        var target = new Vector3(31.8f, 0, 0);

        var route = BotPath.PathTo(from, target, ProductionChunkMeters, ProductionChunkMeters);

        await Assert.That(route.Waypoints.Count).IsEqualTo(1);
        await Assert.That(route.Waypoints[0]).IsEqualTo(target);
        await Assert.That(route.Mode).IsEqualTo(BotPath.LoopMode.Once);
    }

    /// <summary>Chunking must not move the destination it was asked to reach.</summary>
    [Test]
    public async Task Chunking_PreservesTheFinalDestinationExactly()
    {
        var from = new Vector3(1000f, -2000f, 130f);
        var target = new Vector3(1421.5f, -1653.25f, 118.75f); // not a multiple of the chunk, on purpose

        var route = BotPath.PathTo(from, target, ProductionChunkMeters, ProductionChunkMeters);

        var last = route.Waypoints[^1];
        await Assert.That(Math.Abs(last.X - target.X) <= 1e-4f).IsTrue();
        await Assert.That(Math.Abs(last.Y - target.Y) <= 1e-4f).IsTrue();
        await Assert.That(Math.Abs(last.Z - target.Z) <= 1e-4f).IsTrue();
    }

    /// <summary>
    /// Uniform, colinear, no drift: every intermediate waypoint sits on the
    /// straight line at its exact fraction, all full legs are equal length, and
    /// the route's total length equals the original distance (a subdivided
    /// polyline through colinear points adds ZERO extra length).
    /// </summary>
    [Test]
    public async Task Chunks_AreColinearUniform_AndAddNoPathLength()
    {
        var from = new Vector3(-400f, 900f, 60f);
        var target = new Vector3(500f, 2400f, 90f);
        var chunk = ProductionChunkMeters;
        var straight = Vector3.Distance(from, target);

        var route = BotPath.PathTo(from, target, chunk, chunk);

        var direction = Vector3.Normalize(target - from);
        var pathLength = 0f;
        var previous = from;
        for (var i = 0; i < route.Waypoints.Count; i++)
        {
            var waypoint = route.Waypoints[i];
            var leg = Vector3.Distance(previous, waypoint);
            // Colinear: the waypoint lies on the straight line through both ends.
            var offset = Vector3.Distance(waypoint - from, direction * Vector3.Distance(from, waypoint));
            await Assert.That(offset <= 1e-2f, $"waypoint {i} is off the straight line by {offset:0.###}m").IsTrue();
            // Uniform: every full leg is exactly one chunk; only the last may be shorter.
            await Assert.That(leg <= chunk + 1e-3f, $"leg {i} is {leg:0.##}m, over the {chunk}m chunk").IsTrue();
            await Assert.That(leg >= chunk - 1e-3f || i == route.Waypoints.Count - 1,
                $"leg {i} is {leg:0.##}m — a full leg must be exactly one chunk").IsTrue();
            pathLength += leg;
            previous = waypoint;
        }
        await Assert.That(Math.Abs(pathLength - straight) <= 1e-2f,
            $"route length {pathLength:0.###}m drifted from the straight-line {straight:0.###}m").IsTrue();
    }

    /// <summary>
    /// The chunked route really is walkable end-to-end under the ordinary
    /// route machinery: <see cref="BotPath.Arrive"/> advances the index on every
    /// intermediate waypoint and the LAST arrival sets IsFinished, leaving the
    /// bot standing on the destination.
    /// </summary>
    [Test]
    public async Task ChunkedRoute_AdvancesThroughEveryChunk_AndFinishesOnTheDestination()
    {
        var from = new Vector3(0, 0, 0);
        var target = new Vector3(400f, 0, 0);
        var route = BotPath.PathTo(from, target, ProductionChunkMeters, ProductionChunkMeters);
        await Assert.That(route.Waypoints.Count).IsEqualTo(6); // 400 = 5 x 75 + 25

        var position = from;
        var indexMoves = 0;
        var lastIndex = 0;
        for (var tick = 0; tick < 5000 && !route.IsFinished; tick++)
        {
            position = route.Move(position, flatArrival: true);
            if (route.CurrentIndex != lastIndex)
            {
                lastIndex = route.CurrentIndex;
                indexMoves++;
            }
        }

        await Assert.That(route.IsFinished, "the route never finished").IsTrue();
        await Assert.That(Vector3.Distance(position, target) <= ArrivalRadius).IsTrue();
        // The index advanced once per waypoint: the bot really walked through
        // every chunk instead of jumping straight to the destination.
        await Assert.That(indexMoves).IsEqualTo(route.Waypoints.Count - 1);
    }

    /// <summary>
    /// Degenerate inputs must never produce a zero-length or unbounded
    /// subdivision: a zero chunk, a zero/negative envelope, a zero-length
    /// destination and a sub-arrival-radius destination all yield the ordinary
    /// single-leg route (one waypoint, finite, instantly arrived).
    /// </summary>
    [Test]
    [Arguments(0f, 75f, 400f)]
    [Arguments(-5f, 75f, 400f)]
    [Arguments(75f, 0f, 400f)]
    [Arguments(75f, -5f, 400f)]
    [Arguments(0f, 0f, 400f)]
    public async Task DegenerateChunkOrEnvelope_FallsBackToTheSingleLegRoute(float chunk, float envelope, float distance)
    {
        var from = new Vector3(0, 0, 0);
        var target = new Vector3(distance, 0, 0);

        var route = BotPath.PathTo(from, target, chunk, envelope);

        await Assert.That(route.Waypoints.Count).IsEqualTo(1);
        await Assert.That(route.Waypoints[0]).IsEqualTo(target);
    }

    /// <summary>
    /// A destination the bot is ALREADY standing on (and one just inside the
    /// arrival radius) yields one waypoint and arrives on the FIRST Move — no
    /// zero-length legs, no division by zero, no extra ticks.
    /// </summary>
    [Test]
    [Arguments(0f)]
    [Arguments(0.2f)]
    public async Task ZeroAndSubArrivalDistance_ProducesOneWaypoint_ThatArrivesImmediately(float distance)
    {
        var from = new Vector3(0, 0, 0);
        var target = new Vector3(distance, 0, 0);

        var route = BotPath.PathTo(from, target, ProductionChunkMeters, ProductionChunkMeters);

        await Assert.That(route.Waypoints.Count).IsEqualTo(1);
        var moved = route.Move(from, flatArrival: true);
        await Assert.That(route.IsFinished, "a sub-arrival destination must finish immediately").IsTrue();
        await Assert.That(moved).IsEqualTo(target);
    }

    // =================================================================
    // (C) Both arm sites of ArmQuestTravel, through the production entry point.
    // =================================================================

    /// <summary>
    /// ARM 1b (objective source, the ~1933 arm site): a far doodad source is
    /// reached through a CHUNKED route instead of one unwalkable leg, and the
    /// arm's own contract (target, reason, owner) is unchanged.
    /// </summary>
    [Test]
    public async Task ObjectiveSourceArm_FarSource_ArmsChunkedRoute()
    {
        var source = new Vector3(900f, 0, 0);
        var (executor, actor, runtime) = CreateRig("chunk-source-far");
        StageActiveQuest(actor, GatherQuest, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, source);

        // NON-VACUITY PRECONDITION, asserted before the arm runs: the gather
        // quest is really staged and its resolver really names the far
        var outArgs = new object?[] { actor, 0u, null, null, false };
        var resolved = (bool)ResolveObjectiveSource.Invoke(null, outArgs)!;
        await Assert.That(resolved,
            "the objective-source resolver must find the seeded source (arm-1b non-vacuity)").IsTrue();
        await Assert.That((uint)outArgs[3]!).IsEqualTo(GatherDoodad);
        await Assert.That(Vector3.Distance((Vector3)outArgs[2]!, source) <= 1f,
            "the resolved source must be the far spawner the test seeded").IsTrue();


        await Assert.That(Arm(executor, runtime, actor)).IsTrue();

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNotNull();
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
        var route = executor.GetRoamRoute(runtime.CharacterId)!;
        await Assert.That(route.Waypoints.Count).IsEqualTo(12); // 900 m = 12 x 75 m legs exactly
        AssertLegsWithin(route, new Vector3(0, 0, 0), ProductionChunkMeters);
    }

    /// <summary>
    /// ARM 2 (offerer, the ~1955 arm site): same, through the nearest in-band
    /// offerer spawner.
    /// </summary>
    [Test]
    public async Task OffererArm_FarOfferer_ArmsChunkedRoute()
    {
        // ~424 m of separation: well beyond both the 300 m per-leg envelope
        // and the 150 m a 2.5 m/s / 60 s leg can honestly walk, and clear of
        // the TeleportJumpThresholdMeters guard that would (correctly) read a
        // fresh rig position as a harness teleport. Against the production
        // 75 m chunk that is exactly six waypoints.
        var (executor, actor, runtime) = CreateRig("chunk-offerer-far");
        var offerer = new Vector3(300f, 300f, 0);
        SeedNpcSpawner(actor, OffererNpc, offerer);

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNotNull();
        await Assert.That(state.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");
        var route = executor.GetRoamRoute(runtime.CharacterId)!;
        // ceil(424.26 / 75) = 6 waypoints: five 75 m legs plus the 49 m tail.
        await Assert.That(route.Waypoints.Count).IsEqualTo(6);
        await Assert.That(Vector3.Distance(route.Waypoints[^1], offerer) <= 1e-3f,
            "the route must still end on the offerer spawner").IsTrue();
        AssertLegsWithin(route, new Vector3(0, 0, 0), ProductionChunkMeters);
    }

    /// <summary>
    /// The near case through the SAME arm: a 60 m offerer still gets exactly
    /// the single-leg route the arm built before this change — the chunking is
    /// additive, not a rewrite of the ordinary path.
    /// </summary>
    [Test]
    public async Task OffererArm_NearOfferer_StaysASingleWaypoint()
    {
        var (executor, actor, runtime) = CreateRig("chunk-offerer-near");
        SeedNpcSpawner(actor, OffererNpc, new Vector3(60f, 0, 0));

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();

        var route = executor.GetRoamRoute(runtime.CharacterId)!;
        await Assert.That(route.Waypoints.Count).IsEqualTo(1);
        await Assert.That(route.Waypoints[0]).IsEqualTo(new Vector3(60f, 0, 0));
    }

    /// <summary>
    /// The two guards the change must NOT regress: the live-route guard (a
    /// route already walking is never re-armed, so chunking cannot restart a
    /// trek mid-way) and the 3-arm objective-source cap.
    /// </summary>
    [Test]
    public async Task LiveRouteGuard_StillHolds_AcrossAChunkedArm()
    {
        var (executor, actor, runtime) = CreateRig("chunk-guard-live");
        StageActiveQuest(actor, GatherQuest, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, new Vector3(900f, 0, 0));

        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var armed = executor.GetRoamRoute(runtime.CharacterId)!;

        // A second arm must refuse and leave the very same route instance in
        // place — the progress a chunked trek has made is protected.
        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsSameReferenceAs(armed);

        // And it stays protected across REAL progress: walk the route forward
        // through whole chunks (Move alone is the route layer's advance; the
        // production advance additionally dispatches the next MoveTo leg),
        // then re-arm. The guard must refuse regardless.
        var position = new Vector3(0, 0, 0);
        var targetIndex = Math.Min(2, armed.Waypoints.Count - 1);
        while (armed.CurrentIndex < targetIndex)
            position = armed.Move(position, flatArrival: true);
        await Assert.That(armed.CurrentIndex).IsEqualTo(targetIndex);
        await Assert.That(Vector3.Distance(position, Vector3.Zero)).IsGreaterThan(1f);

        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsSameReferenceAs(armed);
    }

    /// <summary>
    /// The 3-arm objective-source cap still counts ARMS, not legs. Each arm in
    /// this fixture ends where the next one is issued — the trek has walked as
    /// far as it can before the cap withholds — so the 4th arm declines with
    /// the byte-identical unreachable reason, exactly as before the change.
    /// </summary>
    [Test]
    public async Task ObjectiveSourceCap_StillStopsAfterThreeArms_UnderChunking()
    {
        const float chunk = ProductionChunkMeters;
        var source = new Vector3(chunk, 0, 0); // one full leg: the walk ENDS here
        var (executor, actor, runtime) = CreateRig("chunk-guard-cap");
        StageActiveQuest(actor, GatherQuest, objective: 0);
        SeedDoodadSpawner(actor, GatherDoodad, source);

        // The route stands at chunk 1 of a 1-chunk trek — walked, not finished,
        // and still inside the 3-arm budget.
        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var route = executor.GetRoamRoute(runtime.CharacterId)!;
        await Assert.That(route.Waypoints.Count).IsEqualTo(1);
        await Assert.That(route.IsFinished).IsFalse();
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.QuestObjectiveSourceArms).IsEqualTo(1);

        // Arms 2 and 3: same spawner, still under the cap.
        for (var arm = 2; arm <= 3; arm++)
        {
            executor.SetRoamRoute(actor.Character, null);
            await Assert.That(Arm(executor, runtime, actor)).IsTrue();
            await Assert.That(executor.GetBotState(runtime.CharacterId)!.QuestObjectiveSourceArms).IsEqualTo(arm);
        }

        // The 4th arm declines: the cap, not the chunking, is what stops it.
        executor.SetRoamRoute(actor.Character, null);
        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.QuestTravelTarget).IsNull();
        await Assert.That(state.QuestTravelReason).IsEqualTo(
            $"objective-source-unreachable (doodad {GatherDoodad})");
    }

    // =================================================================
    // Helpers / fixtures
    // =================================================================

    /// <summary>Every leg the route emits, measured from its start, fits one leg's reach.</summary>
    private static void AssertLegsWithin(BotPath route, Vector3 from, float envelope)
    {
        var previous = from;
        for (var i = 0; i < route.Waypoints.Count; i++)
        {
            var leg = Vector3.Distance(previous, route.Waypoints[i]);
            if (leg > envelope + 1e-3f)
                throw new InvalidOperationException($"leg {i} is {leg:0.##}m — over the {envelope:0.##}m envelope");
            previous = route.Waypoints[i];
        }
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime) CreateRig(string name)
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
            // Production leg constants, so the derived chunk under test is the
            // one production actually uses.
            RoamSpeed = ProductionSpeed,
            RoamLegTimeout = ProductionLegTimeout,
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
        var key = (uint)(96_700 + spawners.Count);
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
        var key = (uint)(96_800 + spawners.Count);
        spawners[key] = new DoodadSpawner
        {
            Id = key,
            UnitId = doodadTemplateId,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
        };
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

        // The fixture row resolves the gather source by INVERTING the doodad's
        // loot chain, and that inversion silently yields nothing while the
        // loot singleton is unrealized — force it before reading the row.
        _ = LootGameData.Instance;

        GameplayActorTestRig.SeedGatherDoodadSource(GatherDoodad, GatherGroup, GatherFunc, GatherItem);
        QuestFixtureRow.InvalidateAll();
        var row = QuestFixtureRow.FromQuestData(GatherQuest);
        if (row.GatherDoodadTemplate != GatherDoodad)
            throw new InvalidOperationException($"fixture row resolved doodad {row.GatherDoodadTemplate}, expected {GatherDoodad}");
    }
}