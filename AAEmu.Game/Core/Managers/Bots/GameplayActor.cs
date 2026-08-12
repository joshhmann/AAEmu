using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Server-side implementation of the M5 gameplay actor contract (v1).
///
/// Execution boundary rules (ROADMAP M5):
///  - At most ONE request runs at a time (single-writer rule). A request
///    while Running is Rejected(StateTransition, "busy"). Stop() is the
///    only request allowed while busy — it interrupts the active one.
///  - Every terminal transition emits an <see cref="ActorAuditRecord"/>.
///  - Retries are deduped through the <see cref="ActorEffectLedger"/>: an
///    explicit idempotency key whose prior attempt may have executed
///    (Completed/Interrupted/TimedOut) is never re-executed; the duplicate
///    is Rejected(StateTransition) pre-flight. Every action supports a
///    timeout budget (§17 reason: Move → Navigation, else Starvation).
///  - All execution goes through normal gameplay services: movement applies
///    the ordinary Transform (same facility Simulation.MoveTo / the M2b
///    pilot use), targeting sets Unit.CurrentTarget, casting calls
///    Character.UseSkill (the exact learned-skill branch CSStartSkillPacket
///    uses). Observe reads the region graph + character state — no packets.
///
/// Threading: NOT thread-safe by itself. The scheduler's per-bot execution
/// lease (IPlayerBotScheduler) guarantees at most one in-flight step per
/// bot, and the M5 A1 marshal executes every step on the single execution
/// boundary (the game-loop thread) — the actor is driven from exactly one
/// execution context at a time.
/// </summary>
public class GameplayActor : IGameplayActor
{
    /// <summary>Arrival radius for Move legs (same checkpoint model as Simulation.RangeToCheckPoint).</summary>
    public const float ArrivalRadius = 0.5f;

    /// <summary>Default navigation budget for a Move request.</summary>
    public static readonly TimeSpan DefaultMoveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Max audit records retained (newest last).</summary>
    private const int MaxTraceRecords = 512;

    private readonly List<ActorAuditRecord> _trace = [];
    private readonly ActorEffectLedger _ledger = new();
    private ActorRequest? _active;

    public uint ActorId => Character.ObjId;

    public Character Character { get; }

    public ActorRequest? ActiveRequest => _active;

    public IReadOnlyList<ActorAuditRecord> AuditTrace => _trace;

