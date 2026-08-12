using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Game.Quests.Scenario;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Final B1 verification tests (t_219f7724) — run at fork/develop tip after
/// Interact/Loot (t_fc51af53), UseItem/Mount/Dismount (t_a5edc1e6),
/// AcceptQuest/TurnInQuest (t_ebfc9b35) and the shared contract layer
/// (t_cbbc1103) all landed. Two consolidated exit tests:
///
///  1. ScriptedActor_NineActionSegment_* — the ROADMAP M5 exit-test shape:
///     ONE scripted actor completes the full segment (Observe · Move ·
///     Interact · Loot · UseItem · Mount · Dismount · AcceptQuest ·
///     TurnInQuest) through the real engine paths, and every action emits
///     exactly one structured trace record in the stable control-plane
///     JSON form ({trace_id, actor_id, action, target_id, requested_at,
///     started_at, completed_at, result, failure, detail, state_changes}).
///     The per-action samples are also written to the gitignored evidence
///     dir (scorecard-explorations/generated/b1-trace-samples.json — same
///     discipline as PlayerbotPilotTests' generated metrics).
///
///  2. SixB1Actions_TimeoutAmbiguityRetry_* — the ROADMAP idempotency exit
///     rule for ALL six B1 actions in one provable flow: a controller that
///     timed out waiting on a synchronous action retries with the SAME
///     idempotency key; every retry is refused pre-flight by the ledger
///     (Completed/Interrupted/TimedOut all lock the key — the timeout
///     ambiguity case), shows no Running transition, and the effect count
///     (item consumption, quest credit, reward, mount state, loot grant,
///     interaction grant) is byte-identical.
///
/// All assertions are contract tests — the server executes/observes each
/// command correctly, independent of any controller (spec §17 split).
/// </summary>
[NotInParallel]
public class GameplayActorB1TraceSamplesTests
{
    // Quest 251 drive (same canonical data as GameplayActorQuestActionsTests).
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>
    /// Real-data actor: pilot singletons FIRST (real QuestManager + real
    /// unit requirements from canonical compact.sqlite3), then the actor
    /// rig — same ordering discipline as GameplayActorQuestActionsTests.
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
    /// Drives quest 251 to the READY state through real engine surfaces
    /// (mirrors GameplayActorQuestActionsTests.AcceptAndProgressToReady).
    /// </summary>
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

    /// <summary>
    /// Shared retry-ambiguity assertions: the same-key retry after a
    /// completed original is refused pre-flight (StateTransition +
    /// "duplicate idempotency key"), never shows a Running transition,
    /// and the ledger still correlates back to the ORIGINAL trace.
    /// </summary>
    private static async Task AssertTimeoutAmbiguityRetry(GameplayActor actor, ActorRequest original, ActorRequest retry)
    {
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains("duplicate idempotency key")).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();

