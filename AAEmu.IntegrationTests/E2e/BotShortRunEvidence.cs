using System.Text.Json;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Consumes passive server observations, never supplies gameplay. Structured
/// decision stamps prove quest dispatch; poll-local server changes prove effects.
/// Explicit request parents and copied intervals prove purpose and effects.
/// </summary>
internal sealed class BotShortRunEvidence
{
    internal const int MaximumAuditRows = 512;
    internal const int MaximumWorkWitnesses = 128;
    internal const int MaximumRecoveryResponses = 3;
    internal const double MaximumSeconds = 900;
    internal const double NoProgressSeconds = 300;

    internal sealed record Work(double T, Guid TraceId, uint QuestId, string Action,
        DateTime RequestedAt, DateTime CompletedAt, string DecisionGoal, string Policy, string Cycle);
    internal sealed record InterruptCandidate(uint QuestId, Guid VictimTraceId,
        DateTime RequestedAt, DateTime ObservedAt, double? RouteX, double? RouteY,
        string MoveOwner = "QUEST_TRAVEL", uint TargetId = 0);
    internal sealed record Recovery(uint QuestId, Guid? ControlTraceId, Guid? VictimTraceId,
        Guid ResponseTraceId, Guid ConsequenceTraceId, string Scope);
    private sealed record QuestState(string Step, string Status);
    private sealed record Snapshot(DateTime At, uint Chosen, bool? Dead,
        Dictionary<uint, QuestState> Quests, Dictionary<uint, bool> Completion);
    private sealed record LiveBefore(Guid TraceId, string Action, uint TargetId, DateTime RequestedAt,
        Snapshot Before, JsonElement Observation);

    private readonly uint _characterId;
    private readonly DateTime _start;
    private readonly long _generation;
    private readonly bool _protocolReady;
    private uint _actorId;
    private DateTime? _joinedAt;
    private Snapshot? _previous;
    private bool _liveBeforeCompetingEffect;
    private LiveBefore? _liveBefore;
    private DateTime? _deathAt;
    private uint _deathGoal;
    private Work? _controlledConsequence;
    private InterruptCandidate? _stimulus;
    private DateTime? _declaredAt;
    private DateTime? _controlRequestedAt;
    private DateTime? _controlCompletedAt;
    private DateTime? _victimCompletedAt;
    private bool _victimOwnerVerified;
    private bool _queueTrue;
    private Guid? _controlTrace;
    private Guid? _freshRequest;
    private DateTime? _freshRequestedAt;
    private bool _freshRequestOwnerVerified;
    private bool _recoveryScopeLost;
    private DateTime? _retiredResponseAt;
    private readonly HashSet<Guid> _recoveryResponseIds = [];
    private readonly HashSet<Guid> _retiredResponseIds = [];
    private readonly HashSet<string> _recoveryResponseCycles = [];

    private JsonElement? _previousObservation;
    private readonly Dictionary<Guid, JsonElement> _running = [];
    private JsonElement? _victimPurpose;
    public JsonElement? LastObservation { get; private set; }
    public bool PurposeProved => EvidenceFault == null && _protocolReady && _joinedAt.HasValue
        && IdentityMismatches == 0 && _stimulus != null && QuestWork.Any(w => w.Action == "Loot" && w.QuestId == _stimulus.QuestId)
        && Recoveries.Count == 1 && InterruptDeclarations == 1 && _queueTrue && _victimOwnerVerified
        && _freshRequestOwnerVerified && !_recoveryScopeLost
        && Recoveries[0].ControlTraceId == _controlTrace && Recoveries[0].VictimTraceId == _stimulus?.VictimTraceId;

    public List<Work> QuestWork { get; } = [];
    public List<Work> CompletedGoals { get; } = [];
    public List<Work> SubsequentChoices { get; } = [];
    public List<Recovery> Recoveries { get; } = [];
    public uint ActorId => _actorId;
    public long Generation => _generation;
    public DateTime? JoinedAt => _joinedAt;
    public int IdentityMismatches { get; private set; }
    public int InterruptDeclarations { get; private set; }
    public int ProvedProgressVersion { get; private set; }
    public double LastProvedProgressSeconds { get; private set; }
    public string? EvidenceFault { get; private set; }
    public int BaselineCompletedCount { get; }
    public int LastCompletedCount { get; private set; }
    public bool ControlCompleted => _controlCompletedAt.HasValue;
    public Guid? ControlTraceId => _controlTrace;

