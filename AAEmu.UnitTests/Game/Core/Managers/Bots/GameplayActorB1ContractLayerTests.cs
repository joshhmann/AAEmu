using System.Numerics;
using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// B1 action contract layer tests (t_cbbc1103) — the shared substrate the
/// B1 milestone's six actions (Interact · Loot · UseItem · Mount/Dismount ·
/// AcceptQuest · TurnInQuest) build on:
///  - idempotency/correlation: explicit-key retries never re-execute a
///    request that may have applied an effect (Completed/Interrupted/
///    TimedOut lock the key; Rejected attempts are retryable); the
///    effect-fingerprint ledger dedupes items/currency/labor/quest
///    credit/interactions at the B1 action layer;
///  - typed B1 seams: the five not-yet-implemented actions fail closed
///    (Rejected(RejectedAction) + audit record), never throw, never no-op;
///  - timeout support on every action (Move → Navigation, others →
///    Starvation, spec §17 only);
///  - trace record JSON form usable by the control-plane API.
/// </summary>
[NotInParallel]
public class GameplayActorB1ContractLayerTests
{
    #region Idempotency — retries never execute twice

    [Test]
    public async Task DuplicateKey_AfterCompletedOriginal_RejectedPreFlight_LockSurvivesThirdRetry()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-idem-1");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        // Original attempt with an explicit key: completes by walking.
        var original = actor.MoveTo(new Vector3(10, 0, 0), speed: 2f, idempotencyKey: "quest-turn-in:q42");
        var guard = 0;
        while (original.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Retry with the SAME key: refused BEFORE execution — the audit
        // record shows no Running transition (execution is the only place
        // effects can land), so the effect cannot duplicate.
        var retry = actor.MoveTo(new Vector3(10, 0, 0), speed: 2f, idempotencyKey: "quest-turn-in:q42");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains(original.TraceId.ToString())).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Rejected"))).IsTrue();

        // The lock SURVIVES the refused duplicate: a third retry is refused
        // too (the refusal must not overwrite the locked outcome).
        var third = actor.MoveTo(new Vector3(10, 0, 0), speed: 2f, idempotencyKey: "quest-turn-in:q42");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(third.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        // FindByKey correlates back to the ORIGINAL completed attempt.
        var byKey = actor.FindByKey("quest-turn-in:q42");
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(byKey.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task DuplicateKey_WhileOriginalRunning_RejectedAsBusy_NotDedupe()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-idem-2");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var first = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f, idempotencyKey: "k-busy");
        var retry = actor.MoveTo(new Vector3(5, 0, 0), speed: 1f, idempotencyKey: "k-busy");

        // The single-writer gate fires first: busy rejection, original still
        // Running and untouched.
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Detail?.Contains("busy")).IsTrue();

        // After the original completes, the key is locked (Completed).
        var guard = 0;
        while (first.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 200)
            actor.Tick(TimeSpan.FromSeconds(1));
        var lateRetry = actor.MoveTo(new Vector3(5, 0, 0), speed: 1f, idempotencyKey: "k-busy");
        await Assert.That(lateRetry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(lateRetry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(lateRetry.Detail?.Contains("duplicate idempotency key")).IsTrue();
    }

    [Test]
    public async Task RetryAfterRejected_IsAllowed_AndExecutes()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-idem-3");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        // First attempt is rejected BEFORE execution (invalid speed).
        var failed = actor.MoveTo(new Vector3(10, 0, 0), speed: 0f, idempotencyKey: "k-retryable");
        await Assert.That(failed.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(failed.Failure).IsEqualTo(ActorFailureReason.RejectedAction);

        // Retry with the same key: allowed (nothing executed), runs to
        // completion, and now locks the key.
        var retry = actor.MoveTo(new Vector3(10, 0, 0), speed: 5f, idempotencyKey: "k-retryable");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Running);
        var guard = 0;
        while (retry.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Completed);

        var third = actor.MoveTo(new Vector3(10, 0, 0), speed: 5f, idempotencyKey: "k-retryable");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(third.Detail?.Contains("duplicate idempotency key")).IsTrue();
    }

    [Test]
    public async Task InterruptedOriginal_LocksKey()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-idem-4");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f, idempotencyKey: "k-interrupted");
        actor.Stop();
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Interrupted);

