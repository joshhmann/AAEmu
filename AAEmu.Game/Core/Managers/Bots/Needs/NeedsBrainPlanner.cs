#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots.Needs;

/// <summary>
/// The needs brain's LIVE ADAPTER: turns one wake's live reads into a
/// <see cref="NeedsBrainInputs"/>, runs <see cref="NeedsBrain.Decide"/>, and
/// reports the decision, where the soil destination came from, and the crop it
/// concerned.
///
/// The split is deliberate: <see cref="NeedsBrain"/> is pure and unit-testable
/// without a world, and every live read lives here — the soil surface read
/// (tri-state) through <see cref="NeedsSoil"/>, the seed count from the bag, the
/// tracked crop's resolve + the crop rule + the maturity read from
/// <see cref="NeedsCrop"/>, and the crop's distance.
///
/// REUSE, NOT REINVENTION (binding for this increment):
///  - the SOIL DESTINATION is always produced by <see cref="NeedsSoil.ResolveDestination"/>
///    — the single bounded spiral (perception ring, then the coarser discovery
///    ring) the existing needs-farm leg walks. The <see cref="Request.SoilProbe"/>
///    seam may override the PROBE (a headless test mapping a surface), but a
///    caller can never inject a DESTINATION: <see cref="Request.SoilDestination"/>
///    is honoured ONLY when <see cref="Request.SoilDestinationResolved"/> is set,
///    which the ordinary caller leaves false so the spiral runs. A fabricated
///    destination therefore cannot reach a travel leg through this adapter.
///
/// Fail-closed contract:
///  - an unreadable soil surface, seed count or crop read holds with a NAMED
///    reason — never a fabricated destination, count, or "gone" crop;
///  - a probe that answered no candidate at all is reported as
///    <see cref="Prepared.SoilProbesReadable"/> == 0, so a caller can tell "no farm
///    anywhere near" from "the surface could not be read";
///  - the crop distance is NaN when it could not be measured, which the brain
///    never reads as "in range".
/// </summary>
public static class NeedsBrainPlanner
{
    /// <summary>
    /// The wake's live measurement plus the decision it produced — the shape a
    /// caller reads to bank the leg it issued, diagnose, and re-evaluate.
    /// </summary>
    public readonly record struct Prepared(
        NeedsBrainInputs Inputs,
        NeedsBrainDecision Decision,
        Vector3? SoilDestination,
        int SoilProbesReadable,
        Doodad? Crop)
    {
        /// <summary>True when this wake produced a leg the caller must dispatch.</summary>
        public bool HasLeg => Decision.HasVerb;

        /// <summary>True when the wake asks the caller to RESOLVE a soil destination (bounded spiral).</summary>
        public bool NeedsSoilResolve => Decision.Verdict == NeedsVerdict.SeekSoil;

        /// <summary>True when the wake asks the caller to WALK a destination through the existing route layer.</summary>
        public bool IsTravel => Decision.Verdict == NeedsVerdict.Travel;

        /// <summary>True when the wake asks the caller to harvest a specific crop.</summary>
        public bool IsHarvest => Decision.Verdict == NeedsVerdict.Harvest;

        /// <summary>True when the soil surface could not be read at all (the named fail-closed hold).</summary>
        public bool SoilUnreadable => Decision.Reason == NeedsReason.SoilUnreadable;
    }

    /// <summary>
    /// Everything the planner needs from the caller — the actor's own tracking
    /// facts about the leg it already issued, the seed it is working toward, the
    /// optional probe override, and the bounds.
    ///
    /// A value type carrying no engine type, so a unit test can drive the live
    /// adapter against a headless actor without constructing policy state.
    /// </summary>
    public readonly record struct Request(
        Vector3 SelfPosition,
        uint SeedItemTemplateId,
        bool SeedReadable,
        int SeedCount,
        bool CropEnRoute,
        bool SoilEnRoute,
        int CropApproachAttempts,
        int MaxCropApproachAttempts,
        bool HarvestJustLanded,
        float HarvestRangeM,
        int SoilAttempts,
        int MaxSoilAttempts,
        Func<Character, Vector3, bool?>? SoilProbe,
        Func<Character, Vector3, Vector3>? GroundZ,
        Func<Character, Vector3, Vector3?>? SoilDestination,
        bool SoilDestinationResolved,
        Vector3 SeedMerchantPosition,
        bool SeedMerchantResolved,
        uint TrackedCropObjId,
        uint TrackedCropTemplateId,
        Func<Character, uint, Doodad?>? DoodadResolver)
    {
        /// <summary>The common case: no tracking facts beyond the actor's own state.</summary>
        public static Request Bare(Vector3 selfPosition, uint seedItemTemplateId)
            => new(
                SelfPosition: selfPosition,
                SeedItemTemplateId: seedItemTemplateId,
                SeedReadable: true,
                SeedCount: 0,
                CropEnRoute: false,
                SoilEnRoute: false,
                CropApproachAttempts: 0,
                MaxCropApproachAttempts: NeedsBrain.DefaultMaxCropApproachAttempts,
                HarvestJustLanded: false,
                HarvestRangeM: NeedsBrain.DefaultHarvestRangeM,
                SoilAttempts: 0,
                MaxSoilAttempts: NeedsBrain.DefaultMaxSoilAttempts,
                SoilProbe: null,
                GroundZ: null,
                SoilDestination: null,
                SoilDestinationResolved: false,
                SeedMerchantPosition: Vector3.Zero,
                SeedMerchantResolved: false,
                TrackedCropObjId: 0,
                TrackedCropTemplateId: 0,
                DoodadResolver: null);
    }

