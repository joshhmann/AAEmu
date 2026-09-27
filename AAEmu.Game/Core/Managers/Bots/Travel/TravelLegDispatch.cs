#nullable enable

using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Travel;

/// <summary>
/// The travel brain's FIRST live dispatch caller seam: it wires an EXISTING
/// caller (the quest return leg) onto <see cref="TravelBrain.Decide"/> →
/// <see cref="TravelIntentStore"/> → <see cref="TravelBrainPlanner"/>, so the
/// brain is no longer a decision surface only unit tests drive.
///
/// Split of ownership (the same discipline the loot brain's caller seam follows):
///  - the CALLER owns WHEN a journey exists — <see cref="EnsureJourney"/> arms one
///    the first time the caller needs to close on a target and leaves it alone
///    afterwards, so the intent's counters and its sticky verdict survive across
///    wakes exactly as <see cref="TravelIntentStore"/> documents;
///    <see cref="EndJourney"/> is the caller's own journey boundary (the quest no
///    longer needs the leg, or the target identity is gone), and it is what keeps a
///    banked ABANDON verdict from outliving the reason it was reached;
///  - the BRAIN owns WHAT this wake asks for (the verb, the named terminal, the
///    repath budget) — <see cref="Prepare"/> runs the whole chain over one
///    <see cref="TravelBrainPlanner.Prepared"/> and banks the decision facts
///    through <see cref="TravelBrainPlanner.Publish"/>. A SETTLED intent is
///    re-read as its own named terminal instead of being decided again (the
///    store's documented stickiness, and what makes "never a bare navigation
///    failure" true for a consumer);
///  - the CALLER owns the leg it actually issued — <see cref="PublishDispatched"/>
///    banks the mode/destination (and spends the repath) from the DISPATCH site, so
///    a decision that loses selection never leaves a leg behind that never ran.
///
/// Fail-closed + additive: a caller that cannot own a journey (no actor identity,
/// so <see cref="TravelIntentStore.Arm"/> refuses) gets <c>null</c> from
/// <see cref="Prepare"/>, and <see cref="DecideReturnLeg"/> then falls back to the
/// caller's own pre-brain rule — the brain never fabricates a decision for an
/// intent that is not armed, and an unexpected verb withdraws rather than
/// dispatching something the caller cannot serve.
/// </summary>
internal static class TravelLegDispatch
{
    /// <summary>The quest return leg's movement-owner tag (<see cref="GameplayActor.SetPendingMoveOwner"/> telemetry).</summary>
    internal const string ReturnMoveOwner = "RETURN_MOVE_TO_UNIT";

    /// <summary>
    /// The quest PURSUIT leg's movement-owner tag (the value the roam executor's own
    /// supersede rule reads, quoted byte-identically so the two can never drift).
    /// </summary>
    internal const string PursuitMoveOwner = "PURSUIT_MOVE_TO_UNIT";

    /// <summary>
    /// The preemption detail the caller's own pursuit retrack stages
    /// (<c>QuestBehavior.DispatchPursuitMove</c>). Caller-authored: it retires a leg
    /// the caller itself decided to replace, never a navigation failure. Shared (not
    /// private) for the same reason <see cref="ReturnRetrackDetail"/> is.
    /// </summary>
    internal const string PursuitRetrackDetail = "quest pursuit retrack";

    /// <summary>
    /// How many of the actor's newest audit rows <see cref="MapLegOutcome"/> walks
    /// looking for the caller's own last leg. The trace is bounded (512 rows) and
    /// the caller's leg is always within a few rows of the tail.
    /// </summary>
    private const int TraceScanRows = 64;

    /// <summary>
    /// The preemption detail the caller's own return retrack stages
    /// (<c>QuestBehavior.DispatchReturnMove</c>). Caller-authored: it retires a
    /// leg the caller itself decided to replace, never a navigation failure.
    /// Shared (not private) so the staging site and <see cref="IsCallerRetirement"/>
    /// can never drift apart — a rename at either end would silently re-arm the
    /// repath-exhaustion bug.
    /// </summary>
    internal const string ReturnRetrackDetail = "quest return retrack";

