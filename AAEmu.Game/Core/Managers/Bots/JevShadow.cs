using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Jev teacher/critic program, Stage 1 (Shadow): frozen shadow-log record +
/// disagreement-dataset writer. The brain decides; this surface only COPIES
/// the already-frozen wake evidence into a lane-side JSONL dataset for the
/// offline critic. It never decides, never mutates Character/world, never
/// makes model calls, and has no runtime authority.
///
/// Allocation discipline (HARD — BotRoamAllocationTests pins calm wakes to
/// ~768B): EVERY entry point here — <see cref="MaybeEmitQuest(uint,string?,string?,QuestDecisionScenario.QuestRunResult,JevShadowBrains?)"/>,
/// <see cref="MaybeEmitNeeds"/>, and the whole wake-scoped capture surface
/// (<see cref="BeginWake"/>, the four <c>Capture*</c> methods, <see cref="TakeBrains"/>)
/// — reads NOTHING and allocates NOTHING while disabled: a static flag read,
/// then return (ref args only). A flag-off wake is byte-identical to the
/// pre-shadow behavior. Everything expensive (candidate projection, hashing,
/// JSON build) runs only when explicitly opted in AND the sampling gate
/// passes — and file I/O never runs on the tick at all: sampled wakes enqueue
/// the already-built JSON line for a single background writer (bounded queue,
/// drop-newest when full, drops counted, writer faults swallowed).
///
/// Frozen-input discipline: every field below is a COPY of a buffer the wake
/// had already frozen (the run result, its rejection proposals, its selected
/// proposal, its dispatched request stamp, the observe detail string, and each
/// brain's own already-computed <c>Prepared.Inputs</c>) — never a fresh
/// Character/world/dictionary read on a calm wake, and never a re-run planner.
///
/// Wake scoping: the per-brain inputs are captured per ACTOR for the CURRENT
/// wake (<see cref="BeginWake"/> resets them before the legs run), so a row
/// reports a brain as absent when it did not run rather than inheriting the
/// previous wake's inputs.
/// </summary>
public static class JevShadow
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Dataset schema version carried on every row.
    /// <para>
    /// v3 (2026-10-02) adds <c>target_id</c> to every candidate row —
    /// hard rejections, legal alternatives, and the incumbent — copied from the
    /// proposal's own <see cref="BotDecisionProposal.TargetId"/>. Before v3 a
    /// reviewer could see that an alternative existed and how it scored, but
    /// never WHICH objective it referred to, so a legal loser could not be
    /// joined to the quest it named. The field is ADDITIVE: nothing else moves,
    /// and <c>candidate_count</c> keeps its v2 selector-truth meaning unchanged.
    /// </para>
    /// <para>
    /// v2 (2026-10-02) was a BEHAVIOURAL cutover, not an additive one: it added
    /// <c>legal_alternatives</c> (the proposals that were legal but lost the
    /// preference order, previously invisible) and redefined
    /// <c>candidate_count</c> as selector truth — hard rejections + every legal
    /// proposal, the winner included — where v1 reported only
    /// rejections + 1 and therefore under-reported any wake with legal losers.
    /// A v1 reader must not consume a v2 row: the count means something
    /// different.
    /// </para>
    /// <para>
    /// Rows already written stay in the version they were written as. Historical
    /// v1 and v2 datasets are never relabelled or rewritten; a v2 row simply has
    /// no <c>target_id</c> to read.
    /// </para>
    /// </summary>
    public const string SchemaVersion = "jev-shadow/v3";

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

    /// <summary>
    /// Bound for sampled lines awaiting the background writer. When full the
    /// tick drops the NEWEST line (the incoming wake) and counts it in
    /// <see cref="DroppedLineCount"/> — it never blocks and never evicts.
    /// Drop-newest (not oldest): under burst the tick does the minimum work
    /// (one count check, no dequeue churn) and the file stays a
    /// prefix-contiguous FIFO (the first N sampled wakes are complete, with
    /// no head gaps to explain). Test seam: lower it to observe drops.
    /// </summary>
    internal static int MaxQueuedLines { get; set; } = 1024;

    /// <summary>Sampled lines dropped while the queue was full (drop-newest).</summary>
    internal static long DroppedLineCount
    {
        get { lock (QueueLock) return _droppedLines; }
    }

    /// <summary>Lines queued but not yet written (test seam).</summary>
    internal static int QueuedLineCount
    {
        get { lock (QueueLock) return _queue.Count; }
    }

    /// <summary>Lines enqueued but not yet drained, queued plus in-flight (test seam).</summary>
    internal static long PendingLineCount => Volatile.Read(ref _pendingLines);

    private static readonly object QueueLock = new();
    private static readonly Queue<QueuedLine> _queue = new();
    private static long _droppedLines;
    private static Thread? _writerThread;
    private static bool _writerPausedForTest;
    private static long _pendingLines;
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
            var line = JsonSerializer.Serialize(
                BuildRecord(characterId, cycleId, decideDetail ?? "", result, brains), JsonOptions);
            EnqueueLine(line);
        }
        catch (Exception)
        {
            // Lane-side sink only: a serialization or enqueue fault must never
            // raise into the scheduler tick. (OSError swallow requirement.)
        }
        finally
        {
            // The row was built from the carrier we were handed, so this wake can
            // never capture again: seal the id (which makes it evictable under
            // the cap) so the caller may release the slot. Without this a bot
            // whose wake never emits would pin its slot until its next wake, and
            // the table would grow toward "every actor ever seen" rather than
            // "mid-wake bots".
            EndWake(characterId);
            ReportEvictionsIfAny();
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
        => BuildRow(
            characterId, cycleId, decideDetail, brains,
            result.Scenario, result.WorkSelected, result.SelectedAction,
            result.FailStage, result.Explanation, result.FailReason,
            result.Rejections, result.SelectedProposal, result.Request,
            result.CompletedQuestIds, result.TraceRecords.Count, result.LegalAlternatives,
            result.SelectionCandidateCount);

    /// <summary>
    /// The NEEDS arbitration wake's row. Same shape as the quest row, built from
    /// the needs leg's OWN already-frozen <see cref="NeedsDecisionScenario.NeedsRunResult"/>
    /// — the needs cycle is a separate decision run the quest emit point never
    /// observes, so it needs its own entry point to be harvestable at all.
    /// </summary>
    internal static JevShadowRecord BuildNeedsRecord(
        uint actorObjId,
        NeedsDecisionScenario.NeedsRunResult result,
        JevShadowBrains? brains)
        => BuildRow(
            actorObjId,
            $"needs-{actorObjId}-{result.Request?.TraceId:N}",
            result.Explanation,
            brains,
            result.Scenario, result.WorkSelected, result.SelectedAction,
            result.FailStage, result.Explanation, result.FailReason,
            result.Rejections, result.SelectedProposal, result.Request,
            [], result.TraceRecords.Count, result.LegalAlternatives, result.SelectionCandidateCount);

    /// <summary>
    /// The ONE row builder every class shares, so a challenger and the incumbent
    /// are always scored on identical fields. Pure over its arguments: the
    /// candidate list, the incumbent's own score row, and the per-brain snapshots
    /// are all COPIES of buffers the wake already froze — nothing here re-selects,
    /// re-scores, re-plans, or reads the world.
    /// </summary>
    private static JevShadowRecord BuildRow(
        uint characterId,
        string cycleId,
        string decideDetail,
        JevShadowBrains? brains,
        string scenario,
        bool workSelected,
        ActorActionType? selectedAction,
        string failStage,
        string explanation,
        string failReason,
        IReadOnlyList<BotProposalRejection> rejections,
        BotDecisionProposal? selectedProposal,
        ActorRequest? request,
        IReadOnlyList<uint> completedQuestIds,
        int auditRowCount,
        IReadOnlyList<BotDecisionProposal>? legalAlternatives,
        int? selectionCandidateCount)
    {
        var candidates = new List<JevShadowCandidate>(rejections.Count);
        foreach (var rejection in rejections)
        {
            var proposal = rejection.Proposal;
            candidates.Add(new JevShadowCandidate(
                proposal.Goal,
                proposal.Action.ToString(),
                proposal.Priority,
                proposal.PersonalityWeight,
                Truncate(rejection.Reason, MaxCandidateReasonChars),
                // v3: the refused proposal's own target, so an illegal
                // candidate is as identifiable as a legal one.
                proposal.TargetId));
        }

        // The LEGAL-but-unselected proposals, kept in their own array so the
        // schema tells the three kinds apart without inference:
        //   candidates[]       -> hard-precondition REJECTIONS (illegal)
        //   legal_alternatives[] -> legal, lost the preference order
        //   selected_candidate  -> the incumbent the game actually took
        // Bounded by the selector's own MaxCandidates and by the reason cap, so
        // a row can never grow without bound. NULL means the lane did not ask
        // (flag off); a COLLECTED wake always serializes an array, empty when
        // the winner was the only legal proposal — so "collected, and there
        // were none" never reads the same as "not collected".
        JevShadowCandidate[]? alternatives = null;
        if (legalAlternatives is not null)
        {
            alternatives = new JevShadowCandidate[legalAlternatives.Count];
            for (var i = 0; i < legalAlternatives.Count; i++)
            {
                var proposal = legalAlternatives[i];
                alternatives[i] = new JevShadowCandidate(
                    proposal.Goal,
                    proposal.Action.ToString(),
                    proposal.Priority,
                    proposal.PersonalityWeight,
                    "legal; not selected",
                    // v3: THIS is the field that makes a legal loser
                    // joinable to the quest it named. Before v3 a reviewer
                    // could see that an alternative existed and what it
                    // scored, but never which objective it referred to.
                    proposal.TargetId);
            }
        }

        // candidate_count is SELECTOR TRUTH: how many proposals the selector
        // actually weighed = hard rejections + every legal proposal (winner
        // included). The selector's own SelectionCandidateCount is authoritative
        // when the lane collected it, because ActorRequest.DecisionCandidates is
        // stamped by the quest leg as rejections + 1 (QuestBehavior.cs
        // SetPendingDecision) and cannot see a legal loser — trusting it
        // under-reported a live wake that had two legal choices as 1.
        // Falls back to the stamp, then to the computed form, so a row that
        // never selected (or never captured) is left alone rather than guessed:
        //   - no selection ran            -> 0
        //   - selected, nothing collected -> the old rejections+1 behaviour
        var computed = rejections.Count + (alternatives?.Length ?? 0) +
                       ((selectedProposal != null || workSelected) ? 1 : 0);
        var candidateCount = selectionCandidateCount
            ?? (request is { DecisionCandidates: > 0 } stamped
                ? stamped.DecisionCandidates
                : computed);

        var verdict = workSelected
            ? $"landed:{selectedAction}"
            : failStage;

        return new JevShadowRecord(
            SchemaVersion,
            scenario,
            cycleId,
            request?.WakeSequence,
            characterId,
            HashInputs(cycleId, decideDetail),
            decideDetail,
            candidates,
            candidateCount,
            rejections.Count,
            request?.DecisionGoal,
            selectedAction?.ToString(),
            verdict,
            Truncate(string.IsNullOrEmpty(explanation) ? failReason : explanation, MaxReasonChars),
            request?.DecisionPolicy,
            request == null
                ? null
                : new JevShadowRequestRef(
                    request.TraceId.ToString(),
                    request.Action.ToString(),
                    request.State.ToString()),
            completedQuestIds,
            auditRowCount,
            brains?.Travel is { } travel ? JevShadowTravel.FromInputs(travel) : null,
            brains?.Survival is { } survival ? JevShadowSurvival.FromInputs(survival) : null,
            brains?.Needs is { } needs ? JevShadowNeeds.FromInputs(needs) : null,
            brains?.Combat is { } combat ? JevShadowCombat.FromPrepared(combat) : null,
            DateTime.UtcNow,
            // ---- v1 additive scoring surface ----
            // The incumbent's OWN score row. Before v1.1 the selected proposal was
            // invisible here (only rejections carried priority/weight), so a
            // challenger re-ranking the set could not score the option the game
            // brain actually took. Copied from the already-frozen result — never
            // re-selected, never re-scored.
            selectedProposal is { } incumbent
                ? new JevShadowCandidate(
                    incumbent.Goal,
                    incumbent.Action.ToString(),
                    incumbent.Priority,
                    incumbent.PersonalityWeight,
                    "selected",
                    // v3: the incumbent's own target, so the taken pick and
                    // every outranked alternative are joinable on the SAME
                    // identity field.
                    incumbent.TargetId)
                : null,
            brains?.TravelDecision ?? null,
            brains?.SurvivalDecision ?? null,
            brains?.NeedsDecision ?? null,
            alternatives);
    }

    /// <summary>
    /// Emits the NEEDS arbitration wake's row. Same contract as
    /// <see cref="MaybeEmitQuest(uint,string?,string?,QuestDecisionScenario.QuestRunResult,JevShadowBrains?)"/>:
    /// flag read, sampling gate, already-frozen inputs only, never throws. The
    /// needs cycle is its own decision run, so without this entry point the needs
    /// class contributes no rows to the dataset.
    /// </summary>
    public static void MaybeEmitNeeds(uint actorObjId, NeedsDecisionScenario.NeedsRunResult? result)
    {
        if (!Enabled || result == null)
            return;
        var cycleId = $"needs-{actorObjId}-{result.Request?.TraceId:N}";
        var seen = Interlocked.Increment(ref _eligibleWakes);
        if ((seen - 1) % Math.Max(1, SampleEvery) != 0)
            return;
        try
        {
            var line = JsonSerializer.Serialize(
                BuildNeedsRecord(actorObjId, result, TakeBrains(actorObjId)), JsonOptions);
            EnqueueLine(line);
        }
        catch (Exception)
        {
            // Lane-side sink only: a serialization or enqueue fault must never
            // raise into the scheduler tick. (OSError swallow requirement.)
        }
        finally
        {
            // The row was built from the carrier we were handed, so this wake can
            // never capture again: seal the id (which makes it evictable under
            // the cap) so the caller may release the slot. Without this a bot
            // whose wake never emits would pin its slot until its next wake, and
            // the table would grow toward "every actor ever seen" rather than
            // "mid-wake bots".
            EndWake(actorObjId);
            ReportEvictionsIfAny();
        }
    }

    /// <summary>Resets the sampling counter (deterministic sampling tests).</summary>
    internal static void ResetSamplingForTest() => Interlocked.Exchange(ref _eligibleWakes, 0);

    /// <summary>
    /// One built JSONL line plus the lane path captured at enqueue time.
    /// Capturing the path per line keeps a mid-flight <see cref="FilePath"/>
    /// swap from rerouting already-queued lines.
    /// </summary>
    private readonly record struct QueuedLine(string Line, string Path);

    /// <summary>
    /// Enqueues a built JSON line for the background writer. Never blocks,
    /// never throws: when the bounded queue is full the NEWEST line (the
    /// incoming wake) is dropped and counted in <see cref="DroppedLineCount"/>.
    /// </summary>
    private static void EnqueueLine(string line)
    {
        try
        {
            var path = FilePath;
            lock (QueueLock)
            {
                if (_queue.Count >= Math.Max(1, MaxQueuedLines))
                {
                    _droppedLines++;
                    return;
                }
                _queue.Enqueue(new QueuedLine(line, path));
                Interlocked.Increment(ref _pendingLines);
                EnsureWriterLocked();
                Monitor.Pulse(QueueLock);
            }
        }
        catch (Exception)
        {
            // Lane-side sink only: an enqueue fault must never raise into
            // the scheduler tick.
        }
    }

    /// <summary>
    /// Starts the single background writer on first enqueue (caller holds
    /// <see cref="QueueLock"/>); recreates it if it ever died. One-time tick
    /// cost only — steady-state wakes never touch thread startup.
    /// </summary>
    private static void EnsureWriterLocked()
    {
        if (_writerThread is { IsAlive: true })
            return;
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "JevShadowWriter"
        };
        _writerThread.Start();
    }

    /// <summary>
    /// Single background writer: drains queued lines to file in FIFO order,
    /// one <c>AppendAllText</c> per line (no batching — crash-safe prefix,
    /// byte-identical to the old inline write). Every fault is swallowed so a
    /// lane sink fault can never raise anywhere. <c>IsBackground</c>, so the
    /// lane process may exit at any time: queued-but-unwritten lines are
    /// best-effort by design and shutdown is never blocked (no flush join on
    /// any shutdown path — per-line appends leave nothing else to flush).
    /// </summary>
    private static void WriterLoop()
    {
        while (true)
        {
            QueuedLine item;
            lock (QueueLock)
            {
                while (_queue.Count == 0 || _writerPausedForTest)
                    Monitor.Wait(QueueLock);
                item = _queue.Dequeue();
            }
            try
            {
                AppendLine(item);
            }
            catch (Exception)
            {
                // OSError swallow: an unwritable lane path (missing volume,
                // directory-as-file, denied) must never raise out of the
                // writer thread (which would kill it and spam restarts).
            }
            finally
            {
                Interlocked.Decrement(ref _pendingLines);
            }
        }
    }

    private static void AppendLine(QueuedLine item)
    {
        try
        {
            var directory = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(item.Path, item.Line + "\n");
        }
        catch (Exception)
        {
            // OSError swallow: an unwritable lane path (missing volume,
            // directory-as-file, denied) must never raise into the tick.
        }
    }

    /// <summary>
    /// Waits (test only) until every enqueued line has been written or
    /// dropped-fault swallowed. Returns false on timeout instead of throwing.
    /// </summary>
    internal static bool WaitForDrainForTest(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (QueueLock)
            {
                if (_queue.Count == 0 && Volatile.Read(ref _pendingLines) <= 0)
                    return true;
            }
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(5);
        }
    }

    /// <summary>Pauses (true) or resumes the background writer (test only).</summary>
    internal static void SetWriterPausedForTest(bool paused)
    {
        lock (QueueLock)
        {
            _writerPausedForTest = paused;
            Monitor.PulseAll(QueueLock);
        }
    }

    /// <summary>
    /// Resets writer state (test only): unpauses, drains what is drainable
    /// without blocking, then clears leftovers and zeroes drops/pending plus
    /// the sampling counter. Never throws.
    /// </summary>
    internal static void ResetQueueForTest()
    {
        try
        {
            lock (QueueLock)
            {
                _writerPausedForTest = false;
                EnsureWriterLocked();
                Monitor.PulseAll(QueueLock);
            }
            WaitForDrainForTest(TimeSpan.FromSeconds(10));
            lock (QueueLock)
            {
                _queue.Clear();
                _droppedLines = 0;
                _writerPausedForTest = false;
                Monitor.PulseAll(QueueLock);
            }
            Interlocked.Exchange(ref _pendingLines, 0);
            Interlocked.Exchange(ref _eligibleWakes, 0);
        }
        catch (Exception)
        {
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

    // ------------------------------------------------------------------
    // Wake-scoped frozen-input capture (the multi-brain harvest surface).
    //
    // The per-brain <see cref="JevShadowBrains"/> slots are populated from
    // ALREADY-COMPUTED inputs — the same <c>Prepared.Inputs</c> the leg that ran
    // this wake already resolved. Capture is a struct copy into per-actor slots:
    // no Planner is re-run, no Character/world read happens here, and nothing
    // allocates while <see cref="Enabled"/> is false (the capture methods are
    // only ever CALLED from a caller's own <c>if (JevShadow.Enabled)</c> guard,
    // and each body re-checks the flag so a caller that forgets cannot regress
    // the calm-wake budget either).
    // ------------------------------------------------------------------

    /// <summary>
    /// One actor's frozen brain-input slots for the CURRENT wake. A struct with
    /// no managed heap footprint: capture is an overwrite of these fields, never
    /// an append, so a bot's slot is bounded at one wake's worth of inputs.
    /// </summary>
    internal sealed class WakeSlots
    {
        public TravelBrainInputs? Travel;
        public TravelDecision TravelDecision;
        public SurvivalBrainInputs? Survival;
        public SurvivalBrainDecision SurvivalDecision;
        public NeedsBrainInputs? Needs;
        public NeedsBrainDecision NeedsDecision;
        public CombatBrainPlanner.Prepared? Combat;

        /// <summary>No brain froze inputs on the wake in progress.</summary>
        public bool IsEmpty =>
            Travel is null && Survival is null && Needs is null && Combat is null;

        /// <summary>Drops every brain's inputs (the start-of-wake freshness rule).</summary>
        public void Reset()
        {
            Travel = null;
            TravelDecision = default;
            Survival = null;
            SurvivalDecision = default;
            Needs = null;
            NeedsDecision = default;
            Combat = null;
        }
    }


    /// <summary>
    /// Wake-scoped slots, keyed by the actor objId.
    ///
    /// BOUNDING DISCIPLINE — deliberately NOT the full-clear pattern the sibling
    /// stores (<c>TravelIntentStore</c>, <c>SurvivalVetoState</c>,
    /// <c>CombatBrainEngagement</c>) use. Their key IS the wake: an entry lives
    /// exactly as long as its journey/engagement, so clearing the table drops only
    /// DEAD state. This table is keyed per-BOT and must survive across a bot's
    /// own legs (BeginWake resets it, the legs fill it, emit reads it), so a
    /// table-wide <c>Clear()</c> would destroy the in-flight captures of every
    /// OTHER bot mid-wake — silently, with no counter and no log, producing rows
    /// with null brain blocks for no visible reason. At 512 bots (an ordinary
    /// soak size here) that is not a theoretical bound.
    ///
    /// Instead an actor's entry is RELEASED the moment its wake has emitted
    /// (<see cref="EndWake"/>), so the steady-state population tracks bots that
    /// are mid-wake rather than every actor ever seen. The hard cap is a
    /// backstop only: a slot is never evicted while its actor is in the middle of
    /// a wake (a sealed wake is finished by construction), so exceeding the cap
    /// costs a missed row for the NEW actor and is counted, never a corrupt row
    /// for someone else.
    /// </summary>
    private const int WakeSlotMemoryBound = 4096;

    private static readonly ConcurrentDictionary<uint, WakeSlots> WakeInputs = new();

    /// <summary>Actor ids whose wake is sealed (every capture complete, emit pending).</summary>
    private static readonly HashSet<uint> SealedWakes = [];

    /// <summary>Slots refused because the cap was hit while the actor was already mid-wake.</summary>
    private static long _forcedEvictions;

    /// <summary>Highest eviction total already surfaced, so the warning is not spammed per wake.</summary>
    private static long _reportedEvictions;

    /// <summary>
    /// Cap refusals: a capture dropped because the wake-slot cap was reached.
    /// SURFACED: a refusal means that wake's row carries null brain blocks, so a
    /// harvest must be able to tell "the brains had nothing to say" from "the cap
    /// ate the data". Read this before trusting a dataset.
    /// </summary>
    internal static long ForcedEvictionCount => Interlocked.Read(ref _forcedEvictions);

    /// <summary>
    /// The lane-side signal for a harvest operator: a one-line warning when any
    /// capture was refused, and whenever new ones have accrued since the last
    /// call. This is what turns the counter from "nobody reads it" into
    /// something a run can actually notice. Called only on the opt-in harvest
    /// path, never on a disabled wake.
    /// </summary>
    internal static void ReportEvictionsIfAny()
    {
        if (!Enabled)
            return;
        var total = ForcedEvictionCount;
        if (total <= Interlocked.Read(ref _reportedEvictions))
            return;
        Interlocked.Exchange(ref _reportedEvictions, total);
        Logger.Warn("JevShadow: {Count} wake-slot capture(s) refused at the cap " +
                    "(bound {Bound}); those rows have null brain blocks",
            total, WakeSlotMemoryBound);
    }

    /// <summary>Test-only diagnostic: the number of wake slots currently held.</summary>
    internal static int WakeSlotCount => WakeInputs.Count;

    /// <summary>
    /// Test-only diagnostic: how many wake ids are currently sealed (finished,
    /// emit read, awaiting release). A sealed id is the ONLY kind that may be
    /// evicted under the cap, so this counter is what shows whether the table can
    /// actually reclaim space.
    /// </summary>
    internal static int SealedWakeCount
    {
        get { lock (SealedWakes) { return SealedWakes.Count; } }
    }

    /// <summary>
    /// Marks this actor's wake finished: no further capture belongs to it, so
    /// the slot may be released and its id is safe to evict under the cap. Called
    /// at the emit points, which run at the END of the legs that captured.
    /// </summary>
    internal static void EndWake(uint actorObjId)
    {
        if (!Enabled || actorObjId == 0)
            return;
        lock (SealedWakes)
        {
            SealedWakes.Add(actorObjId);
        }
    }

    /// <summary>
    /// Releases one actor's slot AFTER its row has been built. Idempotent, and
    /// a no-op when the actor never captured anything. This is the only place a
    /// live entry leaves the table, which is what makes the population track
    /// mid-wake bots instead of growing to every actor ever seen.
    /// </summary>
    internal static void ReleaseWake(uint actorObjId)
    {
        if (actorObjId == 0)
            return;
        WakeInputs.TryRemove(actorObjId, out _);
        lock (SealedWakes)
        {
            SealedWakes.Remove(actorObjId);
        }
    }

    /// <summary>
    /// Releases the slot of a wake that captured but never emitted (a sampling
    /// gate skip, a run with no decision record, or a wake that returned before
    /// its emit point). Without this, such a bot's slot would be pinned until the
    /// next wake's <see cref="BeginWake"/> — correct, but the release is what
    /// keeps the population proportional to live bots.
    /// </summary>
    internal static void ReleaseWakeIfUnemitted(uint actorObjId)
    {
        if (!Enabled || actorObjId == 0)
            return;
        if (!WakeInputs.ContainsKey(actorObjId))
            return;
        lock (SealedWakes)
        {
            if (!SealedWakes.Contains(actorObjId))
                return;
        }
        ReleaseWake(actorObjId);
    }

    /// <summary>
    /// True when a caller must capture this wake's frozen inputs. Read once by a
    /// caller that already holds the <c>Prepared</c> it would copy; the guard
    /// itself allocates nothing.
    /// </summary>
    internal static bool CaptureEnabled => Enabled;

    /// <summary>
    /// Clears one actor's captured slots. Called at the START of a scheduler wake
    /// (before any leg runs) so a wake on which a brain did NOT run reports that
    /// brain as absent rather than inheriting the previous wake's inputs — the
    /// staleness rule that keeps every emitted row self-contained.
    /// </summary>
    internal static void BeginWake(uint actorObjId)
    {
        if (!Enabled || actorObjId == 0)
            return;

        // A previous wake that was never released (its leg threw, so the
        // end-of-wake release never ran) is dropped HERE, not merely reset.
        // Reset alone would keep the id in the table forever if that wake never
        // recurs, and would keep it permanently un-evictable, because only a
        // SEALED wake may be evicted. Dropping is the reclaim point that makes
        // the exception path self-healing: this actor's wake is provably over,
        // so anything still under its key is stale by definition.
        WakeInputs.TryRemove(actorObjId, out _);
        lock (SealedWakes)
        {
            SealedWakes.Remove(actorObjId);
        }

        Bind(actorObjId);
    }

    /// <summary>
    /// Freezes the travel inputs a leg already resolved this wake. Copies the
    /// caller's own <c>Prepared.Inputs</c> in place — no Planner re-run, no world
    /// read. Each body re-checks <see cref="Enabled"/> so a caller that forgets
    /// its own guard still cannot regress the calm-wake budget.
    /// </summary>
    /// <remarks>
    /// The slot key is <c>inputs.ActorObjId</c> — the SAME value
    /// <see cref="BeginWake"/> and <see cref="TakeBrains"/> use
    /// (<c>actor.ActorId</c>). Every planner stamps <c>ActorObjId: actor.ActorId</c>
    /// (TravelBrainPlanner:169, CombatBrainPlanner:205, NeedsBrainPlanner:293), so
    /// the two keys are identical by construction. A capture taken from a
    /// DIFFERENT actor's prepared value would land in that actor's slot and
    /// produce a cross-actor row; the wake-slot test defends that identity.
    /// </remarks>
    internal static void CaptureTravel(uint actorObjId, in TravelBrainInputs inputs, TravelDecision decision)
    {
        if (!Enabled || actorObjId == 0)
            return;
        if (Bind(actorObjId) is not { } slot)
            return;   // cap refused: counted in ForcedEvictionCount, this wake's row loses the block
        slot.Travel = inputs;
        slot.TravelDecision = decision;
    }

    /// <summary>Freezes the survival inputs the elected-wake gate already resolved.</summary>
    internal static void CaptureSurvival(uint actorObjId, in SurvivalBrainInputs inputs, SurvivalBrainDecision decision)
    {
        if (!Enabled || actorObjId == 0)
            return;
        if (Bind(actorObjId) is not { } slot)
            return;
        slot.Survival = inputs;
        slot.SurvivalDecision = decision;
    }

    /// <summary>Freezes the needs inputs a farm/decision leg already resolved.</summary>
    internal static void CaptureNeeds(uint actorObjId, in NeedsBrainInputs inputs, NeedsBrainDecision decision)
    {
        if (!Enabled || actorObjId == 0)
            return;
        if (Bind(actorObjId) is not { } slot)
            return;
        slot.Needs = inputs;
        slot.NeedsDecision = decision;
    }

    /// <summary>Freezes the combat inputs the rotation arm already prepared.</summary>
    internal static void CaptureCombat(uint actorObjId, in CombatBrainPlanner.Prepared prepared)
    {
        if (!Enabled || actorObjId == 0)
            return;
        if (Bind(actorObjId) is { } slot)
            slot.Combat = prepared;
    }

    /// <summary>
    /// The actor's slots for this wake as the emit-time carrier, or null when the
    /// flag is off / the actor captured nothing. Called ONLY from an
    /// <c>if (JevShadow.Enabled)</c> arm, so allocating the carrier is off the
    /// calm-wake path by construction.
    /// </summary>
    internal static JevShadowBrains? TakeBrains(uint actorObjId)
    {
        if (!Enabled || actorObjId == 0)
            return null;
        if (!WakeInputs.TryGetValue(actorObjId, out var slots) || slots.IsEmpty)
            return null;
        return new JevShadowBrains(
            slots.Travel, slots.Survival, slots.Needs, slots.Combat,
            slots.TravelDecision == default ? null : slots.TravelDecision.ToString(),
            slots.SurvivalDecision == default ? null : slots.SurvivalDecision.ToString(),
            slots.NeedsDecision == default ? null : slots.NeedsDecision.ToString());
    }

    /// <summary>
    /// The actor's slot, or <c>null</c> when the cap is reached and no finished
    /// wake can be evicted in its place.
    ///
    /// NEVER a table-wide clear: that would wipe every OTHER actor's in-flight
    /// captures. When the cap is hit, exactly ONE finished (sealed, emit
    /// already-read) wake is evicted — that entry is dead by construction, so
    /// nothing live is lost. If no such entry exists (more simultaneous
    /// mid-wake actors than the cap), the new actor's capture is REFUSED and
    /// counted: its row loses the brain blocks, but no already-capturing actor
    /// is corrupted, and the counter names the condition.
    /// </summary>
    private static WakeSlots? Bind(uint actorObjId)
    {
        if (WakeInputs.TryGetValue(actorObjId, out var existing))
            return existing;

        if (WakeInputs.Count >= WakeSlotMemoryBound && !TryEvictOneSealed())
        {
            Interlocked.Increment(ref _forcedEvictions);
            return null;
        }

        return WakeInputs.GetOrAdd(actorObjId, static _ => new WakeSlots());
    }

    /// <summary>Evicts exactly one finished wake's slot. False when none is finished.</summary>
    private static bool TryEvictOneSealed()
    {
        uint victim = 0;
        lock (SealedWakes)
        {
            foreach (var id in SealedWakes)
            {
                victim = id;
                break;
            }
            if (victim != 0)
                SealedWakes.Remove(victim);
        }
        return victim != 0 && WakeInputs.TryRemove(victim, out _);
    }

    /// <summary>
    /// TEST SEAM ONLY: drops every captured slot and sealed id. Not on any
    /// runtime path — the live table is bounded by per-wake release plus
    /// single-entry eviction, never by clearing it.
    /// </summary>
    internal static void ClearWakeInputsForTest()
    {
        WakeInputs.Clear();
        lock (SealedWakes)
        {
            SealedWakes.Clear();
        }
    }
}
/// <summary>
/// One frozen shadow-log row (Jev teacher/critic Stage 1 disagreement
/// dataset, schema <c>jev-shadow/v2</c>): scenario/wake ids, a frozen-inputs
/// hash plus the frozen decide-detail ref, the brain's candidates + scores,
/// the brain decision + verdict/reason, and outcome-delta refs. Every field
/// is copied from the wake's already-frozen buffers (QuestRunResult +
/// QuestDecideDetail); nothing here is a live read and nothing here drives
/// runtime behavior. Serialized as one JSONL line (snake_case).
///
/// Since v2 the row also carries <see cref="LegalAlternatives"/>, and
/// <c>candidate_count</c> is SELECTOR TRUTH (hard rejections + every legal
/// proposal, the winner included) rather than the v1 rejections+1 stamp. Rows
/// written before the cutover are v1 and are NOT relabelled.
///
/// The four optional brain snapshots (<see cref="Travel"/>,
/// <see cref="Survival"/>, <see cref="Needs"/>, <see cref="Combat"/>) carry
/// THIS WAKE's already-computed per-brain frozen Decide inputs (null when that
/// brain did not run), so the offline replay for those brains can be fed from
/// the lane. They are populated from the wake-scoped capture
/// (<see cref="BeginWake"/> + the <c>Capture*</c> methods), NOT from the quest
/// leg's own run — a brain that ran on a different leg of the same wake (the
/// survival gate, the needs farm leg, the combat rotation arm) still appears.
///
/// <see cref="SelectedCandidate"/> is the INCUMBENT's own score row: the
/// selected proposal's goal/action/priority/weight, copied from the frozen
/// result. <see cref="Candidates"/> holds every REJECTED proposal. Together
/// they are the complete candidate set scored on identical fields, which is
/// what makes a challenger re-rank comparable to the incumbent.
///
/// All of the above are ADDITIVE v1 fields: they are appended after the v1
/// positional parameters with defaults, so an old reader ignores them and an
/// old row simply reads as absent. No existing field name or meaning changed.
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
    DateTime EmittedAtUtc = default,
    // ---- v1 ADDITIVE (backward-compatible): the scoring surface ----
    //
    // Every field below is OPTIONAL and appended after the v1 positional
    // parameters, so an old reader deserializing the row ignores them and a new
    // reader sees them as absent-on-parse when reading an old row. No existing
    // field name or meaning changed.
    //
    // SelectedCandidate is THE INCUMBENT'S OWN SCORE ROW. Before v1.1 only the
    // non-selected candidates carried priority/weight (they were built from the
    // rejection list), which made the incumbent invisible to a challenger
    // re-ranking the same candidate set: it could not score the option the game
    // brain actually took. It is the same candidate SET — the selected proposal
    // the selector returned, copied from the already-frozen result — so a
    // challenger and the incumbent are scored on identical fields.
    JevShadowCandidate? SelectedCandidate = null,
    // The per-brain verdicts, copied from each brain's already-computed decision
    // (never re-run). Lets the replay join inputs → verdict per brain.
    string? TravelDecision = null,
    string? SurvivalDecision = null,
    string? NeedsDecision = null,
    // v1 ADDITIVE: the proposals that were LEGAL this wake but LOST the
    // selector's preference order. Kept in their own array, never merged into
    // Candidates, so a reader can tell the three kinds apart without inference:
    //   Candidates         -> hard-precondition REJECTIONS (illegal this wake)
    //   LegalAlternatives  -> legal, passed every gate, not chosen
    //   SelectedCandidate  -> the incumbent the game actually took
    // Null when the shadow lane did not collect them (flag off), so absent
    // never reads as "there were none". Appended last, so an old reader that
    // ignores unknown fields is unaffected.
    IReadOnlyList<JevShadowCandidate>? LegalAlternatives = null);

