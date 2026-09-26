#nullable enable

using System.Collections.Concurrent;

namespace AAEmu.Game.Core.Managers.Bots.Survival;

/// <summary>
/// THE SINGLE VETO PUBLISHER. One published survival fact per actor — the verdict
/// the survival brain reached, the named reason, and whether the veto stands —
/// written by exactly one entry point and read by every consumer.
///
/// Ownership: this is the ONLY survival-side store. The decision function
/// (<see cref="SurvivalBrain.Decide"/>) stays a pure function of
/// <see cref="SurvivalBrainInputs"/>; everything stateful lands here, keyed by the
/// actor's objId, bounded by a full clear — the same discipline
/// <c>CombatBrainEngagement</c>, <c>LootLedger</c> and <c>TravelIntentStore</c>
/// follow (memory-only, no persistence, no background sweep, no timers).
///
/// Two properties this store guarantees:
///  - the RULE lives once. <see cref="Publish(uint, in SurvivalBrainInputs, out SurvivalBrainDecision)"/>
///    is the only place a veto is produced, so the quest legs' veto precondition
///    and the lane's recorded fact can never disagree — a consumer reads what the
///    survival brain decided, never a second implementation of the same rule;
///  - the VETO is recomputed and re-published every wake a consumer asks, so a
///    cause that clears (the fight ends, hp recovers) clears the fact rather than
///    leaving a stale veto standing.
///
/// Fail-closed: an actor with no published entry reads NOT vetoed, so a consumer
/// can never inherit a veto nobody published, and unreadable vitals publish
/// <see cref="SurvivalReason.VitalsUnreadable"/> — a named hold that vetoes nobody.
/// </summary>
public static class SurvivalVetoState
{
    /// <summary>Published facts bound per process (a full clear, never a per-entry eviction policy).</summary>
    public const int MemoryBound = 256;

    /// <summary>One actor's published survival fact.</summary>
    public readonly record struct Record(
        SurvivalVerdict Verdict,
        SurvivalReason Reason,
        bool Veto,
        float HpRatio,
        bool FightEvidence);

    private static readonly ConcurrentDictionary<uint, Record> Published = new();

    /// <summary>
    /// THE PUBLISHER. Runs the one rule over the supplied inputs, records the fact
    /// for the actor, and reports the verdict reached. Returns the veto as the
    /// out-parameter-adjacent convenience the consumer needs plus the decision so a
    /// caller that wants the verb (the flee destination) still has it.
    ///
    /// Idempotent for identical inputs: the same frozen observation publishes the
    /// same fact, so a repeated selector evaluation cannot drift.
    /// </summary>
    public static bool Publish(uint actorObjId, in SurvivalBrainInputs inputs, out SurvivalBrainDecision decision)
    {
        decision = SurvivalBrain.Decide(inputs);
        Publish(actorObjId, decision);
        return decision.Veto;
    }

    /// <summary>Records an already-decided fact (the live adapter's seam after <c>Prepare</c>).</summary>
    public static void Publish(uint actorObjId, in SurvivalBrainDecision decision)
    {
        if (actorObjId == 0)
            return;
        if (Published.Count >= MemoryBound)
            Published.Clear();
        Published[actorObjId] = new Record(
            decision.Verdict, decision.Reason, decision.Veto, decision.HpRatio, decision.FightEvidence);
    }

    /// <summary>
    /// True while a survival condition owns this actor's wake, as the survival
    /// layer last published it. An actor with no published fact reads false
    /// (fail-closed: no veto is ever inherited from nobody).
    /// </summary>
    public static bool IsVetoed(uint actorObjId)
        => Published.TryGetValue(actorObjId, out var record) && record.Veto;

    /// <summary>Reads the last published fact for an actor, if one exists.</summary>
    public static bool TryGet(uint actorObjId, out Record record)
        => Published.TryGetValue(actorObjId, out record);

    /// <summary>Drops an actor's published fact (the actor left the wake path, or its state was reset).</summary>
    public static void Clear(uint actorObjId) => Published.TryRemove(actorObjId, out _);

    /// <summary>Clears every published fact (the world-reset / test seam, mirroring the other bot memory clears).</summary>
    public static void ClearAll() => Published.Clear();

    /// <summary>Test-only diagnostic: the number of published facts.</summary>
    public static int Count => Published.Count;
}
