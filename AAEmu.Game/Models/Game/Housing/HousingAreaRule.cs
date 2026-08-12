namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// Polygon-level placement rule for one pak <c>housing_area.xml</c> AreaShape, joined to
/// <c>housing_areas</c> by the shape's entity name (<c>housing_areas.comments</c> =
/// <c>LevelDesignShape_&lt;zoneKey&gt;_&lt;name&gt;_&lt;n&gt;</c>).
///
/// Canonical 1.2 placement is per-polygon: a design may be placed only inside a shape whose
/// housing group allows its category, with a per-(group, category) construction cap
/// (<c>max_construct_count</c>, 0 = unlimited) and the group's houseless-only gate.
/// </summary>
public class HousingAreaRule
{
    /// <summary>Shape entity name (<c>housing_areas.comments</c> join key).</summary>
    public string ShapeName { get; init; }

    public uint HousingGroupId { get; init; }

    /// <summary>Categories allowed by the shape's housing group (housing_group_categories).</summary>
    public HashSet<uint> AllowedCategories { get; init; } = [];

    /// <summary>Per-category construction cap for this shape's group (0 = unlimited).</summary>
    public Dictionary<uint, int> MaxConstructCounts { get; init; } = [];

    /// <summary>True when the shape's group is houseless-only (1.2 groups 12/13).</summary>
    public bool HouselessOnly { get; init; }
}
