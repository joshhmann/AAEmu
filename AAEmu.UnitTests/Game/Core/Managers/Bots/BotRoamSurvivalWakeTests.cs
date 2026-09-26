using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// THE ELECTED-WAKE SURVIVAL GATE (BotRoamStepExecutor.StepSurvivalWake).
///
/// The live gate finding: the survival veto's only consumers lived inside the
/// quest leg loop, but at critical hp with fight evidence the arbiter elects
/// recovery.rest (85 &gt; quest 58) or nothing at all (the quest and recovery
/// modules both deny in battle) — so the rule was never evaluated on the wake it
/// was written for. What is pinned here is the behavior the gate must now
/// produce on the wake the arbiter actually elects:
///
///  - CRITICAL HP IN A FIGHT dispatches the flee leg (Move to the shared
///    <see cref="TravelBrain.SafeAnchor"/>) even when the quest module was
///    denied and another module owns the activity;
///  - a live flee leg that still serves this wake's anchor is HELD, never
///    restarted (progress is never lost);
///  - the veto fact is PUBLISHED at the elected-wake layer, so the existing
///    quest-leg consumers read this wake's fact;
///  - RECOVERY stays the recovery module's (the gate dispatches nothing and
///    leaves the election untouched);
///  - HEALTHY wakes are unchanged: no flee leg, no veto, and the module's own
///    legs still land exactly as before.
/// </summary>
[NotInParallel]
public class BotRoamSurvivalWakeTests
{
    private const uint Boar3475 = 3475;

