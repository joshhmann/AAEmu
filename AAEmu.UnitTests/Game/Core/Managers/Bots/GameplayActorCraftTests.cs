using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Crafts;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// M5.1 Craft contract tests (t_0fc3a550, salvaged t_cffb71ad) — one engine
/// craft step through the REAL engine path:
///   - Craft → CharacterCraft.Craft (the exact call CSExecuteCraft makes,
///     count=1) → normal skill pipeline → CraftEffect.Apply → EndCraft
///     (materials consumed before products granted).
///
/// Acceptance: the action walks the full lifecycle + failure taxonomy and
/// emits the structured audit record; retry tests prove crafting does NOT
/// execute twice (no duplicate crafted item / material / labor consumption):
///   - same-key retry after completion → Rejected(StateTransition)
///     pre-flight, no Running transition, engine never re-entered;
///   - fresh-key retry after completion → the consumed materials are the
///     engine-true backstop (RejectedAction, nothing left to craft);
///   - timeout while the engine queue is active → TimedOut(Starvation), the
///     key stays locked, and the engine step that lands afterwards is never
///     re-run by a retry.
///
/// These are contract tests — the server executes/observes a command
/// correctly, independent of any controller (spec §17 split).
/// </summary>
[NotInParallel]
public class GameplayActorCraftTests
{
    private static (GameplayActor Actor, HeadlessSession Session, uint BenchObjId) CreateCraftRig(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SeedCraftSurface();
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        actor.Character.LaborPower = 100;
        GameplayActorTestRig.GrantItem(actor, GameplayActorTestRig.CraftMaterialTemplateId, 2);
        return (actor, session, benchObjId);
    }

