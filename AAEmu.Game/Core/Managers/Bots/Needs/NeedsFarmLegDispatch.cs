#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Needs;

/// <summary>
/// The needs brain's LIVE DISPATCH CALLER seam: it wires an EXISTING caller (the
/// Tier 0 needs-farm leg) onto <see cref="NeedsBrain.Decide"/> →
/// <see cref="NeedsBrainPlanner"/>, so the brain is no longer a decision surface
/// only unit tests drive.
///
/// TWO VERDICT TABLES, ONE PER ARM KIND — the same leg, two shapes of work:
///  - <see cref="DecideNeedsTravelLeg"/> serves the leg's TRAVEL arm: the one
///    destination the leg walks through the EXISTING route layer;
///  - <see cref="DecideNeedsFarmAction"/> serves the leg's ACTION arms: the EXISTING
///    plant/harvest verbs, plus the named maturity wait that dispatches nothing.
///
/// Split of ownership (the same discipline <c>TravelLegDispatch</c> follows):
///  - the CALLER owns the wake, the live reads and the leg it issues — it drives
///    the planner from its own seams and state (the soil probe, the ground pin, the
///    merchant exclusion, the tracked-crop resolve) and walks the destination the
///    decision names through the EXISTING route layer;
///  - the BRAIN owns WHAT this wake asks for — <see cref="NeedsBrainPlanner.Prepare"/>
///    runs the whole chain over one <see cref="NeedsBrainPlanner.Prepared"/>. The
///    DESTINATION is always the bounded spiral's own output, because this caller
///    never sets <see cref="NeedsBrainPlanner.Request.SoilDestinationResolved"/>, so
///    a fabricated point cannot reach a travel leg through it;
///  - EACH TABLE owns its arm's verb mapping — the one place the brain's verbs and
///    this leg's own routing meet.
///
/// Fail-closed + additive. The TRAVEL table routes only the SOIL-TRAVEL arm (the
/// existing <see cref="NeedsVerb.MoveTo"/> to a point the spiral produced). The
/// ACTION table routes only the three verdicts the decision names a verb for:
/// <see cref="NeedsVerdict.Harvest"/>, <see cref="NeedsVerdict.Plant"/> and the
/// no-verb <see cref="NeedsVerdict.WaitMaturity"/>. Every other verdict in either
/// table falls back to the caller's pre-brain rule: a NAMED hold (an unreadable
/// surface, seed count or crop read), the bounded-spiral demand, a spent budget, or
/// an arm this caller has nothing to serve. A caller that cannot read the actor at
/// all gets a fallback verdict with no decision, so the brain never fabricates one.
/// </summary>
internal static class NeedsFarmLegDispatch
{
    /// <summary>
    /// What this leg should do with the brain's verdict, in the leg's own
    /// vocabulary: walk a soil destination the brain resolved, or leave the wake to
    /// the caller's pre-brain rule.
    /// </summary>
    internal enum NeedsTravelVerb
    {
        /// <summary>Walk <see cref="NeedsTravelVerdict.Destination"/> through the existing route layer.</summary>
        WalkSoil = 0,

        /// <summary>No leg this wake: the caller's own pre-brain rule owns it.</summary>
        Fallback = 1
    }

    /// <summary>
    /// One wake's verdict for the caller: the verb, the <c>dispatch=</c> token, the
    /// <c>reason=</c> the leg's own lane vocabulary uses, the destination to walk,
    /// the brain decision that produced it (<c>null</c> only when no decision could
    /// be made at all), and whether the brain's own reading already covers this
    /// leg's pre-brain soil search.
    /// </summary>
    internal readonly record struct NeedsTravelVerdict(
        NeedsTravelVerb Verb,
        string DispatchToken,
        string Reason,
        Vector3? Destination,
        NeedsBrainDecision? Decision,
        bool SearchCovered)
    {
        /// <summary>True when the brain asked for the soil-travel leg and named a point for it.</summary>
        public bool HasLeg => Verb == NeedsTravelVerb.WalkSoil && Destination.HasValue;

        /// <summary>True when this wake routed through the brain (rather than its fallback).</summary>
        public bool Routed => Verb == NeedsTravelVerb.WalkSoil;
    }

