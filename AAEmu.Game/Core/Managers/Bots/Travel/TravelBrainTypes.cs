#nullable enable

using System.Globalization;
using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Travel;

/// <summary>
/// What the caller asked to travel to. <see cref="Position"/> is a fixed world
/// point; <see cref="Unit"/> is a MOVING target addressed by its live objId
/// (re-resolved once per wake through the actor's own world — never a scan).
/// <see cref="None"/> means no intent is armed and the travel brain has nothing
/// to serve, which is a fact, not a failure.
/// </summary>
public enum TravelTargetKind
{
    /// <summary>No travel intent armed.</summary>
    None = 0,

    /// <summary>A fixed world position.</summary>
    Position = 1,

    /// <summary>A unit addressed by its objId, re-resolved live every wake.</summary>
    Unit = 2
}

/// <summary>
/// The verb a travel decision asks the caller's dispatcher for — the EXISTING
/// <c>IGameplayActor</c> movement verbs only (no new verb, no new gameplay path):
/// <c>MoveTo</c>, <c>MoveToUnit</c> and <c>Stop</c>. A <see cref="Hold"/> asks for
/// nothing, which is how every withholding arm reports itself.
/// </summary>
public enum TravelVerb
{
    /// <summary>No actor call this wake.</summary>
    Hold = 0,

    /// <summary>A position leg (local, or a road route's next waypoint).</summary>
    MoveTo = 1,

    /// <summary>A unit-relative leg on the live objId (the engine re-resolves at request time).</summary>
    MoveToUnit = 2,

    /// <summary>The audited halt (arrival, or a leg that must not keep walking).</summary>
    Stop = 3
}

/// <summary>
/// How the destination is approached. The brain decides the mode; the live
/// adapter supplies the road route the <see cref="Route"/> mode consumes.
/// </summary>
public enum TravelMode
{
    /// <summary>No mode chosen (nothing to serve).</summary>
    None = 0,

    /// <summary>
    /// A direct leg to the destination — the shape the caller's own route layer
    /// serves as a single-leg <c>BotPath.PathTo(destination)</c> (the same
    /// destination a <c>MoveTo</c> leg walks); the brain names the point, never a
    /// path object, so no engine movement type enters the decision surface.
    /// </summary>
    Local = 1,

    /// <summary>A leg to the next waypoint of a road-network route.</summary>
    Route = 2,

    /// <summary>A unit-relative leg keeping station off a moving target.</summary>
    Follow = 3
}

/// <summary>
/// THE NAVIGATE RESULT VOCABULARY. Every terminal the travel layer can reach is
/// named here, and the three ABANDON terminals are DISTINCT facts that a consumer
/// can act on and a lane can count:
///  - <see cref="WrongWorld"/> — the destination lives in another world instance
///    (established by the ONE pre-flight world-key check at dispatch);
///  - <see cref="TargetGone"/> — a moving target is provably absent after its one
///    re-resolve;
///  - <see cref="Unreachable"/> — the leg kept failing and the repath budget is
///    spent, so this destination is abandoned with that reason.
///
/// There is deliberately no "Failed" member: a navigation that cannot be served
/// withholds with <see cref="None"/> (and a reason), and every abandonment names
/// WHY it abandoned. <see cref="Arrived"/> is the one success terminal.
/// </summary>
public enum TravelTerminal
{
    /// <summary>Not terminal: the travel is live (a leg is being served) or withheld for a cause that may clear.</summary>
    None = 0,

    /// <summary>The destination (or follow station) is reached — the one success terminal.</summary>
    Arrived = 1,

    /// <summary>The destination lives in another world instance; walking can never reach it.</summary>
    WrongWorld = 2,

    /// <summary>The moving target is provably gone after its one allowed re-resolve.</summary>
    TargetGone = 3,

    /// <summary>The destination could not be walked and the repath budget is spent.</summary>
    Unreachable = 4
}

/// <summary>
/// What the caller's own last leg reported, mapped by the CALLER from the
/// <c>ActorRequest</c> lifecycle it dispatched (the brain never reads an actor
/// request). The mapping is the caller's honesty contract:
///  - a <see cref="Interrupted"/> here means a FOREIGN preemption the leg never
///    completed (a deliberate <c>Stop</c> the caller issued itself is
///    <see cref="Running"/> — its halt was intended, not a navigation failure);
///  - <see cref="TimedOut"/> is the engine's own Navigation failure (budget
///    expiry, or the stuck declaration), the positive evidence that this leg
///    could not walk its destination.
/// </summary>
public enum TravelLegOutcome
{
    /// <summary>No leg has been issued for this intent yet.</summary>
    None = 0,