    public BotShortRunEvidence(uint characterId, JsonElement baseline, bool protocolReady = false)
    {
        _characterId = characterId;
        _start = Time(baseline, "observedAtUtc") ?? DateTime.MaxValue;
        _generation = Long(baseline, "enrollmentGeneration");
        _protocolReady = protocolReady;
        BaselineCompletedCount = (int)Math.Max(0, Long(baseline, "completedCount"));
        LastCompletedCount = BaselineCompletedCount;
        LastObservation = baseline.Clone();
        if (_start == DateTime.MaxValue || Time(baseline, "snapshotStartedAtUtc") == null
            || Time(baseline, "snapshotStartedAtUtc") > _start || UInt(baseline, "id") != characterId || _generation < 0)
            EvidenceFault = "START identity/time unavailable";
        else if (!HasProofArrays(baseline))
            EvidenceFault = "START passive proof arrays are unavailable";
        else if (UInt(baseline, "actorObjId") != 0)
            Join(baseline, _start);
    }

    public string? StopReason(double elapsedSeconds)
    {
        if (EvidenceFault != null) return "EVIDENCE_FAULT";
        if (PurposeProved) return "QUEST_PURPOSE_COMPLETE";
        if (elapsedSeconds >= MaximumSeconds) return "MAXIMUM_WINDOW";
        if (elapsedSeconds - LastProvedProgressSeconds >= NoProgressSeconds) return "NO_PROVED_PROGRESS";
        return null;
    }

    public double RemainingSeconds(double elapsedSeconds)
        => Math.Max(0, Math.Min(MaximumSeconds - elapsedSeconds,
            NoProgressSeconds - (elapsedSeconds - LastProvedProgressSeconds)));

    public void BreakContinuity()
    {
        _previous = null;
        _previousObservation = null;
        _running.Clear();
        _liveBefore = null;
        _deathAt = null;
        if (_stimulus != null) _recoveryScopeLost = true;
    }

