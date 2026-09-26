#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Needs;

/// <summary>
/// The NEEDS BRAIN — one wake's needs-farm verdict for one actor, derived in a
/// fixed chain from an immutable <see cref="NeedsBrainInputs"/>: the landed
/// harvest, the tracked crop's liveness/maturity/distance, and the seed/soil
/// arms that follow it.
///
/// Scope (binding for this increment):
///  - PURE: no engine query, no world scan, no mutation, no clock read, no actor,
///    no perception type — every number and fact arrives on the inputs, and the
///    same inputs always decide the same wake. Every live read (the soil probe
///    and the bounded spiral, the seed count, the crop resolve/maturity/distance)
///    lives in <see cref="NeedsBrainPlanner"/>;
///  - VERBS ONLY, AND ONLY EXISTING ONES: the three verbs this layer can ask for
///    are the EXISTING <c>IGameplayActor.MoveTo</c>, <c>Plant</c> and
///    <c>Harvest</c>. No new verb is invented and no priority is changed;
///    <see cref="NeedsVerdict.SeekSoil"/> and <see cref="NeedsVerdict.WaitMaturity"/>
///    deliberately carry NO verb — resolving a destination is the planner's own
///    bounded spiral, and maturity-waiting is a demand the engine's own growth
///    task answers;
///  - DEMAND ONLY, EXECUTION ELSEWHERE: nothing here dispatches. The travel arms
///    are walked by the EXISTING route layer (<c>BotPath.PathTo</c> + the roam
///    MoveTo legs), and the plant/harvest arms are dispatched by the EXISTING
///    <c>NeedsDecisionScenario</c> leg — this layer names the demand and the point;
///  - FAIL-CLOSED: an unreadable soil surface, seed count or crop read is a NAMED
///    hold, never a fabricated verdict; a soil destination exists ONLY when the
///    caller's bounded spiral produced one, so no destination is ever fabricated;
///    an unmeasurable crop distance never authorises a harvest; a spent resolve or
///    approach budget holds instead of spinning.
///
/// The chain, in evaluation order (the first arm that applies owns the wake):
///   1. HARVEST LANDED — a harvest completed this wake → replant (re-evaluate);
///   2. CROP UNREADABLE — the tracked crop's live read failed → hold, named;
///   3. CROP DISTANCE   — a live crop whose distance could not be measured → hold;
///   4. CROP IMMATURE   — a live crop that is not yet mature → wait at the plot;
///   5. HARVEST         — a live MATURE crop inside harvest range → harvest;
///   6. CROP UNREACHABLE— a mature crop out of range with the budget spent → hold;
///   7. CROP APPROACH   — a mature crop out of range → walk to it (or hold the leg
///                        already walking it);
///   8. SEED UNREADABLE — no live crop and the seed count could not be read → hold;
///   9. SEED ABSENT     — no seed on hand → hold (the existing buy leg owns it);
///  10. SOIL UNREADABLE — seed on hand but the soil surface could not be read → hold;
///  11. PLANT           — seed on hand and standing on valid soil → plant;
///  12. SOIL TRAVEL     — a resolved soil destination → walk it (or hold the leg
///                        already walking it);
///  13. SOIL BUDGET     — no destination and the resolve budget is spent → hold;
///  14. SEEK SOIL       — no destination yet → resolve one (bounded spiral).
///
/// A STALE crop track (gone/despawned/recycled/foreign) is not a verdict of its
/// own: the caller drops the track and the wake falls through to the seed arms,
/// exactly as the existing needs-farm leg re-evaluates.
/// </summary>
public static class NeedsBrain
{
    /// <summary>
    /// The interaction range inside which a mature crop is harvestable (quoted
    /// from the live mature-crop leg's own <c>CropHarvestInteractRange</c>); a
    /// mature crop beyond it is approached first. One literal, every site.
    /// </summary>
    public const float DefaultHarvestRangeM = 2.5f;

    /// <summary>
    /// Crop-approach discards per episode before the leg stops re-arming and holds
    /// (the existing <c>NeedsFarmMaxSoilAttempts</c> discipline — one bound, both
    /// destinations).
    /// </summary>
    public const int DefaultMaxCropApproachAttempts = 3;

    /// <summary>
    /// Soil-destination discards per discovery episode before the resolve demand
    /// stops and holds (never spins forever).
    /// </summary>
    public const int DefaultMaxSoilAttempts = 3;

