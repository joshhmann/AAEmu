using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using AAEmu.UnitTests.Game.Quests.Playerbot;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T4 return path (plan §7, audit L3-b): a leg's terminal outcome reaches the
/// electing module. The executor reports the fact, the arbiter stamps it with
/// the activity's owner and hands it to that module's next
/// <see cref="IBotActivityModule.CanActivate"/> — exactly once, and never to a
/// module that did not own the leg. These are the arbiter's half of the
/// contract; the executor's publish-once half and the real composition live in
/// <see cref="BotLegOutcomePublishTests"/>.
/// </summary>
[NotInParallel]
public class BotLegOutcomeReturnTests
{
    #region Rig

    /// <summary>
    /// A module that reports what the election told it: every probe records the
    /// leg outcome it was handed (null when there was none). This is the shape a
    /// real module needs to answer differently after its own leg failed.
    /// </summary>
    private sealed class LegAwareModule : IBotActivityModule
    {
        public string Name { get; }
        public int Priority { get; }
        public BotActivityDecision Decision { get; set; }
        public List<BotLegOutcome?> Seen { get; } = [];

        public LegAwareModule(string name, int priority, BotActivityDecision decision)
        {
            Name = name;
            Priority = priority;
            Decision = decision;
        }

        public BotActivityDecision CanActivate(BotActivityContext context)
        {
            Seen.Add(context.LastLegOutcome);
            return Decision;
        }

        public BotActivity Activate(BotActivityContext context) => new(Decision.ActivityName!, Name);
    }

