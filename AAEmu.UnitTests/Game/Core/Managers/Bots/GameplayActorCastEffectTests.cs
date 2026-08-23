using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Static;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M7 hardening #4 — causal traces for the Cast action: action accepted →
/// effect observed → target state change → bounded timeout/failure reason.
///
/// The actor samples the resolved target's HP at cast acceptance, then runs
/// a bounded post-completion observation window (drained by Tick on the
/// execution boundary) that enriches the audit record's v2 additive fields:
/// target_hp_before / target_hp_after / effect_observed / effect_wait_ms.
/// The window discriminates a delayed effect (HP changed within the window)
/// from a failed hit (window expired, HP pinned — the fox anomaly
/// signature). Observation outcome NEVER changes the request Result: an
/// unobserved cast is still Completed.
/// </summary>
[NotInParallel]
public class GameplayActorCastEffectTests
{
    private static Npc SpawnedNpc(AAEmu.Game.Models.Game.Bots.HeadlessSession session, uint objId)
        => (Npc)session.World.GetUnit(objId);

    #region Effect observed

    [Test]
    public async Task Cast_EffectLandsWithinWindow_RecordsHpChangeAndAdditiveJson()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cast-effect-1");
        actor.EffectObservationWindow = TimeSpan.FromMilliseconds(500);
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1101);
        var npc = SpawnedNpc(session, npcObjId);

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, npcObjId);

        // The action itself completes synchronously — observation is a
        // bounded POST-completion window, not a second Running phase.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(SkillResult.Success);

        // Simulated DELAYED effect landing (ApplySkillTask fires after
        // UseSkill returned): the target's HP drops inside the window.
        npc.Hp = 70;
        actor.Tick(TimeSpan.FromMilliseconds(100));

        var record = actor.AuditTrace.Last(r => r.Action == ActorActionType.Cast && r.TraceId == request.TraceId);
        await Assert.That(record.TargetHpBefore).IsEqualTo(100);
        await Assert.That(record.TargetHpAfter).IsEqualTo(70);
        await Assert.That(record.EffectObserved).IsEqualTo(true);
        await Assert.That(record.EffectWait).IsEqualTo(TimeSpan.FromMilliseconds(100));
        // Observation success does not touch the lifecycle outcome either.
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();

        // Additive JSON contract (snake_case names are API contract).
        using var json = JsonDocument.Parse(record.ToJson());
        var root = json.RootElement;
        await Assert.That(root.TryGetProperty("target_hp_before", out var hpBefore) && hpBefore.GetInt32() == 100).IsTrue();
        await Assert.That(root.TryGetProperty("target_hp_after", out var hpAfter) && hpAfter.GetInt32() == 70).IsTrue();
        await Assert.That(root.TryGetProperty("effect_observed", out var observed) && observed.GetBoolean()).IsTrue();
        await Assert.That(root.TryGetProperty("effect_wait_ms", out var waitMs) && waitMs.GetDouble() == 100d).IsTrue();
    }

    #endregion

    #region No effect observed

    [Test]
    public async Task Cast_NoDamageWithinWindow_EffectObservedFalse_ResultStillCompleted()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cast-effect-2");
        actor.EffectObservationWindow = TimeSpan.FromMilliseconds(200);
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1102);
        var npc = SpawnedNpc(session, npcObjId);

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, npcObjId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        // Window expires with NO HP change (the fox pinned-HP signature):
        // three polls of 100 ms each cross the 200 ms window.
        actor.Tick(TimeSpan.FromMilliseconds(100));
        actor.Tick(TimeSpan.FromMilliseconds(100));
        actor.Tick(TimeSpan.FromMilliseconds(100));

        var record = actor.AuditTrace.Last(r => r.Action == ActorActionType.Cast && r.TraceId == request.TraceId);
        await Assert.That(record.TargetHpBefore).IsEqualTo(100);
        await Assert.That(record.TargetHpAfter).IsEqualTo(npc.Hp);
        await Assert.That(record.EffectObserved).IsEqualTo(false);
        // Observation failure ≠ action failure — asserted explicitly.
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(SkillResult.Success);
        await Assert.That(record.Failure).IsNull();

        using var json = JsonDocument.Parse(record.ToJson());
        var root = json.RootElement;
        await Assert.That(root.TryGetProperty("effect_observed", out var observed) && observed.ValueKind == JsonValueKind.False).IsTrue();
    }

    [Test]
    public async Task Cast_ObservationPending_DoesNotHoldTheSingleWriterGate()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cast-effect-3");
        actor.EffectObservationWindow = TimeSpan.FromSeconds(5);
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1103);

        var cast = actor.Cast(GameplayActorTestRig.TestSkillId, npcObjId);
        await Assert.That(cast.State).IsEqualTo(ActorLifecycleState.Completed);

        // The pending observation must not occupy the single-writer slot:
        // a new request is accepted immediately while the window drains in
        // the background.
        var observe = actor.Observe();
        await Assert.That(observe.Hp).IsGreaterThan(0);
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    #endregion

    #region Not measured

    [Test]
    public async Task Cast_ObservationDisabled_AdditiveFieldsStayNull()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cast-effect-4");
        actor.EffectObservationWindow = TimeSpan.Zero; // seam: disable observation

        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1104);
        var request = actor.Cast(GameplayActorTestRig.TestSkillId, npcObjId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        actor.Tick(TimeSpan.FromMilliseconds(100));

        var record = actor.AuditTrace.Last(r => r.Action == ActorActionType.Cast && r.TraceId == request.TraceId);
        await Assert.That(record.TargetHpBefore).IsNull();
        await Assert.That(record.TargetHpAfter).IsNull();
        await Assert.That(record.EffectObserved).IsNull();
        await Assert.That(record.EffectWait).IsNull();

        using var json = JsonDocument.Parse(record.ToJson());
        var root = json.RootElement;
        await Assert.That(root.TryGetProperty("target_hp_before", out var before) && before.ValueKind == JsonValueKind.Null).IsTrue();
        await Assert.That(root.TryGetProperty("target_hp_after", out var after) && after.ValueKind == JsonValueKind.Null).IsTrue();
        await Assert.That(root.TryGetProperty("effect_observed", out var observed) && observed.ValueKind == JsonValueKind.Null).IsTrue();
        await Assert.That(root.TryGetProperty("effect_wait_ms", out var waitMs) && waitMs.ValueKind == JsonValueKind.Null).IsTrue();
    }

    [Test]
    public async Task Cast_RejectedByEngine_NoObservationRegistered()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("cast-effect-5");

        // Engine refusal (unknown skill) — no cast executed, so nothing is
        // measured; the record carries no additive fields at all.
        var request = actor.Cast(123_456, actor.ActorId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        actor.Tick(TimeSpan.FromMilliseconds(100));

        var record = actor.AuditTrace.Single(r => r.TraceId == request.TraceId);
        await Assert.That(record.TargetHpBefore).IsNull();
        await Assert.That(record.EffectObserved).IsNull();
    }

    [Test]
    public async Task Cast_DeadTarget_NotObservable_FieldsStayNull()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cast-effect-6");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 1105);
        SpawnedNpc(session, npcObjId).Hp = 0; // dead target: nothing to measure

        var request = actor.Cast(GameplayActorTestRig.TestSkillId, npcObjId);
        if (request.State != ActorLifecycleState.Completed)
            return; // engine refused the dead-target cast — also fine, nothing measured

        actor.Tick(TimeSpan.FromMilliseconds(100));
        var record = actor.AuditTrace.Last(r => r.Action == ActorActionType.Cast && r.TraceId == request.TraceId);
        await Assert.That(record.EffectObserved).IsNull();
    }

    #endregion

    #region ToJson contract regression

    [Test]
    public async Task ToJson_V1KeysUnchanged_AndV2KeysArePurelyAdditive()
    {
        var traceId = Guid.NewGuid();
        var requested = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var started = requested.AddMilliseconds(10);
        var completed = requested.AddMilliseconds(20);
        var record = new ActorAuditRecord(
            traceId, 42, ActorActionType.Cast, 7, requested, started, completed,
            ActorLifecycleState.Completed, null, "detail", ["Requested", "Completed"]);

        using var json = JsonDocument.Parse(record.ToJson());
        var root = json.RootElement;

        // Exact key set: every v1 name identical + exactly the four v2
        // additive names. A rename here is an API contract break.
        var keys = root.EnumerateObject().Select(p => p.Name).ToArray();
        await Assert.That(keys).IsEquivalentTo(new[]
        {
            "trace_id", "actor_id", "action", "target_id", "requested_at", "started_at",
            "completed_at", "result", "failure", "detail", "state_changes",
            "target_hp_before", "target_hp_after", "effect_observed", "effect_wait_ms"
        });

        await Assert.That(root.GetProperty("trace_id").GetGuid()).IsEqualTo(traceId);
        await Assert.That(root.GetProperty("actor_id").GetUInt32()).IsEqualTo(42u);
        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo(nameof(ActorActionType.Cast));
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(7u);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo(nameof(ActorLifecycleState.Completed));
        await Assert.That(root.GetProperty("failure").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsEqualTo(2);
        // Unmeasured observations serialize as null (never fabricated).
        await Assert.That(root.GetProperty("target_hp_before").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("target_hp_after").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("effect_observed").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("effect_wait_ms").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ToJson_MeasuredObservation_SerializesV2Values()
    {
        var record = new ActorAuditRecord(
            Guid.NewGuid(), 1, ActorActionType.Cast, 2,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
            ActorLifecycleState.Completed, null, "ok", ["Requested", "Completed"])
        {
            // with-init assignment via object initializer works because the
            // positional parameters are init-only properties.
            TargetHpBefore = 100,
            TargetHpAfter = 70,
            EffectObserved = true,
            EffectWait = TimeSpan.FromMilliseconds(250)
        };

        using var json = JsonDocument.Parse(record.ToJson());
        var root = json.RootElement;
        await Assert.That(root.GetProperty("target_hp_before").GetInt32()).IsEqualTo(100);
        await Assert.That(root.GetProperty("target_hp_after").GetInt32()).IsEqualTo(70);
        await Assert.That(root.GetProperty("effect_observed").GetBoolean()).IsEqualTo(true);
        await Assert.That(root.GetProperty("effect_wait_ms").GetDouble()).IsEqualTo(250d);
    }

    #endregion
}
