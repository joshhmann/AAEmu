namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// Housing design group shown in the client's build UI (<c>housing_groups</c>).
/// Groups the housing designs by area/plot type (farm land, small house, manor, ...).
/// The same table drives placement validation: <see cref="Houseless"/> marks zone
/// types only claimable by players who own no buildings (1.2 "무주택자 전용" groups 12/13).
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

    /// <summary>Placement-rule alias for <see cref="Houseless"/> (houseless-only zone type).</summary>
    public bool HouselessOnly => Houseless;
}