    public void Observe(double t, JsonElement observation)
    {
        if (EvidenceFault != null) return; // FIRST fault freezes the autonomous proof.
        var begin = Time(observation, "snapshotStartedAtUtc");
        var copiedEnd = Time(observation, "observedAtUtc");
        if (begin == null || copiedEnd == null || begin > copiedEnd
            || (_previousObservation.HasValue && begin <= Time(_previousObservation.Value, "observedAtUtc")))
        {
            EvidenceFault ??= "Copied snapshot intervals are missing or unordered";
            BreakContinuity();
            return;
        }
        if (!HasProofArrays(observation))
        {
            EvidenceFault ??= "Passive proof arrays malformed or ambiguous";
            BreakContinuity();
            return;
        }
        LastObservation = observation.Clone();
        if (_previousObservation.HasValue)
        {
            var priorRows = Array(_previousObservation.Value, "actorAudit").ToArray();
            var rows = Array(observation, "actorAudit").ToArray();
            if ((priorRows.Length > 0 && !rows.Any(r => Id(r, "trace_id") == Id(priorRows[^1], "trace_id")
                    && JsonElement.DeepEquals(r, priorRows[^1])))
                || priorRows.Any(p => rows.Any(r => Id(r, "trace_id") == Id(p, "trace_id")
                    && !JsonElement.DeepEquals(r, p))))
            {
                EvidenceFault ??= "Actor audit tail overlap lost";
                BreakContinuity();
                return;
            }
        }
        var at = Time(observation, "observedAtUtc");
        if (at == null || at < _start || UInt(observation, "id") != _characterId
            || Long(observation, "enrollmentGeneration") != _generation)
        {
            RejectIdentity();
            return;
        }
        if (!_joinedAt.HasValue)
        {
            // A START projection can precede actor construction. Joining never
            // changes START or either monotonic deadline, and excludes earlier work.
            if (UInt(observation, "actorObjId") == 0) return;
            Join(observation, at.Value);
            return;
        }
        if (UInt(observation, "actorCharId") != _characterId
            || UInt(observation, "actorObjId") != _actorId)
        {
            RejectIdentity();
            return;
        }
        var current = ReadSnapshot(observation, at.Value);
        if (_previous == null)
        {
            _previous = current;
            _previousObservation = observation.Clone();
            return;
        }
        if (at <= _previous.At)
        {
            EvidenceFault ??= "Passive observation timestamps are not increasing";
            BreakContinuity();
            return;
        }
        var audit = observation.GetProperty("actorAudit");
        if (audit.GetArrayLength() > MaximumAuditRows)
        {
            EvidenceFault ??= "Actor audit exceeds its declared 512-row bound";
            return;
        }
        var previousBegin = Time(_previousObservation!.Value, "snapshotStartedAtUtc");
        if (audit.EnumerateArray().Any(r => UInt(r, "actor_id") == _actorId
            && MayChangeQuestOrItems(String(r, "action"))
            && Time(r, "completed_at") is { } completed
            && ((completed > begin && completed <= at)
                || (completed > previousBegin && completed <= _previous.At))))
        {
            EvidenceFault ??= "Terminal intersects copied snapshot interval";
            BreakContinuity();
            return;
        }
        // Rows naming an observed recovery request cannot disappear through the
        // general foreign/malformed-row filter and leave a Running witness valid.
        var recoveryTerminals = new HashSet<Guid>();
        foreach (var row in audit.EnumerateArray())
        {
            var id = Id(row, "trace_id");
            if (id == null || _stimulus == null
                || id != _stimulus.VictimTraceId && !_recoveryResponseIds.Contains(id.Value))
                continue;
            if (!recoveryTerminals.Add(id.Value) || !OwnedTerminal(row, copiedEnd.Value)
                || String(row, "result") is not ("Completed" or "Interrupted" or "TimedOut" or "Rejected")
                || (String(row, "result") is "Completed" or "Interrupted")
                    && row.TryGetProperty("failure", out var failure) && failure.ValueKind != JsonValueKind.Null
                || !_running.TryGetValue(id.Value, out var purpose) || !SamePurpose(purpose, row))
            {
                EvidenceFault ??= "Recovery request terminal is malformed or foreign";
                BreakContinuity();
                return;
            }
            // A failed movement ending during a copied interval cannot provide
            // an unambiguous retirement boundary for the replacement chain.
            if (id == _freshRequest && String(row, "result") == "TimedOut"
                && Time(row, "completed_at") > begin && Time(row, "completed_at") <= copiedEnd)
                _recoveryScopeLost = true;
        }
        var fresh = audit.EnumerateArray().Where(r => OwnedTerminal(r, begin.Value)
                && Time(r, "completed_at") > _previous.At)
            .GroupBy(r => Id(r, "trace_id")).Where(g => g.Count() == 1).Select(g => g.Single())
            .OrderBy(r => Time(r, "completed_at")).ToArray();
        if (fresh.Any(r => Id(r, "trace_id") is { } trace
            && _running.TryGetValue(trace, out var running) && !SamePurpose(running, r)))
        {
            EvidenceFault ??= "Running-to-terminal immutable request tuple changed";
            BreakContinuity();
            return;
        }
        foreach (var row in fresh) ObserveMoveTerminal(row);
        if (_liveBefore != null && fresh.Any(r => Id(r, "trace_id") != _liveBefore.TraceId
            && String(r, "result") == "Completed" && MayChangeQuestOrItems(String(r, "action"))))
            _liveBeforeCompetingEffect = true;
        var firstNewWork = QuestWork.Count;

        // Consume a terminal only in the interval where it first completed.
        // Retaining a request until an unrelated future state changes is forbidden.
        foreach (var row in fresh)
        {
            var action = String(row, "action");
            var quest = UInt(row, "decision_quest_id");
            var requested = Time(row, "requested_at")!.Value;
            if (String(row, "result") != "Completed" || quest == 0 || !DecisionMatches(row, action)
                || (action == "Loot" ? UInt(row, "target_id") == 0
                    : (action is "AcceptQuest" or "AdvanceQuest") && UInt(row, "target_id") != quest))
                continue;
            var before = requested >= _previous.At ? _previous
                : _liveBefore?.TraceId == Id(row, "trace_id") && _liveBefore.Action == action
                    && _liveBefore.TargetId == UInt(row, "target_id") && _liveBefore.RequestedAt == requested ? _liveBefore.Before : null;
            if (before == null || requested < before.At
                || fresh.Count(r => String(r, "result") == "Completed"
                    && (String(r, "action") == "Loot" || UInt(r, "decision_quest_id") == quest)
                    && IsQuestAction(String(r, "action"))) != 1
                || fresh.Count(r => String(r, "result") == "Completed"
                    && MayChangeQuestOrItems(String(r, "action"))) != 1
                || (requested < _previous.At && _liveBeforeCompetingEffect))
                continue;
            var effect = action switch
            {
                "Loot" => _previousObservation.HasValue && LootEffect(
                    requested >= _previous.At ? _previousObservation.Value : _liveBefore!.Observation, observation, quest),
                "AcceptQuest" => !before.Quests.ContainsKey(quest) && current.Quests.ContainsKey(quest),
                "AdvanceQuest" => before.Quests.TryGetValue(quest, out var oldQuest)
                    && oldQuest.Step.Length > 0 && oldQuest.Status.Length > 0
                    && ((current.Quests.TryGetValue(quest, out var newQuest)
                        && newQuest.Step.Length > 0 && newQuest.Status.Length > 0 && oldQuest != newQuest)
                        || FreshCompletion(before, current, quest)),
                "TurnInQuest" or "TurnInDoodad" or "AutoTurnIn" => FreshCompletion(before, current, quest),
                _ => false,
            };
            if (!effect) continue;
            if (QuestWork.Count >= MaximumWorkWitnesses)
            {
                EvidenceFault ??= "Quest work exceeds the declared 128-witness bound";
                return;
            }
            var work = new Work(t, Id(row, "trace_id")!.Value, quest, action, requested,
                Time(row, "completed_at")!.Value, String(row, "decision_goal"),
                String(row, "decision_policy"), String(row, "decision_cycle_id"));
            QuestWork.Add(work);
            // A real fresh autonomous quest effect can replace the interrupted
            // route without issuing another Move. Missing route fields are not retirement.
            if (!_freshRequest.HasValue && _recoveryResponseIds.Count == 0
                && _stimulus != null && !_recoveryScopeLost && _victimOwnerVerified
                && IsQuestAction(work.Action)
                && (_stimulus.MoveOwner != "PURSUIT_MOVE_TO_UNIT" || work.QuestId == _stimulus.QuestId)
                && work.RequestedAt > _victimCompletedAt && work.TraceId != _stimulus.VictimTraceId
                && ExplicitNull(observation, "questTravelTargetX") && ExplicitNull(observation, "questTravelTargetY"))
            {
                _freshRequest = work.TraceId;
                _freshRequestedAt = work.RequestedAt;
                _freshRequestOwnerVerified = true;
            }
            if (IsQuestAction(action))
            {
                _controlledConsequence = work;
                TryControlledRecovery(work);
            }
            ProvedProgressVersion++;
            LastProvedProgressSeconds = t;
            if (action is "TurnInQuest" or "TurnInDoodad" or "AutoTurnIn")
            {
                CompletedGoals.Add(work);
                TryRecover(work, current);
            }
        }
        // A next choice is an independently admitted fresh acceptance, not a
        // sticky chosen field or an old request reinterpreted after completion.
        for (var index = firstNewWork; index < QuestWork.Count; index++)
        {
            var work = QuestWork[index];
            if (work.Action == "AcceptQuest" && Recoveries.Any(r => r.QuestId != work.QuestId
                && CompletedGoals.Any(c => c.TraceId == r.ConsequenceTraceId && c.CompletedAt < work.RequestedAt)))
                SubsequentChoices.Add(work);
        }

        if (_previous.Dead == false && current.Dead == true && current.Chosen != 0
            && QuestWork.Any(w => w.QuestId == current.Chosen && w.Action == "AcceptQuest"))
        {
            _deathAt = current.At;
            _deathGoal = current.Chosen;
        }
        if (_deathAt.HasValue && current.Chosen != _deathGoal) _deathAt = null;
        ObserveLive(observation, current, fresh);
        var total = Long(observation, "completedCount");
        if (total >= 0) LastCompletedCount = (int)total; // diagnostic, never progress
        _previous = current;
        _previousObservation = observation.Clone();
    }

