namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The part of the bot that performs an elected activity (plan T9, audit L3-a
/// "dispatcher-or-deny"). An elected activity name is only meaningful when
/// something actually carries it out; this is the one place that says what does.
/// </summary>
internal enum BotActivityCarrier
{
    /// <summary>The executor's conflict engagement step.</summary>
    Conflict,

    /// <summary>The executor's needs (survival/farm) leg.</summary>
    Needs,

    /// <summary>The executor's quest leg (discovery or progress).</summary>
    Quest,

    /// <summary>The executor's homestead leg.</summary>
    Homestead,

    /// <summary>The executor's recovery step (rest/consume).</summary>
    Recovery,

    /// <summary>The executor's operator assignment leg.</summary>
    AssignedTravel,

    /// <summary>The schedule layer's phase route, armed through the schedule behaviour.</summary>
    Schedule,

    /// <summary>The presence module's own patrol route.</summary>
    PresenceRoam,

    /// <summary>The terminal idle fallback, whose work is to clear the route and stand still.</summary>
    Idle,
}

/// <summary>One declared rule: an activity name (or prefix) and what carries it.</summary>
internal readonly record struct BotActivityDispatchRule(
    string Value,
    bool IsPrefix,
    BotActivityCarrier Carrier,
    string CarriedBy);

/// <summary>
/// ONE declaration of which elected activity the bot can actually carry out
/// (plan T9, audit L3-a "dispatcher-or-deny"). Two callers read it and they must
/// never disagree:
///   - <see cref="BotRoamStepExecutor"/> resolves the elected name through
///     <see cref="Resolve"/> instead of testing its own literal prefixes, so the
///     gate that runs the work and the declaration of who owns the work are the
///     same list, and
///   - <see cref="BotGoalArbiter"/> asks <see cref="IsCarried"/> before it
///     reports <c>Activated</c>, so a module that elects a name nobody performs
///     is refused with a reason instead of silently roaming.
///
/// The rules mirror the executor's dispatch branches plus the route layers that
/// arm their own route from inside their activation (schedule phases, presence
/// roam) and the idle fallback. Anything else — today the fishing contest's
/// "fishing.contest" — has no owner: the module must deny rather than report a
/// successful activation (see <see cref="BotActivityDispatchValidation"/>).
/// </summary>
internal static class BotActivityDispatchSurface
{
    /// <summary>Prefix of every conflict engagement activity.</summary>
    public const string ConflictPrefix = "conflict.";

    /// <summary>Prefix of every needs (survival) leg activity.</summary>
    public const string NeedsPrefix = "needs.";

    /// <summary>Prefix of every quest leg activity.</summary>
    public const string QuestPrefix = "quest.";

    /// <summary>Prefix of every homestead leg activity.</summary>
    public const string HomesteadPrefix = "homestead.";

    private static readonly BotActivityDispatchRule[] Rules =
    [
        new(ConflictPrefix, true, BotActivityCarrier.Conflict, "the executor's conflict engagement step"),
        new(NeedsPrefix, true, BotActivityCarrier.Needs, "the executor's needs (survival) leg"),
        new(QuestPrefix, true, BotActivityCarrier.Quest, "the executor's quest leg"),
        new(HomesteadPrefix, true, BotActivityCarrier.Homestead, "the executor's homestead leg"),
        new(OutOfCombatRecoveryModule.ActivityNameRest, false, BotActivityCarrier.Recovery, "the executor's recovery step"),
        new(OutOfCombatRecoveryModule.ActivityNameEat, false, BotActivityCarrier.Recovery, "the executor's recovery step"),
        new(BotAssignmentActivities.Travel, false, BotActivityCarrier.AssignedTravel, "the executor's operator assignment leg"),
        new(SchedulePhaseActivityModule.ActivityPrefix, true, BotActivityCarrier.Schedule, "the schedule behaviour's phase route"),
        new(PresenceRoamActivityModule.ActivityName, false, BotActivityCarrier.PresenceRoam, "the presence module's own patrol route"),
        new(IdleActivityModule.ActivityName, false, BotActivityCarrier.Idle, "the idle fallback (it clears the route and stands still)"),
    ];

    /// <summary>Every declared rule, in resolution order.</summary>
    internal static IReadOnlyList<BotActivityDispatchRule> DeclaredRules => Rules;

    /// <summary>
    /// The carrier of <paramref name="activityName"/>, or null when nothing in
    /// the bot performs that activity.
    /// </summary>
    public static BotActivityCarrier? Resolve(string? activityName)
    {
        if (string.IsNullOrEmpty(activityName))
            return null;

        foreach (var rule in Rules)
        {
            var matched = rule.IsPrefix
                ? activityName.StartsWith(rule.Value, StringComparison.Ordinal)
                : string.Equals(activityName, rule.Value, StringComparison.Ordinal);
            if (matched)
                return rule.Carrier;
        }

        return null;
    }

    /// <summary>True when some part of the bot carries <paramref name="activityName"/>.</summary>
    public static bool IsCarried(string? activityName) => Resolve(activityName) is not null;
}
