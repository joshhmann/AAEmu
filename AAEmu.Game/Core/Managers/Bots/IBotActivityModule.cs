namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// G3-B3 goal arbitration — the module seam that decides WHAT a bot should
/// do next. A module is one capability layer (schedule phases, roam
/// presence, and future farmer/hauler/adventure behaviors) negotiating for
/// ONE active activity per bot per scheduler wake.
///
/// Contract (roadmap G3-B3): modules are EMBODIED (they control real bot
/// characters through the existing M5 surfaces — step executors, schedule
/// behaviors, actor actions). They never open a parallel gameplay path and
/// never mutate the world outside the execution boundary.
///
/// Arbitration order: highest <see cref="Priority"/> wins, ties broken by
/// registration order. The arbiter (<see cref="BotGoalArbiter"/>) calls
/// <see cref="CanActivate"/> on each module in order; the first allowing
/// module wins. Its activity NAME is the arbiter's unit of change — a new
/// name triggers exactly ONE <see cref="Activate"/> (side effects) plus a
/// single transition log; the same name on later wakes is steady state
/// (no re-activation, nothing visible happens).
/// </summary>
public interface IBotActivityModule
{
    /// <summary>Diagnostics name (also the log identity of transitions).</summary>
    string Name { get; }

    /// <summary>Arbitration rank — higher wins. Ties break by registration order.</summary>
    int Priority { get; }

    /// <summary>
    /// Pure eligibility probe for one arbitration pass. Must be cheap and
    /// side-effect free; the returned <see cref="BotActivityDecision.ActivityName"/>
    /// identifies WHAT this module would run (stable across wakes while the
    /// desired behavior is unchanged, e.g. "schedule.work").
    /// </summary>
    BotActivityDecision CanActivate(BotActivityContext context);

    /// <summary>
    /// Applies the winning activity's visible behavior (arm routes, walk to
    /// an anchor, idle...). Called by the arbiter ONLY on an activity change
    /// — never on steady-state wakes.
    /// </summary>
    BotActivity Activate(BotActivityContext context);

    /// <summary>
    /// The activity names this module can elect, declared so the bot can check
    /// that something actually performs them (plan T9, audit L3-a
    /// "dispatcher-or-deny"). A name is declared exactly as it is elected — the
    /// conflict module declares its zone-group prefix, the quest module its two
    /// names. <see cref="BotActivityDispatchValidation"/> reads this together
    /// with <see cref="BotActivityDispatchSurface"/>: a declared name no carrier
    /// performs is a wiring gap to fix, not a silent success.
    ///
    /// Default = empty, the honest answer for a module that does not say: the
    /// validation reports the gap instead of assuming coverage.
    /// </summary>
    IReadOnlyList<string> DeclaredActivities => [];

    /// <summary>
    /// The modes (<see cref="BotMode"/>) this module can be elected in. The
    /// startup dispatch check reports any arbiter mode no registered module
    /// serves, so a gap like run 11's "nobody owns a fight" is found at boot.
    /// Default = Free: almost every module denies while the bot is in battle.
    /// </summary>
    IReadOnlyList<BotMode> ServedModes => [BotMode.Free];

    /// <summary>
    /// Asks this module to RELEASE whatever the activity it is losing still
    /// owns (a live actor request, an armed route, a held reservation).
    /// Called by the arbiter when this module's activity is superseded, BEFORE
    /// the incoming module activates.
    ///
    /// Default = <c>true</c>, the honest answer for a module that owns nothing
    /// to release: no module had teardown when this contract was added, and a
    /// module that does not override it must not be able to pretend it retired
    /// work it still holds. Returning <c>false</c> means "I am still holding
    /// my work"; the incoming module receives that refusal in
    /// <see cref="BotActivityContext.Retirement"/> and must not claim the
    /// superseded work was honoured.
    /// </summary>
    bool Deactivate(BotActivityContext context) => true;
}

/// <summary>
/// Everything a module may look at during one arbitration pass. Built by
/// the arbiter per bot wake; immutable.
/// </summary>
public sealed class BotActivityContext
{
    /// <summary>The bot being arbitrated (ordinary Character record inside).</summary>
    public required PlayerBotRuntime Bot { get; init; }

    /// <summary>Server GAME-clock hour (TimeManager scale, 0..24).</summary>
    public float GameHour { get; init; }

    /// <summary>The activity name currently active for this bot (null = none yet).</summary>
    public string? ActiveActivity { get; init; }

