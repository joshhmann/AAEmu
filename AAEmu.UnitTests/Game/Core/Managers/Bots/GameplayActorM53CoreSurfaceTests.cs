using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Skills.Static;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.3 core-surface contract tests (t_c73d6293) — canonical verification
/// of the four actions on the IGameplayActor surface per ROADMAP M5.3:
///   - Observe (REQ-M5.3-2): one unified snapshot through REAL engine
///     queries only (WorldManager.GetAround region lists + character
///     state), no packets, emits the audit record, completes immediately.
///   - Stop (REQ-M5.3-4): VERIFY-ONLY — implementation owned by the Move
///     rework card (t_3cac48d4). This suite pins the CONTRACT: interrupts
///     a running request (Interrupted, "stop requested") and completes
///     itself; no-op when idle (idempotent).
///   - Target (REQ-M5.3-5): SetTarget through the real engine targeting
///     path (Unit.CurrentTarget exact assignment); unknown objId →
///     Rejected(RejectedAction).
///   - Cast (REQ-M5.3-6): ONE skill through Character.UseSkill (the same
///     call CSStartSkillPacket's learned-skill branch makes); validation
///     gates: template exists, character knows the skill, target resolves;
///     engine refusal → Rejected(RejectedAction); one skill per request.
///
/// Carried: REQ-M5.3-8 (Cast never double-casts — request-key dedupe
/// PRIMARY + engine-true backstop: cooldown consumed), REQ-M5.3-9 (audit
/// shape), REQ-M5.3-10 (contract tests independent of any controller).
///
/// Every assertion rides the canonical rig (GameplayActorTestRig) directly
/// — no controller, no scheduler, no packets. H stays UNKNOWN (never
/// inferred from scripted evidence).
/// </summary>
[NotInParallel]
public class GameplayActorM53CoreSurfaceTests
{
    #region Observe (REQ-M5.3-2)

    /// <summary>
    /// E2: the snapshot's region lists equal DIRECT WorldManager query
    /// results — the actor must read the region graph, not fabricate.
    /// </summary>
    [Test]
    public async Task Observe_SnapshotEqualsDirectWorldManagerQueries()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m53-observe-1");
        var pos = new Vector3(10, 20, 30);
        GameplayActorTestRig.SetPosition(actor, pos);

        // Place the actor + an NPC into the region grid the way the engine
        // does (AddVisibleObject → Region.AddObject), so the nearby-list
        // query has real data to read.
        var region = session.World.GetRegionByPos(pos);
        if (region == null)
            Assert.Fail("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);
        var npc = session.World.GetNpc(npcObjId);
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new Vector3(10.5f, 20, 30));
        region.AddObject(npc);
        npc.Region = region;

        var observation = actor.Observe();

        // Snapshot equals the DIRECT engine query at the same moment.
        // (IsEquivalentTo — TUnit collection equality; the values are the
        // same objIds, order is the region graph's scan order.)
        var directNpcs = AAEmu.Game.Core.Managers.World.WorldManager
            .GetAround<AAEmu.Game.Models.Game.NPChar.Npc>(actor.Character, 25f)
            .Select(n => n.ObjId).ToList();
        await Assert.That(observation.NearbyNpcObjIds).IsEquivalentTo(directNpcs);
        await Assert.That(observation.NearbyNpcObjIds.Contains(npcObjId)).IsTrue();

