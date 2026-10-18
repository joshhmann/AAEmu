using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The activity names the operator assignment plane accepts today. ONE
/// activity is the coverage denominator of the assignment wave: one bot, one
/// destination, one expiry, one refusal path, one read-back. A second
/// activity is a product decision, not a config toggle.
/// </summary>
public static class BotAssignmentActivities
{
    /// <summary>Walk one bot to one world position, bounded by the assignment expiry.</summary>
    public const string Travel = "assigned.travel";
}

/// <summary>
/// Lifecycle of one operator assignment. The operator-facing distinction that
/// matters is Accepted (the plane took it, nothing has run yet) versus Active
/// (the arbiter elected it) versus a terminal Completed/Failed/Expired that
/// carries the reason it ended.
/// </summary>
public enum BotAssignmentState : byte
{
    /// <summary>The operator plane accepted it; the arbiter has not elected it yet.</summary>
    Accepted = 0,

    /// <summary>The arbiter elected it and the executor is walking it.</summary>
    Active = 1,

    /// <summary>The leg reached the destination.</summary>
    Completed = 2,

    /// <summary>The leg ended without reaching the destination (reason recorded).</summary>
    Failed = 3,

    /// <summary>The expiry passed before the leg reached the destination.</summary>
    Expired = 4,
}

/// <summary>
/// One operator assignment (plan T1): what an operator asked ONE bot to do,
/// where, until when, and what happened. Immutable — every transition replaces
/// the record in <see cref="BotAssignmentRegistry"/>, so a reader (the brain
/// read-back, the module, the executor) always sees a consistent snapshot.
///
/// Memory-only on purpose: the assignment is a bounded operator instruction,
/// not durable bot state, so a restart drops it and the bot returns to normal
/// election (durable elected goals are a later, separate slice).
/// </summary>
public sealed record BotAssignment(
    uint CharacterId,
    string Activity,
    float X,
    float Y,
    float Z,
    string? RequestedBy,
    DateTime AcceptedUtc,
    DateTime ExpiresUtc,
    BotAssignmentState State,
    string? Retirement,
    string? LastReason,
    DateTime UpdatedUtc)
{
    /// <summary>Wire-friendly state name (the enum serialises as a number).</summary>
    public string StateName => State.ToString();

    /// <summary>True while the assignment still asks the bot to act.</summary>
    public bool IsLive => State is BotAssignmentState.Accepted or BotAssignmentState.Active;

    /// <summary>Seconds left before expiry (negative once past it).</summary>
    public double SecondsRemaining(DateTime utcNow) => (ExpiresUtc - utcNow).TotalSeconds;
}

/// <summary>
/// The ONE store of operator assignments (plan T1, gap N2): the HTTP route, the
/// E2E bridge command, the arbitration module and the executor's leg all read
/// and write this same registry, so the frontends cannot diverge and the
/// read-back cannot report a state the executor never reached.
///
/// Not durable by design (see <see cref="BotAssignment"/>).
/// </summary>
public interface IBotAssignmentRegistry
{
    /// <summary>
    /// Accepts one assignment for a bot, replacing any live one. Returns null
    /// when the activity is not supported (the plane refuses rather than
    /// silently ignoring an unknown activity).
    /// </summary>
    BotAssignment? Accept(uint characterId, string activity, float x, float y, float z, TimeSpan expiry, string? requestedBy = null);

    /// <summary>
    /// The assignment the bot should still act on (Accepted or Active, not yet
    /// expired). Expiry is evaluated on read: an expired assignment is
    /// transitioned to <see cref="BotAssignmentState.Expired"/> with a recorded
    /// reason and then reported as not live.
    /// </summary>
    bool TryGetLive(uint characterId, out BotAssignment? assignment);

    /// <summary>The last assignment recorded for the bot whatever its state (read-back), null when none.</summary>
    bool TryGet(uint characterId, out BotAssignment? assignment);

    /// <summary>Marks the assignment as elected and being executed (arbiter election).</summary>
    void MarkActive(uint characterId, string reason);

    /// <summary>Marks the assignment completed with the reason it ended.</summary>
    void Complete(uint characterId, string reason);

    /// <summary>Marks the assignment failed with the reason it ended.</summary>
    void Fail(uint characterId, string reason);
}