    /// <summary>
    /// THE CALLER'S VERDICT TABLE — the one place the needs decision and this leg's
    /// own routing meet.
    ///
    /// Only the SOIL-TRAVEL arm routes:
    /// <see cref="NeedsVerdict.Travel"/> named <see cref="NeedsReason.SoilResolved"/>
    /// with the existing <see cref="NeedsVerb.MoveTo"/> verb and a point. A crop
    /// approach is the same verb and is deliberately NOT routed — this leg has no
    /// crop to serve, so serving one would dispatch a leg the wake never earned.
    ///
    /// <see cref="NeedsTravelVerdict.SearchCovered"/> is the fail-closed direction
    /// made cheap: the bounded-spiral demand means the planner walked this leg's own
    /// spiral with this leg's own probe THIS wake and found no plantable candidate,
    /// so its absent destination IS the pre-brain rule's outcome and the caller must
    /// not run that same search a second time. The named unreadable surface is
    /// deliberately NOT covered: that arm is the point read failing, which says
    /// nothing about the spiral's candidates, so the caller's own rule still owns
    /// them.
    /// </summary>
    internal static NeedsTravelVerdict DecideNeedsTravelLeg(NeedsBrainPlanner.Prepared? brain)
    {
        if (brain is not { } prepared)
            return new NeedsTravelVerdict(
                NeedsTravelVerb.Fallback, "fallback", "brain-absent", null, null, SearchCovered: false);

        var decision = prepared.Decision;
        if (decision.Verdict == NeedsVerdict.Travel
            && decision.Reason == NeedsReason.SoilResolved
            && NeedsBrainPlanner.TryGetTravelTarget(prepared, out var destination))
        {
            return new NeedsTravelVerdict(
                NeedsTravelVerb.WalkSoil, "walk", NeedsBrain.Token(decision.Reason),
                destination, decision, SearchCovered: true);
        }

        return new NeedsTravelVerdict(
            NeedsTravelVerb.Fallback, "fallback", NeedsBrain.Token(decision.Reason), null, decision,
            SearchCovered: decision.Verdict == NeedsVerdict.SeekSoil);
    }

    /// <summary>
    /// Which of the leg's two ACTION arms is asking. The arm SCOPES the table, and
    /// that scoping is load-bearing: a Harvest wake must never route a Plant (the
    /// mature-harvest starvation guard the existing leg encodes as
    /// <c>offerPlant: false</c>) and the plant arm has no crop to harvest.
    /// </summary>
    internal enum NeedsFarmArm
    {
        /// <summary>The on-valid-soil + seed arm: the leg's own Plant dispatch site.</summary>
        Plant = 0,

        /// <summary>The mature-crop-in-range arm: the leg's own Harvest dispatch site.</summary>
        Harvest = 1
    }

    /// <summary>
    /// What the caller's action arm should do with the brain's verdict, in the leg's
    /// own vocabulary: dispatch the existing Plant or Harvest verb, hold on the named
    /// maturity wait (dispatching nothing at all), or leave the wake to the caller's
    /// pre-brain rule.
    /// </summary>
    internal enum NeedsFarmActionVerb
    {
        /// <summary>No verb this wake: the caller's own pre-brain rule owns it.</summary>
        Fallback = 0,

        /// <summary>The existing <c>IGameplayActor.Plant</c>, on the soil underfoot.</summary>
        Plant = 1,

        /// <summary>The existing <c>IGameplayActor.Harvest</c>, on <see cref="NeedsFarmActionVerdict.CropObjId"/>.</summary>
        Harvest = 2,

        /// <summary>The tracked crop is immature: HOLD — no harvest, no plant, no verb.</summary>
        WaitMaturity = 3
    }

    /// <summary>
    /// One wake's verdict for the caller's action arm: the verb, the
    /// <c>dispatch=</c> token, the <c>reason=</c> the leg's own lane vocabulary uses,
    /// the crop a harvest would address, and the brain decision that produced it
    /// (<c>null</c> only when no decision could be made at all). No destination: the
    /// action arms map VERBS, never points.
    /// </summary>
    internal readonly record struct NeedsFarmActionVerdict(
        NeedsFarmActionVerb Verb,
        string DispatchToken,
        string Reason,
        uint CropObjId,
        NeedsBrainDecision? Decision)
    {
        /// <summary>True when this wake routed through the brain (rather than its fallback).</summary>
        public bool Routed => Verb != NeedsFarmActionVerb.Fallback;

        /// <summary>True when the brain named the maturity wait: this wake dispatches NOTHING.</summary>
        public bool IsWait => Verb == NeedsFarmActionVerb.WaitMaturity;

        /// <summary>True when the brain confirmed one of the two existing dispatch verbs for this arm.</summary>
        public bool HasLeg => Verb is NeedsFarmActionVerb.Plant or NeedsFarmActionVerb.Harvest;
    }

