namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// Housing design group shown in the client's build UI (housing_groups).
/// Groups the housing designs by area/plot type (farm land, small house, manor, ...).
/// </summary>
public class HousingGroup
{
    public uint Id { get; set; }
    public string Name { get; set; }
    public string Desc { get; set; }
    public uint? DoodadId { get; set; }
    public bool Houseless { get; set; }
    public uint? ExistingCategoryId { get; set; }
    public int AllowedTaxDelayWeek { get; set; }
    public bool CanExtend { get; set; }
}
