using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// CastAt rig tests (M5 vocabulary extension — position-target skills).
///
/// Mirrors the Cast test conventions in GameplayActorTests and the
/// idempotency-retry conventions in GameplayActorB1ContractLayerTests:
/// the actor executes through the REAL engine path (Skill.Use with a
/// SkillCastPositionTarget — the same seam CSStartSkillPacket's Pos branch
/// drives, which is what fishing 21571 plot 809 rides), with the shared
/// lifecycle + audit + ledger machinery. The seeded pos skill carries a
/// minimal REAL plot tree so the plot-start seam runs headless.
/// </summary>
[NotInParallel]
public class GameplayActorCastAtTests
{
    private static readonly Vector3 WaterPosition = new(50f, 75f, 100f);

    /// <summary>Unique high-base world ids (same discipline as PlantActionsTests.RigWorld).</summary>
    private static uint s_nextWorldId = 0x5000_0000;

    /// <summary>
    /// Gives the session world a UNIQUE high-base instance id and registers
    /// it in the WorldManager registry. The Pos cast path clones the caster's
    /// transform into a detached position unit whose ctor resolves
    /// ParentWorld via WorldManager.GetWorld(InstanceId) and whose region is
    /// resolved through the same registry (Skill.SetInitialTarget) — the
    /// headless unregistered-world bypass CreateActor uses cannot serve it.
    /// Same registration shape as CropHarvestLoopRig.RegisterWorld /
    /// PlantActionsTests.RigWorld.
    /// </summary>
    private static void RegisterWorldForPosCast(HeadlessSession session)
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(AAEmu.Game.Models.Game.World.WorldInstance)
            .GetField("<Id>k__BackingField", Flags)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, AAEmu.Game.Models.Game.World.WorldInstance>)
            typeof(AAEmu.Game.Core.Managers.World.WorldManager)
                .GetField("_worlds", Flags)!
                .GetValue(AAEmu.Game.Core.Managers.World.WorldManager.Instance)!;
        worlds.TryAdd(session.World.Id, session.World);
        // Re-pin the character transform to the RENAMED world id.
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", Flags)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    /// <summary>Creates an actor that knows the seeded Pos-target skill.</summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreatePosCaster(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        RegisterWorldForPosCast(session);
        actor.Character.Skills.AddSkill(new SkillTemplate { Id = GameplayActorTestRig.TestPosSkillId }, 1, false);
        return (actor, session);
    }

    [Test]
    public async Task CastAt_UnknownSkill_Rejected()
    {
        var (actor, _) = CreatePosCaster("castat-1");

        var request = actor.CastAt(123_456, WaterPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("unknown skill")).IsTrue();
        // Rejected pre-flight: no Running transition, nothing executed.
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(actor.ActiveRequest).IsNull();
    }

    [Test]
    public async Task CastAt_NotLearnedSkill_Rejected()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("castat-2");
        // Fresh rig: TestPosSkillId exists as a template but was never learned
        // (and is not default/common), so the known-skill gate refuses.
        actor.Character.Skills.Skills.Clear();

        var request = actor.CastAt(GameplayActorTestRig.TestPosSkillId, WaterPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("not learned")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task CastAt_MissingReagent_RejectedBeforeEngineCall()
    {
        var (actor, _) = CreatePosCaster("castat-3");
        // The seeded pos skill requires 1x TestItemTemplateId (worm slot); the
        // bag is empty → refused BEFORE the engine call (no plot may start).

        var request = actor.CastAt(GameplayActorTestRig.TestPosSkillId, WaterPosition);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("missing reagent")).IsTrue();
        await Assert.That(request.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
    }

    [Test]
    public async Task CastAt_ValidPosTargetSkill_CompletesThroughRealEnginePath_AndStartsPlot()
    {
        var (actor, session) = CreatePosCaster("castat-4");
        // Stock the worm-slot reagent through the real acquisition path.
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 5);

        var request = actor.CastAt(GameplayActorTestRig.TestPosSkillId, WaterPosition);

        // The engine accepted the cast (SkillResult.Success at plot start —
        // the template is PlotOnly, exactly like fishing 21571).
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsEqualTo(SkillResult.Success);
        await Assert.That(actor.ActiveRequest).IsNull();

        await Assert.That(actor.AuditTrace.Count).IsEqualTo(1);
        var record = actor.AuditTrace[0];
        await Assert.That(record.Action).IsEqualTo(ActorActionType.CastAt);
        await Assert.That(record.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(record.StateChanges.Any(s => s.Contains("Running"))).IsTrue();

        // Plot-start proof: Template.Plot.RunAsync assigns the plot state to
        // the caster. The seeded tree keeps it alive ~TestPosPlotChildDelayMs,
        // so poll for the transition instead of racing a single read.
        var plotObserved = false;
        var guard = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < guard)
        {
            if (actor.Character.ActivePlotState != null)
            {
                plotObserved = true;
                break;
            }
            await Task.Delay(10);
        }
        await Assert.That(plotObserved).IsTrue();

        // The plot must also END cleanly (DoPlotEnd clears ActivePlotState).
        var ended = false;
        guard = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < guard)
        {
            if (actor.Character.ActivePlotState == null)
            {
                ended = true;
                break;
            }
            await Task.Delay(25);
        }
        await Assert.That(ended).IsTrue();
    }

    [Test]
    public async Task CastAt_SameKeyRetry_NeverDoubleExecutes()
    {
        var (actor, session) = CreatePosCaster("castat-5");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 5);

        // Original attempt with an explicit key: completes at plot start.
        var original = actor.CastAt(GameplayActorTestRig.TestPosSkillId, WaterPosition, idempotencyKey: "fishing:cast-1");
        await Assert.That(original.State).IsEqualTo(ActorLifecycleState.Completed);

        // Wait out the seeded plot so no background state bleeds into the retry assertions.
        var guard = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < guard && actor.Character.ActivePlotState != null)
            await Task.Delay(25);

        // Retry with the SAME key: refused BEFORE execution — the audit record
        // shows no Running transition (execution is the only place a cast or
        // reagent consumption can land), so the effect cannot duplicate.
        var retry = actor.CastAt(GameplayActorTestRig.TestPosSkillId, WaterPosition, idempotencyKey: "fishing:cast-1");
        await Assert.That(retry.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(retry.Failure).IsEqualTo(ActorFailureReason.StateTransition);
        await Assert.That(retry.Detail?.Contains(original.TraceId.ToString())).IsTrue();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Running"))).IsFalse();
        await Assert.That(retry.StateChanges.Any(s => s.Contains("Rejected"))).IsTrue();

        // The lock SURVIVES the refused duplicate: a third attempt is refused too.
        var third = actor.CastAt(GameplayActorTestRig.TestPosSkillId, WaterPosition, idempotencyKey: "fishing:cast-1");
        await Assert.That(third.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(third.Failure).IsEqualTo(ActorFailureReason.StateTransition);

        // FindByKey correlates back to the ORIGINAL completed attempt.
        var byKey = actor.FindByKey("fishing:cast-1");
        await Assert.That(byKey).IsNotNull();
        await Assert.That(byKey!.TraceId).IsEqualTo(original.TraceId);
        await Assert.That(byKey.Result).IsEqualTo(ActorLifecycleState.Completed);

        // Exactly ONE execution ever ran: one Completed record for the
        // original, two pre-flight Rejected records for the retries.
        var completions = actor.AuditTrace.Count(r => r.Action == ActorActionType.CastAt && r.Result == ActorLifecycleState.Completed);
        await Assert.That(completions).IsEqualTo(1);
    }

    [Test]
    public async Task CastAt_EngineRefusal_MapsToRejected()
    {
        var (actor, session) = CreatePosCaster("castat-6");
        GameplayActorTestRig.StockItem(session, GameplayActorTestRig.TestItemTemplateId, 5);
        // A non-finite position fails the input gate before any engine work.
        var request = actor.CastAt(GameplayActorTestRig.TestPosSkillId, new Vector3(float.NaN, 0f, 0f));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(request.Detail?.Contains("finite")).IsTrue();
    }
}
