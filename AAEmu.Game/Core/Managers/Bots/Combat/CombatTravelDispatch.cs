#nullable enable

using System.Numerics;

using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.Game.Core.Managers.Bots.Combat;

/// <summary>
/// The combat brain's KITE-SPACING caller seam: it wires the combat leg's
/// sub-critical spacing wake (the ranged kite arm, <see cref="CombatArm.BackOff"/>)
/// onto <see cref="TravelBrain.Decide"/> → <see cref="TravelIntentStore"/> →
/// <see cref="TravelBrainPlanner"/>, so the combat spacing leg no longer issues its
/// own ad-hoc retreat <c>Move</c> and instead rides the wired travel layer —
/// exactly the discipline <c>TravelLegDispatch</c> established for the quest return
/// leg and <c>NeedsFarmLegDispatch</c> for the needs farm leg.
///
/// WHY ONLY THE KITE ARM (the ownership boundary, and the reason the name says
/// "kite" rather than "disengage"):
///  - the CRITICAL flee is not this seam's. <c>CombatBrain</c>'s disengage arm
///    (<see cref="CombatArm.Disengage"/>) publishes the observable
///    <c>Disengaging</c> fact for EVERY reason, and the survival layer carries that
///    fact forward as its own <c>CombatRetreat</c> flee
///    (<c>SurvivalBrain</c> arm 2) — which is why the executor's elected-wake gate
///    owns that wake and the survival flee leg already walks the SAME safe anchor.
///    Routing it here too would be a duplicate leg fighting for one wake, so this
///    seam deliberately does not serve it;
///  - the CROWD-CONTROL hold (<see cref="CombatArm.CrowdControl"/>) and the
///    survival veto (<see cref="CombatArm.SurvivalVeto"/>) ask for no leg at all;
///  - that leaves the KITE, the one combat spacing arm that is provably
///    sub-critical AND unowned: it fires only when the desired band's FLOOR is
///    undercut (a ranged archer/caster too close to shoot), the disengage arm has
///    already declined the wake, and the brain publishes it as <c>Engaged</c> — so
///    no survival condition is standing and no other leg is walking to that anchor.
///    See <see cref="IsSubCriticalSpacing"/>.
///
/// Split of ownership (the same discipline the sibling caller seams follow):
///  - the CALLER owns WHEN a kite journey exists — <see cref="EnsureJourney"/> arms
///    one the first time the kite needs room and leaves it alone afterwards (so the
///    intent's counters and its sticky verdict survive across wakes exactly as
///    <see cref="TravelIntentStore"/> documents), and <see cref="EndJourney"/> is
///    the caller's own boundary (the kite got its room, or the threat identity is
///    gone), which keeps a banked verdict from outliving the reason it was reached;
///  - the BRAIN owns WHAT this wake asks for — <see cref="Prepare"/> runs the whole
///    chain over one <see cref="TravelBrainPlanner.Prepared"/> and banks the
///    decision facts through <see cref="TravelBrainPlanner.Publish"/>. A SETTLED
///    intent is re-read as its own named terminal instead of being decided again
///    (the store's documented stickiness, which is what makes "never a bare
///    navigation failure" true for a consumer);
///  - the CALLER owns the leg it actually issued — <see cref="PublishDispatched"/>
///    banks the mode/destination from the DISPATCH site, so a verdict that loses
///    selection never leaves a leg behind that never ran.
///
/// Fail-closed + additive: a wake that is not the sub-critical kite, or one whose
/// journey could not be armed (no actor identity — <see cref="TravelIntentStore.Arm"/>
/// refuses id 0), gets <see cref="KiteLegVerdict.Fallback"/> and keeps the caller's
/// exact pre-brain rule (the brain's own <c>RetreatDestination</c>, which is the same
/// <see cref="TravelBrain.SafeAnchor"/> shape). A chain verdict this leg cannot serve
/// withdraws NAMING the cause (a terminal token, or the unserved verb), and an
/// abandonment withdraws with its NAMED terminal — never a bare failure.
/// </summary>
internal static class CombatTravelDispatch
{
    /// <summary>The kite leg's movement-owner tag (<c>GameplayActor.SetPendingMoveOwner</c> telemetry).</summary>
    internal const string KiteMoveOwner = "COMBAT_KITE_MOVE";

