using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using AAEmu.UnitTests.Game.Quests.Playerbot;

using TUnit.Core;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T6 interrupted-goal continuity (plan §7, audit L1.3 / L4-b / L4-c). Two
/// halves are locked here. First, the write: every election that CHANGES the
/// outcome records the goal it put in force and the goal it ended (a supersede,
/// a same-owner rename, or a wake where nobody could activate), while a
/// steady-state wake records nothing. Second, the read: an arbiter that never
/// saw the bot before reads that record once and hands it to the module that
/// LOST the goal — the module that needs the fact, never the one that won — at
/// most once per process read of a record (a restart or a re-enrollment tells it
/// once more, and a record replaced by the next outcome change before the loser
/// was next probed reaches nobody), and a damaged store or row cannot stop a bot
/// from acting.
///
/// The durable round-trip through the metadata row (the plan §8 integration
/// criterion) is NOT pinned here and cannot be: it needs a live MySQL. These
/// rigs drive the arbiter's half with an injected provider/sink, the same seam
/// style as the T4 return path; the store tests pin the column mapping and the
/// no-database degradation, and the real write/reload run is recorded in the
/// evidence ledger (E319), not in this suite.
/// </summary>
[NotInParallel]
public class BotGoalContinuityTests
{
    #region Rig

    /// <summary>
    /// A module that reports what the election told it about a goal it lost:
    /// every probe records the continuity record it was handed (null when there
    /// was none). This is the shape a real module needs to answer differently
    /// for work a higher band interrupted.
    /// </summary>
    private sealed class ContinuityAwareModule : IBotActivityModule
    {
        public string Name { get; }
        public int Priority { get; }
        public BotActivityDecision Decision { get; set; }
        public List<BotGoalContinuity?> Seen { get; } = [];
        public int Activations { get; private set; }

        public ContinuityAwareModule(string name, int priority, BotActivityDecision decision)
        {
            Name = name;
            Priority = priority;
            Decision = decision;
        }

        public BotActivityDecision CanActivate(BotActivityContext context)
        {
            Seen.Add(context.Continuity);
            return Decision;
        }

        public BotActivity Activate(BotActivityContext context)
        {
            Activations++;
            return new BotActivity(Decision.ActivityName!, Name);
        }
    }