    [Test]
    public async Task Craft_WithBenchAndMaterials_CompletesThroughRealEnginePath()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-ok-1");

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);

        // The engine accepted the step (queue active) — request is Running.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(actor.Character.Craft.IsCrafting).IsTrue();

        // Real engine completion: CraftEffect.Apply → EndCraft (the same
        // chain the cast pipeline runs).
        GameplayActorTestRig.CompleteCraftStep(actor, benchObjId);
        actor.Tick(TimeSpan.Zero);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
        await Assert.That(actor.ActiveRequest).IsNull();
        // Materials consumed by the engine (2 → 0), product granted (1).
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
        // The engine queue drained.
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();

        // Result payload: the granted product row.
        var result = request.Result as CraftResult;
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.CraftId).IsEqualTo(GameplayActorTestRig.CraftTestCraftId);
        await Assert.That(result.Products).HasCount().EqualTo(1);
        await Assert.That(result.Products[0].ItemId).IsEqualTo(GameplayActorTestRig.CraftProductTemplateId);
        await Assert.That(result.Products[0].Amount).IsEqualTo(1);

        // Audit record: full structured trace shape.
        var record = actor.AuditTrace[^1];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.Craft);
        await Assert.That(record.TargetId).IsEqualTo(GameplayActorTestRig.CraftTestCraftId);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.Failure).IsNull();
        await Assert.That(record.StateChanges.First()).IsEqualTo("Requested");
        await Assert.That(record.StateChanges.Any(s => s.StartsWith("Running"))).IsTrue();
        await Assert.That(record.StateChanges.Last()).Contains("Completed");
        await Assert.That(record.RequestedAtUtc != default).IsTrue();
        await Assert.That(record.StartedAtUtc != default).IsTrue();
    }

    [Test]
    public async Task Craft_UnknownCraft_Rejected()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-unknown-1");

        var request = actor.Craft(999_999, benchObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("unknown craft");
        // Nothing started — the engine queue never engaged.
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(2);
    }

    [Test]
    public async Task Craft_QueueAlreadyActive_RejectedStateTransition()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-queuebusy-1");
        // Start the engine queue DIRECTLY (the rig engine-path shape) so the
        // actor itself is idle — the engine's queue guard must fire.
        var craft = CraftManager.Instance.GetCraftById(GameplayActorTestRig.CraftTestCraftId);
        actor.Character.Craft.Craft(craft!, 1, benchObjId);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsTrue();

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(request.Detail).Contains("craft queue already active");
        // The pre-existing engine queue is untouched.
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsTrue();
    }

    [Test]
    public async Task Craft_MissingSkillTemplate_Rejected()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-noskill-1");
        // Register a recipe whose skill template is NOT seeded (missing-only,
        // additive — the rig's own craft is untouched).
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager).GetField("_crafts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(CraftManager.Instance)!;
        const uint missingSkillCraftId = 90_510;
        crafts[missingSkillCraftId] = new Craft
        {
            Id = missingSkillCraftId,
            SkillId = 99_999,
            ReqDoodadId = 0,
            ActabilityLimit = 0,
            CraftMaterials = [],
            CraftProducts = []
        };

        var request = actor.Craft(missingSkillCraftId, benchObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("missing skill");
    }

    [Test]
    public async Task Craft_MaterialsNotInBag_Rejected()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("craft-nomats-1");
        GameplayActorTestRig.SeedCraftSurface();
        var benchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor);
        actor.Character.LaborPower = 100;
        // NO material grant — the engine's bag-scope rule must refuse.

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("materials not present in bag");
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
    }

    [Test]
    public async Task Craft_BenchNotFound_Rejected()
    {
        var (actor, _, _) = CreateCraftRig("craft-nobench-1");

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, 0x7FFFFFFFu);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("not found in world");
    }

    [Test]
    public async Task Craft_WrongBenchTemplate_Rejected()
    {
        var (actor, session, _) = CreateCraftRig("craft-wrongbench-1");
        // A bench of a DIFFERENT template than the recipe's req_doodad_id.
        var wrongBenchObjId = GameplayActorTestRig.SpawnCraftBench(session, actor,
            GameplayActorTestRig.CraftWrongBenchTemplateId);

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, wrongBenchObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("requires bench template");
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(2);
    }

    [Test]
    public async Task Craft_BenchOutOfRange_Rejected()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-far-1");
        // Bench spawned 1 m in front of the actor; move the actor far away
        // (skill MaxRange = 100) — the engine range gate must refuse.
        GameplayActorTestRig.SetPosition(actor, new System.Numerics.Vector3(500f, 0f, 0f));

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("out of range");
    }

    [Test]
    public async Task Craft_NotEnoughLabor_Rejected()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-nolabor-1");
        // The rig craft costs 10 labor; the rig seeds 100. Starve it.
        actor.Character.LaborPower = 5;

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail).Contains("labor");
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
    }

    [Test]
    public async Task Craft_SameKeyRetry_RejectedPreFlight_NoDuplicateGrant()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-samekey-1");

        var first = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId, idempotencyKey: "craft-samekey-1");
        GameplayActorTestRig.CompleteCraftStep(actor, benchObjId);
        actor.Tick(TimeSpan.Zero);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);

        // Same-key retry: refused PRE-FLIGHT (StateTransition), no Running
        // transition on the duplicate's record, engine never re-entered.
        var retry = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId, idempotencyKey: "craft-samekey-1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.StateChanges.Any(s => s.StartsWith("Running"))).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task Craft_FreshKeyRetry_MaterialsConsumedBackstop()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-freshkey-1");

        var first = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId, idempotencyKey: "craft-freshkey-a");
        GameplayActorTestRig.CompleteCraftStep(actor, benchObjId);
        actor.Tick(TimeSpan.Zero);
        await Assert.That(first.State).IsEqualTo(ActorLifecycleState.Completed);

        // Fresh-key retry: the consumed materials are the engine-true
        // backstop — nothing left to craft, RejectedAction, no duplicate.
        var retry = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId, idempotencyKey: "craft-freshkey-b");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(retry.Detail).Contains("materials not present in bag");
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(0);
    }

    [Test]
    public async Task Craft_TimeoutWhileQueueActive_TimedOut_KeyStaysLocked()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-timeout-1");

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId,
            timeout: TimeSpan.FromSeconds(1), idempotencyKey: "craft-timeout-1");
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsTrue();

        // Budget exhausted while the engine queue is still active.
        actor.Tick(TimeSpan.FromSeconds(2));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Starvation);

        // The key stays locked: a same-key retry is refused pre-flight even
        // though the engine step is still queued.
        var retry = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId,
            timeout: TimeSpan.FromSeconds(1), idempotencyKey: "craft-timeout-1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        // The engine step that lands afterwards is never re-run by a retry:
        // complete it, then a fresh-key retry finds the materials consumed.
        GameplayActorTestRig.CompleteCraftStep(actor, benchObjId);
        actor.Tick(TimeSpan.Zero);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);

        var fresh = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId, idempotencyKey: "craft-timeout-2");
        await Assert.That(fresh.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(fresh.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
    }

    [Test]
    public async Task Craft_Interrupt_Interrupted()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-interrupt-1");

        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);

        var interrupted = actor.Interrupt(request.TraceId);
        await Assert.That(interrupted).IsTrue();
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
        // The engine queue is engine truth and keeps running; the actor just
        // stops watching it (the step lands or not — protected either way).
        await Assert.That(actor.AuditTrace[^1].Result).IsEqualTo(ActorLifecycleState.Interrupted);
    }
}
