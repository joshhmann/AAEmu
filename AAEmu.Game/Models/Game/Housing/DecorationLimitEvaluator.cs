using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;

namespace AAEmu.Game.Models.Game.Housing;

/// <summary>
/// Server-side decoration-limit enforcement for houses.
///
/// Canonical 1.2 rules (compact.sqlite3):
/// - <c>housings.absolute_deco_limit</c>: hard cap on the total number of decorations
///   (furniture) a house may carry — enforced first, client shows "house_too_many_decorations".
/// - <c>housings.housing_deco_limit_id</c> → <c>housing_deco_limits</c> →
///   <c>housing_deco_limit_elems</c>: per-actability-group allowances
///   (e.g. limit group 1 = actability group 1 x3 + group 5 x2). Enforced when the
///   placed design carries a deco actability group — client shows
///   "housing_actability_deco_limited".
/// - <c>housings.deco_limit</c>: backstop cap on the total number of actability-grouped
///   decorations (uniform 40 in canonical data; the per-group elems are the primary rule).
///
/// Pure evaluator: all data access goes through the two lookup delegates so the rules
/// are unit-testable without engine singletons. The production caller
/// (<see cref="AAEmu.Game.Core.Managers.HousingManager.DecorateHouse"/>) wires the
/// delegates to HousingGameData.
/// </summary>
public static class DecorationLimitEvaluator
{
    /// <summary>
    /// Checks whether a new decoration may be placed in a house given its current decorations.
    /// </summary>
    /// <param name="template">The house's housing template (holds the limit values).</param>
    /// <param name="newDesign">Decoration design of the item being placed.</param>
    /// <param name="existingDecorations">All doodads currently owned by the house (incl. attached doors/windows, which are skipped).</param>
    /// <param name="designLookup">Doodad template id → decoration design (null when the doodad is not a housing decoration).</param>
    /// <param name="groupLimitLookup">(housingDecoLimitId, decoActabilityGroupId) → allowed count (0 = no group limit).</param>
    /// <param name="error">Error message when rejected, <see cref="ErrorMessageType.NoErrorMessage"/> when allowed.</param>
    /// <returns>True when the decoration is within every limit.</returns>
    public static bool IsDecorationAllowed(
        HousingTemplate template,
        HousingDecoration newDesign,
        IReadOnlyCollection<Doodad> existingDecorations,
        Func<uint, HousingDecoration> designLookup,
        Func<uint, uint, int> groupLimitLookup,
        out ErrorMessageType error)
    {
        error = ErrorMessageType.NoErrorMessage;

        // Attached doodads (doors/windows etc.) are house structure, not player decorations.
        var totalCount = 0;
        var groupedTotal = 0;
        var countByGroup = new Dictionary<uint, int>();

        foreach (var doodad in existingDecorations)
        {
            if (doodad.AttachPoint != AttachPointKind.None)
                continue;

            totalCount++;

            var design = designLookup(doodad.TemplateId);
            if (design == null || design.DecoActAbilityGroupId <= 0)
                continue;

            groupedTotal++;
            countByGroup.TryGetValue(design.DecoActAbilityGroupId, out var c);
            countByGroup[design.DecoActAbilityGroupId] = c + 1;
        }

        // Hard cap on everything the house can hold.
        if (template.AbsoluteDecoLimit > 0 && totalCount >= template.AbsoluteDecoLimit)
        {
            error = ErrorMessageType.HouseTooManyDecorations;
            return false;
        }

        // Per-actability-group allowance for the new design.
        if (newDesign.DecoActAbilityGroupId > 0)
        {
            var groupCount = countByGroup.GetValueOrDefault(newDesign.DecoActAbilityGroupId);

            var groupLimit = groupLimitLookup(template.HousingDecoLimitId, newDesign.DecoActAbilityGroupId);
            if (groupLimit > 0 && groupCount >= groupLimit)
            {
                error = ErrorMessageType.HousingActabilityDecoLimited;
                return false;
            }

            // Backstop: total number of actability-grouped decorations.
            if (template.DecoLimit > 0 && groupedTotal >= template.DecoLimit)
            {
                error = ErrorMessageType.HousingActabilityDecoLimited;
                return false;
            }
        }

        return true;
    }
}
