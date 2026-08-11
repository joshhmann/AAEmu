namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// One allowance row of a decoration-limit group (housing_deco_limit_elems):
/// how many decorations of a given deco actability group a house may carry.
/// </summary>
public class HousingDecoLimitElem
{
    public uint Id { get; set; }
    public uint HousingDecoLimitId { get; set; }
    public uint DecoActabilityGroupId { get; set; }
    public int Count { get; set; }
}
