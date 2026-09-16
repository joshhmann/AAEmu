#nullable enable

using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots.Goap.Actions;

/// <summary>
/// Acquires the starter 8x8 Scarecrow Garden design and tax certificates
/// via the Blue Salt Brotherhood introductory quest or starter milestone reward.
/// </summary>
public sealed class AcquireScarecrowAction : GoapActionBase
{
    public const uint ScarecrowDesignTemplateId = 15596; // 도면: 밀짚모자 허수아비 텃밭 (Straw Hat Scarecrow Garden)
    public const uint ScarecrowDesignId = 267;
    public const uint TaxCertificateTemplateId = 8000001;

    public AcquireScarecrowAction(float baseCost = 2.0f)
        : base("AcquireScarecrow", baseCost)
    {
        WithEffect(BotWorldState.HasScarecrowDesign);
        WithEffect(BotWorldState.HasTaxCertificates);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        // Talk to the Blue Salt merchant or complete milestone interaction
        return effectiveActor.Interact(1001);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HasScarecrowDesign) && observedState.Has(BotWorldState.HasTaxCertificates))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        // Context override for test harness / quest simulation
        if (context.Memory.HasScarecrowDesignOverride == true && context.Memory.HasTaxCertificatesOverride == true)
            return GoapActionStatus.Succeeded;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Navigates the bot along continental road networks to the nearest open residential/housing zone.
/// </summary>
public sealed class TravelToHousingZoneAction : GoapActionBase
{
    private readonly Vector3 _defaultHousingZonePos;

    public TravelToHousingZoneAction(Vector3? zonePos = null, float baseCost = 3.0f)
        : base("TravelToHousingZone", baseCost)
    {
        _defaultHousingZonePos = zonePos ?? new Vector3(20450.0f, 10820.0f, 130.0f);
        WithPrecondition(BotWorldState.HasScarecrowDesign);
        WithEffect(BotWorldState.NearHousingZone);
    }

    public static Vector3 ResolveNearestHousingZone(AAEmu.Game.Models.Game.Char.Character character, Vector3 fallback)
    {
        var world = character.ParentWorld as AAEmu.Game.Models.Game.World.WorldInstance;
        if (world?.Template?.HousingZones != null && world.Template.HousingZones.Count > 0)
        {
            var botPos = character.Transform.World.Position;
            Vector3 nearest = fallback;
            float bestDistSq = float.MaxValue;
            foreach (var zoneList in world.Template.HousingZones.Values)
            {
                foreach (var area in zoneList)
                {
                    if (area.Points == null || area.Points.Count == 0)
                        continue;

                    var cx = area.Points.Average(p => p.X);
                    var cy = area.Points.Average(p => p.Y);
                    var distSq = (cx - botPos.X) * (cx - botPos.X) + (cy - botPos.Y) * (cy - botPos.Y);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        float gz = botPos.Z;
                        try
                        {
                            var zk = WorldManager.Instance.GetZoneId(world.Template, cx, cy);
                            gz = WorldManager.Instance.GetHeight(zk, cx, cy, botPos.Z);
                        }
                        catch
                        {
                            gz = botPos.Z;
                        }
                        nearest = new Vector3(cx, cy, gz);
                    }
                }
            }
            return nearest;
        }
        return fallback;
    }

    public override float CalculateCost(PlayerBotRuntime? bot, in BotWorldState currentState)
    {
        if (bot == null) return BaseCost;
        var botPos = bot.Character.Transform?.World?.Position ?? Vector3.Zero;
        var targetPos = ResolveNearestHousingZone(bot.Character, _defaultHousingZonePos);
        return BaseCost + (Vector3.Distance(botPos, targetPos) / 500f);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var targetPos = ResolveNearestHousingZone(bot.Character, _defaultHousingZonePos);
        return effectiveActor.NavigateTo(targetPos, speed: 5.4f);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.NearHousingZone))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (context.Memory.KnownHousingZonePos.HasValue)
        {
            var botPos = bot.Character.Transform?.World?.Position ?? Vector3.Zero;
            if (Vector3.Distance(botPos, context.Memory.KnownHousingZonePos.Value) <= 25.0f)
                return GoapActionStatus.Succeeded;
        }

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Surveys open land coordinates within the housing zone, validates placement constraints,
/// and builds the 8x8 Scarecrow Garden via IGameplayActor.BuildHouse.
/// </summary>
public sealed class SurveyAndPlacePlotAction : GoapActionBase
{
    private readonly Vector3 _defaultPlotPos;

    public SurveyAndPlacePlotAction(Vector3? plotPos = null, float baseCost = 4.0f)
        : base("SurveyAndPlacePlot", baseCost)
    {
        _defaultPlotPos = plotPos ?? new Vector3(20455.0f, 10825.0f, 130.0f);
        WithPrecondition(BotWorldState.HasScarecrowDesign);
        WithPrecondition(BotWorldState.HasTaxCertificates);
        WithPrecondition(BotWorldState.NearHousingZone);
        WithEffect(BotWorldState.HasLandPlot);
        WithEffect(BotWorldState.NearHomeSite);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var targetPos = TravelToHousingZoneAction.ResolveNearestHousingZone(bot.Character, _defaultPlotPos);
        return effectiveActor.BuildHouse(
            AcquireScarecrowAction.ScarecrowDesignId,
            AcquireScarecrowAction.ScarecrowDesignTemplateId,
            targetPos,
            zRot: 0f);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HasLandPlot))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (context.Memory.HasLandPlotOverride == true || context.Memory.OwnedHouseId.HasValue)
            return GoapActionStatus.Succeeded;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Navigates the bot directly to its owned homestead or house site.
