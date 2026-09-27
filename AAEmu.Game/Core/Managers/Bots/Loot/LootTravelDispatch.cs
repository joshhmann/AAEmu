#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models.Game.Items.Containers;

namespace AAEmu.Game.Core.Managers.Bots.Loot;

/// <summary>
/// The loot brain's CORPSE-APPROACH caller seam: it wires the loot leg's
/// walk-to-the-pinned-corpse leg onto <see cref="TravelBrain.Decide"/> →
/// <see cref="TravelIntentStore"/> → <see cref="TravelBrainPlanner"/>, so the
/// wired travel layer serves the loot slice too instead of a second, hand-rolled
/// close-in rule.
///
/// Approach only: this seam decides the LEG, never the take. The
/// <c>LootedCorpses</c> loot-once memory, the <see cref="LootLedger"/> bank, the
/// <see cref="LootBrain"/> chain and the <c>GameplayActor.Loot</c> verb are all
/// untouched — a corpse already taken is this leg's to walk to and the loot arm's
/// to withhold, and this seam never reads, writes or re-derives either fact.
///
/// Split of ownership (the same discipline <see cref="TravelLegDispatch"/> and
/// <c>NeedsFarmLegDispatch</c> follow):
///  - the CALLER owns WHEN a journey exists — <see cref="EnsureJourney"/> arms one
///    the first time the leg has a pinned corpse to close on and leaves it alone
///    afterwards, so the intent's counters and its sticky verdict survive across
///    wakes exactly as <see cref="TravelIntentStore"/> documents;
///    <see cref="EndJourney"/> is the caller's own journey boundary (the corpse is
///    taken or the pin moved on), and it is what keeps a banked ABANDON verdict
///    from outliving the reason it was reached;
///  - the BRAIN owns WHAT this wake asks for — <see cref="Prepare"/> runs the whole
///    chain over one <see cref="TravelBrainPlanner.Prepared"/> and banks the
///    decision facts through <see cref="TravelBrainPlanner.Publish"/>. A SETTLED
///    intent is re-read as its own named terminal instead of being decided again
///    (the store's documented stickiness, and what makes "never a bare navigation
///    failure" true for this consumer);
///  - the CALLER owns the leg it actually issued — <see cref="PublishDispatched"/>
///    banks the mode/destination (and spends the repath) from the DISPATCH site, so
///    a decision that loses selection never leaves a leg behind that never ran.
///
/// The journey is per (actor, corpse) AND per owning leg:
/// <see cref="TravelIntentStore"/> holds one intent per (actor, leg owner), so this
/// leg's corpse approach coexists with the combat kite's (or the quest legs')
/// journeys on the same actor, and a re-arm to a DIFFERENT objId starts a clean
/// journey (a new budget, no inherited verdict), while a re-arm to the SAME corpse
/// keeps the counters — so a re-pinned corpse can never inherit the abandoned one's
/// budget, and neither journey's boundary can disarm the other's.
///
/// The caller's journey boundaries, all of them owner-scoped and all of them facts
/// the caller established OUTSIDE this seam: the pin re-pointed to another corpse
/// (<see cref="EndJourneyForPin"/>), the corpse taken (<see cref="EndJourney"/>), and
/// a corpse the loot chain will never take (a terminal skip) — refused before a
/// journey is ever armed, because walking to it would be motion with no take at the
/// end of it.
///
/// Fail-closed, in the direction this slice's own evidence allows:
///  - no pinned corpse at all (<see cref="Request.CorpseObjId"/> 0) and an actor
///    with no identity to key a journey on both return <c>null</c>, and
///    <see cref="DecideApproach"/> then falls back to the caller's own pre-brain
///    rule — the brain never fabricates a journey (or a decision) it was not armed
///    for;
///  - a recorded objId that resolves LIVE again (or as another template) is no
///    longer our corpse: the engine's own recycle rule, read through
///    <see cref="LootCorpseProbe"/>, is overridden onto the travel layer's own
///    named <see cref="TravelTerminal.TargetGone"/> verdict — the brain's resolve
///    would otherwise happily walk the actor to whatever now holds that objId;
///  - a despawned corpse rides the brain's own two-strike probe
///    (<see cref="TravelReason.TargetUnresolved"/> hold, then the terminal
///    <see cref="TravelTerminal.TargetGone"/>), so every withholding wake names WHY
///    and the journey never reports a bare navigation failure;
///  - a verb this leg does not serve (a position leg, the retreat leg) withdraws
///    naming the fact rather than dispatching something the caller cannot serve.
/// </summary>
internal static class LootTravelDispatch
{
    /// <summary>
    /// The corpse-approach leg's movement-owner tag
    /// (<see cref="GameplayActor.SetPendingMoveOwner"/> telemetry). Distinct from
    /// every other leg's owner so the quest-pursuit, quest-return and survival-flee
    /// legs can never be read as ours.
    /// </summary>
    internal const string ApproachMoveOwner = "LOOT_APPROACH_MOVE_TO_UNIT";

