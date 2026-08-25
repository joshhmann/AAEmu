using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.DoodadObj;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Capability-matrix gap #3 — the first-class doodad-interaction contract
/// action <see cref="IGameplayActor.InteractWith"/> (generalizes the
/// fishing/indun portal "real doodad-cast injection").
///
/// Evidence chain under test:
///  - Client path: CSStartSkillPacket carries a SkillCastDoodadTarget with
///    the interaction use-skill; the server's skill pipeline lands in
///    InteractionEffect → WorldInteraction Use.Execute → Doodad.Use(caster,
///    skillId) — the exact call this action makes. DoodadManager.GetFunc
///    matches the client's skill id against func.SkillId bindings /
///    DoodadFuncUse / DoodadFuncFakeUse templates — the same rules
///    ResolveInteractionSkill uses to DERIVE the id from the doodad's own
///    phase group.
///  - Engine validation: despawn guard only (#1443); everything else
///    (missing funcs, failed phase conditions) is a SILENT void — so the
///    action post-checks an observable state delta (phase, world, position,
///    inventory, buffs) and fails closed on none (PartyInvite precedent).
///
/// Headless rig: loot-doodad case reuses SeedDoodadLootInteraction (the
/// proven generic-world-interactable surface); portal-style case seeds a
/// skill-bound DoodadFuncBuff func — an observable engine effect through
/// the same DoFunc chain a real portal func rides. The full indun teleport
/// (IndunManager.RequestDungeonInstance) is NOT exercisable headless —
/// world/instance deltas are covered by the fingerprint design, noted as
/// UNKNOWN in the interface doc.
/// </summary>
[NotInParallel]
public class GameplayActorInteractWithTests
{
    [Test]
    public async Task InteractWith_GenericLootDoodad_GrantsThroughRealEnginePath()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gap3-interact-1");
        GameplayActorTestRig.SeedItemTemplate(GameplayActorTestRig.InteractItemTemplateId);
        var doodadObjId = GameplayActorTestRig.SpawnInteractableDoodad(
            session, GameplayActorTestRig.InteractDoodadGroupId,
            GameplayActorTestRig.InteractLootFuncId, GameplayActorTestRig.InteractItemTemplateId);

        var request = actor.InteractWith(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.InteractItemTemplateId)).IsEqualTo(1);

        // The observable-effect payload proves WHY it completed.
        var result = (InteractWithResult)request.Result!;
        await Assert.That(result.DoodadObjId).IsEqualTo(doodadObjId);
        await Assert.That(result.TemplateId).IsEqualTo(GameplayActorTestRig.InteractDoodadGroupId);
        await Assert.That(result.ObservedChanges.Any(c => c.Contains("bag"))).IsTrue();
    }

    [Test]
    public async Task InteractWith_SkillBoundPortalStyleDoodad_DerivesUseSkillAndAppliesEngineEffect()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gap3-interact-2");
        GameplayActorTestRig.SeedSkillBoundDoodad();
        var doodadObjId = GameplayActorTestRig.SpawnGroupedDoodad(session, GameplayActorTestRig.InteractWithPortalGroupId);

        var request = actor.InteractWith(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var result = (InteractWithResult)request.Result!;
        // The use-skill was derived from the doodad's OWN func binding — the
        // same id the client would have sent in CSStartSkillPacket.
        await Assert.That(result.UsedSkillId).IsEqualTo(GameplayActorTestRig.InteractWithUseSkillId);
        // The engine effect (buff on the caster) actually landed…
        await Assert.That(actor.Character.Buffs.CheckBuff(GameplayActorTestRig.InteractWithBuffId)).IsTrue();
        // …and is reported in the observable-delta payload.
        await Assert.That(result.ObservedChanges.Any(c => c.Contains("buffs"))).IsTrue();
    }

    [Test]
    public async Task InteractWith_EngineSilentRefusal_NoFuncs_RejectedNoStateChange()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gap3-interact-3");
        // Phase group with NO funcs: Doodad.Use returns silently ("Phase has
        // no funcs") — the exact silent void the post-check converts into a
        // fail-closed rejection.
        var doodadObjId = GameplayActorTestRig.SpawnGroupedDoodad(session, GameplayActorTestRig.InteractWithEmptyGroupId);

        var request = actor.InteractWith(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("no state change")).IsTrue();
    }

    [Test]
    public async Task InteractWith_UnknownDoodad_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("gap3-interact-4");

        var request = actor.InteractWith(0xBEEF); // resolves to no world object

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task InteractWith_OutOfRange_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gap3-interact-5");
        var doodadObjId = GameplayActorTestRig.SpawnGroupedDoodad(session, GameplayActorTestRig.InteractWithEmptyGroupId);
        var doodad = session.World.GetDoodad(doodadObjId)!;
        doodad.Transform.Local.SetPosition(new System.Numerics.Vector3(500, 500, 0));

        var request = actor.InteractWith(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("interaction range")).IsTrue();
    }

    [Test]
    public async Task InteractWith_ScheduledDespawn_RejectedBeforeEngine()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gap3-interact-6");
        var doodadObjId = GameplayActorTestRig.SpawnGroupedDoodad(session, GameplayActorTestRig.InteractWithEmptyGroupId);
        var doodad = session.World.GetDoodad(doodadObjId)!;
        doodad.Despawn = DateTime.UtcNow.AddMinutes(1); // engine's #1443 guard

        var request = actor.InteractWith(doodadObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail!.Contains("despawn")).IsTrue();
    }

    [Test]
    public async Task InteractWith_Completes_EmitsAuditRecordWithFullLifecycle()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("gap3-interact-7");
        GameplayActorTestRig.SeedSkillBoundDoodad();
        var doodadObjId = GameplayActorTestRig.SpawnGroupedDoodad(session, GameplayActorTestRig.InteractWithPortalGroupId);

        _ = actor.InteractWith(doodadObjId);

        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.InteractWith);
        await Assert.That(record.TargetId).IsEqualTo(doodadObjId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running (interacting"))).IsTrue();
        await Assert.That(record.StateChanges.Last()).Contains("Completed");
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.CompletedAtUtc != default).IsTrue();
    }
}
