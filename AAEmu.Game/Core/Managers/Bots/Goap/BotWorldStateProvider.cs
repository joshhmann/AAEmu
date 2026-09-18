#nullable enable

using System.Numerics;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Live sensing adapter projecting rich AAEmu engine state, character vitals, inventory,
/// and spatial relationships into the compact 64-bit BotWorldState.
///
/// Invariant: BotWorldState is a compressed planning projection of reality, not reality itself.
/// Detailed spatial targets, entity IDs, and timestamps remain in BotContext / BotMemory.
/// </summary>
public sealed class BotWorldStateProvider : IBotWorldStateProvider
{
    /// <summary>Canonical potato seed item id (15659, 감자 씨앗).</summary>
    public const uint DefaultSeedTemplateId = 15659;
    public const uint DefaultSaplingTemplateId = DefaultSeedTemplateId;
    public const uint DefaultFoodTemplateId = 8219;

    /// <summary>
    /// Straw Hat Scarecrow Garden design (15596) — the single design whose
    /// item_housings row maps to housing 267, the plot this bot claims/builds.
    /// Shares the action layer's constant so the two cannot drift.
    /// </summary>
    public const uint AcquireScarecrowDesignTemplateId = Actions.AcquireScarecrowAction.ScarecrowDesignTemplateId;

    /// <summary>
    /// Flat distance at which the character is considered inside its target's
    /// engagement band. Uses the engine-side melee reach
    /// (<see cref="CombatDecisionTree.DefaultMeleeMax"/>) rather than a local guess.
    /// </summary>
    public const float TargetEngagementRange = CombatDecisionTree.DefaultMeleeMax;

    /// <summary>
    /// Free inventory slots, or null when bag fullness cannot be established
    /// (no inventory, no bag, or an unlimited container).
    /// </summary>
    private static int? ReadBagFreeSlots(Character ch)
    {
        try
        {
            var bag = ch.Inventory?.Bag;
            if (bag == null)
                return null;
            // ContainerSize < 0 is the engine's "unlimited" container: fullness is
            // not a meaningful question, so the caller must not read it as full.
            return bag.ContainerSize < 0 ? null : bag.FreeSlotCount;
        }
        catch
        {
            return null;
        }
    }

    public BotWorldState Project(PlayerBotRuntime bot, BotContext context)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(context);

        var ch = bot.Character;
        ulong flags = BotWorldState.None;
        // Flags whose truth the live read cannot establish are cleared from the mask,
        // so Satisfies()/planner goals treat them as unspecified instead of false.
        ulong mask = ulong.MaxValue;

        int maxHp = 1000;
        int maxMp = 500;
        try { maxHp = ch.MaxHp > 0 ? ch.MaxHp : 1000; } catch { maxHp = 1000; }
        try { maxMp = ch.MaxMp > 0 ? ch.MaxMp : 500; } catch { maxMp = 500; }

        // 1. Vital & Combat status
        if (ch.Hp < maxHp * 0.5f)
            flags |= BotWorldState.LowHealth;

        if (ch.Mp < maxMp * 0.4f)
            flags |= BotWorldState.LowMana;

        if (ch.Hp >= maxHp * 0.95f && ch.Mp >= maxMp * 0.95f)
            flags |= BotWorldState.Recovered;

        if (ch.IsInBattle)
            flags |= BotWorldState.InCombat;

        if (ch.Stance == UnitStance.Sit)
            flags |= BotWorldState.IsSitting;

        // 2. Inventory status (checks real bag or test overrides)
        bool hasFood = context.Memory.HasFoodOverride ??
            (ch.Inventory != null && ch.Inventory.Bag.Items.Any(i => i != null && i.TemplateId == DefaultFoodTemplateId));
        if (hasFood)
            flags |= BotWorldState.HasEdibleFood;

        bool hasSaplings = context.Memory.HasSaplingsOverride ??
            (ch.Inventory != null && ch.Inventory.Bag.Items.Any(i => i != null && i.TemplateId == DefaultSaplingTemplateId));
        if (hasSaplings)
            flags |= BotWorldState.HasTreeSaplings;

        if (context.Memory.HasActiveGroves)
            flags |= BotWorldState.SecretGrovePlanted;

        // Bag fullness is REAL bag capacity (ItemContainer.FreeSlotCount), never grove
        // maturity: a mature grove is a harvest *opportunity*, not a full bag.
        // An indeterminable bag (absent inventory/container, or an unlimited -1
        // container size) reports false AND is left out of the mask so no consumer
        // can read a fabricated "full".
        var bagFreeSlots = ReadBagFreeSlots(ch);
        if (bagFreeSlots is 0)
            flags |= BotWorldState.BagFull;
        else if (bagFreeSlots is null)
            mask &= ~BotWorldState.BagFull;

        // 3. Spatial & POI Proximity
        var botPos = ch.Transform?.World?.Position ?? Vector3.Zero;

        if (context.Memory.KnownSeedMerchantPos.HasValue)
        {
            var distMerchant = Vector3.Distance(botPos, context.Memory.KnownSeedMerchantPos.Value);
            if (distMerchant <= 15.0f)
                flags |= BotWorldState.NearSeedMerchant;
        }

        if (context.Memory.TargetWildFarmPos.HasValue && !context.Memory.TargetWildFarmPoiInvalidated)
        {
            var distFarm = Vector3.Distance(botPos, context.Memory.TargetWildFarmPos.Value);
            if (distFarm <= 15.0f)
                flags |= BotWorldState.AtWildFarm;
        }

