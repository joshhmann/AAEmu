#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Travel;

/// <summary>
/// The TRAVEL BRAIN — one wake's navigation decision for one destination,
/// derived in a fixed chain from an immutable <see cref="TravelBrainInputs"/>:
/// the intent gate, the ONE dispatch-time world pre-flight, the retreat move, the
/// distance/arrival read, the moving target's single re-resolve, the repath /
/// abandon rule, and the mode select (local / road route / follow).
///
/// Scope (binding for this increment):
///  - PURE: no engine query, no world scan, no mutation, no clock read — every
///    number and verdict arrives on the inputs, and the same inputs always decide
///    the same wake. Every live read (the target resolve, the world-key
///    pre-flight, the road route) lives in <see cref="TravelBrainPlanner"/>; the
///    per-actor intent memory lives in <see cref="TravelIntentStore"/> and arrives
///    as inputs;
///  - VERBS ONLY: the decision names one of the EXISTING actor verbs
///    (<c>IGameplayActor.MoveTo</c>, <c>MoveToUnit</c>, <c>Stop</c>) and its
///    argument. No new verb, no second gameplay path, no priority change — the
///    caller's dispatcher issues the leg;
///  - NOTHING ELSE: the brain decides destination and mode for an intent the
///    CALLER armed; it never decides whether to go, never touches engine movement
///    integration (<c>GameplayActor.Tick</c>), and never reads the belief or
///    perception stacks;
///  - FAIL-CLOSED, in the direction each verdict's own evidence allows: an
///    unreadable distance is never read as arrival, an unknown destination world
///    key is never read as a mismatch OR a match, an unresolved moving target is
///    not a gone one (it gets exactly one more resolve), and a missing road route
///    is never unreachability (a local leg serves far destinations too — only a
///    spent repath budget abandons).
///
/// The chain, in evaluation order (the first arm that applies owns the wake):
///   1. INTENT             — no travel intent armed → hold;
///   2. PRE-FLIGHT         — the ONE world-key check at dispatch: another world
///                           instance can never be walked to → abandon WrongWorld;
///   3. RETREAT            — a disengage/kite asks for the safe anchor: walk 25 m
///                           opposite the threat (the shared <see cref="SafeAnchor"/>
///                           shape CombatBrain's flee leg uses);
///   4. TARGET PROBE       — a moving target that did not resolve: one re-resolve
///                           is allowed, the second miss is the terminal
///                           TargetGone (probed before the distance read, so a
///                           stale last-known position can never read as arrival);
///   5. ARRIVAL            — an unmeasurable distance/destination → hold; inside
///                           the arrival radius → the audited Stop (Arrived for a
///                           position, station-hold for a follow);
///   6. REPATH             — a failed leg is re-selected (mode and destination)
///                           while the budget lasts, else the destination is
///                           abandoned Unreachable;
///   7. MODE SELECT        — follow (unit-relative) / route (road waypoint) /
///                           local (direct) — computed before the hold arm
///                           because the hold compares the live leg against the
///                           point this mode would actually issue;
///   8. LEG HOLD           — a live leg already tracking that point → hold (never
///                           restart progress); a drifted leg is re-issued.
/// </summary>
public static class TravelBrain
{
    /// <summary>
    /// Retreat anchor distance (engine precedent: <c>CombatDecisionTree.Evaluate</c>'s
    /// emergency flee steps 25 m; quoted as a literal because the decision paths
    /// may not reach into the combat tree).
    /// </summary>
    public const float RetreatAnchorDistanceM = 25.0f;

    /// <summary>
    /// The distance under which a position destination is served by a direct local
    /// leg instead of a road route: a short hop is cheaper to walk straight than to
    /// route through junctions that are nowhere near the line.
    /// </summary>
    public const float DefaultLocalModeMaxM = 60.0f;

    /// <summary>Default arrival radius (engine-mirrored: the quest pursuit's 3.0 m stop radius).</summary>
    public const float DefaultArrivalRadiusM = 3.0f;

    /// <summary>Default repath budget: a failed leg is re-selected this many times before the destination is abandoned.</summary>
    public const int DefaultRepathBudget = 2;

    /// <summary>The single re-resolve a moving target is allowed before its absence is terminal.</summary>
    public const int TargetResolveAllowance = 1;