    /// <summary>
    /// The kite journey's arrival gate. The retreat arm deliberately precedes the
    /// arrival read (the anchor is derived from live positions, so it must never be
    /// withheld for a distance), so this radius only ever serves a NON-retreat
    /// verdict on the same journey; the shared travel default is the honest value.
    /// </summary>
    internal const float KiteArrivalRadiusM = TravelBrain.DefaultArrivalRadiusM;

    /// <summary>The kite journey's repath budget (the shared travel default).</summary>
    internal const int KiteRepathBudget = TravelBrain.DefaultRepathBudget;

    // ---------------------------------------------------------------- journey

    /// <summary>
    /// Arms the caller's kite journey for <paramref name="threatObjId"/> when it does
    /// not exist yet (or names another target — another journey), and otherwise leaves
    /// the armed intent exactly as it is.
    ///
    /// The journey is keyed by this leg's OWNER (<see cref="KiteMoveOwner"/>), so a
    /// kite journey and another leg's journey on the same actor (the corpse approach,
    /// the quest return/pursuit legs) are separate rows: neither wake nor either
    /// leg's <c>EndJourney</c> can wipe the other's counters, route or terminal.
    ///
    /// This is the store's documented retry path: a journey's counters and its sticky
    /// verdict are the caller's to keep across wakes, and only an explicit re-arm
    /// starts clean. Re-arming on every wake would hand the receding anchor a fresh
    /// budget each time, because <see cref="TravelIntentStore.Arm"/> treats a moved
    /// destination as a new journey.
    /// </summary>
    /// <returns>True when a journey is armed for this threat; false when the arm was refused (no actor identity, or no threat identity).</returns>
    internal static bool EnsureJourney(IGameplayActor actor, uint threatObjId, Vector3 threatPosition)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (threatObjId == 0)
            return false;
        if (TravelIntentStore.TryGet(actor.ActorId, out var armed, KiteMoveOwner)
            && armed.Kind == TravelTargetKind.Unit
            && armed.TargetObjId == threatObjId)
            return true;