    /// <summary>The space-free token for a verdict, for the lane line and for unit tests that pin the vocabulary.</summary>
    public static string Token(NeedsVerdict verdict) => verdict switch
    {
        NeedsVerdict.SeekSoil => "seek-soil",
        NeedsVerdict.Travel => "travel",
        NeedsVerdict.Plant => "plant",
        NeedsVerdict.WaitMaturity => "wait-maturity",
        NeedsVerdict.Harvest => "harvest",
        NeedsVerdict.Replant => "replant",
        _ => "hold"
    };

    /// <summary>The space-free token for a verb.</summary>
    public static string Token(NeedsVerb verb) => verb switch
    {
        NeedsVerb.MoveTo => "move-to",
        NeedsVerb.Plant => "plant",
        NeedsVerb.Harvest => "harvest",
        _ => "hold"
    };

    /// <summary>The space-free token for a named reason. Every reason names its own cause; there is no bare "failed".</summary>
    public static string Token(NeedsReason reason) => reason switch
    {
        NeedsReason.SoilUnreadable => "soil-unreadable",
        NeedsReason.SeedUnreadable => "seed-unreadable",
        NeedsReason.CropUnreadable => "crop-unreadable",
        NeedsReason.SoilResolveBudgetSpent => "soil-resolve-budget-spent",
        NeedsReason.SeedAbsent => "seed-absent",
        NeedsReason.SeedOnSoil => "seed-on-soil",
        NeedsReason.NoSoilNearby => "no-soil-nearby",
        NeedsReason.SoilResolved => "soil-resolved",
        NeedsReason.SoilEnRoute => "soil-en-route",
        NeedsReason.CropImmature => "crop-immature",
        NeedsReason.CropMature => "crop-mature",
        NeedsReason.CropApproach => "crop-approach",
        NeedsReason.CropApproachEnRoute => "crop-approach-en-route",
        NeedsReason.CropUnreachable => "crop-unreachable",
        NeedsReason.CropDistanceUnreadable => "crop-distance-unreadable",
        NeedsReason.HarvestCompleted => "harvest-completed",
        _ => "none"
    };

