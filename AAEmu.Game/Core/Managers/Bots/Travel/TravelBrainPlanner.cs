#nullable enable

using System.Numerics;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots.Travel;

/// <summary>
/// The travel brain's LIVE ADAPTER: turns one wake's armed intent into a
/// <see cref="TravelBrainInputs"/>, runs <see cref="TravelBrain.Decide"/>, and
/// reports what the caller must bank.
///
/// The split is deliberate: <see cref="TravelBrain"/> is pure and unit-testable
/// without a world, and every live read lives here — the destination's world key
/// (the ONE pre-flight check), the moving target's resolve, the distance
/// measurement, and the road-network route. Nothing else is scanned: a unit target
/// resolves by objId through the actor's OWN world, exactly as the combat brain
/// resolves its incumbent.
///
/// Two explicit publish seams, mirroring the loot brain's discipline:
///  - <see cref="Publish"/> banks a wake's DECISION facts (the terminal, a moving
///    target's miss/resolved transition, the resolved route). The caller calls it
///    the moment it has the decision;
///  - <see cref="PublishDispatched"/> banks what was actually ISSUED (the mode and
///    destination the leg was dispatched against). A decision that loses selection
///    must not leave a leg behind that never ran.
///
/// Fail-closed contract: a distance the world cannot measure is NaN (never 0), an
/// unreadable destination world key is UNKNOWN (never "same world"), an
/// unresolvable unit target is not a gone one (it spends its one re-resolve), and
/// an absent road network yields no route — which reads as a local leg, never as
/// unreachability.
/// </summary>
public static class TravelBrainPlanner
{
    /// <summary>
    /// Position tolerance under which a re-arm to the same coordinates counts as the
    /// SAME destination, so the intent keeps its counters instead of restarting its
    /// budget.
    /// </summary>
    public const float SameDestinationToleranceM = 1.0f;

    /// <summary>
    /// The wake's live measurement plus the decision it produced — the shape a
    /// caller reads to dispatch, diagnose, and bank.
    /// </summary>
    public readonly record struct Prepared(
        TravelBrainInputs Inputs,
        TravelDecision Decision,
        IReadOnlyList<Vector3> Route)
    {
        /// <summary>True when this wake produced a leg the caller must dispatch.</summary>
        public bool HasLeg => Decision.HasVerb;

        /// <summary>True when the wake reached a terminal (arrival, or one of the three abandonments).</summary>
        public bool IsTerminal => Decision.IsTerminal;

        /// <summary>True when the destination drifted away from the leg already issued for it.</summary>
        public bool IsRetrack => Decision.Reason == TravelReason.DriftRetrack;
    }

    /// <summary>
    /// Everything the planner needs from the caller that is NOT a live world read:
    /// the armed intent's own parameters, the caller's readings of the leg it
    /// dispatched, and the policy knobs.
    ///
    /// A value type carrying no engine type, so a unit test can drive the live
    /// adapter against a headless actor without constructing policy state.
    /// </summary>
    public readonly record struct Request(
        TravelTargetKind Kind,
        uint TargetObjId,
        Vector3 Destination,
        bool FollowRequested,
        bool DestWorldKnown,
        uint DestWorldId,
        uint DestInstanceId,
        float ArrivalRadiusM,
        float LocalModeMaxM,
        int RepathBudget,
        bool AllowRoute,
        TravelLegOutcome LegOutcome,
        bool LegLive,
        uint LegTargetObjId,
        bool LegDestinationKnown,
        Vector3 LegDestination,
        bool RetreatRequested,
        uint ThreatObjId,
        Vector3 ThreatPosition);

    /// <summary>
    /// Arms (or refreshes) an actor's travel intent — the caller's own act, and the
    /// only entry point that starts a journey. See <see cref="TravelIntentStore.Arm"/>
    /// for the refresh-vs-restart rule.
    /// </summary>
    public static bool Arm(
        uint actorObjId, TravelTargetKind kind, uint targetObjId, Vector3 destination,
        bool followRequested,
        float arrivalRadiusM = TravelBrain.DefaultArrivalRadiusM,
        float localModeMaxM = TravelBrain.DefaultLocalModeMaxM,
        int repathBudget = TravelBrain.DefaultRepathBudget,
        string legOwner = "")
        => TravelIntentStore.Arm(
            actorObjId, kind, targetObjId, destination, followRequested,
            arrivalRadiusM, localModeMaxM, repathBudget, SameDestinationToleranceM, legOwner);

