#nullable enable

using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using NLog;
namespace AAEmu.Game.Core.Managers.Bots.Goap.Actions;

/// <summary>
/// Acquires the starter 8x8 Scarecrow Garden design and tax certificates
/// via the Blue Salt Brotherhood introductory quest or starter milestone reward.
/// </summary>
public sealed class AcquireScarecrowAction : GoapActionBase
{
    public const uint ScarecrowDesignTemplateId = 15596; // 도면: 밀짚모자 허수아비 텃밭 (Straw Hat Scarecrow Garden)
    public const uint ScarecrowDesignId = 267;
    public const uint TaxCertificateBoundTemplateId = 31892; // 귀속된 건축물 세금 증지 (Bound Tax Certificate)
    public const uint TaxCertificateTradeableTemplateId = 31891; // 건축물 세금 증지 (Tradeable Tax Certificate)
    public const uint TaxCertificateTemplateId = TaxCertificateBoundTemplateId;

    /// <summary>
    /// Copper value of one tax certificate, canonical: the engine converts a copper tax
    /// bill into a certificate count with <c>Math.Ceiling(due / 10000f)</c>
    /// (HousingManager.cs:718 Build, MailManager.cs:551 tax mail). Same divisor, shared so
    /// the two cannot drift.
    /// </summary>
    public const int TaxCertificateCopperValue = 10_000;

    /// <summary>
    /// Bound tax certificates a FIRST placement of <see cref="ScarecrowDesignId"/> (housing
    /// 267) requires under the engine's own tax branch. Canonical inputs:
    /// compact.sqlite3 <c>housings</c> row 267 → taxation_id 8, heavy_tax 't';
    /// <c>taxations</c> row 8 → tax 50,000 copper ("소형 텃밭 (8m x 8m)");
    /// HousingManager.CalculateBuildingTaxInfo (HousingManager.cs:1024-1070): new building =
    /// one week tax (heavy multiplier is 1.0 below 3 heavy houses) + deposit (base × 2,
    /// HousingManager.cs:526) = 150,000 copper → 15 certificates.
    /// </summary>
    public static int RequiredFirstPlacementTaxCertificates()
    {
        var template = AAEmu.Game.GameData.HousingGameData.Instance.GetTemplate(ScarecrowDesignId);
        var weekly = (int)(template?.Taxation?.Tax ?? 0);
        if (weekly <= 0)
            return 0;
        var due = weekly /* one week */ + weekly * 2 /* deposit */;
        return (int)Math.Ceiling(due / (float)TaxCertificateCopperValue);
    }

    public AcquireScarecrowAction(float baseCost = 2.0f)
        : base("AcquireScarecrow", baseCost)
    {
        WithEffect(BotWorldState.HasScarecrowDesign);
        WithEffect(BotWorldState.HasTaxCertificates);
    }

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

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

        // Step-3 isolation: an in-flight acquisition request still waits for its
        // terminal state — but with no design/certs there is nothing to wait for.
        // Fail loudly instead of Running forever.
        // NOTE: the fixture override layer is intentionally NOT consulted here.
        // Simulated bag state is projected by BotWorldStateProvider (which honours
        // those overrides under the fixture gate) into observedState above; an
        // action that re-read the override itself would report success for a
        // pretend bag even on a production run where the gate is closed.
        if (activeRequest != null && !activeRequest.IsTerminal)
            return GoapActionStatus.Running;

        Logger.Warn("AcquireScarecrow: no scarecrow design/certs observed — acquire via ordinary progression or /bot home kit opt-in");
        return GoapActionStatus.Failed;
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