    /// <summary>
    /// Evaluates the wake's decision. Pure over the inputs: the caller owns the
    /// soil/crop/seed reads and the leg facts, and every world read that produced
    /// them.
    /// </summary>
    public static NeedsBrainDecision Decide(in NeedsBrainInputs inputs)
    {
        // ------------------------------------------------ 1. HARVEST LANDED
        // A harvest completed this wake: the wake's verdict is the RE-EVALUATION,
        // never a scripted harvest-then-plant chain inside one wake. The next wake
        // reads the bag (seed + output) and decides from scratch.
        if (inputs.HarvestJustLanded)
            return new NeedsBrainDecision(
                NeedsVerdict.Replant, NeedsReason.HarvestCompleted, NeedsVerb.Hold,
                inputs.CropObjId, inputs.CropTemplateId, null, inputs.CropDistanceM);

        // ------------------------------------------------ 2. CROP UNREADABLE
        // The tracked crop's live read failed. This is NOT "the crop is gone": a
        // failed read that fabricated a drop would abandon a real crop, so it is
        // the named hold the caller resolves (or drops the track for) itself.
        if (inputs.CropState == NeedsCropState.Unreadable)
            return Hold(NeedsReason.CropUnreadable, inputs);

        // ------------------------------------------------ 3. CROP DISTANCE
        // A live crop whose distance could not be measured: neither a harvest (a
        // fabricated 0 would harvest a crop across the zone) nor an approach (the
        // point would be a fabrication). Hold, named.
        if (inputs.HasLiveCrop && !inputs.HasCropDistance)
            return Hold(NeedsReason.CropDistanceUnreadable, inputs);

        // ------------------------------------------------ 4. CROP IMMATURE
        // Live but not yet mature: WAIT at the plot. No harvest is issued — the
        // engine's own growth task stays the sole maturity authority, and the
        // caller's mature read is re-evaluated every wake.
        if (inputs.HasLiveCrop && !inputs.CropMature)
            return new NeedsBrainDecision(
                NeedsVerdict.WaitMaturity, NeedsReason.CropImmature, NeedsVerb.Hold,
                inputs.CropObjId, inputs.CropTemplateId, null, inputs.CropDistanceM);

        // ------------------------------------------------ 5. HARVEST
        // Live, mature, and inside the interaction range: the one crop verb.
        if (inputs.HasLiveCrop && inputs.CropMature && inputs.CropDistanceM <= inputs.HarvestRange)
            return new NeedsBrainDecision(
                NeedsVerdict.Harvest, NeedsReason.CropMature, NeedsVerb.Harvest,
                inputs.CropObjId, inputs.CropTemplateId, null, inputs.CropDistanceM);

        if (inputs.HasLiveCrop && inputs.CropMature)
        {
            // -------------------------------------------- 6. CROP UNREACHABLE
            // The mature crop stayed out of reach across the whole approach budget:
            // hold (never spin, never issue a harvest the engine would refuse for
            // range). The crop keeps its track so a later wake can retry.
            if (inputs.CropApproachAttempts >= inputs.CropApproachBudget)
                return Hold(NeedsReason.CropUnreachable, inputs);

            // -------------------------------------------- 7. CROP APPROACH
            // Out of range: walk to the crop. A leg already walking it is HELD
            // (verdict Travel, no verb) so the caller never restarts progress.
            return inputs.CropEnRoute
                ? new NeedsBrainDecision(
                    NeedsVerdict.Travel, NeedsReason.CropApproachEnRoute, NeedsVerb.Hold,
                    inputs.CropObjId, inputs.CropTemplateId, inputs.CropPosition, inputs.CropDistanceM)
                : new NeedsBrainDecision(
                    NeedsVerdict.Travel, NeedsReason.CropApproach, NeedsVerb.MoveTo,
                    inputs.CropObjId, inputs.CropTemplateId, inputs.CropPosition, inputs.CropDistanceM);
        }

        // ------------------------------------------------ 8-14. SEED ARMS
        // No live crop (none tracked, or the track was stale and the caller dropped
        // it): the seed/soil arms own the wake. The seed count is read BEFORE the
        // soil surface because a bot with no seed has no soil question to answer —
        // reporting a soil failure there would name the wrong cause.
        if (!inputs.SeedReadable)
            return Hold(NeedsReason.SeedUnreadable, inputs);

        if (inputs.SeedCount <= 0)
            return Hold(NeedsReason.SeedAbsent, inputs);

        // ------------------------------------------------ 10. SOIL UNREADABLE
        // Seed on hand but the plantable-surface read could not be made: a NAMED
        // hold, and crucially NO destination — the fail-closed direction, because a
        // fabricated soil destination would walk the bot somewhere the spiral never
        // proved plantable.
        if (!inputs.SoilReadable)
            return Hold(NeedsReason.SoilUnreadable, inputs);

        // ------------------------------------------------ 11. PLANT
        // Seed on hand and standing on valid soil: the one soil-to-action verdict.
        if (inputs.OnValidSoil)
            return new NeedsBrainDecision(
                NeedsVerdict.Plant, NeedsReason.SeedOnSoil, NeedsVerb.Plant,
                0, 0, null, float.NaN);

        // ------------------------------------------------ 12. SOIL TRAVEL
        // A destination the caller's BOUNDED SPIRAL produced. Walking it is the
        // demand; a leg already walking it is HELD (verdict Travel, no verb).
        if (inputs.SoilResolved)
        {
            return inputs.SoilEnRoute
                ? new NeedsBrainDecision(
                    NeedsVerdict.Travel, NeedsReason.SoilEnRoute, NeedsVerb.Hold,
                    0, 0, inputs.SoilDestination, float.NaN)
                : new NeedsBrainDecision(
                    NeedsVerdict.Travel, NeedsReason.SoilResolved, NeedsVerb.MoveTo,
                    0, 0, inputs.SoilDestination, float.NaN);
        }

        // ------------------------------------------------ 13. SOIL BUDGET
        if (inputs.SoilAttempts > inputs.SoilBudget)
            return Hold(NeedsReason.SoilResolveBudgetSpent, inputs);

        // ------------------------------------------------ 14. SEEK SOIL
        // No destination yet: resolve one through the bounded spiral. A DEMAND with
        // no verb and no destination — the caller owns the resolve and its bounds.
        return new NeedsBrainDecision(
            NeedsVerdict.SeekSoil, NeedsReason.NoSoilNearby, NeedsVerb.Hold,
            0, 0, null, float.NaN);
    }

    /// <summary>
    /// A hold that names its own cause: asks the actor for nothing, carries no
    /// destination, and preserves the crop this wake was about so a consumer can
    /// still see which track the hold concerned.
    /// </summary>
    private static NeedsBrainDecision Hold(NeedsReason reason, in NeedsBrainInputs inputs)
        => new(NeedsVerdict.Hold, reason, NeedsVerb.Hold,
            inputs.CropObjId, inputs.CropTemplateId, null, inputs.CropDistanceM);
}
