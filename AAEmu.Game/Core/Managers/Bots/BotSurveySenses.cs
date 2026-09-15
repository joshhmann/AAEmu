using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// What kind of world object a survey occupant is.
/// Mates, slaves and transfers are explicitly deferred (not sensed).
/// </summary>
public enum SurveyOccupantKind
{
    Character = 0,
    Npc = 1,
    Doodad = 2
}

/// <summary>
/// One perceived occupant, resolved from an <see cref="ActorObservation"/>
/// ObjId through ordinary world lookups at sense time.
/// </summary>
public sealed record SurveyOccupant(
    SurveyOccupantKind Kind,
    uint ObjId,
    uint TemplateId,
    float X,
    float Y,
    float Z,
    float DistanceM,
    int Hp,
    string? Name);

/// <summary>
/// One lidar bearing: terrain heights stepping outward from the observer,
/// per-step rise gates, and endpoint visibility. Terrain only — structures,
/// walls and vegetation are invisible to the heightmap sweep.
/// </summary>
public sealed record SurveyBearing(
    float BearingDeg,
    float[] TerrainZ,
    bool[] StepBlocked,
    bool EndpointVisible,
    float MinZ,
    float MaxZ);

/// <summary>
/// Sensor configuration captured at survey start. A sweep is blind when
/// heightmaps are disabled; navmesh overlays vanish when GeoDataMode is off.
/// Recorded so later readers can interpret the profile honestly.
/// </summary>
public sealed record SurveySensorFlags(
    bool GeoDataMode,
    bool HeightMapsEnable,
    float MaxStepHeight);

/// <summary>
/// One surveyed waypoint: measured position, place names, occupants and the
/// lidar profile. Baked-coarse Z must never appear here — position always
/// comes from the live server transform.
/// </summary>
public sealed record SurveyPoint(
    float X,
    float Y,
    float Z,
    uint ZoneId,
    string ZoneName,
    uint ZoneGroupId,
    string ZoneGroupName,
    IReadOnlyList<SurveyOccupant> Occupants,
    IReadOnlyList<SurveyBearing> Bearings,
    SurveySensorFlags Flags,
    DateTime CapturedAtUtc);

/// <summary>
/// Pure sweep math for the surveyor lidar. No singletons, no world reads —
/// height and visibility arrive as delegates so unit tests can drive the
/// geometry without a server. Mirrors the NPC precedents
/// (<c>Npc.HasLineOfSight</c> sampling, <c>Npc.IsStepBlocked</c> gating)
/// without calling them.
/// </summary>
public static class SurveySweepMath
{
    public const int BearingCount = 36;
    public const int RangeSteps = 10;
    public const float StepMeters = 2f;
    public const float MaxRangeMeters = RangeSteps * StepMeters;

    public static float BearingRadians(int index)
        => index * MathF.PI * 2f / BearingCount;

    public static Vector2 StepPoint(Vector2 origin, float bearingRad, float distanceM)
        => new(origin.X + MathF.Cos(bearingRad) * distanceM,
               origin.Y + MathF.Sin(bearingRad) * distanceM);

    /// <summary>
    /// Rise gate with the NPC fallback semantics: 0 (no data) and downhill
    /// never block; only a rise above maxStepHeight blocks.
    /// </summary>
    public static bool IsRiseBlocked(float currentZ, float sampleZ, float maxStepHeight)
        => sampleZ != 0f && sampleZ - currentZ > maxStepHeight;

    public static (float MinZ, float MaxZ, float MeanZ, int BlockedSteps) SummarizeBearing(
        float[] terrainZ, bool[] stepBlocked)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        var sum = 0f;
        var count = 0;
        var blocked = 0;
        for (var i = 0; i < terrainZ.Length; i++)
        {
            var z = terrainZ[i];
            if (z == 0f)
                continue; // no data — excluded from the profile, never blocks
            if (z < min) min = z;
            if (z > max) max = z;
            sum += z;
            count++;
            if (i < stepBlocked.Length && stepBlocked[i])
                blocked++;
        }
        return count == 0
            ? (0f, 0f, 0f, blocked)
            : (min, max, sum / count, blocked);
    }
}