    /// <summary>
    /// Outcome of asking the outgoing module to retire (null = nothing was
    /// superseded). Set by the arbiter on the context handed to
    /// <see cref="IBotActivityModule.Activate"/>, so a module that takes an
    /// activity away from another one can see whether that work was actually
    /// released before it claims the new activity is being honoured.
    /// </summary>
    public BotActivityRetirement? Retirement { get; init; }

    /// <summary>
    /// The last leg the bot ran under THIS module's activity ended (plan T4,
    /// audit L3-b). Set by the arbiter on the context handed to the owning
    /// module's <see cref="IBotActivityModule.CanActivate"/> — and only to that
    /// module, exactly once, on the first election after the executor reported
    /// it. Null on every other module's context and on every other wake.
    ///
    /// It exists because a module's only recurring hook is
    /// <see cref="IBotActivityModule.CanActivate"/>: without the leg's fate a
    /// module whose work failed answers identically wake after wake. A module
    /// that wants to remember the outcome longer (a cooldown, a deferral) keeps
    /// its own memory — the arbiter delivers the fact, it does not hold policy.
    ///
    /// A leg the bot has already left its activity behind is not delivered at
    /// all: the fact is about work that is over, so nobody is handed it.
    /// </summary>
    public BotLegOutcome? LastLegOutcome { get; init; }

    /// <summary>
    /// The bot's durable goal-continuity record, handed to the module that LOST
    /// the goal (plan T6; audit L1.3 "no intent is persisted", L4-b, L4-c). Set
    /// by the arbiter on the context handed to that module's
    /// <see cref="IBotActivityModule.CanActivate"/>, and only to that module: at
    /// most once per process read of a given record, so a restart or a
    /// re-enrollment tells it once more, and a record replaced by the next
    /// outcome change before that module was next probed is told to nobody (the
    /// band scan stops at the first band that yields a candidate, so a loser
    /// below the winner may never be asked). The record is written whenever the
    /// election changes the outcome, and read back from the bot metadata store
    /// on the first election after a restart or a re-enrollment, so the goal the
    /// bot was running — and the reason it ended — survives the process instead
    /// of being rebuilt from nothing.
    ///
    /// It is a fact, not an instruction: the arbiter never re-elects a recorded
    /// goal by itself, and a module that wants to resume, defer or ignore the
    /// interrupted work says so in its own answer.
    /// </summary>
    public BotGoalContinuity? Continuity { get; init; }
}

/// <summary>
/// What happened when the arbiter asked the module that owned the outgoing
/// activity to release it (<see cref="IBotActivityModule.Deactivate"/>).
/// </summary>
/// <param name="ModuleName">Diagnostics name of the outgoing module.</param>
/// <param name="ActivityName">The activity that was superseded.</param>
/// <param name="Retired">True when the module reported it released its work.</param>
/// <param name="Reason">Why it retired, or why it refused / failed.</param>
public sealed record BotActivityRetirement(string ModuleName, string ActivityName, bool Retired, string Reason);

/// <summary>Result of <see cref="IBotActivityModule.CanActivate"/>.</summary>
public sealed record BotActivityDecision(bool CanActivate, string? ActivityName = null, string? WhyNot = null, int Weight = 0)
{
    /// <summary>
    /// Allows activation under the given stable activity name.
    /// <paramref name="weight"/> is the module's DRAWN MOTIVATION for this
    /// wake — how strongly it wants this activity right now (0 = eligible but
    /// unmotivated, the default for every module that draws nothing).
    ///
    /// Weight orders the CANDIDATES OF ONE PRIORITY BAND only. Priority stays
    /// the safety ordering: a heavier module can never outrank a higher
    /// priority band, so survival/recovery bands keep their precedence.
    /// </summary>
    public static BotActivityDecision Allow(string activityName, int weight = 0) => new(true, activityName, null, weight);

    /// <summary>Declines with a diagnostics reason (falls through to the next module).</summary>
    public static BotActivityDecision Deny(string whyNot) => new(false, WhyNot: whyNot);
}

/// <summary>
/// Shared motivation scale for <see cref="BotActivityDecision.Allow"/> weights.
/// Weights are only ever compared between modules inside ONE priority band, so
/// every module that shares a band must draw on this same scale — otherwise the
/// comparison is meaningless.
/// </summary>
public static class BotActivityMotivation
{
    /// <summary>The activity is already satisfied — hold cheaply.</summary>
    public const int Satisfied = 0;

    /// <summary>A modest draw: worth doing, or already under way.</summary>
    public const int Wants = 40;

    /// <summary>An unmet need this activity exists to serve.</summary>
    public const int Needs = 100;
}

/// <summary>An activated activity handle (identity only — behavior lives in the module).</summary>
public sealed record BotActivity(string Name, string ModuleName);
