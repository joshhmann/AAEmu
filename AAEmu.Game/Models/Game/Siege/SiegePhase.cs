using System;

namespace AAEmu.Game.Models.Game.Siege;

/// <summary>
/// Weekly dominion/siege cycle phases derived from the <c>siege_zones</c> schedule columns.
/// </summary>
public enum SiegePhase : byte
{
    /// <summary>No scheduled activity for this zone in the current cycle.</summary>
    Peace = 0,
    /// <summary>Declaration window (start_declare_* → start_warmup_*).</summary>
    Declare = 1,
    /// <summary>Siege warmup window (start_warmup_* → start_siege_*).</summary>
    Warmup = 2,
    /// <summary>Active siege battle (start_siege_* → + siege_days/hours/mins).</summary>
    Siege = 3,
    /// <summary>Tax payoff moment (pay_weekday/pay_hour/pay_min). A point event, not an interval.</summary>
    Payoff = 4
}