    private static PlayerBotRuntime NewBot(string name)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, 1, Race.Nuian);
        return new PlayerBotRuntime(session.Character, "continuity-tests");
    }

    /// <summary>Collects what the arbiter recorded, with the bot each record belongs to.</summary>
    private sealed class RecordingSink
    {
        public List<(uint CharacterId, BotGoalContinuity Record)> Records { get; } = [];

        public BotGoalContinuity Last => Records[^1].Record;
    }

    /// <summary>
    /// A module that wins the election and then fails to start, the way a real
    /// module can: the arbiter rolls the election back, so nothing may have been
    /// recorded as the goal in force.
    /// </summary>
    private sealed class ThrowingModule : IBotActivityModule
    {
        public string Name { get; }
        public int Priority { get; }
        public BotActivityDecision Decision { get; set; } = BotActivityDecision.Deny("not yet");

        public ThrowingModule(string name, int priority)
        {
            Name = name;
            Priority = priority;
        }

        public BotActivityDecision CanActivate(BotActivityContext context) => Decision;

        public BotActivity Activate(BotActivityContext context) =>
            throw new InvalidOperationException("activate failed");
    }

    #endregion

    #region Write path

    /// <summary>
    /// The supersede case the audit names (L4-c): a higher band allows, the
    /// lower band's in-progress goal is replaced, and the record says both what
    /// took over and what it took over from. The first election records too —
    /// with nothing interrupted, because nothing was.
    /// </summary>
    [Test]
    public async Task Arbitrate_Supersede_RecordsTheElectedAndTheInterruptedGoal()
    {
        var farm = new ContinuityAwareModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var recover = new ContinuityAwareModule("Recover", 100, BotActivityDecision.Deny("nothing to recover"));
        var arbiter = new BotGoalArbiter([farm, recover]);
        var bot = NewBot("continuity-supersede");
        var sink = new RecordingSink();
        arbiter.ContinuitySink = (characterId, record) => sink.Records.Add((characterId, record));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("farm.work");

        var first = sink.Last;
        await Assert.That(sink.Records.Count).IsEqualTo(1);
        await Assert.That(sink.Records[0].CharacterId).IsEqualTo(bot.CharacterId);
        await Assert.That(first.ActivityName).IsEqualTo("farm.work");
        await Assert.That(first.ModuleName).IsEqualTo("Farm");
        await Assert.That(first.InterruptedActivity, "nothing was in force before the first election").IsNull();
        await Assert.That(first.InterruptedModule).IsNull();
        await Assert.That(first.InterruptionReason).IsEqualTo("first election for this bot");

        recover.Decision = BotActivityDecision.Allow("recover.work");
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        var superseded = sink.Last;
        await Assert.That(sink.Records.Count).IsEqualTo(2);
        await Assert.That(sink.Records[1].CharacterId).IsEqualTo(bot.CharacterId);
        await Assert.That(superseded.ActivityName).IsEqualTo("recover.work");
        await Assert.That(superseded.ModuleName).IsEqualTo("Recover");
        await Assert.That(superseded.InterruptedActivity).IsEqualTo("farm.work");
        await Assert.That(superseded.InterruptedModule).IsEqualTo("Farm");
        await Assert.That(superseded.InterruptionReason).IsEqualTo("superseded by recover.work");
    }

    /// <summary>
    /// A wake where nobody can activate ends the goal the bot was running. That
    /// drop used to vanish with the process's memory; the record keeps it — with
    /// nothing in force, and the module that owned it named as the loser.
    /// </summary>
    [Test]
    public async Task Arbitrate_NoCandidate_RecordsTheDroppedGoal()
    {
        var farm = new ContinuityAwareModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var arbiter = new BotGoalArbiter([farm]);
        var bot = NewBot("continuity-drop");
        var sink = new RecordingSink();
        arbiter.ContinuitySink = (characterId, record) => sink.Records.Add((characterId, record));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        farm.Decision = BotActivityDecision.Deny("crops are in");
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.NoCandidate);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsNull();

        var dropped = sink.Last;
        await Assert.That(sink.Records.Count).IsEqualTo(2);
        await Assert.That(dropped.ActivityName).IsNull();
        await Assert.That(dropped.ModuleName).IsNull();
        await Assert.That(dropped.InterruptedActivity).IsEqualTo("farm.work");
        await Assert.That(dropped.InterruptedModule).IsEqualTo("Farm");
        await Assert.That(dropped.InterruptionReason).IsEqualTo("no module could activate");
    }

    /// <summary>
    /// One module changing its own activity name (schedule.work ->
    /// schedule.rest) is not a supersede: it kept its work. The record says so,
    /// and it names the same module on both sides, which is what keeps the
    /// handoff from being delivered as a loss.
    /// </summary>
    [Test]
    public async Task Arbitrate_SameOwnerRename_RecordsTheRename()
    {
        var schedule = new ContinuityAwareModule("Schedule", 50, BotActivityDecision.Allow("schedule.work"));
        var arbiter = new BotGoalArbiter([schedule]);
        var bot = NewBot("continuity-rename");
        var sink = new RecordingSink();
        arbiter.ContinuitySink = (characterId, record) => sink.Records.Add((characterId, record));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(schedule.Activations).IsEqualTo(1);

        schedule.Decision = BotActivityDecision.Allow("schedule.rest");
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(schedule.Activations, "the same owner must not be retired and re-activated").IsEqualTo(2);

        var renamed = sink.Last;
        await Assert.That(renamed.ActivityName).IsEqualTo("schedule.rest");
        await Assert.That(renamed.ModuleName).IsEqualTo("Schedule");
        await Assert.That(renamed.InterruptedActivity).IsEqualTo("schedule.work");
        await Assert.That(renamed.InterruptedModule).IsEqualTo("Schedule");
        await Assert.That(renamed.InterruptionReason).IsEqualTo("same owner Schedule, activity renamed to schedule.rest");
    }

    /// <summary>
    /// The steady state writes nothing. A bot that keeps waking with the same
    /// goal must not re-record it every tick — the record is an outcome change,
    /// not a heartbeat.
    /// </summary>
    [Test]
    public async Task Arbitrate_UnchangedOutcome_RecordsNothing()
    {
        var farm = new ContinuityAwareModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var arbiter = new BotGoalArbiter([farm]);
        var bot = NewBot("continuity-steady");
        var sink = new RecordingSink();
        arbiter.ContinuitySink = (characterId, record) => sink.Records.Add((characterId, record));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(sink.Records.Count).IsEqualTo(1);

        for (var i = 0; i < 5; i++)
            await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);

        await Assert.That(sink.Records.Count).IsEqualTo(1);
    }

    /// <summary>
    /// An election that is rolled back is not recorded. When the winning module
    /// throws on its way up, the arbiter restores the previous activity and
    /// rethrows, so the durable record must keep naming the goal that is still
    /// in force — never a goal that never started, and never a loss fact for a
    /// module that was not actually superseded.
    /// </summary>
    [Test]
    public async Task Arbitrate_ActivateThrows_RecordsNothingAndKeepsTheOldGoal()
    {
        var schedule = new ContinuityAwareModule("Schedule", 10, BotActivityDecision.Allow("schedule.work"));
        var throwing = new ThrowingModule("Farm", 90);
        var arbiter = new BotGoalArbiter([schedule, throwing]);
        var bot = NewBot("continuity-activate-throws");
        var sink = new RecordingSink();
        arbiter.ContinuitySink = (characterId, record) => sink.Records.Add((characterId, record));

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("schedule.work");
        await Assert.That(sink.Records.Count).IsEqualTo(1);

        throwing.Decision = BotActivityDecision.Allow("farm.work");
        await Assert.That(() => arbiter.Arbitrate(bot, 12f)).Throws<InvalidOperationException>();

        await Assert.That(sink.Records.Count, "the rolled-back election must not be recorded").IsEqualTo(1);
        await Assert.That(sink.Last.ActivityName, "the record still names the goal in force").IsEqualTo("schedule.work");
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("schedule.work");
    }

    /// <summary>
    /// A store that refuses the write does not take the wake down: the goal is
    /// already decided, so the bot acts and the failure is only logged (the
    /// store keeps its row dirty and retries on the next save).
    /// </summary>
    [Test]
    public async Task Arbitrate_ThrowingSink_StillElects()
    {
        var farm = new ContinuityAwareModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var arbiter = new BotGoalArbiter([farm])
        {
            ContinuitySink = (_, _) => throw new InvalidOperationException("metadata store unavailable"),
        };
        var bot = NewBot("continuity-sink-throws");

        var result = arbiter.Arbitrate(bot, 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.Name).IsEqualTo("farm.work");
        await Assert.That(farm.Activations).IsEqualTo(1);
    }

    #endregion

    #region Read path

    /// <summary>
    /// The restart the audit says cannot happen today (L1.3): a brand-new
    /// arbiter — one that never saw this bot — reads the stored record and hands
    /// it to the module that lost the goal, on that module's own probe. The
    /// winner is not handed it, and the next wake is back to a plain context:
    /// one record, one delivery.
    /// </summary>
    [Test]
    public async Task FreshArbiter_ReadsTheRecord_AndHandsItToTheInterruptedOwnerOnce()
    {
        var stored = new BotGoalContinuity("recover.work", "Recover", "farm.work", "Farm",
            "superseded by recover.work", new DateTime(2026, 10, 17, 3, 4, 5, DateTimeKind.Utc));

        var farm = new ContinuityAwareModule("Farm", 60, BotActivityDecision.Deny("the field is not mine any more"));
        var recover = new ContinuityAwareModule("Recover", 50, BotActivityDecision.Allow("recover.work"));
        var reads = 0;
        var arbiter = new BotGoalArbiter([farm, recover])
        {
            ContinuityProvider = _ =>
            {
                reads++;
                return stored;
            },
        };
        var bot = NewBot("continuity-restart");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("recover.work");

        var delivered = farm.Seen[^1];
        await Assert.That(delivered, "the module that lost the goal is told").IsNotNull();
        await Assert.That(delivered!.ActivityName).IsEqualTo("recover.work");
        await Assert.That(delivered.InterruptedActivity).IsEqualTo("farm.work");
        await Assert.That(delivered.InterruptionReason).IsEqualTo("superseded by recover.work");
        await Assert.That(recover.Seen[^1], "the winner already knows it won").IsNull();

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(farm.Seen[^1], "one record, one delivery").IsNull();

        await Assert.That(reads, "the store is read once per bot, not once per wake").IsEqualTo(1);
    }

    /// <summary>
    /// A record whose loser is also the goal's owner is a rename, not a loss:
    /// the module kept the work, so it is not handed the fact as if it had lost
    /// something. Handing it back would make every schedule phase change look
    /// like an interruption.
    /// </summary>
    [Test]
    public async Task FreshArbiter_RecordNamingTheSameOwner_IsNotHandedBack()
    {
        var stored = new BotGoalContinuity("schedule.rest", "Schedule", "schedule.work", "Schedule",
            "same owner Schedule, activity renamed to schedule.rest", new DateTime(2026, 10, 17, 4, 0, 0, DateTimeKind.Utc));

        var schedule = new ContinuityAwareModule("Schedule", 50, BotActivityDecision.Allow("schedule.rest"));
        var arbiter = new BotGoalArbiter([schedule]) { ContinuityProvider = _ => stored };
        var bot = NewBot("continuity-rename-restart");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);

        await Assert.That(schedule.Seen[^1]).IsNull();
    }

    /// <summary>
    /// A bot that is forgotten and enrolled again is a fresh start: its first
    /// election reads the durable record once more and tells the module that lost
    /// the goal once more, because that module is looking at a bot that no longer
    /// knows anything. The re-read is still once per enrollment.
    /// </summary>
    [Test]
    public async Task Forget_ReReadsTheRecord_AndDeliversItAgainOnce()
    {
        var stored = new BotGoalContinuity("recover.work", "Recover", "farm.work", "Farm",
            "superseded by recover.work", new DateTime(2026, 10, 17, 3, 4, 5, DateTimeKind.Utc));

        var farm = new ContinuityAwareModule("Farm", 60, BotActivityDecision.Deny("not mine"));
        var recover = new ContinuityAwareModule("Recover", 50, BotActivityDecision.Allow("recover.work"));
        var reads = 0;
        var arbiter = new BotGoalArbiter([farm, recover])
        {
            ContinuityProvider = _ =>
            {
                reads++;
                return stored;
            },
        };
        var bot = NewBot("continuity-reenroll");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(farm.Seen[^1]).IsNotNull();
        await Assert.That(reads).IsEqualTo(1);

        arbiter.Forget(bot.CharacterId);

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(farm.Seen[^1], "a re-enrolled bot starts over and is told again").IsNotNull();
        await Assert.That(reads).IsEqualTo(2);

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);
        await Assert.That(farm.Seen[^1]).IsNull();
        await Assert.That(reads).IsEqualTo(2);
    }

    /// <summary>
    /// A damaged row must not stop a bot from acting: a provider that throws is
    /// treated as "nothing was ever recorded" — the wake proceeds and the bot is
    /// elected as it was before T6.
    /// </summary>
    [Test]
    public async Task Arbitrate_ThrowingProvider_StillElects()
    {
        var farm = new ContinuityAwareModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var arbiter = new BotGoalArbiter([farm])
        {
            ContinuityProvider = _ => throw new InvalidOperationException("planner_state is not valid JSON"),
        };
        var bot = NewBot("continuity-provider-throws");

        var result = arbiter.Arbitrate(bot, 12f);

        await Assert.That(result.Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(result.Activity!.Name).IsEqualTo("farm.work");
        await Assert.That(farm.Seen[^1]).IsNull();
    }

    /// <summary>
    /// With no seams wired the arbiter behaves exactly as it did before T6: it
    /// records nothing and reads nothing, so the pre-T6 behaviour is the default
    /// and persistence is an explicit wiring decision.
    /// </summary>
    [Test]
    public async Task Arbitrate_WithoutSeams_RecordsAndReadsNothing()
    {
        var farm = new ContinuityAwareModule("Farm", 10, BotActivityDecision.Allow("farm.work"));
        var arbiter = new BotGoalArbiter([farm]);
        var bot = NewBot("continuity-unwired");

        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Activated);
        await Assert.That(arbiter.Arbitrate(bot, 12f).Outcome).IsEqualTo(BotArbitrationOutcome.Unchanged);

        await Assert.That(farm.Seen[^1]).IsNull();
    }

    #endregion

    #region Record serialization

    /// <summary>
    /// The record is what the metadata row holds, so its JSON round-trip is the
    /// contract the restart depends on: every field survives, under camelCase
    /// names, and the timestamp comes back as the same instant.
    /// </summary>
    [Test]
    public async Task ToJson_TryParse_RoundTripsEveryField()
    {
        var record = new BotGoalContinuity("recover.work", "Recover", "farm.work", "Farm",
            "superseded by recover.work", new DateTime(2026, 10, 17, 3, 4, 5, DateTimeKind.Utc));

        var json = record.ToJson();
        var parsed = BotGoalContinuity.TryParse(json);

        await Assert.That(json).Contains("recover.work");
        await Assert.That(json, "the metadata row's JSON convention is camelCase")
            .Contains("\"interruptedModule\":\"Farm\"");
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.ActivityName).IsEqualTo("recover.work");
        await Assert.That(parsed.ModuleName).IsEqualTo("Recover");
        await Assert.That(parsed.InterruptedActivity).IsEqualTo("farm.work");
        await Assert.That(parsed.InterruptedModule).IsEqualTo("Farm");
        await Assert.That(parsed.InterruptionReason).IsEqualTo("superseded by recover.work");
        await Assert.That(parsed.RecordedAtUtc).IsEqualTo(record.RecordedAtUtc);
    }

    /// <summary>
    /// Reading is total: an empty column (a bot that never had an outcome
    /// recorded), junk, or a payload of the wrong shape all read as "nothing was
    /// recorded" rather than throwing at the scheduler.
    /// </summary>
    [Test]
    public async Task TryParse_UnreadablePayload_IsNull()
    {
        await Assert.That(BotGoalContinuity.TryParse(null)).IsNull();
        await Assert.That(BotGoalContinuity.TryParse(string.Empty)).IsNull();
        await Assert.That(BotGoalContinuity.TryParse("   ")).IsNull();
        await Assert.That(BotGoalContinuity.TryParse("not json at all")).IsNull();
        await Assert.That(BotGoalContinuity.TryParse("[1,2,3]")).IsNull();
        await Assert.That(BotGoalContinuity.TryParse("{\"activityName\":\"farm.work\"}"),
            "a partial payload parses; the missing fields are simply absent").IsNotNull();
    }

    #endregion
}
