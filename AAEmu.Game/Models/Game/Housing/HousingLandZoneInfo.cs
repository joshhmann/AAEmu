namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// Zone-level homestead land info, derived from 1.2 data: <c>housing_areas</c> joined to
/// <c>housing_groups</c> (+ <c>housing_group_categories</c>) by area name, where the area name
/// equals a world zone name (e.g. zone "w_solzreed_1" contains areas named "w_solzreed_1").
///
/// The server has no positional plot shapes — 1.2 plot boundaries are client geodata
/// (LevelDesignShape ids in the <c>comments</c> column), so placement rules are enforced at
/// ZONE granularity: a zone is a land zone iff it carries housing areas, and the zone's
/// allowed house categories + houseless rule are the union over its areas' housing groups.
/// </summary>
public class HousingLandZoneInfo
{
    public string ZoneName { get; init; }

    /// <summary>The housing groups present in this zone (each area belongs to one group).</summary>
    public List<HousingGroup> Groups { get; } = [];

    /// <summary>Union of house categories allowed anywhere in this zone (housing_group_categories).</summary>
    public HashSet<uint> AllowedCategories { get; } = [];

    /// <summary>
    /// True when any area in this zone belongs to a houseless-only housing group
    /// (1.2 "무주택자 전용" — only players owning no buildings may claim there).
    /// Conservative zone-level interpretation: the rule applies to the whole zone.
    /// </summary>
    public bool IsHouselessOnly => Groups.Any(g => g.HouselessOnly);

    /// <summary>
    /// Builds the zone-name → land-zone map from the raw 1.2 tables. Areas whose name does
    /// not match a world zone (LevelDesignShape names like "142solzreed") simply never get
    /// looked up — harmless entries.
    /// </summary>
    public static Dictionary<string, HousingLandZoneInfo> BuildFromData(
        IEnumerable<HousingAreas> areas,
        IReadOnlyDictionary<uint, HousingGroup> groups,
        IReadOnlyDictionary<uint, HashSet<uint>> groupCategories)
    {
        var result = new Dictionary<string, HousingLandZoneInfo>();

        foreach (var area in areas)
        {
            if (area == null || string.IsNullOrEmpty(area.Name))
                continue;
            if (!groups.TryGetValue(area.GroupId, out var group))
                continue;

            if (!result.TryGetValue(area.Name, out var landZone))
            {
                landZone = new HousingLandZoneInfo { ZoneName = area.Name };
                result.Add(area.Name, landZone);
            }

            if (!landZone.Groups.Contains(group))
                landZone.Groups.Add(group);

            if (groupCategories.TryGetValue(group.Id, out var categories))
                landZone.AllowedCategories.UnionWith(categories);
        }

        return result;
    }
}
