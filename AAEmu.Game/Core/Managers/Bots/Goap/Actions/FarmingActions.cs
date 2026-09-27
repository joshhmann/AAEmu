#nullable enable

using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.Game.Core.Managers.Bots.Goap.Actions;

/// <summary>
/// Navigates the bot to the nearest known seed/merchant hub.
/// </summary>
public sealed class TravelToSeedMerchantAction : GoapActionBase
{
    private readonly Vector3 _defaultMerchantPos;

    public TravelToSeedMerchantAction(Vector3? merchantPos = null, float baseCost = 2.0f)
        : base("TravelToSeedMerchant", baseCost)
    {
        _defaultMerchantPos = merchantPos ?? new Vector3(14485.0f, 14411.0f, 112.5f);
        WithEffect(BotWorldState.NearSeedMerchant);
    }

    public override float CalculateCost(PlayerBotRuntime? bot, in BotWorldState currentState)
    {
        if (bot == null)
            return BaseCost;

        var botPos = bot.Character.Transform.World.Position;
        var distance = Vector3.Distance(botPos, _defaultMerchantPos);
        return BaseCost + (distance / 500f);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.NavigateTo(_defaultMerchantPos, speed: 5.0f);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        // Verified by real spatial proximity
        if (observedState.Has(BotWorldState.NearSeedMerchant))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (activeRequest != null && activeRequest.State == ActorLifecycleState.Completed && !observedState.Has(BotWorldState.NearSeedMerchant))
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Purchases TREE SAPLINGS from the sapling merchant (canonical pack 141,
/// 묘목 상인 — never the seed vendor's potato seed 15659). The purchased item is
/// <see cref="SharedGameKnowledge.TreeSaplingItemId"/> (4862, 코르크참나무 묘목:
/// ItemCategory.Saplings), the same identity the projection's HasTreeSaplings
/// flag resolves, so the plan's buy effect and the observed effect cannot drift.
/// Requires NearSeedMerchant and gold.
/// </summary>
public sealed class BuySaplingsAction : GoapActionBase
{
    public uint SaplingTemplateId { get; init; }
    public int SaplingCount { get; init; }
    public uint GoldCost { get; init; }

    /// <summary>
    /// One canonical sapling at its canonical price: the minimum purchase that
    /// establishes <see cref="BotWorldState.HasTreeSaplings"/> (a single plantable
    /// tree), and exactly what <see cref="BotWorldStateProvider"/> observes in the bag.
    /// </summary>
    public BuySaplingsAction(
        uint saplingTemplateId = SharedGameKnowledge.TreeSaplingItemId,
        int saplingCount = 1,
        uint goldCost = SharedGameKnowledge.TreeSaplingUnitPrice,
        float baseCost = 1.0f)
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
        return effectiveActor.Buy(0, SaplingTemplateId, SaplingCount);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        // Crucial Invariant: Succeeded ONLY when inventory genuinely contains saplings
        if (observedState.Has(BotWorldState.HasTreeSaplings))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (activeRequest != null && activeRequest.State == ActorLifecycleState.Completed && !observedState.Has(BotWorldState.HasTreeSaplings))
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
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

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        // Scenario D: Invalidate if target POI became invalid during transit
        if (context.Memory.TargetWildFarmPoiInvalidated)
            return GoapActionStatus.Invalidated;

        if (observedState.Has(BotWorldState.AtWildFarm))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (activeRequest != null && activeRequest.State == ActorLifecycleState.Completed && !observedState.Has(BotWorldState.AtWildFarm))
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Plants TREE SAPLINGS at the secluded wild tree farm.
/// The planted item is <see cref="SharedGameKnowledge.TreeSaplingItemId"/> (4862,
/// 코르크참나무 묘목 — ItemCategory.Saplings), never a crop seed.
/// Consumes saplings and labor points.
/// </summary>
public sealed class PlantWildSaplingAction : GoapActionBase
{
    public uint SaplingTemplateId { get; init; }
    public ushort LaborCost { get; init; }

    public PlantWildSaplingAction(
        uint saplingTemplateId = SharedGameKnowledge.TreeSaplingItemId,
        ushort laborCost = 10,
        float baseCost = 2.0f)
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

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.SecretGrovePlanted) || context.Memory.HasActiveGroves)
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (activeRequest != null && activeRequest.State == ActorLifecycleState.Completed)
        {
            if (observedState.Has(BotWorldState.SecretGrovePlanted) || context.Memory.HasActiveGroves)
                return GoapActionStatus.Succeeded;

            return GoapActionStatus.Failed;
        }

        return GoapActionStatus.Running;
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
        return effectiveActor.Harvest(0);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.BagFull))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
    }
}
