using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using AAEmu.UnitTests.Game.Quests.Playerbot;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T7 operator decision read-back (plan §7, audit L2.7): the arbiter's own
/// refusal text, the elected activity and the candidate/refusal counts survive
/// the wake they were decided in, and the snapshot names which side of the
/// control plane the work in force came from.
///
/// Two producers are locked here. The decorator keeps the LAST arbitration
/// result per bot (<see cref="BotGoalArbiterStepExecutor.GetLastDecision"/>),
/// overwritten every wake so it never accumulates and never describes an older
/// wake; the projection maps it into the snapshot beside the origin rule. The
/// route that serves it to an operator is driven end-to-end in
/// <c>BotDecisionReadbackRouteTests</c>; these tests pin the mapping and the
/// absent cases (no decision yet, nothing in force, a refusal-free wake) that
/// the route test cannot produce cheaply.
/// </summary>
[NotInParallel]
public class BotWakeDecisionTests
{
    #region Rig

    /// <summary>A module that answers whatever the test wants, and counts its elections.</summary>
    private sealed class StubModule : IBotActivityModule
    {
        public string Name { get; }
        public int Priority { get; }
        public BotActivityDecision Decision { get; set; }
        public int Activations { get; private set; }

        public StubModule(string name, int priority, BotActivityDecision decision)
        {
            Name = name;
            Priority = priority;
            Decision = decision;
        }

