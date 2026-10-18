namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The ONE rule for who owns a bot's roam route (plan T8, audit L1-g
/// "schedule split-brain"). Two components apply schedule phases — the
/// tick-driven <see cref="BotScheduleService"/> in production and the arbiter's
/// own <see cref="SchedulePhaseActivityModule"/> in isolated rigs — and both
/// must ask the same question before arming a route.
///
/// The authority is the arbiter's live single-active activity
/// (<see cref="IBotGoalArbiter.GetActiveActivity"/>): there is no second copy of
/// "who is in force". Two activities do NOT own a route:
///   - the terminal idle fallback, which CLEARS the route instead of owning
///     one, so a route-less/idle bot stays free for the schedule to arm, and
///   - the schedule layer's own "schedule.*" activities, which ARE the
///     schedule's route.
/// Everything else that is live — presence roam, needs-farm, quest bootstrap,
/// assigned travel, recovery, ... — is work in force, and its route must
/// survive a schedule phase change.
/// </summary>
internal static class BotRouteOwnership
{
    /// <summary>
    /// True when <paramref name="activityName"/> owns the bot's route, so a
    /// lower-authority writer must not overwrite it. Null, empty, the idle
    /// fallback and the schedule layer's own activities are not owners.
    /// </summary>
    internal static bool OwnsRoute(string? activityName) =>
        !string.IsNullOrEmpty(activityName)
        && !string.Equals(activityName, IdleActivityModule.ActivityName, StringComparison.Ordinal)
        && !activityName.StartsWith(SchedulePhaseActivityModule.ActivityPrefix, StringComparison.Ordinal);
}
