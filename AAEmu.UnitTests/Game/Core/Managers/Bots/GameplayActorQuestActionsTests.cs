using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Game.Quests.Scenario;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 quest action contract tests (t_ebfc9b35) — AcceptQuest · AdvanceQuest ·
/// TurnInQuest through the REAL engine paths on REAL canonical data:
///   - accept  → CharacterQuests.AddQuest (real gates: template lookup, start
///               component unit requirements, repeatable/completed checks)
///   - turn-in → QuestManager.DoReportEvents (the exact CSCompleteQuestContextPacket
///               path) + the engine's post-event step-machine evaluations
///
/// Data: PlayerbotPilotRig.SeedPilotSingletons loads REAL quest templates +
/// REAL unit requirements from the canonical compact.sqlite3 (the same data
/// prod boots with), so the accept gate is the prod gate — not a mock.
/// Quest 251 (t1 manifest) is the drive: accept from NPC 3512, gather item
/// 4058 x3, report to NPC 3512, reward item 18791 x1.
///
/// Acceptance (ROADMAP M5 idempotency rule): retries/timeouts must not
/// double-credit objectives, quest state, or rewards. Same-key retries are
/// refused pre-flight by the ledger; fresh-key retries after a landed accept
/// are refused pre-flight by the questcredit marker; fresh-key retries after
/// a completed turn-in are refused by engine state (quest dropped). The
/// audit records carry {trace_id, actor_id, action, target_id, requested_at,
/// started_at, completed_at, result, state_changes} with quest_id in
/// state_changes.
/// </summary>
[NotInParallel]
public class GameplayActorQuestActionsTests
{
    private const uint QuestId = 251;
    private const uint AcceptorNpcTemplateId = 3512;
    private const uint GatherItemId = 4058;
    private const int GatherCount = 3;
    private const uint RewardItemId = 18791;