/// <summary>
/// Carrier for one wake's optional per-brain frozen Decide inputs. Each slot
/// holds the already-computed inputs object the leg ran this wake (a
/// <c>Prepared</c>'s <c>Inputs</c>, or a combat <c>Prepared</c>); null when
/// that brain did not run. The shadow surface copies by value/ref-copy only —
/// never a fresh world read, never a new Planner run. The trailing decision
/// tokens are additive v1 fields: already-computed verdicts copied as
/// space-free strings so the replay can join inputs → verdict per brain.
/// </summary>
public sealed record JevShadowBrains(
    TravelBrainInputs? Travel = null,
    SurvivalBrainInputs? Survival = null,
    NeedsBrainInputs? Needs = null,
    CombatBrainPlanner.Prepared? Combat = null,
    // v1 ADDITIVE: the per-brain verdicts, already computed by the leg that ran
    // this wake and copied as space-free tokens. Appending optional parameters
    // keeps every existing 4-slot construction (and its positional order) valid.
    string? TravelDecision = null,
    string? SurvivalDecision = null,
    string? NeedsDecision = null);

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
/// One candidate with its deterministic score (priority + personality
/// weight), its rejection reason, and — since <c>jev-shadow/v3</c> — the
/// proposal's own <c>TargetId</c>, copied from the frozen proposal and never
/// re-evaluated.
///
/// The target is copied verbatim, so its MEANING is whatever the emitting
/// proposal used it for: the quest id for the quest goals, an in-world object
/// id for a Target/Move proposal. Read it together with <see cref="Action"/>,
/// never on its own. <c>0</c> means the proposal named no target, which is a
/// real recorded value and not a missing field — that is exactly why this is a
/// non-nullable <c>uint</c> with NO default: every construction states its own
/// target explicitly rather than silently inheriting zero.
/// </summary>
public sealed record JevShadowCandidate(
    string Goal,
    string Action,
    int Priority,
    int Weight,
    string Reason,
    uint TargetId);

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