    /// <summary>
    /// Drift beyond which a live leg no longer serves the point this wake would
    /// issue, so the leg is re-issued (the same 2.0 m discipline the quest pursuit's
    /// retrack gate uses, quoted as a literal because the travel layer does not
    /// reach into the quest behavior).
    /// </summary>
    public const float LegDriftToleranceM = 2.0f;

    /// <summary>
    /// True for the three terminals that ABANDON the destination. An abandoned
    /// intent must not be served again by the same arming: the verdict is banked
    /// and re-read, so a consumer can never see a bare "failed" navigation.
    /// </summary>
    public static bool IsAbandon(TravelTerminal terminal)
        => terminal is TravelTerminal.WrongWorld or TravelTerminal.TargetGone or TravelTerminal.Unreachable;

    /// <summary>
    /// The space-free token for a terminal, for the lane line and for unit tests
    /// that pin the vocabulary. Every abandonment names its own cause.
    /// </summary>
    public static string Token(TravelTerminal terminal) => terminal switch
    {
        TravelTerminal.Arrived => "arrived",
        TravelTerminal.WrongWorld => "wrong-world",
        TravelTerminal.TargetGone => "target-gone",
        TravelTerminal.Unreachable => "unreachable",
        _ => "none"
    };

    /// <summary>
    /// THE SHARED RETREAT ANCHOR: the point <paramref name="distanceM"/> meters from
    /// <paramref name="self"/> on the ray pointing AWAY from <paramref name="threat"/>
    /// — the one implementation of the flee step, used by this brain's retreat arm
    /// and by <c>CombatBrain.RetreatDestination</c>. Keeping it in one place is what
    /// makes the two retreat legs the same shape by construction rather than by
    /// coincidence.
    ///
    /// Fail-closed on unreadable input, exactly as the combat brain's flee leg is:
    /// an unset threat position falls back to a fixed reference point one meter to
    /// the actor's west (so the escape direction is still defined), and a
    /// degenerate/NaN direction — the actor standing on the threat — degrades to
    /// due east rather than producing a NaN destination the Move verb would refuse.
    /// A non-finite or non-positive distance falls back to
    /// <see cref="RetreatAnchorDistanceM"/>; it never lands the actor on the threat.
    /// </summary>
    public static Vector3 SafeAnchor(Vector3 self, Vector3 threat, float distanceM)
    {
        var from = threat != Vector3.Zero ? threat : self - new Vector3(1f, 0f, 0f);
        var direction = Vector3.Normalize(self - from);
        if (!IsUsableDirection(direction))
            direction = new Vector3(1f, 0f, 0f);
        var distance = float.IsFinite(distanceM) && distanceM > 0f ? distanceM : RetreatAnchorDistanceM;
        return self + direction * distance;
    }

    /// <summary>
    /// The wake's mode: a UNIT target always rides the unit-relative verb (the
    /// engine re-resolves the objId at request time, so a moving target is never
    /// chased through a stale position snapshot — the caller's follow flag only
    /// decides whether its arrival keeps station or completes); a Position
    /// destination beyond the local threshold with a route the adapter actually
    /// resolved rides the road waypoints; everything else is a direct local leg. An
    /// unreadable distance never fabricates a route choice — it reads local, and the
    /// arrival arm has already withheld on it.
    /// </summary>
    public static TravelMode SelectMode(
        TravelTargetKind kind, float distanceM, bool routeAvailable, float localModeMaxM)
    {
        if (kind == TravelTargetKind.Unit)
            return TravelMode.Follow;

        var threshold = float.IsFinite(localModeMaxM) && localModeMaxM > 0f
            ? localModeMaxM
            : DefaultLocalModeMaxM;
        if (routeAvailable && !float.IsNaN(distanceM) && distanceM > threshold)
            return TravelMode.Route;
        return TravelMode.Local;
    }

