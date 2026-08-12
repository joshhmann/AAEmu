using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Idempotency/correlation utilities for the M5 actor contract (ROADMAP M5:
/// "idempotency/correlation rules so retries and timeouts cannot duplicate
/// items, currency, labor consumption, quest credit, or interactions").
///
/// Two complementary surfaces:
///  - <see cref="Key"/> — deterministic request fingerprint used for
///    correlation and (when a controller supplies an explicit key) retry
///    dedupe.
///  - <see cref="EffectKey"/> — deterministic effect fingerprint that B1
///    action implementations record AFTER applying a real effect (an item,
///    currency change, labor spend, quest credit, or interaction) so a
///    re-run of the same logical operation can prove the effect is already
///    applied and must not be applied twice.
///
/// Rule: a retry is identified by the CALLER reusing an explicit
/// idempotency key. Requests without an explicit key always execute (they
/// still get a derived fingerprint for trace correlation). Only requests
/// with an explicit key are dedupe-gated by <see cref="ActorEffectLedger"/>.
/// </summary>
public static class ActorIdempotency
{
    /// <summary>
    /// Deterministic fingerprint of a request: action + target + skill +
    /// payload. Same inputs ⇒ same key (invariant culture, stable across
    /// processes). Used for correlation; never used to auto-dedupe (a
    /// repeat of an identical request is a new logical operation unless the
    /// caller declares it a retry with an explicit key).
    /// </summary>
    public static string Key(ActorActionType action, uint targetId, uint skillId, object? payload = null)
        => FormattableString.Invariant($"{action}:{targetId}:{skillId}:{payload ?? ""}");

    /// <summary>
    /// Deterministic effect fingerprint for a completed gameplay effect.
    /// B1 action implementations call this after the engine path applied
    /// the effect and record it on the ledger (kind examples: "item",
    /// "currency", "labor", "questcredit", "interaction"). The qualifier
    /// disambiguates multi-item effects (e.g. item template id).
    /// </summary>
    public static string EffectKey(string kind, uint targetId, string? qualifier = null)
        => FormattableString.Invariant($"{kind}:{targetId}{(qualifier is null ? "" : $":{qualifier}")}");
}

/// <summary>
/// Bounded per-actor ledger of terminal request outcomes keyed by explicit
/// idempotency key, plus applied-effect fingerprints for B1 actions.
///
/// Dedupe semantics (the "never execute twice" guarantee):
///  - A retry with an explicit key whose prior attempt ended Completed,
///    Interrupted, or TimedOut is Rejected(StateTransition) BEFORE any
///    execution starts — the audit record of the duplicate shows no
///    Running transition. Interrupted/TimedOut lock the key because the
///    execution started and the effect may have been applied; the
///    controller must issue a fresh key for a genuinely new operation.
///  - A retry after Rejected is allowed: every v1 rejection happens before
///    engine execution, so nothing was applied.
///
/// The ledger is NOT a transaction log — it is a dedupe/correlation
/// surface. It is bounded (oldest entries evicted); retries must reuse a
/// key within the retention window. A dedupe-refused duplicate (see
/// <see cref="ActorRequest.IsDedupeRejection"/>) does not record: the
/// locked outcome stays under the key so later retries are refused too.
/// </summary>
public sealed class ActorEffectLedger
{
    /// <summary>Max outcomes/effects retained (FIFO eviction).</summary>
    public const int MaxRecords = 256;

    /// <summary>One terminal outcome recorded under an explicit key.</summary>
    /// <param name="TraceId">Correlation id of the attempt that terminated.</param>
    /// <param name="Result">Terminal lifecycle state.</param>
    /// <param name="Failure">§17 taxonomy reason (null when not Rejected/TimedOut).</param>
    /// <param name="CompletedAtUtc">Terminal transition time.</param>
    public sealed record Outcome(Guid TraceId, ActorLifecycleState Result, ActorFailureReason? Failure, DateTime CompletedAtUtc);

    private readonly Dictionary<string, Outcome> _outcomes = [];
    private readonly Dictionary<string, Guid> _effects = [];
    private readonly Queue<string> _order = [];
    private readonly HashSet<string> _queued = [];

    /// <summary>Number of recorded outcomes (explicit keys).</summary>
    public int OutcomeCount => _outcomes.Count;

    /// <summary>Number of recorded effect fingerprints.</summary>
    public int EffectCount => _effects.Count;

    /// <summary>
    /// Records a terminal outcome under an explicit key. Replaces any prior
    /// outcome for the same key (a Rejected attempt is superseded by the
    /// retry's outcome). Returns false when the key was null/empty.
    /// </summary>
    public bool TryRecordOutcome(string? idempotencyKey, Guid traceId, ActorLifecycleState result, ActorFailureReason? failure)
    {
        if (string.IsNullOrEmpty(idempotencyKey))
            return false;
        _outcomes[idempotencyKey] = new Outcome(traceId, result, failure, DateTime.UtcNow);
        Touch(idempotencyKey);
        return true;
    }

    /// <summary>Reads the recorded outcome for an explicit key, if any.</summary>
    public bool TryGetOutcome(string? idempotencyKey, out Outcome outcome)
        => _outcomes.TryGetValue(idempotencyKey ?? "", out outcome!);

    /// <summary>
    /// Records an applied effect fingerprint (B1 actions call this after
    /// the effect lands). Returns false when the fingerprint was already
    /// recorded — the caller MUST NOT apply the effect again.
    /// </summary>
    public bool RecordEffect(string fingerprint, Guid traceId)
    {
        if (_effects.ContainsKey(fingerprint))
            return false;
        _effects[fingerprint] = traceId;
        Touch(fingerprint);
        return true;
    }

    /// <summary>True when the effect fingerprint was already applied (dedupe probe).</summary>
    public bool IsEffectApplied(string fingerprint)
        => _effects.ContainsKey(fingerprint);

    /// <summary>Trace id that applied the effect, when recorded.</summary>
    public bool TryGetEffectTrace(string fingerprint, out Guid traceId)
        => _effects.TryGetValue(fingerprint, out traceId);

    private void Touch(string key)
    {
        // A key is enqueued exactly once (distinct-key FIFO): re-recording
        // a key updates the dictionary in place without re-queuing, so
        // eviction always removes the oldest DISTINCT key.
        if (_queued.Add(key))
            _order.Enqueue(key);
        while (_order.Count > MaxRecords)
        {
            var oldest = _order.Dequeue();
            _queued.Remove(oldest);
            _outcomes.Remove(oldest);
            _effects.Remove(oldest);
        }
    }
}
