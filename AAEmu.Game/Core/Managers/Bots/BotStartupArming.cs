namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// T2 supported arming — the one place a deployment turns the bot host on at
/// startup. It arms the host through the single arming owner
/// (<see cref="IPlayerBotScheduler.Arm"/>) and then runs the ordinary
/// enrollment the caller hands it (the presence coordinator in production), so
/// the bots it starts are the same real managed accounts + characters as every
/// other enrollment: no second scheduler, no second start path, no parallel
/// bot store.
///
/// The switch is deliberately thin. It owns nothing but the arming reason and
/// the honest result of the attempt; the keys live in
/// <see cref="BotStartupArmingOptions"/>, the enrollment flow lives in
/// <see cref="BotPresenceCoordinator"/>, and the disarm at shutdown is the one
/// the host already had (<c>GameService.DisarmBotHost</c> →
/// <c>IPlayerBotScheduler.Disarm</c>).
/// </summary>
internal static class BotStartupArming
{
    /// <summary>The arming reason recorded by the one owner for this switch.</summary>
    internal const string Owner = "startup-arming";

    /// <summary>
    /// Arms the host and then runs <paramref name="enroll"/> for
    /// <paramref name="config"/>, reporting what the enrollment actually did
    /// (<see cref="BotPresenceCoordinator.BotEnrollmentOutcome"/>) instead of
    /// what was asked for. Never throws: a refused arming or a failed
    /// enrollment is reported in the result, and the caller logs it.
    /// </summary>
    public static BotStartupArmingResult Enroll(
        IPlayerBotScheduler scheduler,
        BotPresenceCoordinator.BotPresenceConfig config,
        Func<BotPresenceCoordinator.BotPresenceConfig, BotPresenceCoordinator.BotEnrollmentOutcome> enroll)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(enroll);

        var armed = scheduler.Arm(Owner);
        if (!scheduler.IsRunning)
        {
            // The host was disarmed earlier in this process: no bot can step, so
            // enrolling would only create characters that never move. The two
            // shapes are reported apart (review D3): Arm itself refused, or Arm
            // succeeded and a concurrent Disarm landed between the two reads.
            return new BotStartupArmingResult(
                Armed: false,
                HostRunning: false,
                ArmedBy: scheduler.ArmedBy,
                Count: config.BotCount,
                Enrolled: 0,
                Converged: true,
                Message: armed
                    ? "the bot host stopped while the startup enrollment was starting — no bots enrolled"
                    : "the bot host is not running (arming was refused) — no bots enrolled");
        }

        var outcome = enroll(config);

        // The enrollment runs on a detached boot task that nothing awaits, so a
        // host shutdown can land while it is still enrolling (review D4).
        // Disarm is idempotent and converges on a fresh snapshot, so one more
        // pass once the enrollment is done cannot leave a bot Active behind a
        // stopped host — the state the card's acceptance forbids.
        var converged = true;
        var stoppedDuringEnrollment = !scheduler.IsRunning;
        if (stoppedDuringEnrollment)
        {
            var report = scheduler.Disarm($"{Owner}-after-stop");
            converged = report.Converged;
        }

        var roster = $"of a roster of up to {config.BotCount}";
        var message = outcome.Any
            ? (armed
                ? $"armed the bot host and enrolled {outcome.Enrolled} bot(s) {roster}"
                : $"the bot host was already armed by {scheduler.ArmedBy ?? "an unrecorded caller"}; enrolled {outcome.Enrolled} bot(s) {roster}")
            : outcome.AlreadyEmbodied
                ? "the bot host is armed but the world already held enough bots — nothing enrolled (idempotent)"
                : "the bot host is running but the startup enrollment enrolled no bot (see the coordinator log)";

        if (outcome.Adopted > 0)
            message += $"; {outcome.Adopted} of them re-enrolled in-process (no new provisioning)";
        if (stoppedDuringEnrollment)
            message += $"; the host stopped while the enrollment was in flight and was disarmed again (converged={converged})";

        return new BotStartupArmingResult(armed, !stoppedDuringEnrollment, scheduler.ArmedBy, config.BotCount, outcome.Enrolled, converged, message);
    }
}

/// <summary>
/// What one startup-arming attempt actually did — reported, never assumed.
/// <c>Armed</c> is true only when THIS call armed the host; <c>HostRunning</c>
/// is the host state at return, not at entry (a shutdown that landed while the
/// enrollment was in flight is reported, not hidden); <c>ArmedBy</c> is the
/// owner the scheduler records; <c>Count</c> is the roster size that was asked
/// for; <c>Enrolled</c> is the number of bots the enrollment reported as
/// reaching the world (a re-enrolled bot counts, a skipped enrollment reports
/// 0); <c>Converged</c> is the teardown verdict of the extra pass taken when
/// the host stopped while the enrollment was in flight.
/// </summary>
internal readonly record struct BotStartupArmingResult(
    bool Armed,
    bool HostRunning,
    string? ArmedBy,
    int Count,
    int Enrolled,
    bool Converged,
    string Message);