    /// <summary>A leg is in flight.</summary>
    Running = 1,

    /// <summary>The leg completed.</summary>
    Completed = 2,

    /// <summary>The leg was preempted by something outside this intent.</summary>
    Interrupted = 3,

    /// <summary>The leg failed: navigation budget expired or the mover was declared stuck.</summary>
    TimedOut = 4
}

/// <summary>
/// The named reason a decision took its arm — an ENUM, not a built string: the
/// wake path formats text only when a lane diagnostic actually reads it, so a
/// per-wake decision allocates nothing.
/// </summary>
public enum TravelReason
{
    /// <summary>No reason recorded.</summary>
    None = 0,

    /// <summary>No travel intent is armed — nothing to serve.</summary>
    NoIntent,

    /// <summary>The destination's world key does not match the actor's (the dispatch pre-flight).</summary>
    WrongWorld,

    /// <summary>A moving target's first missed re-resolution — one more is allowed.</summary>
    TargetUnresolved,

    /// <summary>The one allowed re-resolve is spent and the target is still absent.</summary>
    TargetGone,

    /// <summary>The destination is reached.</summary>
    Arrived,

    /// <summary>A follower is inside its keep distance — hold station.</summary>
    FollowInPosition,

    /// <summary>The live leg already walks this destination — keep the progress.</summary>
    LegLive,

    /// <summary>The live leg's destination has drifted — re-issue on the moving target.</summary>
    DriftRetrack,

    /// <summary>A failed leg was re-selected (mode and/or destination) — the repath.</summary>
    Repathed,

    /// <summary>The repath budget is spent and the destination is abandoned.</summary>
    RepathExhausted,

    /// <summary>Mode select: a far position destination with a road route.</summary>
    RouteSelected,

    /// <summary>Mode select: a direct local leg.</summary>
    LocalSelected,

    /// <summary>Mode select: a unit-relative follow leg.</summary>
    FollowSelected,

    /// <summary>A disengage's safe anchor owns the wake.</summary>
    Retreat,

    /// <summary>No leg may be issued: the distance to the destination could not be measured.</summary>
    DistanceUnreadable
}

/// <summary>Which arm of the decision chain produced the wake's decision — the decision's own vocabulary for diagnostics.</summary>
public enum TravelArm
{
    /// <summary>Nothing to decide.</summary>
    None = 0,

    /// <summary>No intent is armed.</summary>
    NoIntent = 1,

    /// <summary>The ONE dispatch pre-flight: the destination's world key is not the actor's.</summary>
    PreFlight = 2,

    /// <summary>A disengage's retreat move owns the wake.</summary>
    Retreat = 3,

    /// <summary>The destination / follow station is reached.</summary>
    Arrival = 4,

    /// <summary>A moving target's live resolution (the one allowed re-resolve, then the gone verdict).</summary>
    TargetProbe = 5,

    /// <summary>A failed leg is repathed, or the destination abandoned when the budget is spent.</summary>
    Repath = 6,

    /// <summary>A live leg already serves this destination.</summary>
    LegHold = 7,

    /// <summary>Mode select: follow a moving unit.</summary>
    Follow = 8,

    /// <summary>Mode select: a road-network route.</summary>
    Route = 9,

    /// <summary>Mode select: a direct local leg.</summary>
    Local = 10
}

