#nullable enable

using System.Collections.Concurrent;

namespace AAEmu.Game.Core.Managers.Bots.Loot;

/// <summary>
/// The loot brain's MEMORY: the banked disposition of every corpse this actor
/// has already decided about — bank-only, so a disposition is WRITTEN when the
/// brain decided it and READ BACK on every later wake, and never spent, mutated,
/// or re-derived.
///
/// Ownership: this is the ONLY loot-side store. The brain's decision function
/// stays pure over <see cref="LootBrainInputs"/>; everything stateful lands here,
/// keyed by (actor objId, corpse objId), bounded by a full clear — the same
/// discipline the existing per-actor caches use (<c>QuestBehavior.LastCorpse</c>,
/// <c>LootedCorpses</c>, <c>CombatBrainEngagement</c>): memory-only, no
/// persistence, no background sweep, no timers.
///
/// Two things are banked and nothing else:
///  - <see cref="LootDisposition.Take"/> — the corpse's Loot verb ran its
///    terminal course. This is the brain's own bookkeeping of the same fact the
///    quest leg's loot-once memory records, so the decision layer can name WHY a
///    corpse is withheld without reading another layer's cache;
///  - <see cref="LootDisposition.Skipped"/> — a TERMINAL skip (the container was
///    junk, or the bag had no room). The cause is banked beside it so the later
///    wake's <see cref="LootReason.PriorSkip"/> decision can report the ORIGINAL
///    reason instead of inventing a generic one. A skip is never re-evaluated:
///    worth and space are properties of the corpse and of the actor's bag that a
///    refill does not change, and re-deciding them every wake is exactly the
///    churn the bank exists to prevent.
///
/// Every non-terminal verdict (a rival claim, an unreadable frame, a live
/// auto-attack loop, a hostile inside the safe radius) banks NOTHING: those
/// causes can clear, so the brain must see them fresh next wake.
/// </summary>
public static class LootLedger
{
    /// <summary>Ledger bound per process (a full clear, never a per-entry eviction policy).</summary>
    public const int MemoryBound = 512;

    /// <summary>One banked corpse disposition.</summary>
    public readonly record struct Entry(
        uint ActorObjId,
        uint CorpseObjId,
        LootDisposition Disposition,
        LootReason Cause,
        DateTime RecordedUtc);

    private static readonly ConcurrentDictionary<(uint ActorObjId, uint CorpseObjId), Entry> Entries = new();

    /// <summary>
    /// Banks a terminal disposition for a corpse. An <see cref="LootDisposition.Undecided"/>
    /// write is REFUSED — the ledger only carries terminal facts, so a caller can
    /// never accidentally pin a transient verdict onto a corpse.
    ///
    /// A <see cref="LootDisposition.Take"/> write is monotonic: once a corpse is
    /// banked as taken, a later skip can never overwrite it (a refilled container
    /// after a successful take is not a reason to re-decide the corpse).
    /// </summary>
    public static bool Bank(uint actorObjId, uint corpseObjId, LootDisposition disposition, LootReason cause, DateTime nowUtc)
    {
        if (actorObjId == 0 || corpseObjId == 0 || disposition == LootDisposition.Undecided)
            return false;

        var key = (actorObjId, corpseObjId);
        if (Entries.TryGetValue(key, out var prior) && prior.Disposition == LootDisposition.Take)
            return false;

        if (Entries.Count >= MemoryBound && !Entries.ContainsKey(key))
            Entries.Clear();

        Entries[key] = new Entry(actorObjId, corpseObjId, disposition, cause, nowUtc);
        return true;
    }

    /// <summary>Reads a corpse's banked disposition (false when nothing was banked for it).</summary>
    public static bool TryGet(uint actorObjId, uint corpseObjId, out Entry entry)
        => Entries.TryGetValue((actorObjId, corpseObjId), out entry);

    /// <summary>
    /// The banked disposition and cause for a corpse, as the brain's inputs want
    /// them — <see cref="LootDisposition.Undecided"/> with
    /// <see cref="LootReason.None"/> when nothing was banked.
    /// </summary>
    public static (LootDisposition Disposition, LootReason Cause) Read(uint actorObjId, uint corpseObjId)
        => TryGet(actorObjId, corpseObjId, out var entry)
            ? (entry.Disposition, entry.Cause)
            : (LootDisposition.Undecided, LootReason.None);

    /// <summary>Drops one corpse's banked row (the explicit invalidation seam a caller can name).</summary>
    public static bool Forget(uint actorObjId, uint corpseObjId)
        => Entries.TryRemove((actorObjId, corpseObjId), out _);

    /// <summary>Clears the whole ledger (the world-reset / test seam, mirroring the other bot memory clears).</summary>
    public static void ClearAll() => Entries.Clear();

    /// <summary>Test-only diagnostic: the number of banked rows.</summary>
    public static int Count => Entries.Count;
}
