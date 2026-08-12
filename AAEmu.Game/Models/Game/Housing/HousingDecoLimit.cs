namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// Decoration-limit group for a house family (housing_deco_limits).
/// Each group carries per-actability-group allowances via <see cref="HousingDecoLimitElem"/>.
/// </summary>
public class HousingDecoLimit
{
    public uint Id { get; set; }
    public string Name { get; set; }
}
