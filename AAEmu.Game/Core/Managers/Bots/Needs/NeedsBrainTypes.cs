#nullable enable

using System.Globalization;
using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Needs;

/// <summary>
/// The needs layer's verdict for ONE wake — the six demands the Tier 0
/// needs-farm loop can make, plus the one <see cref="Hold"/> that asks for
/// nothing:
///  - <see cref="SeekSoil"/> — seed on hand, standing off valid soil, and no
///    destination resolved yet: RESOLVE a soil destination (the bounded spiral);
///  - <see cref="Travel"/> — a destination is known (resolved soil, an
///    already-walked soil leg, or a mature crop out of harvest range): WALK it
///    through the existing route layer;
///  - <see cref="Plant"/> — seed on hand and standing on valid soil: PLANT;
///  - <see cref="WaitMaturity"/> — a tracked crop is live but immature: WAIT at
///    the plot (the engine stays the sole maturity authority at dispatch);
///  - <see cref="Harvest"/> — a tracked crop is live and mature, inside harvest
///    range: HARVEST it;
///  - <see cref="Replant"/> — a harvest landed this wake: RE-EVALUATE next wake
///    (seed + output → replant), never a scripted harvest-then-plant chain;
///  - <see cref="Hold"/> — this layer asks nothing (no needs work, the existing
///    buy leg owns the wake, or the farm state could not be read).
///
/// There is deliberately no <c>Failed</c> member and no <c>Unknown</c> member:
/// every wake resolves to one of these with a named <see cref="NeedsReason"/>,
/// and an unreadable farm read degrades to <see cref="Hold"/> with a named
/// reason — never to a fabricated verdict and never to a fabricated destination.
/// </summary>
public enum NeedsVerdict
{
    /// <summary>This layer asks nothing of the actor this wake.</summary>
    Hold = 0,

    /// <summary>Resolve a soil destination for the seed on hand (bounded spiral).</summary>
    SeekSoil = 1,

    /// <summary>Walk the resolved destination through the existing route layer.</summary>
    Travel = 2,

    /// <summary>Plant the seed on hand on the valid soil underfoot.</summary>
    Plant = 3,

    /// <summary>A tracked crop is immature: wait at the plot, issue no harvest.</summary>
    WaitMaturity = 4,

    /// <summary>A tracked crop is mature and in harvest range: harvest it.</summary>
    Harvest = 5,

    /// <summary>A harvest landed this wake: re-evaluate next wake.</summary>
    Replant = 6
}

/// <summary>
/// The actor-face verb a needs decision asks for — the EXISTING
/// <c>IGameplayActor</c> verbs only, and only the three this increment can
/// honestly name: <c>MoveTo</c> (the travel leg to a resolved destination),
/// <c>Plant</c>, and <c>Harvest</c>. <see cref="Hold"/> asks for nothing, which
/// is how every non-dispatching verdict reports itself.
///
/// The DEMAND only: <c>NeedsBrain</c> never executes, never writes a Character
/// field and never opens a gameplay path of its own — the existing route layer
/// walks the travel leg and the existing <c>NeedsDecisionScenario</c> leg
/// dispatches the plant/harvest, exactly as they already do.
/// </summary>
public enum NeedsVerb
{
    /// <summary>No verb (the verdict asks the actor for nothing).</summary>
    Hold = 0,

    /// <summary>The existing <c>IGameplayActor.MoveTo</c>, to <see cref="NeedsBrainDecision.Destination"/>.</summary>
    MoveTo = 1,

    /// <summary>The existing <c>IGameplayActor.Plant</c>, on the soil underfoot.</summary>
    Plant = 2,

    /// <summary>The existing <c>IGameplayActor.Harvest</c>, on <see cref="NeedsBrainDecision.CropObjId"/>.</summary>
    Harvest = 3
}

/// <summary>
/// What the caller's own tracking says about the crop this wake is about —
/// deliberately NOT a boolean pair, so the three distinct facts stay distinct:
///  - <see cref="None"/> — no crop is tracked and no crop was found (a fact);
///  - <see cref="Live"/> — the tracked crop resolved and is still OURS (the
///    maturity read then decides wait/approach/harvest);
///  - <see cref="Stale"/> — the tracked objId is gone, despawn-scheduled, a
///    different template, or no longer ours: the track is dropped and the wake
///    re-evaluates (the executor's existing drop-and-fall-through discipline);
///  - <see cref="Unreadable"/> — the crop's live read itself failed. This is NOT
///    <see cref="Stale"/>: a failed read never fabricates "gone", it holds with
///    a named reason.
/// </summary>
public enum NeedsCropState
{
    /// <summary>No crop tracked, none found — the seed/soil arms own the wake.</summary>
    None = 0,

