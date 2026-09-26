#nullable enable

using System.Collections.Concurrent;
using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Travel;

/// <summary>
/// The travel brain's MEMORY: one armed travel intent per actor, its live
/// progress counters (the moving target's consecutive resolve misses and the
/// repath count), the cached road route, and the TERMINAL verdict the intent
/// reached.
///
/// Ownership: this is the ONLY travel-side store. The brain's decision function
/// stays pure over <see cref="TravelBrainInputs"/>; everything stateful lands
/// here, keyed by the actor's objId, bounded by a full clear — the same discipline
/// <c>CombatBrainEngagement</c> and <c>LootLedger</c> follow (memory-only, no
/// persistence, no background sweep, no timers).
///
/// Two properties this store guarantees and the decision layer depends on:
///  - an ABANDONED or ARRIVED destination is STICKY. The terminal rides on the
///    intent until the caller arms a new one, so a consumer can never observe a
///    bare "navigation failed" — it re-reads the named terminal
///    (<see cref="TravelTerminal.WrongWorld"/> / <see cref="TravelTerminal.TargetGone"/> /
///    <see cref="TravelTerminal.Unreachable"/>);
///  - a RE-ARM to the SAME destination keeps the counters (the caller refreshing a
///    moved target position must not hand itself a fresh repath budget), while an
///    arm to a DIFFERENT destination starts clean — a new journey is a new
///    budget, and the cached route of the old one is dropped with it.
/// </summary>
public static class TravelIntentStore
{
    /// <summary>Intent memory bound per process (a full clear, never a per-entry eviction policy).</summary>
    public const int MemoryBound = 256;

    /// <summary>
    /// One armed travel intent. Immutable: every mutation replaces the row, so a
    /// reader can never observe a half-updated intent.
    /// </summary>
    public readonly record struct Intent(
        TravelTargetKind Kind,
        uint TargetObjId,
        Vector3 Destination,
        bool FollowRequested,
        float ArrivalRadiusM,
        float LocalModeMaxM,
        int RepathBudget,
        int ResolveAttempts,
        int RepathCount,
        TravelMode Mode,
        Vector3 PriorDestination,
        TravelTerminal Terminal,
        TravelReason Reason,
        IReadOnlyList<Vector3> Route)
    {
        /// <summary>True when this intent already reached a terminal (arrival or one of the three abandonments).</summary>
        public bool IsSettled => Terminal != TravelTerminal.None;

        /// <summary>True when another wake may serve this intent.</summary>
        public bool IsLive => !IsSettled;
    }

    private static readonly ConcurrentDictionary<uint, Intent> Intents = new();

    /// <summary>
    /// (Re)arms an actor's travel intent. Arming to a destination within
    /// <paramref name="sameDestinationToleranceM"/> of the stored one REFRESHES the
    /// row (position, follow flag, radii, budget) while keeping the resolve/repath
    /// counters and the cached route; arming elsewhere starts a clean row with no
    /// terminal and no route.
    ///
    /// A different TARGET IDENTITY (kind or objId) always starts clean, even at the
    /// same coordinates: a follow of another unit is another journey.
    /// </summary>
    /// <returns>True when the call started a fresh intent (a new journey).</returns>
    public static bool Arm(
        uint actorObjId, TravelTargetKind kind, uint targetObjId, Vector3 destination,
        bool followRequested, float arrivalRadiusM, float localModeMaxM, int repathBudget,
        float sameDestinationToleranceM)
    {
        if (actorObjId == 0 || kind == TravelTargetKind.None)
            return false;

        if (Intents.Count >= MemoryBound)
            Intents.Clear();

        var fresh = true;
        var attempts = 0;
        var repaths = 0;
        var mode = TravelMode.None;
        var priorDestination = destination;
        IReadOnlyList<Vector3> route = [];
        if (Intents.TryGetValue(actorObjId, out var prior)
            && prior.Kind == kind
            && prior.TargetObjId == targetObjId
            && Vector3.Distance(prior.Destination, destination) <= Math.Max(0f, sameDestinationToleranceM))
        {
            // Same destination: refresh without resetting progress.
            fresh = false;
            attempts = prior.ResolveAttempts;
            repaths = prior.RepathCount;
            mode = prior.Mode;
            priorDestination = prior.PriorDestination;
            route = prior.Route;
        }

        Intents[actorObjId] = new Intent(
            kind, targetObjId, destination, followRequested, arrivalRadiusM, localModeMaxM,
            repathBudget, attempts, repaths, mode, priorDestination, TravelTerminal.None,
            TravelReason.None, route);
        return fresh;
    }

