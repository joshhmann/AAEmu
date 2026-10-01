using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Jev teacher/critic program, Stage 1 (Shadow): frozen shadow-log record +
/// disagreement-dataset writer. The brain decides; this surface only COPIES
/// the already-frozen wake evidence into a lane-side JSONL dataset for the
/// offline critic. It never decides, never mutates Character/world, never
/// makes model calls, and has no runtime authority.
///
/// Allocation discipline (HARD — BotRoamAllocationTests pins calm wakes to
/// ~768B): <see cref="MaybeEmitQuest"/> reads NOTHING and allocates NOTHING
/// while disabled (a static flag read, then return — ref args only), so a
/// flag-off wake is byte-identical to the pre-shadow behavior. Everything
/// expensive (candidate projection, hashing, JSON, file I/O) runs only when
/// explicitly opted in AND the sampling gate passes. Every field below is a
/// copy of an already-frozen buffer (the QuestRunResult, its rejection
/// proposals, its dispatched request stamp, the QuestDecideDetail observe
/// string) — never a fresh Character/world/dictionary read on a calm wake.
/// </summary>
public static class JevShadow
{
    /// <summary>Dataset schema version carried on every row.</summary>
    public const string SchemaVersion = "jev-shadow/v1";

    /// <summary>Bound for the frozen reason text copied into a row.</summary>
    public const int MaxReasonChars = 500;

    /// <summary>Bound for one candidate's rejection reason.</summary>
    public const int MaxCandidateReasonChars = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Brain inputs carry honest NaN unreadables (unmeasured distance/ratio).
        // Named literals keep those rows writable; v1 rows carry no floats, so
        // their bytes are unchanged.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly object WriteLock = new();
    private static long _eligibleWakes;

    /// <summary>
    /// Opt-in master switch. Default OFF (env <c>AAEMU_JEV_SHADOW</c> = 1/true
    /// to arm); flag-off wakes return on this read with zero allocation and
    /// zero behavior change. Settable directly for tests.
    /// </summary>
    public static bool Enabled { get; set; } = IsEnabledFromEnv();

    /// <summary>
    /// Sampling gate: when enabled, only every Nth eligible wake is written
    /// (1 = every wake). Env <c>AAEMU_JEV_SHADOW_SAMPLE</c>; values &lt; 1
    /// read as 1. Reset the counter with <see cref="ResetSamplingForTest"/>.
    /// </summary>
    public static int SampleEvery { get; set; } = SampleEveryFromEnv();

    /// <summary>
    /// Lane-side JSONL path (one record per line). Env
    /// <c>AAEMU_JEV_SHADOW_PATH</c>, else <c>Logs/jev-shadow.jsonl</c> under
    /// the app base directory. The writer creates the directory on demand.
    /// </summary>
    public static string FilePath { get; set; } = FilePathFromEnv();

    /// <summary>
    /// Emits one shadow row for a quest wake from ALREADY-FROZEN evidence
    /// only: the wake's cycle id, the mirrored QuestDecideDetail observe
    /// string, and the behavior runtime's QuestRunResult. Safe to call on
    /// every quest wake — while disabled this is a flag read + return.
    /// Never throws: every failure (including I/O) is swallowed so a lane
    /// sink fault can never raise into the tick.
    /// </summary>
    public static void MaybeEmitQuest(
        uint characterId,
        string? cycleId,
        string? decideDetail,
        QuestDecisionScenario.QuestRunResult? result)
        => MaybeEmitQuest(characterId, cycleId, decideDetail, result, null);

    /// <summary>
    /// Emits one shadow row with the quest wake's optional per-brain frozen
    /// input snapshots. Each snapshot MUST be an already-computed object the
    /// quest leg ran this wake (a <c>Prepared</c>'s <c>Inputs</c>, or a combat
    /// <c>Prepared</c>'s inputs) — copied by value/ref-copy here, never a
    /// fresh world read and never a new Planner run. Null when that brain did
    /// not run. Flag-off stays a flag read + return (no snapshot is touched).
    /// Never throws: every failure (including I/O) is swallowed so a lane
    /// sink fault can never raise into the tick.
    /// </summary>
    public static void MaybeEmitQuest(
        uint characterId,
        string? cycleId,
        string? decideDetail,
        QuestDecisionScenario.QuestRunResult? result,
        JevShadowBrains? brains)
    {
        if (!Enabled || result == null || string.IsNullOrEmpty(cycleId))
            return;
        var seen = Interlocked.Increment(ref _eligibleWakes);
        if ((seen - 1) % Math.Max(1, SampleEvery) != 0)
            return;
        try
        {
            WriteLine(BuildRecord(characterId, cycleId, decideDetail ?? "", result, brains));
        }
        catch (Exception)
        {
            // Lane-side sink only: a serialization or I/O fault must never
            // raise into the scheduler tick. (OSError swallow requirement.)
        }
    }