    /// <summary>The tracked crop resolved and is still ours.</summary>
    Live = 1,

    /// <summary>The tracked crop is provably not ours any more (gone/recycled/foreign).</summary>
    Stale = 2,

    /// <summary>The crop's live read could not be made.</summary>
    Unreadable = 3
}

/// <summary>
/// The named reason a verdict was reached — an ENUM rather than a built string,
/// so a per-wake decision allocates nothing (the same discipline
/// <c>TravelReason</c>, <c>LootReason</c> and <c>SurvivalReason</c> follow);
/// <see cref="NeedsBrain.Token(NeedsReason)"/> maps each member onto the
/// space-free token a lane line can carry. Every member names its own cause, so
/// no verdict is ever a bare fail.
/// </summary>
public enum NeedsReason
{
    /// <summary>No reason recorded (the sentinel a default-constructed value reads as).</summary>
    None = 0,

    /// <summary>The soil read could not be made — a named hold, no fabricated destination.</summary>
    SoilUnreadable = 1,

    /// <summary>The seed-count read could not be made — a named hold.</summary>
    SeedUnreadable = 2,

    /// <summary>The tracked crop's live read failed — a named hold, never a fabricated "gone".</summary>
    CropUnreadable = 3,

    /// <summary>No soil destination was resolvable and the discard budget is spent: hold, never spin.</summary>
    SoilResolveBudgetSpent = 4,

    /// <summary>No seed on hand: the existing buy leg owns the wake (a hold, not a failure).</summary>
    SeedAbsent = 5,

    /// <summary>Seed on hand and standing on valid soil: plant.</summary>
    SeedOnSoil = 6,

    /// <summary>Seed on hand, off valid soil, nothing resolved yet: seek a soil destination.</summary>
    NoSoilNearby = 7,

    /// <summary>A soil destination was resolved by the bounded spiral: walk it.</summary>
    SoilResolved = 8,

    /// <summary>The caller's route leg already walks the resolved soil destination: hold the leg.</summary>
    SoilEnRoute = 9,

    /// <summary>A tracked crop is live but immature: wait at the plot.</summary>
    CropImmature = 10,

    /// <summary>A tracked crop is live, mature and in harvest range: harvest.</summary>
    CropMature = 11,

    /// <summary>A mature crop sits outside harvest range: walk to it through the route layer.</summary>
    CropApproach = 12,

    /// <summary>The caller's route leg already walks the mature crop: hold the leg.</summary>
    CropApproachEnRoute = 13,

    /// <summary>A mature crop stayed out of reach and the approach budget is spent: hold, no harvest issued.</summary>
    CropUnreachable = 14,

    /// <summary>The crop's distance could not be measured: hold, never a fabricated harvest or approach.</summary>
    CropDistanceUnreadable = 15,

    /// <summary>A harvest landed this wake: re-evaluate next wake (seed + output → replant).</summary>
    HarvestCompleted = 16
}

