namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// Category allowance within a housing group (housing_group_categories):
/// which item category may be built on a group's plots and how many times (0 = unlimited).
/// </summary>
public class HousingGroupCategory
{
    public uint Id { get; set; }
    public uint HousingGroupId { get; set; }
    public uint CategoryId { get; set; }
    public int MaxConstructCount { get; set; }
}