    public InterruptCandidate? GetInterruptCandidate(JsonElement observation)
    {
        if (_stimulus != null || EvidenceFault != null || _previous == null
            || UInt(observation, "actorObjId") != _actorId || UInt(observation, "actorCharId") != _characterId
            || Time(observation, "observedAtUtc") != _previous.At
            || String(observation, "liveAction") != "Move" || String(observation, "liveState") != "Running"
            || !IsQualifiedQuestMove(observation)
            || String(observation, "liveDecisionPolicy") != "quest-v1"
            || string.IsNullOrWhiteSpace(String(observation, "liveDecisionCycleId"))
            || UInt(observation, "liveDecisionQuestId") == 0
            || UInt(observation, "liveDecisionQuestId") != UInt(observation, "chosenQuestId")) return null;
        if (!TryReadGatherState(observation, UInt(observation, "liveDecisionQuestId"), out var active, out _, out _)
            || String(active, "status") != "Progress" || String(active, "step") != "Progress") return null;
        var id = Id(observation, "liveTraceId");
        var requested = Time(observation, "liveRequestedAtUtc");
        var x = Number(observation, "liveDestinationX");
        var y = Number(observation, "liveDestinationY");
        if (!id.HasValue || !requested.HasValue || requested < _joinedAt || requested > _previous.At
            || requested > Time(observation, "snapshotStartedAtUtc")
            || !_running.ContainsKey(id.Value)) return null;
        return new(UInt(observation, "liveDecisionQuestId"), id.Value, requested.Value, _previous.At, x, y,
            String(observation, "liveMoveOwner"), UInt(observation, "liveTargetId"));
    }

