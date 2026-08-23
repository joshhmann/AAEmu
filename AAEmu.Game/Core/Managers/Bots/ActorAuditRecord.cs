using System.Text.Json;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Structured audit trace record for one actor request (ROADMAP M5:
/// {trace_id, actor_id, action, target_id, requested_at, started_at,
/// completed_at, result, state_changes}). Every action emits exactly one
/// record on its terminal transition; records are immutable.
///
/// v2 ADDITIVE fields (ROADMAP M7 hardening #4 — causal traces):
/// target_hp_before / target_hp_after / effect_observed /
/// effect_wait_ms. Old consumers ignore unknown keys; existing field
/// names never change (contract rule — renames require a version bump).
/// </summary>
/// <param name="TraceId">Correlation id of the request.</param>
/// <param name="ActorId">The embodied character's objId.</param>
/// <param name="Action">Which validated action was requested.</param>
/// <param name="TargetId">Primary target objId (0 when not applicable).</param>
/// <param name="RequestedAtUtc">Request creation time.</param>
/// <param name="StartedAtUtc">First Running transition time (null when rejected pre-start).</param>
/// <param name="CompletedAtUtc">Terminal transition time.</param>
/// <param name="Result">Terminal lifecycle state (Completed/Rejected/Interrupted/TimedOut).</param>
/// <param name="Failure">Spec §17 taxonomy reason (null for Completed/Interrupted).</param>
/// <param name="StateChanges">Full transition log, oldest first.</param>
/// <param name="TargetHpBefore">
/// v2 additive: the cast target's HP sampled at cast acceptance (null =
/// not measured — non-unit targets, observation disabled, or window
/// still pending).
/// </param>
/// <param name="TargetHpAfter">
/// v2 additive: the cast target's HP after the bounded effect
/// observation window resolved.
/// </param>
/// <param name="EffectObserved">
/// v2 additive: null = not measured; true = the target's HP changed
/// within the observation window (effect landed); false = the window
/// expired with no HP change (failed hit vs delayed-effect
/// discriminator). Observation outcome NEVER changes Result.
/// </param>
/// <param name="EffectWait">
/// v2 additive: how long the bounded observation window waited before
/// resolving (≈0 for an immediately observed effect, ≈window for a
/// no-change expiry).
/// </param>
public sealed record ActorAuditRecord(
    Guid TraceId,
    uint ActorId,
    ActorActionType Action,
    uint TargetId,
    DateTime RequestedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    ActorLifecycleState Result,
    ActorFailureReason? Failure,
    string? Detail,
    IReadOnlyList<string> StateChanges,
    int? TargetHpBefore = null,
    int? TargetHpAfter = null,
    bool? EffectObserved = null,
    TimeSpan? EffectWait = null)
{
    /// <summary>Stable one-line log form (structured fields, no packet content).</summary>
    public override string ToString()
        => $"actor={ActorId} trace={TraceId} action={Action} target={TargetId} " +
           $"req={RequestedAtUtc:O} start={StartedAtUtc:O} done={CompletedAtUtc:O} " +
           $"result={Result} failure={Failure} detail={Detail}";

    /// <summary>
    /// Stable JSON form for the control-plane API (ROADMAP M5 field names,
    /// snake_case, declaration-ordered). The API consumes this shape; field
    /// names are contract and must not change without a version bump. Times
    /// are ISO-8601 (UTC), enums render as names, state_changes is the full
    /// transition log oldest-first.
    ///
    /// v2 ADDITIVE keys (M7 hardening #4): target_hp_before,
    /// target_hp_after, effect_observed, effect_wait_ms. Additive only —
    /// every v1 key keeps its exact name and shape; old consumers ignore
    /// unknown keys. Unmeasured observations serialize as null.
    /// </summary>
    public string ToJson()
        => JsonSerializer.Serialize(new
        {
            trace_id = TraceId,
            actor_id = ActorId,
            action = Action.ToString(),
            target_id = TargetId,
            requested_at = RequestedAtUtc,
            started_at = StartedAtUtc,
            completed_at = CompletedAtUtc,
            result = Result.ToString(),
            failure = Failure?.ToString(),
            detail = Detail,
            state_changes = StateChanges,
            // v2 additive causal-trace fields (see record doc).
            target_hp_before = TargetHpBefore,
            target_hp_after = TargetHpAfter,
            effect_observed = EffectObserved,
            effect_wait_ms = EffectWait?.TotalMilliseconds
        });
}