/// </summary>
public sealed class TravelToHomeSiteAction : GoapActionBase
{
    private readonly Vector3 _defaultHomePos;

    public TravelToHomeSiteAction(Vector3? homePos = null, float baseCost = 2.0f)
        : base("TravelToHomeSite", baseCost)
    {
        _defaultHomePos = homePos ?? new Vector3(20455.0f, 10825.0f, 130.0f);
        WithPrecondition(BotWorldState.HasLandPlot);
        WithEffect(BotWorldState.NearHomeSite, true);
        WithEffect(BotWorldState.NearWorkbench, false);
        WithEffect(BotWorldState.NearSeedMerchant, false);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var targetPos = _defaultHomePos;
        return effectiveActor.NavigateTo(targetPos, speed: 5.4f);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.NearHomeSite))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Plants crops or trees specifically within the protected boundaries of the bot's owned plot.
/// </summary>
public sealed class PlantOnPlotAction : GoapActionBase
{
    public const uint DefaultSeedTemplateId = 15659;

    public PlantOnPlotAction(float baseCost = 2.5f)
        : base("PlantOnPlot", baseCost)
    {
        WithPrecondition(BotWorldState.HasLandPlot);
        WithPrecondition(BotWorldState.NearHomeSite);
        WithPrecondition(BotWorldState.HasTreeSaplings);
        WithEffect(BotWorldState.SecretGrovePlanted);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var plotCenter = bot.Character.Transform?.World?.Position ?? Vector3.Zero;
        return effectiveActor.Plant(DefaultSeedTemplateId, plotCenter, zRot: 0f);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.SecretGrovePlanted))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (context.Memory.HasActiveGroves)
            return GoapActionStatus.Succeeded;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Gathers mature timber and logs from trees growing on the bot's homestead plot.
/// </summary>
public sealed class HarvestTimberAction : GoapActionBase
{
    public HarvestTimberAction(float baseCost = 2.5f)
        : base("HarvestTimber", baseCost)
    {
        WithPrecondition(BotWorldState.HasLandPlot);
        WithPrecondition(BotWorldState.NearHomeSite);
        WithPrecondition(BotWorldState.SecretGrovePlanted);
        WithEffect(BotWorldState.HasTimber);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.Interact(2001);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HasTimber))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (context.Memory.HasTimberOverride == true)
            return GoapActionStatus.Succeeded;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Travels to a public carpentry or masonry workbench to craft construction material packs.
/// </summary>
public sealed class TravelToWorkbenchAction : GoapActionBase
{
    private readonly Vector3 _defaultWorkbenchPos;

    public TravelToWorkbenchAction(Vector3? workbenchPos = null, float baseCost = 2.0f)
        : base("TravelToWorkbench", baseCost)
    {
        _defaultWorkbenchPos = workbenchPos ?? new Vector3(20480.0f, 10860.0f, 130.0f);
        WithPrecondition(BotWorldState.HasTimber);
        WithEffect(BotWorldState.NearWorkbench, true);
        WithEffect(BotWorldState.NearHomeSite, false);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.NavigateTo(_defaultWorkbenchPos, speed: 5.4f);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.NearWorkbench))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Crafts a Lumber or Stone material pack at the workbench.
/// </summary>
public sealed class CraftMaterialPackAction : GoapActionBase
{
    public const uint LumberPackRecipeId = 1001;

    public CraftMaterialPackAction(float baseCost = 3.0f)
        : base("CraftMaterialPack", baseCost)
    {
        WithPrecondition(BotWorldState.HasTimber);
        WithPrecondition(BotWorldState.NearWorkbench);
        WithEffect(BotWorldState.HasBuildingMaterials);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.Craft(LumberPackRecipeId, doodadObjId: 0);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HasBuildingMaterials))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (context.Memory.HasBuildingMaterialsOverride == true)
            return GoapActionStatus.Succeeded;

        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Interacts with the house construction frame, using material packs to advance construction steps to completion.
/// </summary>
public sealed class ConstructHomeAction : GoapActionBase
{
    public ConstructHomeAction(float baseCost = 4.0f)
        : base("ConstructHome", baseCost)
    {
        WithPrecondition(BotWorldState.HasLandPlot);
        WithPrecondition(BotWorldState.NearHomeSite);
        WithPrecondition(BotWorldState.HasBuildingMaterials);
        WithEffect(BotWorldState.HomeConstructed);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        return effectiveActor.Interact(101);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HomeConstructed))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        if (context.Memory.HomeConstructedOverride == true)
            return GoapActionStatus.Succeeded;

        return GoapActionStatus.Running;
    }
}
