using System;

namespace AAEmu.Game.Models.Game.Siege;

/// <summary>
/// Mutable runtime state of a declared dominion, persisted one-row-per-zone in the
/// MySQL <c>dominions</c> table (aaemu_game). This is the only invented mutable
/// dominion state; everything else is reference data from compact.sqlite3.
/// </summary>
public class Dominion
{
    /// <summary>zone_groups.id of the owned zone — primary key of the store.</summary>
    public uint ZoneGroupId { get; set; }
    public uint ExpeditionId { get; set; }
    public string ExpeditionName { get; set; } = string.Empty;
    public int TaxRate { get; set; }
    public DateTime DeclaredAt { get; set; }
}
