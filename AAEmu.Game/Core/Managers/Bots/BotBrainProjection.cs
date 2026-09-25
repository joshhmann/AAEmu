namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Phase 1 Brain Inspector projection (diagnostics half): one bounded,
/// read-only per-bot view over production-owned state — the arbiter
/// decorator's wake counter, the quest behavior runtime's lifecycle fields,
/// the executor's live actor (request + audit trace), and the roam state's
/// leg/travel flags.
///
/// The dashboard consumes this projection ONLY. It never decides, owns
/// state, mutates Character, or re-reads Character to infer behavior:
/// activity/behavior/phase come from <see cref="BotBehaviorRuntime"/> fields,
/// funnel tallies from the recorded DECIDE fail detail (the same bracket
/// block the bridge consumes from <c>lastDetail</c>), and history from
/// <see cref="ActorAuditRecord"/> rows. Absent stays absent (null) — nothing
/// is synthesized, per the ActorRequest join-key contract.
///
/// Pure static projection (no DI, no world lookups) so it is unit-testable
/// against recorded evidence shapes without a live server.
/// </summary>
public static class BotBrainProjection
{
    /// <summary>History rows per inspector snapshot (execution order, oldest first).</summary>
    public const int DefaultHistoryLimit = 20;

    /// <summary>Rejection reasons per snapshot (bounded).</summary>
    public const int MaxRejections = 8;

    /// <summary>History reason text bound (Failure + Detail, truncated).</summary>
    public const int MaxReasonChars = 280;

    /// <summary>
    /// Decision-funnel tallies parsed from the recorded DECIDE fail detail
    /// (<c>[swept=N targets=.. offerings=M inBand=K legal=J firstZero=R]</c>,
    /// appended by the quest behavior). Trace-derived counts only — null when
    /// no DECIDE block was recorded (nothing to parse, never defaulted).
    /// </summary>
    public sealed record BotQuestFunnel(
        int SweptTargets,
        string? SweptTargetIds,
        int Offerings,
        int InBand,
        int Legal,
        string? FirstZero)
    {
        /// <summary>
        /// Parses the funnel bracket the quest leg appends to its DECIDE
        /// FailReason. Returns null when the text carries no bracket block.
        /// </summary>
        public static BotQuestFunnel? TryParseFailDetail(string? failReason)
        {
            if (string.IsNullOrEmpty(failReason))
                return null;
            var open = failReason.LastIndexOf('[');
            var close = failReason.LastIndexOf(']');
            if (open < 0 || close <= open)
                return null;
            var inner = failReason.Substring(open + 1, close - open - 1);
            int? swept = null;
            string? targets = null;
            int? offerings = null;
            int? inBand = null;
            int? legal = null;
            string? firstZero = null;
            foreach (var part in inner.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = part.Substring(0, eq);
                var value = part.Substring(eq + 1);
                switch (key)
                {
                    case "swept" when int.TryParse(value, out var n): swept = n; break;
                    case "targets": targets = value; break;
                    case "offerings" when int.TryParse(value, out var n): offerings = n; break;
                    case "inBand" when int.TryParse(value, out var n): inBand = n; break;
                    case "legal" when int.TryParse(value, out var n): legal = n; break;
                    case "firstZero": firstZero = value; break;
                }
            }
            if (swept == null || offerings == null || inBand == null || legal == null || firstZero == null)
                return null;
            return new BotQuestFunnel(swept.Value, targets, offerings.Value, inBand.Value, legal.Value, firstZero);
        }
    }

    /// <summary>
    /// One bounded history row: the recorded per-request join keys
    /// (wake sequence, decision cycle, trace) plus the recorded outcome.
    /// Per-row activity/behavior/phase are NOT stored per-row in production
    /// state, so the row carries the recorded goal/policy instead — the
    /// CURRENT activity/behavior/phase head the snapshot.
    /// </summary>
    public sealed record BotBrainHistoryRow(
        long? WakeSequence,
        string? DecisionCycleId,
        Guid TraceId,
        string Action,
        string Result,
        string? Reason,
        string? Goal,
        string? Policy);