    /// <summary>
    /// Resolves a placement position whose canonical housing-area rule ACCEPTS the
    /// design's house category. The nearest-area-centroid resolver above answers
    /// "where do I travel to", not "where may I legally build": housing areas differ
    /// per area (housing_areas.comments → housing_area.xml shape → the area's
    /// housing_group rule), so a 16-category plot placed on the nearest area's
    /// centroid is refused by the engine's polygon gate when that area allows only
    /// 1/10/17/18 (verified live: w_solzreed_3 moang areas).
    ///
    /// Selection uses the engine's own rule data (HousingGameData.GetAreaRuleByShapeName
    /// + the zone-level land-zone categories) and requires the centroid to actually be
    /// inside the shape (Point.IsInside) — no hardcoded coordinates, no stand-in id.
    /// Returns null when no area on this world accepts the design.
    /// </summary>
    public static Vector3? ResolvePlacementPosition(
        AAEmu.Game.Models.Game.Char.Character character,
        uint houseCategoryId,
        bool characterOwnsHouses,
        Vector3 fallback)
    {
        var world = character.ParentWorld as AAEmu.Game.Models.Game.World.WorldInstance;
        var zones = world?.Template?.HousingZones;
        if (zones == null || zones.Count == 0)
            return null;

        var botPos = character.Transform.World.Position;
        Vector3? best = null;
        var bestDistSq = float.MaxValue;

        foreach (var (zoneId, areaList) in zones)
        {
            foreach (var area in areaList)
            {
                if (area?.Points == null || area.Points.Count < 3)
                    continue;

                var rule = AAEmu.Game.GameData.HousingGameData.Instance.GetAreaRuleByShapeName(area.Name);
                if (rule == null || !rule.AllowedCategories.Contains(houseCategoryId))
                    continue;
                if (rule.HouselessOnly && characterOwnsHouses)
                    continue;

                var cx = area.Points.Average(p => p.X);
                var cy = area.Points.Average(p => p.Y);
                if (!AAEmu.Game.Models.Game.World.Point.IsInside(area.Points, area.Points.Count, new Vector3(cx, cy, 0)))
                    continue;

                // Zone-level rules are layered on the polygon rules by the engine.
                var zone = AAEmu.Game.Core.Managers.World.ZoneManager.Instance.GetZoneById(zoneId);
                var landZone = AAEmu.Game.GameData.HousingGameData.Instance.GetLandZoneByZoneName(zone?.Name);
                if (landZone == null || !landZone.AllowedCategories.Contains(houseCategoryId))
                    continue;
                if (landZone.IsHouselessOnly && characterOwnsHouses)
                    continue;

                var distSq = (cx - botPos.X) * (cx - botPos.X) + (cy - botPos.Y) * (cy - botPos.Y);
                if (distSq >= bestDistSq)
                    continue;

                float gz = botPos.Z;
                try
                {
                    var zk = AAEmu.Game.Core.Managers.World.WorldManager.Instance.GetZoneId(world.Template, cx, cy);
                    gz = AAEmu.Game.Core.Managers.World.WorldManager.Instance.GetHeight(zk, cx, cy, botPos.Z);
                }
                catch
                {
                    // No height data for this world — the engine's terrain band is
                    // skipped for the same reason, so the bot's own Z is the best known.
                }

                bestDistSq = distSq;
                best = new Vector3(cx, cy, gz);
            }
        }

        return best ?? (zones.Count > 0 ? fallback : null);
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
        var template = AAEmu.Game.GameData.HousingGameData.Instance.GetTemplate(AcquireScarecrowAction.ScarecrowDesignId);
        var ownsHouses = AAEmu.Game.Core.Managers.HousingManager.PeekInstance != null
            && AAEmu.Game.Core.Managers.HousingManager.PeekInstance.GetAllHouses().Any(h => h.OwnerId == bot.Character.Id);
        // A LEGAL spot (an area whose canonical rule accepts this design's category),
        // not merely the nearest housing zone — see ResolvePlacementPosition.
        var targetPos = TravelToHousingZoneAction.ResolvePlacementPosition(
                bot.Character, template?.CategoryId ?? 0, ownsHouses, _defaultPlotPos)
            ?? TravelToHousingZoneAction.ResolveNearestHousingZone(bot.Character, _defaultPlotPos);
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

        // Ownership is read from the live projection only: Memory.OwnedHouseId is a
        // fixture/rig field no production code writes, so treating it as evidence
        // would let a bot claim a plot it never placed.
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

        // Timber evidence is the live projection (bag contents), never the fixture
        // override read directly.
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

        // Material evidence is the live projection, never the fixture override.
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

        // Construction evidence is the live projection (HousingManager house state),
        // never the fixture override.
        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Completes the single construction step for an 8x8 Small Scarecrow Garden (Housing 267)
/// using Skill 18553 (consumes 10 LP, 0 material packs).
/// </summary>
public sealed class ConstructPlotAction : GoapActionBase
{
    public const uint ScarecrowBuildSkillId = 18553;
    public const ushort RequiredLabor = 10;

    public ConstructPlotAction(float baseCost = 1.5f)
        : base("ConstructPlot", baseCost)
    {
        WithPrecondition(BotWorldState.HasLandPlot);
        WithPrecondition(BotWorldState.NearHomeSite);
        WithEffect(BotWorldState.PlotConstructed);
    }

    public override bool CheckPreconditions(in BotWorldState currentState)
    {
        if (!base.CheckPreconditions(currentState))
            return false;
        return currentState.Labor >= RequiredLabor;
    }

    public override BotWorldState ApplyEffects(in BotWorldState currentState)
    {
        var next = base.ApplyEffects(currentState);
        var remainingLabor = currentState.Labor >= RequiredLabor ? (ushort)(currentState.Labor - RequiredLabor) : (ushort)0;
        return next.WithLabor(remainingLabor);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        // The target is the RESOLVED house frame, never a stand-in: a bare
        // Interact(skillId) would dispatch construction at an object id that is
        // a skill, and any engine-side resolution of it would be an accident.
        // No owned unfinished frame -> no request at all, and EvaluateStatus
        // reports the failure (see below) instead of a fabricated success.
        var house = ResolveOwnedUnfinishedFrame(bot.Character.Id);
        if (house == null)
            return null;
        return effectiveActor.Interact(house.ObjId, ScarecrowBuildSkillId);
    }

    /// <summary>
    /// The live housing scan for this bot's own frame still under construction
    /// (<c>CurrentStep != -1</c>). Null means there is nothing legal to construct —
    /// a missing frame is a FAILED action, never a stand-in target.
    /// </summary>
    public static Models.Game.Housing.House? ResolveOwnedUnfinishedFrame(uint characterId)
        => HousingManager.PeekInstance?.GetAllHouses()
            .FirstOrDefault(h => h.OwnerId == characterId && h.CurrentStep != -1);

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.PlotConstructed))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        // Plot-construction evidence is the live projection, never the fixture override.
        return GoapActionStatus.Running;
    }
}

