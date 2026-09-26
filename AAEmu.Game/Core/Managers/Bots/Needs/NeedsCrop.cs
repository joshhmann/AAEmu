#nullable enable

using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.Game.Core.Managers.Bots.Needs;

/// <summary>
/// THE SINGLE CROP RULE — what counts as OUR needs-farm crop, whether a tracked
/// crop is still ours, and whether it is mature. Moved out of
/// <c>BotRoamStepExecutor</c> (its <c>IsNeedsFarmCrop</c>,
/// <c>IsTrackedCropLive</c> and <c>IsCropMature</c>) so the existing needs-farm
/// leg and the needs BRAIN's planner answer every crop question by the same rule,
/// never a second copy that could drift.
///
/// The ownership rule is unchanged: directly character-owned, or house-bound where
/// the house allows interaction, or a system-owned crop on public-farm soil after
/// the public-farm tick cleared its owner. The maturity read is deliberately NOT a
/// second maturity model — it is the SAME data-driven harvestability the Harvest
/// engine path resolves (<c>M3aM4ReplayScenario.TryGetHarvestSkill</c>), so the
/// engine stays the sole maturity authority at dispatch.
/// </summary>
public static class NeedsCrop
{
    /// <summary>
    /// Tier 0 crop rule: directly character-owned by the bot, or house-bound where
    /// the house allows interaction (the <c>FarmerCycleScenario</c> owned-plot
    /// precedent). System-owned crops on public-farm soil are also accepted after
    /// <c>PublicFarmTick</c> expiry clears their owner. Maturity stays the engine's
    /// own fail-closed gate at dispatch.
    /// </summary>
    public static bool IsOurs(Doodad doodad, Character character)
    {
        if (doodad.OwnerType == DoodadOwnerType.Character)
            return doodad.OwnerId == character.Id;
        if (doodad.OwnerType == DoodadOwnerType.Housing)
            return HousingManager.Instance.GetHouseById(doodad.OwnerDbId)?.AllowedToInteract(character) == true;
        if (doodad.OwnerType != DoodadOwnerType.System && doodad.OwnerId != 0)
            return false;

        var world = doodad.ParentWorld;
        if (world == null)
            return false;
        var position = doodad.Transform.World.Position;
        if (!PublicFarmManager.Instance.InPublicFarm(world.Template, position) ||
            PublicFarmManager.IsProtected(doodad))
            return false;

        var farmType = PublicFarmManager.Instance.GetFarmType(world, position);
        return CommonFarmGameData.Instance.GetAllowedDoodads(farmType).Contains(doodad.TemplateId);
    }

    /// <summary>
    /// Tracked-crop liveness: null/gone, despawn-scheduled, template-swapped, or no
    /// longer ours (ownership change) all read stale. Maturity stays the engine's
    /// gate — this only decides whether the track is OURS.
    /// </summary>
    public static bool IsTrackedLive(Doodad? tracked, Character character, uint expectedTemplateId)
    {
        if (tracked == null || tracked.Despawn > DateTime.MinValue)
            return false;
        if (expectedTemplateId != 0 && tracked.TemplateId != expectedTemplateId)
            return false;
        try
        {
            return IsOurs(tracked, character);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Maturity read: the same data-driven harvestability the Harvest engine path
    /// resolves (the current phase carries a loot-linked interaction). Never
    /// mutates — a probe, not a transition.
    /// </summary>
    public static bool IsMature(Doodad doodad)
    {
        try
        {
            return M3aM4ReplayScenario.TryGetHarvestSkill(doodad, out _);
        }
        catch
        {
            return false;
        }
    }
}