    /// <summary>
    /// The preemption detail the survival flee stages over a live Move leg it is
    /// replacing (<c>BotRoamStepExecutor.StepSurvivalWake</c>). Caller-authored:
    /// it retires a leg the brain stack decided to replace, never a navigation failure.
    /// Shared (not private) so the staging site and <see cref="IsCallerRetirement"/>
    /// can never drift apart again — a rename re-arms the repath-exhaustion bug silently.
    /// </summary>
    internal const string SurvivalFleeRetrackDetail = "survival flee retrack";

    /// <summary>The halt detail the engine's own <c>Stop</c> stamps on the leg it interrupts.</summary>
    private const string StopDetail = "stop requested";

    /// <summary>The engine's own rejection detail for a leg whose destination unit is absent.</summary>
    private const string MissingUnitDetail = "target unit not found";

    /// <summary>
    /// The brain decision a dispatched proposal carries to its dispatch site, so
    /// the leg that actually RAN is the one banked (<c>null</c> for a proposal the
    /// caller's own fallback rule produced, which banks nothing).
    /// </summary>
    internal readonly record struct TravelDispatchParams(TravelDecision? Decision);

    // ---------------------------------------------------------------- journey

    /// <summary>
    /// Arms the caller's journey for <paramref name="targetObjId"/> when it does not
    /// exist yet (or names another target — another journey, a fresh budget), and
    /// otherwise leaves the armed intent exactly as it is.
    ///
    /// This is the store's documented retry path: a journey's counters and its
    /// sticky verdict are the caller's to keep across wakes, and only an explicit
    /// re-arm starts clean.
    /// </summary>
    /// <returns>True when a journey is armed for this target; false when the arm was refused (no actor identity).</returns>
    internal static bool EnsureJourney(
        IGameplayActor actor, uint targetObjId, Vector3 targetPosition,
        float arrivalRadiusM, int repathBudget, string legOwner = "")
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (TravelIntentStore.TryGet(actor.ActorId, out var armed, legOwner)
            && armed.Kind == TravelTargetKind.Unit
            && armed.TargetObjId == targetObjId)
            return true;