        return TravelBrainPlanner.Arm(
            actor.ActorId, TravelTargetKind.Unit, threatObjId, threatPosition,
            followRequested: false, arrivalRadiusM: KiteArrivalRadiusM, repathBudget: KiteRepathBudget,
            legOwner: KiteMoveOwner);
    }

    /// <summary>
    /// The caller's journey boundary: the kite no longer needs room, so the next wake
    /// that needs a leg arms a clean one (a fresh budget and no inherited verdict).
    /// Called by the caller on the wakes where it withdraws for a cause OUTSIDE the
    /// travel layer — the kite arm stopped firing, or the threat identity is gone.
    ///
    /// Scoped to this leg's owner (<see cref="KiteMoveOwner"/>): it drops only the
    /// kite's own journey and leaves every other leg's armed on the same actor.
    /// </summary>
    internal static bool EndJourney(IGameplayActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return TravelIntentStore.Disarm(actor.ActorId, KiteMoveOwner);
    }

    // ---------------------------------------------------------------- decision

    /// <summary>
    /// Runs one wake of the travel decision for the caller's kite journey.
    ///
    /// The threat position the caller passes is only the arming point: the retreat arm
    /// derives the anchor from the positions THIS wake carries, so a stale snapshot
    /// only ever matters if the caller hands one in.
    /// </summary>
    /// <param name="actor">The live actor.</param>
    /// <param name="threatObjId">The committed target's live objId (the journey's identity).</param>
    /// <param name="threatPosition">The caller's own resolve of the threat this wake.</param>
    /// <param name="liveLegDestination">
    /// The caller's reading of the leg it dispatched: the destination of the kite's
    /// own live <c>Move</c> leg, or <c>null</c> when it has none in flight. The kite's
    /// anchor RECEDES every wake by construction (it is recomputed from the actor's own
    /// position), so the chain's own leg-hold arm — which the retreat arm precedes —
    /// cannot serve it: the hold discipline arrives as this one reading and is applied
    /// by <see cref="DecideKiteLeg"/>, exactly as the survival flee gate applies it to
    /// its own receding anchor. A leg outcome is deliberately not claimed: a
    /// timed-out leg re-issues either way.
    /// </param>
    /// <returns>Null when no journey could be armed (the caller's own fallback rule owns the wake).</returns>
    internal static TravelBrainPlanner.Prepared? Prepare(
        IGameplayActor actor, uint threatObjId, Vector3 threatPosition, Vector3? liveLegDestination)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!EnsureJourney(actor, threatObjId, threatPosition))
            return null;

        var prepared = TravelBrainPlanner.Prepare(actor, new TravelBrainPlanner.Request(
            Kind: TravelTargetKind.Unit,
            TargetObjId: threatObjId,
            Destination: threatPosition,
            FollowRequested: false,
            // A unit target is resolved through the actor's own world, so there is
            // nothing to pre-flight for it — its world key is the actor's by
            // construction (a fabricated mismatch is exactly what the planner's
            // honesty contract forbids).
            DestWorldKnown: false,
            DestWorldId: 0,
            DestInstanceId: 0,
            ArrivalRadiusM: KiteArrivalRadiusM,
            LocalModeMaxM: TravelBrain.DefaultLocalModeMaxM,
            RepathBudget: KiteRepathBudget,
            AllowRoute: false,
            LegOutcome: liveLegDestination.HasValue ? TravelLegOutcome.Running : TravelLegOutcome.None,
            LegLive: liveLegDestination.HasValue,
            LegTargetObjId: threatObjId,
            LegDestinationKnown: liveLegDestination.HasValue,
            LegDestination: liveLegDestination ?? Vector3.Zero,
            // THE RETREAT ARM: a sub-critical kite asks for ROOM, so the travel brain
            // walks the shared safe anchor opposite the threat — the same
            // TravelBrain.SafeAnchor the combat brain's fallback destination and the
            // survival flee leg use, never a second copy of the escape ray.
            RetreatRequested: true,
            ThreatObjId: threatObjId,
            ThreatPosition: threatPosition), legOwner: KiteMoveOwner);

        // THE STORE'S STICKINESS, made real for this consumer: a journey that already
        // reached a terminal re-reads that NAMED verdict instead of deciding again, so
        // a terminated destination can never be re-derived as a bare failure (or
        // silently resumed) later in the same journey.
        if (TravelIntentStore.TryGet(actor.ActorId, out var armed, KiteMoveOwner) && armed.IsSettled)
            prepared = prepared with { Decision = SettledTerminal(armed) };

        TravelBrainPlanner.Publish(actor.ActorId, prepared, KiteMoveOwner);
        return prepared;
    }

    /// <summary>
    /// Whether a combat decision is the SUB-CRITICAL KITE this seam serves — the one
    /// combat spacing arm the survival layer does not already own.
    ///
    /// Every <see cref="CombatArm.Disengage"/> wake publishes the observable
    /// <c>Disengaging</c> fact, and the survival layer carries that fact forward as its
    /// own flee (so a disengage — critical hp, critical-with-no-escape, or a leash edge
    /// — already has a leg walking the same anchor, and duplicating it would put two
    /// legs on one wake). The survival veto and the crowd-control hold ask for no leg
    /// at all. The kite is what remains: the ranged/caster spacing arm that fires when
    /// the band FLOOR is undercut, published as <c>Engaged</c> with no retreat fact
    /// standing.
    /// </summary>
    internal static bool IsSubCriticalSpacing(in CombatBrainDecision decision)
        => decision.Arm == CombatArm.BackOff && decision.Verb == CombatVerb.Move;

    /// <summary>
    /// What this leg should do with the travel chain's verdict, in the leg's own
    /// vocabulary. Every member maps onto an EXISTING verb: a <see cref="Move"/> and a
    /// <see cref="Stop"/> ride <c>IGameplayActor.MoveTo</c>/<c>Stop</c>, and
    /// <see cref="Held"/>/<see cref="Withdraw"/>/<see cref="Fallback"/> issue nothing.
    /// </summary>
    internal enum KiteLegVerb
    {
        /// <summary>No leg from this seam: the caller's own pre-brain rule owns the wake.</summary>
        Fallback = 0,

        /// <summary>The travel retreat arm's anchor leg (the existing <c>MoveTo</c> verb).</summary>
        Move = 1,

        /// <summary>The audited halt (the chain's arrival arm on this journey).</summary>
        Stop = 2,

        /// <summary>No leg this wake (a live leg keeps its progress).</summary>
        Held = 3,

        /// <summary>No leg, and no kite: the journey reached a named terminal (or asks for a verb this seam cannot serve).</summary>
        Withdraw = 4
    }

    /// <summary>
    /// One wake's verdict for the caller: the verb, the <c>dispatch=</c> token and
    /// <c>reason=</c> the leg's own lane vocabulary uses, the terminal it names when
    /// the journey ended, the destination a leg would walk (the travel layer's own on
    /// the routed path, the brain's own on the fallback path), and the travel decision
    /// that produced it (<c>null</c> on the fallback path).
    /// </summary>
    internal readonly record struct KiteLegVerdict(
        KiteLegVerb Verb,
        string DispatchToken,
        string Reason,
        string? Terminal,
        Vector3? Destination,
        TravelDecision? Decision)
    {
        /// <summary>True when this verdict asks the actor for a leg (a Hold/Withdraw/Fallback asks for none from this seam).</summary>
        public bool HasLeg => Verb is KiteLegVerb.Move or KiteLegVerb.Stop;

        /// <summary>True when the TRAVEL CHAIN produced this verdict (false only for the caller's own pre-brain fallback).</summary>
        public bool Routed => Verb != KiteLegVerb.Fallback;

        /// <summary>True when this verdict names a travel terminal (the fail-closed, never-bare-failure arm).</summary>
        public bool IsTerminal => Terminal != null;
    }

    /// <summary>
    /// THE CALLER'S VERDICT TABLE — the one place the travel decision and this leg's
    /// own routing meet. The leg's lane tokens (<c>dispatch=move|stop|held|withdrawn</c>
    /// and the <c>reason=</c> vocabulary) mirror the sibling caller seams, and the
    /// travel brain's own arm/reason/terminal ride additively in the caller's
    /// <c>:travel=</c> fragment (the same key <c>PursuitEmit</c> and <c>ReturnEmit</c>
    /// print, so one lane parser reads every travel-routed leg).
    ///
    /// Fail-closed in every direction:
    ///  - a wake that is not the sub-critical kite, and a wake whose journey could not
    ///    be armed, both FALL BACK (the caller's exact pre-brain rule — which is the
    ///    brain's own <c>RetreatDestination</c>, the same
    ///    <see cref="TravelBrain.SafeAnchor"/> shape), never a fabricated leg;
    ///  - an abandonment withdraws NAMING its terminal (never a bare failure, never a
    ///    leg to an abandoned destination);
    ///  - a withhold for a cause that is NOT progress withdraws NAMING the reason, and
    ///    an unexpected verb withdraws rather than dispatching something this seam
    ///    cannot serve.
    /// </summary>
    /// <param name="brain">The travel chain's wake preparation, or null when no journey could be armed.</param>
    /// <param name="decision">The combat brain's own decision for this wake (the fallback destination, and the sub-criticality verdict).</param>
    internal static KiteLegVerdict DecideKiteLeg(
        TravelBrainPlanner.Prepared? brain, in CombatBrainDecision decision)
    {
        if (!IsSubCriticalSpacing(decision))
        {
            // The caller's own pre-brain rule owns the wake; the brain's own
            // destination (its <c>RetreatDestination</c> anchor on a disengage) rides
            // along so that fallback leg is byte-identical to the pre-brain behavior.
            return new KiteLegVerdict(
                KiteLegVerb.Fallback, "fallback", "not-sub-critical-spacing",
                null, decision.Destination, null);
        }

        if (brain is not { } prepared)
            return new KiteLegVerdict(
                KiteLegVerb.Fallback, "fallback", "no-journey", null, decision.Destination, null);

        var travel = prepared.Decision;
        if (travel.IsAbandon)
            return new KiteLegVerdict(
                KiteLegVerb.Withdraw, "withdrawn", $"terminal-{TravelBrain.Token(travel.Terminal)}",
                TravelBrain.Token(travel.Terminal), null, travel);

        switch (travel.Verb)
        {
            case TravelVerb.MoveTo when KiteLegAlreadyServes(prepared, travel):
                // The kite's anchor RECEDES as the bot walks, and the chain's own
                // leg-hold arm sits BEHIND the retreat arm — so the hold is applied
                // here, from the caller's own reading of the leg it issued: a live leg
                // already pointing at (within tolerance of) this wake's anchor keeps its
                // progress instead of restarting the escape every wake.
                return new KiteLegVerdict(
                    KiteLegVerb.Held, "held", "anchor-held", null, null, travel);
            case TravelVerb.MoveTo:
                return new KiteLegVerdict(
                    KiteLegVerb.Move, "move", ReasonToken(travel.Reason), null, travel.Destination, travel);
            case TravelVerb.Stop:
                return new KiteLegVerdict(
                    KiteLegVerb.Stop, "stop", "in-range", null, null, travel);
            case TravelVerb.Hold when travel.Reason == TravelReason.LegLive:
                // A live leg that still serves this anchor keeps its progress: the
                // caller's own held token, exactly as the sibling seams print it.
                return new KiteLegVerdict(
                    KiteLegVerb.Held, "held", "drift-held", null, null, travel);
            case TravelVerb.Hold:
                // A withhold for a cause that is NOT progress (no journey, an
                // unreadable distance): withdraw NAMING the fact rather than claiming
                // the leg is being held on purpose.
                return new KiteLegVerdict(
                    KiteLegVerb.Withdraw, "withdrawn", ReasonToken(travel.Reason),
                    TerminalOf(travel), null, travel);
            default:
                // A verb this seam does not serve (a unit-relative leg, a route leg):
                // withdraw naming the fact rather than dispatching it blind.
                return new KiteLegVerdict(
                    KiteLegVerb.Withdraw, "withdrawn", $"unexpected-verb-{travel.Verb}",
                    TerminalOf(travel), null, travel);
        }
    }

    /// <summary>
    /// Whether the caller's own live kite leg still serves THIS wake's anchor — the
    /// hold discipline the retreat arm's position in the chain means this seam has to
    /// apply itself. Reads the leg destination the caller reported and compares it to
    /// the anchor this wake would issue, under the shared
    /// <see cref="TravelBrain.LegDriftToleranceM"/> (the same tolerance the chain's own
    /// leg-hold arm uses). An unreadable leg destination reads as NOT tracking, so a
    /// leg whose point could not be read is re-issued rather than assumed to be right.
    /// </summary>
    private static bool KiteLegAlreadyServes(
        in TravelBrainPlanner.Prepared prepared, in TravelDecision travel)
    {
        if (!prepared.Inputs.LegLive || !prepared.Inputs.LegDestinationKnown)
            return false;
        return travel.Destination is { } anchor
               && TravelBrainInputs.IsFinite(anchor)
               && Vector3.Distance(prepared.Inputs.LegDestination, anchor) <= TravelBrain.LegDriftToleranceM;
    }

    /// <summary>
    /// Banks the leg the caller actually DISPATCHED (its mode and destination). A
    /// fallback verdict carries no travel decision and banks nothing.
    /// </summary>
    internal static void PublishDispatched(IGameplayActor actor, TravelDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (decision.HasValue)
            TravelBrainPlanner.PublishDispatched(actor, decision.Value, KiteMoveOwner);
    }

    /// <summary>
    /// The kite's own reading of the leg it dispatched, for the journey's leg input:
    /// true only while OUR kite Move leg is still in flight. A foreign leg (another
    /// owner, another action) is never read as ours — the same ownership discipline
    /// the sibling seams' leg checks apply.
    /// </summary>
    internal static bool IsOurLiveLeg(ActorRequest? request)
        => request is { IsTerminal: false, Action: ActorActionType.Move, MoveOwner: KiteMoveOwner };

    /// <summary>
    /// The wake's routing fact for the caller's observability, space-free and
    /// bracket-free so a lane parser keeps working: the travel brain's own decision
    /// description plus whether this leg routed through it and which dispatch it named.
    /// Built on read only.
    /// </summary>
    internal static string Describe(in KiteLegVerdict verdict)
    {
        var terminal = verdict.Terminal is { } name ? $":terminal={name}" : "";
        return verdict.Decision is { } decision
            ? $"{decision.Describe()}:routed=true:dispatch={verdict.DispatchToken}:reason={verdict.Reason}{terminal}"
            : $"verdict=none:reason={verdict.Reason}:routed=false:dispatch={verdict.DispatchToken}{terminal}";
    }

    // ---------------------------------------------------------------- mapping

    /// <summary>The leg's own <c>reason=</c> token for a travel reason (space-free, lowercase, this leg's vocabulary).</summary>
    private static string ReasonToken(TravelReason reason) => reason switch
    {
        TravelReason.Retreat => "retreat",
        TravelReason.LegLive => "leg-live",
        TravelReason.DriftRetrack => "retrack",
        TravelReason.Repathed => "repathed",
        TravelReason.RepathExhausted => "unreachable",
        TravelReason.Arrived => "arrived",
        TravelReason.FollowInPosition => "in-position",
        TravelReason.NoIntent => "no-journey",
        TravelReason.WrongWorld => "wrong-world",
        TravelReason.TargetUnresolved => "target-unresolved",
        TravelReason.TargetGone => "target-gone",
        TravelReason.DistanceUnreadable => "distance-unreadable",
        _ => "withheld"
    };

    /// <summary>The named terminal a withdrawing verdict carries (<c>null</c> while the journey is still live).</summary>
    private static string? TerminalOf(in TravelDecision decision)
        => decision.Terminal == TravelTerminal.None ? null : TravelBrain.Token(decision.Terminal);

    /// <summary>
    /// The decision shape a SETTLED intent re-reads: the same named terminal and reason
    /// the store banked, carried on the arm that reached it (so the diagnostic names
    /// the real arm rather than a fabricated one). Mirrors
    /// <c>TravelLegDispatch</c>'s own settled re-read, which is private to that caller.
    /// </summary>
    private static TravelDecision SettledTerminal(in TravelIntentStore.Intent intent)
    {
        var arm = intent.Terminal switch
        {
            TravelTerminal.WrongWorld => TravelArm.PreFlight,
            TravelTerminal.TargetGone => TravelArm.TargetProbe,
            _ => TravelArm.Repath
        };
        return new TravelDecision(
            arm, TravelVerb.Hold, intent.Mode, intent.Terminal, intent.Reason,
            intent.TargetObjId, null, float.NaN, 0, intent.RepathCount);
    }
}
