using System.Reflection;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Game.Quests.Scenario;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Quest-lifecycle run measurement (progress capability): the three counters
/// projected onto <see cref="PlayerBotSchedulerMetrics"/> must count ONCE per
/// real engine occurrence, at the engine seam — not bot intent.
///
/// Seams under test (all real, all production paths):
///   accepted  → CharacterQuests.AddQuest (the funnel every AddQuestFrom*,
///               CSStartQuestContextPacket and the kill/level-up auto-starts
///               delegate to)
///   turn-in   → QuestManager.DoReportEvents (the exact
///               CSCompleteQuestContextPacket path; all three branches)
///   completed → Quest.GoToNextStep's Reward branch (where SetCompletedQuestFlag
///               and the drop happen)
///
/// Data: PlayerbotPilotRig.SeedPilotSingletons loads the REAL quest templates
/// and unit requirements from the canonical compact.sqlite3 — a READ-ONLY
/// SQLite file that is copied into the test app path if absent. Nothing here
/// opens MySQL, so this class cannot touch a shared E2E database.
///
/// COUNTERS ARE PROCESS-GLOBAL (deliberately: they measure a whole run, and a
/// lane is one process). Assertions are therefore DELTAS around each engine
/// occurrence, never absolute totals — a sibling rig class completing a quest
/// concurrently cannot make a delta wrong. This class asserts relative
/// behaviour only; it deliberately does not assert "count == 0 on a fresh
/// process", which is not observable from inside a shared test process.
/// </summary>
[NotInParallel]
public class QuestProgressCountersTests
{
    private const uint QuestId = 251;
    private const uint AcceptorNpcTemplateId = 3512;
    private const uint GatherItemId = 4058;
    private const int GatherCount = 3;