/// <inheritdoc />
public sealed class BotAssignmentRegistry : IBotAssignmentRegistry
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Default expiry when the caller does not name one: bounded, so the band above 100 cannot starve election.</summary>
    public const int DefaultExpirySeconds = 600;

    /// <summary>Longest accepted expiry (one hour — beyond that the operator should re-issue).</summary>
    public const int MaxExpirySeconds = 3600;

    private readonly object _lock = new();
    private readonly Dictionary<uint, BotAssignment> _assignments = [];

    /// <summary>Clock seam (tests drive expiry deterministically; production reads UTC).</summary>
    public Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;

    /// <summary>
    /// Wake hook invoked once for every ACCEPTED assignment, with the
    /// character id. Production wires it to <c>IPlayerBotScheduler.Wake</c> so
    /// an operator instruction reaches a bot the scheduler has stopped waking;
    /// tests and rigs leave it null (no scheduler, no side effect).
    /// </summary>
    public Action<uint>? Wake { get; init; }

    /// <inheritdoc />
    public BotAssignment? Accept(uint characterId, string activity, float x, float y, float z, TimeSpan expiry, string? requestedBy = null)
    {
        if (!string.Equals(activity, BotAssignmentActivities.Travel, StringComparison.OrdinalIgnoreCase))
            return null;

        var now = UtcNow();
        var bounded = expiry <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(DefaultExpirySeconds)
            : expiry > TimeSpan.FromSeconds(MaxExpirySeconds)
                ? TimeSpan.FromSeconds(MaxExpirySeconds)
                : expiry;
        var assignment = new BotAssignment(
            characterId, BotAssignmentActivities.Travel, x, y, z, requestedBy, now, now + bounded,
            BotAssignmentState.Accepted, null, "accepted by the operator assignment plane", now);

        bool replaced;
        lock (_lock)
        {
            replaced = _assignments.TryGetValue(characterId, out var previous) && previous.IsLive;
            _assignments[characterId] = assignment;
        }

        Logger.Info("Bot assignment accepted: bot {CharacterId} {Activity} to ({X:F0},{Y:F0},{Z:F0}) for {Seconds:F0}s by {RequestedBy}{Replaced}",
            characterId, assignment.Activity, x, y, z, bounded.TotalSeconds, requestedBy ?? "(unknown)",
            replaced ? " — replaced a live assignment" : string.Empty);

        // An operator instruction has to reach a bot that is asleep right now:
        // with the idle-wake gate off (the default) an idle bot's executor
        // returns null from its wake and the scheduler stops waking it until
        // something re-arms it — so the assignment itself must be that
        // something. Production wires this to IPlayerBotScheduler.Wake; a rig
        // leaves it null. A failure here never fails the accepted assignment:
        // it is recorded and the operator's next instruction or any wake
        // delivers it.
        var wake = Wake;
        if (wake != null)
        {
            try
            {
                wake(characterId);
            }
            catch (Exception ex)
            {
                Logger.Warn("Bot assignment accepted but the bot could not be woken: bot {CharacterId} — {Error}", characterId, ex.Message);
            }
        }

        return assignment;
    }

    /// <inheritdoc />
    public bool TryGetLive(uint characterId, out BotAssignment? assignment)
    {
        assignment = null;
        BotAssignment current;
        lock (_lock)
        {
            if (!_assignments.TryGetValue(characterId, out current!))
                return false;

            current = ApplyExpiryLocked(characterId, current);
        }

        if (!current.IsLive)
            return false;

        assignment = current;
        return true;
    }

    /// <inheritdoc />
    public bool TryGet(uint characterId, out BotAssignment? assignment)
    {
        lock (_lock)
        {
            if (!_assignments.TryGetValue(characterId, out assignment!))
                return false;

            // The read-back applies the SAME lazy expiry the executor does: a bot
            // nobody is ticking (wake hook refused, deactivated, scheduler stopped)
            // would otherwise report a past ExpiresUtc as live — "Accepted" for a
            // record that is really Expired (code review 2026-10-16, P3-4).
            assignment = ApplyExpiryLocked(characterId, assignment!);
            return true;
        }
    }

    /// <summary>
    /// Applies the lazy expiry to a live record and returns it as it now stands.
    /// MUST be called while holding <see cref="_lock"/>.
    /// </summary>
    private BotAssignment ApplyExpiryLocked(uint characterId, BotAssignment current)
    {
        if (!current.IsLive || UtcNow() < current.ExpiresUtc)
            return current;

        var expired = current with
        {
            State = BotAssignmentState.Expired,
            LastReason = $"expired after {(current.ExpiresUtc - current.AcceptedUtc).TotalSeconds:F0}s without reaching the destination",
            UpdatedUtc = UtcNow(),
        };
        _assignments[characterId] = expired;
        Logger.Info("Bot assignment expired: bot {CharacterId} {Activity} — {Reason}", characterId, expired.Activity, expired.LastReason);
        return expired;
    }

    /// <inheritdoc />
    public void MarkActive(uint characterId, string reason) => Transition(characterId, BotAssignmentState.Active, reason, null);

    /// <inheritdoc />
    public void Complete(uint characterId, string reason) => Transition(characterId, BotAssignmentState.Completed, reason, null);

    /// <inheritdoc />
    public void Fail(uint characterId, string reason) => Transition(characterId, BotAssignmentState.Failed, reason, null);

    /// <summary>
    /// Applies one transition to a LIVE assignment only: a completed, failed or
    /// expired assignment never moves again (a late leg verdict cannot rewrite
    /// history), and an unknown character is a no-op.
    /// </summary>
    private void Transition(uint characterId, BotAssignmentState state, string reason, string? retirement)
    {
        BotAssignment? updated = null;
        lock (_lock)
        {
            if (!_assignments.TryGetValue(characterId, out var current) || !current.IsLive)
                return;

            updated = current with
            {
                State = state,
                LastReason = reason,
                Retirement = retirement ?? current.Retirement,
                UpdatedUtc = UtcNow(),
            };
            _assignments[characterId] = updated;
        }

        Logger.Info("Bot assignment {State}: bot {CharacterId} {Activity} — {Reason}",
            updated.StateName, characterId, updated.Activity, reason);
    }
}