    /// <summary>
    /// Builds the wake's inputs and evaluates the decision. Writes nothing: the
    /// caller banks the crop track (a landed plant) and the soil destination (the
    /// point it armed a route to) through its own state.
    /// </summary>
    public static Prepared Prepare(IGameplayActor actor, in Request request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var character = actor.Character;
        if (character == null)
        {
            // No character to read: every read is UNREADABLE, so the ordinary chain
            // decides the named hold (never a special-cased verdict of its own).
            var blind = EmptyInputs(actor.ActorId, request.SelfPosition) with
            {
                CropObjId = request.TrackedCropObjId,
                CropTemplateId = request.TrackedCropTemplateId
            };
            return new Prepared(blind, NeedsBrain.Decide(blind), null, 0, null);
        }

        var position = character.Transform.World.Position;

        // ------------------------------------------------------ tracked crop read
        // The cheap per-wake liveness check (one world lookup + the crop rule + the
        // maturity read — never a scan). The scan for an untracked crop is the
        // caller's discovery step (it decides whether tracking is desired), so this
        // adapter reads only the crop the caller hands it and never invents one.
        var cropObjId = request.TrackedCropObjId;
        var cropTemplateId = request.TrackedCropTemplateId;
        var cropState = NeedsCropState.None;
        var cropMature = false;
        Doodad? crop = null;
        var cropPosition = Vector3.Zero;
        var cropDistance = float.NaN;
        try
        {
            crop = request.TrackedCropObjId != 0
                ? Resolve(request.DoodadResolver, character, request.TrackedCropObjId)
                : null;
            if (request.TrackedCropObjId != 0)
            {
                if (crop == null || !NeedsCrop.IsTrackedLive(crop, character, cropTemplateId))
                {
                    // Provably not ours any more. The CALLER drops the track; the
                    // chain falls through to the seed arms this wake.
                    cropState = NeedsCropState.Stale;
                    cropObjId = 0;
                    cropTemplateId = 0;
                    crop = null;
                }
                else
                {
                    cropState = NeedsCropState.Live;
                    cropTemplateId = crop.TemplateId;
                    cropPosition = crop.Transform.World.Position;
                    cropMature = NeedsCrop.IsMature(crop);
                    cropDistance = MathUtil.CalculateDistance(position, cropPosition, false);
                }
            }
        }
        catch
        {
            // The crop's live read itself failed: a named hold, never a fabricated
            // "gone" (the caller keeps the track and retries next wake).
            cropState = NeedsCropState.Unreadable;
        }

        // ------------------------------------------------------ seed read
        var seedReadable = request.SeedReadable;
        var seedCount = request.SeedCount;
        if (seedReadable && request.SeedItemTemplateId != 0)
        {
            try
            {
                seedCount = character.Inventory?.GetItemsCount(SlotType.Inventory, request.SeedItemTemplateId) ?? 0;
            }
            catch
            {
                seedReadable = false;
                seedCount = 0;
            }
        }

        // ------------------------------------------------------ soil read
        var seedItemTemplateId = request.SeedItemTemplateId;
        var probe = request.SoilProbe
            ?? (Func<Character, Vector3, bool?>)((c, p) => NeedsSoil.Probe(c.ParentWorld, p, seedItemTemplateId));
        bool? underfoot;
        try
        {
            underfoot = probe(character, position);
        }
        catch
        {
            underfoot = null;
        }

        // The surface is READABLE when the point underfoot itself answered; that is
        // what authorises a plant or a resolve. (A deeper search that found no
        // plantable candidate is "no soil nearby", a different named fact.)
        var soilReadable = underfoot != null;
        var onValidSoil = underfoot == true;

        var merchant = request.SeedMerchantResolved ? request.SeedMerchantPosition : (Vector3?)null;
        Vector3? destination = null;
        var probesReadable = 0;
        if (soilReadable && seedCount > 0 && !onValidSoil)
        {
            // THE ONE DESTINATION AUTHORITY: the bounded spiral. A caller-supplied
            // destination is honoured ONLY when it explicitly says the spiral
            // already ran (the ordinary caller leaves that false) — so the fixture
            // seam can map a SURFACE but never invent a DESTINATION.
            if (request.SoilDestinationResolved)
            {
                destination = request.SoilDestination != null
                    ? request.SoilDestination(character, position)
                    : null;
                probesReadable = destination != null ? 1 : 0;
            }
            else
            {
                var groundZ = request.GroundZ ?? (Func<Character, Vector3, Vector3>)DefaultGroundZ;
                var search = NeedsSoil.ResolveDestination(
                    position,
                    candidate => SafeProbe(probe, character, candidate),
                    merchant,
                    (candidate, fallbackZ) => groundZ(character, candidate));
                destination = search.Destination;
                probesReadable = search.ReadableProbes;

                // A search in which NOT ONE candidate's plantability could be decided
                // is an UNREAD surface, not a proven absence: the wake must name the
                // fail-closed hold rather than assert "no soil here" on a surface
                // nobody could read. (A search that read candidates and found none
                // plantable stays a genuine absence.)
                if (destination == null && probesReadable == 0)
                    soilReadable = false;
            }
        }

        var inputs = new NeedsBrainInputs(
            ActorObjId: actor.ActorId,
            SelfPosition: position,
            SoilReadable: soilReadable,
            SeedReadable: seedReadable,
            SeedCount: seedCount,
            OnValidSoil: onValidSoil,
            CropState: cropState,
            CropMature: cropMature,
            CropDistanceM: cropDistance,
            CropPosition: cropPosition,
            CropEnRoute: request.CropEnRoute,
            CropObjId: cropObjId,
            CropTemplateId: cropTemplateId,
            CropApproachAttempts: request.CropApproachAttempts,
            MaxCropApproachAttempts: request.MaxCropApproachAttempts,
            HarvestJustLanded: request.HarvestJustLanded,
            SoilEnRoute: request.SoilEnRoute,
            SoilResolved: destination.HasValue,
            SoilDestination: destination ?? Vector3.Zero,
            SoilAttempts: request.SoilAttempts,
            MaxSoilAttempts: request.MaxSoilAttempts,
            HarvestRangeM: request.HarvestRangeM);

        return new Prepared(inputs, NeedsBrain.Decide(inputs), destination, probesReadable, crop);
    }