    // Point travel and unit-relative pursuit are distinct production request
    // shapes. A pursuit target has no immutable point destination to invent.
    private static bool IsQualifiedQuestMove(JsonElement live)
        => String(live, "liveAction") == "Move" && (
            String(live, "liveMoveOwner") == "QUEST_TRAVEL"
                && String(live, "liveDecisionGoal") == "quest.progress"
                && Number(live, "liveDestinationX").HasValue
                && Number(live, "liveDestinationY").HasValue
                && Number(live, "liveDestinationZ").HasValue
            || String(live, "liveMoveOwner") == "PURSUIT_MOVE_TO_UNIT"
                && String(live, "liveDecisionGoal") == "quest.objective-pursuit"
                && UInt(live, "liveTargetId") != 0);

    public void DeclareInterrupt(InterruptCandidate candidate, DateTime declaredAt)
    {
        if (_stimulus != null || !LastObservation.HasValue
            || candidate != GetInterruptCandidate(LastObservation.Value) || declaredAt < candidate.ObservedAt)
            throw new InvalidOperationException("One current own parented Running Move is required");
        _victimPurpose = LastObservation.Value.Clone();
        _stimulus = candidate;
        _declaredAt = declaredAt;
        InterruptDeclarations++;
    }

    public void AcknowledgeInterrupt(Guid controlTrace)
    {
        if (_stimulus == null || controlTrace == Guid.Empty || controlTrace == _stimulus.VictimTraceId || _controlTrace.HasValue)
            throw new InvalidOperationException("Interrupt acknowledgment does not match the single declaration");
        _controlTrace = controlTrace;
    }

    public void ObserveControl(JsonElement result)
    {
        if (EvidenceFault != null) return;
        if (_stimulus == null || Id(result, "trace_id") != _controlTrace
            || UInt(result, "actor_id") != _actorId || String(result, "action") != "Interrupt")
        {
            EvidenceFault ??= "Interrupt control identity/action mismatch";
            return;
        }
        var state = String(result, "state");
        if (state is "Requested" or "Accepted" or "Running") return;
        _controlRequestedAt = Time(result, "requested_at");
        _controlCompletedAt = Time(result, "completed_at");
        _queueTrue = state == "Completed" && Bool(result, "result_payload") == true
            && _controlRequestedAt >= _declaredAt && _controlCompletedAt >= _controlRequestedAt;
        if (!_queueTrue) EvidenceFault ??= "Declared interruption did not complete with queued true";
        // This is an already-proved, request-local consequence, never an old
        // request waiting for a future state change. Control delivery can be
        // observed after the passive consequence without changing its proof.
        if (_controlledConsequence != null) TryControlledRecovery(_controlledConsequence);
    }

    private void Join(JsonElement observation, DateTime at)
    {
        if (UInt(observation, "actorCharId") != _characterId)
        {
            RejectIdentity();
            return;
        }
        _actorId = UInt(observation, "actorObjId");
        _joinedAt = at;
        _previousObservation = observation.Clone();
        _previous = ReadSnapshot(observation, at);
    }

    private void RejectIdentity()
    {
        IdentityMismatches++;
        EvidenceFault ??= "Owned actor/enrollment identity changed or is unavailable";
        BreakContinuity();
    }

    private bool OwnedTerminal(JsonElement row, DateTime observedAt)
    {
        var requested = Time(row, "requested_at");
        var completed = Time(row, "completed_at");
        return Id(row, "trace_id") != null && UInt(row, "actor_id") == _actorId
            && requested >= _joinedAt && completed >= requested && completed <= observedAt;
    }

    private bool DecisionMatches(JsonElement row, string action)
    {
        var goal = action switch
        {
            "AcceptQuest" => "quest.accept",
            "AdvanceQuest" => "quest.advance",
            "Loot" => "quest.objective-loot",
            "Move" => "quest.progress",
            "TurnInQuest" or "TurnInDoodad" or "AutoTurnIn" => "quest.turn-in",
            _ => null,
        };
        var cycle = String(row, "decision_cycle_id");
        var trace = Id(row, "trace_id");
        if (goal == null || UInt(row, "decision_quest_id") == 0
            || String(row, "decision_goal") != goal || String(row, "decision_policy") != "quest-v1"
            || string.IsNullOrWhiteSpace(cycle) || trace == null) return false;
        return !_running.TryGetValue(trace.Value, out var running) || SamePurpose(running, row);
    }

    private static bool SamePurpose(JsonElement live, JsonElement row)
        => UInt(live, "liveDecisionQuestId") == UInt(row, "decision_quest_id")
            && String(live, "liveDecisionGoal") == String(row, "decision_goal")
            && String(live, "liveDecisionPolicy") == String(row, "decision_policy")
            && String(live, "liveDecisionCycleId") == String(row, "decision_cycle_id")
            && Time(live, "liveRequestedAtUtc") == Time(row, "requested_at")
            && String(live, "liveAction") == String(row, "action")
            && (String(live, "liveAction") != "Move"
                || String(live, "liveMoveOwner") == String(row, "move_owner"))
            && UInt(live, "liveTargetId") == UInt(row, "target_id")
            && Id(live, "liveTraceId") == Id(row, "trace_id");

