namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// What one host disarm actually did (A1 converging teardown). Reported by
/// <see cref="IPlayerBotScheduler.Disarm"/> so a host shutdown or an operator
/// can read back — instead of guessing from logs — how many enrolled bots left
/// the Active set, how many queued steps the stopped execution boundary could
/// no longer run, and which bots failed to leave. A non-empty
/// <see cref="Failures"/> list is a partial teardown, never a success.
/// </summary>
/// <param name="Reason">Why the host was disarmed (host-shutdown, operator route, …).</param>
/// <param name="SchedulerWasRunning">True when the scheduler was still running and this call stopped it.</param>
/// <param name="QueuedStepsDropped">
/// Steps still on the marshal queue when the execution boundary stopped. They
/// are dropped — the game loop is gone at shutdown and executing them
/// off-boundary would violate the M5 contract — and their leases are released.
/// </param>
/// <param name="BotsDeactivated">Enrolled bots that reached Deactivated through the ordinary manager/leave path.</param>
/// <param name="BotsFailed">
/// Enrolled bots that did NOT provably leave: a leave the manager refused or
/// that threw, a bot already sitting in Deactivating/DeactivationFailed when the
/// disarm began, and one that landed there while the host was stopping. A bot
/// that was never embodied (Registered) is not counted — it holds no world
/// presence and no leave-save.
/// </param>
/// <param name="Failures">One entry per failed bot, "&lt;characterId&gt;:&lt;state&gt;".</param>
public sealed record BotHostTeardownReport(
    string Reason,
    bool SchedulerWasRunning,
    int QueuedStepsDropped,
    int BotsDeactivated,
    int BotsFailed,
    IReadOnlyList<string> Failures)
{
    /// <summary>
    /// True when no enrolled bot was left un-safely-deactivated — every bot is
    /// Deactivated (left), Registered (never embodied) or was never enrolled.
    /// A non-empty <see cref="Failures"/> list is a partial teardown.
    /// </summary>
    public bool Converged => BotsFailed == 0;

    /// <summary>One-line operator summary of the teardown.</summary>
    public override string ToString()
        => $"disarm({Reason}): running={SchedulerWasRunning} deactivated={BotsDeactivated} "
            + $"failed={BotsFailed} queuedStepsDropped={QueuedStepsDropped}"
            + (Failures.Count > 0 ? $" failures=[{string.Join(", ", Failures)}]" : string.Empty);
}
