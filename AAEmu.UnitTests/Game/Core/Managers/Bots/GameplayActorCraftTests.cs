using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Packets;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.Units;

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
///   - timeout or interruption before the native effect conserves materials,
///     products and labor after the original callback deadline;
///   - a committed native step remains Completed; stale cancellation preserves
///     a foreign replacement skill or same-recipe craft queue.
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
    public async Task Craft_TimeoutBeforeNativeEffect_ConservesResourcesAndKeepsKeyLocked()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-timeout");
        using var fixture = new NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId,
            timeout: TimeSpan.FromSeconds(1), idempotencyKey: "craft-native-timeout");
        actor.Tick(TimeSpan.FromSeconds(2));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.TimedOut);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.Starvation);
        await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(350));
        await AssertConserved(actor);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
        await Assert.That(actor.Character.SkillTask).IsNull();
        var retry = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId,
            idempotencyKey: "craft-native-timeout");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
    }

    [Test]
    public async Task Craft_InterruptBeforeNativeEffect_ConservesResourcesAfterOriginalDeadline()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-interrupt");
        using var fixture = new NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);
        await Assert.That(actor.Interrupt(request.TraceId)).IsTrue();
        await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(350));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await AssertConserved(actor);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
        await Assert.That(actor.Character.SkillTask).IsNull();
    }

    [Test]
    public async Task Craft_NativeCallback_CompletesWithExactMaterialProductAndLaborDeltas()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-complete");
        using var fixture = new NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Running);
        await AssertConserved(actor);
        await scheduler.WaitForTerminal(actor, request);
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
        await AssertCommitted(actor);
        await Assert.That(((CraftResult)request.Result!).Products.Single().Amount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Craft_NativeCommitBeforeCancellationOrTimeout_ReportsCompleted(bool timeout)
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-committed");
        using var fixture = new NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId,
            timeout: TimeSpan.FromSeconds(1));
        await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
        await AssertCommitted(actor);
        if (timeout)
            actor.Tick(TimeSpan.FromSeconds(2));
        else
            actor.PreemptCurrent("late cancellation after native commit");
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
        await Assert.That(((CraftResult)request.Result!).Products.Single().Amount).IsEqualTo(1);
    }

    [Test]
    public async Task Craft_StaleCancellation_PreservesForeignSameRecipeQueueAndSkill()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-same-recipe");
        using var fixture = new NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);
        var oldSkill = actor.Character.SkillTask.Skill;
        var recipe = CraftManager.Instance.GetCraftById(GameplayActorTestRig.CraftTestCraftId);
        actor.Character.Craft.Craft(recipe, 1, benchObjId);
        var foreignTask = actor.Character.SkillTask;
        await Assert.That(ReferenceEquals(foreignTask.Skill, oldSkill)).IsFalse();
        await Assert.That(foreignTask.Skill.Template.Id).IsEqualTo(oldSkill.Template.Id);
        actor.PreemptCurrent("stale owner release");
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(ReferenceEquals(actor.Character.SkillTask, foreignTask)).IsTrue();
        await Assert.That(foreignTask.Skill.Cancelled).IsFalse();
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsTrue();
        await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
        await AssertCommitted(actor);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
    }

    [Test]
    public async Task Craft_StaleCancellation_PreservesForeignReplacementSkill()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-foreign-skill");
        using var fixture = new NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);
        var template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.TestSkillId);
        var previousCastTime = template.CastingTime;
        var foreignSkill = new Skill(template);
        try
        {
            template.CastingTime = 5000;
            var caster = SkillCaster.GetByType(SkillCasterType.Unit);
            caster.ObjId = actor.Character.ObjId;
            var target = SkillCastTarget.GetByType(SkillCastTargetType.Unit);
            target.ObjId = actor.Character.ObjId;
            foreignSkill.Use(actor.Character, caster, target, null, false, out _);
            var foreignTask = actor.Character.SkillTask;
            actor.PreemptCurrent("stale craft cancellation");
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
            await Assert.That(ReferenceEquals(actor.Character.SkillTask, foreignTask)).IsTrue();
            await Assert.That(foreignSkill.Cancelled).IsFalse();
            await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(350));
            await AssertConserved(actor);
            await Assert.That(ReferenceEquals(actor.Character.SkillTask, foreignTask)).IsTrue();
        }
        finally
        {
            foreignSkill.Stop(actor.Character);
            template.CastingTime = previousCastTime;
        }
    }

    [Test]
    public async Task Craft_UnobservedNativeQueue_ContinuesOrdinaryCompletion()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-unobserved");
        using var fixture = new NativeCraftSkill();
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        actor.Character.Craft.Craft(CraftManager.Instance.GetCraftById(GameplayActorTestRig.CraftTestCraftId), 1, benchObjId);
        await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
        await AssertCommitted(actor);
        await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
    }

    [Test]
    public async Task Craft_ForeignSkillStartedDuringNativeEffect_SurvivesNaturalCompletion()
    {
        var (actor, _, benchObjId) = CreateCraftRig("craft-native-foreign-during-effect");
        using var effect = new PausedCraftEffect(afterCommit: false);
        using var fixture = new NativeCraftSkill(effect, effectDelay: 100);
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);
        var template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.TestSkillId);
        var previousCastTime = template.CastingTime;
        var foreignSkill = new Skill(template);
        try
        {
            await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
            await Assert.That(effect.Entered.IsSet).IsTrue();
            template.CastingTime = 5000;
            var caster = SkillCaster.GetByType(SkillCasterType.Unit);
            caster.ObjId = actor.Character.ObjId;
            var target = SkillCastTarget.GetByType(SkillCastTargetType.Unit);
            target.ObjId = actor.Character.ObjId;
            foreignSkill.Use(actor.Character, caster, target, null, false, out _);
            var foreignTask = actor.Character.SkillTask;
            effect.Release.Set();
            await scheduler.WaitForTerminal(actor, request);
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed, request.Detail ?? "");
            await AssertCommitted(actor);
            await Assert.That(foreignSkill.Cancelled).IsFalse();
            await Assert.That(ReferenceEquals(actor.Character.SkillTask, foreignTask)).IsTrue();
        }
        finally
        {
            effect.Release.Set();
            await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(100));
            foreignSkill.Stop(actor.Character);
            template.CastingTime = previousCastTime;
        }
    }

    [Test]
    public async Task Craft_CancellationWaitingDuringDelayedNativeCommit_ReportsCompleted()
    {
        // Both contenders reach the existing observed lock. Repeating this
        // bounded interleaving catches an unprotected ApplyEffects/EndSkill gap.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var (actor, _, benchObjId) = CreateCraftRig($"craft-native-commit-race-{attempt}");
            using var effect = new PausedCraftEffect(afterCommit: true);
            using var fixture = new NativeCraftSkill(effect, effectDelay: 100);
            using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
            var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, benchObjId);
            var ownedSkill = actor.Character.SkillTask.Skill;
            System.Threading.Tasks.Task? cancellation = null;
            try
            {
                await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(350));
                await Assert.That(effect.Entered.IsSet).IsTrue();
                await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
                using var started = new ManualResetEventSlim();
                cancellation = System.Threading.Tasks.Task.Run(() =>
                {
                    ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
                    started.Set();
                    var contention = System.Diagnostics.Stopwatch.StartNew();
                    while (!Monitor.TryEnter(ownedSkill.ExecutionLock))
                    {
                        if (contention.Elapsed > TimeSpan.FromSeconds(5))
                            throw new TimeoutException("native craft cancellation could not enter its execution lock");
                        Thread.SpinWait(1);
                    }
                    try
                    {
                        actor.PreemptCurrent("cancellation waiting on committed native effect");
                    }
                    finally
                    {
                        Monitor.Exit(ownedSkill.ExecutionLock);
                    }
                });
                await Assert.That(started.Wait(TimeSpan.FromSeconds(5))).IsTrue();
                await System.Threading.Tasks.Task.Delay(25);
                await Assert.That(cancellation.IsCompleted).IsFalse();
                effect.Release.Set();
                await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
                await scheduler.PumpFor(actor, TimeSpan.FromMilliseconds(100));
                await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed,
                    $"native commit must win pending cancellation, attempt {attempt}: {request.Detail}");
                await AssertCommitted(actor);
            }
            finally
            {
                effect.Release.Set();
                if (cancellation != null)
                    await cancellation.WaitAsync(TimeSpan.FromSeconds(5));
                ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
                await scheduler.PumpFor(null, TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private sealed class PausedCraftEffect(bool afterCommit) : CraftEffect, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public override void Apply(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
            CastAction castObj, EffectSource source, SkillObject skillObject, DateTime time,
            CompressedGamePackets packetBuilder = null)
        {
            WorldInteraction = WorldInteractionType.CraftStart;
            if (!afterCommit)
                Pause();
            base.Apply(caster, casterObj, target, targetObj, castObj, source, skillObject, time, packetBuilder);
            if (afterCommit)
                Pause();
        }

        private void Pause()
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("native CraftEffect barrier was not released");
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private static async Task AssertConserved(GameplayActor actor)
    {
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(2);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(0);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)100);
    }

    private static async Task AssertCommitted(GameplayActor actor)
    {
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftMaterialTemplateId)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(actor, GameplayActorTestRig.CraftProductTemplateId)).IsEqualTo(1);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)90);
    }

    [Test]
    public async Task Craft_NativeCooldownRefusal_DrainsWithoutCharges_AndReportsOrdinaryFailurePacket()
    {
        var (actor, _, bench) = CreateCraftRig("native-craft-refusal");
        var capture = new AAEmu.UnitTests.Game.Core.Managers.PacketCaptureSession();
        actor.Character.Connection = new AAEmu.Game.Core.Network.Connections.GameConnection(capture) { ActiveChar = actor.Character };
        using var native = new NativeCraftSkill();
        var template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.CraftTestSkillId);
        var previousCooldown = template.CooldownTime;
        template.CooldownTime = 1000;
        actor.Character.Cooldowns.AddCooldown(template.Id, 1000); // Initial ordinary native readiness fixture.
        try
        {
            var request = actor.Craft(GameplayActorTestRig.CraftTestCraftId, bench);
            actor.Tick(TimeSpan.Zero);
            await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Interrupted);
            await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
            await AssertConserved(actor);
            await AssertFailurePacket(capture, actor, bench, SkillResult.CooldownTime);
        }
        finally
        {
            template.CooldownTime = previousCooldown;
            actor.Character.Cooldowns.RemoveCooldown(template.Id);
            actor.PreemptCurrent("native refusal fixture cleanup");
        }
    }

    [Test]
    public async Task OrdinaryCharacterCraft_NativeRefusal_PreservesForeignUnobservedSkillTask()
    {
        var (actor, _, bench) = CreateCraftRig("ordinary-craft-foreign");
        var capture = new AAEmu.UnitTests.Game.Core.Managers.PacketCaptureSession();
        actor.Character.Connection = new AAEmu.Game.Core.Network.Connections.GameConnection(capture) { ActiveChar = actor.Character };
        using var native = new NativeCraftSkill();
        var template = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.CraftTestSkillId);
        var previousCooldown = template.CooldownTime;
        template.CooldownTime = 1000;
        actor.Character.Cooldowns.AddCooldown(template.Id, 1000);
        var foreign = new Skill(new SkillTemplate { Id = 99110, CastingTime = 5000, MaxRange = 100, IgnoreGlobalCooldown = true });
        try
        {
            var result = foreign.Use(actor.Character, new SkillCasterUnit(actor.Character.ObjId),
                new SkillCastUnitTarget(actor.Character.ObjId), new SkillObject(), false, out _);
            await Assert.That(result).IsEqualTo(SkillResult.Success);
            var foreignTask = actor.Character.SkillTask;
            await Assert.That(foreignTask).IsNotNull();
            actor.Character.Craft.Craft(CraftManager.Instance.GetCraftById(GameplayActorTestRig.CraftTestCraftId), 1, bench);
            await Assert.That(actor.Character.Craft.IsCraftQueueActive).IsFalse();
            await Assert.That(ReferenceEquals(actor.Character.SkillTask, foreignTask)).IsTrue();
            await Assert.That(foreign.Cancelled).IsFalse();
            await AssertConserved(actor);
            await AssertFailurePacket(capture, actor, bench, SkillResult.CooldownTime);
        }
        finally
        {
            if (ReferenceEquals(actor.Character.SkillTask?.Skill, foreign)) foreign.Stop(actor.Character);
            template.CooldownTime = previousCooldown;
            actor.Character.Cooldowns.RemoveCooldown(template.Id);
        }
    }

    private static async Task AssertFailurePacket(
        AAEmu.UnitTests.Game.Core.Managers.PacketCaptureSession capture,
        GameplayActor actor, uint bench, SkillResult result)
    {
        var caster = SkillCaster.GetByType(SkillCasterType.Unit);
        caster.ObjId = actor.Character.ObjId;
        var target = SkillCastTarget.GetByType(SkillCastTargetType.Doodad);
        target.ObjId = bench;
        var expected = new AAEmu.Game.Core.Packets.G2C.SCSkillStartedPacket(
            GameplayActorTestRig.CraftTestSkillId, 0, caster, target, null, null)
            .SetSkillResult(result).Write(new AAEmu.Commons.Network.PacketStream()).GetBytes();
        await Assert.That(capture.CapturedPackets.Any(bytes => bytes.Length >= 8
            && BitConverter.ToUInt16(bytes, 6) == AAEmu.Game.Core.Packets.G2C.SCOffsets.SCSkillStartedPacket
            && bytes.AsSpan(8).SequenceEqual(expected))).IsTrue();
    }

    internal sealed class NativeCraftSkill : IDisposable
    {
        private readonly SkillTemplate _skill = SkillManager.Instance.GetSkillTemplate(GameplayActorTestRig.CraftTestSkillId);
        private readonly int _castingTime;
        private readonly int _effectDelay;
        private readonly List<SkillEffect> _effects;

        public NativeCraftSkill(CraftEffect? effect = null, int effectDelay = 0)
        {
            _castingTime = _skill.CastingTime;
            _effectDelay = _skill.EffectDelay;
            _effects = _skill.Effects.ToList();
            _skill.CastingTime = 150;
            _skill.EffectDelay = effectDelay;
            _skill.Effects = [new SkillEffect
            {
                Template = effect ?? new CraftEffect { WorldInteraction = WorldInteractionType.CraftStart },
                Chance = 100, Friendly = true, NonFriendly = true, AlwaysHit = true,
                StartLevel = 0, EndLevel = byte.MaxValue,
                ApplicationMethod = SkillEffectApplicationMethod.Target
            }];
        }

        public void Dispose()
        {
            _skill.CastingTime = _castingTime;
            _skill.EffectDelay = _effectDelay;
            _skill.Effects = _effects;
        }
    }
}