    private static PlayerBotRuntime NewBot(string name)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, 1, Race.Nuian);
        return new PlayerBotRuntime(session.Character, "leg-outcome-tests");
    }

    private static BotLegOutcome Outcome(uint characterId, ActorLifecycleState state, string detail = "move timed out",
        string? moveOwner = "ROAM", string? activityName = null) =>
        new(characterId, ActorActionType.Move, state, detail, moveOwner, Guid.NewGuid(), activityName);

    #endregion

    #region Classification

    /// <summary>
    /// The three endings the return path carries: a leg that did the job, a leg
    /// that did not (rejected or timed out), and a leg something else stopped.
    /// </summary>
    [Test]
    public async Task BotLegOutcome_ClassifiesTheTerminalState()
    {
        var completed = Outcome(1, ActorLifecycleState.Completed);
        var timedOut = Outcome(1, ActorLifecycleState.TimedOut);
        var rejected = Outcome(1, ActorLifecycleState.Rejected);
        var interrupted = Outcome(1, ActorLifecycleState.Interrupted);

        await Assert.That(completed.Kind).IsEqualTo(BotLegOutcomeKind.Completed);
        await Assert.That(completed.IsFailure).IsFalse();

        await Assert.That(timedOut.Kind).IsEqualTo(BotLegOutcomeKind.Failed);
        await Assert.That(timedOut.IsFailure).IsTrue();
        await Assert.That(rejected.Kind).IsEqualTo(BotLegOutcomeKind.Failed);
        await Assert.That(rejected.IsFailure).IsTrue();

        await Assert.That(interrupted.Kind).IsEqualTo(BotLegOutcomeKind.Interrupted);
        await Assert.That(interrupted.IsFailure).IsFalse();
    }

    #endregion

    #region Delivery

    /// <summary>
    /// The owning module is handed its own leg outcome on the first election
    /// after the report — and only on that one. The wake after it is back to a
    /// plain context, so a module that wants to remember keeps its own memory.
    /// </summary>
    [Test]
    public async Task ReportLegOutcome_ReachesTheOwningModuleExactlyOnce()
    {
        var module = new LegAwareModule("Owner", 50, BotActivityDecision.Allow("owner.work"));
        var arbiter = new BotGoalArbiter([module]);
        var bot = NewBot("leg-outcome-once");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        var outcome = Outcome(bot.CharacterId, ActorLifecycleState.TimedOut, "Navigation: move timed out");
        arbiter.ReportLegOutcome(outcome);

        // The election after the failure is the one that hears about it...
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        var delivered = module.Seen[^1];
        await Assert.That(delivered).IsNotNull();
        await Assert.That(delivered!.TraceId).IsEqualTo(outcome.TraceId);
        await Assert.That(delivered.CharacterId).IsEqualTo(bot.CharacterId);
        await Assert.That(delivered.Action).IsEqualTo(ActorActionType.Move);
        await Assert.That(delivered.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(delivered.Detail).IsEqualTo("Navigation: move timed out");
        await Assert.That(delivered.MoveOwner).IsEqualTo("ROAM");
        await Assert.That(delivered.IsFailure).IsTrue();

        // ...and the one after that is not: one leg, one delivery.
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(module.Seen[^1]).IsNull();
    }

    /// <summary>
    /// A band-mate probed BEFORE the owner does not see another module's leg:
    /// the outcome goes to the owner, not to whoever happens to be asked first.
    /// The band-mate sits higher and denies, so it is probed first and the owner
    /// is probed after it — a "first probe wins" implementation fails here.
    /// </summary>
    [Test]
    public async Task ReportLegOutcome_IsNotHandedToAnotherModule()
    {
        var bandMate = new LegAwareModule("BandMate", 60, BotActivityDecision.Deny("not mine"));
        var owner = new LegAwareModule("Owner", 50, BotActivityDecision.Allow("owner.work"));
        var arbiter = new BotGoalArbiter([bandMate, owner]);
        var bot = NewBot("leg-outcome-bandmate");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("owner.work");

        arbiter.ReportLegOutcome(Outcome(bot.CharacterId, ActorLifecycleState.Rejected, "Navigation: no path"));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(bandMate.Seen[^1]).IsNull();
        await Assert.That(owner.Seen[^1]).IsNotNull();
    }

    /// <summary>
    /// The leg says which activity issued it. A leg issued under an activity the
    /// bot has already left is refused on arrival, so the module that took the
    /// bot over is never handed somebody else's leg — the case the executor's
    /// one-wake-late stamp exists for (a handover that ends the outgoing leg).
    /// </summary>
    [Test]
    public async Task ReportLegOutcome_ForAnActivityTheBotHasLeft_IsDropped()
    {
        var owner = new LegAwareModule("Owner", 50, BotActivityDecision.Allow("owner.work"));
        var rival = new LegAwareModule("Rival", 60, BotActivityDecision.Deny("not yet"));
        var arbiter = new BotGoalArbiter([rival, owner]);
        var bot = NewBot("leg-outcome-stale-stamp");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("owner.work");

        // The owner's leg ends while the rival takes the bot over.
        rival.Decision = BotActivityDecision.Allow("rival.work");
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("rival.work");

        arbiter.ReportLegOutcome(Outcome(bot.CharacterId, ActorLifecycleState.Interrupted,
            "the assignment band was superseded", activityName: "owner.work"));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(rival.Seen[^1]).IsNull();

        // The same fact reported under the activity that is still active is
        // delivered — the stamp is what decides, not the age of the report.
        arbiter.ReportLegOutcome(Outcome(bot.CharacterId, ActorLifecycleState.TimedOut, activityName: "rival.work"));
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(rival.Seen[^1]).IsNotNull();
    }

    /// <summary>
    /// A report for a bot nobody owns has no module to reach, so it is dropped
    /// rather than kept for a later activity that never ran the leg.
    /// </summary>
    [Test]
    public async Task ReportLegOutcome_WithoutAnActiveActivity_IsDropped()
    {
        var module = new LegAwareModule("Owner", 50, BotActivityDecision.Deny("nothing to do"));
        var arbiter = new BotGoalArbiter([module]);
        var bot = NewBot("leg-outcome-orphan");

        arbiter.ReportLegOutcome(Outcome(bot.CharacterId, ActorLifecycleState.TimedOut));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(module.Seen[^1]).IsNull();
    }

    /// <summary>
    /// When the bot leaves the activity before the election that would have
    /// carried the outcome, the fact is dropped: it belonged to work that is
    /// over, and the module that took the bot over must not be told about
    /// somebody else's leg. (A higher band deciding a wake never asks the owner,
    /// so this is the real shape of that case.)
    /// </summary>
    [Test]
    public async Task ReportLegOutcome_ActivityChangeBeforeDelivery_DropsIt()
    {
        var rival = new LegAwareModule("Rival", 60, BotActivityDecision.Deny("not yet"));
        var owner = new LegAwareModule("Owner", 50, BotActivityDecision.Allow("owner.work"));
        var arbiter = new BotGoalArbiter([rival, owner]);
        var bot = NewBot("leg-outcome-superseded");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("owner.work");

        arbiter.ReportLegOutcome(Outcome(bot.CharacterId, ActorLifecycleState.TimedOut));

        // The rival band now wins: the owner is never asked this wake, so the
        // pending outcome dies with the activity it belonged to.
        rival.Decision = BotActivityDecision.Allow("rival.work");
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("rival.work");
        await Assert.That(rival.Seen[^1]).IsNull();

        // The owner is asked again on a later wake (the rival withdraws) and is
        // back to a plain context.
        rival.Decision = BotActivityDecision.Deny("done");
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("owner.work");
        await Assert.That(owner.Seen[^1]).IsNull();
    }

    /// <summary>
    /// Deactivation cleanup forgets the bot's pending outcome with its activity
    /// memory: nothing may survive to be handed to a later activation.
    /// </summary>
    [Test]
    public async Task Forget_DropsThePendingLegOutcome()
    {
        var module = new LegAwareModule("Owner", 50, BotActivityDecision.Allow("owner.work"));
        var arbiter = new BotGoalArbiter([module]);
        var bot = NewBot("leg-outcome-forget");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        arbiter.ReportLegOutcome(Outcome(bot.CharacterId, ActorLifecycleState.TimedOut));

        arbiter.Forget(bot.CharacterId);

        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsNull();
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(module.Seen[^1]).IsNull();
    }

    #endregion
}