    /// <summary>
    /// The corpse-approach journey's arrival radius — the leg's own HALT BAND, and
    /// deliberately the engine's loot gate itself
    /// (<see cref="LootingContainer.MaxLootingRange"/>): a corpse approach is over
    /// exactly when <c>GameplayActor.Loot</c> becomes legal, so the journey's own
    /// <see cref="TravelTerminal.Arrived"/> and the take's reachability band are the
    /// same reading rather than two bands that could drift apart. Quoted from the
    /// engine constant, never re-typed.
    /// </summary>
    internal const float ApproachArrivalRadiusM = LootingContainer.MaxLootingRange;

    /// <summary>
    /// The corpse-approach journey's repath budget (the shared travel default: two
    /// repaths, then the named <see cref="TravelTerminal.Unreachable"/> terminal).
    /// A corpse does not walk away, so this budget is spent only by legs the actor
    /// could not walk — never by a moving destination.
    /// </summary>
    internal const int ApproachRepathBudget = TravelBrain.DefaultRepathBudget;

    /// <summary>
    /// Arms the caller's journey for <paramref name="corpseObjId"/> when it does not
    /// exist yet (or names another corpse — another journey, a fresh budget), and
    /// otherwise leaves the armed intent exactly as it is.
    ///
    /// This is the store's documented retry path: a journey's counters and its
    /// sticky verdict are the caller's to keep across wakes, and only an explicit
    /// re-arm to another corpse starts clean.
    /// </summary>
    /// <param name="actor">The live actor.</param>
    /// <param name="corpseObjId">The pinned corpse's objId (0 = nothing pinned; nothing is armed).</param>
    /// <param name="corpsePosition">
    /// The corpse's live position — the arming point only (the planner re-resolves
    /// the objId through the actor's own world every wake), but also the stored
    /// destination the store's same-corpse tolerance compares against, so a caller
    /// must pass a real position rather than a placeholder.
    /// </param>
    /// <returns>True when a journey is armed for this corpse; false when the arm was refused.</returns>
    internal static bool EnsureJourney(
        IGameplayActor actor, uint corpseObjId, Vector3 corpsePosition,
        float arrivalRadiusM, int repathBudget)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (corpseObjId == 0)
            return false;

        if (TravelIntentStore.TryGet(actor.ActorId, out var armed, ApproachMoveOwner)
            && armed.Kind == TravelTargetKind.Unit
            && armed.TargetObjId == corpseObjId)
            return true;

