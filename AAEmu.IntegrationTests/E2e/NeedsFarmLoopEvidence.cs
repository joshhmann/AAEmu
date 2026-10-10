using System.Text.Json;

using static AAEmu.IntegrationTests.E2e.BotShortRunEvidence;

namespace AAEmu.IntegrationTests.E2e;

// Supplied passive observations are evidence, not gameplay. No dispatch, wake,
// timer acceleration or inferred effects; lifecycle Completed is insufficient.
internal sealed class NeedsFarmLoopEvidence
{
    internal const int MaximumSeconds = 900, NoProgressSeconds = 300, CallSeconds = 5;
    internal const uint Seed = 15659, Potato = 7992, Auxiliary = 19887, CropTemplate = 2259, Mature = 4457;
    internal sealed record Position(double X, double Y, double Z)
    {
        internal double Distance(Position other)
        {
            var dx = X - other.X; var dy = Y - other.Y; var dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        internal double Horizontal(Position other)
        {
            var dx = X - other.X; var dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
    internal sealed record Crop(uint ObjId, uint DbId, uint OwnerId, uint Template, uint Phase,
        string OwnerType, uint HouseId, string FarmType, DateTime PlantTime, DateTime GrowthTime, Position Position);
    internal sealed record PhaseWitness(DateTime ObservedAtUtc, long Wake, uint Phase, Position ActorPosition,
        Position CropPosition, double DistanceMetres, double HorizontalMetres);
    internal sealed record Actability(uint Id, long Point, long Step);
    internal sealed record HarvestWitness(Guid TraceId, DateTime RequestedAtUtc, DateTime CompletedAtUtc,
        DateTime ObservedAtUtc, string DecisionCycle, long? RequestWakeSequence, long PotatoGain, long ReturnedSeeds, long AuxiliaryGain,
        long LaborBefore, long LaborAfter, long ExperienceBefore, long ExperienceAfter,
        Actability[] ActabilitiesBefore, Actability[] ActabilitiesAfter, Position ActorPosition,
        double DistanceToLastCropPositionMetres, double HorizontalToLastCropPositionMetres);
    internal sealed class Lifetime(Guid plantTrace, string cycle, long? requestWake, Crop crop, DateTime requested, DateTime completed, DateTime observed)
    {
        public Guid PlantTraceId { get; } = plantTrace;
        public string DecisionCycle { get; } = cycle;
        public long? RequestWakeSequence { get; } = requestWake;
        public Crop Creation { get; } = crop;
        public DateTime PlantRequestedAtUtc { get; } = requested;
        public DateTime PlantCompletedAtUtc { get; } = completed;
        public DateTime FirstObservedAtUtc { get; } = observed;
        public List<PhaseWitness> Immature { get; } = [];
        public PhaseWitness? MatureWitness { get; internal set; }
        public HarvestWitness? Harvest { get; internal set; }
        public bool DatabaseCreationObserved { get; internal set; }
        public bool DatabaseDeletionObserved { get; internal set; }
    }
    internal sealed record InterruptCandidate(Guid VictimTraceId, DateTime RequestedAtUtc, DateTime ObservedAtUtc,
        Position ActorPosition, Position Destination, bool RequestedBeforeStart);
    private sealed record Snapshot(DateTime Begin, DateTime End, uint Character, uint Actor, long Generation,
        long Wake, bool Registered, bool ActorAvailable, string Activity, long Money, long Labor, long Experience,
        long Level, Position Position, Dictionary<uint, long> Items, Actability[] Actabilities,
        Dictionary<uint, Crop> Crops, HashSet<uint> Missing, JsonElement Raw);

    private readonly uint _character;
    private readonly DateTime _start;
    private readonly Position _approachBaseline;
    private Snapshot _previous;
    private Snapshot? _last;
    private readonly HashSet<Guid> _seen = [];
    private readonly HashSet<string> _decisionCycles = [];
    private Guid? _auditTail;
    private DateTime? _lastFarmCompleted;
    private double _lastProgress;
    private InterruptCandidate? _victim;
    private DateTime? _victimInterruptedAt;
    private DateTime? _controlRequestedAt, _controlCompletedAt;
    private Guid? _freshMove;
    private Position? _freshMovePosition;
    private DateTime? _freshMoveRequested;
    private bool _freshMoveDisplaced;
    internal List<Lifetime> Lifetimes { get; } = [];
    internal uint ActorId { get; private set; }
    internal long Generation { get; private set; }
    internal Guid? ControlTraceId { get; private set; }
    internal bool ControlCompleted { get; private set; }
    internal DateTime? ControlRequestedAtUtc => _controlRequestedAt;
    internal DateTime? ControlCompletedAtUtc => _controlCompletedAt;
    internal DateTime? VictimInterruptedAtUtc => _victimInterruptedAt;
    internal bool RecoveryProved { get; private set; }
    internal string? EvidenceFault { get; private set; }
    internal DateTime StartUtc => _start;
    internal IEnumerable<uint> WatchedObjIds => Lifetimes.Select(l => l.Creation.ObjId).Distinct();
    internal int HarvestCount => Lifetimes.Count(l => l.Harvest != null);
    internal bool LiveAccepted => EvidenceFault == null && ControlCompleted && RecoveryProved
        && Lifetimes.Count == 2 && Lifetimes.All(l => l.Harvest != null && l.DatabaseCreationObserved)
        && Lifetimes[0].DatabaseDeletionObserved;
    internal JsonElement? LastObservation => _last?.Raw;

    // Baseline is BEFORE enrollment. START is after its one wake; never replace
    // the bag/crop baseline with a post-wake state and hide setup effects.
    internal NeedsFarmLoopEvidence(uint character, JsonElement beforeEnrollment, DateTime startUtc)
    {
        _character = character;
        _start = startUtc;
        _approachBaseline = RequiredPosition(beforeEnrollment);
        _previous = Parse(beforeEnrollment);
        if (_previous.Character != character || _previous.Registered || _previous.ActorAvailable
            || _previous.Crops.Count != 0 || _previous.Items[Seed] != 3)
            Fail("PRESTART_BASELINE: expected own absent enrollment/actor, no tracked crop and three seeds");
    }
    internal string? StopReason(double seconds) => EvidenceFault != null ? "EVIDENCE_FAULT"
        : LiveAccepted ? "TWO_APPLIED_HARVESTS" : seconds >= MaximumSeconds ? "MAXIMUM_WINDOW"
        : seconds - _lastProgress >= NoProgressSeconds ? "NO_AUTHORITATIVE_PROGRESS" : null;
    internal double RemainingSeconds(double seconds) => Math.Max(0,
        Math.Min(MaximumSeconds - seconds, NoProgressSeconds - (seconds - _lastProgress)));
    private void Fail(string predicate) => EvidenceFault ??= predicate;
    private void Progress(double seconds) => _lastProgress = seconds;

    internal void Observe(double seconds, JsonElement value)
    {
        if (StopReason(seconds) != null) return;
        Snapshot current;
        try { current = Parse(value); }
        catch (Exception ex) { Fail("PASSIVE_PROJECTION: " + ex.Message); return; }
        _last = current;
        if (current.Character != _character || current.End <= _previous.End || current.Begin < _previous.End)
        { Fail("SNAPSHOT_OWNERSHIP_OR_ORDER"); return; }
        if (!current.Registered)
        { Fail("ENROLLMENT_ABSENT: observation must not enroll or invent state"); return; }
        if (!current.ActorAvailable) { _previous = current; return; }
        if (ActorId == 0) { ActorId = current.Actor; Generation = current.Generation; }
        if (current.Actor != ActorId || current.Generation != Generation || current.Actor == 0 || current.Wake <= 0
            || Bool(value, "enrollmentSchedulerRunning") != true || Bool(value, "enrollmentIdleWakeEnabled") != true
            || Bool(value, "isDead") != false || Number(value, "dropRateMul") != 0
            || Number(value, "growthRate") != 10 || Number(value, "lootRate") != 1
            || Bool(value, "laborConsumptionDisabled") != false)
        { Fail("ACTOR_ENROLLMENT_OR_ORDINARY_LOOT_CHANGED"); return; }
        if (_previous.ActorAvailable && current.Wake < _previous.Wake)
        { Fail("DECORATOR_WAKE_REGRESSED"); return; }
        var rows = Array(value, "actorAudit").ToArray();
        if (rows.Length > 512) { Fail("AUDIT_BOUND"); return; }
        if (_auditTail.HasValue && !rows.Any(r => Id(r, "trace_id") == _auditTail))
        { Fail("AUDIT_PREVIOUS_TAIL_MISSING: terminal history overlap cannot be proved"); return; }
        foreach (var row in rows.OrderBy(r => Time(r, "completed_at")))
        {
            var id = Id(row, "trace_id");
            var requested = Time(row, "requested_at");
            var completed = Time(row, "completed_at");
            if (id == null || requested == null || completed == null || UInt(row, "actor_id") != ActorId)
            { Fail("AUDIT_IDENTITY_OR_TIME"); break; }
            if (!_seen.Add(id.Value)) continue;
            if (_seen.Count > 4096) { Fail("AUDIT_HISTORY_BOUND"); break; }
            if (_victim?.VictimTraceId == id && String(row, "result") == "Interrupted")
            {
                if (String(row, "action") != "Move" || String(row, "move_owner") != "OTHER:needs-farm-travel"
                    || requested != _victim.RequestedAtUtc || completed < _start)
                    Fail("INTERRUPTED_VICTIM_JOIN");
                else { _victimInterruptedAt = completed; QualifyControlWindow(); Progress(seconds); }
            }
            if (EvidenceFault != null) break;
            var action = String(row, "action");
            if (action is not ("Plant" or "Harvest")) continue;
            if (_lastFarmCompleted.HasValue && requested.Value <= _lastFarmCompleted.Value)
            { Fail("STALE_FARM_REQUEST_ORDER"); break; }
            var carriedRequest = Id(_previous.Raw, "liveTraceId") == id
                && String(_previous.Raw, "liveAction") == action && String(_previous.Raw, "liveState") == "Running"
                && UInt(_previous.Raw, "liveTargetId") == UInt(row, "target_id")
                && Time(_previous.Raw, "liveRequestedAtUtc") == requested && _previous.Begin > requested;
            if (requested < _start || (requested <= _previous.End && !carriedRequest)
                || completed <= _previous.End || completed >= current.Begin || completed < requested)
            { Fail("EFFECT_JOIN_AMBIGUOUS: unobserved request/effect straddles START or a snapshot watermark"); break; }
            if (String(row, "result") != "Completed")
            { Fail("FARM_REQUEST_NOT_APPLIED: " + action + "/" + String(row, "result")); break; }
            if (String(row, "decision_goal") != (action == "Plant" ? "needs.plant" : "needs.harvest")
                || string.IsNullOrWhiteSpace(String(row, "decision_policy"))
                || string.IsNullOrWhiteSpace(String(row, "decision_cycle_id"))
                || current.Activity != "needs.farm")
            { Fail("FRESH_NEEDS_DECISION_JOIN"); break; }
            var cycle = String(row, "decision_cycle_id");
            if (!_decisionCycles.Add(cycle))
            { Fail("REUSED_DECISION_CYCLE"); break; }
            if (action == "Plant") Plant(seconds, current, row, id.Value, requested.Value, completed.Value);
            else Harvest(seconds, current, row, id.Value, requested.Value, completed.Value);
            if (EvidenceFault != null) break;
            _lastFarmCompleted = completed;
        }
        if (rows.Length > 0) _auditTail = Id(rows[^1], "trace_id");
        if (EvidenceFault == null && !rows.Any(r => Time(r, "completed_at") > _previous.End
            && String(r, "action") is "Plant" or "Harvest")
            && (current.Money != _previous.Money || current.Labor != _previous.Labor
                || current.Experience != _previous.Experience || current.Level != _previous.Level
                || !current.Actabilities.SequenceEqual(_previous.Actabilities)
                || current.Items.Any(i => i.Value != _previous.Items[i.Key])))
            Fail("RESOURCE_CHANGE_WITHOUT_OWN_FARM_REQUEST");
        if (EvidenceFault == null)
        {
            ObserveRecovery(current);
            foreach (var life in Lifetimes.Where(l => l.Harvest == null))
            {
                if (!current.Crops.TryGetValue(life.Creation.ObjId, out var crop) || !SameIncarnation(crop, life.Creation))
                { Fail("TRACKED_CROP_LOST_WITHOUT_APPLIED_HARVEST"); break; }
                if (crop.Phase is 4379 or 4456)
                {
                    if (life.MatureWitness != null) { Fail("CROP_PHASE_REGRESSED"); break; }
                    if ((life.Immature.Count == 0 || current.Wake > life.Immature[^1].Wake) && life.Immature.Count < 2)
                    { life.Immature.Add(Witness(current, crop)); Progress(seconds); }
                }
                else if (crop.Phase == Mature)
                {
                    if (life.Immature.Count < 2) { Fail("ORDINARY_WAIT_NOT_WITNESSED_ACROSS_WAKES"); break; }
                    if (life.MatureWitness == null) Progress(seconds);
                    // Preserve actual pre-request evidence while an observed
                    // long-running Harvest spans later passive polls.
                    life.MatureWitness ??= Witness(current, crop);
                }
                else { Fail("UNEXPECTED_CROP_PHASE"); break; }
            }
        }
        _previous = current;
    }

    private static PhaseWitness Witness(Snapshot current, Crop crop) => new(current.End, current.Wake, crop.Phase,
        current.Position, crop.Position, current.Position.Distance(crop.Position), current.Position.Horizontal(crop.Position));

    private void Plant(double seconds, Snapshot current, JsonElement row, Guid trace, DateTime requested, DateTime completed)
    {
        var cycle = String(row, "decision_cycle_id");
        var tracked = UInt(current.Raw, "cropObjId");
        if (Lifetimes.Count >= 2 || Lifetimes.Any(l => l.Harvest == null) || UInt(row, "target_id") != Seed
            || !current.Crops.TryGetValue(tracked, out var crop) || crop.DbId == 0 || crop.OwnerId != _character
            || crop.Template != CropTemplate || crop.OwnerType != "Character" || crop.HouseId != 0 || crop.FarmType != "Farm"
            || crop.PlantTime < requested || crop.PlantTime > completed
            || _previous.Items[Seed] - current.Items[Seed] != 1 || _previous.Items[Potato] != current.Items[Potato]
            || _previous.Items[Auxiliary] != current.Items[Auxiliary] || current.Money != _previous.Money
            || current.Labor != _previous.Labor || current.Experience != _previous.Experience
            || current.Level != _previous.Level || !current.Actabilities.SequenceEqual(_previous.Actabilities))
        { Fail("PLANT_HAS_NO_UNAMBIGUOUS_SEED_CROP_EFFECT"); return; }
        if (Lifetimes.Count == 0 && (!ControlCompleted || !_victimInterruptedAt.HasValue || !_freshMoveDisplaced
            || !_freshMoveRequested.HasValue || requested <= _freshMoveRequested.Value))
        { Fail("FIRST_PLANT_WITHOUT_TRUE_INTERRUPTION_RECOVERY"); return; }
        if (Lifetimes.Count == 1)
        {
            var first = Lifetimes[0];
            if (!first.DatabaseDeletionObserved || first.Harvest == null || requested <= first.Harvest.ObservedAtUtc
                || first.DecisionCycle == cycle || first.Harvest.DecisionCycle == cycle
                || crop.PlantTime <= first.Creation.PlantTime
                || (crop.ObjId == first.Creation.ObjId && !current.Missing.Contains(crop.ObjId)
                    && !_previous.Missing.Contains(crop.ObjId)))
            { Fail("SECOND_CROP_IS_NOT_A_FRESH_LIFETIME"); return; }
        }
        Lifetimes.Add(new Lifetime(trace, cycle, RequestWake(row), crop, requested, completed, current.End));
        if (Lifetimes.Count == 1) RecoveryProved = true;
        Progress(seconds);
    }
    private void Harvest(double seconds, Snapshot current, JsonElement row, Guid trace, DateTime requested, DateTime completed)
    {
        var life = Lifetimes.SingleOrDefault(l => l.Harvest == null);
        var gain = current.Items[Potato] - _previous.Items[Potato];
        var seedGain = current.Items[Seed] - _previous.Items[Seed];
        var auxiliaryGain = current.Items[Auxiliary] - _previous.Items[Auxiliary];
        if (life == null || UInt(row, "target_id") != life.Creation.ObjId || life.Immature.Count < 2
            || life.MatureWitness == null || life.MatureWitness.ObservedAtUtc >= requested
            || !life.DatabaseCreationObserved || !current.Missing.Contains(life.Creation.ObjId)
            || gain is < 2 or > 4 || seedGain is < 0 or > 1 || auxiliaryGain is < 0 or > 1
            || current.Labor != _previous.Labor - 1 || current.Money != _previous.Money)
        { Fail("HARVEST_HAS_NO_PRE_REQUEST_MATURITY_DELETION_YIELD_LABOR_EFFECT"); return; }
        life.Harvest = new HarvestWitness(trace, requested, completed, current.End, String(row, "decision_cycle_id"),
            RequestWake(row), gain, seedGain, auxiliaryGain,
            _previous.Labor, current.Labor, _previous.Experience, current.Experience,
            _previous.Actabilities, current.Actabilities, current.Position,
            current.Position.Distance(life.MatureWitness.CropPosition), current.Position.Horizontal(life.MatureWitness.CropPosition));
        Progress(seconds);
    }
    private static bool SameIncarnation(Crop actual, Crop expected) => actual.ObjId == expected.ObjId && actual.DbId == expected.DbId
        && actual.OwnerId == expected.OwnerId && actual.Template == expected.Template && actual.PlantTime == expected.PlantTime
        && actual.HouseId == expected.HouseId && actual.FarmType == expected.FarmType;
    private static long? RequestWake(JsonElement row) => row.TryGetProperty("wake_sequence", out var wake)
        && wake.ValueKind == JsonValueKind.Number && wake.TryGetInt64(out var number) ? number : null;

    internal InterruptCandidate? GetInterruptCandidate()
    {
        var current = _last;
        if (_victim != null || current == null || Lifetimes.Count != 0 || ActorId == 0 || EvidenceFault != null) return null;
        var raw = current.Raw;
        var trace = Id(raw, "liveTraceId");
        var requested = Time(raw, "liveRequestedAtUtc");
        var destination = TryPosition(raw, "liveDestination");
        if (trace == null || requested == null || destination == null || current.Activity != "needs.farm"
            || String(raw, "liveAction") != "Move" || String(raw, "liveState") != "Running"
            || String(raw, "liveMoveOwner") != "OTHER:needs-farm-travel"
            || current.Position.Horizontal(destination) <= 4
            || current.Position.Horizontal(_approachBaseline) < 0.1
            || current.Position.Horizontal(destination) >= _approachBaseline.Horizontal(destination)) return null;
        return new InterruptCandidate(trace.Value, requested.Value, current.End, current.Position, destination, requested < _start);
    }
    internal void DeclareInterrupt(InterruptCandidate candidate)
    {
        if (_victim != null || candidate != GetInterruptCandidate()) { Fail("INTERRUPT_OWNERSHIP_OR_COUNT"); return; }
        _victim = candidate;
    }
    internal void AcknowledgeInterrupt(Guid controlTrace)
    {
        if (_victim == null || controlTrace == Guid.Empty || ControlTraceId.HasValue) Fail("INTERRUPT_ACK_JOIN");
        else ControlTraceId = controlTrace;
    }
    internal void ObserveControl(JsonElement value)
    {
        if (String(value, "state") is "Requested" or "Accepted" or "Running") return;
        if (_victim == null || Id(value, "trace_id") != ControlTraceId || UInt(value, "actor_id") != ActorId
            || String(value, "action") != "Interrupt" || String(value, "state") != "Completed"
            || Bool(value, "result_payload") != true || Time(value, "requested_at") is not { } requested
            || Time(value, "completed_at") is not { } completed || requested < _victim.ObservedAtUtc || completed < requested)
            Fail("INTERRUPT_CONTROL_DID_NOT_MATCH_AN_ACTIVE_REQUEST");
        else
        {
            _controlRequestedAt = requested;
            _controlCompletedAt = completed;
            QualifyControlWindow();
        }
    }
    private void QualifyControlWindow()
    {
        if (!_controlRequestedAt.HasValue || !_controlCompletedAt.HasValue || !_victimInterruptedAt.HasValue) return;
        if (_victimInterruptedAt < _controlRequestedAt || _victimInterruptedAt > _controlCompletedAt)
            Fail("VICTIM_INTERRUPTED_OUTSIDE_CONTROL_WINDOW");
        else ControlCompleted = true;
    }
    private void ObserveRecovery(Snapshot current)
    {
        // Preserve actually observed replacement movement while its control
        // receipt is pending; first Plant still requires the qualified join.
        if (!_victimInterruptedAt.HasValue || Lifetimes.Count != 0) return;
        var id = Id(current.Raw, "liveTraceId");
        var requested = Time(current.Raw, "liveRequestedAtUtc");
        if (id == null || id == _victim?.VictimTraceId || requested <= _victimInterruptedAt
            || String(current.Raw, "liveAction") != "Move" || String(current.Raw, "liveState") != "Running"
            || String(current.Raw, "liveMoveOwner") != "OTHER:needs-farm-travel") return;
        if (_freshMove == null)
        { _freshMove = id; _freshMoveRequested = requested; _freshMovePosition = current.Position; }
        else if (_freshMove == id && _freshMovePosition != null && current.Position.Horizontal(_freshMovePosition) >= 0.1)
            _freshMoveDisplaced = true;
    }
    internal void RecordDatabaseCreation(uint dbId, uint ownerId, uint template, DateTime plantTime, uint houseId, int farmType)
    {
        var life = Lifetimes.LastOrDefault(l => l.Creation.DbId == dbId && l.Harvest == null);
        if (life == null || ownerId != _character || template != CropTemplate || houseId != 0 || farmType != 1
            || Math.Abs((plantTime.ToUniversalTime() - life.Creation.PlantTime).TotalSeconds) >= 1)
            Fail("DATABASE_CROP_CREATION_JOIN");
        else life.DatabaseCreationObserved = true;
    }
    internal void RecordDatabaseDeletion(Guid plantTrace, bool absent)
    {
        var life = Lifetimes.SingleOrDefault(l => l.PlantTraceId == plantTrace);
        if (life?.Harvest == null || !absent) Fail("DATABASE_CROP_DELETION_JOIN");
        else life.DatabaseDeletionObserved = true;
    }

    private static Snapshot Parse(JsonElement value)
    {
        var begin = Time(value, "snapshotStartedAtUtc") ?? throw new InvalidOperationException("Missing snapshot start");
        var end = Time(value, "observedAtUtc") ?? throw new InvalidOperationException("Missing end-of-copy watermark");
        if (begin > end) throw new InvalidOperationException("Snapshot time reversed");
        if (!value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || !value.TryGetProperty("doodads", out var doodads) || doodads.ValueKind != JsonValueKind.Array
            || !value.TryGetProperty("actabilities", out var abilities) || abilities.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Missing actual resource/crop/actability arrays");
        var counts = items.EnumerateArray().ToDictionary(i => UInt(i, "template"), i => Long(i, "count"));
        if (new uint[] { Seed, Potato, Auxiliary }.Any(t => !counts.TryGetValue(t, out var n) || n < 0))
            throw new InvalidOperationException("Missing actual watched item count");
        var actabilities = abilities.EnumerateArray()
            .Select(a => new Actability(UInt(a, "id"), Long(a, "point"), Long(a, "step"))).OrderBy(a => a.Id).ToArray();
        if (actabilities.Any(a => a.Id == 0 || a.Point < 0 || a.Step is < 0 or > 255)
            || actabilities.Select(a => a.Id).Distinct().Count() != actabilities.Length)
            throw new InvalidOperationException("Invalid/duplicate actual actability");
        var crops = new Dictionary<uint, Crop>();
        var missing = new HashSet<uint>();
        var dispositions = new HashSet<uint>();
        foreach (var row in doodads.EnumerateArray())
        {
            var objId = UInt(row, "objId");
            if (objId == 0 || !dispositions.Add(objId)) throw new InvalidOperationException("Missing/duplicate/conflicting crop disposition");
            if (Bool(row, "found") == false) { missing.Add(objId); continue; }
            if (Bool(row, "found") != true) throw new InvalidOperationException("Missing crop presence");
            crops.Add(objId, new Crop(objId, UInt(row, "dbId"), UInt(row, "ownerId"), UInt(row, "template"), UInt(row, "phase"),
                String(row, "ownerType"), UInt(row, "houseId"), String(row, "farmType"),
                Time(row, "plantTime") ?? throw new InvalidOperationException("Missing PlantTime"),
                Time(row, "growthTime") ?? throw new InvalidOperationException("Missing GrowthTime"), RequiredPosition(row)));
        }
        var available = Bool(value, "actorAvailable") == true;
        if (available && (Bool(value, "stateAvailable") != true || UInt(value, "actorCharId") != UInt(value, "id")
            || UInt(value, "actorObjId") != UInt(value, "objId")
            || !value.TryGetProperty("actorAudit", out var audit) || audit.ValueKind != JsonValueKind.Array))
            throw new InvalidOperationException("Absent/foreign live actor audit");
        if (Long(value, "money") < 0 || Long(value, "labor") < 0 || Long(value, "experience") < 0 || Long(value, "level") < 1)
            throw new InvalidOperationException("Actual body/resources are absent");
        return new Snapshot(begin, end, UInt(value, "id"), UInt(value, "actorObjId"), Long(value, "enrollmentGeneration"),
            Long(value, "wakeSequence"), Bool(value, "enrollmentRegistered") == true, available,
            String(value, "arbiterActivity"), Long(value, "money"), Long(value, "labor"), Long(value, "experience"),
            Long(value, "level"), RequiredPosition(value), counts, actabilities, crops, missing, value.Clone());
    }
    private static Position RequiredPosition(JsonElement row) => new(Number(row, "x") ?? throw new InvalidOperationException("Missing x"),
        Number(row, "y") ?? throw new InvalidOperationException("Missing y"), Number(row, "z") ?? throw new InvalidOperationException("Missing z"));
    private static Position? TryPosition(JsonElement row, string prefix) => Number(row, prefix + "X") is { } x
        && Number(row, prefix + "Y") is { } y && Number(row, prefix + "Z") is { } z ? new(x, y, z) : null;
}