    public GameplayActor(Character character)
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
    }

    #region Observe

    public ActorObservation Observe()
    {
        var observation = new ActorObservation
        {
            ActorId = ActorId,
            Position = Character.Transform.World.Position,
            CurrentTargetObjId = Character.CurrentTarget?.ObjId ?? 0,
            Hp = Character.Hp,
            MaxHp = Character.MaxHp,
            Mp = Character.Mp,
            MaxMp = Character.MaxMp,
            NearbyCharacterObjIds = WorldManager.GetAround<Character>(Character, 25f).Select(c => c.ObjId).ToList(),
            NearbyNpcObjIds = WorldManager.GetAround<Npc>(Character, 25f).Select(n => n.ObjId).ToList(),
            NearbyDoodadObjIds = WorldManager.GetAround<Doodad>(Character, 25f).Select(d => d.ObjId).ToList(),
            ActiveQuestIds = Character.Quests?.ActiveQuests.Keys.ToList() ?? []
        };

        // Observe is a query, not a mutation: it completes immediately and
        // still emits the audit record (every action emits one).
        var request = NewRequest(ActorActionType.Observe, 0);
        request.Accept("observe");
        request.Start("query");
        Finish(request, request.Complete(observation));
        return observation;
    }

    #endregion

    #region Actions

    public ActorRequest MoveTo(Vector3 destination, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Move, 0, destination, timeout: timeout ?? DefaultMoveTimeout, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "move"))
            return request;

        if (speed <= 0f)
            return Reject(request, ActorFailureReason.RejectedAction, "speed must be positive");
        if (!destination.IsFinite())
            return Reject(request, ActorFailureReason.RejectedAction, "destination must be finite");
        return StartMove(request, destination, speed);
    }

    /// <summary>
    /// Sets up the movement leg for an already-accepted Move request and
    /// starts it Running (or completes immediately when already there).
    /// </summary>
    private ActorRequest StartMove(ActorRequest request, Vector3 destination, float speed)
    {
        if (MathUtil.CalculateDistance(Character.Transform.World.Position, destination, false) <= ArrivalRadius
            && Math.Abs(Character.Transform.World.Position.Z - destination.Z) <= ArrivalRadius)
            return Complete(request, "already at destination");

        _moveTarget = destination;
        _moveSpeed = speed;
        request.Start("walking");
        return request;
    }

    public ActorRequest MoveToUnit(uint targetObjId, float speed = 5f, TimeSpan? timeout = null, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Move, targetObjId, timeout: timeout ?? DefaultMoveTimeout, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "move"))
            return request;

        var unit = ResolveUnit(targetObjId);
        if (unit == null)
            return Reject(request, ActorFailureReason.RejectedAction, "target unit not found");
        return StartMove(request, unit.Transform.World.Position, speed);
    }

    public ActorRequest Stop()
    {
        var request = NewRequest(ActorActionType.Stop, 0);
        if (!request.Accept("stop"))
            return request; // defensive; should never happen

        // Interrupt whatever is running (if anything), then complete the stop.
        if (_active is { IsTerminal: false })
            InterruptActive("stop requested");
        request.Start("interrupting");
        Finish(request, request.Complete(detail: "stopped"));
        return request;
    }

    public ActorRequest SetTarget(uint targetObjId)
    {
        var request = NewRequest(ActorActionType.Target, targetObjId);
        if (!TryBegin(request, "target"))
            return request;

        var unit = ResolveUnit(targetObjId);
        if (unit == null)
            return Reject(request, ActorFailureReason.RejectedAction, "target not found in world");

        Character.CurrentTarget = unit;
        return Complete(request, $"targeting {unit.ObjId}");
    }

    public ActorRequest Cast(uint skillId, uint targetObjId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.Cast, targetObjId, skillId: skillId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "cast"))
            return request;

        // Validation gate 1: the skill template must exist.
        var template = SkillManager.Instance.GetSkillTemplate(skillId);
        if (template == null)
            return Reject(request, ActorFailureReason.RejectedAction, $"unknown skill {skillId}");

        // Validation gate 2: the character must actually know the skill
        // (learned, default/common, or a variant of one) — same rule the
        // CSStartSkillPacket learned-skill branch applies.
        var known = Character.Skills?.Skills.ContainsKey(skillId) == true
                    || Character.Skills?.IsVariantOfSkill(skillId) == true
                    || SkillManager.Instance.IsDefaultSkill(skillId)
                    || SkillManager.Instance.IsCommonSkill(skillId);
        if (!known)
            return Reject(request, ActorFailureReason.RejectedAction, $"skill {skillId} not learned");

        // Validation gate 3: the target must resolve to a unit we can cast at.
        var target = ResolveUnit(targetObjId);
        if (target == null)
            return Reject(request, ActorFailureReason.RejectedAction, "cast target not found in world");

        request.Start($"casting {skillId} on {target.ObjId}");

        // Execute through the REAL engine path — the same call the
        // CSStartSkillPacket learned-skill branch makes.
        var result = Character.UseSkill(skillId, target);
        if (result == SkillResult.Success)
            return Complete(request, result, $"skill {skillId} cast succeeded");
        return Reject(request, ActorFailureReason.RejectedAction, $"skill {skillId} refused: {result}");
    }

    public bool Interrupt(Guid traceId)
    {
        if (_active == null || _active.TraceId != traceId || _active.IsTerminal)
            return false;
        InterruptActive("interrupted by controller");
        return true;
    }

    #endregion

    #region Quest actions (M5 vocabulary — real engine paths)

    private PlayerBotController? _questController;

    /// <summary>
    /// Quest ops compose around the shared PlayerBotController, which is
    /// itself a thin wrapper over the ordinary character's quest surfaces
    /// (CharacterQuests.AddQuest / UnitEvents / QuestManager.DoReportEvents).
    /// No bot-only quest state is created here.
    /// </summary>
    private PlayerBotController QuestController => _questController ??= new PlayerBotController(Character);

    public ActorRequest AcceptQuest(uint questId, QuestAcceptorType acceptorType, uint acceptorId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.AcceptQuest, questId,
            payload: new QuestAcceptParams(acceptorType, acceptorId), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "accept quest"))
            return request;

        request.Start($"accepting quest {questId} via {acceptorType}/{acceptorId}");
        var accepted = QuestController.AcceptQuest(questId, acceptorType, acceptorId);
        if (accepted)
            return Complete(request, accepted, $"quest {questId} accepted ({acceptorType}/{acceptorId})");
        return Reject(request, ActorFailureReason.RejectedAction,
            $"quest {questId} accept refused by engine gate ({acceptorType}/{acceptorId})");
    }

    public ActorRequest AdvanceQuest(uint questId, string? idempotencyKey = null)
    {
        var request = NewRequest(ActorActionType.AdvanceQuest, questId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "advance quest"))
            return request;

        var quest = Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return Reject(request, ActorFailureReason.StateTransition,
                $"quest {questId} not active (cannot advance)");

        request.Start($"advancing quest {questId} (step {quest.Step}, status {quest.Status})");
        _ = quest.RunCurrentStep();
        return Complete(request, true,
            $"quest {questId} advanced (step {quest.Step}, status {quest.Status})");
    }

    public ActorRequest TurnInQuest(uint questId, uint npcObjId, int selectedReward = -1, string? idempotencyKey = null)
        => TurnIn(questId, ActorActionType.TurnInQuest, npcObjId, selectedReward, idempotencyKey);

    public ActorRequest TurnInAtDoodad(uint questId, uint doodadObjId, int selectedReward = -1, string? idempotencyKey = null)
        => TurnIn(questId, ActorActionType.TurnInDoodad, doodadObjId, selectedReward, idempotencyKey);

    public ActorRequest AutoTurnInQuest(uint questId, int selectedReward = -1, string? idempotencyKey = null)
        => TurnIn(questId, ActorActionType.AutoTurnIn, 0, selectedReward, idempotencyKey);

    /// <summary>
    /// Turn-in through the real packet path (QuestManager.DoReportEvents),
    /// then the same single step-machine advance the world pipeline performs
    /// after a report event. The world target must resolve when one is
    /// given; 0 (auto turn-in) always resolves.
    /// </summary>
    private ActorRequest TurnIn(uint questId, ActorActionType action, uint targetObjId, int selectedReward, string? idempotencyKey)
    {
        var request = NewRequest(action, questId, payload: new QuestTurnInParams(targetObjId, selectedReward), idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "turn in"))
            return request;

        var quest = Character.Quests?.ActiveQuests.GetValueOrDefault(questId);
        if (quest == null)
            return Reject(request, ActorFailureReason.StateTransition,
                $"quest {questId} not active (cannot turn in)");

        if (action != ActorActionType.AutoTurnIn && targetObjId != 0)
        {
            var target = ResolveUnit(targetObjId);
            if (target == null)
                return Reject(request, ActorFailureReason.RejectedAction,
                    $"turn-in target {targetObjId} not found in world (quest {questId})");
        }

        request.Start($"turning in quest {questId} (target {targetObjId}, selected {selectedReward})");
        switch (action)
        {
            case ActorActionType.TurnInQuest:
                _ = QuestController.ReportTurnIn(questId, targetObjId, selectedReward);
                break;
            case ActorActionType.TurnInDoodad:
                _ = QuestController.ReportDoodadTurnIn(questId, targetObjId, selectedReward);
                break;
            default:
                _ = QuestController.AutoTurnIn(questId, selectedReward);
                break;
        }

        // The report event drives the step machine; the world pipeline's
        // post-event advance is the last leg (completion path drops the quest
        // from ActiveQuests — terminal state, correct engine behavior).
        if (Character.Quests?.ActiveQuests.ContainsKey(questId) == true)
            _ = quest.RunCurrentStep();

        var completed = Character.Quests?.HasQuestCompleted(questId) == true;
        return Complete(request, completed, completed
            ? $"quest {questId} completed by turn-in"
            : $"quest {questId} turn-in executed (still active)");
    }

    #endregion

    #region B1 seams (typed, fail-closed — implementations land with the B1 milestone)

    public ActorRequest Interact(uint targetObjId, string? idempotencyKey = null)
        => NotImplementedSeam(ActorActionType.Interact, targetObjId, idempotencyKey);

    public ActorRequest Loot(uint corpseObjId, string? idempotencyKey = null)
        => NotImplementedSeam(ActorActionType.Loot, corpseObjId, idempotencyKey);

    public ActorRequest UseItem(uint itemId, string? idempotencyKey = null)
        => NotImplementedSeam(ActorActionType.UseItem, itemId, idempotencyKey);

    public ActorRequest Mount(uint mountObjId, string? idempotencyKey = null)
        => NotImplementedSeam(ActorActionType.Mount, mountObjId, idempotencyKey);

    public ActorRequest Dismount(string? idempotencyKey = null)
        => NotImplementedSeam(ActorActionType.Dismount, 0, idempotencyKey);

    /// <summary>
    /// Fail-closed seam behavior: the request walks the normal lifecycle
    /// (single-writer gate, audit emission) but is Rejected(RejectedAction)
    /// before any execution — the typed surface exists so controllers and
    /// tests bind against the B1 vocabulary today, and a call can never
    /// silently no-op or crash.
    /// </summary>
    private ActorRequest NotImplementedSeam(ActorActionType action, uint targetId, string? idempotencyKey)
    {
        var request = NewRequest(action, targetId, idempotencyKey: idempotencyKey);
        if (!TryBegin(request, "b1 seam"))
            return request;
        return Reject(request, ActorFailureReason.RejectedAction,
            $"B1 seam: {action} is not implemented in this slice (lands with the B1 milestone)");
    }

    #endregion

    #region Tick / movement

    private Vector3? _moveTarget;
    private float _moveSpeed;

    public void Tick(TimeSpan elapsed)
    {
        if (_active is not { IsTerminal: false } request)
            return;

        request.AddElapsed(elapsed);

        // Timeout enforcement on EVERY action that carries a budget — not
        // just movement. The §17 reason maps per action kind (Move →
        // Navigation; everything else → Starvation, budget exhaustion).
        if (request.Timeout is { } budget && request.Elapsed > budget)
        {
            Finish(request, request.Expire(ActorTimeoutPolicy.ReasonFor(request.Action),
                request.Action == ActorActionType.Move ? "navigation budget exceeded" : "action budget exceeded"));
            _moveTarget = null;
            return;
        }

        if (request.Action == ActorActionType.Move && _moveTarget is { } destination)
        {
            var position = Character.Transform.World.Position;
            var flatDistance = MathUtil.CalculateDistance(position, destination, false);
            var zDistance = Math.Abs(destination.Z - position.Z);

            if (flatDistance <= ArrivalRadius && zDistance <= ArrivalRadius)
            {
                Finish(request, request.Complete(detail: "arrived"));
                _moveTarget = null;
                return;
            }

            var step = Math.Min(_moveSpeed * (float)Math.Max(elapsed.TotalSeconds, 0.05), flatDistance);
            if (flatDistance > 0.0001f)
            {
                var angle = (float)MathUtil.CalculateAngleFrom(position, destination).DegToRad();
                var (newX, newY) = MathUtil.AddDistanceToFront(step, position.X, position.Y, angle);
                var fraction = step / flatDistance;
                var newZ = position.Z + (destination.Z - position.Z) * fraction;
                ApplyPosition(new Vector3(newX, newY, newZ));
            }
            else
            {
                var dir = destination.Z >= position.Z ? 1f : -1f;
                var zStep = Math.Min(step, zDistance);
                ApplyPosition(new Vector3(position.X, position.Y, position.Z + dir * zStep));
            }
        }
    }

    private void ApplyPosition(Vector3 next)
    {
        var transform = Character.Transform;
        var angle = (float)MathUtil.CalculateAngleFrom(transform.World.Position, next);
        transform.Local.SetRotationDegree(0f, 0f, angle - 90);
        transform.Local.SetPosition(next);
    }

    #endregion

    #region Internals

    private ActorRequest NewRequest(ActorActionType action, uint targetId,
        Vector3? destination = null, uint skillId = 0, TimeSpan? timeout = null, object? payload = null,
        string? idempotencyKey = null)
        => new(action, targetId, destination, skillId, timeout, payload, idempotencyKey);

    /// <summary>
    /// Single-writer gate: accepts the request as the new active one, or
    /// rejects it with StateTransition("busy") when another request is live.
    /// The busy request still walks the full lifecycle (Requested → Accepted
    /// → Rejected) so its audit record shows the complete transition log.
    /// </summary>
    private bool TryBegin(ActorRequest request, string what)
    {
        if (_active is { IsTerminal: false })
        {
            request.Accept(what);
            Reject(request, ActorFailureReason.StateTransition, $"actor busy with {_active.Action}");
            return false;
        }

        request.Accept(what);

        // Idempotency gate: an explicit key whose prior attempt ended in a
        // state that may have executed (Completed/Interrupted/TimedOut) is
        // NEVER re-executed — the duplicate is rejected pre-flight and its
        // audit record shows no Running transition (the roadmap's "retries
        // and timeouts cannot duplicate" guarantee). Rejected attempts are
        // retryable (v1 rejections all occur before engine execution). The
        // refusal is flagged so it cannot replace the locked outcome.
        if (!string.IsNullOrEmpty(request.IdempotencyKey)
            && _ledger.TryGetOutcome(request.IdempotencyKey, out var prior)
            && prior.Result != ActorLifecycleState.Rejected)
        {
            request.IsDedupeRejection = true;
            Reject(request, ActorFailureReason.StateTransition,
                $"duplicate idempotency key '{request.IdempotencyKey}' — original trace {prior.TraceId} " +
                $"already {prior.Result}; retry is not re-executed");
            return false;
        }

        _active = request;
        return true;
    }

    private void InterruptActive(string detail)
    {
        if (_active is { IsTerminal: false } request)
        {
            Finish(request, request.Interrupt(detail));
        }
        _moveTarget = null;
    }

    private ActorRequest Complete(ActorRequest request, string detail)
        => Complete(request, null, detail);

    private ActorRequest Complete(ActorRequest request, object? result, string detail)
    {
        Finish(request, request.Complete(result, detail));
        return request;
    }

    private ActorRequest Reject(ActorRequest request, ActorFailureReason reason, string detail)
    {
        Finish(request, request.Reject(reason, detail));
        return request;
    }

    private void Finish(ActorRequest request, bool transitioned)
    {
        if (!transitioned || request == null || !request.IsTerminal)
            return;
        // A dedupe refusal is not an attempt of its own: it must not
        // replace the original (possibly locked) outcome under the key.
        if (!request.IsDedupeRejection)
            _ledger.TryRecordOutcome(request.IdempotencyKey, request.TraceId, request.State, request.Failure);
        _trace.Add(new ActorAuditRecord(
            request.TraceId, ActorId, request.Action, request.TargetId,
            request.RequestedAtUtc, request.StartedAtUtc, request.CompletedAtUtc,
            request.State, request.Failure, request.Detail, request.StateChanges.ToList()));
        if (_trace.Count > MaxTraceRecords)
            _trace.RemoveRange(0, _trace.Count - MaxTraceRecords);
        if (ReferenceEquals(_active, request))
            _active = null;
    }

    public ActorAuditRecord? FindByKey(string idempotencyKey)
        => _ledger.TryGetOutcome(idempotencyKey, out var entry)
            ? _trace.LastOrDefault(r => r.TraceId == entry.TraceId)
            : null;

    private Unit? ResolveUnit(uint objId)
    {
        if (objId == 0)
            return null;
        // Real engine lookup: the owning world instance's unit registry
        // (WorldInstance._units — populated by AddObject for NPCs/characters).
        return Character.ParentWorld?.GetUnit(objId);
    }

    #endregion
}

/// <summary>Vector3.IsFinite extension (System.Numerics has no built-in).</summary>
internal static class VectorMath
{
    public static bool IsFinite(this Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>
/// §17 reason mapping for action timeouts. Every action supports a timeout
/// budget; the taxonomy reason is per action kind: movement timeouts are
/// Navigation failures, every other action that exceeds its budget is
/// Starvation (resource/budget exhaustion). No new reasons — only spec §17
/// vocabulary.
/// </summary>
public static class ActorTimeoutPolicy
{
    public static ActorFailureReason ReasonFor(ActorActionType action)
        => action == ActorActionType.Move ? ActorFailureReason.Navigation : ActorFailureReason.Starvation;
}
