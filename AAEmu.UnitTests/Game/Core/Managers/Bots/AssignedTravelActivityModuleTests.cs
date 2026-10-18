using AAEmu.Game.Core.Managers.Bots;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The operator assignment band and the retirement contract (plan T1, gaps L4-a
/// and N3/N4). Two claims are pinned here: the assignment band sits ABOVE every
/// ordinary band, so a live assignment decides alone instead of winning a
/// weight comparison; and when it takes the activity away from another module,
/// that module is asked to release its work — a refusal is recorded and the
/// assignment does NOT claim success.
/// </summary>
[NotInParallel]
public class AssignedTravelActivityModuleTests
{
    [Test]
    public async Task CanActivate_WithoutAssignment_Denies()
    {
        var rig = CreateRig();

        var decision = rig.Module.CanActivate(rig.Context());

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).IsEqualTo("no operator assignment");
        await Assert.That(rig.Registry.TryGet(rig.Runtime.CharacterId, out _)).IsFalse();
    }

    [Test]
    public async Task CanActivate_WithLiveAssignment_AllowsTheAssignedActivity()
    {
        var rig = CreateRig();
        rig.Assign();

        var decision = rig.Module.CanActivate(rig.Context());

        await Assert.That(decision.CanActivate).IsTrue();
        await Assert.That(decision.ActivityName).IsEqualTo(BotAssignmentActivities.Travel);
    }

    [Test]
    public async Task CanActivate_DeadBot_Denies()
    {
        var rig = CreateRig();
        rig.Assign();
        rig.Runtime.Character.Hp = 0;

        var decision = rig.Module.CanActivate(rig.Context());

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot).IsEqualTo("the bot is dead or incapacitated");
    }

    /// <summary>
    /// The band's rank is not a magic number: a module one band BELOW it (the
    /// highest ordinary band, Schedules = 100) must never even be asked while an
    /// assignment is live, because that is what makes an assignment a preemption
    /// rather than a competing motivation.
    /// </summary>
    [Test]
    public async Task LiveAssignment_DecidesAlone_LowerBandIsNeverAsked()
    {
        var rig = CreateRig();
        var lower = new FakeModule("LowerBand", 100, "lower.activity");
        var arbiter = new BotGoalArbiter([rig.Module, lower]);
        rig.Assign();

        var arbitration = arbiter.Arbitrate(rig.Runtime, 12f);

        await Assert.That(arbitration.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbitration.Activity!.Name).IsEqualTo(BotAssignmentActivities.Travel);
        await Assert.That(lower.Probes).IsEqualTo(0);
        await Assert.That(arbiter.GetActiveActivity(rig.Runtime.CharacterId)).IsEqualTo(BotAssignmentActivities.Travel);
    }

    /// <summary>
    /// The refusal path (gap N3): the outgoing module keeps holding its work, so
    /// the assignment is recorded as FAILED with the refusal as the reason — the
    /// operator never sees a success claim for work that was not released.
    /// </summary>
    [Test]
    public async Task Transition_OutgoingModuleRefuses_RecordsTheRefusalAndFailsTheAssignment()
    {
        var rig = CreateRig();
        var outgoing = new FakeModule("FakeOutgoing", 100, "fake.activity") { Retire = false };
        var arbiter = new BotGoalArbiter([rig.Module, outgoing]);

        var first = arbiter.Arbitrate(rig.Runtime, 12f);
        await Assert.That(first.Activity!.Name).IsEqualTo("fake.activity");

        rig.Assign();
        var second = arbiter.Arbitrate(rig.Runtime, 12f);

        await Assert.That(second.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(outgoing.Retirements).IsEqualTo(1);
        await Assert.That(rig.Registry.TryGet(rig.Runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Failed);
        await Assert.That(recorded.LastReason).Contains("refused to retire");
        await Assert.That(recorded.LastReason).Contains("FakeOutgoing/fake.activity");
        await Assert.That(rig.Registry.TryGetLive(rig.Runtime.CharacterId, out _)).IsFalse();
    }

    [Test]
    public async Task Transition_OutgoingModuleRetires_RecordsTheRetiredOwner()
    {
        var rig = CreateRig();
        var outgoing = new FakeModule("FakeOutgoing", 100, "fake.activity");
        var arbiter = new BotGoalArbiter([rig.Module, outgoing]);
        arbiter.Arbitrate(rig.Runtime, 12f);

        rig.Assign();
        arbiter.Arbitrate(rig.Runtime, 12f);

        await Assert.That(outgoing.Retirements).IsEqualTo(1);
        await Assert.That(rig.Registry.TryGet(rig.Runtime.CharacterId, out var recorded)).IsTrue();
        await Assert.That(recorded!.State).IsEqualTo(BotAssignmentState.Active);
        await Assert.That(recorded.LastReason).Contains("previous activity FakeOutgoing/fake.activity retired");
    }

    /// <summary>
    /// A steady-state wake must not ask anyone to retire: the assignment band
    /// holds its own activity (the arbiter's incumbent rule), so nothing is
    /// superseded and the recorded state keeps its original reason.
    /// </summary>
    [Test]
    public async Task UnchangedActivity_DoesNotRetireAnything()
    {
        var rig = CreateRig();
        var outgoing = new FakeModule("FakeOutgoing", 100, "fake.activity");
        var arbiter = new BotGoalArbiter([rig.Module, outgoing]);
        rig.Assign();
        arbiter.Arbitrate(rig.Runtime, 12f);
        await Assert.That(rig.Registry.TryGet(rig.Runtime.CharacterId, out var elected)).IsTrue();

        var steady = arbiter.Arbitrate(rig.Runtime, 12f);

        await Assert.That(steady.Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(outgoing.Retirements).IsEqualTo(0);
        await Assert.That(rig.Registry.TryGet(rig.Runtime.CharacterId, out var after)).IsTrue();
        await Assert.That(after!.LastReason).IsEqualTo(elected!.LastReason);
    }

    private static ModuleRig CreateRig()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("assigned-travel-module");
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var registry = new BotAssignmentRegistry();
        var executor = new BotRoamStepExecutor { ActorFactory = _ => actor };
        return new ModuleRig(new AssignedTravelActivityModule(registry, executor), registry, runtime);
    }

    /// <summary>
    /// One module under test plus the runtime it acts for — the registry is
    /// keyed by the RUNTIME's character id, so every test must assign through
    /// the same key the module reads.
    /// </summary>
    private sealed record ModuleRig(AssignedTravelActivityModule Module, BotAssignmentRegistry Registry, PlayerBotRuntime Runtime)
    {
        public BotActivityContext Context() => new() { Bot = Runtime, GameHour = 12f };

        public void Assign() => Registry.Accept(
            Runtime.CharacterId, BotAssignmentActivities.Travel, 3f, 0f, 4f, TimeSpan.FromSeconds(60), "rig");
    }

    private sealed class FakeModule(string name, int priority, string activity) : IBotActivityModule
    {
        public int Probes { get; private set; }

        public int Retirements { get; private set; }

        public bool Retire { get; init; } = true;

        public string Name => name;

        public int Priority => priority;

        public BotActivityDecision CanActivate(BotActivityContext context)
        {
            Probes++;
            return BotActivityDecision.Allow(activity);
        }

        public BotActivity Activate(BotActivityContext context) => new(activity, name);

        public bool Deactivate(BotActivityContext context)
        {
            Retirements++;
            return Retire;
        }
    }
}