        return TravelBrainPlanner.Arm(
            actor.ActorId, TravelTargetKind.Unit, targetObjId, targetPosition,
            followRequested: true, arrivalRadiusM: arrivalRadiusM, repathBudget: repathBudget,
            legOwner: legOwner);
    }

    /// <summary>
    /// The caller's journey boundary: the journey this actor was walking is over,
    /// so the next wake that needs a leg arms a clean one (a fresh budget and no
    /// inherited verdict). Called by the caller on the wakes where it withdraws for
    /// a cause OUTSIDE the travel layer — the quest no longer needs the return leg,
    /// or its target identity is gone.
    /// </summary>
    internal static bool EndJourney(IGameplayActor actor, string legOwner = "")
    {
        ArgumentNullException.ThrowIfNull(actor);
        return TravelIntentStore.Disarm(actor.ActorId, legOwner);
    }

    // ---------------------------------------------------------------- decision

    /// <summary>
    /// Runs one wake of the travel decision for the caller's journey.
    ///
    /// The destination the caller passes is only the arming point: the planner
    /// re-resolves the unit through the actor's OWN world every wake, so a moving
    /// target is never chased through a stale snapshot.
    /// </summary>
    /// <param name="actor">The live actor.</param>
    /// <param name="targetObjId">The moving target's live objId.</param>
    /// <param name="targetPosition">The caller's own resolve of the target this wake.</param>
    /// <param name="arrivalRadiusM">The caller's arrival gate (the radius inside which the brain asks for the audited Stop).</param>
    /// <param name="legLive">
    /// The caller's reading of the leg it dispatched: true only while that leg still
    /// SERVES this target. The engine's <c>MoveToUnit</c> resolves its destination
    /// ONCE (it is a point leg the engine never re-anchors, unlike this brain's own
    /// follow leg), so the drift discipline that decides "still serving" is the
    /// caller's own, and its verdict arrives here as this one flag — the brain's
    /// leg-hold arm consumes it verbatim rather than re-deriving a distance it was
    /// not given.
    /// </param>
    /// <param name="legOutcome">The caller's mapping of that leg's lifecycle.</param>
    /// <returns>Null when no journey could be armed (the caller's own fallback rule owns the wake).</returns>
    internal static TravelBrainPlanner.Prepared? Prepare(
        IGameplayActor actor, uint targetObjId, Vector3 targetPosition, float arrivalRadiusM,
        bool legLive, TravelLegOutcome legOutcome,
        int repathBudget = TravelBrain.DefaultRepathBudget, string legOwner = "")
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!EnsureJourney(actor, targetObjId, targetPosition, arrivalRadiusM, repathBudget, legOwner))
            return null;

        var prepared = TravelBrainPlanner.Prepare(actor, new TravelBrainPlanner.Request(
            Kind: TravelTargetKind.Unit,
            TargetObjId: targetObjId,
            Destination: targetPosition,
            FollowRequested: true,
            // A unit target is resolved through the actor's own world, so there is
            // nothing to pre-flight for it — its world key is the actor's by
            // construction (a fabricated mismatch is exactly what the planner's
            // honesty contract forbids).
            DestWorldKnown: false,
            DestWorldId: 0,
            DestInstanceId: 0,
            ArrivalRadiusM: arrivalRadiusM,
            LocalModeMaxM: TravelBrain.DefaultLocalModeMaxM,
            RepathBudget: repathBudget,
            AllowRoute: false,
            LegOutcome: legOutcome,
            LegLive: legLive,
            LegTargetObjId: targetObjId,
            // The caller's drift verdict arrives as the single legLive flag above:
            // for a UNIT target the brain's tracking gate is the objId itself
            // (the engine's MoveToUnit re-resolves at request time), so no leg
            // destination point is claimed here — claiming one the caller does not
            // track would be a fabricated reading.
            LegDestinationKnown: false,
            LegDestination: Vector3.Zero,
            RetreatRequested: false,
            ThreatObjId: 0,
            ThreatPosition: Vector3.Zero), legOwner: legOwner);

        // THE STORE'S STICKINESS, made real for the consumer: a journey that already
        // reached a terminal re-reads that NAMED verdict instead of deciding again,
        // so an abandoned destination can never be re-derived as a bare failure (or
        // silently resumed) later in the same journey.
        if (TravelIntentStore.TryGet(actor.ActorId, out var armed, legOwner) && armed.IsSettled)
            prepared = prepared with { Decision = SettledTerminal(armed) };

        TravelBrainPlanner.Publish(actor.ActorId, prepared, legOwner);
        return prepared;
    }

    /// <summary>
    /// The ONE verb the caller should ask for this wake, derived from the brain's
    /// decision (or from the caller's own pre-brain rule when no journey is armed).
    /// </summary>
    internal enum ReturnLegVerb
    {
        /// <summary>Close on the live target.</summary>
        MoveToUnit = 0,

        /// <summary>The audited halt (the brain's arrival arm, or the caller's own in-gate hold).</summary>
        Stop = 1,

        /// <summary>The settled dialogue (the caller's hold-confirm, and only ever on a landed halt).</summary>
        InteractNpc = 2,

        /// <summary>No leg this wake (a live leg keeps its progress).</summary>
        Held = 3,

        /// <summary>No leg, and no travel: the journey reached a named terminal (or asks for a verb this caller cannot serve).</summary>
        Withdraw = 4
    }

    /// <summary>
    /// One wake's verdict for the caller: the verb, the <c>dispatch=</c> token and
    /// <c>reason=</c> the caller's own lane vocabulary uses, the terminal it names
    /// when the journey ended, and the brain decision that produced it (<c>null</c>
    /// on the fallback path).
    /// </summary>
    internal readonly record struct ReturnLegVerdict(
        ReturnLegVerb Verb,
        string DispatchToken,
        string Reason,
        string? Terminal,
        TravelDecision? Decision)
    {
        /// <summary>True when this verdict asks the actor for a leg (a Hold/Withdraw asks for nothing).</summary>
        public bool HasLeg => Verb is not (ReturnLegVerb.Held or ReturnLegVerb.Withdraw);
    }

    /// <summary>
    /// THE CALLER'S VERDICT TABLE — the one place the travel decision and the
    /// caller's own readings meet. The caller's lane tokens
    /// (<c>dispatch=move|stop|interact|held|withdrawn</c> and the
    /// <c>reason=</c> vocabulary) are preserved verbatim; the brain's own arm and
    /// reason ride additively in the caller's <c>:travel=</c> fragment.
    ///
    /// Fail-closed in both directions: a settled/abandon decision withdraws NAMING
    /// the terminal (never a bare failure and never a leg to an abandoned
    /// destination), an unexpected verb withdraws rather than dispatching something
    /// this caller cannot serve, and the FALLBACK branch (no journey armed) is the
    /// caller's exact pre-brain rule.
    /// </summary>
    /// <param name="brain">The brain's wake preparation, or null when no journey could be armed.</param>
    /// <param name="inGate">The caller's own arrival gate reading (fallback branch only).</param>
    /// <param name="holdConfirmed">The caller's settled-halt memory: a Stop already landed here, so the dialogue yields.</param>
    /// <param name="legLive">The caller's reading of the leg it dispatched (fallback branch only).</param>
    /// <param name="moveReason">The caller's own move reason token (<c>retrack</c> when a live leg is being re-issued, otherwise the drift reading).</param>
    /// <param name="driftText">The caller's own drift reading against its last issue (<c>fresh</c> before any issue).</param>
    internal static ReturnLegVerdict DecideReturnLeg(
        TravelBrainPlanner.Prepared? brain,
        bool inGate,
        bool holdConfirmed,
        bool legLive,
        string moveReason,
        string driftText)
    {
        if (brain is not { } prepared)
            return Fallback(inGate, holdConfirmed, legLive, moveReason, driftText);

        var decision = prepared.Decision;
        if (decision.IsAbandon)
            return new ReturnLegVerdict(
                ReturnLegVerb.Withdraw, "withdrawn", $"terminal-{TravelBrain.Token(decision.Terminal)}",
                TravelBrain.Token(decision.Terminal), decision);

        switch (decision.Verb)
        {
            case TravelVerb.Stop:
                return holdConfirmed
                    ? new ReturnLegVerdict(ReturnLegVerb.InteractNpc, "interact", "settled", null, decision)
                    : new ReturnLegVerdict(ReturnLegVerb.Stop, "stop", "in-range", null, decision);
            case TravelVerb.MoveToUnit:
                return new ReturnLegVerdict(ReturnLegVerb.MoveToUnit, "move", moveReason, null, decision);
            case TravelVerb.Hold when decision.Reason == TravelReason.LegLive:
                // A live leg that still serves this target keeps its progress: the
                // caller's own held token, exactly as its pre-brain rule printed it.
                return new ReturnLegVerdict(
                    ReturnLegVerb.Held, "held", $"drift-held(drift={driftText})", null, decision);
            case TravelVerb.Hold:
                // A withhold for a cause that is NOT progress (the moving target did
                // not resolve, the distance is unreadable): withdraw NAMING the fact
                // rather than claiming the leg is being held on purpose.
                return new ReturnLegVerdict(
                    ReturnLegVerb.Withdraw, "withdrawn", ReasonToken(decision.Reason), TerminalOf(decision), decision);
            default:
                // A verb this caller does not serve (a position leg, the retreat leg):
                // withdraw naming the fact rather than dispatching it blind.
                return new ReturnLegVerdict(
                    ReturnLegVerb.Withdraw, "withdrawn", $"unexpected-verb-{decision.Verb}", TerminalOf(decision), decision);
        }
    }

    /// <summary>
    /// THE CALLER'S PURSUIT VERDICT TABLE — the pursuit leg's own copy of the ONE
    /// discipline <see cref="DecideReturnLeg"/> encodes, with the caller's pursuit
    /// lane tokens preserved verbatim (<c>dispatch=move|stop|held|withdrawn</c>) and
    /// the brain's own arm/reason riding additively in the caller's <c>:travel=</c>
    /// fragment.
    ///
    /// It differs from the return table in exactly one arm, and deliberately so: the
    /// return leg's PRE-BRAIN in-gate rule holds then dials (its reporter is an NPC
    /// to talk to, so the settled wake yields to InteractNpc); the pursuit leg's
    /// pre-brain in-gate rule holds and stays settled (its prey is a unit to close on
    /// — combat, never conversation). Every other arm is the same vocabulary, and the
    /// equivalence is pinned by test.
    ///
    /// Fail-closed in both directions, exactly as the return table is: a settled
    /// decision withdraws NAMING the terminal, and any verb this leg cannot serve
    /// (a position leg, the retreat leg) withdraws rather than dispatching blind.
    /// </summary>
    /// <param name="brain">The brain's wake preparation, or null when no journey could be armed.</param>
    /// <param name="inGate">The caller's own stop-radius reading (fallback branch only).</param>
    /// <param name="legLive">The caller's reading of the leg it dispatched (fallback branch only).</param>
    /// <param name="moveReason">The caller's own move reason token (<c>retrack</c> when a live leg is being re-issued, otherwise the drift reading).</param>
    /// <param name="driftText">The caller's own drift reading against its last issue (<c>fresh</c> before any issue).</param>
    internal static ReturnLegVerdict DecidePursuitLeg(
        TravelBrainPlanner.Prepared? brain,
        bool inGate,
        bool legLive,
        string moveReason,
        string driftText)
    {
        if (brain is not { } prepared)
            return FallbackPursuit(inGate, legLive, moveReason, driftText);

        var decision = prepared.Decision;
        if (decision.IsAbandon)
            return new ReturnLegVerdict(
                ReturnLegVerb.Withdraw, "withdrawn", $"terminal-{TravelBrain.Token(decision.Terminal)}",
                TravelBrain.Token(decision.Terminal), decision);

        switch (decision.Verb)
        {
            case TravelVerb.Stop:
                // A keep-station follow is not a finished journey: the arrival arm
                // halts and the intent stays live, so the pursuit holds station with
                // the SAME audited Stop token its own in-range rule printed.
                return new ReturnLegVerdict(ReturnLegVerb.Stop, "stop", "in-range", null, decision);
            case TravelVerb.MoveToUnit:
                return new ReturnLegVerdict(ReturnLegVerb.MoveToUnit, "move", moveReason, null, decision);
            case TravelVerb.Hold when decision.Reason == TravelReason.LegLive:
                return new ReturnLegVerdict(
                    ReturnLegVerb.Held, "held", $"drift-held(drift={driftText})", null, decision);
            case TravelVerb.Hold:
                return new ReturnLegVerdict(
                    ReturnLegVerb.Withdraw, "withdrawn", ReasonToken(decision.Reason), TerminalOf(decision), decision);
            default:
                return new ReturnLegVerdict(
                    ReturnLegVerb.Withdraw, "withdrawn", $"unexpected-verb-{decision.Verb}", TerminalOf(decision), decision);
        }
    }

    /// <summary>The pursuit leg's own pre-brain rule, unchanged: inside the stop radius it holds and stays settled; outside it closes, drift-gated.</summary>
    private static ReturnLegVerdict FallbackPursuit(
        bool inGate, bool legLive, string moveReason, string driftText)
    {
        if (inGate)
            return new ReturnLegVerdict(ReturnLegVerb.Stop, "stop", "in-range", null, null);
        return legLive
            ? new ReturnLegVerdict(ReturnLegVerb.Held, "held", $"drift-held(drift={driftText})", null, null)
            : new ReturnLegVerdict(ReturnLegVerb.MoveToUnit, "move", moveReason, null, null);
    }

    /// <summary>The leg's own <c>reason=</c> token for a brain reason (space-free, lowercase, the leg's vocabulary).</summary>
    private static string ReasonToken(TravelReason reason) => reason switch
    {
        TravelReason.TargetUnresolved => "target-unresolved",
        TravelReason.TargetGone => "target-gone",
        TravelReason.DistanceUnreadable => "distance-unreadable",
        TravelReason.NoIntent => "no-journey",
        TravelReason.WrongWorld => "wrong-world",
        TravelReason.RepathExhausted => "unreachable",
        _ => "withheld"
    };

    /// <summary>The named terminal a withdrawing verdict carries (<c>null</c> while the journey is still live).</summary>
    private static string? TerminalOf(in TravelDecision decision)
        => decision.Terminal == TravelTerminal.None ? null : TravelBrain.Token(decision.Terminal);

    /// <summary>The caller's own pre-brain return rule, unchanged: in-gate holds then interacts; outside it closes, drift-gated.</summary>
    private static ReturnLegVerdict Fallback(
        bool inGate, bool holdConfirmed, bool legLive, string moveReason, string driftText)
    {
        if (inGate)
        {
            return holdConfirmed
                ? new ReturnLegVerdict(ReturnLegVerb.InteractNpc, "interact", "settled", null, null)
                : new ReturnLegVerdict(ReturnLegVerb.Stop, "stop", "in-range", null, null);
        }
        return legLive
            ? new ReturnLegVerdict(ReturnLegVerb.Held, "held", $"drift-held(drift={driftText})", null, null)
            : new ReturnLegVerdict(ReturnLegVerb.MoveToUnit, "move", moveReason, null, null);
    }

    /// <summary>
    /// The decision shape a SETTLED intent re-reads: the same named terminal and
    /// reason the store banked, carried on the arm that reached it (so the
    /// diagnostic names the real arm rather than a fabricated one).
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

    // ---------------------------------------------------------------- dispatch

    /// <summary>
    /// Banks the leg the caller actually DISPATCHED (its mode, its destination, and
    /// the repath it spent). A fallback proposal carries no decision and banks
    /// nothing.
    /// </summary>
    internal static void PublishDispatched(IGameplayActor actor, TravelDecision? decision, string legOwner = "")
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (decision.HasValue)
            TravelBrainPlanner.PublishDispatched(actor, decision.Value, legOwner);
    }

    /// <summary>
    /// The caller's HONESTY CONTRACT: map the <c>ActorRequest</c> lifecycle the
    /// caller itself dispatched into the brain's leg-outcome vocabulary. The brain
    /// never reads an actor request; this is the one place the two vocabularies
    /// meet.
    ///
    ///  - a Move in flight ON THIS TARGET AND OWNER is
    ///    <see cref="TravelLegOutcome.Running"/> (a leg was issued for this intent
    ///    and has not terminated) — a foreign leg is never read as ours;
    ///  - otherwise the NEWEST terminal Move on this owner + target is read from its
    ///    own result: <c>Completed</c> → Completed; <c>TimedOut</c> → TimedOut (the
    ///    engine's Navigation failure — budget expiry or the stuck declaration — is
    ///    the positive evidence that this leg could not walk its destination); an
    ///    <c>Interrupted</c> the caller itself staged (its own retrack preemption, or
    ///    its own audited Stop) → Running, because that halt was intended, while any
    ///    other interruption → Interrupted (a foreign preemption the leg never
    ///    completed); a <c>Rejected</c> whose reason is an unresolvable destination
    ///    unit → TimedOut, any other rejection → Running (the leg never got the
    ///    actor, so the next wake simply re-decides);
    ///  - no such leg ever dispatched → <see cref="TravelLegOutcome.None"/>.
    /// </summary>
    internal static TravelLegOutcome MapLegOutcome(IGameplayActor actor, uint targetObjId, string moveOwner)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (IsOurLiveLeg(actor.ActiveRequest, targetObjId, moveOwner))
            return TravelLegOutcome.Running;

        var trace = actor.AuditTrace;
        var scanned = 0;
        for (var i = trace.Count - 1; i >= 0 && scanned < TraceScanRows; i--, scanned++)
        {
            var row = trace[i];
            if (row.Action != ActorActionType.Move || row.MoveOwner != moveOwner || row.TargetId != targetObjId)
                continue;
            return row.Result switch
            {
                ActorLifecycleState.Completed => TravelLegOutcome.Completed,
                ActorLifecycleState.TimedOut => TravelLegOutcome.TimedOut,
                ActorLifecycleState.Interrupted => IsCallerRetirement(row.Detail)
                    ? TravelLegOutcome.Running
                    : TravelLegOutcome.Interrupted,
                ActorLifecycleState.Rejected => row.Detail?.Contains(MissingUnitDetail, StringComparison.Ordinal) == true
                    ? TravelLegOutcome.TimedOut
                    : TravelLegOutcome.Running,
                _ => TravelLegOutcome.Running
            };
        }
        return TravelLegOutcome.None;
    }

    /// <summary>True when the request is a live Move the caller itself issued on this target (owner-tagged).</summary>
    internal static bool IsOurLiveLeg(ActorRequest? request, uint targetObjId, string moveOwner)
        => request is { IsTerminal: false, Action: ActorActionType.Move }
           && request.TargetId == targetObjId
           && request.MoveOwner == moveOwner;

    /// <summary>
    /// True for the interrupt details the brain stack itself issues over its own leg
    /// (the quest and survival retrack preemptions and the audited Stop halt). Each
    /// retires a leg the caller decided to replace, so none is a navigation failure.
    /// </summary>
    private static bool IsCallerRetirement(string? detail)
        => detail != null
           && (detail.Contains(ReturnRetrackDetail, StringComparison.Ordinal)
               || detail.Contains(PursuitRetrackDetail, StringComparison.Ordinal)
               || detail.Contains(SurvivalFleeRetrackDetail, StringComparison.Ordinal)
               || detail.Equals(StopDetail, StringComparison.Ordinal));
}