        public BotActivityDecision CanActivate(BotActivityContext context) => Decision;

        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity(Decision.ActivityName!, Name);
        }
    }

    /// <summary>
    /// The decorator only forwards to its inner executor; the read-back under
    /// test never depends on what the inner step did, so this records the
    /// forward and sleeps the bot.
    /// </summary>
    private sealed class StubInner : IBotStepExecutor
    {
        public int Steps { get; private set; }

        public Task<TimeSpan?> StepAsync(PlayerBotRuntime bot, CancellationToken cancellationToken)
        {
            Steps++;
            return Task.FromResult<TimeSpan?>(null);
        }
    }

    private static PlayerBotRuntime NewBot(string name)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, 1, Race.Nuian);
        return new PlayerBotRuntime(session.Character, "wake-decision-tests");
    }

    /// <summary>A real registry assignment (T1) in whatever state the test needs.</summary>
    private static BotAssignment AssignmentInState(uint characterId, bool live)
    {
        var registry = new BotAssignmentRegistry();
        var accepted = registry.Accept(characterId, BotAssignmentActivities.Travel,
            1f, 2f, 3f, TimeSpan.FromMinutes(5), "wake-decision-tests");
        if (accepted == null)
            throw new InvalidOperationException("the assignment plane refused travel");
        if (!live)
            registry.Complete(characterId, "reached the destination");
        registry.TryGet(characterId, out var recorded);
        return recorded ?? throw new InvalidOperationException("no assignment recorded");
    }

    #endregion

    #region Origin rule

    /// <summary>Nothing in force has no origin to name — null, never a guess.</summary>
    [Test]
    public async Task WorkOrigin_NothingInForce_IsNull()
    {
        var live = AssignmentInState(910001u, live: true);

        await Assert.That(BotWorkOrigin.Of(null, live)).IsNull();
        await Assert.That(BotWorkOrigin.Of(string.Empty, live)).IsNull();
        await Assert.That(BotWorkOrigin.Of(null, null)).IsNull();
    }

    /// <summary>The live assignment IS the work running: commanded.</summary>
    [Test]
    public async Task WorkOrigin_LiveAssignmentMatchingTheActivityInForce_IsCommanded()
    {
        var live = AssignmentInState(910002u, live: true);

        await Assert.That(live.IsLive).IsTrue();
        await Assert.That(BotWorkOrigin.Of(BotAssignmentActivities.Travel, live))
            .IsEqualTo(BotWorkOrigin.Commanded);
    }

    /// <summary>A live assignment for other work does not make the running work commanded.</summary>
    [Test]
    public async Task WorkOrigin_LiveAssignmentForAnotherActivity_IsAutonomous()
    {
        var live = AssignmentInState(910003u, live: true);

        await Assert.That(BotWorkOrigin.Of("farm.work", live)).IsEqualTo(BotWorkOrigin.Autonomous);
    }

    /// <summary>An assignment that already ended commands nothing.</summary>
    [Test]
    public async Task WorkOrigin_EndedAssignment_IsAutonomous()
    {
        var ended = AssignmentInState(910004u, live: false);

        await Assert.That(ended.IsLive).IsFalse();
        await Assert.That(ended.StateName).IsEqualTo(nameof(BotAssignmentState.Completed));
        await Assert.That(BotWorkOrigin.Of(BotAssignmentActivities.Travel, ended))
            .IsEqualTo(BotWorkOrigin.Autonomous);
    }

    #endregion

    #region Decorator read-back

    /// <summary>A bot the decorator never stepped has no decision to report.</summary>
    [Test]
    public async Task GetLastDecision_BeforeAnyWake_IsNull()
    {
        var arbiter = new BotGoalArbiter([new StubModule("Farm", 10, BotActivityDecision.Allow("farm.work"))]);
        var decorator = new BotGoalArbiterStepExecutor(arbiter, new StubInner());

        await Assert.That(decorator.GetLastDecision(910005u)).IsNull();
    }

    /// <summary>
    /// The case the audit names: nobody could activate, and the arbiter's own
    /// refusal text is what the operator reads — not a synthesized reason.
    /// </summary>
    [Test]
    public async Task GetLastDecision_AfterARefusal_ReportsTheArbiterReasonAndCounts()
    {
        var farm = new StubModule("Farm", 10, BotActivityDecision.Deny("no ripe crop in reach"));
        var arbiter = new BotGoalArbiter([farm]);
        var inner = new StubInner();
        var decorator = new BotGoalArbiterStepExecutor(arbiter, inner);
        var bot = NewBot("wake-decision-refusal");

        await decorator.StepAsync(bot, CancellationToken.None);

        var decision = decorator.GetLastDecision(bot.CharacterId);
        await Assert.That(decision).IsNotNull();
        await Assert.That(decision!.Outcome).IsEqualTo(nameof(BotArbitrationOutcome.NoCandidate));
        await Assert.That(decision.Activity).IsNull();
        await Assert.That(decision.Reason).IsEqualTo("no ripe crop in reach");
        await Assert.That(decision.Candidates).IsEqualTo(0);
        await Assert.That(decision.Rejections).IsEqualTo(1);
        await Assert.That(inner.Steps, "the inner step still runs after a no-candidate wake").IsEqualTo(1);
    }

    /// <summary>
    /// A refusal-free wake reports the elected activity and no reason: a wake
    /// where every module allowed has no refusal to show.
    /// </summary>
    [Test]
    public async Task GetLastDecision_AfterAnElection_ReportsTheActivityAndNoReason()
    {
        var farm = new StubModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var arbiter = new BotGoalArbiter([farm]);
        var decorator = new BotGoalArbiterStepExecutor(arbiter, new StubInner());
        var bot = NewBot("wake-decision-election");

        await decorator.StepAsync(bot, CancellationToken.None);

        var decision = decorator.GetLastDecision(bot.CharacterId);
        await Assert.That(decision).IsNotNull();
        await Assert.That(decision!.Outcome).IsEqualTo(nameof(BotArbitrationOutcome.Activated));
        await Assert.That(decision.Activity).IsEqualTo("farm.work");
        await Assert.That(decision.Reason).IsNull();
        await Assert.That(decision.Candidates).IsEqualTo(1);
        await Assert.That(decision.Rejections).IsEqualTo(0);
        await Assert.That(farm.Activations).IsEqualTo(1);
    }

    /// <summary>
    /// The read-back describes the LAST wake, not the first one: a steady-state
    /// wake replaces it, and the entry is per bot.
    /// </summary>
    [Test]
    public async Task GetLastDecision_SecondWake_ReplacesTheFirst_AndIsPerBot()
    {
        var farm = new StubModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var arbiter = new BotGoalArbiter([farm]);
        var decorator = new BotGoalArbiterStepExecutor(arbiter, new StubInner());
        var first = NewBot("wake-decision-first");
        var second = NewBot("wake-decision-second");

        await decorator.StepAsync(first, CancellationToken.None);
        await decorator.StepAsync(second, CancellationToken.None);
        await Assert.That(decorator.GetLastDecision(second.CharacterId)!.Activity).IsEqualTo("farm.work");

        farm.Decision = BotActivityDecision.Deny("the field is empty");
        await decorator.StepAsync(first, CancellationToken.None);

        var replaced = decorator.GetLastDecision(first.CharacterId)!;
        await Assert.That(replaced.Outcome).IsEqualTo(nameof(BotArbitrationOutcome.NoCandidate));
        await Assert.That(replaced.Reason).IsEqualTo("the field is empty");
        await Assert.That(decorator.GetLastDecision(second.CharacterId)!.Outcome,
            "one bot's wake never rewrites another bot's read-back").IsEqualTo(nameof(BotArbitrationOutcome.Activated));
    }

    #endregion

    #region Snapshot mapping

    /// <summary>
    /// The snapshot carries the decision verbatim with the counts, and the
    /// origin follows the decision's elected activity rather than the
    /// `Activity` field's runtime-first fallback, so the two can disagree (see
    /// the stale-runtime test below).
    /// </summary>
    [Test]
    public async Task Capture_DecisionAndOrigin_AreReported()
    {
        var live = AssignmentInState(910006u, live: true);

        var snapshot = BotBrainProjection.Capture(
            910006u, "ReadbackBot", 1f, 2f, 3f, 42u, 1u, 0u, 0u, null,
            wakeSequence: 7,
            arbiterActivity: BotAssignmentActivities.Travel,
            runtime: null,
            liveRequest: null,
            trace: null,
            questLegActive: false,
            questTravelX: null, questTravelY: null, questTravelZ: null,
            questTravelReason: null,
            assignment: live,
            decision: new BotWakeDecision(nameof(BotArbitrationOutcome.Activated),
                BotAssignmentActivities.Travel, null, 1, 0));

        await Assert.That(snapshot.DecisionOutcome).IsEqualTo(nameof(BotArbitrationOutcome.Activated));
        await Assert.That(snapshot.DecisionActivity).IsEqualTo(BotAssignmentActivities.Travel);
        await Assert.That(snapshot.DecisionReason, "nothing refused this wake").IsNull();
        await Assert.That(snapshot.DecisionCandidates).IsEqualTo(1);
        await Assert.That(snapshot.DecisionRejections).IsEqualTo(0);
        await Assert.That(snapshot.WorkOrigin).IsEqualTo(BotWorkOrigin.Commanded);
    }

    /// <summary>
    /// The origin is built from the ARBITRATION's activity, not from the
    /// fallback the `Activity` field uses: with both the runtime and the
    /// arbiter's fallback naming nothing, a decision that elected the assigned
    /// activity still reads as commanded.
    /// </summary>
    [Test]
    public async Task Capture_OriginFollowsTheArbitrationsActivity_NotTheRuntimeFallback()
    {
        var live = AssignmentInState(910009u, live: true);

        var snapshot = BotBrainProjection.Capture(
            910009u, "ReadbackBot", 1f, 2f, 3f, 42u, 1u, 0u, 0u, null,
            wakeSequence: 7,
            arbiterActivity: null,
            runtime: null,
            liveRequest: null,
            trace: null,
            questLegActive: false,
            questTravelX: null, questTravelY: null, questTravelZ: null,
            questTravelReason: null,
            assignment: live,
            decision: new BotWakeDecision(nameof(BotArbitrationOutcome.Activated),
                BotAssignmentActivities.Travel, null, 1, 0));

        await Assert.That(snapshot.WorkOrigin, "the elected activity is the one in force")
            .IsEqualTo(BotWorkOrigin.Commanded);
    }

    /// <summary>
    /// The shape the review's P1 finding named: a runtime that ticked a quest
    /// leg earlier — `CurrentActivity` is written only while a quest leg ticks
    /// and is never cleared — while an operator assignment is what the arbiter
    /// put in force. The stale runtime value must not decide the side: the
    /// origin follows the arbitration's activity, while the `Activity` field
    /// keeps its runtime-first provenance and is reported as it is.
    /// </summary>
    [Test]
    public async Task Capture_StaleRuntimeActivity_DoesNotDecideTheOrigin()
    {
        var live = AssignmentInState(910010u, live: true);
        var runtime = new BotBehaviorRuntime(() => DateTimeOffset.UtcNow);
        // Production writes this from the quest tick that carries the electing
        // activity; the rig sets the same property instead of driving a whole
        // gameplay actor for one field.
        typeof(BotBehaviorRuntime).GetProperty(nameof(BotBehaviorRuntime.CurrentActivity))!
            .SetValue(runtime, "quest.progress");

        var snapshot = BotBrainProjection.Capture(
            910010u, "ReadbackBot", 1f, 2f, 3f, 42u, 1u, 0u, 0u, null,
            wakeSequence: 7,
            arbiterActivity: null,
            runtime: runtime,
            liveRequest: null,
            trace: null,
            questLegActive: false,
            questTravelX: null, questTravelY: null, questTravelZ: null,
            questTravelReason: null,
            assignment: live,
            decision: new BotWakeDecision(nameof(BotArbitrationOutcome.Activated),
                BotAssignmentActivities.Travel, null, 1, 0));

        await Assert.That(snapshot.Activity, "the runtime field keeps its own provenance")
            .IsEqualTo("quest.progress");
        await Assert.That(snapshot.DecisionActivity).IsEqualTo(BotAssignmentActivities.Travel);
        await Assert.That(snapshot.WorkOrigin, "the elected activity is the one in force")
            .IsEqualTo(BotWorkOrigin.Commanded);
    }

    /// <summary>
    /// A bot the decorator never stepped reports no decision (nulls, zero
    /// counts) rather than an invented one, while the origin still describes
    /// the work in force.
    /// </summary>
    [Test]
    public async Task Capture_WithoutADecision_ReportsNullsAndZero()
    {
        var snapshot = BotBrainProjection.Capture(
            910007u, "ReadbackBot", 1f, 2f, 3f, 42u, 1u, 0u, 0u, null,
            wakeSequence: 0,
            arbiterActivity: "farm.work",
            runtime: null,
            liveRequest: null,
            trace: null,
            questLegActive: false,
            questTravelX: null, questTravelY: null, questTravelZ: null,
            questTravelReason: null);

        await Assert.That(snapshot.DecisionOutcome).IsNull();
        await Assert.That(snapshot.DecisionActivity).IsNull();
        await Assert.That(snapshot.DecisionReason).IsNull();
        await Assert.That(snapshot.DecisionCandidates).IsEqualTo(0);
        await Assert.That(snapshot.DecisionRejections).IsEqualTo(0);
        await Assert.That(snapshot.WorkOrigin).IsEqualTo(BotWorkOrigin.Autonomous);
    }

    /// <summary>Nothing in force (no activity, no decision) reports no origin.</summary>
    [Test]
    public async Task Capture_NothingInForce_ReportsNoOrigin()
    {
        var snapshot = BotBrainProjection.Capture(
            910008u, "ReadbackBot", 1f, 2f, 3f, 42u, 1u, 0u, 0u, null,
            wakeSequence: 0,
            arbiterActivity: null,
            runtime: null,
            liveRequest: null,
            trace: null,
            questLegActive: false,
            questTravelX: null, questTravelY: null, questTravelZ: null,
            questTravelReason: null);

        await Assert.That(snapshot.WorkOrigin).IsNull();
    }

    #endregion
}
