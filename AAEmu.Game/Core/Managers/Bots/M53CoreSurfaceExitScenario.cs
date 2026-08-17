using System.Numerics;

using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// M5.3 EXIT scenario (t_c73d6293, REQ-M5.3-11) — a scripted actor
/// completes the curated core-surface segment
///
///     observe → move → stop → target → cast
///
/// through the REAL engine paths (WorldManager queries, the movement
/// pipeline, Unit.CurrentTarget assignment, Character.UseSkill), and the
/// run produces a machine-readable trace: every request, transition,
/// result, and failure (ActorAuditRecord.ToJson — the M5 trace shape).
///
/// This is contract-level: the scenario drives the IGameplayActor surface
/// (no controller, no scheduler, no packets). It is implementation-agnostic
/// over the Move/Stop internals (owned by the Move rework card t_3cac48d4)
/// — it asserts lifecycle + audit semantics, never movement internals.
/// H stays UNKNOWN — scripted evidence is proxy/bot-functional only.
/// </summary>
public static class M53CoreSurfaceExitScenario
{
    public const string ScenarioName = "m5.3-core-surface-exit";

    /// <summary>Move leg: far enough that one Tick cannot arrive (speed 2 → 200 s to cover 400 u).</summary>
    private static readonly Vector3 MoveDestination = new(400, 0, 0);

    /// <summary>
    /// The rig's seeded learned skill (GameplayActorTestRig.TestSkillId,
    /// 90001 — zero mana/cooldown, CastingTime 0, Self target). The rig
    /// seeds this into SkillManager and learns it on the character; the
    /// scenario casts it through the real Character.UseSkill pipeline.
    /// Kept as a local constant so the Game-layer scenario never depends
    /// on the UnitTests rig type.
    /// </summary>
    public const uint TestCastSkillId = 90001;

    /// <summary>
    /// Runs the segment on an embodied character. <paramref name="world"/>
    /// resolves the cast/target NPC (fixture rig or live world adapter).
    /// </summary>
    public static BotScenarioRunner.ScenarioRunResult Run(Character character, BotScenarioRunner.IScenarioWorldAdapter world)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(world);

        var actor = new GameplayActor(character);
        var rigNotes = new List<string>();
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();

