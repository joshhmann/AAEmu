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
}

/// <summary>Result of <see cref="IBotActivityModule.CanActivate"/>.</summary>
public sealed record BotActivityDecision(bool CanActivate, string? ActivityName = null, string? WhyNot = null)
{
    /// <summary>Allows activation under the given stable activity name.</summary>
    public static BotActivityDecision Allow(string activityName) => new(true, activityName);

    /// <summary>Declines with a diagnostics reason (falls through to the next module).</summary>
    public static BotActivityDecision Deny(string whyNot) => new(false, WhyNot: whyNot);
}

/// <summary>An activated activity handle (identity only — behavior lives in the module).</summary>
public sealed record BotActivity(string Name, string ModuleName);