/// <summary>
/// Crafts Bound Tax Certificates (Item 31892) via Craft 76 at any Building Management Plaque (Doodad 2392).
/// Consumes 200 LP via Skill 16767 to produce 5 Bound Tax Certificates.
/// </summary>
public sealed class CraftTaxCertificatesAction : GoapActionBase
{
    public const uint TaxCraftId = 76;
    public const uint TaxCraftSkillId = 16767;
    public const uint BuildingPlaqueDoodadTemplateId = 2392;
    public const ushort RequiredLabor = 200;

    public CraftTaxCertificatesAction(float baseCost = 2.0f)
        : base("CraftTaxCertificates", baseCost)
    {
        WithPrecondition(BotWorldState.HasLandPlot);
        WithPrecondition(BotWorldState.NearHomeSite);
        WithEffect(BotWorldState.HasTaxCertificates);
    }

    public override bool CheckPreconditions(in BotWorldState currentState)
    {
        if (!base.CheckPreconditions(currentState))
            return false;
        return currentState.Labor >= RequiredLabor;
    }

    public override BotWorldState ApplyEffects(in BotWorldState currentState)
    {
        var next = base.ApplyEffects(currentState);
        var remainingLabor = currentState.Labor >= RequiredLabor ? (ushort)(currentState.Labor - RequiredLabor) : (ushort)0;
        return next.WithLabor(remainingLabor);
    }

    public override ActorRequest? CreateActorRequest(PlayerBotRuntime bot, IGameplayActor? actor = null)
    {
        var effectiveActor = actor ?? new GameplayActor(bot.Character);
        var plaqueObjId = bot.Character.ParentWorld?.GetAllDoodads()
            .FirstOrDefault(d => d.TemplateId == BuildingPlaqueDoodadTemplateId)?.ObjId
            ?? BuildingPlaqueDoodadTemplateId;
        return effectiveActor.Craft(TaxCraftId, doodadObjId: plaqueObjId);
    }

    public override GoapActionStatus EvaluateStatus(
        PlayerBotRuntime bot,
        in BotWorldState observedState,
        ActorRequest? activeRequest,
        BotContext context)
    {
        if (observedState.Has(BotWorldState.HasTaxCertificates))
            return GoapActionStatus.Succeeded;

        if (activeRequest != null && activeRequest.IsTerminal && activeRequest.State != ActorLifecycleState.Completed)
            return GoapActionStatus.Failed;

        // Certificate evidence is the live projection, never the fixture override.
        return GoapActionStatus.Running;
    }
}