    /// <summary>
    /// Builds one shadow row from frozen wake evidence (pure over its
    /// inputs — the unit-test seam for the record shape).
    /// <paramref name="brains"/> carries the quest leg's already-computed
    /// per-brain frozen input snapshots for this wake (null per brain that
    /// did not run); inputs are value/ref-copied, never re-read or re-planned.
    /// </summary>
    internal static JevShadowRecord BuildRecord(
        uint characterId,
        string cycleId,
        string decideDetail,
        QuestDecisionScenario.QuestRunResult result,
        JevShadowBrains? brains = null)
    {
        var request = result.Request;
        var candidates = new List<JevShadowCandidate>(result.Rejections.Count);
        foreach (var rejection in result.Rejections)
        {
            var proposal = rejection.Proposal;
            candidates.Add(new JevShadowCandidate(
                proposal.Goal,
                proposal.Action.ToString(),
                proposal.Priority,
                proposal.PersonalityWeight,
                Truncate(rejection.Reason, MaxCandidateReasonChars)));
        }

        var candidateCount = request is { DecisionCandidates: > 0 }
            ? request.DecisionCandidates
            : result.Rejections.Count + (result.WorkSelected ? 1 : 0);
        var verdict = result.WorkSelected
            ? $"landed:{result.SelectedAction}"
            : result.FailStage;

        return new JevShadowRecord(
            SchemaVersion,
            result.Scenario,
            cycleId,
            request?.WakeSequence,
            characterId,
            HashInputs(cycleId, decideDetail),
            decideDetail,
            candidates,
            candidateCount,
            result.Rejections.Count,
            request?.DecisionGoal,
            result.SelectedAction?.ToString(),
            verdict,
            Truncate(string.IsNullOrEmpty(result.Explanation) ? result.FailReason : result.Explanation, MaxReasonChars),
            request?.DecisionPolicy,
            request == null
                ? null
                : new JevShadowRequestRef(
                    request.TraceId.ToString(),
                    request.Action.ToString(),
                    request.State.ToString()),
            result.CompletedQuestIds,
            result.TraceRecords.Count,
            brains?.Travel is { } travel ? JevShadowTravel.FromInputs(travel) : null,
            brains?.Survival is { } survival ? JevShadowSurvival.FromInputs(survival) : null,
            brains?.Needs is { } needs ? JevShadowNeeds.FromInputs(needs) : null,
            brains?.Combat is { } combat ? JevShadowCombat.FromPrepared(combat) : null,
            DateTime.UtcNow);
    }

    /// <summary>Resets the sampling counter (deterministic sampling tests).</summary>
    internal static void ResetSamplingForTest() => Interlocked.Exchange(ref _eligibleWakes, 0);

