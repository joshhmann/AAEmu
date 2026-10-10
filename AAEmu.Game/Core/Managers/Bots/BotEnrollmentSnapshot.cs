namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Read-only per-bot enrollment snapshot for the P0.1 first-wake discriminator
/// (Layer 1 §D / E-48): distinguishes no-entry, entry-never-due and
/// due-never-executed without mutating registry, scheduler or executor state.
/// Data only; the harness computes verdicts from <see cref="Disposition"/>.
/// </summary>
/// <param name="Registered">Manager holds a runtime for this character id.</param>
/// <param name="State">Registry state string (Active/Registered/Deactivated/-, never null).</param>
/// <param name="ScheduledDueUtc">Next due time when the scheduler holds an entry; null when no entry.</param>
/// <param name="Leased">A step is currently queued or running for this bot.</param>
/// <param name="PendingWakeUtc">Due folded while leased; null when none.</param>
/// <param name="LastOutcome">Last terminal step outcome; null when no wake ever reached a terminal site.</param>
/// <param name="LastStepUtc">UTC of the last terminal outcome; null when none.</param>
/// <param name="Skipped">Cumulative skipped steps for this bot.</param>
/// <param name="Failed">Cumulative failed steps for this bot.</param>
/// <param name="TimedOut">Cumulative timed-out steps for this bot.</param>
/// <param name="Ran">Cumulative executed steps for this bot.</param>
/// <param name="SchedulerRunning">Scheduler scan/worker loops live between Start() and StopAsync().</param>
/// <param name="Generation">Live activation generation (0 = unregistered); stale callers refuse on mismatch.</param>
public sealed record BotEnrollmentSnapshot(
    bool Registered,
    string State,
    DateTime? ScheduledDueUtc,
    bool Leased,
    DateTime? PendingWakeUtc,
    string? LastOutcome,
    DateTime? LastStepUtc,
    long Skipped,
    long Failed,
    long TimedOut,
    long Ran,
    bool SchedulerRunning,
    ulong Generation)
{
    /// <summary>
    /// The P0.1 discriminator, computed from this snapshot alone:
    /// no-entry (not registered or not Active), entry-never-due (active but
    /// no due entry, lease or pending wake), due-never-executed (due/leased/
    /// pending held but no terminal outcome yet), executed (a wake reached a
    /// terminal site at least once).
    /// </summary>
    public string Disposition
    {
        get
        {
            if (!Registered || State != nameof(PlayerBotState.Active))
                return "no-entry";
            if (LastOutcome is not null || Ran + Skipped + Failed + TimedOut > 0)
                return "executed";
            if (ScheduledDueUtc is not null || Leased || PendingWakeUtc is not null)
                return "due-never-executed";
            return "entry-never-due";
        }
    }
}
