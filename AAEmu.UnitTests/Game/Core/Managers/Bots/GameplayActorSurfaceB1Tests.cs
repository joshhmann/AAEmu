using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 B1 actor surface tests (t_659f891f) — Interact · Loot · UseItem ·
/// Mount/Dismount through the REAL engine paths:
///   - Interact   → Doodad.Use (the call interaction skills make)
///   - Loot       → LootingContainer.OpenBag(lootAll) (the CSLootOpenBagPacket path)
///   - UseItem    → Skill.Use with a SkillItem caster (the CSStartSkillPacket branch)
///   - Mount      → MateManager.MountMate (the CSMountMatePacket path)
///   - Dismount   → MateManager.UnMountMate (the CSUnMountMatePacket path)
///
/// Acceptance: every action walks the full lifecycle + failure taxonomy and
/// emits the structured audit record; retry tests prove non-idempotent
/// actions (loot grants, item consumption, mounting) do NOT execute twice.
/// These are contract tests — the server executes/observes a command
/// correctly, independent of any controller (spec §17 split).
/// </summary>
[NotInParallel]
public class GameplayActorSurfaceB1Tests
{
    #region Interact

    [Test]
    public async Task Interact_LootFuncDoodad_GrantsItemThroughRealEnginePath()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sb1-interact-1");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);

        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);

        var request = actor.Interact(doodadObjId, skillId: 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);
        await Assert.That(actor.ActiveRequest).IsNull();

        // Audit record: action + target + full trace shape.
        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Interact);
        await Assert.That(record.TargetId).IsEqualTo(doodadObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Last()).Contains("Completed");
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.StartedAtUtc != default).IsTrue();
        await Assert.That(record.CompletedAtUtc != default).IsTrue();
    }

    [Test]
    public async Task Interact_UnknownDoodad_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sb1-interact-2");

        var request = actor.Interact(0x7FFF_FFFF, skillId: 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Interact);
    }

    [Test]
    public async Task Interact_UnknownInteractionSkill_RejectedWithRejectedAction()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sb1-interact-3");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);

        var request = actor.Interact(doodadObjId, skillId: 0x7FFF_FFFF);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // The engine never ran — nothing was granted.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task Interact_DespawnScheduled_RejectedBeforeEngine()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sb1-interact-4");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);
        session.World.GetDoodad(doodadObjId).Despawn = DateTime.UtcNow.AddSeconds(5);

        var request = actor.Interact(doodadObjId, skillId: 0);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(0);
    }

    #endregion

    #region Loot

    private static uint SeedCorpse(GameplayActor actor, HeadlessSession session)
    {
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.UseItemTemplateId);
        var npcObjId = session.SpawnNpc(1000);
        var npc = session.World.GetNpc(npcObjId);
        GameplayActorTestRig.SeedLootContainer(npc,
            (GameplayActorTestRig.InteractItemTemplateId, 2),
            (GameplayActorTestRig.UseItemTemplateId, 1));
        return npcObjId;
    }

    [Test]
    public async Task Loot_SeededCorpse_GrantsItemsThroughRealEnginePath_AndEmptiesContainer()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sb1-loot-1");
        var npcObjId = SeedCorpse(actor, session);

        var request = actor.Loot(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.UseItemTemplateId)).IsEqualTo(1);
        // The engine removed every granted entry (TryReserveLootItem).
        await Assert.That(session.World.GetNpc(npcObjId).LootingContainer.Items.Count).IsEqualTo(0);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Loot);
        await Assert.That(record.TargetId).IsEqualTo(npcObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
    }

    [Test]
    public async Task Loot_RetryAfterSuccess_RejectedAlreadyLooted_NoDuplicate()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sb1-loot-2");
        var npcObjId = SeedCorpse(actor, session);

        var first = actor.Loot(npcObjId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Unkeyed retry: the engine container is now empty (the merged tree
        // carries the empty-container pre-flight from fork/develop), so the
        // request is rejected with a clear reason — no duplicate loot.
        var retry = actor.Loot(npcObjId);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail).Contains("already looted");

        // Retry did NOT duplicate the loot.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.UseItemTemplateId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Loot_UnknownOwner_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sb1-loot-3");

        var request = actor.Loot(0x7FFF_FFFF);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Loot);
    }

    #endregion

    #region UseItem

    [Test]
    public async Task UseItem_ConsumableItem_CompletesAndConsumesThroughRealEnginePath()
    {
        // Uses the rig's canonical usable item (TestItemTemplateId + real
        // reagent mapping on TestItemUseSkillId — the merged B1 rig setup):
        // consumption flows through the ordinary skill-pipeline reagent path.
        var (actor, session) = GameplayActorTestRig.CreateActor("sf-useitem-1");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 2);

        var request = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
        // The engine consumed one unit through the skill's reagent entry.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(1);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.UseItem);
        await Assert.That(record.TargetId).IsEqualTo(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
    }

    [Test]
    public async Task UseItem_RetryAfterConsumption_RejectedWithoutDoubleUse_ProvesIdempotency()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sf-useitem-2");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 1);

        var first = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.TestItemTemplateId)).IsEqualTo(0);

        // Retry: the item is gone — Rejected BEFORE any engine execution.
        var retry = actor.UseItem(GameplayActorTestRig.TestItemTemplateId);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
    }

    [Test]
    public async Task UseItem_NoItemInInventory_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-useitem-3");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.UseItemTemplateId,
            GameplayActorTestRig.UseItemSkillId, useSkillAsReagent: true);
        GameplayActorTestRig.SeedSkillTemplate(GameplayActorTestRig.UseItemSkillId);

        var request = actor.UseItem(GameplayActorTestRig.UseItemTemplateId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task UseItem_ItemWithoutUseSkill_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-useitem-4");
        GameplayActorTestRig.SeedItemTemplate(91_003, useSkillId: 0);
        GameplayActorTestRig.GrantItem(actor, 91_003, 1);

        var request = actor.UseItem(91_003);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        // Nothing consumed.
        await Assert.That(GameplayActorTestRig.BagCount(actor, 91_003)).IsEqualTo(1);
    }

    [Test]
    public async Task UseItem_UnknownUseSkill_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-useitem-5");
        GameplayActorTestRig.SeedItemTemplate(91_004, useSkillId: 0x7FFF_FFFE);
        GameplayActorTestRig.GrantItem(actor, 91_004, 1);

        var request = actor.UseItem(91_004);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    #endregion

    #region Mount / Dismount

    private const uint MateObjId = 0x5001;

    [Test]
    public async Task Mount_WithConnection_CompletesThroughRealEnginePath()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sf-mount-1");
        GameplayActorTestRig.AttachConnection(actor);
        GameplayActorTestRig.SpawnMate(actor, MateObjId, 1);

        var request = actor.Mount(MateObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.IsRiding).IsTrue();
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.Driver);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Mount);
        await Assert.That(record.TargetId).IsEqualTo(MateObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
    }

    [Test]
    public async Task Mount_Headless_CompletesThroughCharacterDrivenPath()
    {
        // Merged contract (t_a5edc1e6): Mount is character-driven through the
        // shared MateManager.MountMate(Character, …) entry — the same engine
        // call the packet wrapper reaches via connection.ActiveChar. Headless
        // pilots mount without a GameConnection; no fabricated session needed.
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-mount-2");
        GameplayActorTestRig.SpawnMate(actor, MateObjId, 1);

        var request = actor.Mount(MateObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.IsRiding).IsTrue();
    }

    [Test]
    public async Task Mount_AlreadyMounted_RejectedWithStateTransition_ProvesIdempotency()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-mount-3");
        GameplayActorTestRig.AttachConnection(actor);
        GameplayActorTestRig.SpawnMate(actor, MateObjId, 1);

        var first = actor.Mount(MateObjId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Retry while riding: StateTransition, engine never re-entered.
        var retry = actor.Mount(MateObjId);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(actor.Character.IsRiding).IsTrue();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Mount_UnknownMate_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-mount-4");
        GameplayActorTestRig.AttachConnection(actor);

        var request = actor.Mount(0x5002);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task Dismount_Mounted_CompletesThroughRealEnginePath()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-dismount-1");
        GameplayActorTestRig.AttachConnection(actor);
        GameplayActorTestRig.SpawnMate(actor, MateObjId, 1);
        var mount = actor.Mount(MateObjId);
        await Assert.That(mount.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.Dismount();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.IsRiding).IsFalse();
        await Assert.That(actor.Character.AttachedPoint).IsEqualTo(AttachPointKind.None);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Dismount);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    [Test]
    public async Task Dismount_NotMounted_RejectedWithStateTransition()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-dismount-2");

        var request = actor.Dismount();

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
    }

    [Test]
    public async Task Dismount_RetryAfterSuccess_RejectedWithStateTransition_ProvesIdempotency()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-dismount-3");
        GameplayActorTestRig.AttachConnection(actor);
        GameplayActorTestRig.SpawnMate(actor, MateObjId, 1);
        var mount = actor.Mount(MateObjId);
        await Assert.That(mount.State).IsEqualTo(ActorLifecycleState.Completed);

        var first = actor.Dismount();
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Retry after success: not mounted → StateTransition, no double-dismount.
        var retry = actor.Dismount();
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(actor.Character.IsRiding).IsFalse();
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Dismount_WrongMateSpecified_RejectedWithStateTransition()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("sf-dismount-4");
        GameplayActorTestRig.AttachConnection(actor);
        GameplayActorTestRig.SpawnMate(actor, MateObjId, 1);
        var mount = actor.Mount(MateObjId);
        await Assert.That(mount.State).IsEqualTo(ActorLifecycleState.Completed);

        var request = actor.Dismount(0x5002);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(actor.Character.IsRiding).IsTrue(); // still riding — nothing changed
    }

    #endregion

    #region Trace shape

    [Test]
    public async Task B1Actions_AllEmitStructuredTraceRecords()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("sf-trace-1");
        GameplayActorTestRig.AttachConnection(actor);
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.UseItemTemplateId,
            GameplayActorTestRig.UseItemSkillId, useSkillAsReagent: true);
        GameplayActorTestRig.SeedSkillTemplate(GameplayActorTestRig.UseItemSkillId);
        GameplayActorTestRig.GrantItem(actor, GameplayActorTestRig.UseItemTemplateId, 1);
        GameplayActorTestRig.SpawnMate(actor, MateObjId, 1);

        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);
        var npcObjId = session.SpawnNpc(1000);
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId),
            (GameplayActorTestRig.InteractItemTemplateId, 1));

        _ = actor.Interact(doodadObjId);
        _ = actor.Loot(npcObjId);
        _ = actor.UseItem(GameplayActorTestRig.UseItemTemplateId);
        _ = actor.Mount(MateObjId);
        _ = actor.Dismount();

        var actions = actor.AuditTrace.Select(r => r.Action).ToList();
        await Assert.That(actions).Contains(ActorActionType.Interact);
        await Assert.That(actions).Contains(ActorActionType.Loot);
        await Assert.That(actions).Contains(ActorActionType.UseItem);
        await Assert.That(actions).Contains(ActorActionType.Mount);
        await Assert.That(actions).Contains(ActorActionType.Dismount);

        // Every record carries the full structured shape
        // {trace_id, actor_id, action, target_id, requested_at, started_at,
        //  completed_at, result, state_changes}.
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
            await Assert.That(record.StateChanges.Last()).Contains(record.Result.ToString());
        }
    }

    #endregion
}