        var byKey = actor.FindByKey(original.IdempotencyKey!);
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(byKey.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    #region Exit test 1 — nine-action segment, one structured trace sample per action

    [Test]
    public async Task ScriptedActor_NineActionSegment_ProducesOneStructuredTraceSamplePerAction()
    {
        var (actor, session) = CreateRealActor("b1-exit-1");

        // 1. Observe — direct server-state query (spec §8: no packets).
        var observation = actor.Observe();
        await Assert.That(observation.ActorId).IsEqualTo(actor.ActorId);
        await Assert.That(observation.ActiveQuestIds).IsNotNull();

        // 2. Move — bounded walk through the ordinary Transform.
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var move = actor.MoveTo(new Vector3(10, 0, 0), speed: 2f);
        var guard = 0;
        while (move.State is ActorLifecycleState.Accepted or ActorLifecycleState.Running && guard++ < 100)
            actor.Tick(TimeSpan.FromSeconds(1));
        await Assert.That(move.State).IsEqualTo(ActorLifecycleState.Completed);

        // 3. Interact — Doodad.Use skill-less loot-func branch.
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);
        var interact = actor.Interact(doodadObjId);
        await Assert.That(interact.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);

        // 4. Loot — LootingContainer.OpenBag(lootAll) (CSLootOpenBagPacket path).
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.LootItemTemplateId);
        var npcObjId = session.SpawnNpc(1000);
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId),
            (GameplayActorTestRig.InteractItemTemplateId, 2),
            (GameplayActorTestRig.LootItemTemplateId, 1));
        var loot = actor.Loot(npcObjId);
        await Assert.That(loot.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.LootItemTemplateId)).IsEqualTo(1);

        // 5. UseItem — Skill.Use with a SkillItem caster (CSStartSkillPacket branch).
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);
        var useItem = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(useItem.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);

        // 6+7. Mount / Dismount — MateManager.MountMate / UnMountMate.
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor);
        var mount = actor.Mount(mateObjId);
        await Assert.That(mount.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.IsRiding).IsTrue();
        var dismount = actor.Dismount();
        await Assert.That(dismount.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.IsRiding).IsFalse();

        // 8. AcceptQuest — real AddQuest gate (level/race/repeatable checks).
        var accept = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();

        // 9. TurnInQuest — real DoReportEvents path + reward pool.
        GameplayActorTestRig.GrantItem(actor, GatherItemId, GatherCount);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = QuestId,
            ItemId = GatherItemId,
            Count = GatherCount
        });
        var advance = actor.AdvanceQuest(QuestId);
        await Assert.That(advance.State).IsEqualTo(ActorLifecycleState.Completed);
        var turnInNpcObjId = session.SpawnNpc(AcceptorNpcTemplateId);
        var turnIn = actor.TurnInQuest(QuestId, turnInNpcObjId, 0);
        await Assert.That(turnIn.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(QuestId)).IsTrue();
        await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);

        // --- Per-action trace sample assertions -------------------------------
        // Every required action emitted EXACTLY one structured record.
        var required = new[]
        {
            ActorActionType.Observe, ActorActionType.Move, ActorActionType.Interact,
            ActorActionType.Loot, ActorActionType.UseItem, ActorActionType.Mount,
            ActorActionType.Dismount, ActorActionType.AcceptQuest, ActorActionType.TurnInQuest
        };
        foreach (var action in required)
        {
            var records = actor.AuditTrace.Where(r => r.Action == action).ToList();
            await Assert.That(records.Count).IsEqualTo(1);
        }

        // Every record carries the full trace shape and the stable
        // control-plane JSON form.
        await Assert.That(actor.AuditTrace.Count).IsGreaterThanOrEqualTo(required.Length + 1); // + AdvanceQuest
        foreach (var record in actor.AuditTrace)
        {
            await Assert.That(record.TraceId != Guid.Empty).IsTrue();
            await Assert.That(record.ActorId).IsEqualTo(actor.ActorId);
            await Assert.That(record.RequestedAtUtc != default).IsTrue();
            await Assert.That(record.StartedAtUtc != default).IsTrue();
            await Assert.That(record.CompletedAtUtc != default).IsTrue();
            await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(record.Failure).IsNull();
            await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
            await Assert.That(record.StateChanges.Last().Contains(record.Result.ToString())).IsTrue();

            using var doc = JsonDocument.Parse(record.ToJson());
            var root = doc.RootElement;
            await Assert.That(root.TryGetProperty("trace_id", out var traceIdProp)).IsTrue();
            await Assert.That(traceIdProp.GetGuid()).IsEqualTo(record.TraceId);
            await Assert.That(root.GetProperty("actor_id").GetUInt32()).IsEqualTo(actor.ActorId);
            await Assert.That(root.GetProperty("action").GetString()).IsEqualTo(record.Action.ToString());
            await Assert.That(root.TryGetProperty("target_id", out _)).IsTrue();
            await Assert.That(root.GetProperty("requested_at").GetDateTimeOffset()).IsNotEqualTo(default);
            await Assert.That(root.GetProperty("started_at").GetDateTimeOffset()).IsNotEqualTo(default);
            await Assert.That(root.GetProperty("completed_at").GetDateTimeOffset()).IsNotEqualTo(default);
            await Assert.That(root.GetProperty("result").GetString()).IsEqualTo("Completed");
            await Assert.That(root.GetProperty("failure").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(root.GetProperty("state_changes").GetArrayLength()).IsGreaterThanOrEqualTo(3);
            await Assert.That(root.GetProperty("state_changes")[0].GetString()).IsEqualTo("Requested");
        }

        // --- Evidence artifact: one structured trace sample per action --------
        // Written to the gitignored generated/ dir (same discipline as
        // PlayerbotPilotTests' m2b-pilot-metrics.md) so the card can attach
        // the machine-readable trace as review evidence.
        var samples = new JsonArray();
        foreach (var record in actor.AuditTrace)
            samples.Add(JsonNode.Parse(record.ToJson()));
        var doc2 = new JsonObject
        {
            ["generated_by"] = "GameplayActorB1TraceSamplesTests (kanban t_219f7724 final B1 verification)",
            ["action_count"] = actor.AuditTrace.Count,
            ["actions"] = samples
        };
        var outPath = Path.Combine(RepoRoot(), "scorecard-explorations", "generated", "b1-trace-samples.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, doc2.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("B1 trace samples written to " + outPath);
    }

    #endregion

    #region Exit test 2 — all six B1 actions: timeout-ambiguity retries never execute twice

    [Test]
    public async Task SixB1Actions_TimeoutAmbiguityRetry_RefusedPreFlight_NoDoubleExecution()
    {
        // Interact — a retry must not re-grant the interaction item.
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("b1-vrfy-interact");
            GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
            var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
                session, GameplayActorTestRig.InteractDoodadGroupId,
                GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);

            var original = actor.Interact(doodadObjId, idempotencyKey: "b1v-interact");
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);

            var retry = actor.Interact(doodadObjId, idempotencyKey: "b1v-interact");
            await AssertTimeoutAmbiguityRetry(actor, original, retry);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);
        }

        // Loot — a retry must not duplicate loot grants.
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("b1-vrfy-loot");
            GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
            GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.LootItemTemplateId);
            var npcObjId = session.SpawnNpc(1000);
            GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId),
                (GameplayActorTestRig.InteractItemTemplateId, 2),
                (GameplayActorTestRig.LootItemTemplateId, 1));

            var original = actor.Loot(npcObjId, idempotencyKey: "b1v-loot");
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.LootItemTemplateId)).IsEqualTo(1);

            var retry = actor.Loot(npcObjId, idempotencyKey: "b1v-loot");
            await AssertTimeoutAmbiguityRetry(actor, original, retry);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(2);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.LootItemTemplateId)).IsEqualTo(1);
        }

        // UseItem — a retry must not consume the item twice.
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("b1-vrfy-useitem");
            GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);

            var original = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "b1v-useitem");
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);

            var retry = actor.UseItem(GameplayActorTestRig.TestItemTemplateId, idempotencyKey: "b1v-useitem");
            await AssertTimeoutAmbiguityRetry(actor, original, retry);
            await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);
        }

        // Mount — a retry must not flip the mount state (no double attach).
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("b1-vrfy-mount");
            var mateObjId = GameplayActorTestRig.SummonMate(session, actor);

            var original = actor.Mount(mateObjId, idempotencyKey: "b1v-mount");
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(actor.Character.IsRiding).IsTrue();

            var retry = actor.Mount(mateObjId, idempotencyKey: "b1v-mount");
            await AssertTimeoutAmbiguityRetry(actor, original, retry);
            await Assert.That(actor.Character.IsRiding).IsTrue();
            await Assert.That(session.World.MateManager.GetIsMounted(actor.ActorId, out _)).IsNotNull();
        }

        // Dismount — a retry must not flip the mount state (no double detach).
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("b1-vrfy-dismount");
            var mateObjId = GameplayActorTestRig.SummonMate(session, actor);
            var mount = actor.Mount(mateObjId);
            await Assert.That(mount.State).IsEqualTo(ActorLifecycleState.Completed);

            var original = actor.Dismount(idempotencyKey: "b1v-dismount");
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(actor.Character.IsRiding).IsFalse();

            var retry = actor.Dismount(idempotencyKey: "b1v-dismount");
            await AssertTimeoutAmbiguityRetry(actor, original, retry);
            await Assert.That(actor.Character.IsRiding).IsFalse();
            await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.None);
        }

        // AcceptQuest — a retry must not double-credit the quest accept.
        {
            var (actor, _) = CreateRealActor("b1-vrfy-accept");

            var original = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId, idempotencyKey: "b1v-accept");
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();

            var retry = actor.AcceptQuest(QuestId, QuestAcceptorType.Npc, AcceptorNpcTemplateId, idempotencyKey: "b1v-accept");
            await AssertTimeoutAmbiguityRetry(actor, original, retry);
            await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(QuestId)).IsTrue();
        }

        // TurnInQuest — a retry must not double-credit the reward.
        {
            var (actor, session) = CreateRealActor("b1-vrfy-turnin");
            AcceptAndProgressToReady(actor);
            var npcObjId = session.SpawnNpc(AcceptorNpcTemplateId);

            var original = actor.TurnInQuest(QuestId, npcObjId, 0, idempotencyKey: "b1v-turnin");
            await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);
            await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);

            var retry = actor.TurnInQuest(QuestId, npcObjId, 0, idempotencyKey: "b1v-turnin");
            await AssertTimeoutAmbiguityRetry(actor, original, retry);
            await Assert.That(GameplayActorTestRig.BagCount(actor, RewardItemId)).IsEqualTo(1);
        }
    }

    #endregion
}