    private static QuestScenarioManifest LoadManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t1", $"{QuestId}.json");
        return QuestScenarioManifest.LoadFromFile(path);
    }

    /// <summary>
    /// Rig ordering discipline (see GameplayActorTestRig doc): the actor rig
    /// NEVER seeds the pilot singletons itself; the TEST calls
    /// SeedPilotSingletons() FIRST (real QuestManager + real unit
    /// requirements), then CreateActor — whose missing-only guards keep the
    /// real data. Order-robust both ways (the pilot guard re-seeds when the
    /// QuestManager no longer holds real templates).
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateRealActor(string name)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2; // quest 251 is level 2; the real gate evaluates
        PlayerbotPilotRig.RegisterQuestItems(LoadManifest());
        return (actor, session);
    }

    /// <summary>
    /// Drives quest 251 to the READY state through real engine surfaces:
    /// accept via the actor, stock the gather item through the REAL
    /// acquisition path, fire the ItemGather event, advance once. Returns
    /// the actor with the quest active at Ready.
    /// </summary>
    private static GameplayActor AcceptAndProgress(string name)
    {
        var (actor, _) = CreateRealActor(name);

        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");

        // Stock the gather objective through the real inventory path.
        GameplayActorTestRig.GrantItem(actor, GatherItemId, GatherCount);

        // Fire the engine event the world pipeline fires on item acquisition.
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = QuestId,
            ItemId = GatherItemId,
            Count = GatherCount
        });

        var advance = actor.AdvanceQuest(QuestId);
        if (advance.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"advance failed: {advance.State} {advance.Detail}");

        return actor;
    }

    #region AcceptQuest — real engine gate

    [Test]
    public async Task AcceptQuest_RealGate_Completes_AndQuestIsActive()
    {
        var (actor, _) = CreateRealActor("qa-accept-1");

        var request = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();
        await Assert.That(actor.ActiveRequest).IsNull();

        // Audit record: full trace shape with quest_id in state_changes.
        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.AcceptQuest);
        await Assert.That(record.TargetId).IsEqualTo(QuestId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges).Contains($"quest_id={QuestId}");
        await Assert.That(record.StateChanges.Last()).Contains("Completed");
    }

    [Test]
    public async Task AcceptQuest_InvalidQuestId_RejectedByEngineGate()
    {
        var (actor, _) = CreateRealActor("qa-accept-2");

        var request = actor.AcceptQuest(999_999, QuestAcceptorType.Npc, AcceptorNpcTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("refused by engine gate")).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(999_999)).IsFalse();
    }

    #endregion

    #region TurnInQuest — real packet path + rewards

    [Test]
    public async Task TurnInQuest_RealPath_Completes_AndGrantsReward()
    {
        var (actor, session) = CreateRealActor("qa-turnin-1");
        AcceptAndProgressToReady(actor);

        var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);
        var request = actor.TurnInQuest(QuestId, npcObjId, 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(true);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(QuestId)).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsFalse();
        // Reward granted exactly once through the real reward pool.
        await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);

        // Audit: quest_id in state_changes.
        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.TurnInQuest);
        await Assert.That(record.TargetId).IsEqualTo(QuestId);
        await Assert.That(record.StateChanges).Contains($"quest_id={QuestId}");
    }

    [Test]
    public async Task TurnInQuest_NotReady_StillActive_NoReward()
    {
        var (actor, session) = CreateRealActor("qa-turnin-2");

        // Accept but do NOT gather the objective: the report event fires but
        // the ready-act must not pass; the quest stays active, no reward.
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);
        var request = actor.TurnInQuest(QuestId, npcObjId, 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(false);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(0);
    }

    [Test]
    public async Task TurnInQuest_UnknownNpc_RejectedWithRejectedAction()
    {
        var (actor, _) = CreateRealActor("qa-turnin-3");
        AcceptAndProgressToReady(actor);

        var request = actor.TurnInQuest(QuestId, 0xFFFF_FFFE, 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not found in world")).IsTrue();
    }

    #endregion

    #region Idempotency — no duplicate quest credit / reward

    [Test]
    public async Task AcceptQuest_SameKeyRetry_RejectedPreFlight_NoDoubleQuestState()
    {
        var (actor, _) = CreateRealActor("qa-idem-accept-1");

        var original = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId, idempotencyKey: "qa-accept-251");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Same-key retry: refused BEFORE execution (no Running transition).
        var retry = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId, idempotencyKey: "qa-accept-251");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains(original.TraceId.ToString())).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Quest state NOT duplicated: exactly one active instance.
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();

        // Correlates back to the original via the key.
        var byKey = actor.FindByKey("qa-accept-251");
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(byKey.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task AcceptQuest_FreshKeyRetry_AfterLandedAccept_RejectedPreFlight_NoDoubleCredit()
    {
        var (actor, _) = CreateRealActor("qa-idem-accept-2");

        var original = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Fresh-key retry (timeout ambiguity): the questcredit accept marker
        // proves the credit landed → refused pre-flight, engine never re-entered.
        var retry = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId, idempotencyKey: "qa-accept-251-fresh");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("accept credit already applied")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        // Still exactly one active quest instance.
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();
    }

    [Test]
    public async Task TurnInQuest_SameKeyRetry_RejectedPreFlight_NoDoubleReward()
    {
        var (actor, session) = CreateRealActor("qa-idem-turnin-1");
        AcceptAndProgressToReady(actor);

        var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);
        var original = actor.TurnInQuest(QuestId, npcObjId, 0, idempotencyKey: "qa-turnin-251");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);

        // Same-key retry after completion: refused pre-flight, reward NOT granted twice.
        var retry = actor.TurnInQuest(QuestId, npcObjId, 0, idempotencyKey: "qa-turnin-251");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);
    }

    [Test]
    public async Task TurnInQuest_FreshKeyRetry_AfterCompletion_RejectedByEngineState_NoDoubleReward()
    {
        var (actor, session) = CreateRealActor("qa-idem-turnin-2");
        AcceptAndProgressToReady(actor);

        var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);
        var original = actor.TurnInQuest(QuestId, npcObjId, 0);
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);

        // Fresh-key retry after the quest completed: the quest was dropped by
        // the engine (terminal state) → refused; reward count unchanged.
        var retry = actor.TurnInQuest(QuestId, npcObjId, 0, idempotencyKey: "qa-turnin-251-fresh");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("not active")).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);
    }

    #endregion

    #region Helpers

    private static void AcceptAndProgressToReady(GameplayActor actor)
    {
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");

        GameplayActorTestRig.GrantItem(actor, GatherItemId, GatherCount);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = QuestId,
            ItemId = GatherItemId,
            Count = GatherCount
        });

        var advance = actor.AdvanceQuest(QuestId);
        if (advance.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"advance failed: {advance.State} {advance.Detail}");
    }

    #endregion
}
