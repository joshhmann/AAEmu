using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Plan T5 rigs (audit L1-f): the executor's retirement owner is the one place
/// a bot leaves its execution ownership, so it is where the arbiter's memory
/// for that bot is dropped. A retirement that succeeds reports the bot once —
/// including the "nothing to retire" path — and a refused retirement reports
/// nothing; the production wiring (RetiredBotSink -> IBotGoalArbiter.Forget)
/// then makes a re-enrolled bot be elected afresh instead of being reported
/// Unchanged. The supersede-retirement semantics themselves (outgoing module
/// asked exactly once, an owner handing over to itself not at all) are pinned
/// by BotGoalArbiterTests.
/// </summary>
[NotInParallel]
public class BotRetirementOwnerTests
{
    #region Rig

    private sealed class CountingModule : IBotActivityModule
    {
        public string Name { get; }
        public int Priority { get; }
        public string ActivityName { get; }
        public int Activations { get; private set; }
        public int Retirements { get; private set; }

        public CountingModule(string name, int priority, string activityName)
        {
            Name = name;
            Priority = priority;
            ActivityName = activityName;
        }

        public BotActivityDecision CanActivate(BotActivityContext context) =>
            BotActivityDecision.Allow(ActivityName);

        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity(ActivityName, Name);
        }

        public bool Deactivate(BotActivityContext context)
        {
            Retirements++;
            return true;
        }
    }

    private readonly List<(BotRoamStepExecutor Executor, GameplayActor Actor)> _rigs = [];
    private HashSet<uint> _previousTasks = [];

    private static ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task> Tasks
        => (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(TaskManager.Instance)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        _previousTasks = Tasks.Keys.ToHashSet();
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (executor, actor) in _rigs)
        {
            executor.ReleaseRecovery(actor.Character.Id, "retirement-owner teardown");
            actor.PreemptCurrent("retirement-owner teardown");
        }
        foreach (var task in Tasks.Where(x => !_previousTasks.Contains(x.Key)).Select(x => x.Value).ToArray())
            TaskManager.Instance.Cancel(task);
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    private (BotRoamStepExecutor Executor, PlayerBotRuntime Bot, FakeTimeProvider Clock) CreateExecutor(GameplayActor actor)
    {
        var clock = new FakeTimeProvider();
        var executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false,
            EnableWildlifeButcher = false,
            GroundHeightProvider = (_, _) => 0f,
        };
        _rigs.Add((executor, actor));
        return (executor, new PlayerBotRuntime(actor.Character, "rig"), clock);
    }

    private static bool Retire(BotRoamStepExecutor executor, PlayerBotRuntime bot, string reason, ulong? generation = null)
    {
        bool retired;
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        try
        {
            lock (bot.Sync)
            {
                bot.State = PlayerBotState.Deactivating;
                retired = executor.TryRetireNativeWork(bot, generation ?? bot.Generation, reason);
            }
        }
        finally
        {
            ExecutionBoundary.ResetForTest();
        }
        return retired;
    }

    #endregion

    [Test]
    public async Task RetireNativeWork_ReportsTheBotOnce_WhenRetirementSucceeds()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("retire-report");
        var (executor, bot, clock) = CreateExecutor(actor);
        var reported = new List<uint>();
        executor.RetiredBotSink = reported.Add;

        // One wake so the per-bot execution state exists (the tail path).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(bot, CancellationToken.None);

        var retired = Retire(executor, bot, "rig-retire");

        await Assert.That(retired).IsTrue();
        await Assert.That(reported).IsEquivalentTo([actor.Character.Id]);
    }

    [Test]
    public async Task RetireNativeWork_WithNoExecutionState_StillReportsTheBot()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("retire-report-empty");
        var (executor, bot, _) = CreateExecutor(actor);
        var reported = new List<uint>();
        executor.RetiredBotSink = reported.Add;

        // No wake: the bot is retired before it ever held execution state.
        var retired = Retire(executor, bot, "rig-retire");

        await Assert.That(retired).IsTrue();
        await Assert.That(reported).IsEquivalentTo([actor.Character.Id]);
    }

    [Test]
    public async Task RetireNativeWork_RefusedByTheGenerationCheck_ReportsNothing()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("retire-report-refused");
        var (executor, bot, clock) = CreateExecutor(actor);
        var reported = new List<uint>();
        executor.RetiredBotSink = reported.Add;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(bot, CancellationToken.None);

        var retired = Retire(executor, bot, "rig-retire", bot.Generation + 1);

        await Assert.That(retired).IsFalse();
        await Assert.That(reported).IsEmpty();
    }

    [Test]
    public async Task RetiredBotSink_WiredToTheArbiter_DropsMemorySoTheNextElectionActivatesAgain()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("retire-compose");
        var (executor, bot, _) = CreateExecutor(actor);
        var module = new CountingModule("RetireProbe", 50, "probe.work");
        var arbiter = new BotGoalArbiter([module]);
        // The production wiring (Program.cs): retirement is the single point
        // where the bot leaves the world, so the arbiter forgets it there.
        executor.RetiredBotSink = characterId => arbiter.Forget(characterId);
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);

        await steps.StepAsync(bot, CancellationToken.None);
        await Assert.That(module.Activations).IsEqualTo(1);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("probe.work");

        // A steady bot is reported Unchanged: the module is not re-activated.
        await steps.StepAsync(bot, CancellationToken.None);
        await Assert.That(module.Activations).IsEqualTo(1);

        var retired = Retire(executor, bot, "rig-retire");
        await Assert.That(retired).IsTrue();
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsNull();

        // Re-enrolled: the same bot is elected afresh, not reported Unchanged.
        await steps.StepAsync(bot, CancellationToken.None);
        await Assert.That(module.Activations).IsEqualTo(2);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("probe.work");
    }
}