    private static void WriteLine(JevShadowRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOptions);
            var path = FilePath;
            lock (WriteLock)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(path, line + "\n");
            }
        }
        catch (Exception)
        {
            // OSError swallow: an unwritable lane path (missing volume,
            // directory-as-file, denied) must never raise into the tick.
        }
    }


    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "…";

    internal static string HashInputs(string cycleId, string decideDetail)
    {
        const ulong offset = 14695981039346656037ul;
        const ulong prime = 1099511628211ul;
        var hash = offset;
        HashPart(ref hash, prime, cycleId);
        HashPart(ref hash, prime, decideDetail);
        return hash.ToString("x16");
    }

    private static void HashPart(ref ulong hash, ulong prime, string part)
    {
        foreach (var ch in part)
        {
            hash ^= ch;
            hash *= prime;
        }
        hash ^= '\0';
        hash *= prime;
    }
    private static bool IsEnabledFromEnv()
        => Environment.GetEnvironmentVariable("AAEMU_JEV_SHADOW") is "1" or "true" or "True";
    private static int SampleEveryFromEnv()
        => int.TryParse(Environment.GetEnvironmentVariable("AAEMU_JEV_SHADOW_SAMPLE"), out var n) && n > 0 ? n : 1;
    private static string FilePathFromEnv()
    {
        var configured = Environment.GetEnvironmentVariable("AAEMU_JEV_SHADOW_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return Path.Combine(AppContext.BaseDirectory, "Logs", "jev-shadow.jsonl");
    }
}
/// <summary>
/// One frozen shadow-log row (Jev teacher/critic Stage 1 disagreement
/// dataset, schema <c>jev-shadow/v1</c>): scenario/wake ids, a frozen-inputs
/// hash plus the frozen decide-detail ref, the brain's candidates + scores,
/// the brain decision + verdict/reason, and outcome-delta refs. Every field
/// is copied from the wake's already-frozen buffers (QuestRunResult +
/// QuestDecideDetail); nothing here is a live read and nothing here drives
/// runtime behavior. Serialized as one JSONL line (snake_case).
///
/// The four optional brain snapshots (<see cref="Travel"/>,
/// <see cref="Survival"/>, <see cref="Needs"/>, <see cref="Combat"/>) carry
/// the quest leg's already-computed per-brain frozen Decide inputs for this
/// wake (null when that brain did not run), so the offline replay for those
/// brains can be fed from the lane. Additive: old readers ignore them.
/// </summary>
public sealed record JevShadowRecord(
    string Schema,
    string Scenario,
    string CycleId,
    long? WakeSequence,
    uint CharacterId,
    string InputsHash,
    string DecideDetail,
    IReadOnlyList<JevShadowCandidate> Candidates,
    int CandidateCount,
    int RejectionCount,
    string? SelectedGoal,
    string? SelectedAction,
    string Verdict,
    string Reason,
    string? Policy,
    JevShadowRequestRef? Request,
    IReadOnlyList<uint> CompletedQuestIds,
    int AuditRowCount,
    JevShadowTravel? Travel = null,
    JevShadowSurvival? Survival = null,
    JevShadowNeeds? Needs = null,
    JevShadowCombat? Combat = null,
    DateTime EmittedAtUtc = default);

/// <summary>
/// Carrier for one wake's optional per-brain frozen Decide inputs. Each slot
/// holds the already-computed inputs object the quest leg ran this wake (a
/// <c>Prepared</c>'s <c>Inputs</c>, or a combat <c>Prepared</c>); null when
/// that brain did not run. The shadow surface copies by value/ref-copy only —
/// never a fresh world read, never a new Planner run.
/// </summary>
public sealed record JevShadowBrains(
    TravelBrainInputs? Travel = null,
    SurvivalBrainInputs? Survival = null,
    NeedsBrainInputs? Needs = null,
    CombatBrainPlanner.Prepared? Combat = null);

/// <summary>
/// Frozen travel Decide inputs for one wake — value-copied from the quest
/// leg's already-computed <c>TravelBrainPlanner.Prepared.Inputs</c>, plus the
/// decision's own arm/verb/mode/terminal/reason tokens so the lane can join
/// inputs to verdict without re-running the chain. Null row when travel did
/// not run this wake.
/// </summary>
public sealed record JevShadowTravel(
    string TargetKind,
    uint TargetObjId,
    JevShadowVec3 SelfPosition,
    JevShadowVec3 Destination,
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
    string LegOutcome,
    bool LegLive,
    uint LegTargetObjId,
    bool LegDestinationKnown,
    JevShadowVec3 LegDestination,
    int PriorRepathCount,
    int RepathBudget,
    bool RouteAvailable,
    JevShadowVec3 RouteWaypoint,
    int RouteWaypointCount,
    string PriorMode,
    bool RetreatRequested,
    uint ThreatObjId,
    JevShadowVec3 ThreatPosition,
    string Arm,
    string Verb,
    string Mode,
    string Terminal,
    string Reason)
{
    internal static JevShadowTravel FromInputs(
        TravelBrainInputs inputs,
        TravelDecision decision = default)
        => new(
            inputs.TargetKind.ToString(),
            inputs.TargetObjId,
            JevShadowVec3.From(inputs.SelfPosition),
            JevShadowVec3.From(inputs.Destination),
            inputs.DistanceM,
            inputs.ArrivalRadiusM,
            inputs.LocalModeMaxM,
            inputs.FollowRequested,
            inputs.DestWorldKnown,
            inputs.ActorWorldId,
            inputs.ActorInstanceId,
            inputs.DestWorldId,
            inputs.DestInstanceId,
            inputs.TargetResolved,
            inputs.PriorResolveAttempts,
            inputs.LegOutcome.ToString(),
            inputs.LegLive,
            inputs.LegTargetObjId,
            inputs.LegDestinationKnown,
            JevShadowVec3.From(inputs.LegDestination),
            inputs.PriorRepathCount,
            inputs.RepathBudget,
            inputs.RouteAvailable,
            JevShadowVec3.From(inputs.RouteWaypoint),
            inputs.RouteWaypointCount,
            inputs.PriorMode.ToString(),
            inputs.RetreatRequested,
            inputs.ThreatObjId,
            JevShadowVec3.From(inputs.ThreatPosition),
            decision.Arm.ToString(),
            decision.Verb.ToString(),
            decision.Mode.ToString(),
            decision.Terminal.ToString(),
            decision.Reason.ToString());
}

