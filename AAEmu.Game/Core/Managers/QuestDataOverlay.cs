using AAEmu.Game.Models.Game.Quests;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Additive data overlay for quest templates (upstream alignment rule 3:
/// compact.sqlite3 is a READ-ONLY reference — data corrections land here as an
/// in-memory overlay at load time, NEVER by editing the reference file).
///
/// Current rows: the 3 cosmetic COMPONENT_NEXT_MISSING corrections from
/// data-defects.md §3 (verified against the canonical 1.2 compact.sqlite3,
/// prod md5 78b3bdbf0383db3b927056106efdf91af):
///   quest 330 (zone 125, golden route): comp 1520 next 3543 -> 1521 (Ready)
///   quest 776 (zone 8):                  comp 3480 next 4370 -> 3482 (Progress)
///   quest 777 (zone 8):                  comp 3488 next 3487 -> 11591 (Ready)
/// Each target exists among its quest's own components (verified 2026-08-04).
///
/// Policy (mirrors the sanity verifier): the overlay NEVER throws. A row whose
/// component id vanished from the reference is logged as a Warn by the caller
/// (data drift signal), the remaining rows still apply.
/// </summary>
public static class QuestDataOverlay
{
    /// <summary>next_component corrections, keyed by quest_component id.</summary>
    public static readonly IReadOnlyDictionary<uint, uint> NextComponentFixes = new Dictionary<uint, uint>
    {
        [1520] = 1521, // quest 330: 3543 exists in no quest -> 1521
        [3480] = 3482, // quest 776: 4370 exists in no quest -> 3482
        [3488] = 11591 // quest 777: 3487 exists in no quest -> 11591
    };

    public sealed record OverlayResult(int Applied, IReadOnlyList<uint> MissingComponentIds);

    /// <summary>
    /// Applies the overlay to the loaded component templates. Returns how many rows
    /// were applied and which component ids were missing (drift). Never throws.
    /// </summary>
    public static OverlayResult Apply(Dictionary<uint, QuestComponentTemplate> componentTemplates)
    {
        var applied = 0;
        var missing = new List<uint>();

        foreach (var (componentId, nextComponent) in NextComponentFixes)
        {
            if (!componentTemplates.TryGetValue(componentId, out var component))
            {
                missing.Add(componentId);
                continue;
            }

            component.NextComponent = nextComponent;
            applied++;
        }

        return new OverlayResult(applied, missing);
    }
}