    /// <summary>
    /// Evaluates the wake's decision. Pure over the inputs: the caller owns the
    /// intent memory, the clock, and every world read that produced them.
    /// </summary>
    public static TravelDecision Decide(in TravelBrainInputs inputs)
    {
        // ------------------------------------------------ 1. INTENT
        if (!inputs.HasIntent)
            return Withhold(TravelArm.NoIntent, TravelReason.NoIntent, inputs);

        // ------------------------------------------------ 2. PRE-FLIGHT
        // The ONE world-key check, at dispatch: a destination in another world
        // instance is unwalkable, and saying so once here is what keeps a
        // cross-world route from ever becoming a bare, unexplained leg failure.
        // An UNKNOWN key is not a mismatch — nothing is claimed either way.
        if (inputs.WorldMismatch)
            return Terminal(TravelArm.PreFlight, TravelTerminal.WrongWorld, TravelReason.WrongWorld, inputs);

        // ------------------------------------------------ 3. RETREAT
        // A disengage/kite asked for room: walk the safe anchor opposite the
        // threat. This arm precedes the distance read because the anchor is
        // derived from live positions in the actor's OWN world — it needs no
        // destination distance and must never be withheld for one.
        if (inputs.RetreatRequested)
        {
            return new TravelDecision(
                TravelArm.Retreat, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
                TravelReason.Retreat, inputs.ThreatObjId,
                SafeAnchor(inputs.SelfPosition, inputs.ThreatPosition, RetreatAnchorDistanceM),
                inputs.DistanceM, 0, inputs.PriorRepathCount);
        }

        // ------------------------------------------------ 4. TARGET PROBE
        // A moving target that could not be resolved this wake has NO readable
        // destination: the caller's position is the last-known snapshot, and
        // deciding arrival against a stale point would end (or complete) a journey
        // whose target may simply have walked out of the resolve. The probe runs
        // BEFORE the distance read for exactly that reason: a miss gets one
        // re-resolve, and only the second consecutive miss is the terminal.
        if (inputs.TargetKind == TravelTargetKind.Unit && !inputs.TargetResolved)
        {
            return inputs.PriorResolveAttempts < TargetResolveAllowance
                ? Withhold(TravelArm.TargetProbe, TravelReason.TargetUnresolved, inputs)
                : Terminal(TravelArm.TargetProbe, TravelTerminal.TargetGone, TravelReason.TargetGone, inputs);
        }

        // ------------------------------------------------ 5. ARRIVAL
        // Fail-closed: an unmeasurable distance or an unusable destination point
        // never reads as arrival, and never authorises a leg to a NaN.
        if (!inputs.HasDistance || !inputs.HasDestination)
            return Withhold(TravelArm.Arrival, TravelReason.DistanceUnreadable, inputs);

        var arrivalRadius = float.IsFinite(inputs.ArrivalRadiusM) && inputs.ArrivalRadiusM > 0f
            ? inputs.ArrivalRadiusM
            : DefaultArrivalRadiusM;
        if (inputs.DistanceM <= arrivalRadius)
        {
            // A keep-station FOLLOW is not a finished journey: inside the station
            // distance the follower halts (the audited Stop) and the intent stays
            // LIVE, so the next wake keeps station instead of banking an arrival
            // that would end the follow. Every other destination is reached.
            var following = inputs.TargetKind == TravelTargetKind.Unit && inputs.FollowRequested;
            return new TravelDecision(
                TravelArm.Arrival, TravelVerb.Stop, inputs.PriorMode,
                following ? TravelTerminal.None : TravelTerminal.Arrived,
                following ? TravelReason.FollowInPosition : TravelReason.Arrived,
                inputs.TargetObjId, null, inputs.DistanceM, 0, inputs.PriorRepathCount);
        }

        // ------------------------------------------------ 6. REPATH
        // A leg that failed (navigation budget expired, or the mover was declared
        // stuck) is re-selected: a fresh mode/destination decision is the repath.
        // When the budget is spent the destination is abandoned UNREACHABLE — the
        // named terminal, never a bare failure.
        var legFailed = inputs.LegOutcome is TravelLegOutcome.TimedOut or TravelLegOutcome.Interrupted;
        var repathBudget = Math.Max(0, inputs.RepathBudget);
        if (legFailed && inputs.PriorRepathCount >= repathBudget)
            return Terminal(TravelArm.Repath, TravelTerminal.Unreachable, TravelReason.RepathExhausted, inputs);

        // ------------------------------------------------ 7. MODE SELECT
        // The mode depends only on the target kind, the distance and the resolved
        // route — never on leg state — so it is computed here, BEFORE the leg-hold
        // arm, because that arm must compare the live leg against the point this
        // wake would actually issue (a route's waypoint, a follow's live unit,
        // otherwise the destination). Comparing against the destination instead
        // would read a route leg as drifted every single wake.
        var mode = SelectMode(
            inputs.TargetKind, inputs.DistanceM,
            inputs.RouteAvailable && inputs.RouteWaypointCount > 0, inputs.LocalModeMaxM);
        var legPoint = mode switch
        {
            TravelMode.Route => inputs.RouteWaypoint,
            _ => inputs.Destination
        };

        // ------------------------------------------------ 8. LEG HOLD
        // A live leg that still tracks the point this wake would issue is progress:
        // re-issuing would restart it every wake. A live leg whose destination
        // DRIFTED falls through to the issue, which re-targets it.
        if (inputs.LegLive && LegTracksIssue(inputs, mode, legPoint))
            return Withhold(TravelArm.LegHold, TravelReason.LegLive, inputs);

        var reason = inputs.LegLive
            ? TravelReason.DriftRetrack
            : legFailed
                ? TravelReason.Repathed
                : mode switch
                {
                    TravelMode.Follow => TravelReason.FollowSelected,
                    TravelMode.Route => TravelReason.RouteSelected,
                    _ => TravelReason.LocalSelected
                };
        var repathCount = legFailed ? inputs.PriorRepathCount + 1 : inputs.PriorRepathCount;

        return mode switch
        {
            TravelMode.Follow => new TravelDecision(
                TravelArm.Follow, TravelVerb.MoveToUnit, TravelMode.Follow, TravelTerminal.None,
                reason, inputs.TargetObjId, inputs.Destination, inputs.DistanceM,
                inputs.RouteWaypointCount, repathCount),
            TravelMode.Route => new TravelDecision(
                TravelArm.Route, TravelVerb.MoveTo, TravelMode.Route, TravelTerminal.None,
                reason, inputs.TargetObjId, inputs.RouteWaypoint, inputs.DistanceM,
                inputs.RouteWaypointCount, repathCount),
            _ => new TravelDecision(
                TravelArm.Local, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
                reason, inputs.TargetObjId, inputs.Destination, inputs.DistanceM,
                inputs.RouteWaypointCount, repathCount)
        };
    }