    /// <summary>Reads an actor's armed intent, if one is armed.</summary>
    public static bool TryGet(uint actorObjId, out Intent intent)
        => Intents.TryGetValue(actorObjId, out intent);

    /// <summary>Drops an actor's intent entirely (the explicit invalidation seam a caller can name).</summary>
    public static bool Disarm(uint actorObjId) => Intents.TryRemove(actorObjId, out _);

    /// <summary>
    /// Banks one CONSECUTIVE resolve miss for a moving target. The counter is what
    /// the one-allowed re-resolve rule reads; it is reset by a successful resolve.
    /// </summary>
    public static int NoteResolveMiss(uint actorObjId)
    {
        if (!Intents.TryGetValue(actorObjId, out var current))
            return 0;
        var attempts = current.ResolveAttempts + 1;
        Intents[actorObjId] = current with { ResolveAttempts = attempts };
        return attempts;
    }

    /// <summary>Clears the consecutive-miss counter after a target resolved live.</summary>
    public static void NoteResolved(uint actorObjId)
    {
        if (!Intents.TryGetValue(actorObjId, out var current) || current.ResolveAttempts == 0)
            return;
        Intents[actorObjId] = current with { ResolveAttempts = 0 };
    }

    /// <summary>
    /// Banks a repath: the chosen mode, the destination it was issued against
    /// (the next wake's drift comparison), and the spent attempt. The cached route
    /// is dropped, because a repath exists precisely to re-resolve the route.
    /// </summary>
    public static void NoteRepath(uint actorObjId, int repathCount, TravelMode mode, Vector3 destination)
    {
        if (!Intents.TryGetValue(actorObjId, out var current))
            return;
        Intents[actorObjId] = current with
        {
            RepathCount = repathCount,
            Mode = mode,
            PriorDestination = destination,
            Route = []
        };
    }

    /// <summary>Records the mode/destination a leg was issued against (no counter change).</summary>
    public static void NoteIssued(uint actorObjId, TravelMode mode, Vector3 destination)
    {
        if (!Intents.TryGetValue(actorObjId, out var current))
            return;
        Intents[actorObjId] = current with { Mode = mode, PriorDestination = destination };
    }

    /// <summary>Caches the resolved road route for the intent (the head is walked first).</summary>
    public static void SetRoute(uint actorObjId, IReadOnlyList<Vector3> waypoints)
    {
        if (!Intents.TryGetValue(actorObjId, out var current))
            return;
        Intents[actorObjId] = current with { Route = waypoints };
    }

    /// <summary>
    /// Pops the route head whose waypoint the actor has REACHED (measured from
    /// <paramref name="position"/>). Returns true when at least one waypoint was
    /// consumed. The route is never re-queried for a head that was already walked —
    /// that is what keeps a road route progressing instead of restarting at
    /// junction one every wake.
    /// </summary>
    public static bool AdvanceRoute(uint actorObjId, Vector3 position, float reachedRadiusM)
    {
        if (!Intents.TryGetValue(actorObjId, out var current) || current.Route.Count == 0)
            return false;
        var remaining = new List<Vector3>(current.Route);
        var advanced = false;
        while (remaining.Count > 0
               && Vector3.Distance(position, remaining[0]) <= Math.Max(0f, reachedRadiusM))
        {
            remaining.RemoveAt(0);
            advanced = true;
        }
        if (!advanced)
            return false;
        Intents[actorObjId] = current with { Route = remaining };
        return true;
    }

    /// <summary>
    /// Banks the intent's TERMINAL verdict. Sticky: once an intent reached a
    /// terminal, later wakes re-read the same named verdict instead of re-deriving
    /// one, and only a fresh arm clears it.
    /// </summary>
    public static void BankTerminal(uint actorObjId, TravelTerminal terminal, TravelReason reason)
    {
        if (terminal == TravelTerminal.None || !Intents.TryGetValue(actorObjId, out var current))
            return;
        Intents[actorObjId] = current with { Terminal = terminal, Reason = reason };
    }

    /// <summary>Clears the whole store (the world-reset / test seam, mirroring the other bot memory clears).</summary>
    public static void ClearAll() => Intents.Clear();

    /// <summary>Test-only diagnostic: the number of armed intents.</summary>
    public static int Count => Intents.Count;
}
