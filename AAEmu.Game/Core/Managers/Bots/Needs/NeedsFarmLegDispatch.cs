#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Needs;

/// <summary>
/// The needs brain's FIRST LIVE DISPATCH CALLER seam: it wires an EXISTING caller
/// (the Tier 0 needs-farm leg's soil-travel arm) onto
/// <see cref="NeedsBrain.Decide"/> → <see cref="NeedsBrainPlanner"/>, so the brain
/// is no longer a decision surface only unit tests drive.
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
///  - THIS SEAM owns the verb mapping — the one place the brain's verbs and this
///    leg's own routing meet.
///
/// Fail-closed + additive: only the SOIL-TRAVEL arm routes (the existing
/// <see cref="NeedsVerb.MoveTo"/> to a point the spiral produced). Every other
/// verdict falls back to the caller's pre-brain rule: a NAMED hold (an unreadable
/// soil surface or seed count), the bounded-spiral demand, a spent budget, or a
/// crop arm this leg has no crop to serve. A caller that cannot read the actor at
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