/// <summary>
/// Every input the needs decision chain reads for one wake — the soil reads, the
/// seed count, the tracked crop's liveness/maturity/distance, and the caller's
/// own leg/budget facts, and nothing else.
///
/// Value type: the live adapter (<see cref="NeedsBrainPlanner"/>) builds it from
/// the actor's own character record plus the shared soil/crop resolvers, and the
/// brain never stores it. There is no world handle, no clock, no perception type
/// and no actor: the same inputs always decide the same wake.
///
/// Honesty contract (the decision layer's own fail-closed rules):
///  - <see cref="SoilReadable"/>/<see cref="SeedReadable"/> false and
///    <see cref="NeedsCropState.Unreadable"/> are named holds — a read that could
///    not be made never fabricates a destination, a seed count, or a "gone" crop;
///  - a crop distance that could not be measured is NaN, never 0 — a fabricated 0
///    would authorise a harvest on a crop that might be hundreds of meters away;
///  - <see cref="SoilResolved"/> with <see cref="SoilDestination"/> is the ONLY
///    way a soil destination reaches the chain, so a caller can never inject one
///    that the bounded spiral did not produce;
///  - an "en route" flag is the caller's own reading of the leg IT issued: it
///    never upgrades a missing destination into a resolved one.
/// </summary>
public readonly record struct NeedsBrainInputs(
    uint ActorObjId,
    Vector3 SelfPosition,
    bool SoilReadable,
    bool SeedReadable,
    int SeedCount,
    bool OnValidSoil,
    NeedsCropState CropState,
    bool CropMature,
    float CropDistanceM,
    Vector3 CropPosition,
    bool CropEnRoute,
    uint CropObjId,
    uint CropTemplateId,
    int CropApproachAttempts,
    int MaxCropApproachAttempts,
    bool HarvestJustLanded,
    bool SoilEnRoute,
    bool SoilResolved,
    Vector3 SoilDestination,
    int SoilAttempts,
    int MaxSoilAttempts,
    float HarvestRangeM)
{
    /// <summary>True when this layer has a live tracked/scanned crop to act on.</summary>
    public bool HasLiveCrop => CropState == NeedsCropState.Live;

    /// <summary>True when the crop's distance could be measured (a NaN distance never authorises a harvest).</summary>
    public bool HasCropDistance => !float.IsNaN(CropDistanceM) && !float.IsInfinity(CropDistanceM);

    /// <summary>
    /// The interaction range inside which a mature crop is harvestable — the
    /// caller's value when finite and positive, else
    /// <see cref="NeedsBrain.DefaultHarvestRangeM"/>.
    /// </summary>
    public float HarvestRange => float.IsFinite(HarvestRangeM) && HarvestRangeM > 0f
        ? HarvestRangeM
        : NeedsBrain.DefaultHarvestRangeM;

    /// <summary>The crop-approach discard bound — the caller's, else the layer's own default.</summary>
    public int CropApproachBudget => MaxCropApproachAttempts > 0
        ? MaxCropApproachAttempts
        : NeedsBrain.DefaultMaxCropApproachAttempts;

    /// <summary>The soil-destination discard bound — the caller's, else the layer's own default.</summary>
    public int SoilBudget => MaxSoilAttempts > 0 ? MaxSoilAttempts : NeedsBrain.DefaultMaxSoilAttempts;
}

/// <summary>
/// One wake's needs decision: the verdict, the named reason, the verb (with the
/// destination or crop it acts on), and the demand's own measurements —
/// everything a dispatcher and a lane log need, and nothing that executes
/// gameplay itself.
///
/// <see cref="Destination"/> is non-null ONLY when the verb needs a point (the
/// travel arms): a verb-less verdict deliberately carries none, so no consumer
/// can read a destination the brain never asked to walk.
/// </summary>
public readonly record struct NeedsBrainDecision(
    NeedsVerdict Verdict,
    NeedsReason Reason,
    NeedsVerb Verb,
    uint CropObjId,
    uint CropTemplateId,
    Vector3? Destination,
    float DistanceM)
{
    /// <summary>True when this decision asks the actor for a verb.</summary>
    public bool HasVerb => Verb != NeedsVerb.Hold;

    /// <summary>True while this layer asks the actor for nothing.</summary>
    public bool IsHold => Verdict == NeedsVerdict.Hold;

    /// <summary>True when the decision carries a point for the caller's leg.</summary>
    public bool HasDestination => Destination.HasValue;

    /// <summary>True while a live crop is being waited on (no harvest issued).</summary>
    public bool IsWaitMaturity => Verdict == NeedsVerdict.WaitMaturity;

    /// <summary>True while a mature crop is being harvested.</summary>
    public bool IsHarvest => Verdict == NeedsVerdict.Harvest;

    /// <summary>True while a landed harvest is being re-evaluated.</summary>
    public bool IsReplant => Verdict == NeedsVerdict.Replant;

    /// <summary>True while the decision walks a destination through the existing route layer.</summary>
    public bool IsTravel => Verdict == NeedsVerdict.Travel;

    /// <summary>True when the verdict can only proceed once a new soil destination is resolved.</summary>
    public bool IsSeekSoil => Verdict == NeedsVerdict.SeekSoil;

    /// <summary>
    /// Compact, space-free diagnostic token for the wake line: no spaces and no
    /// square brackets, so a lane parser scanning a DECIDE bracket keeps working.
    /// Built on read only.
    /// </summary>
    public string Describe()
        => $"verdict={NeedsBrain.Token(Verdict)}:reason={NeedsBrain.Token(Reason)}:verb={NeedsBrain.Token(Verb)}" +
           $":crop={CropObjId}:dest={(Destination.HasValue ? "set" : "-")}:dist={Fmt(DistanceM)}";

    private static string Fmt(float value)
        => float.IsNaN(value) ? "NA" : value.ToString("F2", CultureInfo.InvariantCulture);
}
