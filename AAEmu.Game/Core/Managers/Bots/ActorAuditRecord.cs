namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Structured audit trace record for one actor request (ROADMAP M5:
/// {trace_id, actor_id, action, target_id, requested_at, started_at,
/// completed_at, result, state_changes}). Every action emits exactly one
/// record on its terminal transition; records are immutable.
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
    IReadOnlyList<string> StateChanges)
{
    /// <summary>Stable one-line log form (structured fields, no packet content).</summary>
    public override string ToString()
        => $"actor={ActorId} trace={TraceId} action={Action} target={TargetId} " +
           $"req={RequestedAtUtc:O} start={StartedAtUtc:O} done={CompletedAtUtc:O} " +
           $"result={Result} failure={Failure} detail={Detail}";
}
