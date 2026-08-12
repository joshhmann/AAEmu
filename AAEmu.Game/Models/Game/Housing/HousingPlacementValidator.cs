using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Housing;

public enum HousingPlacementError : byte
{
    None = 0,

    /// <summary>Zone is not a land zone, or zone-type rules reject the placement (faction, category, houseless-only).</summary>
    InvalidArea = 1,

    /// <summary>Position overlaps an existing house (garden spacing).</summary>
    OverlapHouse = 2,

    /// <summary>Position overlaps a spawned unit/NPC (canonical 114 house_cannot_locate_overlap_unit).</summary>
    OverlapUnit = 3,

    /// <summary>Ground is higher than the template's terrain band allows (canonical 115 terrain_too_high).</summary>
    TerrainTooHigh = 4,

    /// <summary>Ground is lower than the template's terrain band allows (canonical 116 terrain_too_low).</summary>
    TerrainTooLow = 5,

    /// <summary>Position is inside a land zone but not inside any housing-area polygon (canonical 229 no_housing_area).</summary>
    NoHousingArea = 6,

    /// <summary>The area's per-category construction cap is reached (canonical 766 max_construct_count).</summary>
    MaxConstructCount = 7,
}

/// <summary>
/// Pure homestead placement / ownership / permission rules for the real engine paths
/// (HousingManager.Build / ConstructHouseTax / House.AllowedToInteract / owner-only ops).
///
/// No singleton dependencies — all inputs are passed in, so the scenario rig exercises the
/// exact engine rules without booting managers. Rules are grounded in the canonical 1.2
/// data: housing_areas/housing_groups/housing_group_categories (zone type rules) and the
/// housing template garden radii (overlap spacing).
/// </summary>
public static class HousingPlacementValidator
{
    /// <summary>
    /// Minimum horizontal spacing between house centers. Templates with placeholder data
    /// carry GardenRadius 0, so a floor keeps zero-radius designs from stacking on one spot.
    /// Real 1.2 houses (small 7.5, mansion 22) never hit this floor.
    /// </summary>
    public const float MinHouseSpacing = 5f;

    /// <summary>
    /// Full placement validation for a house design at a position, in evaluation order.
    /// Returns <see cref="HousingPlacementError.None"/> when the placement is accepted.
    /// </summary>
    public static HousingPlacementError ValidatePlacement(
        HousingLandZoneInfo landZone,
        FactionsEnum zoneFaction,
        HousingTemplate template,
        Vector3 position,
        FactionsEnum characterFaction,
        bool characterOwnsHouses,
        IReadOnlyCollection<House> existingHouses)
    {
        if (template == null)
            return HousingPlacementError.InvalidArea;

        var error = ValidateLandZone(landZone);
        if (error != HousingPlacementError.None)
            return error;

        error = ValidateFaction(zoneFaction, characterFaction);
        if (error != HousingPlacementError.None)
            return error;

        error = ValidateHouselessOnly(landZone, characterOwnsHouses);
        if (error != HousingPlacementError.None)
            return error;

        error = ValidateCategory(landZone, template);
        if (error != HousingPlacementError.None)
            return error;

        return ValidateOverlap(position, template, existingHouses);
    }

    /// <summary>Zone must be a known land zone (carries 1.2 housing areas).</summary>
    public static HousingPlacementError ValidateLandZone(HousingLandZoneInfo landZone)
        => landZone == null ? HousingPlacementError.InvalidArea : HousingPlacementError.None;

    /// <summary>
    /// Zone faction gate: faction-owned zones (NuiaAlliance 148 / HaranyaAlliance 149) may
    /// only be claimed by characters of that faction; neutral (unclaimed) zones are open.
    /// </summary>
    public static HousingPlacementError ValidateFaction(FactionsEnum zoneFaction, FactionsEnum characterFaction)
    {
        if (zoneFaction == FactionsEnum.Invalid || zoneFaction == characterFaction)
            return HousingPlacementError.None;
        return HousingPlacementError.InvalidArea;
    }

    /// <summary>Houseless-only zone types (1.2 groups 12/13) reject owners who already hold a house.</summary>
    public static HousingPlacementError ValidateHouselessOnly(HousingLandZoneInfo landZone, bool characterOwnsHouses)
    {
        if (landZone != null && landZone.IsHouselessOnly && characterOwnsHouses)
            return HousingPlacementError.InvalidArea;
        return HousingPlacementError.None;
    }