/// <summary>
/// Surveyor senses: vision (Observe + resolve + place names) and lidar
/// (radial heightmap sweep). Additive and read-only — never moves the bot,
/// never writes world state. Mates/slaves/transfers are deferred, not sensed.
/// </summary>
public sealed class BotSurveySenses
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IGameplayActor _actor;

    public BotSurveySenses(IGameplayActor actor)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
    }

    public Character Character => _actor.Character;

    public SurveySensorFlags CaptureFlags()
        => new(AppConfiguration.Instance.World.GeoDataMode,
               AppConfiguration.Instance.HeightMapsEnable,
               AppConfiguration.Instance.World.NpcMaxStepHeight);

    /// <summary>
    /// Full waypoint sense: live position, zone/place names, resolved
    /// occupants (stealth-filtered through CanSeeTarget, which plain Observe
    /// does not apply), and the lidar profile from the live heightmap.
    /// </summary>
    public SurveyPoint SenseWaypoint(float senseRadiusM = 25f)
    {
        var observation = _actor.Observe();
        var pos = observation.Position;
        var flags = CaptureFlags();

        var zoneId = Character.Transform.ZoneId;
        var zone = ZoneManager.Instance.GetZoneByKey(zoneId);
        var group = zone is null ? null : ZoneManager.Instance.GetZoneGroupById(zone.GroupId);

        var occupants = ResolveOccupants(observation, pos);

        var bearings = AppConfiguration.Instance.HeightMapsEnable
            ? SweepTerrain(new Vector2(pos.X, pos.Y), pos.Z,
                (x, y) => SampleLiveHeight(x, y),
                flags.MaxStepHeight,
                (from, to) => HasTerrainLineOfSight(from, to))
            : [];

        return new SurveyPoint(
            pos.X, pos.Y, pos.Z,
            zoneId, zone?.Name ?? "unknown",
            zone?.GroupId ?? 0, group?.Name ?? "unknown",
            occupants, bearings, flags, DateTime.UtcNow);
    }

    /// <summary>
    /// Radial sweep core. Height and endpoint visibility arrive as delegates
    /// so the geometry is unit-testable; live callers pass the heightmap
    /// sampler and the terrain LOS check.
    /// </summary>
    public static IReadOnlyList<SurveyBearing> SweepTerrain(
        Vector2 origin,
        float observerZ,
        Func<float, float, float> getHeight,
        float maxStepHeight,
        Func<Vector3, Vector3, bool>? hasLineOfSight = null)
    {
        var result = new List<SurveyBearing>(SurveySweepMath.BearingCount);
        for (var b = 0; b < SurveySweepMath.BearingCount; b++)
        {
            var rad = SurveySweepMath.BearingRadians(b);
            var heights = new float[SurveySweepMath.RangeSteps];
            var blocked = new bool[SurveySweepMath.RangeSteps];
            for (var s = 0; s < SurveySweepMath.RangeSteps; s++)
            {
                var p = SurveySweepMath.StepPoint(origin, rad, (s + 1) * SurveySweepMath.StepMeters);
                float z;
                try
                {
                    z = getHeight(p.X, p.Y);
                }
                catch
                {
                    z = 0f; // unreadable sample degrades to no-data, never blocks
                }
                heights[s] = z;
                blocked[s] = SurveySweepMath.IsRiseBlocked(observerZ, z, maxStepHeight);
            }
            var end = SurveySweepMath.StepPoint(origin, rad, SurveySweepMath.MaxRangeMeters);
            var visible = true;
            if (hasLineOfSight is not null)
            {
                try
                {
                    visible = hasLineOfSight(
                        new Vector3(origin.X, origin.Y, observerZ),
                        new Vector3(end.X, end.Y, getHeight(end.X, end.Y)));
                }
                catch
                {
                    visible = true; // no data never occludes (legacy fallback)
                }
            }
            var (min, max, _, _) = SurveySweepMath.SummarizeBearing(heights, blocked);
            result.Add(new SurveyBearing(b * 360f / SurveySweepMath.BearingCount,
                heights, blocked, visible, min, max));
        }
        return result;
    }

    private List<SurveyOccupant> ResolveOccupants(ActorObservation observation, Vector3 pos)
    {
        var world = Character.ParentWorld;
        var occupants = new List<SurveyOccupant>(
            observation.NearbyNpcObjIds.Count + observation.NearbyDoodadObjIds.Count);
        foreach (var objId in observation.NearbyNpcObjIds)
        {
            var npc = world?.GetNpc(objId);
            if (npc is null || !Character.CanSeeTarget(npc))
                continue; // stealth/invisible — Observe sees it, NPC sight would not
            occupants.Add(ToOccupant(SurveyOccupantKind.Npc, npc, pos));
        }
        foreach (var objId in observation.NearbyDoodadObjIds)
        {
            var doodad = world?.GetDoodad(objId);
            if (doodad is null || !Character.CanSeeTarget(doodad))
                continue;
            occupants.Add(ToOccupant(SurveyOccupantKind.Doodad, doodad, pos));
        }
        foreach (var objId in observation.NearbyCharacterObjIds)
        {
            if (objId == Character.ObjId)
                continue;
            var other = world?.GetCharacterByObjId(objId);
            if (other is null || !Character.CanSeeTarget(other))
                continue;
            occupants.Add(ToOccupant(SurveyOccupantKind.Character, other, pos));
        }
        return occupants;
    }

    private static SurveyOccupant ToOccupant(SurveyOccupantKind kind, BaseUnit unit, Vector3 pos)
    {
        var upos = unit.Transform.World.Position;
        var dx = upos.X - pos.X;
        var dy = upos.Y - pos.Y;
        return new SurveyOccupant(kind, unit.ObjId, unit.TemplateId,
            upos.X, upos.Y, upos.Z, MathF.Sqrt(dx * dx + dy * dy),
            unit is Models.Game.Units.Unit u ? u.Hp : 0, unit.Name);
    }

    private float SampleLiveHeight(float x, float y)
    {
        try
        {
            return Character.ParentWorld?.Template?.GetHeight(x, y) ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    private bool HasTerrainLineOfSight(Vector3 from, Vector3 to)
    {
        var template = Character.ParentWorld?.Template;
        if (template is null)
            return true;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance <= 2f)
            return true;
        var samples = Math.Min((int)(distance / 2f), 64);
        for (var i = 1; i < samples; i++)
        {
            var t = i / (float)samples;
            float z;
            try
            {
                z = template.GetHeight(from.X + dx * t, from.Y + dy * t);
            }
            catch
            {
                continue;
            }
            if (z == 0f)
                continue;
            if (z > from.Z + (to.Z - from.Z) * t + 2f)
                return false;
        }
        return true;
    }
}
