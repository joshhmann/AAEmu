using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Game.Quests.Scenario;

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

    /// <summary>The copper-bootstrap quest the return leg is wired for.</summary>
    private const uint Quest251 = 251;

    /// <summary>The quest 251 return-report reporter (the t1 manifest's Ready-component npc).</summary>
    private const uint Reporter3512 = 3512;

    /// <summary>The 251 gather objective's item, held at its need count (3) by the return fixture.</summary>
    private const uint Meat4058 = 4058;

    /// <summary>The 3475→4530→4058 loot link the 251 fixture row derives its prey/pack through.</summary>
    private const uint Pack4530 = 4530;

    /// <summary>The quest-return leg's movement-owner tag (the lane vocabulary's own spelling).</summary>
    private const string ReturnMoveOwner = "RETURN_MOVE_TO_UNIT";

    [Before(Test)]
    public void SetUp()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
        // The return leg's journey memory is process-wide (the same discipline
        // the sibling QuestReturnInteractTests rig applies): a leftover intent
        // from another test would hand this wake the previous journey's budget.
        TravelIntentStore.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
        TravelIntentStore.ClearAll();
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

    // -------------------------------------------------- flee vs return arbitration

    /// <summary>
    /// THE MOVE-VERB ARBITRATION (the gap the elected-wake gate left open): on the
    /// wake where a READY 251 return leg has a live MoveToUnit leg and the survival
    /// gate elects a flee, BOTH legs claim the Move verb. The flee must win the
    /// wake, the live return leg must be retired exactly once (named by the flee's
    /// own retrack detail), and the quest tick must not run — one Move leaves the
    /// dispatcher this wake, and it is the flee.
    /// </summary>
    [Test]
    public async Task FleeAndReturnClaimTheMoveVerb_FleeWinsAndTheReturnLegIsRetired()
    {
        var (executor, actor, runtime, clock, session, reporterObjId) =
            CreateReadyReturnRig("surv-arb-flee", "quest.progress");
        var boarObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;

        // Wake 1: an ordinary healthy wake — the return leg owns it and dispatches
        // its closing MoveToUnit (the reporter is outside the 25 m return gate).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var returnLeg = actor.ActiveRequest;
        await Assert.That(returnLeg is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(returnLeg!.MoveOwner).IsEqualTo(ReturnMoveOwner);
        await Assert.That(returnLeg.TargetId).IsEqualTo(reporterObjId);
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        var landedReturnTick = questRuntime!.LastResult;
        // The return Move is the quest tick's OWN landed work (a Running leg is
        // still the quest leg's work — QuestLegActive is the terminal-landed flag
        // and stays false, exactly as the sibling supersede test pins).
        await Assert.That(landedReturnTick!.WorkSelected).IsTrue();
        await Assert.That(landedReturnTick.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(landedReturnTick.Request!.MoveOwner).IsEqualTo(ReturnMoveOwner);

        // Wake 2: critical hp WITH fight evidence — both legs now claim Move.
        SetHpFraction(actor.Character, SurvivalBrain.FleeHpThreshold * 0.5f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);
        var traceBefore = actor.AuditTrace.Count;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        // The flee wins the wake and OWNS it.
        await Assert.That(state.SurvivalWakeOwned).IsTrue();
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo(BotRoamStepExecutor.SurvivalFleeMoveOwner);
        await Assert.That(actor.ActiveRequest).IsNotSameReferenceAs(returnLeg);

        // The preempted return leg retired exactly once, with the flee's own
        // retrack detail — and it was never re-dispatched.
        var returnRows = actor.AuditTrace.Where(r => r.Action == ActorActionType.Move
            && r.MoveOwner == ReturnMoveOwner && r.TargetId == reporterObjId).ToList();
        await Assert.That(returnRows.Count).IsEqualTo(1);
        await Assert.That(returnRows[0].Result).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(returnRows[0].Detail).Contains("survival flee retrack");

        // Exactly ONE Move left the dispatcher this wake (the retirement row); the
        // flee leg is live, so this wake dispatched no second Move.
        var movesThisWake = actor.AuditTrace.Skip(traceBefore).Count(r => r.Action == ActorActionType.Move);
        await Assert.That(movesThisWake).IsEqualTo(1);

        // SurvivalWakeOwned consumed the quest tick: no quest leg landed and the
        // runtime was not ticked again this wake.
        await Assert.That(state.QuestLegActive).IsFalse();
        await Assert.That(questRuntime.LastResult).IsSameReferenceAs(landedReturnTick);
    }

    /// <summary>
    /// NO STRANDED LEG: the preempted return leg retires once and nothing resumes
    /// it behind the flee's back. While the veto still stands, the live flee leg is
    /// the actor's ONE live leg, held (never restarted) and in agreement with the
    /// executor's pending-leg record — no double-drive, no ghost return leg.
    /// </summary>
    [Test]
    public async Task PreemptedReturnLeg_LeavesNoStrandedLegAndNoDoubleDrive()
    {
        var (executor, actor, runtime, clock, session, _) =
            CreateReadyReturnRig("surv-arb-strand", "quest.progress");
        var boarObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.ActiveRequest?.MoveOwner).IsEqualTo(ReturnMoveOwner);

        SetHpFraction(actor.Character, SurvivalBrain.FleeHpThreshold * 0.5f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var fleeLeg = actor.ActiveRequest;
        await Assert.That(fleeLeg?.MoveOwner).IsEqualTo(BotRoamStepExecutor.SurvivalFleeMoveOwner);

        // Wake 3: the veto still stands and the anchor is unchanged — the flee leg
        // is held (same instance), the return leg is never resumed, and the two
        // readers of the live leg (actor + executor) name one leg.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.SurvivalWakeOwned).IsTrue();
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(fleeLeg);
        await Assert.That(ReferenceEquals(state.PendingLeg, actor.ActiveRequest)).IsTrue();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move
            && r.MoveOwner == ReturnMoveOwner)).IsEqualTo(1);
    }

    /// <summary>
    /// THE JOURNEY SURVIVES THE FLEE: the survival preemption is not a navigation
    /// failure, so the return intent keeps its identity and its repath budget, and
    /// once the flee clears the return leg re-issues on the same journey.
    ///
    /// EXPOSES A REAL BUG (reported, not fixed — ticket contract): the flee's own
    /// preemption stages the detail <c>"survival flee retrack"</c>
    /// (<c>BotRoamStepExecutor.StepSurvivalWake</c>), but
    /// <c>TravelLegDispatch.IsCallerRetirement</c> recognises only
    /// <c>"quest return retrack"</c> and <c>"stop requested"</c>, so
    /// <c>MapLegOutcome</c> reads the survival retirement as a FOREIGN interruption
    /// (<see cref="TravelLegOutcome.Interrupted"/>). <c>TravelBrain.Decide</c> then
    /// treats it as a navigation failure and spends a repath
    /// (<c>reason=Repathed:repaths=1</c>), so this assertion fails against current
    /// production. The budget is two, so two flee episodes reach
    /// <see cref="TravelTerminal.Unreachable"/> / <see cref="TravelReason.RepathExhausted"/>
    /// and the return journey is STICKILY abandoned although the legs never failed
    /// to navigate.
    /// </summary>
    [Test]
    public async Task ReturnJourneySurvivesTheFleePreemption_IntentStaysArmedAndTheLegReIssues()
    {
        var (executor, actor, runtime, clock, session, reporterObjId) =
            CreateReadyReturnRig("surv-arb-journey", "quest.progress");
        var boarObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;

        // Wake 1: the return leg arms the journey (a fresh budget, no terminal).
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.ActiveRequest?.MoveOwner).IsEqualTo(ReturnMoveOwner);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var armed)).IsTrue();
        await Assert.That(armed.Kind).IsEqualTo(TravelTargetKind.Unit);
        await Assert.That(armed.TargetObjId).IsEqualTo(reporterObjId);
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(armed.RepathCount).IsEqualTo(0);

        // Wake 2: the flee preempts the return leg. The survival layer touches
        // nothing travel-side, so the journey is still armed and unspent.
        SetHpFraction(actor.Character, SurvivalBrain.FleeHpThreshold * 0.5f);
        actor.Character.CurrentTarget = session.World.GetNpc(boarObjId);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(executor.GetBotState(runtime.CharacterId)!.SurvivalWakeOwned).IsTrue();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var afterFlee)).IsTrue();
        await Assert.That(afterFlee.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(afterFlee.RepathCount).IsEqualTo(0);

        // Heal and drop the fight: the flee leg finishes walking its 25 m out.
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.CurrentTarget = null;
        for (var wake = 0; wake < 20 && actor.ActiveRequest is { IsTerminal: false }; wake++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await executor.StepAsync(runtime, CancellationToken.None);
        }
        await Assert.That(actor.ActiveRequest is not { IsTerminal: false }).IsTrue()
            .Because("the flee leg must terminate so the return leg can take a wake again");

        // The next quest wake re-issues the return leg on the SAME journey.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo(ReturnMoveOwner);
        await Assert.That(actor.ActiveRequest.TargetId).IsEqualTo(reporterObjId);
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var resumedRuntime)).IsTrue();
        await Assert.That(resumedRuntime!.LastResult is { WorkSelected: true, SelectedAction: ActorActionType.Move }).IsTrue()
            .Because("the re-issued leg must be the quest tick's own landed return work");
        await Assert.That(resumedRuntime.LastResult!.Request!.MoveOwner).IsEqualTo(ReturnMoveOwner);
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var resumed)).IsTrue();
        await Assert.That(resumed.TargetObjId).IsEqualTo(reporterObjId);
        await Assert.That(resumed.Terminal).IsEqualTo(TravelTerminal.None);

        // THE SPEC: the survival preemption is NOT a navigation failure — the flee
        // must not spend the return journey's repath budget.
        var ret = executor.TryGetQuestRuntime(runtime.CharacterId, out var qr) && qr?.LastResult != null
            ? qr.LastResult.LegEvidence.FirstOrDefault(e => e.Leg == QuestLegId.Return).Detail ?? ""
            : "";
        await Assert.That(resumed.RepathCount).IsEqualTo(0)
            .Because($"a survival flee preemption is not a navigation failure; the resumed return leg's own diag was: {ret}");
    }

    // ------------------------------------------------------------ return fixture

    /// <summary>
    /// The arbitration rig: an ordinary headless bot (the same actor surface the
    /// survival tests drive) whose quest 251 is active and READY on a live reporter
    /// 3512, with the quest arbiter electing <c>quest.progress</c> so the quest leg
    /// actually runs on each wake. The 251 Ready row needs the 3475→4530→4058 loot
    /// link (the sibling QuestReturnInteractTests fixture) and the pilot singletons
    /// (real quest/item data), so both are staged exactly as that rig stages them.
    /// The turn-in withhold mirrors the sibling E2E seam: while the return slice
    /// withdraws nothing may quietly complete the quest out from under the test.
    /// </summary>
    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime,
        FakeTimeProvider Clock, HeadlessSession Session, uint ReporterObjId) CreateReadyReturnRig(
        string name, string activity)
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        SeedBoarLootLink();

        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        PlayerbotPilotRig.RegisterQuestItems(LoadManifest());
        GameplayActorTestRig.AttachCaptureConnection(actor);

        var accept = actor.AcceptQuest(Quest251, QuestAcceptorType.Npc, Reporter3512);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");
        GameplayActorTestRig.GrantItem(actor, Meat4058, 3);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = Quest251,
            ItemId = Meat4058,
            Count = 3
        });
        var advance = actor.AdvanceQuest(Quest251);
        if (advance.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"advance failed: {advance.State} {advance.Detail}");
        if (actor.Character.Quests.ActiveQuests.GetValueOrDefault(Quest251)?.Status != QuestStatus.Ready)
            throw new InvalidOperationException("quest 251 did not reach Ready");

        // The bot at (100,100,10); the reporter 30 m east (outside the 25 m return
        // gate, so the return leg dispatches a Move); the prey 10 m east (the flee
        // anchor walks west, away from it).
        var actorPos = new Vector3(100f, 100f, 10f);
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var reporterObjId = session.SpawnNpc(Reporter3512);
        var reporter = session.World.GetNpc(reporterObjId)!;
        reporter.Hp = 100;
        reporter.MaxHp = 100;
        GameplayActorTestRig.SetNpcPosition(session, reporterObjId, new Vector3(130f, 100f, 10f));
        region.AddObject(reporter);
        reporter.Region = region;

        var boarObjId = session.SpawnNpc(Boar3475);
        GameplayActorTestRig.SetNpcPosition(session, boarObjId, new Vector3(110f, 100f, 10f));

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
            // Deterministic ground: no terrain data, so the clamp never fights
            // the flee leg's arrival (0 = "no heightmap" per the seam contract).
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => activity,
            NearbyNpcProvider = (_, _) => []
        };
        executor.SetQuestTurnInWithheld(runtime.CharacterId, true);
        return (executor, actor, runtime, clock, session, reporterObjId);
    }

    /// <summary>
    /// Additive, missing-only seed of the 3475→4530→4058 loot link the 251
    /// fixture row resolves its prey/pack through (the same shape the sibling
    /// objective-path rigs use). Canonical pilot data is never clobbered: each
    /// row is added only when the lookup would otherwise resolve empty.
    /// </summary>
    private static void SeedBoarLootLink()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || rows.All(r => r.LootPackId != Pack4530))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = Boar3475,
                NpcId = Boar3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Boar3475] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(Pack4530, out var pack) || pack.Loots.All(l => l.ItemId != Meat4058))
        {
            var loot = new Loot
            {
                Id = Pack4530,
                Group = 0,
                ItemId = Meat4058,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = Pack4530,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[Pack4530] = new LootPack
                {
                    Id = Pack4530,
                    Loots = [loot],
                    LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
                    Groups = [],
                    ActabilityGroups = [],
                    GroupCount = 1,
                };
            }
            else
            {
                pack.Loots.Add(loot);
                if (pack.LootsByGroupNo.TryGetValue(0, out var group))
                    group.Add(loot);
                else
                    pack.LootsByGroupNo[0] = [loot];
            }
        }

        // The 251 row memo is derived from exactly these two tables: a staged
        // link must never be shadowed by a row memoized from the prior staging.
        QuestFixtureRow.InvalidateAll();
    }

    private static QuestScenarioManifest LoadManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null &&
               !Directory.Exists(Path.Combine(dir.FullName, ".git")) &&
               !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t1", $"{Quest251}.json");
        return QuestScenarioManifest.LoadFromFile(path);
    }
}