    /// <summary>
    /// Per-bot Brain Inspector snapshot. Every field is a read of
    /// production-owned state; see <see cref="Capture"/> inputs.
    /// </summary>
    public sealed record BotBrainSnapshot(
        uint CharacterId,
        string Name,
        // Identity: per-bot wake counter (null = decorator unavailable) +
        // per-wake decision cycle id (null = behavior never ticked). No
        // numeric decision counter exists in production; the CycleId string
        // (quest-{id}-{ticks}) is the wake→terminal join key.
        long? WakeSequence,
        string? DecisionCycleId,
        // Lifecycle (runtime fields, never inferred).
        string? Activity,
        string Behavior,
        string Phase,
        string Status,
        string? BehaviorInstanceId,
        // Position truth (identity reads off the acting character).
        float X, float Y, float Z,
        uint ZoneId,
        uint WorldId,
        uint InstanceId,
        // Target (the actor's live target reference, resolved by the engine).
        uint TargetObjId,
        string? TargetType,
        // Decision funnel (DECIDE-block tallies; null when none recorded).
        BotQuestFunnel? Funnel,
        // Live actor request (nulls = actor idle — stated, never bare).
        Guid? LiveTraceId,
        string? LiveAction,
        string? LiveState,
        string? LiveDetail,
        // Last tick evidence.
        bool WorkSelected,
        string? SelectedAction,
        string Explanation,
        string FailStage,
        string? Failure,
        string FailReason,
        IReadOnlyList<uint> CompletedQuestIds,
        int RejectionCount,
        IReadOnlyList<string> Rejections,
        string? LastFailure,
        // One-line recorded-fact summary (never a bare status word).
        string StatusLine,
        DateTimeOffset? StartedAt,
        DateTimeOffset? LastProgressAt,
        // Next wake: the quest leg is purely reactive (always null today).
        DateTime? NextWakeUtc,
        string? NextWakeHint,
        // Leg flags (roam state reads).
        bool QuestLegActive,
        float? QuestTravelX,
        float? QuestTravelY,
        float? QuestTravelZ,
        string? QuestTravelReason,
        // Bounded recent audit history (execution order, oldest first).
        IReadOnlyList<BotBrainHistoryRow> History);

    /// <summary>
    /// Projects one bot's inspector snapshot. All inputs are reads of
    /// production-owned state assembled by the caller (controller/bridge):
    /// identity + position off the acting character, sequences from the
    /// decorator + runtime, lifecycle from the runtime, the live request +
    /// trace from the actor, leg flags from the roam state.
    /// </summary>
    public static BotBrainSnapshot Capture(
        uint characterId,
        string name,
        float x, float y, float z,
        uint zoneId, uint worldId, uint instanceId,
        uint targetObjId, string? targetType,
        long? wakeSequence,
        string? arbiterActivity,
        BotBehaviorRuntime? runtime,
        ActorRequest? liveRequest,
        IReadOnlyList<ActorAuditRecord>? trace,
        bool questLegActive,
        float? questTravelX, float? questTravelY, float? questTravelZ,
        string? questTravelReason,
        int historyLimit = DefaultHistoryLimit)
    {
        var result = runtime?.LastResult;
        var funnel = result != null && result.FailStage == "DECIDE"
            ? BotQuestFunnel.TryParseFailDetail(result.FailReason)
            : null;
        var liveRunning = liveRequest is { IsTerminal: false };
        var status = runtime?.Status.ToString() ?? nameof(BotBehaviorStatus.NotStarted);
        var phase = runtime?.CurrentPhase ?? "idle";
        var statusLine = BuildStatusLine(status, phase, liveRunning, liveRequest,
            runtime?.CurrentActivity ?? arbiterActivity, funnel, result, runtime?.LastFailure);

        var rows = trace ?? [];
        var capped = Math.Clamp(historyLimit, 1, 100);
        var history = rows
            .TakeLast(capped)
            .Select(r => new BotBrainHistoryRow(
                r.WakeSequence,
                r.DecisionCycleId,
                r.TraceId,
                r.Action.ToString(),
                r.Result.ToString(),
                BuildReason(r.Failure?.ToString(), r.Detail),
                r.DecisionGoal,
                r.DecisionPolicy))
            .ToList();

        var rejections = result?.Rejections.Take(MaxRejections)
            .Select(r => $"{r.Proposal.Goal}: {r.Reason}")
            .ToList() ?? [];

        return new BotBrainSnapshot(
            characterId, name,
            wakeSequence,
            runtime?.BehaviorInstanceId,
            runtime?.CurrentActivity ?? arbiterActivity,
            runtime?.CurrentBehavior ?? BotBehaviorRuntime.QuestBehaviorName,
            phase, status,
            runtime?.BehaviorInstanceId,
            x, y, z, zoneId, worldId, instanceId,
            targetObjId, targetType,
            funnel,
            liveRunning ? liveRequest!.TraceId : null,
            liveRunning ? liveRequest!.Action.ToString() : null,
            liveRequest?.State.ToString(),
            liveRequest?.Detail,
            result?.WorkSelected ?? false,
            result?.SelectedAction?.ToString(),
            result?.Explanation ?? "",
            result?.FailStage ?? "",
            result?.Failure?.ToString(),
            result?.FailReason ?? "",
            result?.CompletedQuestIds ?? [],
            result?.Rejections.Count ?? 0,
            rejections,
            runtime?.LastFailure,
            statusLine,
            runtime?.StartedAt,
            runtime?.LastProgressAt,
            null,
            runtime == null
                ? "unknown — behavior never ticked for this bot"
                : "reactive — runs when the arbiter elects quest.*; no self-scheduled wake",
            questLegActive,
            questTravelX, questTravelY, questTravelZ,
            questTravelReason,
            history);
    }