        // The observation itself is a snapshot, not a live handle.
        var directCharacters = AAEmu.Game.Core.Managers.World.WorldManager
            .GetAround<AAEmu.Game.Models.Game.Char.Character>(actor.Character, 25f)
            .Select(c => c.ObjId).ToList();
        await Assert.That(observation.NearbyCharacterObjIds).IsEquivalentTo(directCharacters);
    }

    /// <summary>
    /// E2: Observe emits exactly one audit record, Completed, and completes
    /// immediately (the record's StartedAt/CompletedAt are set).
    /// </summary>
    [Test]
    public async Task Observe_EmitsAuditRecord_CompletedImmediately()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-observe-2");

        var observation = actor.Observe();

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Observe);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StartedAtUtc).IsNotNull();
        await Assert.That(record.CompletedAtUtc).IsNotNull();
        // v1 Observe walks the full lifecycle Requested → Accepted → Running
        // → Completed (request.Start("query") then immediate Complete — the
        // lifecycle law: no Completed record skips Running). Still a query:
        // no engine state is mutated and it completes within the call.
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
        await Assert.That(observation.ActorId).IsEqualTo(actor.ActorId);
    }

    #endregion

    #region Stop (REQ-M5.3-4 — VERIFY ONLY, implementation owned by the Move card)

    /// <summary>
    /// E4: a Stop issued while a Move is running interrupts the Move
    /// (Interrupted, "stop requested") and completes itself.
    /// </summary>
    [Test]
    public async Task Stop_RunningMove_InterruptedAndStopCompletes()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-stop-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        var stop = actor.Stop();

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(move.Detail?.Contains("stop requested")).IsTrue();
        await Assert.That(stop.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.ActiveRequest).IsNull();
        // Two terminal records: Move Interrupted + Stop Completed.
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move && r.Result == ActorLifecycleState.Interrupted)).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Stop && r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    /// <summary>
    /// E4: a second Stop when idle is a no-op — it still completes (every
    /// action emits a terminal record) but nothing is interrupted.
    /// </summary>
    [Test]
    public async Task Stop_WhenIdle_CompletesAsNoOp()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-stop-2");

        var first = actor.Stop();
        var second = actor.Stop();

        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.ActiveRequest).IsNull();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        // No interrupted action between them.
        await Assert.That(actor.AuditTrace.Any(r => r.Result == ActorLifecycleState.Interrupted)).IsFalse();
    }

    #endregion

    #region Target (REQ-M5.3-5)

    /// <summary>
    /// E5: SetTarget assigns Unit.CurrentTarget through the real engine
    /// targeting path (the exact assignment CSChangeTargetPacket performs),
    /// and the subsequent Observe reflects the target.
    /// </summary>
    [Test]
    public async Task SetTarget_ValidUnit_SetsCurrentTarget_ObserveReflects()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m53-target-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);

        var request = actor.SetTarget(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.CurrentTarget).IsNotNull();
        await Assert.That(actor.Character.CurrentTarget!.ObjId).IsEqualTo(npcObjId);
        await Assert.That(actor.Observe().CurrentTargetObjId).IsEqualTo(npcObjId);
        // Target emits exactly one audit record (before the Observe appends).
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace[0].Action).IsEqualTo(ActorActionType.Target);
        await Assert.That(actor.AuditTrace[0].Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    /// <summary>
    /// E5: unknown objId → Rejected(RejectedAction); CurrentTarget unchanged.
    /// </summary>
    [Test]
    public async Task SetTarget_UnknownUnit_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-target-2");

        var request = actor.SetTarget(999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    #endregion

    #region Cast (REQ-M5.3-6 + REQ-M5.3-8)

    /// <summary>
    /// E6: a learned skill executes through the REAL Character.UseSkill
    /// engine path (Success), and the request completes.
    /// </summary>
    [Test]
    public async Task Cast_LearnedSkill_CompletesThroughRealEnginePath()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-cast-1");

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(SkillResult.Success);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Cast);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.TargetId).IsEqualTo(actor.ActorId);
        // The real skill pipeline ran: the request carried a Running state.
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();
    }

    /// <summary>
    /// E6 gate 1: unknown skill template → Rejected(RejectedAction).
    /// </summary>
    [Test]
    public async Task Cast_UnknownSkill_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-cast-2");

        var request = actor.Cast(123_456, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("unknown skill")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    /// <summary>
    /// E6 gate 2: character does not know the skill → Rejected(RejectedAction).
    /// </summary>
    [Test]
    public async Task Cast_NotLearned_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-cast-3");
        actor.Character.Skills.Skills.Clear();

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not learned")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    /// <summary>
    /// E6 gate 3: target does not resolve → Rejected(RejectedAction).
    /// </summary>
    [Test]
    public async Task Cast_UnknownTarget_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-cast-4");

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, 999_999);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("cast target not found")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    /// <summary>
    /// E8 (PRIMARY guard): a same-key retry of a Completed Cast is refused
    /// PRE-FLIGHT (Rejected(StateTransition)) and the engine is never
    /// re-entered — no second cast can ever land.
    /// </summary>
    [Test]
    public async Task Cast_SameKeyRetry_NeverDoubleCasts()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-cast-5");

        var original = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId, idempotencyKey: "m53-cast:1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);

        var retry = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId, idempotencyKey: "m53-cast:1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        // The duplicate never started — no Running transition on the retry
        // record, and the engine was entered exactly once (one Cast record).
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Cast)).IsEqualTo(2); // original + refused retry
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Cast && r.Result == ActorLifecycleState.Completed)).IsEqualTo(1);

        // A THIRD retry is still refused — the refusal never replaced the lock.
        var third = actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId, idempotencyKey: "m53-cast:1");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Cast && r.Result == ActorLifecycleState.Completed)).IsEqualTo(1);
    }

    /// <summary>
    /// E8 (engine-true BACKSTOP): a skill with a real cooldown is refused
    /// by the ENGINE on a fresh-key second cast (CooldownTime consumed) —
    /// the engine-true backstop behind the request-key dedupe.
    /// </summary>
    [Test]
    public async Task Cast_EngineCooldown_RefusesFreshKeySecondCast()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("m53-cast-6");
        var cooldownSkillId = SeedCooldownSkill(actor);

        var first = actor.Cast(cooldownSkillId, actor.ActorId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(first.Result).IsEqualTo(SkillResult.Success);

        // Fresh key — the request-level dedupe does NOT apply; the ENGINE's
        // cooldown gate must refuse (Skill.Use: CheckCooldown → CooldownTime).
        var second = actor.Cast(cooldownSkillId, actor.ActorId);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(second.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(second.Detail?.Contains("refused")).IsTrue();
        await Assert.That(second.Detail?.Contains("CooldownTime")).IsTrue();
    }

    /// <summary>
    /// Seeds a skill template with a REAL cooldown into SkillManager
    /// (additive, missing-only — never replaces an existing template), and
    /// learns it on the character. Local to this suite so the shared rig's
    /// zero-cooldown Cast surface is untouched.
    /// </summary>
    private static uint SeedCooldownSkill(GameplayActor actor)
    {
        const uint skillId = 90003;
        var manager = AAEmu.Game.Core.Managers.SkillManager.Instance;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var skillsField = typeof(AAEmu.Game.Core.Managers.SkillManager).GetField("_skills", flags)!;
        var skills = (Dictionary<uint, AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate>)skillsField.GetValue(manager)!;
        if (!skills.ContainsKey(skillId))
        {
            skills[skillId] = new AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate
            {
                Id = skillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 5000,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target
            };
        }

        if (!actor.Character.Skills.Skills.ContainsKey(skillId))
            actor.Character.Skills.AddSkill(skills[skillId], 1, false);
        return skillId;
    }

    #endregion

    #region Audit shape (REQ-M5.3-9)

    /// <summary>
    /// E9: every action emits the structured record
    /// {trace_id, actor_id, action, target_id, requested_at, started_at,
    /// completed_at, result, state_changes} — pinned per action kind.
    /// </summary>
    [Test]
    public async Task AuditRecord_ShapePerAction()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("m53-audit-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1000);

        actor.Observe();
        actor.MoveTo(new Vector3(20, 0, 0), speed: 2f);
        actor.Stop();
        actor.SetTarget(npcObjId);
        actor.Cast(GameplayActorTestRig.TestSkillId, actor.ActorId);

        // Observe(1) + Move(2) + Stop(3) + Target(4) + Cast(5).
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(5);
        // IsEquivalentTo: TUnit collection matcher (order-insensitive); the
        // per-position order is covered by the exit scenario segment.
        await Assert.That(actor.AuditTrace.Select(r => r.Action)).IsEquivalentTo(
            new[] { ActorActionType.Observe, ActorActionType.Move, ActorActionType.Stop, ActorActionType.Target, ActorActionType.Cast });

        foreach (var record in actor.AuditTrace)
        {
            await Assert.That(record.TraceId).IsNotEqualTo(Guid.Empty);
            await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
            await Assert.That(record.RequestedAtUtc <= record.StartedAtUtc).IsTrue();
            await Assert.That(record.StartedAtUtc <= record.CompletedAtUtc).IsTrue();
            await Assert.That(record.StateChanges.Count >= 3).IsTrue(); // Requested → Accepted → terminal
            // Stable JSON form parses with the M5 field names.
            using var doc = System.Text.Json.JsonDocument.Parse(record.ToJson());
            var root = doc.RootElement;
            await Assert.That(root.TryGetProperty("trace_id", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("actor_id", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("action", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("target_id", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("requested_at", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("started_at", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("completed_at", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("result", out _)).IsTrue();
            await Assert.That(root.TryGetProperty("state_changes", out _)).IsTrue();
        }
    }

    #endregion
}
