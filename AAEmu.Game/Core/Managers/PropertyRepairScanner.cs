namespace AAEmu.Game.Core.Managers;

// ---------------------------------------------------------------------------
// Property repair tooling (M3b-4, t_7c71be66): administrative repair for
// corrupted / lost property state. The scanner below is PURE — it operates on
// a state view (rows + template/character existence), so every rule is
// unit-testable without a live MySQL. The service layer (PropertyRepairService)
// loads the view from the DB and applies fixes.
// ---------------------------------------------------------------------------

/// <summary>Minimal housings row projection the scanner needs.</summary>
public sealed record HouseRow(
    uint Id,
    uint AccountId,
    uint OwnerId,
    uint TemplateId,
    float X,
    float Y,
    float Z,
    int CurrentStep,
    int CurrentAction);

/// <summary>Minimal persistent-doodad row projection (doodads table).</summary>
public sealed record DoodadRow(
    uint Id,
    uint OwnerId,
    byte OwnerType,
    uint HouseId);

/// <summary>
/// Immutable snapshot of property state for scanning. Existence sets are
/// resolved by the caller (HousingGameData templates, characters table).
/// </summary>
public sealed record PropertyStateView(
    IReadOnlyList<HouseRow> Houses,
    IReadOnlyList<DoodadRow> Doodads,
    IReadOnlySet<uint> ExistingTemplateIds,
    IReadOnlySet<uint> ExistingCharacterIds,
    IReadOnlyDictionary<uint, int> TemplateBuildStepCounts);

public enum PropertyRepairIssueKind
{
    /// <summary>housings.template_id not in HousingGameData — the house can never load; a boot NRE risk.</summary>
    InvalidTemplateHouse,

    /// <summary>housings.owner references a deleted character — lost ownership.</summary>
    OrphanedOwnerHouse,

    /// <summary>Persistent doodad whose house_id references a non-existent housings row (bound furniture of a demolished house).</summary>
    OrphanedBoundDoodad,

    /// <summary>Persistent doodad owned by a deleted character.</summary>
    OrphanedDoodadOwner,

    /// <summary>Two+ housings rows with the same owner+template+position — duplication on re-entry.</summary>
    DuplicateHouse,

    /// <summary>current_step / current_action outside the template's build steps.</summary>
    OutOfRangeBuildStep
}

/// <summary>One scanner finding. Fix is a suggestion; the service decides what to apply.</summary>
public sealed record PropertyRepairIssue(PropertyRepairIssueKind Kind, uint TargetId, string Detail);

/// <summary>
/// Pure property-state corruption scanner (M3b-4). No DB, no singletons —
/// feed it a <see cref="PropertyStateView"/> and get issues back. Every rule
/// maps 1:1 to a repair action in <see cref="PropertyRepairService"/>.
/// </summary>
public static class PropertyRepairScanner
{
    /// <summary>DoodadOwnerType.Character from the game model (kept as a constant to stay DB-side).</summary>
    public const byte DoodadOwnerTypeCharacter = 254;

    /// <summary>Owner 0 = system/NPC-owned (e.g. the seed lodestones) — never "orphaned".</summary>
    private const uint SystemOwnerId = 0;

    public static IReadOnlyList<PropertyRepairIssue> Scan(PropertyStateView view)
    {
        var issues = new List<PropertyRepairIssue>();

        var houseIds = new HashSet<uint>();
        foreach (var house in view.Houses)
        {
            houseIds.Add(house.Id);

            // 1. Invalid template: can never load, and LoadPlayerHousing NREs
            //    on the null Create() result — a boot-blocker.
            if (!view.ExistingTemplateIds.Contains(house.TemplateId))
            {
                issues.Add(new PropertyRepairIssue(
                    PropertyRepairIssueKind.InvalidTemplateHouse, house.Id,
                    $"template_id {house.TemplateId} not in HousingGameData — house can never load"));
                continue;
            }

            // 6. Build step out of range (only checkable when the template exists).
            if (view.TemplateBuildStepCounts.TryGetValue(house.TemplateId, out var stepCount))
            {
                if (house.CurrentStep < -1 || house.CurrentStep >= stepCount || house.CurrentAction < 0)
                {
                    issues.Add(new PropertyRepairIssue(
                        PropertyRepairIssueKind.OutOfRangeBuildStep, house.Id,
                        $"current_step {house.CurrentStep} / current_action {house.CurrentAction} outside template {house.TemplateId} build range (steps {stepCount})"));
                }
            }
        }

        // 2. Orphaned owner (owner > 0 means player-owned).
        foreach (var house in view.Houses)
        {
            if (house.OwnerId > SystemOwnerId && !view.ExistingCharacterIds.Contains(house.OwnerId))
            {
                issues.Add(new PropertyRepairIssue(
                    PropertyRepairIssueKind.OrphanedOwnerHouse, house.Id,
                    $"owner character {house.OwnerId} no longer exists"));
            }
        }

        // 3. Duplicate houses: same owner + template + position (within 0.5m).
        var seen = new Dictionary<(uint Owner, uint Template, long X, long Y, long Z), uint>();
        foreach (var house in view.Houses.OrderBy(h => h.Id))
        {
            var key = (house.OwnerId, house.TemplateId,
                (long)Math.Round(house.X * 2), (long)Math.Round(house.Y * 2), (long)Math.Round(house.Z * 2));
            if (seen.TryGetValue(key, out var firstId))
            {
                issues.Add(new PropertyRepairIssue(
                    PropertyRepairIssueKind.DuplicateHouse, house.Id,
                    $"duplicate of house {firstId} (same owner {house.OwnerId} + template {house.TemplateId} at same position)"));
            }
            else
            {
                seen[key] = house.Id;
            }
        }

        // 4/5. Orphaned doodads: house_id dangles, or Character-owned doodad of a deleted character.
        foreach (var doodad in view.Doodads)
        {
            if (doodad.HouseId > 0 && !houseIds.Contains(doodad.HouseId))
            {
                issues.Add(new PropertyRepairIssue(
                    PropertyRepairIssueKind.OrphanedBoundDoodad, doodad.Id,
                    $"bound to house {doodad.HouseId} which no longer exists"));
            }
            else if (doodad.OwnerType == DoodadOwnerTypeCharacter &&
                     doodad.OwnerId > SystemOwnerId &&
                     !view.ExistingCharacterIds.Contains(doodad.OwnerId))
            {
                issues.Add(new PropertyRepairIssue(
                    PropertyRepairIssueKind.OrphanedDoodadOwner, doodad.Id,
                    $"owned by deleted character {doodad.OwnerId}"));
            }
        }

        return issues;
    }
}