/// <summary>
/// Every input the travel decision chain reads for one wake.
///
/// Value type: the live adapter builds it once per wake from the reads it owns
/// (the target resolve, the world-key pre-flight, the road route) and the brain
/// never stores it. The intent facts (<see cref="PriorResolveAttempts"/>,
/// <see cref="PriorRepathCount"/>, the cached route head and the prior issued
/// leg) likewise arrive as inputs, read from <see cref="TravelIntentStore"/> by
/// the caller — so the decision function itself stays a pure function of its
/// inputs.
///
/// Honesty contract (the decision layer's own fail-closed rules):
///  - a distance that could not be measured is NaN, never 0 — a fabricated 0
///    would read as "already arrived" and a fabricated large value as "far";
///  - an unreadable destination world key is UNKNOWN, never "same world": no
///    verdict is claimed in either direction;
///  - an unresolved unit target is not a gone one — the probe arm spends exactly
///    one re-resolve before it names <see cref="TravelTerminal.TargetGone"/>;
///  - a missing road route is NOT unreachability: the local mode serves far
///    destinations as well, and only a spent repath budget abandons;
///  - an unreadable LIVE leg destination is not proof of tracking: the leg is
///    re-issued rather than assumed to be heading the right way.
/// </summary>
public readonly record struct TravelBrainInputs(
    uint ActorObjId,
    Vector3 SelfPosition,
    TravelTargetKind TargetKind,
    uint TargetObjId,
    Vector3 Destination,
    float DistanceM,
    float ArrivalRadiusM,
    float LocalModeMaxM,
    bool FollowRequested,
    bool DestWorldKnown,
    uint ActorWorldId,
    uint ActorInstanceId,
    uint DestWorldId,
    uint DestInstanceId,
    bool TargetResolved,
    int PriorResolveAttempts,
    TravelLegOutcome LegOutcome,
    bool LegLive,
    uint LegTargetObjId,
    bool LegDestinationKnown,
    Vector3 LegDestination,
    int PriorRepathCount,
    int RepathBudget,
    bool RouteAvailable,
    Vector3 RouteWaypoint,
    int RouteWaypointCount,
    TravelMode PriorMode,
    bool RetreatRequested,
    uint ThreatObjId,
    Vector3 ThreatPosition)
{
    /// <summary>True when a travel intent is armed (a target kind was named).</summary>
    public bool HasIntent => TargetKind != TravelTargetKind.None;

    /// <summary>True when the destination reads as a usable point (every component finite).</summary>
    public bool HasDestination => IsFinite(Destination);

    /// <summary>True when the distance to the destination was measurable this wake.</summary>
    public bool HasDistance => !float.IsNaN(DistanceM);

    /// <summary>
    /// True when the destination's world key was established AND differs from the
    /// actor's own. An unknown key never reads as a mismatch (Unknown stays
    /// unknown).
    /// </summary>
    public bool WorldMismatch
        => DestWorldKnown && (ActorWorldId != DestWorldId || ActorInstanceId != DestInstanceId);

    /// <summary>Every component finite (a legitimate position, unlike CombatBrain's "unset Zero" reading).</summary>
    public static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// One wake's travel decision: the arm that fired, the verb (with the
/// destination or unit it walks to), the mode, the terminal verdict, and the
/// named reason — everything a dispatcher and a lane log need, and nothing that
/// executes gameplay itself.
/// </summary>
public readonly record struct TravelDecision(
    TravelArm Arm,
    TravelVerb Verb,
    TravelMode Mode,
    TravelTerminal Terminal,
    TravelReason Reason,
    uint TargetObjId,
    Vector3? Destination,
    float DistanceM,
    int WaypointCount,
    int RepathCount)
{
    /// <summary>True when this decision asks the actor for a verb (false = the leg withdraws with this decision as evidence).</summary>
    public bool HasVerb => Verb != TravelVerb.Hold;

    /// <summary>True when the wake reached a terminal verdict (arrival, or one of the three abandonments).</summary>
    public bool IsTerminal => Terminal != TravelTerminal.None;

    /// <summary>
    /// True for the three terminals that ABANDON the destination. A consumer must
    /// stop serving the intent and may only retry through an explicit re-arm; the
    /// store banks these, so a later wake re-reads the same verdict rather than
    /// re-deriving a bare failure.
    /// </summary>
    public bool IsAbandon => TravelBrain.IsAbandon(Terminal);

    /// <summary>Compact, space-free diagnostic token for the wake line (built on read only).</summary>
    public string Describe()
        => $"arm={Arm}:verb={Verb}:mode={Mode}:terminal={TravelBrain.Token(Terminal)}:reason={Reason}" +
           $":distM={Fmt(DistanceM)}:target={TargetObjId}:waypoints={WaypointCount}:repaths={RepathCount}";

    private static string Fmt(float value)
        => float.IsNaN(value) ? "NA" : value.ToString("F2", CultureInfo.InvariantCulture);
}
