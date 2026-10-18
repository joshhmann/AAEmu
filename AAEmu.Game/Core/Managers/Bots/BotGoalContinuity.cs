using System.Text.Json;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// The durable record of a bot's last election outcome (plan T6; layer-audit
/// L1.3 "two tables, no intent", L4-b "retention is an unpersisted name
/// string", L4-c "cross-family precedence ignores unfinished work"): the goal
/// that election put in force and, when it replaced one, the goal it
/// interrupted and why.
///
/// It exists because nothing about a bot's intent survived the process. The
/// arbiter's memory is in-process only, so a restart rebuilt the bot from its
/// character plus home/schedule, and a goal a higher priority band preempted
/// was dropped without a trace. The arbiter writes the record through the bot
/// metadata store's <c>planner_state</c> column and reads it back on the first
/// election after a restart or a re-enrollment, where it is handed to the
/// module that lost the goal through <see cref="BotActivityContext.Continuity"/>.
/// The record is a fact; what a module does about it is the module's business.
///
/// Delivery is best-effort, deliberately: the fact reaches the module that lost
/// the goal when that module is next asked whether it can act, and a band scan
/// stops at the first band that yields, so a loser below the winner may not be
/// asked for several wakes — and a later outcome change replaces the record
/// before it was ever read. In-process the loser is still told by the
/// retirement that supersedes it; the record is the part that survives a
/// restart. Nothing retries, and no module is asked outside its own probe.
///
/// Serialized as JSON — the same convention as the schedule and behavior
/// config columns of that row. Reading is total: an absent, empty or
/// unreadable payload yields null rather than throwing, because a damaged row
/// must never be able to take a scheduler wake down.
/// </summary>
/// <param name="ActivityName">
/// The goal the election put in force (null when no module could activate, so
/// nothing is in force).
/// </param>
/// <param name="ModuleName">The module that owns <paramref name="ActivityName"/> (null when nothing is in force).</param>
/// <param name="InterruptedActivity">
/// The goal this election replaced or dropped (null on the first election for
/// a bot — nothing was interrupted).
/// </param>
/// <param name="InterruptedModule">
/// The module that owned <paramref name="InterruptedActivity"/> — the module
/// the record is handed to, because the incoming module already knows it won.
/// </param>
/// <param name="InterruptionReason">
/// Why the interrupted goal ended (a supersede, a same-owner rename, a
/// retirement the outgoing module refused, or a wake where nobody could
/// activate).
/// </param>
/// <param name="RecordedAtUtc">When the arbiter recorded it.</param>
public sealed record BotGoalContinuity(
    string? ActivityName,
    string? ModuleName,
    string? InterruptedActivity,
    string? InterruptedModule,
    string? InterruptionReason,
    DateTime RecordedAtUtc)
{
    /// <summary>Serializes the record for the bot metadata row's planner_state column.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Reads a record back from a stored payload. Null for an absent, empty or
    /// unreadable payload — never throws, so one damaged row cannot break the
    /// election that reads it.
    /// </summary>
    public static BotGoalContinuity? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<BotGoalContinuity>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