    /// <summary>
    /// Builds the wake's inputs and evaluates the decision. Writes nothing: the
    /// caller banks the outcome through <see cref="Publish"/> (decision facts) and
    /// <see cref="PublishDispatched"/> (the leg it actually issued).
    /// </summary>
    /// <param name="actor">The live actor.</param>
    /// <param name="request">The caller's own intent parameters and leg readings.</param>
    /// <param name="roads">
    /// The road-network seam. Null resolves the process's own network when it was
    /// initialized; a host without one simply gets no route, which is the
    /// fail-closed direction (a local leg still serves the destination).
    /// </param>
    public static Prepared Prepare(
        IGameplayActor actor, in Request request, IRoadNetworkService? roads = null,
        string legOwner = "")
    {
        ArgumentNullException.ThrowIfNull(actor);

        var character = actor.Character;
        var selfPosition = character?.Transform.World.Position ?? Vector3.Zero;
        TravelIntentStore.TryGet(actor.ActorId, out var intent, legOwner);

        // ------------------------------------------------------ destination + probe
        var destination = request.Destination;
        var targetResolved = true;

        // The world-key pre-flight reads the CALLER's reading of the destination's
        // world — it applies to a POSITION destination, which can name a point in
        // another world instance. A UNIT target is resolved by objId through the
        // actor's OWN world (a resolve that could only have found a co-instanced
        // unit), so there is nothing left to pre-flight for it: its world key is
        // the actor's by construction, and claiming otherwise would be a fabricated
        // mismatch.
        var destWorldKnown = request.Kind == TravelTargetKind.Position && request.DestWorldKnown;
        var destWorldId = destWorldKnown ? request.DestWorldId : 0u;
        var destInstanceId = destWorldKnown ? request.DestInstanceId : 0u;
        if (request.Kind == TravelTargetKind.Unit)
        {
            var unit = character?.ParentWorld?.GetUnit(request.TargetObjId);
            if (unit == null)
            {
                targetResolved = false;
            }
            else
            {
                destination = unit.Transform.World.Position;
            }
        }

        var distance = character == null
            ? float.NaN
            : MathUtil.CalculateDistance(selfPosition, destination, false);

        // ------------------------------------------------------ road route
        var route = intent.Route;
        if (route.Count == 0 && request.Kind == TravelTargetKind.Position && request.AllowRoute
            && !float.IsNaN(distance) && distance > LocalThreshold(request.LocalModeMaxM))
        {
            route = BuildRoute(roads ?? RoadNetworkService.PeekInstance, selfPosition, destination);
        }

        var inputs = new TravelBrainInputs(
            ActorObjId: actor.ActorId,
            SelfPosition: selfPosition,
            TargetKind: request.Kind,
            TargetObjId: request.TargetObjId,
            Destination: destination,
            DistanceM: distance,
            ArrivalRadiusM: request.ArrivalRadiusM,
            LocalModeMaxM: request.LocalModeMaxM,
            FollowRequested: request.FollowRequested,
            DestWorldKnown: destWorldKnown,
            ActorWorldId: character?.ParentWorld?.Template?.Id ?? character?.Transform.WorldId ?? 0,
            ActorInstanceId: character?.Transform.InstanceId ?? 0,
            DestWorldId: destWorldId,
            DestInstanceId: destInstanceId,
            TargetResolved: targetResolved,
            PriorResolveAttempts: intent.ResolveAttempts,
            LegOutcome: request.LegOutcome,
            LegLive: request.LegLive,
            LegTargetObjId: request.LegTargetObjId,
            LegDestinationKnown: request.LegDestinationKnown,
            LegDestination: request.LegDestination,
            PriorRepathCount: intent.RepathCount,
            RepathBudget: request.RepathBudget,
            RouteAvailable: route.Count > 0,
            RouteWaypoint: route.Count > 0 ? route[0] : destination,
            RouteWaypointCount: route.Count,
            PriorMode: intent.Mode,
            RetreatRequested: request.RetreatRequested,
            ThreatObjId: request.ThreatObjId,
            ThreatPosition: request.ThreatPosition);

        return new Prepared(inputs, TravelBrain.Decide(inputs), route);
    }

