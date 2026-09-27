#nullable enable

using System.Collections.Concurrent;
using System.Numerics;

namespace AAEmu.Game.Core.Managers.Bots.Travel;

/// <summary>
/// The travel brain's MEMORY: the armed travel intents (one per actor PER OWNING
/// LEG), their live progress counters (the moving target's consecutive resolve
/// misses and the repath count), the cached road route, and the TERMINAL verdict
/// each intent reached.
///
/// Ownership: this is the ONLY travel-side store. The brain's decision function
/// stays pure over <see cref="TravelBrainInputs"/>; everything stateful lands
/// here, keyed by the actor's objId and the owning leg's tag, bounded by a full
/// clear — the same discipline <c>CombatBrainEngagement</c> and <c>LootLedger</c>
/// follow (memory-only, no persistence, no background sweep, no timers).
///
/// Why the owning leg is part of the key: one actor walks one journey at a time,
/// but two legs of the SAME quest wake can each need their own (the return leg's
/// reporter approach and the pursuit leg's prey approach). Keying by owner is what
/// lets each leg reach its own journey boundary (<c>EndJourney</c>) without
/// disarming the other's armed journey — and therefore what lets a pursuit reach
/// its named abandonment terminal instead of having its budget reset every wake.
/// An owner-less caller (a leg that names none, as the loot/combat seams do) keeps
/// its own single row, exactly as before.
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
        /// <summary>
        /// The leg that OWNS this journey (<c>""</c> for a caller that names none).
        ///
        /// One actor walks one journey at a time, but more than one leg of the SAME
        /// wake may need one: the quest return leg owns the reporter approach while
        /// the quest no longer needs it, and the quest pursuit leg owns the prey
        /// approach. Without this tag the return leg's own journey boundary
        /// (<c>EndJourney</c>) would disarm the pursuit leg's armed journey on every
        /// progress wake — restarting its budget every wake, so the pursuit could
        /// never reach its named abandonment terminal. The owner is part of the
        /// journey's IDENTITY: a different owner is a different journey (a fresh
        /// budget), exactly as a different target objId is.
        /// </summary>
        public string LegOwner { get; init; } = "";
        /// <summary>True when this intent already reached a terminal (arrival or one of the three abandonments).</summary>
        public bool IsSettled => Terminal != TravelTerminal.None;

        /// <summary>True when another wake may serve this intent.</summary>
        public bool IsLive => !IsSettled;
    }

    private static readonly ConcurrentDictionary<(uint ActorObjId, string LegOwner), Intent> Intents = new();

    /// <summary>
    /// (Re)arms an actor's travel intent. Arming to a destination within
    /// <paramref name="sameDestinationToleranceM"/> of the stored one REFRESHES the
    /// row (position, follow flag, radii, budget) while keeping the resolve/repath
    /// counters and the cached route; arming elsewhere starts a clean row with no
    /// terminal and no route.
    ///
    /// A different TARGET IDENTITY (kind, objId, or the owning leg) always starts
    /// clean, even at the same coordinates: a follow of another unit — or another
    /// leg's journey — is another journey. Keying by owner is what lets the quest
    /// return leg's own boundary drop ITS journey without disarming the pursuit
    /// leg's (and vice versa) — one actor, one journey PER OWNING LEG.
    /// </summary>
    /// <returns>True when the call started a fresh intent (a new journey).</returns>
    public static bool Arm(
        uint actorObjId, TravelTargetKind kind, uint targetObjId, Vector3 destination,
        bool followRequested, float arrivalRadiusM, float localModeMaxM, int repathBudget,
        float sameDestinationToleranceM, string legOwner = "")
    {
        if (actorObjId == 0 || kind == TravelTargetKind.None)
            return false;

        if (Intents.Count >= MemoryBound)
            Intents.Clear();

        var key = (actorObjId, legOwner ?? "");
        var fresh = true;
        var attempts = 0;
        var repaths = 0;
        var mode = TravelMode.None;
        var priorDestination = destination;
        IReadOnlyList<Vector3> route = [];
        if (Intents.TryGetValue(key, out var prior)
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

        Intents[key] = new Intent(
            kind, targetObjId, destination, followRequested, arrivalRadiusM, localModeMaxM,
            repathBudget, attempts, repaths, mode, priorDestination, TravelTerminal.None,
            TravelReason.None, route)
        {
            LegOwner = legOwner ?? ""
        };
        return fresh;
    }

    /// <summary>Reads an actor's armed intent for one owning leg (<paramref name="legOwner"/> empty = the owner-less callers).</summary>
    public static bool TryGet(uint actorObjId, out Intent intent, string legOwner = "")
        => Intents.TryGetValue((actorObjId, legOwner ?? ""), out intent);

    /// <summary>
    /// Drops the journey one owning leg armed (the explicit invalidation seam a
    /// caller can name). A leg's own boundary can therefore never disarm another
    /// leg's armed journey.
    /// </summary>
    public static bool Disarm(uint actorObjId, string legOwner = "")
        => Intents.TryRemove((actorObjId, legOwner ?? ""), out _);

    /// <summary>
    /// Banks one CONSECUTIVE resolve miss for a moving target. The counter is what
    /// the one-allowed re-resolve rule reads; it is reset by a successful resolve.
    /// </summary>
    public static int NoteResolveMiss(uint actorObjId, string legOwner = "")
    {
        var key = (actorObjId, legOwner ?? "");
        if (!Intents.TryGetValue(key, out var current))
            return 0;
        var attempts = current.ResolveAttempts + 1;
        Intents[key] = current with { ResolveAttempts = attempts };
        return attempts;
    }

    /// <summary>Clears the consecutive-miss counter after a target resolved live.</summary>
    public static void NoteResolved(uint actorObjId, string legOwner = "")
    {
        var key = (actorObjId, legOwner ?? "");
        if (!Intents.TryGetValue(key, out var current) || current.ResolveAttempts == 0)
            return;
        Intents[key] = current with { ResolveAttempts = 0 };
    }

    /// <summary>
    /// Banks a repath: the chosen mode, the destination it was issued against
    /// (the next wake's drift comparison), and the spent attempt. The cached route
    /// is dropped, because a repath exists precisely to re-resolve the route.
    /// </summary>
    public static void NoteRepath(uint actorObjId, int repathCount, TravelMode mode, Vector3 destination, string legOwner = "")
    {
        var key = (actorObjId, legOwner ?? "");
        if (!Intents.TryGetValue(key, out var current))
            return;
        Intents[key] = current with
        {
            RepathCount = repathCount,
            Mode = mode,
            PriorDestination = destination,
            Route = []
        };
    }

    /// <summary>Records the mode/destination a leg was issued against (no counter change).</summary>
    public static void NoteIssued(uint actorObjId, TravelMode mode, Vector3 destination, string legOwner = "")
    {
        var key = (actorObjId, legOwner ?? "");
        if (!Intents.TryGetValue(key, out var current))
            return;
        Intents[key] = current with { Mode = mode, PriorDestination = destination };
    }

    /// <summary>Caches the resolved road route for the intent (the head is walked first).</summary>
    public static void SetRoute(uint actorObjId, IReadOnlyList<Vector3> waypoints, string legOwner = "")
    {
        var key = (actorObjId, legOwner ?? "");
        if (!Intents.TryGetValue(key, out var current))
            return;
        Intents[key] = current with { Route = waypoints };
    }

    /// <summary>
    /// Pops the route head whose waypoint the actor has REACHED (measured from
    /// <paramref name="position"/>). Returns true when at least one waypoint was
    /// consumed. The route is never re-queried for a head that was already walked —
    /// that is what keeps a road route progressing instead of restarting at
    /// junction one every wake.
    /// </summary>
    public static bool AdvanceRoute(uint actorObjId, Vector3 position, float reachedRadiusM, string legOwner = "")
    {
        var key = (actorObjId, legOwner ?? "");
        if (!Intents.TryGetValue(key, out var current) || current.Route.Count == 0)
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
        Intents[key] = current with { Route = remaining };
        return true;
    }

    /// <summary>
    /// Banks the intent's TERMINAL verdict. Sticky: once an intent reached a
    /// terminal, later wakes re-read the same named verdict instead of re-deriving
    /// one, and only a fresh arm clears it.
    /// </summary>
    public static void BankTerminal(uint actorObjId, TravelTerminal terminal, TravelReason reason, string legOwner = "")
    {
        var key = (actorObjId, legOwner ?? "");
        if (terminal == TravelTerminal.None || !Intents.TryGetValue(key, out var current))
            return;
        Intents[key] = current with { Terminal = terminal, Reason = reason };
    }

    /// <summary>Clears the whole store (the world-reset / test seam, mirroring the other bot memory clears).</summary>
    public static void ClearAll() => Intents.Clear();

    /// <summary>Test-only diagnostic: the number of armed intents.</summary>
    public static int Count => Intents.Count;
}