        // followRequested: false — the corpse approach is a POINT journey, not a
        // station-keeping follow: reaching the corpse is the journey's own terminal
        // (TravelTerminal.Arrived), which the caller reads as "dispatch the audited
        // Stop and hand the corpse to the loot arm".
        return TravelBrainPlanner.Arm(
            actor.ActorId, TravelTargetKind.Unit, corpseObjId, corpsePosition,
            followRequested: false, arrivalRadiusM: arrivalRadiusM, repathBudget: repathBudget,
            legOwner: ApproachMoveOwner);
    }

    /// <summary>
    /// The caller's journey boundary: the corpse this actor was walking to is done
    /// (taken, or the pin moved on), so the next wake that needs an approach arms a
    /// clean one (a fresh budget and no inherited verdict).
    ///
    /// This is the <see cref="TravelIntentStore"/> seam scoped to THIS leg's owner
    /// (<see cref="ApproachMoveOwner"/>), so the boundary drops only the corpse
    /// approach: any other leg's journey on the same actor (the combat kite's, the
    /// quest return/pursuit legs') stays armed, and nothing about the quest return
    /// leg's own boundary rules is borrowed for it.
    /// </summary>
    internal static bool EndJourney(IGameplayActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return TravelIntentStore.Disarm(actor.ActorId, ApproachMoveOwner);
    }

    /// <summary>
    /// The PIN-IDENTITY form of <see cref="EndJourney(IGameplayActor)"/>, for the one
    /// site that knows the corpse identity changed WITHOUT a live actor in hand: the
    /// caller's pin write. It carries an actor id rather than an
    /// <see cref="IGameplayActor"/> because the pin is re-pointed inside the caller's own
    /// memory block, before any wake reads the store — so a banked terminal reached on
    /// the previous corpse can never be re-read for the next one.
    ///
    /// Scoped to this leg's owner exactly as the actor overload is: a re-pin drops the
    /// corpse approach and nothing else on that actor.
    /// </summary>
    internal static bool EndJourneyForPin(uint actorObjId)
    {
        if (actorObjId == 0)
            return false;
        return TravelIntentStore.Disarm(actorObjId, ApproachMoveOwner);
    }

    /// <summary>
    /// The brain decision a dispatched approach proposal carries to its dispatch site,
    /// so the leg that actually RAN is the one banked (its mode and destination — the
    /// next wake's drift comparison — plus the repath it spent). <c>null</c> for a
    /// proposal the caller's own pre-brain fallback produced, which banks nothing.
    /// The same shape the sibling caller seams' dispatch payloads use, for this leg's
    /// own vocabulary.
    /// </summary>
    internal readonly record struct LootApproachDispatchParams(TravelDecision? Decision);

    /// <summary>
    /// Everything this seam needs from the caller that is NOT a live world read: the
    /// pinned corpse identity, the caller's reading of the leg it dispatched, and the
    /// policy knobs. A value type carrying no engine type, so a unit test can drive
    /// the seam against a headless actor.
    /// </summary>
    internal readonly record struct Request(
        uint CorpseObjId,
        uint PreyTemplateId,
        float ArrivalRadiusM,
        TravelLegOutcome LegOutcome,
        bool LegLive,
        int RepathBudget);

    /// <summary>
    /// Runs one wake of the corpse-approach decision for the caller's journey.
    ///
    /// The corpse is resolved ONCE here (through <see cref="LootCorpseProbe"/> — the
    /// one corpse-identity rule the loot chain also reads) so the arming point is the
    /// corpse's live position and the recycle verdict is the same fact the loot
    /// chain sees; the planner then re-resolves the objId through the actor's OWN
    /// world, exactly as it does for any unit target.
    /// </summary>
    /// <returns>Null when no journey could be armed (a missing pin, or an actor with no identity to key one) — the caller's own fallback rule owns the wake.</returns>
    internal static TravelBrainPlanner.Prepared? Prepare(IGameplayActor actor, in Request request)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (request.CorpseObjId == 0)
            return null;

        var probe = LootCorpseProbe.Resolve(actor.Character, request.CorpseObjId, request.PreyTemplateId);

        // The arming point: the corpse's live position when it IS our corpse, else the
        // journey's OWN recorded point (never the position of a row that merely holds
        // the objId now, and never a fabricated zero — a zero would read as another
        // destination and hand a despawned corpse's journey a fresh repath budget every
        // wake). A recycled corpse never dispatches a leg at all, but it must still arm
        // on its own history rather than on the occupant's coordinates.
        var position = probe.IsDead
            ? probe.Corpse!.Transform.World.Position
            : TravelIntentStore.TryGet(actor.ActorId, out var prior, ApproachMoveOwner)
              && prior.TargetObjId == request.CorpseObjId
                ? prior.Destination
                : Vector3.Zero;

        if (!EnsureJourney(actor, request.CorpseObjId, position, request.ArrivalRadiusM, request.RepathBudget))
            return null;

        var prepared = TravelBrainPlanner.Prepare(actor, new TravelBrainPlanner.Request(
            Kind: TravelTargetKind.Unit,
            TargetObjId: request.CorpseObjId,
            Destination: position,
            FollowRequested: false,
            // A unit target is resolved through the actor's own world, so there is
            // nothing to pre-flight for it — its world key is the actor's by
            // construction (a fabricated mismatch is exactly what the planner's
            // honesty contract forbids).
            DestWorldKnown: false,
            DestWorldId: 0,
            DestInstanceId: 0,
            ArrivalRadiusM: request.ArrivalRadiusM,
            LocalModeMaxM: TravelBrain.DefaultLocalModeMaxM,
            RepathBudget: request.RepathBudget,
            AllowRoute: false,
            LegOutcome: request.LegOutcome,
            LegLive: request.LegLive,
            LegTargetObjId: request.CorpseObjId,
            // The caller's drift verdict arrives as the single LegLive flag: for a
            // UNIT target the brain's tracking gate is the objId itself (the engine's
            // MoveToUnit re-resolves at request time), so no leg destination point is
            // claimed here — claiming one the caller does not track would be a
            // fabricated reading.
            LegDestinationKnown: false,
            LegDestination: Vector3.Zero,
            RetreatRequested: false,
            ThreatObjId: 0,
            ThreatPosition: Vector3.Zero), legOwner: ApproachMoveOwner);

        // THE STORE'S STICKINESS, made real for this consumer: a journey that already
        // reached a terminal re-reads that NAMED verdict instead of deciding again, so
        // an abandoned or arrived corpse can never be re-derived as a bare failure (or
        // silently resumed) later in the same journey.
        if (TravelIntentStore.TryGet(actor.ActorId, out var armed, ApproachMoveOwner) && armed.IsSettled)
            prepared = prepared with { Decision = SettledTerminal(armed) };

        // THE RECYCLE OVERRIDE — applied AFTER the sticky re-read, because it is the
        // one fact about the destination that a banked verdict may not outlive: the
        // planner's unit resolve reads any row the objId now addresses as RESOLVED and
        // would walk the actor to whatever holds it (a respawned mob, another
        // template's corpse), and a settled ARRIVED journey would keep re-reading its
        // arrival at a row that is no longer our corpse — the stale "stop here" leg.
        // The loot chain's own recycle rule says that row is not our corpse, so the
        // journey names the travel layer's terminal for a destination that cannot be
        // served: TargetGone. Banked by Publish below, so every later wake re-reads it
        // (never a bare failure, never a silent resume, never a leg onto the occupant).
        if (probe.State == LootCorpseState.Recycled)
            prepared = prepared with { Decision = RecycledTerminal(prepared, request.CorpseObjId) };

        TravelBrainPlanner.Publish(actor.ActorId, prepared, ApproachMoveOwner);
        return prepared;
    }

    /// <summary>
    /// Banks the leg the caller actually DISPATCHED (its mode, its destination, and
    /// the repath it spent). A fallback proposal carries no decision and banks
    /// nothing.
    /// </summary>
    internal static void PublishDispatched(IGameplayActor actor, TravelDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (decision.HasValue)
            TravelLegDispatch.PublishDispatched(actor, decision.Value, ApproachMoveOwner);
    }

    /// <summary>
    /// The caller's HONESTY CONTRACT, delegated to the ONE mapping
    /// (<see cref="TravelLegDispatch.MapLegOutcome"/>) with this leg's own owner tag:
    /// the brain never reads an actor request, and this seam never reads one either.
    /// </summary>
    internal static TravelLegOutcome MapLegOutcome(IGameplayActor actor, uint corpseObjId)
        => TravelLegDispatch.MapLegOutcome(actor, corpseObjId, ApproachMoveOwner);

    /// <summary>True when the request is a live Move this seam's caller issued on this corpse (owner-tagged).</summary>
    internal static bool IsOurLiveLeg(ActorRequest? request, uint corpseObjId)
        => TravelLegDispatch.IsOurLiveLeg(request, corpseObjId, ApproachMoveOwner);

    /// <summary>
    /// The verb this leg should ask for this wake.
    /// </summary>
    internal enum ApproachVerb
    {
        /// <summary>Close on the pinned corpse (the existing <c>MoveToUnit</c> verb).</summary>
        MoveToUnit = 0,

        /// <summary>The audited halt: the journey reached the corpse, so the take is the loot arm's.</summary>
        Stop = 1,

        /// <summary>No leg this wake (a live leg keeps its progress).</summary>
        Held = 2,

        /// <summary>No leg, and no travel: the journey reached a named terminal (or asks for a verb this leg cannot serve).</summary>
        Withdraw = 3
    }

    /// <summary>
    /// One wake's verdict for the caller: the verb, the <c>dispatch=</c> token and
    /// <c>reason=</c> the leg's own lane vocabulary uses, the terminal it names when
    /// the journey ended, and the brain decision that produced it (<c>null</c> on
    /// the fallback path).
    /// </summary>
    internal readonly record struct ApproachVerdict(
        ApproachVerb Verb,
        string DispatchToken,
        string Reason,
        string? Terminal,
        TravelDecision? Decision)
    {
        /// <summary>True when this verdict asks the actor for a leg (a Hold/Withdraw asks for nothing).</summary>
        public bool HasLeg => Verb is ApproachVerb.MoveToUnit or ApproachVerb.Stop;

        /// <summary>True when this wake routed through the brain (rather than its fallback).</summary>
        public bool Routed => Decision.HasValue;

        /// <summary>True when the verdict names a journey terminal (<c>arrived</c>, or one of the abandonments).</summary>
        public bool IsTerminal => Terminal != null;
    }

    /// <summary>
    /// THE CALLER'S VERDICT TABLE — the one place the travel decision and this leg's
    /// own routing meet. The caller's lane tokens
    /// (<c>dispatch=move|stop|held|withdrawn</c> and the <c>reason=</c> vocabulary)
    /// are the same ones the quest return leg prints (pinned by an equivalence test),
    /// and the brain's own arm and reason ride additively in the caller's
    /// <c>:travel=</c> fragment.
    ///
    /// Fail-closed in both directions: a settled/abandoned journey withdraws NAMING
    /// the terminal (never a bare failure and never a leg to an abandoned corpse), an
    /// unexpected verb withdraws rather than dispatching something this leg cannot
    /// serve, and the FALLBACK branch (no journey armed) is the caller's pre-brain
    /// rule: in the gate → the audited Stop; a live leg → Hold; otherwise close,
    /// drift-gated.
    /// </summary>
    /// <param name="brain">The brain's wake preparation, or null when no journey could be armed.</param>
    /// <param name="inRange">The caller's own arrival gate reading (fallback branch only).</param>
    /// <param name="legLive">The caller's reading of the leg it dispatched (fallback branch only).</param>
    /// <param name="moveReason">The caller's own move reason token (<c>retrack</c> when a live leg is being re-issued, otherwise the drift reading).</param>
    /// <param name="driftText">The caller's own drift reading against its last issue (<c>fresh</c> before any issue).</param>
    internal static ApproachVerdict DecideApproach(
        TravelBrainPlanner.Prepared? brain,
        bool inRange,
        bool legLive,
        string moveReason,
        string driftText)
    {
        if (brain is not { } prepared)
            return Fallback(inRange, legLive, moveReason, driftText);

        var decision = prepared.Decision;
        if (decision.IsAbandon)
            return new ApproachVerdict(
                ApproachVerb.Withdraw, "withdrawn", $"terminal-{TravelBrain.Token(decision.Terminal)}",
                TravelBrain.Token(decision.Terminal), decision);

        // Arrival is checked before the verb: a SETTLED arrival re-reads as a Hold on
        // the store's own sticky path, and it must still land on the caller's Stop arm
        // rather than withdraw as an unexplained hold.
        if (decision.Terminal == TravelTerminal.Arrived || decision.Verb == TravelVerb.Stop)
            return new ApproachVerdict(
                ApproachVerb.Stop, "stop",
                decision.Terminal == TravelTerminal.Arrived ? "arrived" : "in-range",
                TerminalOf(decision), decision);

        switch (decision.Verb)
        {
            case TravelVerb.MoveToUnit:
                return new ApproachVerdict(ApproachVerb.MoveToUnit, "move", moveReason, null, decision);
            case TravelVerb.Hold when decision.Reason == TravelReason.LegLive:
                // A live leg that still serves this corpse keeps its progress: the
                // caller's own held token, exactly as its pre-brain rule printed it.
                return new ApproachVerdict(
                    ApproachVerb.Held, "held", $"drift-held(drift={driftText})", null, decision);
            case TravelVerb.Hold:
                // A withhold for a cause that is NOT progress (the corpse did not
                // resolve, the distance is unreadable): withdraw NAMING the fact rather
                // than claiming the leg is being held on purpose.
                return new ApproachVerdict(
                    ApproachVerb.Withdraw, "withdrawn", ReasonToken(decision.Reason), TerminalOf(decision), decision);
            default:
                // A verb this leg does not serve (a position leg, the retreat leg):
                // withdraw naming the fact rather than dispatching it blind.
                return new ApproachVerdict(
                    ApproachVerb.Withdraw, "withdrawn", $"unexpected-verb-{decision.Verb}", TerminalOf(decision), decision);
        }
    }

    /// <summary>
    /// The wake's routing fact for the caller's observability, space-free so a lane
    /// parser keeps working: the brain's own decision description plus whether this
    /// leg routed through it and which dispatch it named. Built on read only.
    /// </summary>
    internal static string Describe(in ApproachVerdict verdict)
        => verdict.Decision is { } decision
            ? $"{decision.Describe()}:routed=true:dispatch={verdict.DispatchToken}" +
              (verdict.Terminal is { } terminal ? $":terminal={terminal}" : "")
            : $"verdict=none:reason={verdict.Reason}:routed=false:dispatch={verdict.DispatchToken}";

    /// <summary>
    /// The leg's own <c>reason=</c> token for a brain reason (space-free, lowercase,
    /// the leg's vocabulary). The SAME token set the quest return leg prints — the
    /// two tables are the same vocabulary by construction, and the equivalence is
    /// pinned by test.
    /// </summary>
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

    /// <summary>The named terminal a verdict carries (<c>null</c> while the journey is still live).</summary>
    private static string? TerminalOf(in TravelDecision decision)
        => decision.Terminal == TravelTerminal.None ? null : TravelBrain.Token(decision.Terminal);

    /// <summary>The caller's own pre-brain approach rule: in the gate holds at the audited Stop; a live leg keeps its progress; otherwise close, drift-gated.</summary>
    private static ApproachVerdict Fallback(bool inRange, bool legLive, string moveReason, string driftText)
    {
        if (inRange)
            return new ApproachVerdict(ApproachVerb.Stop, "stop", "in-range", null, null);
        return legLive
            ? new ApproachVerdict(ApproachVerb.Held, "held", $"drift-held(drift={driftText})", null, null)
            : new ApproachVerdict(ApproachVerb.MoveToUnit, "move", moveReason, null, null);
    }

    /// <summary>
    /// The decision shape a SETTLED intent re-reads: the same named terminal and
    /// reason the store banked, carried on the arm that reached it (so the diagnostic
    /// names the real arm rather than a fabricated one).
    /// </summary>
    private static TravelDecision SettledTerminal(in TravelIntentStore.Intent intent)
    {
        var arm = intent.Terminal switch
        {
            TravelTerminal.WrongWorld => TravelArm.PreFlight,
            TravelTerminal.TargetGone => TravelArm.TargetProbe,
            TravelTerminal.Arrived => TravelArm.Arrival,
            _ => TravelArm.Repath
        };
        return new TravelDecision(
            arm, TravelVerb.Hold, intent.Mode, intent.Terminal, intent.Reason,
            intent.TargetObjId, null, float.NaN, 0, intent.RepathCount);
    }

    /// <summary>
    /// The decision a RECYCLED corpse's journey names: the travel layer's terminal
    /// for a destination that cannot be served, on the arm that establishes it
    /// (the live target resolve), with the wake's own measurements kept honest.
    /// </summary>
    private static TravelDecision RecycledTerminal(in TravelBrainPlanner.Prepared prepared, uint corpseObjId)
        => new(
            TravelArm.TargetProbe, TravelVerb.Hold, prepared.Inputs.PriorMode,
            TravelTerminal.TargetGone, TravelReason.TargetGone, corpseObjId, null,
            prepared.Inputs.DistanceM, 0, prepared.Inputs.PriorRepathCount);
}