    /// <summary>
    /// THE ACTION-ARM VERDICT TABLE — the second place the needs decision and this
    /// leg's own routing meet, for the two arms that DISPATCH a gameplay verb.
    ///
    /// The three routed rows, and only these:
    ///  - <see cref="NeedsVerdict.Harvest"/> with the existing
    ///    <see cref="NeedsVerb.Harvest"/> verb ON A NAMED CROP (the harvest arm): the
    ///    mature-in-range wake harvests through the leg's existing tail;
    ///  - <see cref="NeedsVerdict.Plant"/> with the existing
    ///    <see cref="NeedsVerb.Plant"/> verb (the plant arm): the on-soil + seed wake
    ///    plants through the same tail;
    ///  - <see cref="NeedsVerdict.WaitMaturity"/> (arm-independent, because it is the
    ///    one verdict that STOPS a dispatch): the crop is not mature, so the wake
    ///    holds with the wait named and issues nothing.
    ///
    /// Fail-closed, in the direction that matters: a Harvest verdict with NO crop
    /// objId cannot be addressed, so it falls back rather than harvesting 0; the arm
    /// scoping keeps a Harvest wake from ever routing a Plant (the starvation guard);
    /// and every other verdict — a named hold (an unreadable surface, seed count or
    /// crop read), the bounded-spiral demand, a spent budget, a travel verdict this
    /// arm does not walk — falls back to the caller's exact pre-brain rule, so the
    /// decision rides ADDITIVELY and never fabricates a dispatch.
    /// </summary>
    internal static NeedsFarmActionVerdict DecideNeedsFarmAction(
        NeedsBrainPlanner.Prepared? brain, NeedsFarmArm arm)
    {
        if (brain is not { } prepared)
            return new NeedsFarmActionVerdict(
                NeedsFarmActionVerb.Fallback, "fallback", "brain-absent", 0, null);

        var decision = prepared.Decision;

        // The maturity wait is checked FIRST and is the only row that is not scoped to
        // an arm: it is the verdict that must be able to stop a dispatch, and it
        // carries the crop it held on so the caller keeps its track.
        if (decision.Verdict == NeedsVerdict.WaitMaturity
            && decision.Verb == NeedsVerb.Hold
            && decision.CropObjId != 0)
        {
            return new NeedsFarmActionVerdict(
                NeedsFarmActionVerb.WaitMaturity, "wait-maturity",
                NeedsBrain.Token(decision.Reason), decision.CropObjId, decision);
        }

        if (arm == NeedsFarmArm.Harvest
            && decision.Verdict == NeedsVerdict.Harvest
            && decision.Verb == NeedsVerb.Harvest
            && decision.CropObjId != 0)
        {
            return new NeedsFarmActionVerdict(
                NeedsFarmActionVerb.Harvest, "harvest",
                NeedsBrain.Token(decision.Reason), decision.CropObjId, decision);
        }

        if (arm == NeedsFarmArm.Plant
            && decision.Verdict == NeedsVerdict.Plant
            && decision.Verb == NeedsVerb.Plant)
        {
            return new NeedsFarmActionVerdict(
                NeedsFarmActionVerb.Plant, "plant",
                NeedsBrain.Token(decision.Reason), 0, decision);
        }

        return new NeedsFarmActionVerdict(
            NeedsFarmActionVerb.Fallback, "fallback",
            NeedsBrain.Token(decision.Reason), 0, decision);
    }

    /// <summary>
    /// The wake's ACTION routing fact for the caller's observability, space-free so a
    /// lane parser keeps working: the brain's own decision description plus whether
    /// this arm routed through it and which dispatch it named. Built on read only.
    /// </summary>
    internal static string Describe(in NeedsFarmActionVerdict verdict)
        => verdict.Decision is { } decision
            ? $"{decision.Describe()}:routed={(verdict.Routed ? "true" : "false")}" +
              $":dispatch={verdict.DispatchToken}"
            : $"verdict=none:reason={verdict.Reason}:routed=false:dispatch={verdict.DispatchToken}";

    /// <summary>
    /// The wake's routing fact for the caller's observability, space-free so a lane
    /// parser keeps working: the brain's own decision description plus whether this
    /// leg routed through it, and whether the brain already answered this leg's own
    /// soil search. Built on read only.
    /// </summary>
    internal static string Describe(in NeedsTravelVerdict verdict)
        => verdict.Decision is { } decision
            ? $"{decision.Describe()}:routed={(verdict.Routed ? "true" : "false")}" +
              $":dispatch={verdict.DispatchToken}:search-covered={(verdict.SearchCovered ? "true" : "false")}"
            : $"verdict=none:reason={verdict.Reason}:routed=false:dispatch={verdict.DispatchToken}" +
              ":search-covered=false";
}
