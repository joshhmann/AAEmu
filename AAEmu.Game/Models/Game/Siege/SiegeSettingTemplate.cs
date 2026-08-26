namespace AAEmu.Game.Models.Game.Siege;

/// <summary>
/// One <c>siege_settings</c> row — per-castle-slot defender/reinforcement caps.
/// </summary>
public class SiegeSettingTemplate
{
    /// <summary>Row id / castle slot index (0..total_castles-1 in the shipped data).</summary>
    public uint Id { get; set; }
    public int NumDefenders { get; set; }
    public int NumReinforcements { get; set; }
}