    private static string BuildStatusLine(
        string status, string phase, bool liveRunning, ActorRequest? liveRequest,
        string? activity, BotQuestFunnel? funnel,
        QuestDecisionScenario.QuestRunResult? result, string? lastFailure)
    {
        if (liveRunning && liveRequest != null)
            return $"{status}:{phase} — running {liveRequest.Action} ({liveRequest.State})";
        if (status == nameof(BotBehaviorStatus.Blocked))
        {
            var firstZero = funnel?.FirstZero != null ? $" firstZero={funnel.FirstZero}" : "";
            var explanation = string.IsNullOrEmpty(result?.Explanation) ? "" : $" — {Truncate(result.Explanation, 160)}";
            return $"{status}:{phase}{firstZero} — no legal quest proposal{explanation}";
        }
        if (status == nameof(BotBehaviorStatus.Failed))
            return $"{status}:{phase} — {result?.FailStage}: {lastFailure}";
        if (status == nameof(BotBehaviorStatus.Waiting))
            return $"{status}:{phase} — actor busy, work queued for next wake";
        if (status == nameof(BotBehaviorStatus.NotStarted))
            return $"{status}:{phase} — behavior never ticked (activity={activity ?? "none"})";
        if (status == nameof(BotBehaviorStatus.Cancelled))
            return $"{status}:{phase} — electing activity moved away from quest.*";
        return $"{status}:{phase} — actor idle, no live request";
    }

    private static string? BuildReason(string? failure, string? detail)
    {
        var combined = string.IsNullOrEmpty(failure)
            ? detail ?? ""
            : string.IsNullOrEmpty(detail) ? failure : $"{failure}: {detail}";
        if (string.IsNullOrEmpty(combined))
            return null;
        return Truncate(combined, MaxReasonChars);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "…";

    /// <summary>
    /// Cheap per-bot bucket input for the fleet view: flag reads only, no
    /// behavior inference. Buckets are exclusive by precedence
    /// blocked › questing › farming › combat › roaming › idle.
    /// </summary>
    public sealed record BotFleetInput(
        bool IsActive,
        bool QuestLegActive,
        bool NeedsLegActive,
        BotRoamStepExecutor.NeedsFarmLoopPhase NeedsFarmPhase,
        bool HasCombatTarget,
        bool HasRoute,
        bool IsBlocked);

    /// <summary>
    /// Fleet view: state/leg counts plus the already-available scheduler
    /// metrics passthrough. No per-bot rows (bounded payload).
    /// </summary>
    public sealed record BotFleetSnapshot(
        DateTime GeneratedAtUtc,
        int Active,
        int Dormant,
        int Questing,
        int Farming,
        int Combat,
        int Roaming,
        int Idle,
        int Blocked,
        PlayerBotSchedulerMetrics? Scheduler);

    /// <summary>Buckets fleet inputs by the documented precedence.</summary>
    public static BotFleetSnapshot CaptureFleet(
        IEnumerable<BotFleetInput> inputs, PlayerBotSchedulerMetrics? scheduler)
    {
        var (active, dormant, questing, farming, combat, roaming, idle, blocked) = (0, 0, 0, 0, 0, 0, 0, 0);
        foreach (var b in inputs)
        {
            if (!b.IsActive)
            {
                dormant++;
                continue;
            }
            active++;
            if (b.IsBlocked) blocked++;
            else if (b.QuestLegActive) questing++;
            else if (b.NeedsLegActive || b.NeedsFarmPhase != BotRoamStepExecutor.NeedsFarmLoopPhase.Idle) farming++;
            else if (b.HasCombatTarget) combat++;
            else if (b.HasRoute) roaming++;
            else idle++;
        }
        return new BotFleetSnapshot(DateTime.UtcNow,
            active, dormant, questing, farming, combat, roaming, idle, blocked, scheduler);
    }
}