    /// <summary>
    /// Whether the actor's live leg still serves the point this wake would issue:
    /// a unit leg on the SAME objId, or a position leg whose recorded destination
    /// has not drifted past <see cref="LegDriftToleranceM"/>. An unreadable leg
    /// destination reads as NOT tracking, so the leg is re-issued rather than
    /// assumed to be heading the right way.
    /// </summary>
    private static bool LegTracksIssue(in TravelBrainInputs inputs, TravelMode mode, Vector3 legPoint)
    {
        if (mode == TravelMode.Follow)
            return inputs.LegTargetObjId == inputs.TargetObjId;
        return inputs.LegDestinationKnown
               && TravelBrainInputs.IsFinite(legPoint)
               && Vector3.Distance(inputs.LegDestination, legPoint) <= LegDriftToleranceM;
    }

    /// <summary>Builds a verb-less decision (the leg withdraws with it as evidence).</summary>
    private static TravelDecision Withhold(TravelArm arm, TravelReason reason, in TravelBrainInputs inputs)
        => new(arm, TravelVerb.Hold, inputs.PriorMode, TravelTerminal.None, reason,
            inputs.TargetObjId, null, inputs.DistanceM, inputs.RouteWaypointCount, inputs.PriorRepathCount);

    /// <summary>Builds a verb-less TERMINAL decision (the three abandonment verdicts and arrival's Stop are built by their own arms).</summary>
    private static TravelDecision Terminal(
        TravelArm arm, TravelTerminal terminal, TravelReason reason, in TravelBrainInputs inputs)
        => new(arm, TravelVerb.Hold, inputs.PriorMode, terminal, reason,
            inputs.TargetObjId, null, inputs.DistanceM, inputs.RouteWaypointCount, inputs.PriorRepathCount);

    /// <summary>A usable escape direction: non-zero and fully finite (NaN never rides into a destination).</summary>
    private static bool IsUsableDirection(Vector3 value)
        => value != Vector3.Zero && float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
