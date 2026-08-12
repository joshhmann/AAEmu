using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5 B1 actor surface tests (t_fc51af53) — Interact · Loot through the
/// REAL engine paths:
///   - Interact → Doodad.Use (the call interaction skills make; skill-less
///     loot-func branch)
///   - Loot     → LootingContainer.OpenBag(lootAll) (the CSLootOpenBagPacket path)
///
/// Acceptance: every action walks the full lifecycle + failure taxonomy and
/// emits the structured audit record; retry tests prove non-idempotent
/// actions (loot grants, phase-consuming interactions) do NOT execute
/// twice — both through the shared ActorEffectLedger (keyed retries are
/// rejected pre-flight) and through engine state (empty container rejects
/// the retry). These are contract tests — the server executes/observes a
/// command correctly, independent of any controller (spec §17 split).
/// </summary>
[NotInParallel]
public class GameplayActorInteractLootTests
{
    #region Interact

    [Test]
    public async Task Interact_LootFuncDoodad_GrantsItemThroughRealEnginePath()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-interact-1");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);

        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);

        var request = actor.Interact(doodadObjId);

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
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-interact-2");

        var request = actor.Interact(0x7FFF_FFFF);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Interact);
    }

    [Test]
    public async Task Interact_DespawnScheduled_RejectedBeforeEngine()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-interact-3");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);
        session.World.GetDoodad(doodadObjId).Despawn = DateTime.UtcNow.AddSeconds(5);

        var request = actor.Interact(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task Interact_OutOfRange_RejectedWithRejectedAction()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-interact-4");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);

        // Move the doodad beyond interaction range (the actor stays at origin).
        var doodad = session.World.GetDoodad(doodadObjId);
        doodad.Transform.Local.SetPosition(new System.Numerics.Vector3(1000f, 0f, 0f));

        var request = actor.Interact(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task Interact_KeyedRetryAfterSuccess_RejectedPreFlight_NoDoubleGrant()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-interact-5");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);

        var first = actor.Interact(doodadObjId, idempotencyKey: "interact-key-1");
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);

        // Retry with the SAME key: the shared ledger rejects it pre-flight —
        // the same interaction cannot fire twice.
        var retry = actor.Interact(doodadObjId, idempotencyKey: "interact-key-1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        // No second grant, no engine re-entry.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
    }

    #endregion

    #region Loot

    private static uint SeedCorpse(GameplayActor actor, HeadlessSession session)
    {
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.LootItemTemplateId);
        var npcObjId = session.SpawnNpc(1000);
        var npc = session.World.GetNpc(npcObjId);
        GameplayActorTestRig.SeedLootContainer(npc,
            (GameplayActorTestRig.InteractItemTemplateId, 2),
            (GameplayActorTestRig.LootItemTemplateId, 1));
        return npcObjId;
    }

    [Test]
    public async Task Loot_SeededCorpse_GrantsItemsThroughRealEnginePath_AndEmptiesContainer()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-loot-1");
        var npcObjId = SeedCorpse(actor, session);

        var request = actor.Loot(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.LootItemTemplateId)).IsEqualTo(1);
        // The engine removed every granted entry (TryReserveLootItem).
        await Assert.That(session.World.GetNpc(npcObjId).LootingContainer.Items.Count).IsEqualTo(0);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Loot);
        await Assert.That(record.TargetId).IsEqualTo(npcObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Last()).Contains("Completed");
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.StartedAtUtc != default).IsTrue();
        await Assert.That(record.CompletedAtUtc != default).IsTrue();
    }

    [Test]
    public async Task Loot_RetryAfterSuccess_RejectedAlreadyLooted_NoDuplicate()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-loot-2");
        var npcObjId = SeedCorpse(actor, session);

        var first = actor.Loot(npcObjId);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Unkeyed retry: the engine container is now empty, so the request is
        // rejected with a clear reason — no duplicate loot.
        var retry = actor.Loot(npcObjId);
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail).Contains("already looted");

        // Retry did NOT duplicate the loot.
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.LootItemTemplateId)).IsEqualTo(1);
        await Assert.That(session.World.GetNpc(npcObjId).LootingContainer.Items.Count).IsEqualTo(0);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Loot_KeyedRetryAfterSuccess_RejectedPreFlight_NoDuplicate()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-loot-4");
        var npcObjId = SeedCorpse(actor, session);

        var first = actor.Loot(npcObjId, idempotencyKey: "loot-key-1");
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Keyed retry: the shared ledger rejects pre-flight — the engine is
        // never re-entered.
        var retry = actor.Loot(npcObjId, idempotencyKey: "loot-key-1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.LootItemTemplateId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Loot_UnknownOwner_RejectedWithRejectedAction()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("b1-loot-3");

        var request = actor.Loot(0x7FFF_FFFF);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(actor.AuditTrace[^1].Action).IsEqualTo(ActorActionType.Loot);
    }

    [Test]
    public async Task Loot_EmptyContainer_RejectedWithClearReason()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-loot-5");
        var npcObjId = session.SpawnNpc(1001);
        GameplayActorTestRig.SeedLootContainer(session.World.GetNpc(npcObjId)); // no entries

        var request = actor.Loot(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("nothing to loot");
    }

    [Test]
    public async Task Loot_OutOfRange_RejectedWithRejectedAction()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("b1-loot-6");
        var npcObjId = SeedCorpse(actor, session);

        // Move the corpse beyond loot range (the actor stays at origin).
        var npc = session.World.GetNpc(npcObjId);
        npc.Transform.Local.SetPosition(new System.Numerics.Vector3(10000f, 0f, 0f));

        var request = actor.Loot(npcObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(0);
    }

    #endregion
}