    private static QuestScenarioManifest LoadManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null &&
               !Directory.Exists(Path.Combine(dir.FullName, ".git")) &&
               !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t1", $"{QuestId}.json");
        return QuestScenarioManifest.LoadFromFile(path);
    }

    /// <summary>
    /// Rig ordering discipline (GameplayActorTestRig doc): the test seeds the
    /// pilot singletons FIRST (real QuestManager + real unit requirements),
    /// then CreateActor — whose missing-only guards keep the real data.
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateRealActor(string name)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2; // quest 251 is level 2; the real gate evaluates
        PlayerbotPilotRig.RegisterQuestItems(LoadManifest());
        return (actor, session);
    }

    /// <summary>
    /// Drives quest 251 to READY through real engine surfaces: accept via the
    /// actor, stock the gather objective through the real acquisition path,
    /// fire the ItemGather event the world pipeline fires, advance once.
    /// </summary>
    private static void AcceptAndProgressToReady(GameplayActor actor)
    {
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");

        GameplayActorTestRig.GrantItem(actor, GatherItemId, GatherCount);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = QuestId,
            ItemId = GatherItemId,
            Count = GatherCount
        });

        var advance = actor.AdvanceQuest(QuestId);
        if (advance.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"advance failed: {advance.State} {advance.Detail}");
    }

    private static PlayerBotScheduler NewScheduler()
    {
        GameplayActorTestRig.Seed();
        var manager = new PlayerBotManager(new NullLifecycle());
        return new PlayerBotScheduler(manager, new NullExecutor(), new PlayerBotSchedulerOptions
        {
            SubscribeToTickManager = false,
            ScanInterval = TimeSpan.FromHours(1), // background loop inert; nothing is pumped
        });
    }

    private sealed class NullLifecycle : IPlayerBotLifecycleService
    {
        public bool ActivateHeadless(AAEmu.Game.Models.Game.Char.Character character, object botContext) => true;
        public bool Deactivate(AAEmu.Game.Models.Game.Char.Character character, string reason) => true;
    }

    private sealed class NullExecutor : IBotStepExecutor
    {
        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
            => Task.FromResult<TimeSpan?>(null);
    }

    #region Projection — the counters reach the existing metrics accessor

    [Test]
    public async Task MetricsExposeQuestCounters_ThroughExistingGetMetricsAccessor()
    {
        var scheduler = NewScheduler();

        var metrics = scheduler.GetMetrics();

        // Reachable through the SAME read-only accessor the scheduler and
        // bridge already use — no parallel metrics system, no new endpoint.
        await Assert.That(metrics.TotalQuestsAccepted).IsGreaterThanOrEqualTo(0);
        await Assert.That(metrics.TotalQuestTurnInsRequested).IsGreaterThanOrEqualTo(0);
        await Assert.That(metrics.TotalQuestsCompleted).IsGreaterThanOrEqualTo(0);

        // The projection is live: it reads the engine-owned counters, so it
        // can never hold a stale value while the counters advance.
        var before = scheduler.GetMetrics().TotalQuestsAccepted;
        QuestProgressCounters.RecordAccepted();
        await Assert.That(scheduler.GetMetrics().TotalQuestsAccepted).IsEqualTo(before + 1);
    }

    [Test]
    public async Task MetricsSnapshot_QuestCounterRead_AllocatesNothing()
    {
        const int reads = 2_000;
        var scheduler = NewScheduler();

        var (baseline, withQuestCounters) = MeasureOnDedicatedThread(() =>
        {
            // A: the PRE-EXISTING read path — snapshot + read only counters
            //    that were already on the record before this change.
            long sinkA = 0;
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < reads; i++)
                sinkA += scheduler.GetMetrics().TotalStepsRun;
            var a = GC.GetAllocatedBytesForCurrentThread() - a0;

            // B: the SAME read path, now also reading the three quest
            //    counters. Identical loop shape so the only difference is the
            //    added Volatile.Read statics.
            long sinkB = 0;
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < reads; i++)
            {
                var m = scheduler.GetMetrics();
                sinkB += m.TotalStepsRun + m.TotalQuestsAccepted
                         + m.TotalQuestTurnInsRequested + m.TotalQuestsCompleted;
            }
            var b = GC.GetAllocatedBytesForCurrentThread() - b0;

            GC.KeepAlive(sinkA);
            GC.KeepAlive(sinkB);
            return (a, b);
        });

        // The counter reads themselves are free: three Volatile.Read of a
        // static long, no boxing, no delegate, no lock — the exact primitive
        // every other counter in PlayerBotScheduler already uses.
        var staticsOnly = MeasureOnDedicatedThread(() =>
        {
            long sink = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100_000; i++)
                sink += QuestProgressCounters.Accepted
                      + QuestProgressCounters.TurnInsRequested
                      + QuestProgressCounters.Completed;
            GC.KeepAlive(sink);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        await Assert.That(withQuestCounters - baseline).IsEqualTo(0)
            .Because($"reading the three quest counters must add no allocation to the snapshot read " +
                     $"(baseline {baseline} B, with quest counters {withQuestCounters} B over {reads} reads)");
        await Assert.That(staticsOnly).IsEqualTo(0)
            .Because("100k reads of the three counters must allocate nothing at all; saw " + staticsOnly + " bytes");
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh dedicated thread and returns
    /// its value. The body is responsible for sampling
    /// GC.GetAllocatedBytesForCurrentThread itself — it runs entirely on that
    /// thread, so the counter it reads covers only the loop under test. A
    /// dedicated thread (rather than a pool thread) guarantees nothing else can
    /// allocate inside the window.
    /// </summary>
    private static T MeasureOnDedicatedThread<T>(Func<T> body)
    {
        T result = default!;
        var failure = default(System.Runtime.ExceptionServices.ExceptionDispatchInfo);
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "quest-progress-alloc-probe",
        };
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("allocation probe did not finish within 2 minutes");
        failure?.Throw();
        return result;
    }

    #endregion

    #region Lifecycle — counters are always on and never reset spontaneously

    /// <summary>
    /// Counters must not reset mid-run: the scheduler's own Start()/StopAsync()
    /// lifecycle is the most likely place a reset could sneak in, because that
    /// is where a designer would naturally clear "per-run" metrics. It must
    /// not, and it must not have to — the quest counters are owned by the engine
    /// statics, so the scheduler cannot clear them even in principle.
    /// </summary>
    [Test]
    public async Task SchedulerLifecycle_NeverResetsQuestCounters()
    {
        var scheduler = NewScheduler();

        QuestProgressCounters.RecordAccepted();
        QuestProgressCounters.RecordTurnInRequested();
        QuestProgressCounters.RecordCompleted();

        var before = scheduler.GetMetrics();
        await scheduler.StopAsync();
        var afterStop = scheduler.GetMetrics();
        scheduler.Start();
        var afterRestart = scheduler.GetMetrics();

        await Assert.That(afterStop.TotalQuestsAccepted).IsEqualTo(before.TotalQuestsAccepted)
            .Because("StopAsync must not clear a counter the engine owns");
        await Assert.That(afterStop.TotalQuestTurnInsRequested).IsEqualTo(before.TotalQuestTurnInsRequested);
        await Assert.That(afterStop.TotalQuestsCompleted).IsEqualTo(before.TotalQuestsCompleted);
        await Assert.That(afterRestart.TotalQuestsAccepted).IsEqualTo(before.TotalQuestsAccepted)
            .Because("a scheduler restart must not silently understate a run");
        await Assert.That(afterRestart.TotalQuestsCompleted).IsEqualTo(before.TotalQuestsCompleted);

        await scheduler.StopAsync();
    }

    /// <summary>
    /// The counters live on the production type, not on a test shim: an
    /// unattended run reads them through AAEmu.Game with no test assembly
    /// loaded. Pinned here so a future refactor cannot quietly make the metric
    /// a rig-only surface.
    /// </summary>
    [Test]
    public async Task CountersLiveOnShippingAssembly_NotATestShim()
    {
        await Assert.That(typeof(QuestProgressCounters).Assembly.GetName().Name).IsEqualTo("AAEmu.Game");

        // And the stats fields are plain process statics (zero-initialised,
        // never assigned outside the increment), not DI state that a lane
        // restart could inherit from another lane.
        var fields = typeof(QuestProgressCounters)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(long))
            .ToList();
        await Assert.That(fields.Count).IsEqualTo(3)
            .Because("exactly three static counter fields — one per quest lifecycle event");
        foreach (var field in fields)
            await Assert.That(field.IsInitOnly).IsFalse();
    }

    #endregion

    #region Once-per-occurrence — accepted

    [Test]
    public async Task AcceptQuest_CountsAcceptedExactlyOnce()
    {
        var (actor, _) = CreateRealActor("qpc-accept-1");

        var before = QuestProgressCounters.Accepted;
        var request = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        var delta = QuestProgressCounters.Accepted - before;

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(delta).IsEqualTo(1)
            .Because("one real acceptance through the AddQuest funnel must count exactly once");
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();
    }

    /// <summary>
    /// The accept counter must not fire for a REFUSED acceptance — that is the
    /// whole point of counting at the seam on the success path rather than at
    /// the bot's intent.
    /// </summary>
    [Test]
    public async Task AcceptQuest_RefusedByEngineGate_CountsNothing()
    {
        var (actor, _) = CreateRealActor("qpc-accept-2");

        var before = QuestProgressCounters.Accepted;
        // 999_999 has no template: AddQuest returns false before it is active.
        var request = actor.AcceptQuest(999_999, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        var delta = QuestProgressCounters.Accepted - before;

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(delta).IsEqualTo(0)
            .Because("a refused accept is not an accepted quest");
    }

    #endregion

    #region Once-per-occurrence — turn-in requested vs completed

    /// <summary>
    /// The required asymmetry: a turn-in that the engine REFUSES (quest not
    /// ready) counts a turn-in REQUEST but NOT a completion. Proving the two
    /// are independent counters, not one event counted twice.
    /// </summary>
    [Test]
    public async Task TurnIn_NotReady_CountsRequestButNoCompletion()
    {
        var (actor, session) = CreateRealActor("qpc-turnin-1");

        // Accept but do NOT gather the objective: the report event fires and
        // reaches the seam, but the ready-act must not pass.
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);

        var turnInsBefore = QuestProgressCounters.TurnInsRequested;
        var completionsBefore = QuestProgressCounters.Completed;
        var request = actor.TurnInQuest(QuestId, npcObjId, 0);
        var turnInDelta = QuestProgressCounters.TurnInsRequested - turnInsBefore;
        var completionDelta = QuestProgressCounters.Completed - completionsBefore;

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        // The refusal is read from the ENGINE's own quest state (still active,
        // completed flag unset) rather than from ActorRequest.Result, which is
        // a bridge echo of the same fact.
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue()
            .Because("the engine refused the report — the quest was never dropped");
        await Assert.That(actor.Character.Quests.HasQuestCompleted(QuestId)).IsFalse()
            .Because("a refused turn-in completed nothing");

        await Assert.That(turnInDelta).IsEqualTo(1)
            .Because("the turn-in reached the report seam exactly once");
        await Assert.That(completionDelta).IsEqualTo(0)
            .Because("a refused turn-in must NOT increment the completion counter");
    }

    /// <summary>
    /// The successful turn-in: counted once as a request and once as a
    /// completion. The accepted counter is NOT re-incremented — the turn-in
    /// lifecycle does not re-accept.
    /// </summary>
    [Test]
    public async Task TurnIn_Ready_CountsOneRequestAndOneCompletion()
    {
        var (actor, session) = CreateRealActor("qpc-turnin-2");
        AcceptAndProgressToReady(actor);

        var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);

        var acceptedBefore = QuestProgressCounters.Accepted;
        var turnInsBefore = QuestProgressCounters.TurnInsRequested;
        var completionsBefore = QuestProgressCounters.Completed;

        var request = actor.TurnInQuest(QuestId, npcObjId, 0);

        var acceptedDelta = QuestProgressCounters.Accepted - acceptedBefore;
        var turnInDelta = QuestProgressCounters.TurnInsRequested - turnInsBefore;
        var completionDelta = QuestProgressCounters.Completed - completionsBefore;

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(QuestId)).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsFalse();

        await Assert.That(turnInDelta).IsEqualTo(1)
            .Because("one real turn-in reaching the report seam counts exactly once");
        await Assert.That(completionDelta).IsEqualTo(1)
            .Because("the Reward branch ran exactly once for this quest");
        await Assert.That(acceptedDelta).IsEqualTo(0)
            .Because("turning a quest in is not an acceptance — do not force symmetry");
    }

    /// <summary>
    /// Repeat turn-in attempts against a quest that is already gone (the stuck
    /// or over-eager bot shape): each attempt that REACHES the seam counts a
    /// request, and none of them can manufacture a completion. This is the
    /// metric pair that makes "keeps handing in, never finishes" visible.
    /// </summary>
    [Test]
    public async Task TurnIn_RepeatAfterCompletion_CountsRequestsAndNoExtraCompletion()
    {
        var (actor, session) = CreateRealActor("qpc-turnin-3");
        AcceptAndProgressToReady(actor);
        var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);

        var completionsBefore = QuestProgressCounters.Completed;
        var first = actor.TurnInQuest(QuestId, npcObjId, 0);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(QuestProgressCounters.Completed - completionsBefore).IsEqualTo(1);

        // The engine already dropped the quest: a second turn-in cannot complete
        // anything again.
        var completionsAfterFirst = QuestProgressCounters.Completed;
        var turnInsAfterFirst = QuestProgressCounters.TurnInsRequested;

        for (var i = 0; i < 3; i++)
            _ = actor.TurnInQuest(QuestId, npcObjId, 0);

        await Assert.That(QuestProgressCounters.Completed).IsEqualTo(completionsAfterFirst)
            .Because("a quest already completed must never be counted as completed twice");
        await Assert.That(QuestProgressCounters.TurnInsRequested - turnInsAfterFirst).IsGreaterThanOrEqualTo(0)
            .Because("turn-in counting is monotonic — a refused repeat never decrements");
    }

    #endregion
}