    /// <summary>
    /// The RETREAT MOVE for a disengage/kite: the safe anchor 25 m opposite the
    /// threat, as a leg the caller dispatches through the ordinary Move verb.
    ///
    /// This is the SAME shape <c>CombatBrain</c>'s flee arm produces, from the same
    /// <see cref="TravelBrain.SafeAnchor"/> helper — so a combat disengage and a
    /// travel retreat can never drift apart. It needs no armed intent and no world
    /// read: both positions are arguments.
    /// </summary>
    public static TravelDecision RetreatDecision(Vector3 selfPosition, uint threatObjId, Vector3 threatPosition)
        => new(
            TravelArm.Retreat, TravelVerb.MoveTo, TravelMode.Local, TravelTerminal.None,
            TravelReason.Retreat, threatObjId,
            TravelBrain.SafeAnchor(selfPosition, threatPosition, TravelBrain.RetreatAnchorDistanceM),
            float.NaN, 0, 0);

    /// <summary>
    /// Banks a wake's DECISION facts: the terminal verdict (sticky), a moving
    /// target's consecutive miss or its clearing, and the road route the wake
    /// resolved. Called by the caller immediately after <see cref="Prepare"/>.
    /// </summary>
    public static void Publish(uint actorObjId, in Prepared prepared, string legOwner = "")
    {
        if (prepared.Decision.IsTerminal)
        {
            TravelIntentStore.BankTerminal(actorObjId, prepared.Decision.Terminal, prepared.Decision.Reason, legOwner);
            return;
        }

        if (prepared.Route.Count > 0)
            TravelIntentStore.SetRoute(actorObjId, prepared.Route, legOwner);

        if (prepared.Inputs.TargetKind != TravelTargetKind.Unit)
            return;

        if (prepared.Inputs.TargetResolved)
            TravelIntentStore.NoteResolved(actorObjId, legOwner);
        else if (prepared.Decision.Arm == TravelArm.TargetProbe)
            TravelIntentStore.NoteResolveMiss(actorObjId, legOwner);
    }

    /// <summary>
    /// Banks the leg the caller actually DISPATCHED (its mode and destination), so
    /// the next wake's drift comparison is made against the point the actor was
    /// really sent to, and a repath spends its attempt. A verb-less decision banks
    /// nothing.
    /// </summary>
    public static void PublishDispatched(IGameplayActor actor, in TravelDecision decision, string legOwner = "")
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (decision.Verb == TravelVerb.Hold)
            return;

        if (decision.Reason == TravelReason.Repathed)
            TravelIntentStore.NoteRepath(actor.ActorId, decision.RepathCount, decision.Mode,
                decision.Destination ?? Vector3.Zero, legOwner);
        else if (decision.Destination.HasValue)
            TravelIntentStore.NoteIssued(actor.ActorId, decision.Mode, decision.Destination.Value, legOwner);
    }

    /// <summary>
    /// Banks a completed leg: the road route's head is dropped when the actor stands
    /// on it, so the next wake walks the NEXT junction instead of restarting the
    /// route at junction one. Called by the caller when its leg reached a terminal
    /// success.
    /// </summary>
    public static bool PublishLegCompleted(IGameplayActor actor, string legOwner = "")
    {
        ArgumentNullException.ThrowIfNull(actor);
        var position = actor.Character?.Transform.World.Position;
        return position.HasValue
               && TravelIntentStore.AdvanceRoute(actor.ActorId, position.Value, TravelBrain.DefaultArrivalRadiusM, legOwner);
    }

    // ------------------------------------------------------------------ live reads

    /// <summary>
    /// The road route from the actor's position to the destination, as the ordered
    /// waypoint list the intent walks (junction vectors only — the destination
    /// itself is the intent's own goal). Empty when no network is available or the
    /// two ends route through no junction — which reads as "no route", never as
    /// unreachability.
    /// </summary>
    private static List<Vector3> BuildRoute(
        IRoadNetworkService? roads, Vector3 start, Vector3 destination)
    {
        if (roads == null)
            return [];

        IReadOnlyList<RoadJunction> path;
        try
        {
            path = roads.FindRoadPath(start, destination);
        }
        catch
        {
            // A host whose network is malformed cannot route: the caller keeps its
            // local leg rather than seeing a navigation failure.
            return [];
        }

        var waypoints = new List<Vector3>(path.Count);
        foreach (var junction in path)
        {
            var position = junction.Position;
            if (Vector3.Distance(start, position) <= TravelBrain.DefaultArrivalRadiusM)
                continue; // already in this junction's neighbourhood — never walk back to it
            waypoints.Add(position);
        }
        return waypoints;
    }

    private static float LocalThreshold(float localModeMaxM)
        => float.IsFinite(localModeMaxM) && localModeMaxM > 0f ? localModeMaxM : TravelBrain.DefaultLocalModeMaxM;
}
