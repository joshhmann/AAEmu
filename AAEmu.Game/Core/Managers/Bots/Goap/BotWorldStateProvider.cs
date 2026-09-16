#nullable enable

using System.Numerics;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;

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
    public const uint DefaultSaplingTemplateId = 15659;
    public const uint DefaultFoodTemplateId = 8219;

    public BotWorldState Project(PlayerBotRuntime bot, BotContext context)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(context);

        var ch = bot.Character;
        ulong flags = BotWorldState.None;

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

        if (context.Memory.HasMatureGroves(context.GetUtcNow()))
            flags |= BotWorldState.BagFull;

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

        // 4. Target & Threat perception
        var target = ch.CurrentTarget;
        if (target != null)
        {
            flags |= BotWorldState.HasActiveTarget;

            if (target is Npc)
                flags |= BotWorldState.TargetIsHostile;

            if (target.Transform?.World != null)
            {
                var distTarget = Vector3.Distance(botPos, target.Transform.World.Position);
                if (distTarget <= 6.0f)
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

        return new BotWorldState(flags, mask: ulong.MaxValue, labor: labor, gold: gold);
    }
}