/// <summary>
/// Frozen survival Decide inputs for one wake — value-copied from the quest
/// leg's already-computed survival inputs, plus the decision's own
/// verdict/reason/verb tokens. Null row when survival did not run this wake.
/// </summary>
public sealed record JevShadowSurvival(
    float SelfHpRatio,
    bool CombatRetreatPublished,
    uint CommittedTargetObjId,
    uint SelectedTargetObjId,
    JevShadowVec3 SelfPosition,
    JevShadowVec3 ThreatPosition,
    string Verdict,
    string Reason,
    string Verb,
    bool Veto)
{
    internal static JevShadowSurvival FromInputs(
        SurvivalBrainInputs inputs,
        SurvivalBrainDecision decision = default)
        => new(
            inputs.SelfHpRatio,
            inputs.CombatRetreatPublished,
            inputs.CommittedTargetObjId,
            inputs.SelectedTargetObjId,
            JevShadowVec3.From(inputs.SelfPosition),
            JevShadowVec3.From(inputs.ThreatPosition),
            decision.Verdict.ToString(),
            decision.Reason.ToString(),
            decision.Verb.ToString(),
            decision.Veto);
}

/// <summary>
/// Frozen needs Decide inputs for one wake — value-copied from the quest
/// leg's already-computed <c>NeedsBrainPlanner.Prepared.Inputs</c>, plus the
/// decision's own verdict/reason/verb tokens. Null row when needs did not run
/// this wake.
/// </summary>
public sealed record JevShadowNeeds(
    JevShadowVec3 SelfPosition,
    bool SoilReadable,
    bool SeedReadable,
    int SeedCount,
    bool OnValidSoil,
    string CropState,
    bool CropMature,
    float CropDistanceM,
    JevShadowVec3 CropPosition,
    bool CropEnRoute,
    uint CropObjId,
    uint CropTemplateId,
    int CropApproachAttempts,
    int MaxCropApproachAttempts,
    bool HarvestJustLanded,
    bool SoilEnRoute,
    bool SoilResolved,
    JevShadowVec3 SoilDestination,
    int SoilAttempts,
    int MaxSoilAttempts,
    float HarvestRangeM,
    string Verdict,
    string Reason,
    string Verb)
{
    internal static JevShadowNeeds FromInputs(
        NeedsBrainInputs inputs,
        NeedsBrainDecision decision = default)
        => new(
            JevShadowVec3.From(inputs.SelfPosition),
            inputs.SoilReadable,
            inputs.SeedReadable,
            inputs.SeedCount,
            inputs.OnValidSoil,
            inputs.CropState.ToString(),
            inputs.CropMature,
            inputs.CropDistanceM,
            JevShadowVec3.From(inputs.CropPosition),
            inputs.CropEnRoute,
            inputs.CropObjId,
            inputs.CropTemplateId,
            inputs.CropApproachAttempts,
            inputs.MaxCropApproachAttempts,
            inputs.HarvestJustLanded,
            inputs.SoilEnRoute,
            inputs.SoilResolved,
            JevShadowVec3.From(inputs.SoilDestination),
            inputs.SoilAttempts,
            inputs.MaxSoilAttempts,
            inputs.HarvestRangeM,
            decision.Verdict.ToString(),
            decision.Reason.ToString(),
            decision.Verb.ToString());
}

