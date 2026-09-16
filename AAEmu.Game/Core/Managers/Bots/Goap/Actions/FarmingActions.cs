#nullable enable

using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.Game.Core.Managers.Bots.Goap.Actions;

/// <summary>
/// Navigates the bot to the nearest known seed/sapling merchant hub.
/// </summary>
public sealed class TravelToSeedMerchantAction : GoapActionBase
{
    private readonly Vector3 _defaultMerchantPos;

    public TravelToSeedMerchantAction(Vector3? merchantPos = null, float baseCost = 2.0f)
        : base("TravelToSeedMerchant", baseCost)
    {
        // Default to Solzreed nursery / seed merchant coordinates if not specified
        _defaultMerchantPos = merchantPos ?? new Vector3(14485.0f, 14411.0f, 112.5f);
        WithEffect(BotWorldState.NearSeedMerchant);
    }

    public override float CalculateCost(PlayerBotRuntime? bot, in BotWorldState currentState)
    {
        if (bot == null)
            return BaseCost;

        var botPos = bot.Character.Transform.World.Position;
        var distance = Vector3.Distance(botPos, _defaultMerchantPos);
        // Cost scales mildly with distance (1.0 base + 1.0 per 500m)
        return BaseCost + (distance / 500f);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.NavigateTo(_defaultMerchantPos, speed: 5.0f);
    }
}

/// <summary>
/// Purchases tree saplings from the seed merchant.
/// Requires NearSeedMerchant and gold.
/// </summary>
public sealed class BuySaplingsAction : GoapActionBase
{
    public uint SaplingTemplateId { get; init; }
    public int SaplingCount { get; init; }
    public uint GoldCost { get; init; }

    public BuySaplingsAction(uint saplingTemplateId = 15659, int saplingCount = 5, uint goldCost = 50, float baseCost = 1.0f)
        : base("BuyTreeSaplings", baseCost)
    {
        SaplingTemplateId = saplingTemplateId;
        SaplingCount = saplingCount;
        GoldCost = goldCost;

        WithPrecondition(BotWorldState.NearSeedMerchant);
        WithGoldPrecondition(goldCost);

        WithEffect(BotWorldState.HasTreeSaplings);
        WithGoldCost(goldCost);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        // In real execution, targetObjId is the nearby merchant NPC objId
        return effectiveActor.Buy(0, SaplingTemplateId, SaplingCount);
    }
}

/// <summary>
/// Hikes off-road or follows mountain ridges to a secret wild tree farm POI.
/// </summary>
public sealed class HikeToWildFarmAction : GoapActionBase
{
    private readonly WildFarmPoi? _targetPoi;

    public HikeToWildFarmAction(WildFarmPoi? targetPoi = null, float baseCost = 3.0f)
        : base("HikeToSecretPlateau", baseCost)
    {
        _targetPoi = targetPoi;
        WithEffect(BotWorldState.AtWildFarm);
    }

    public override float CalculateCost(PlayerBotRuntime? bot, in BotWorldState currentState)
    {
        if (bot == null)
            return BaseCost;

        var botPos = bot.Character.Transform.World.Position;
        var destination = _targetPoi != null
            ? new Vector3(_targetPoi.X, _targetPoi.Y, _targetPoi.Z)
            : new Vector3(14210.5f, 14680.2f, 123.6f);

        var distance = Vector3.Distance(botPos, destination);
        return BaseCost + (distance / 400f);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var destination = _targetPoi != null
            ? new Vector3(_targetPoi.X, _targetPoi.Y, _targetPoi.Z)
            : new Vector3(14210.5f, 14680.2f, 123.6f);

        return effectiveActor.NavigateTo(destination, speed: 4.5f);
    }
}

/// <summary>
/// Plants tree saplings at the secluded wild tree farm.
/// Consumes saplings and labor points.
/// </summary>
public sealed class PlantWildSaplingAction : GoapActionBase
{
    public uint SaplingTemplateId { get; init; }
    public ushort LaborCost { get; init; }

    public PlantWildSaplingAction(uint saplingTemplateId = 15659, ushort laborCost = 10, float baseCost = 2.0f)
        : base("PlantSecretGrove", baseCost)
    {
        SaplingTemplateId = saplingTemplateId;
        LaborCost = laborCost;

        WithPrecondition(BotWorldState.AtWildFarm);
        WithPrecondition(BotWorldState.HasTreeSaplings);
        WithLaborPrecondition(laborCost);

        WithEffect(BotWorldState.SecretGrovePlanted);
        WithLaborCost(laborCost);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var pos = bot.Character.Transform.World.Position;
        return effectiveActor.Plant(SaplingTemplateId, pos);
    }
}

/// <summary>
/// Chops or harvests a mature tree doodad in the secret grove.
/// Consumes labor points.
/// </summary>
public sealed class ChopMatureTreeAction : GoapActionBase
{
    public ushort LaborCost { get; init; }

    public ChopMatureTreeAction(ushort laborCost = 15, float baseCost = 2.5f)
        : base("ChopMatureTree", baseCost)
    {
        LaborCost = laborCost;

        WithPrecondition(BotWorldState.AtWildFarm);
        WithLaborPrecondition(laborCost);

        WithEffect(BotWorldState.BagFull);
        WithLaborCost(laborCost);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        // In real execution, doodadObjId is the nearby tree doodad
        return effectiveActor.Harvest(0);
    }
}