        // 3b. Homestead & Land ownership projection
        // Straw Hat Scarecrow Garden design only. Grounded in compact.sqlite3
        // item_housings: design template 15596 -> housing 267 ("밀짚모자 허수아비 텃밭"),
        // the house AcquireScarecrowAction/SurveyAndPlacePlotAction build. Template
        // 15566 is a DIFFERENT design (-> housing 89, 호박머리 허수아비 텃밭) and must
        // not satisfy this flag: the bot would claim design-267 ownership without it.
        bool hasScarecrowDesign = context.Memory.HasScarecrowDesignOverride ??
            (ch.Inventory?.Bag?.GetItemsSnapshot().Any(i => i != null && i.TemplateId == AcquireScarecrowDesignTemplateId) ?? false);
        if (hasScarecrowDesign)
            flags |= BotWorldState.HasScarecrowDesign;

        bool hasTaxCert = context.Memory.HasTaxCertificatesOverride ??
            (ch.Inventory?.Bag?.GetItemsSnapshot().Any(i => i != null && (i.TemplateId == Item.TaxCertificate || i.TemplateId == Item.BoundTaxCertificate)) ?? false);
        if (hasTaxCert)
            flags |= BotWorldState.HasTaxCertificates;

        // Stale memory ids are not plots: ownership counts only when the live
        // housing scan confirms a house for this bot (fixtures use the override gate).
        bool hasLandPlot = context.Memory.HasLandPlotOverride ??
            (HousingManager.PeekInstance != null && HousingManager.PeekInstance.GetAllHouses().Any(h => h.OwnerId == ch.Id));
        if (hasLandPlot)
            flags |= BotWorldState.HasLandPlot;

        bool hasTimber = context.Memory.HasTimberOverride ??
            (ch.Inventory?.Bag?.GetItemsSnapshot().Any(i => i != null && (i.TemplateId == 14 || i.TemplateId == 15 || i.Template?.CategoryId == (int)ItemCategory.Lumber)) ?? false);
        if (hasTimber)
            flags |= BotWorldState.HasTimber;

        // Building materials are the crafted pack items the construction seam consumes —
        // never a backpack/glider merely worn in the back slot.
        bool hasBuildingMaterials = context.Memory.HasBuildingMaterialsOverride ??
            (ch.Inventory?.Bag?.GetItemsSnapshot().Any(i => i?.Template != null && (i.Template.CategoryId == (int)ItemCategory.Trade_Pack || i.Template.CategoryId == (int)ItemCategory.Body_Pack)) ?? false);
        if (hasBuildingMaterials)
            flags |= BotWorldState.HasBuildingMaterials;

        bool homeConstructed = context.Memory.HomeConstructedOverride ??
            (HousingManager.PeekInstance != null && HousingManager.PeekInstance.GetAllHouses().Any(h => h.OwnerId == ch.Id && h.CurrentStep == -1 && h.Template != null && h.Template.MainModelId > 0));
        if (homeConstructed)
            flags |= BotWorldState.HomeConstructed;

        bool plotConstructed = context.Memory.HomeConstructedOverride ??
            (HousingManager.PeekInstance != null && HousingManager.PeekInstance.GetAllHouses().Any(h => h.OwnerId == ch.Id && h.CurrentStep == -1));
        if (plotConstructed)
            flags |= BotWorldState.PlotConstructed;

        if (context.Memory.KnownHousingZonePos.HasValue)
        {
            var distZone = Vector3.Distance(botPos, context.Memory.KnownHousingZonePos.Value);
            if (distZone <= 25.0f)
                flags |= BotWorldState.NearHousingZone;
        }

        if (context.Memory.OwnedHousePos.HasValue)
        {
            var distHome = Vector3.Distance(botPos, context.Memory.OwnedHousePos.Value);
            if (distHome <= 10.0f)
                flags |= BotWorldState.NearHomeSite;
        }

        if (context.Memory.KnownWorkbenchPos.HasValue)
        {
            var distWorkbench = Vector3.Distance(botPos, context.Memory.KnownWorkbenchPos.Value);
            if (distWorkbench <= 15.0f)
                flags |= BotWorldState.NearWorkbench;
        }

        // 4. Target & Threat perception
        var target = ch.CurrentTarget;
        if (target != null)
        {
            flags |= BotWorldState.HasActiveTarget;

            if (target is Npc && CombatDecisionTree.IsHostileTarget(ch, target))
                flags |= BotWorldState.TargetIsHostile;

            if (target.Transform?.World != null)
            {
                var distTarget = Vector3.Distance(botPos, target.Transform.World.Position);
                if (distTarget <= TargetEngagementRange)
                    flags |= BotWorldState.TargetInRange;
            }

            if (target is Unit unit && unit.IsDead)
            {
                flags |= BotWorldState.TargetDead;
                if (!context.Memory.HasLootedCurrentTarget)
                    flags |= BotWorldState.HasCorpseToLoot;
            }
        }

        if (context.Memory.HasLootedCurrentTarget)
            flags |= BotWorldState.HasLooted;

        // 5. Numeric Currencies
        ushort labor = (ushort)Math.Clamp(ch.LaborPower, 0, ushort.MaxValue);
        uint gold = (uint)Math.Clamp(ch.Money, 0, uint.MaxValue);

        return new BotWorldState(flags, mask: mask, labor: labor, gold: gold);
    }
}