        try
        {
            // ------------------------------------------------ 1. OBSERVE
            var observation = actor.Observe();
            traceRecords.Add(actor.AuditTrace.Last());
            stages.Add(Stage("OBSERVE", 0, actor.AuditTrace.Last()));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "observe-completed", observation.ActorId == actor.ActorId && actor.AuditTrace.Last().Result == ActorLifecycleState.Completed,
                $"observe snapshot actor={observation.ActorId} pos={observation.Position}"));

            // ------------------------------------------------ 2. MOVE
            var move = actor.MoveTo(MoveDestination, speed: 2f, timeout: TimeSpan.FromSeconds(30));
            traceRecords.AddRange(NewRecords(actor, traceRecords.Count));
            stages.Add(Stage("MOVE", 0, move));

            // One tick: the leg is mid-flight (far destination — cannot
            // arrive in a single 1 s tick at speed 2).
            actor.Tick(TimeSpan.FromSeconds(1));
            var moving = move.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running;
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "move-running", moving,
                $"move request {move.State} after 1 s tick (detail: {move.Detail ?? "n/a"})"));

            // ------------------------------------------------ 3. STOP
            var stop = actor.Stop();
            traceRecords.AddRange(NewRecords(actor, traceRecords.Count));
            stages.Add(Stage("STOP", 0, stop));

            var moveTerminal = move.IsTerminal;
            var stopCompleted = stop.State == ActorLifecycleState.Completed;
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "stop-interrupted-move", moveTerminal && stopCompleted,
                $"move {move.State}{(move.Detail is { } d ? $" ({d})" : "")}; stop {stop.State}"));

            // ------------------------------------------------ 4. TARGET
            var npcObjId = world.ResolveNpcObjId(1000);
            var target = actor.SetTarget(npcObjId);
            traceRecords.AddRange(NewRecords(actor, traceRecords.Count));
            stages.Add(Stage("TARGET", npcObjId, target));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "target-set", target.State == ActorLifecycleState.Completed && character.CurrentTarget?.ObjId == npcObjId,
                $"target {target.State}; CurrentTarget={(character.CurrentTarget?.ObjId ?? 0)}"));

            // ------------------------------------------------ 5. CAST
            var cast = actor.Cast(TestCastSkillId, npcObjId);
            traceRecords.AddRange(NewRecords(actor, traceRecords.Count));
            stages.Add(Stage("CAST", npcObjId, cast));
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "cast-completed", cast.State == ActorLifecycleState.Completed,
                $"cast {cast.State} (detail: {cast.Detail ?? "n/a"})"));

            // ------------------------------------------------ VERIFY
            // The five audit records, in segment order, each carrying the
            // full lifecycle transition log.
            var lifecycle = AssertTraceCompleteness(traceRecords, out var lifecycleDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("lifecycle-trace-complete", lifecycle, lifecycleDetail));

            var actionOrder = string.Join(" → ", traceRecords.Select(r => r.Action.ToString()));
            var orderOk = traceRecords.Select(r => r.Action).SequenceEqual(
                new[] { ActorActionType.Observe, ActorActionType.Move, ActorActionType.Stop, ActorActionType.Target, ActorActionType.Cast });
            criteria.Add(new BotScenarioRunner.CriterionVerdict(
                "segment-order", orderOk,
                orderOk ? $"segment order: {actionOrder}" : $"WRONG segment order: {actionOrder}"));

            var passed = criteria.All(c => c.Passed);
            return new BotScenarioRunner.ScenarioRunResult
            {
                Template = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                FailReason = passed ? "" : string.Join("; ", criteria.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}")),
                RigNotes = rigNotes,
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                TraceRecords = traceRecords,
                ActorRequests = traceRecords.Count
            };
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", rigNotes, stages, criteria, traceRecords);
        }
    }

    /// <summary>New audit records emitted since the last snapshot (allows multi-record actions).</summary>
    private static IEnumerable<ActorAuditRecord> NewRecords(GameplayActor actor, int alreadyRecorded)
        => actor.AuditTrace.Skip(alreadyRecorded).ToList();

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, uint target, ActorRequest request)
        => new(name, 1, request.State.ToString(), target.ToString(), request.Detail ?? "");

    /// <summary>Stage verdict from an audit record (observation stages).</summary>
    private static BotScenarioRunner.ScenarioStageVerdict Stage(string name, uint target, ActorAuditRecord record)
        => new(name, 1, record.Result.ToString(), target.ToString(), record.Detail ?? "");

    /// <summary>
    /// Lifecycle correctness: every Completed action carries Requested →
    /// Accepted → Completed (Target/Observe are immediate — no Running);
    /// execution actions (Move/Stop/Cast) additionally carry Running.
    /// Interrupted records (the stop's interrupt of the Move leg) are
    /// legitimate terminal states and are not judged by the Completed bar.
    /// </summary>
    private static bool AssertTraceCompleteness(List<ActorAuditRecord> records, out string detail)
    {
        var incomplete = records
            .Where(r => r.Result == ActorLifecycleState.Completed)
            .Where(r => r.StateChanges.Count == 0 ||
                        !r.StateChanges.Any(s => s.Contains("Requested")) ||
                        !r.StateChanges.Any(s => s.Contains("Accepted")) ||
                        !r.StateChanges.Any(s => s.Contains("Completed")) ||
                        (r.Action != ActorActionType.Target && r.Action != ActorActionType.Observe &&
                         !r.StateChanges.Any(s => s.Contains("Running"))))
            .ToList();
        detail = $"records={records.Count} completed={records.Count(r => r.Result == ActorLifecycleState.Completed)} incomplete={incomplete.Count}";
        return records.Count == 5 && incomplete.Count == 0;
    }

    private static BotScenarioRunner.ScenarioRunResult Fail(
        string reason, List<string> rigNotes,
        List<BotScenarioRunner.ScenarioStageVerdict> stages,
        List<BotScenarioRunner.CriterionVerdict> criteria,
        List<ActorAuditRecord> traceRecords)
        => new()
        {
            Template = ScenarioName,
            Passed = false,
            FailStage = "RUN",
            FailReason = reason,
            RigNotes = rigNotes,
            Gates = [],
            Stages = stages,
            Criteria = criteria,
            TraceRecords = traceRecords,
            ActorRequests = traceRecords.Count
        };
}