        // Interrupted = execution may have started: the key locks.
        var retry = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f, idempotencyKey: "k-interrupted");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains(move.TraceId.ToString())).IsTrue();
    }

    [Test]
    public async Task TimedOutOriginal_LocksKey()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-idem-5");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(100, 0, 0), speed: 1f, timeout: TimeSpan.FromMilliseconds(100), idempotencyKey: "k-timedout");
        actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.TimedOut);

        // TimedOut = the attempt ran and its effect state is unknown: lock.
        var retry = actor.MoveTo(new Vector3(100, 0, 0), speed: 1f, idempotencyKey: "k-timedout");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains(move.TraceId.ToString())).IsTrue();
    }

    [Test]
    public async Task UnkeyedIdenticalRequests_BothExecute_NoAccidentalDedupe()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-idem-6");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        // Without an explicit key there is NO retry semantics: identical
        // requests are new logical operations and always execute.
        var first = actor.MoveTo(new Vector3(5, 0, 0), speed: 5f);
        var guard = 0;
        while (first.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));
        var second = actor.MoveTo(new Vector3(5, 0, 0), speed: 5f);

        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(second.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace.All(r => r.Result == ActorLifecycleState.Completed)).IsTrue();
    }

    [Test]
    public async Task FindByKey_UnknownKey_ReturnsNull()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-idem-7");
        await Assert.That(actor.FindByKey("never-used")).IsNull();
    }

    #endregion

    #region Idempotency — effect fingerprint ledger (B1 action layer)

    [Test]
    public async Task EffectLedger_DedupeFingerprints_ItemCurrencyLaborQuestCreditInteractions()
    {
        // The B1 action layer's guarantee: after applying an effect the
        // implementation records its fingerprint; a re-run proves the effect
        // is already applied and must not apply twice.
        var ledger = new ActorEffectLedger();
        var traceA = Guid.NewGuid();
        var traceB = Guid.NewGuid();

        // Item grant (item template 100, count 5).
        var itemFp = ActorIdempotency.EffectKey("item", 100, "5");
        await Assert.That(ledger.RecordEffect(itemFp, traceA)).IsTrue();
        await Assert.That(ledger.IsEffectApplied(itemFp)).IsTrue();
        await Assert.That(ledger.TryGetEffectTrace(itemFp, out var appliedBy)).IsTrue();
        await Assert.That(appliedBy).IsEqualTo(traceA);

        // A retry/timeout re-run must NOT apply again.
        await Assert.That(ledger.RecordEffect(itemFp, traceB)).IsFalse();

        // Currency change, labor spend, quest credit, interaction — each its
        // own fingerprint, each dedupeable.
        await Assert.That(ledger.RecordEffect(ActorIdempotency.EffectKey("currency", 0, "gold:-500"), traceA)).IsTrue();
        await Assert.That(ledger.RecordEffect(ActorIdempotency.EffectKey("labor", 0, "-10"), traceA)).IsTrue();
        await Assert.That(ledger.RecordEffect(ActorIdempotency.EffectKey("questcredit", 42), traceA)).IsTrue();
        await Assert.That(ledger.RecordEffect(ActorIdempotency.EffectKey("interaction", 90001), traceA)).IsTrue();
        await Assert.That(ledger.RecordEffect(ActorIdempotency.EffectKey("item", 100, "5"), traceB)).IsFalse();
        await Assert.That(ledger.RecordEffect(ActorIdempotency.EffectKey("interaction", 90001), traceB)).IsFalse();
    }

    [Test]
    public async Task EffectLedger_Bounded_EvictsOldestOutcomes()
    {
        var ledger = new ActorEffectLedger();
        for (var i = 0; i < ActorEffectLedger.MaxRecords + 8; i++)
            ledger.TryRecordOutcome($"key-{i}", Guid.NewGuid(), ActorLifecycleState.Completed, null);

        await Assert.That(ledger.OutcomeCount).IsEqualTo(ActorEffectLedger.MaxRecords);
        // Oldest distinct keys evicted, newest retained.
        await Assert.That(ledger.TryGetOutcome("key-0", out _)).IsFalse();
        await Assert.That(ledger.TryGetOutcome("key-2", out _)).IsFalse();
        await Assert.That(ledger.TryGetOutcome($"key-{ActorEffectLedger.MaxRecords + 7}", out _)).IsTrue();
    }

    [Test]
    public async Task EffectLedger_ReRecordedKey_NotDoubleQueued_EvictionStaysCorrect()
    {
        var ledger = new ActorEffectLedger();
        // Re-record a key many times: it stays a single FIFO slot, so the
        // NEWEST keys are never evicted by a stale duplicate queue entry.
        for (var i = 0; i < ActorEffectLedger.MaxRecords + 4; i++)
            ledger.TryRecordOutcome("hot-key", Guid.NewGuid(), ActorLifecycleState.Completed, null);
        await Assert.That(ledger.TryGetOutcome("hot-key", out _)).IsTrue();
        await Assert.That(ledger.OutcomeCount).IsEqualTo(1);
    }

    [Test]
    public async Task IdempotencyKey_Derivation_DeterministicAndDistinct()
    {
        // Deterministic: same inputs ⇒ same key, across calls.
        await Assert.That(ActorIdempotency.Key(ActorActionType.Move, 1, 0))
            .IsEqualTo(ActorIdempotency.Key(ActorActionType.Move, 1, 0));

        // Distinct: action / target / skill / payload all separate keys.
        await Assert.That(ActorIdempotency.Key(ActorActionType.Move, 1, 0))
            .IsNotEqualTo(ActorIdempotency.Key(ActorActionType.Move, 2, 0));
        await Assert.That(ActorIdempotency.Key(ActorActionType.Move, 1, 0))
            .IsNotEqualTo(ActorIdempotency.Key(ActorActionType.Cast, 1, 0));
        await Assert.That(ActorIdempotency.Key(ActorActionType.AcceptQuest, 5, 0))
            .IsNotEqualTo(ActorIdempotency.Key(ActorActionType.AcceptQuest, 5, 0,
                new QuestAcceptParams(QuestAcceptorType.Npc, 1)));
        await Assert.That(ActorIdempotency.Key(ActorActionType.AcceptQuest, 5, 0,
                new QuestAcceptParams(QuestAcceptorType.Npc, 1)))
            .IsEqualTo(ActorIdempotency.Key(ActorActionType.AcceptQuest, 5, 0,
                new QuestAcceptParams(QuestAcceptorType.Npc, 1)));
    }

    #endregion

    #region B1 typed seams — fail closed

    [Test]
    public async Task SeamActions_FailClosed_RejectedWithAuditRecord_NoThrow()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-seam-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 2000);

        var interact = actor.Interact(npcObjId);
        var loot = actor.Loot(npcObjId);
        var useItem = actor.UseItem(1234);
        var mount = actor.Mount(npcObjId);
        var dismount = actor.Dismount();

        foreach (var request in new[] { interact, loot, useItem, mount, dismount })
        {
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
            await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
            await Assert.That(request.Detail?.Contains("B1 seam")).IsTrue();
            await Assert.That(request.Detail?.Contains("not implemented in this slice")).IsTrue();
        }

        // Every seam call emitted a full audit record; the actor is idle.
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(5);
        await Assert.That(actor.AuditTrace.All(r => r.Result == ActorLifecycleState.Rejected)).IsTrue();
        await Assert.That(actor.AuditTrace.Select(r => r.Action))
            .IsEquivalentTo(new[] { ActorActionType.Interact, ActorActionType.Loot, ActorActionType.UseItem, ActorActionType.Mount, ActorActionType.Dismount });
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task SeamAction_WhileBusy_RejectedStateTransition()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-seam-2");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));

        var move = actor.MoveTo(new Vector3(50, 0, 0), speed: 1f);
        var interact = actor.Interact(1);

        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(interact.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(interact.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(interact.Detail?.Contains("busy")).IsTrue();
    }

    [Test]
    public async Task SeamEnum_Values_AreStableContract()
    {
        // Enum values are contract (audit output / control-plane API): the
        // B1 seams must not shift the v1 vocabulary.
        await Assert.That((byte)ActorActionType.Observe).IsEqualTo((byte)0);
        await Assert.That((byte)ActorActionType.Move).IsEqualTo((byte)1);
        await Assert.That((byte)ActorActionType.Stop).IsEqualTo((byte)2);
        await Assert.That((byte)ActorActionType.Target).IsEqualTo((byte)3);
        await Assert.That((byte)ActorActionType.Cast).IsEqualTo((byte)4);
        await Assert.That((byte)ActorActionType.AutoTurnIn).IsEqualTo((byte)9);
        await Assert.That((byte)ActorActionType.Interact).IsEqualTo((byte)10);
        await Assert.That((byte)ActorActionType.Loot).IsEqualTo((byte)11);
        await Assert.That((byte)ActorActionType.UseItem).IsEqualTo((byte)12);
        await Assert.That((byte)ActorActionType.Mount).IsEqualTo((byte)13);
        await Assert.That((byte)ActorActionType.Dismount).IsEqualTo((byte)14);
    }

    #endregion

    #region Timeout support on every action

    [Test]
    public async Task TimeoutPolicy_MapsMoveToNavigation_EverythingElseToStarvation()
    {
        // MoveToUnit is ActorActionType.Move by construction, so the same
        // mapping covers both movement entry points.
        await Assert.That(ActorTimeoutPolicy.ReasonFor(ActorActionType.Move))
            .IsEqualTo(ActorFailureReason.Navigation);
        await Assert.That(ActorTimeoutPolicy.ReasonFor(ActorActionType.Cast))
            .IsEqualTo(ActorFailureReason.Starvation);
        await Assert.That(ActorTimeoutPolicy.ReasonFor(ActorActionType.Interact))
            .IsEqualTo(ActorFailureReason.Starvation);
        await Assert.That(ActorTimeoutPolicy.ReasonFor(ActorActionType.Loot))
            .IsEqualTo(ActorFailureReason.Starvation);
        await Assert.That(ActorTimeoutPolicy.ReasonFor(ActorActionType.AcceptQuest))
            .IsEqualTo(ActorFailureReason.Starvation);
        // Only spec §17 vocabulary — no invented reasons.
        await Assert.That(Enum.GetValues<ActorFailureReason>())
            .IsEquivalentTo(new[]
            {
                ActorFailureReason.None, ActorFailureReason.WrongDecision, ActorFailureReason.Navigation,
                ActorFailureReason.RejectedAction, ActorFailureReason.StateTransition,
                ActorFailureReason.Persistence, ActorFailureReason.Starvation, ActorFailureReason.FidelityError
            });
    }

    #endregion

    #region Trace record — control-plane API form

    [Test]
    public async Task AuditRecord_ToJson_ExposesStableControlPlaneShape()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-json-1");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 3000);

        actor.SetTarget(npcObjId);
        var record = actor.AuditTrace[0];
        var json = record.ToJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ROADMAP M5 field names, snake_case.
        await Assert.That(root.TryGetProperty("trace_id", out var traceIdProp)).IsTrue();
        await Assert.That(traceIdProp.GetGuid()).IsEqualTo(record.TraceId);
        await Assert.That(root.TryGetProperty("actor_id", out var actorIdProp)).IsTrue();
        await Assert.That(actorIdProp.GetUInt32()).IsEqualTo(GameplayActorTestRig.ActorObjId);
        await Assert.That(root.GetProperty("action").GetString()).IsEqualTo("Target");
        await Assert.That(root.GetProperty("target_id").GetUInt32()).IsEqualTo(npcObjId);
        await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
        await Assert.That(root.GetProperty("failure").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("detail").GetString()).IsNotEmpty();
        var stateChanges = root.GetProperty("state_changes");
        await Assert.That(stateChanges.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(stateChanges.GetArrayLength()).IsGreaterThanOrEqualTo(3);
        // Oldest first: Requested → … → Completed.
        await Assert.That(stateChanges[0].GetString()).IsEqualTo("Requested");
        await Assert.That(stateChanges[stateChanges.GetArrayLength() - 1].GetString()!.Contains("Completed")).IsTrue();
    }

    [Test]
    public async Task AuditRecord_ToJson_RejectedRecordCarriesFailure()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-json-2");
        actor.Cast(123_456, GameplayActorTestRig.ActorObjId);
        var record = actor.AuditTrace[0];

        using var doc = JsonDocument.Parse(record.ToJson());
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Rejected");
        await Assert.That(root.GetProperty("failure").GetString()).IsEqualTo("RejectedAction");
        await Assert.That(root.GetProperty("detail").GetString()!.Contains("unknown skill")).IsTrue();
    }

    #endregion
}