    /// <summary>
    /// Zone-type rule: the design's house category must be allowed by at least one of the
    /// zone's housing groups (housing_group_categories). A zone whose groups allow no
    /// categories ("nothing can be built" — 1.2 group 11) rejects everything.
    /// </summary>
    public static HousingPlacementError ValidateCategory(HousingLandZoneInfo landZone, HousingTemplate template)
    {
        if (landZone == null || landZone.AllowedCategories.Count == 0 || !landZone.AllowedCategories.Contains(template.CategoryId))
            return HousingPlacementError.InvalidArea;
        return HousingPlacementError.None;
    }

    /// <summary>
    /// Overlap check: horizontal distance from every existing house center must be at least
    /// the sum of the two garden radii (gardens must not overlap), floored at
    /// <see cref="MinHouseSpacing"/> for zero-radius placeholder templates.
    /// </summary>
    public static HousingPlacementError ValidateOverlap(Vector3 position, HousingTemplate template, IReadOnlyCollection<House> existingHouses)
    {
        if (template == null)
            return HousingPlacementError.InvalidArea;

        foreach (var house in existingHouses)
        {
            if (house?.Transform == null)
                continue;

            var otherRadius = house.Template?.GardenRadius ?? 0f;
            var required = MathF.Max(template.GardenRadius + otherRadius, MinHouseSpacing);

            var dx = position.X - house.Transform.World.Position.X;
            var dy = position.Y - house.Transform.World.Position.Y;

            if (dx * dx + dy * dy < required * required)
                return HousingPlacementError.OverlapHouse;
        }

        return HousingPlacementError.None;
    }

    /// <summary>
    /// Polygon-level placement validation, layered on top of the zone-level fast path
    /// (<see cref="ValidatePlacement"/>). Enforces the canonical 1.2 placement rules that
    /// operate per pak AreaShape polygon (housing_area.xml, joined via housing_areas.comments):
    /// <list type="number">
    /// <item>Point-in-polygon containment — outside every shape → 229 no_housing_area.</item>
    /// <item>Per-shape housing group rules: category + houseless gate → 112 invalid_area;
    /// per-(group, category) max_construct_count → 766.</item>
    /// <item>Unit/NPC overlap → 114 overlap_unit.</item>
    /// <item>Terrain band (extra_height_above/below vs ground height) → 115/116.</item>
    /// </list>
    /// Returns <see cref="HousingPlacementError.None"/> when the polygon layer cannot run
    /// (no shapes loaded — e.g. client pak unavailable); the zone-level checks still gate.
    /// </summary>
    /// <param name="zoneShapes">Pak AreaShapes for the zone (world coords), or empty when unavailable.</param>
    /// <param name="ruleResolver">Resolves a shape entity name to its housing_areas rule (comments join).</param>
    /// <param name="template">House design template.</param>
    /// <param name="position">Placement position.</param>
    /// <param name="characterOwnsHouses">Whether the character already owns a house (houseless-only gate).</param>
    /// <param name="groundHeight">Ground height at the position, or null when no height data is available (terrain checks skipped).</param>
    /// <param name="unitPositions">Spawned unit/NPC positions near the placement (may be empty).</param>
    /// <param name="existingHouses">All placed houses (used for the per-area max_construct_count).</param>
    public static HousingPlacementError ValidatePolygonPlacement(
        IReadOnlyList<Area> zoneShapes,
        Func<string, HousingAreaRule> ruleResolver,
        HousingTemplate template,
        Vector3 position,
        bool characterOwnsHouses,
        float? groundHeight,
        IReadOnlyCollection<Vector3> unitPositions,
        IReadOnlyCollection<House> existingHouses)
    {
        if (template == null || zoneShapes == null || zoneShapes.Count == 0)
            return HousingPlacementError.None;

        // 1. Point-in-polygon containment against every shape of the zone.
        var containingShapes = zoneShapes
            .Where(s => s != null && s.Points != null && Point.IsInside(s.Points, s.Points.Count, new Vector3(position.X, position.Y, 0)))
            .ToList();
        if (containingShapes.Count == 0)
            return HousingPlacementError.NoHousingArea;

        // Resolve the buildable rules for the containing shapes. A shape without a
        // housing_areas row (deleted/legacy shape) grants nothing.
        var containingRules = containingShapes
            .Select(s => ruleResolver?.Invoke(s.Name))
            .Where(r => r != null)
            .ToList();
        if (containingRules.Count == 0)
            return HousingPlacementError.NoHousingArea;

        // 2. Per-shape group rules: category + houseless gate, then max_construct_count.
        //    The position may sit inside several overlapping shapes; any shape that accepts
        //    the design makes the placement legal (the player picked that area).
        var acceptingRules = containingRules
            .Where(r => !(r.HouselessOnly && characterOwnsHouses) && r.AllowedCategories.Contains(template.CategoryId))
            .ToList();
        if (acceptingRules.Count == 0)
            return HousingPlacementError.InvalidArea;

        var shapeByRule = containingShapes
            .Where(s => s != null)
            .ToDictionary(s => s.Name);

        var anyCapacity = acceptingRules.Any(rule =>
        {
            if (!rule.MaxConstructCounts.TryGetValue(template.CategoryId, out var maxCount) || maxCount <= 0)
                return true; // unlimited
            if (!shapeByRule.TryGetValue(rule.ShapeName, out var shape))
                return true;

            var existingInShape = existingHouses.Count(h =>
                h?.Template != null && h.Template.CategoryId == template.CategoryId &&
                h.Transform != null &&
                Point.IsInside(shape.Points, shape.Points.Count,
                    new Vector3(h.Transform.World.Position.X, h.Transform.World.Position.Y, 0)));
            return existingInShape < maxCount;
        });
        if (!anyCapacity)
            return HousingPlacementError.MaxConstructCount;

        // 3. Unit/NPC overlap: the plot's garden circle must be free of spawned units.
        if (unitPositions != null && unitPositions.Count > 0)
        {
            var required = MathF.Max(template.GardenRadius, MinHouseSpacing);
            foreach (var unitPosition in unitPositions)
            {
                var dx = position.X - unitPosition.X;
                var dy = position.Y - unitPosition.Y;
                if (dx * dx + dy * dy < required * required)
                    return HousingPlacementError.OverlapUnit;
            }
        }

        // 4. Terrain band: ground must sit within [base - ExtraHeightBelow, base + ExtraHeightAbove].
        if (groundHeight.HasValue)
        {
            if (groundHeight.Value - position.Z > template.ExtraHeightAbove)
                return HousingPlacementError.TerrainTooHigh;
            if (position.Z - groundHeight.Value > template.ExtraHeightBelow)
                return HousingPlacementError.TerrainTooLow;
        }

        return HousingPlacementError.None;
    }

