namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// A housing zone type (1.2 <c>housing_groups</c> table): general residential, small/medium
/// house areas, scarecrow gardens, ocean/water housing, homeless-only zones, etc.
/// Placement rules for a zone type are expressed by its <c>housing_group_categories</c>
/// (which house categories may be built) and the <c>houseless</c> flag (zone only claimable
/// by players who own no buildings — 1.2 "무주택자 전용" zones).
/// </summary>
public class HousingGroup
{
    public uint Id { get; init; }

    public string Name { get; init; }

    /// <summary>
    /// <c>houseless</c> flag — this zone type may only be claimed by players who do not
    /// own any building (1.2 groups 12/13).
    /// </summary>
    public bool HouselessOnly { get; init; }

    public bool CanExtend { get; init; }
}