    private static bool LootEffect(JsonElement before, JsonElement after, uint quest)
    {
        foreach (var name in new[] { "items", "objectiveItems", "activeQuests" })
            if (!before.TryGetProperty(name, out var oldArray) || oldArray.ValueKind != JsonValueKind.Array
                || !after.TryGetProperty(name, out var newArray) || newArray.ValueKind != JsonValueKind.Array) return false;
        if (!TryReadGatherState(before, quest, out _, out var mappings, out var oldObjectives)
            || !TryReadGatherState(after, quest, out _, out var later, out var newObjectives)
            || mappings.Length != later.Length) return false;
        var gained = false;
        foreach (var map in mappings)
        {
            var index = Long(map, "objectiveIndex");
            var item = UInt(map, "itemId");
            if (!later.Any(m => UInt(m, "actId") == UInt(map, "actId")
                    && UInt(m, "componentId") == UInt(map, "componentId") && Long(m, "objectiveIndex") == index
                    && UInt(m, "itemId") == item && Long(m, "required") == Long(map, "required"))) return false;
            if (quest == 251 && (UInt(map, "actId") != 10473 || UInt(map, "componentId") != 633
                || index != 0 || item != 4058 || Long(map, "required") != 3)) return false;
            var oldValue = oldObjectives[index].GetInt64();
            var newValue = newObjectives[index].GetInt64();
            if (newValue > Long(map, "required")) return false;
            var oldItems = Array(before, "items").Where(i => UInt(i, "template") == item).ToArray();
            var newItems = Array(after, "items").Where(i => UInt(i, "template") == item).ToArray();
            if (oldItems.Any(i => Long(i, "count") < 0) || newItems.Any(i => Long(i, "count") < 0)) return false;
            gained |= newValue > oldValue && newItems.Sum(i => Long(i, "count")) > oldItems.Sum(i => Long(i, "count"));
        }
        return gained;
    }