    /// <summary>
    /// THE CALLER'S LEG MAPPING: turns the wake's decision into the route-layer
    /// demand the caller should act on — the point to arm a <c>BotPath.PathTo</c>
    /// leg to, or none. Deliberately requires the ACTUAL <see cref="NeedsVerb.MoveTo"/>
    /// verb: a Travel verdict that already has a leg walking its point (the
    /// en-route holds) must NOT be re-armed, or the caller would restart progress
    /// every wake. The plant/harvest verbs are dispatched by the existing needs leg,
    /// unchanged.
    /// </summary>
    public static bool TryGetTravelTarget(in Prepared prepared, out Vector3 destination)
    {
        destination = prepared.Decision.Destination ?? Vector3.Zero;
        return prepared.Decision.Verb == NeedsVerb.MoveTo && prepared.Decision.HasDestination;
    }

    // ------------------------------------------------------------------ live reads

    private static Doodad? Resolve(Func<Character, uint, Doodad?>? resolver, Character character, uint objId)
        => resolver != null
            ? resolver(character, objId)
            : character.ParentWorld?.GetDoodad(objId);

    private static bool? SafeProbe(Func<Character, Vector3, bool?> probe, Character character, Vector3 candidate)
    {
        try
        {
            return probe(character, candidate);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ground-pins a resolved destination: the spiral carries the anchor's Z, but
    /// terrain along the walk can sit below it, and the actor's MoveTo arrival gate
    /// is 3D — so a stale-Z target would never complete. 0 height = no data → keep
    /// the anchor Z.
    /// </summary>
    private static Vector3 DefaultGroundZ(Character character, Vector3 candidate)
    {
        float groundZ;
        try
        {
            var terrain = WorldManager.PeekInstance?.GetTerrainHeight(character.Transform.ZoneId, candidate.X, candidate.Y);
            groundZ = terrain is { } th && th != 0f
                ? th
                : WorldManager.PeekInstance?.GetReferenceHeight(
                    null, candidate.X, candidate.Y, candidate.Z, character.Transform.ZoneId) ?? 0f;
        }
        catch
        {
            groundZ = 0f;
        }
        return groundZ != 0f ? new Vector3(candidate.X, candidate.Y, groundZ) : candidate;
    }

    private static NeedsBrainInputs EmptyInputs(uint actorObjId, Vector3 position)
        => new(
            ActorObjId: actorObjId, SelfPosition: position,
            SoilReadable: false, SeedReadable: false, SeedCount: 0, OnValidSoil: false,
            CropState: NeedsCropState.None, CropMature: false, CropDistanceM: float.NaN,
            CropPosition: Vector3.Zero, CropEnRoute: false, CropObjId: 0, CropTemplateId: 0,
            CropApproachAttempts: 0, MaxCropApproachAttempts: 0, HarvestJustLanded: false,
            SoilEnRoute: false, SoilResolved: false, SoilDestination: Vector3.Zero,
            SoilAttempts: 0, MaxSoilAttempts: 0, HarvestRangeM: 0f);
}