    [Before(Test)]
    public void SetUp()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime,
        FakeTimeProvider Clock, HeadlessSession Session) CreateRig(
        string name, string? activity, Func<Character, float, IEnumerable<Npc>>? nearbyNpcs = null)
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            RoamSpeed = 2f,
            EnableWildlifeHunt = false,
            ActiveActivityProvider = activity == null ? null : _ => activity,
            NearbyNpcProvider = nearbyNpcs
        };
        return (executor, actor, runtime, clock, session);
    }

    /// <summary>
    /// Sets a character's hp to a fraction of its REAL maximum (Character.MaxHp is
    /// formula-derived — writing the property directly is not possible, so the
    /// fraction is computed from the value the survival chain will read).
    /// </summary>
    private static void SetHpFraction(Character character, float fraction)
        => character.Hp = Math.Max(0, (int)(character.MaxHp * fraction));

    /// <summary>
    /// The live-gate shape: the bot is critically wounded WITH a selected target
    /// (the code's own fight-evidence contract), and the arbiter elects a
    /// NON-quest activity — the exact wake on which the veto used to be starved.
    /// The gate must still put the flee leg on the actor.
    /// </summary>
    [Test]
    public async Task CriticalHpWithFightEvidence_DispatchesFleeLeg_EvenWhenQuestLegIsDenied()
    {
        var (executor, actor, runtime, clock, session) = CreateRig("surv-flee-elect", "recovery.rest");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(Boar3475);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(10f, 0f, 0f));

        SetHpFraction(actor.Character, SurvivalBrain.FleeHpThreshold * 0.5f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        // The flee leg is live on the actor, owned by the survival gate.
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue()
            .Because("the flee leg must be dispatched on the wake the arbiter gave to recovery.rest");
        await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo(BotRoamStepExecutor.SurvivalFleeMoveOwner);
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsTrue();

        // The destination is the shared anchor, and it walks AWAY from the boar.
        var expected = TravelBrain.SafeAnchor(here, here + new Vector3(10f, 0f, 0f), TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(actor.ActiveRequest.Destination).IsEqualTo(expected);
        await Assert.That(actor.ActiveRequest.Destination!.Value.X).IsLessThan(here.X);
    }

    /// <summary>
    /// The gate publishes the fact at the elected-wake layer, so the quest legs'
    /// own precondition reads THIS wake's verdict rather than a stale one.
    /// </summary>
    [Test]
    public async Task CriticalHpWithFightEvidence_PublishesTheVetoForTheQuestLegConsumers()
    {
        var (executor, actor, runtime, clock, session) = CreateRig("surv-flee-publish", "conflict.23163");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));
        var boarObjId = session.SpawnNpc(Boar3475);
        SetHpFraction(actor.Character, 0.10f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(SurvivalVetoState.IsVetoed(actor.ActorId)).IsTrue();
        await Assert.That(SurvivalVetoState.TryGet(actor.ActorId, out var record)).IsTrue();
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.HpCritical);
        await Assert.That(record.Verdict).IsEqualTo(SurvivalVerdict.Flee);
    }

    /// <summary>
    /// A live flee leg that still serves this wake's anchor keeps its progress:
    /// the escape is never restarted wake after wake (the anchor is recomputed
    /// from the actor's own position, so an unconditional re-issue would pin the
    /// bot in place).
    /// </summary>
    [Test]
    public async Task LiveFleeLegServingTheAnchor_IsHeldNotRestarted()
    {
        var (executor, actor, runtime, clock, session) = CreateRig("surv-flee-hold", null);
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(Boar3475);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(10f, 0f, 0f));
        SetHpFraction(actor.Character, 0.15f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var firstLeg = actor.ActiveRequest;
        await Assert.That(firstLeg).IsNotNull();

        // Second wake at the SAME position: the anchor is unchanged, so the live
        // leg serves it and must be held (identical request instance).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(firstLeg);
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsTrue();
    }

    /// <summary>
    /// RECOVER is a DEMAND, never a dispatch and never a veto: an out-of-combat
    /// low-hp bot with no fight on gets no flee leg from the gate, no veto, and
    /// the arbiter's own election is left alone.
    /// </summary>
    [Test]
    public async Task OutOfCombatLowHp_DispatchesNothingAndDoesNotVeto()
    {
        var (executor, actor, runtime, clock, _) = CreateRig("surv-recover", "recovery.rest");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));
        // Below the 0.75 recover line, above the 0.20 flee line.
        SetHpFraction(actor.Character, 0.40f);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move)).IsFalse();
        await Assert.That(SurvivalVetoState.IsVetoed(actor.ActorId)).IsFalse();
        // The verdict itself is the brain's own (recover at 0.40 with no fight on);
        // the gate's cheap path left the wake untouched and cleared any stale fact.
        await Assert.That(SurvivalVetoState.TryGet(actor.ActorId, out _)).IsFalse();
        var decision = SurvivalBrain.Decide(SurvivalBrainPlanner.SnapshotInputs(
            actor.ActorId, actor.Character.Hp, actor.Character.MaxHp, 0u));
        await Assert.That(decision.IsRecover).IsTrue();
        await Assert.That(decision.Reason).IsEqualTo(SurvivalReason.OutOfCombatWounded);
        await Assert.That(decision.Veto).IsFalse();
    }

    /// <summary>
    /// HEALTHY wake: byte-identical behavior. No flee leg, no veto, and the
    /// existing route leg still goes out exactly as it did before the gate.
    /// </summary>
    [Test]
    public async Task HealthyWake_IssuesNoFleeLeg_AndLeavesTheRouteLegIntact()
    {
        var (executor, actor, runtime, clock, _) = CreateRig("surv-healthy", "presence.roam");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0f, 0f, 0f));
        var route = new BotPath([new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f)], BotPath.LoopMode.Loop);
        executor.SetRoamRoute(runtime.Character, route);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsFalse();
        await Assert.That(SurvivalVetoState.IsVetoed(actor.ActorId)).IsFalse();
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(actor.ActiveRequest!.MoveOwner).IsNotEqualTo(BotRoamStepExecutor.SurvivalFleeMoveOwner);
    }

    /// <summary>
    /// The flee owns the wake: even with a quest activity elected and a quest leg
    /// that would otherwise run, a critical-in-fight verdict keeps the wake — no
    /// AdvanceQuest lands this wake.
    /// </summary>
    [Test]
    public async Task CriticalHpWithFightEvidence_SupersedesTheQuestLeg()
    {
        var (executor, actor, runtime, clock, session) = CreateRig("surv-flee-quest", "quest.progress");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(Boar3475);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(10f, 0f, 0f));
        SetHpFraction(actor.Character, 0.15f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.QuestLegActive ?? false).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AdvanceQuest)).IsFalse();
    }

    /// <summary>
    /// A critical-in-fight bot with nothing nearby to run from still breaks
    /// contact: an unresolvable threat position falls back to the anchor's own
    /// documented escape direction rather than refusing the flee.
    /// </summary>
    [Test]
    public async Task CriticalHpWithAnUnresolvableThreat_StillDispatchesAFleeLeg()
    {
        var (executor, actor, runtime, clock, _) = CreateRig("surv-flee-ghost", "recovery.rest");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        SetHpFraction(actor.Character, 0.10f);
        // A committed engagement naming an objId no world resolve can find.
        CombatBrainEngagement.Publish(actor.ActorId, 0xDEAD, 100, Vector3.Zero, 50f,
            crowdControlled: false, disengaging: false, DateTime.UtcNow);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsTrue();
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();

        // The anchor's own documented fail-closed path still defines an escape
        // direction, so the fleet leg never receives a NaN destination.
        var expected = TravelBrain.SafeAnchor(here, Vector3.Zero, TravelBrain.RetreatAnchorDistanceM);
        await Assert.That(actor.ActiveRequest!.Destination).IsEqualTo(expected);
        await Assert.That(Vector3.Distance(here, actor.ActiveRequest.Destination!.Value))
            .IsCloseTo(TravelBrain.RetreatAnchorDistanceM, 0.001f);
    }

    /// <summary>
    /// The combat brain's own published retreat reaches the elected-wake gate:
    /// a Disengaging engagement flees even at healthy hp (the fact IS the
    /// retreat, exactly as the survival chain documents).
    /// </summary>
    [Test]
    public async Task PublishedCombatRetreat_ReachesTheElectedWakeGate()
    {
        var (executor, actor, runtime, clock, _) = CreateRig("surv-flee-disengage", null);
        GameplayActorTestRig.SetPosition(actor, new Vector3(100f, 100f, 10f));
        CombatBrainEngagement.Publish(actor.ActorId, 0xBEEF, 100, Vector3.Zero, 50f,
            crowdControlled: false, disengaging: true, DateTime.UtcNow);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(SurvivalVetoState.TryGet(actor.ActorId, out var record)).IsTrue();
        await Assert.That(record.Reason).IsEqualTo(SurvivalReason.CombatRetreat);
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsTrue();
    }

    /// <summary>
    /// An incapacitated actor vetoes and owns the wake, but dispatches nothing:
    /// the engine's death path owns a down actor, and no leg (quest included) may
    /// take the wake from it.
    /// </summary>
    [Test]
    public async Task IncapacitatedActor_VetoesAndOwnsTheWake_ButDispatchesNoLeg()
    {
        var (executor, actor, runtime, clock, _) = CreateRig("surv-down", "quest.progress");
        GameplayActorTestRig.SetPosition(actor, new Vector3(50f, 50f, 10f));
        SetHpFraction(actor.Character, 0f);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move)).IsFalse();
        await Assert.That(SurvivalVetoState.IsVetoed(actor.ActorId)).IsTrue();
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.QuestLegActive ?? false).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AdvanceQuest)).IsFalse();
    }

    /// <summary>
    /// A cause that clears must clear the FACT: once the fight is over and the bot is
    /// healthy again, the wake stops vetoing and the stale published veto from the
    /// critical wake is dropped — a consumer can never inherit a veto whose cause is
    /// gone.
    /// </summary>
    [Test]
    public async Task RecoveredWake_ClearsTheStalePublishedVeto()
    {
        var (executor, actor, runtime, clock, session) = CreateRig("surv-clear", "presence.roam");
        var here = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, here);
        var boarObjId = session.SpawnNpc(Boar3475);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, here + new Vector3(10f, 0f, 0f));

        // Wake 1: critical hp with fight evidence → veto published.
        SetHpFraction(actor.Character, 0.10f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(SurvivalVetoState.IsVetoed(actor.ActorId)).IsTrue();

        // Wake 2: healed, target dropped → the cause is gone.
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.CurrentTarget = null;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(SurvivalVetoState.IsVetoed(actor.ActorId)).IsFalse();
        await Assert.That(executor.GetBotState(runtime.CharacterId)?.SurvivalWakeOwned).IsFalse();
    }
}