    private static bool TryReadGatherState(JsonElement observation, uint quest, out JsonElement active,
        out JsonElement[] mappings, out JsonElement[] objectives)
    {
        active = default;
        mappings = [];
        objectives = [];
        if (quest == 0 || !observation.TryGetProperty("activeQuests", out var quests) || quests.ValueKind != JsonValueKind.Array
            || !observation.TryGetProperty("objectiveItems", out var links) || links.ValueKind != JsonValueKind.Array) return false;
        foreach (var row in quests.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) return false;
            if (UInt(row, "questId") != quest) continue;
            if (active.ValueKind != JsonValueKind.Undefined) return false;
            active = row;
        }
        if (active.ValueKind != JsonValueKind.Object || !active.TryGetProperty("objectives", out var counters)
            || counters.ValueKind != JsonValueKind.Array) return false;
        objectives = counters.EnumerateArray().ToArray();
        if (objectives.Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var value) || value < 0)
            || links.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.Object)) return false;
        mappings = links.EnumerateArray().Where(m => UInt(m, "questId") == quest).ToArray();
        if (mappings.Length == 0) return false;
        var indices = new HashSet<long>(mappings.Length);
        var items = new HashSet<uint>(mappings.Length);
        foreach (var map in mappings)
        {
            var index = Long(map, "objectiveIndex");
            var item = UInt(map, "itemId");
            if (index < 0 || index >= objectives.Length || !indices.Add(index) || item == 0 || !items.Add(item)
                || UInt(map, "actId") == 0 || UInt(map, "componentId") == 0 || Long(map, "required") <= 0) return false;
        }
        return true;
    }



    private void ObserveLive(JsonElement observation, Snapshot current, JsonElement[] fresh)
    {
        var id = Id(observation, "liveTraceId");
        var requested = Time(observation, "liveRequestedAtUtc");
        if (id.HasValue && requested.HasValue && String(observation, "liveState") == "Running")
        {
            if (_running.TryGetValue(id.Value, out var earlier)
                && (UInt(earlier, "liveDecisionQuestId") != UInt(observation, "liveDecisionQuestId")
                    || String(earlier, "liveDecisionCycleId") != String(observation, "liveDecisionCycleId")
                    || String(earlier, "liveDecisionGoal") != String(observation, "liveDecisionGoal")
                    || String(earlier, "liveDecisionPolicy") != String(observation, "liveDecisionPolicy")
                    || String(earlier, "liveAction") != String(observation, "liveAction")
                    || String(observation, "liveAction") == "Move"
                        && String(earlier, "liveMoveOwner") != String(observation, "liveMoveOwner")
                    || UInt(earlier, "liveTargetId") != UInt(observation, "liveTargetId")
                    || Time(earlier, "liveRequestedAtUtc") != requested))
                EvidenceFault ??= "Running request purpose changed";
            else if (_running.Count >= MaximumAuditRows && !_running.ContainsKey(id.Value))
                EvidenceFault ??= "Running identity memory exceeds bound";
            else _running.TryAdd(id.Value, JsonSerializer.SerializeToElement(new
            {
                liveTraceId = id, liveRequestedAtUtc = requested, liveAction = String(observation, "liveAction"),
                liveMoveOwner = String(observation, "liveMoveOwner"),
                liveTargetId = UInt(observation, "liveTargetId"), liveDecisionQuestId = UInt(observation, "liveDecisionQuestId"),
                liveDecisionGoal = String(observation, "liveDecisionGoal"),
                liveDecisionPolicy = String(observation, "liveDecisionPolicy"),
                liveDecisionCycleId = String(observation, "liveDecisionCycleId")
            }));
        }
        var running = String(observation, "liveState") is "Requested" or "Accepted" or "Running";
        if (id != null && requested >= _previous!.At && requested <= current.At && running)
        {
            _liveBefore = new(id.Value, String(observation, "liveAction"), UInt(observation, "liveTargetId"),
                requested.Value, _previous, _previousObservation!.Value);
            _liveBeforeCompetingEffect = fresh.Any(r => Id(r, "trace_id") != id
                && String(r, "result") == "Completed" && MayChangeQuestOrItems(String(r, "action")));
        }
        else if (id != _liveBefore?.TraceId || !running)
            _liveBefore = null;
        if (id.HasValue && running && _retiredResponseIds.Contains(id.Value))
        {
            EvidenceFault ??= "Retired recovery response resumed";
            _recoveryScopeLost = true;
            return;
        }
        if (_stimulus != null && _victimCompletedAt.HasValue && id == _stimulus.VictimTraceId && running)
            EvidenceFault ??= "Interrupted victim resumed";
        if (_stimulus == null || _recoveryScopeLost || _freshRequest.HasValue || !_victimCompletedAt.HasValue
            || !IsQualifiedQuestMove(observation)
            || String(observation, "liveState") != "Running" || id == null || id == _stimulus.VictimTraceId
            || requested <= (_retiredResponseAt ?? _victimCompletedAt) || requested > current.At
            || UInt(observation, "liveDecisionQuestId") == 0
            || _stimulus.MoveOwner == "PURSUIT_MOVE_TO_UNIT"
                && UInt(observation, "liveDecisionQuestId") != _stimulus.QuestId
            || UInt(observation, "liveDecisionQuestId") != current.Chosen
            || String(observation, "liveDecisionPolicy") != "quest-v1"
            || string.IsNullOrWhiteSpace(String(observation, "liveDecisionCycleId"))
            || String(observation, "liveDecisionCycleId") == String(_victimPurpose!.Value, "liveDecisionCycleId"))
            return;
        var cycle = String(observation, "liveDecisionCycleId");
        // Failed responses remain failed. Only a separately observed, new request
        // after their exact terminal can continue this finite proof chain.
        if (_recoveryResponseIds.Count >= MaximumRecoveryResponses
            || !_recoveryResponseIds.Add(id.Value) || !_recoveryResponseCycles.Add(cycle))
        {
            _recoveryScopeLost = true;
            return;
        }
        _freshRequest = id;
        _freshRequestedAt = requested;
        _freshRequestOwnerVerified = true;
    }

    private void ObserveMoveTerminal(JsonElement row)
    {
        if (_stimulus == null) return;
        var id = Id(row, "trace_id");
        if (id == _stimulus.VictimTraceId)
        {
            _victimCompletedAt = Time(row, "completed_at");
            _victimOwnerVerified = String(row, "action") == "Move" && String(row, "result") == "Interrupted"
                && Time(row, "requested_at") == _stimulus.RequestedAt
                && _victimPurpose.HasValue && SamePurpose(_victimPurpose.Value, row);
            if (!_victimOwnerVerified) _recoveryScopeLost = true;
        }
        if (id == _freshRequest)
        {
            var immutable = String(row, "action") == "Move"
                && Time(row, "requested_at") == _freshRequestedAt
                && _running.TryGetValue(id!.Value, out var purpose) && SamePurpose(purpose, row);
            _freshRequestOwnerVerified = immutable && String(row, "result") is "Completed" or "Interrupted";
            if (immutable) _retiredResponseIds.Add(id!.Value);
            if (immutable && String(row, "result") == "TimedOut" && String(row, "failure") == "Navigation")
            {
                // Terminal retirement is not recovery credit. A later qualified
                // response must have its own GUID/cycle and real quest effect.
                _retiredResponseAt = Time(row, "completed_at");
                _freshRequest = null;
                _freshRequestedAt = null;
                if (_recoveryResponseIds.Count >= MaximumRecoveryResponses)
                    _recoveryScopeLost = true;
            }
            else if (!_freshRequestOwnerVerified)
                _recoveryScopeLost = true;
        }
    }

    private void TryRecover(Work consequence, Snapshot current)
    {
        if (current.Dead != false || Recoveries.Count != 0) return;
        if (_stimulus == null && _deathAt.HasValue && consequence.QuestId == _deathGoal
            && consequence.RequestedAt > _deathAt)
            Recoveries.Add(new(consequence.QuestId, null, null, consequence.TraceId, consequence.TraceId,
                "PROVEN: observed own alive-to-dead transition followed by fresh same-purpose quest consequence; no forced death or repair"));
    }

    private void TryControlledRecovery(Work consequence)
    {
        if (IsQuestAction(consequence.Action) && _stimulus != null && !_recoveryScopeLost && Recoveries.Count == 0
            && _queueTrue && _victimOwnerVerified && _freshRequestOwnerVerified
            && (_stimulus.MoveOwner != "PURSUIT_MOVE_TO_UNIT" || consequence.QuestId == _stimulus.QuestId)
            && _victimCompletedAt >= _controlRequestedAt && _victimCompletedAt <= _controlCompletedAt
            && _freshRequestedAt > _victimCompletedAt
            && (consequence.RequestedAt > _freshRequestedAt
                || consequence.TraceId == _freshRequest && consequence.RequestedAt == _freshRequestedAt))
            Recoveries.Add(new(consequence.QuestId, _controlTrace, _stimulus.VictimTraceId,
                _freshRequest!.Value, consequence.TraceId,
                "PROVEN: immutable own quest Move, exact queued interruption, retired arm and fresh autonomous reselection with an authoritative request-local quest effect; root-Q Loot is independently required"));
    }
    private static bool FreshCompletion(Snapshot before, Snapshot after, uint quest)
        => before.Quests.ContainsKey(quest) && before.Completion.TryGetValue(quest, out var oldFlag) && !oldFlag
            && after.Completion.TryGetValue(quest, out var newFlag) && newFlag && !after.Quests.ContainsKey(quest);


    private static Snapshot ReadSnapshot(JsonElement observation, DateTime at)
        => new(at, UInt(observation, "chosenQuestId"), Bool(observation, "isDead"),
            Array(observation, "activeQuests").Where(q => UInt(q, "questId") != 0)
                .ToDictionary(q => UInt(q, "questId"), q => new QuestState(String(q, "step"), String(q, "status"))),
            Array(observation, "questCompletion").Where(q => UInt(q, "questId") != 0 && Bool(q, "completed").HasValue)
                .ToDictionary(q => UInt(q, "questId"), q => Bool(q, "completed")!.Value));

    private static bool ExplicitNull(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Null;

    private static bool MayChangeQuestOrItems(string action)
        => action is not ("Observe" or "Move" or "Stop" or "Target" or "DiscoverQuests" or "DiscoverSelfQuests" or "StopAutoAttack");
    private static bool HasProofArrays(JsonElement observation)
    {
        foreach (var name in new[] { "actorAudit", "activeQuests", "questCompletion" })
            if (!observation.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array
                || array.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.Object)) return false;
        foreach (var name in new[] { "activeQuests", "questCompletion" })
        {
            var rows = Array(observation, name).ToArray();
            if (rows.Length > 256 || rows.Any(r => UInt(r, "questId") == 0)
                || rows.Select(r => UInt(r, "questId")).Distinct().Count() != rows.Length) return false;
        }
        foreach (var name in new[] { "items", "objectiveItems" })
            if (observation.TryGetProperty(name, out var array) && (array.ValueKind != JsonValueKind.Array
                || array.GetArrayLength() > 256 || array.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.Object))) return false;
        return true;
    }

    private static bool IsQuestAction(string action)
        => action is "Loot" or "AcceptQuest" or "AdvanceQuest" or "TurnInQuest" or "TurnInDoodad" or "AutoTurnIn";
    internal static IEnumerable<JsonElement> Array(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray() : [];
    internal static uint UInt(JsonElement value, string name)
        => value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : 0;
    internal static long Long(JsonElement value, string name)
        => value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : -1;
    internal static string String(JsonElement value, string name)
        => value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    internal static bool? Bool(JsonElement value, string name)
        => value.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    internal static Guid? Id(JsonElement value, string name)
        => Guid.TryParse(String(value, name), out var id) && id != Guid.Empty ? id : null;
    internal static DateTime? Time(JsonElement value, string name)
        => value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTime(out var at)
            ? at.ToUniversalTime() : null;
    internal static double? Number(JsonElement value, string name)
        => value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
}