/// <summary>
/// Frozen combat Decide inputs for one wake — copied from the quest leg's
/// already-computed <c>CombatBrainPlanner.Prepared</c> (scalars by value,
/// candidate rows projected once). Carries the commitment/census counts so the
/// lane can tell a one-row quest commitment from a free competition. Null row
/// when combat did not run this wake.
/// </summary>
public sealed record JevShadowCombat(
    string Role,
    bool Alive,
    float SelfHpRatio,
    byte SelfLevel,
    JevShadowVec3 SelfPosition,
    int EnemyCount,
    float NearestEnemyDistanceM,
    bool TookDamageThisFrame,
    bool CrowdControlled,
    bool CcAvailable,
    bool IsAutoAttackLive,
    uint HealItemTemplateId,
    float SkillMinRangeM,
    float SkillMaxRangeM,
    uint SelectedSkillId,
    uint LastSkillUsed,
    uint IncumbentObjId,
    bool IncumbentValid,
    bool IncumbentUnknown,
    int IncumbentScore,
    bool CommitmentInForce,
    JevShadowVec3 IncumbentPosition,
    float IncumbentDistanceM,
    float IncumbentLeashDriftM,
    float LeashBudgetM,
    IReadOnlyList<JevShadowCombatCandidate> Candidates,
    int CandidateCount,
    int UnresolvedCount,
    int RejectedCount,
    uint CommittedQuestTarget)
{
    internal static JevShadowCombat FromPrepared(in CombatBrainPlanner.Prepared prepared)
    {
        var inputs = prepared.Inputs;
        var rows = new List<JevShadowCombatCandidate>(prepared.CandidateCount);
        foreach (var row in inputs.Candidates)
            rows.Add(new JevShadowCombatCandidate(
                row.ObjId,
                row.AttentionScore,
                row.DistanceM,
                row.TargetLevel,
                row.TargetHpRatio,
                row.QuestRelevant,
                row.LeashDistanceM));
        return new JevShadowCombat(
            inputs.Role.ToString(),
            inputs.Alive,
            inputs.SelfHpRatio,
            inputs.SelfLevel,
            JevShadowVec3.From(inputs.SelfPosition),
            inputs.EnemyCount,
            inputs.NearestEnemyDistanceM,
            inputs.TookDamageThisFrame,
            inputs.CrowdControlled,
            inputs.CcAvailable,
            inputs.IsAutoAttackLive,
            inputs.HealItemTemplateId,
            inputs.SkillMinRangeM,
            inputs.SkillMaxRangeM,
            inputs.SelectedSkillId,
            inputs.LastSkillUsed,
            inputs.IncumbentObjId,
            inputs.IncumbentValid,
            inputs.IncumbentUnknown,
            inputs.IncumbentScore,
            inputs.CommitmentInForce,
            JevShadowVec3.From(inputs.IncumbentPosition),
            inputs.IncumbentDistanceM,
            inputs.IncumbentLeashDriftM,
            inputs.LeashBudgetM,
            rows,
            prepared.CandidateCount,
            prepared.UnresolvedCount,
            prepared.RejectedCount,
            prepared.CommittedQuestTarget);
    }
}
/// <summary>
/// One non-selected brain candidate with its deterministic score
/// (priority + personality weight) and its rejection reason — copied from
/// the frozen <see cref="BotProposalRejection"/> list, never re-evaluated.
/// </summary>
public sealed record JevShadowCandidate(
    string Goal,
    string Action,
    int Priority,
    int Weight,
    string Reason);

/// <summary>
/// Outcome-request ref: the dispatched request's join keys, copied from the
/// frozen request stamp (trace id, action, terminal state).
/// </summary>
public sealed record JevShadowRequestRef(
    string TraceId,
    string Action,
    string State);

/// <summary>One frozen combat candidate row (planner-resolved, never re-scored).</summary>
public sealed record JevShadowCombatCandidate(
    uint ObjId,
    int AttentionScore,
    float DistanceM,
    byte TargetLevel,
    float TargetHpRatio,
    bool QuestRelevant,
    float LeashDistanceM);

/// <summary>A JSON-friendly frozen position (System.Numerics vectors carry no DTO shape).</summary>
public sealed record JevShadowVec3(float X, float Y, float Z)
{
    internal static JevShadowVec3 From(Vector3 value) => new(value.X, value.Y, value.Z);
}