    /// <summary>Ownership rule: only the owner may manage the house (rename, permission, sell, demolish).</summary>
    public static bool CanManage(House house, uint characterId)
        => house != null && house.OwnerId == characterId;

    /// <summary>
    /// Permission model (who can interact with what). Mirrors the 1.2 HousingPermission
    /// model: Private (owner + same-account alts) / Family (family members) / Guild
    /// (expedition members) / Public (everyone). Always-public templates and unfinished
    /// houses are always interactable.
    ///
    /// Fixes the upstream fall-through: players WITHOUT a family (or without an expedition)
    /// previously matched the `when`-guard failure and fell into the Public branch — a
    /// Family/Guild-locked house was open to everyone outside the family/guild.
    /// </summary>
    public static bool CanInteract(
        House house,
        Character player,
        INameManager nameManager,
        Func<uint, Family> familyResolver)
    {
        if (house?.Template?.AlwaysPublic == true)
            return true;

        if (house.CurrentStep != -1) // unfinished houses can't be used to private store, so always true
            return true;

        switch (house.Permission)
        {
            case HousingPermission.Private:
                if (player.Id == house.OwnerId)
                    return true;
                var ownerAccountId = nameManager.GetCharacterAccount(house.OwnerId);
                return player.AccountId == ownerAccountId;
            case HousingPermission.Family:
                if (player.Family == 0)
                    return false;
                return familyResolver(player.Family)?.Members.Any(x => x.Id == house.OwnerId) == true;
            case HousingPermission.Guild:
                if (player.Expedition == null || player.Expedition.Id == 0)
                    return false;
                return player.Expedition.Members.Any(x => x.CharacterId == house.OwnerId);
            case HousingPermission.Public:
            default:
                return true;
        }
    }
}
