#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.CommonFarm.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots.Needs;

/// <summary>
/// THE SINGLE SOIL RESOLVER. The one implementation of "is this world position
/// plantable soil for this seed?" and the one bounded waypoint spiral that finds
/// such a position — moved out of <c>BotRoamStepExecutor</c> (its
/// <c>SoilSpiralCandidates</c> loop and its <c>IsValidFarmSoil</c> membership
/// chain) so the existing needs-farm leg and the needs BRAIN's planner resolve
/// soil by exactly the same rule and the same candidate order, never a second copy.
///
/// Discipline this keeps:
///  - NEVER A WHOLE-WORLD SCAN: candidates come from the deterministic spiral
///    (8 compass points per ring) out to the perception radius, then a bounded
///    coarser discovery ring;
///  - NEVER FIXTURE-INJECTED: a caller (or a test) may inject the soil PROBE —
///    "is this point plantable?" — but the DESTINATION is always produced by the
///    spiral walking those probes, so no caller can hand a fabricated point to a
///    travel leg;
///  - FAIL-CLOSED: the probe is TRI-STATE. <c>true</c>/<c>false</c> are readable
///    answers; <c>null</c> means the soil surface could not be read at all
///    (no world, no template, an engine read that threw, or a seed with no doodad
///    mapping) — which the decision layer turns into a NAMED hold rather than a
///    fabricated "no soil here".
/// </summary>
public static class NeedsSoil
{
    /// <summary>
    /// The distance inside which a candidate soil point counts as "at the
    /// merchant": a seed merchant's own patch of ground is never a farm
    /// destination (the shared 5 m discipline — one literal, every site).
    /// </summary>
    public const float MerchantExclusionM = 5.0f;

    /// <summary>Bounded perception radius for soil discovery (mirrors the needs-farm leg's own const).</summary>
    public const float DefaultPerceptionRadiusM = 45f;

    /// <summary>Ring step of the perception spiral (mirrors the needs-farm leg's own 5 m).</summary>
    public const float DefaultPerceptionStepM = 5f;

    /// <summary>Discovery extension radius: the perception spiral runs first, this coarser bound second.</summary>
    public const float DefaultDiscoveryRadiusM = 150f;

    /// <summary>Coarse spiral step for the discovery extension.</summary>
    public const float DefaultDiscoveryStepM = 15f;

    /// <summary>
    /// The deterministic waypoint spiral: rings at <paramref name="step"/>
    /// increments out to <paramref name="maxRadius"/>, 8 compass points each,
    /// skipping anything inside <paramref name="skipWithin"/>. Moved verbatim
    /// from <c>BotRoamStepExecutor.SoilSpiralCandidates</c> — the candidate ORDER
    /// is part of the discipline (nearest ring first, fixed compass order), so
    /// two callers resolve the same destination from the same soil.
    /// </summary>
    public static IEnumerable<Vector3> SpiralCandidates(Vector3 origin, float maxRadius, float step,
        float skipWithin = 0f)
    {
        for (var r = step; r <= maxRadius + 0.001f; r += step)
        {
            if (r <= skipWithin)
                continue;
            for (var k = 0; k < 8; k++)
            {
                var a = (float)(k * Math.PI / 4);
                yield return new Vector3(
                    origin.X + MathF.Cos(a) * r,
                    origin.Y + MathF.Sin(a) * r,
                    origin.Z);
            }
        }
    }

    /// <summary>
    /// True when a candidate sits at a seed merchant (and is therefore excluded
    /// as a soil destination). A missing merchant position is not a proximity: an
    /// unresolved merchant never excludes a candidate.
    /// </summary>
    public static bool AtMerchant(Vector3 candidate, Vector3? merchantPosition)
        => merchantPosition.HasValue
           && MathUtil.CalculateDistance(candidate, merchantPosition.Value, false) <= MerchantExclusionM;

    /// <summary>
    /// THE SOIL READ, TRI-STATE: <c>true</c> when the seed may be planted at
    /// <paramref name="position"/>; <c>false</c> when the surface was read and the
    /// seed may not; <c>null</c> when the surface could not be read at all.
    ///
    /// The membership chain is the engine's own: the position is inside a public
    /// farm subzone, that subzone's farm type is valid, the seed maps to a doodad,
    /// and that doodad is on the farm type's allowlist. The engine still
    /// revalidates the count cap fail-closed at dispatch — this is a probe, never
    /// a placement.
    /// </summary>
    public static bool? Probe(WorldInstance? world, Vector3 position, uint seedItemTemplateId)
    {
        var template = world?.Template;
        if (template == null)
            return null;
        try
        {
            if (!PublicFarmManager.Instance.InPublicFarm(template, position))
                return false;
            var farmType = PublicFarmManager.Instance.GetFarmType(world!, position);
            if (farmType == FarmType.Invalid)
                return false;
            var doodadId = ItemManager.Instance.GetDoodadIdFromItem(seedItemTemplateId);
            if (doodadId == 0)
                return null; // no seed→doodad mapping: the surface is unreadable, not unplantable
            return CommonFarmGameData.Instance.GetAllowedDoodads(farmType).Contains(doodadId);
        }
        catch
        {
            // An engine read that threw is an UNREAD surface — never a "no soil here".
            return null;
        }
    }

    /// <summary>
    /// The result of a bounded soil search: the destination (if one was found),
    /// how many candidates the probe could actually ANSWER, and whether any
    /// answer at all was readable. The two counters are what let the decision
    /// layer tell "no farm anywhere near" (a real absence) from "the soil surface
    /// could not be read" (the fail-closed hold) — neither is inferred from the
    /// mere absence of a destination.
    /// </summary>
    public readonly record struct SearchResult(Vector3? Destination, int ReadableProbes)
    {
        /// <summary>True when at least one candidate's plantability was actually decided.</summary>
        public bool AnyReadable => ReadableProbes > 0;
    }

    /// <summary>
    /// Resolves the nearest plantable soil destination for the seed by walking the
    /// bounded spiral (perception rings first, then the coarser discovery rings),
    /// skipping candidates at a seed merchant and ground-pinning the survivor
    /// through <paramref name="groundZ"/>.
    ///
    /// <paramref name="probe"/> is the soil read (injectable so a headless test can
    /// map a surface, exactly as the existing leg's provider seam does); the
    /// DESTINATION is always the spiral's own output.
    /// </summary>
    public static SearchResult ResolveDestination(
        Vector3 from,
        Func<Vector3, bool?> probe,
        Vector3? merchantPosition,
        Func<Vector3, float, Vector3> groundZ,
        float perceptionRadiusM = DefaultPerceptionRadiusM,
        float perceptionStepM = DefaultPerceptionStepM,
        float discoveryRadiusM = DefaultDiscoveryRadiusM,
        float discoveryStepM = DefaultDiscoveryStepM)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(groundZ);

        var readable = 0;
        foreach (var candidate in SpiralCandidates(from, perceptionRadiusM, perceptionStepM))
        {
            if (AtMerchant(candidate, merchantPosition))
                continue;
            var answer = probe(candidate);
            if (answer == null)
                continue;
            readable++;
            if (answer == true)
                return new SearchResult(groundZ(candidate, from.Z), readable);
        }

        foreach (var candidate in SpiralCandidates(from, discoveryRadiusM, discoveryStepM, perceptionRadiusM))
        {
            if (AtMerchant(candidate, merchantPosition))
                continue;
            var answer = probe(candidate);
            if (answer == null)
                continue;
            readable++;
            if (answer == true)
                return new SearchResult(groundZ(candidate, from.Z), readable);
        }

        return new SearchResult(null, readable);
    }
}
