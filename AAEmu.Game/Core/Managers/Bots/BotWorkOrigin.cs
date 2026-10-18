#nullable enable

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Which side of the control plane the work a bot has in force came from
/// (plan T7; audit L2.7). The rule is deliberately mechanical, so the operator
/// read-back cannot flatter itself:
///
/// <list type="bullet">
/// <item>the work in force is <see cref="Commanded"/> when a LIVE assignment
/// exists and its activity is the activity in force — the operator asked for
/// exactly that work and it is the work running;</item>
/// <item>anything else in force is <see cref="Autonomous"/> — the bot chose it,
/// including the case where a live assignment exists but a different activity
/// won the wake (the commanded work is not what is running);</item>
/// <item>null when no work is in force: nothing is running, so there is no
/// origin to name. The caller reads <see cref="BotBrainSnapshot.Assignment"/>
/// for commanded intent that is pending rather than in force.</item>
/// </list>
/// </summary>
public static class BotWorkOrigin
{
    /// <summary>Work in force that the operator asked for (a live assignment).</summary>
    public const string Commanded = "commanded";

    /// <summary>Work in force that the bot's own election chose.</summary>
    public const string Autonomous = "autonomous";

    /// <summary>
    /// The origin of the activity in force, or null when nothing is in force.
    /// <paramref name="activityInForce"/> is the activity the ARBITRATION
    /// elected (the decision's own activity): a quest runtime's last-ticked
    /// activity is not the same fact and must not be passed here.
    /// </summary>
    public static string? Of(string? activityInForce, BotAssignment? assignment)
    {
        if (string.IsNullOrEmpty(activityInForce))
            return null;
        return assignment is { IsLive: true }
            && string.Equals(assignment.Activity, activityInForce, StringComparison.Ordinal)
                ? Commanded
                : Autonomous;
    }
}
