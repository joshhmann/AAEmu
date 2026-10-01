using System.Text.Json;

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
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
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
    {
        if (!Enabled || result == null || string.IsNullOrEmpty(cycleId))
            return;
        var seen = Interlocked.Increment(ref _eligibleWakes);
        if ((seen - 1) % Math.Max(1, SampleEvery) != 0)
            return;
        try
        {
            WriteLine(BuildRecord(characterId, cycleId, decideDetail ?? "", result));
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
    /// </summary>
    internal static JevShadowRecord BuildRecord(
        uint characterId,
        string cycleId,
        string decideDetail,
        QuestDecisionScenario.QuestRunResult result)
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
/// <summary>
/// One frozen shadow-log row (Jev teacher/critic Stage 1 disagreement
/// dataset, schema <c>jev-shadow/v1</c>): scenario/wake ids, a frozen-inputs
/// hash plus the frozen decide-detail ref, the brain's candidates + scores,
/// the brain decision + verdict/reason, and outcome-delta refs. Every field
/// is copied from the wake's already-frozen buffers (QuestRunResult +
/// QuestDecideDetail); nothing here is a live read and nothing here drives
/// runtime behavior. Serialized as one JSONL line (snake_case).
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
    DateTime EmittedAtUtc);

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
}
