using System;

namespace AAEmu.Game.Models.Game.Siege;

/// <summary>
/// One <c>siege_plans</c> rotation entry: which zone group is scheduled for a
/// siege during the week starting at <see cref="WeekStart"/> (legacy 2014 dates;
/// the rotation repeats with the period of the distinct week starts).
/// </summary>
public class SiegePlanEntry
{
    public uint Id { get; set; }
    public uint ZoneGroupId { get; set; }
    public DateTime WeekStart { get; set; }
}
