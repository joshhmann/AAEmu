using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using System.Numerics;
using System.Reflection;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// G6 quest-owned combat: the AutoAttack proposal below pursuit (priority 23).
/// Full decision-stack coverage through <see cref="QuestBehavior.Run"/> on the
/// REAL headless actor (Observe/Target/Move/Stop/AutoAttack all execute the
/// engine verbs — no fakes): in-range + assigned fires AutoAttack once the
/// pursuit Stop leg hold-confirms; out-of-range/dead/unassigned/already-live
/// withhold with named reasons; selection is the G4 funnel's, never a second
/// competition.
/// </summary>
[NotInParallel]
public class QuestObjectiveCombatTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static uint s_extraObjId = 0x76000;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedCombatSurface();
    }

    // ------------------------------------------------------------ decisions

    [Test]
    public async Task InRangeAssigned_StopFirstThenAutoAttack()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-combat-ok");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);

        // Wake 1: range-hold (Stop, 24) wins while the hold is unconfirmed.
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-ok-1" });
        await Assert.That(first.WorkSelected).IsTrue();
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);

        // Wake 2: settled (nobody moved) — Stop hold-confirms away, combat (23) fires.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-ok-2" });
        await Assert.That(second.WorkSelected).IsTrue();
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(second.Request!.TargetId).IsEqualTo(npcObjId);
        await Assert.That(second.Request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(second.Request.Detail).Contains("auto-attack started");
        await Assert.That(actor.Character.IsAutoAttack).IsTrue();
        await Assert.That(actor.Character.CurrentTarget?.ObjId).IsEqualTo(npcObjId);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Cast)).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Loot)).IsFalse();
    }

    [Test]
    public async Task OutOfRange_WithholdsCombat_PursuitOwns()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-combat-far");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-far-1" });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();
    }

    [Test]
    public async Task DeadTarget_NoCombatProposal()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-combat-dead");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        session.World.GetNpc(npcObjId)!.Hp = 0;
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-dead-1" });

        await Assert.That(result.SelectedAction == ActorActionType.AutoAttack).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();
    }

    [Test]
    public async Task UnassignedTarget_TargetOwnsWake_NoRecompetition()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-combat-unassigned");
        SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-unassigned-1" });

        // Assignment (Target, 25) owns the wake — combat never assigns.
        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Target);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();
    }

    [Test]
    public async Task SameTarget_NoRecompetition_NearestFunnelSelection()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-combat-two");
        var nearObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var farObjId = SpawnExtraBoar(session, new Vector3(2.5f, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(nearObjId);

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-two-1" });
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-two-2" });

        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(second.Request!.TargetId).IsEqualTo(nearObjId);
        await Assert.That(second.Request!.TargetId == farObjId).IsFalse();
    }

    [Test]
    public async Task AlreadyLive_NoRedispatch()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-combat-live");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        _ = actor.AutoAttack(npcObjId);
        var issued = actor.AuditTrace.Count(r => r.Action == ActorActionType.AutoAttack);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-live-1" });

        await Assert.That(result.SelectedAction == ActorActionType.AutoAttack).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AutoAttack)).IsEqualTo(issued);
        actor.StopAutoAttack();
    }

    [Test]
    public async Task AutoAttackFlagWithoutTask_RecoversThroughOrdinaryAttack()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-missing-native-loop");
        var target = SpawnBoar(session, actor, Vector3.Zero, new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(target);
        actor.Character.IsAutoAttack = true;
        actor.Character.AutoAttackTask = null;
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-missing-loop-stop" });

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-missing-loop-attack" });

        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(actor.Character.AutoAttackTask).IsNotNull();
        actor.StopAutoAttack();
    }
    /// <summary>
    /// NO-PROGRESS STALL TRIP (kill22 char 2): a ranged actor holding inside its own
    /// band with a live-but-damageless loop must not yield forever. After
    /// <see cref="QuestBehavior.CombatStallWakes"/> consecutive hold-wakes with no HP
    /// decrease, the pursuit leg stops yielding and closes to contact (a Move wins)
    /// instead of holding at range.
    /// </summary>
    [Test]
    public async Task InBandHold_WithNoHpProgress_TripsToCloseInMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-stall-trip");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(18, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicRangedAutoAttackSkillId);
        await Assert.That(actor.AutoAttack(npcObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        QuestDecisionScenario.QuestRunResult? last = null;
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes; wake++)
        {
            last = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"g6-stall-{wake}" });
            // Damageless loop: HP pinned, flag stays live.
            npc.Hp = 100;
            actor.Character.IsAutoAttack = true;
        }

        await Assert.That(last!.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(last.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains("reason=stall-close-in");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// The trip is progress-gated: HP falling between wakes clears the streak, so a
    /// working loop never trips — the in-band hold stands for all ten wakes.
    /// </summary>
    [Test]
    public async Task InBandHold_WithHpDecreasing_NeverTrips()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-stall-progress");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(18, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        actor.Character.IsAutoAttack = true;
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        QuestDecisionScenario.QuestRunResult? last = null;
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes; wake++)
        {
            npc.Hp = Math.Max(1, 100 - wake * 5);
            last = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"g6-stall-ok-{wake}" });
            actor.Character.IsAutoAttack = true;
        }

        await Assert.That(last!.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(last.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains(":dispatch=held:reason=inside-combat-band");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// Sum (not reset) on target switch: consecutive hold-wakes carry across the
    /// switch actor-wide, so a short run (8 + 1 = 9) still holds while a full
    /// run (9 + 1 = 10) trips — rotating live targets without dealing damage
    /// still closes to contact at the same threshold.
    /// </summary>
    [Test]
    public async Task InBandHold_TargetSwitch_SumsAcrossTheSwitch()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-stall-switch");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var firstObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(18, 0, 0));
        var secondObjId = SpawnExtraBoar(session, new Vector3(18.5f, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(firstObjId);
        actor.Character.IsAutoAttack = true;
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        for (var wake = 1; wake < QuestBehavior.CombatStallWakes - 1; wake++)
        {
            _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"g6-stall-sw-{wake}" });
            actor.Character.IsAutoAttack = true;
        }

        // Short carry (8 + 1 = 9): still holds, never trips.
        var second = session.World.GetNpc(secondObjId)!;
        actor.Character.CurrentTarget = second;
        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-stall-sw-next" });

        await Assert.That(result.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(result.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains(":dispatch=held:reason=inside-combat-band");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// ROTATION TRIP: alternating two live targets with flat HP trips the contact
    /// close-in at the same threshold — the actor-wide backstop sums consecutive
    /// hold-wakes across the switch instead of restarting per target.
    /// </summary>
    [Test]
    public async Task InBandHold_RotatingTargets_WithNoHpProgress_TripsToCloseInMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-stall-rotate");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var firstObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(18, 0, 0));
        var secondObjId = SpawnExtraBoar(session, new Vector3(18.5f, 0, 0));
        Activate251(actor.Character);
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        QuestDecisionScenario.QuestRunResult? last = null;
        var first = session.World.GetNpc(firstObjId)!;
        var second = session.World.GetNpc(secondObjId)!;
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicRangedAutoAttackSkillId);
        await Assert.That(actor.AutoAttack(firstObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes; wake++)
        {
            // Alternate every wake; HP pinned on both, flag stays live.
            actor.Character.CurrentTarget = wake % 2 == 1 ? first : second;
            first.Hp = 100;
            second.Hp = 100;
            actor.Character.IsAutoAttack = true;
            last = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"g6-stall-rot-{wake}" });
        }

        await Assert.That(last!.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(last.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains("reason=stall-close-in");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// SPLIT-RUN TRIP: 8 flat hold-wakes on A then 8 on B trips (8 + 8 = 16
    /// actor-wide) — the kill28 shape that motivated the backstop.
    /// </summary>
    [Test]
    public async Task StallCounter_SplitAcrossTargets_TripsAtSameThreshold()
    {
        const uint actorId = 0x6E31u;
        const uint first = 0x6E32u;
        const uint second = 0x6E33u;
        QuestBehavior.ClearCombatStallMemory();
        for (var i = 0; i < 8; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actorId, first, 100, $"split-a-{i}", out _)).IsFalse();
        var tripped = false;
        for (var i = 0; i < 8; i++)
            tripped |= QuestBehavior.CountCombatStallWake(actorId, second, 100, $"split-b-{i}", out _);
        await Assert.That(tripped).IsTrue();
        QuestBehavior.ClearCombatStallMemory();
    }

    /// <summary>
    /// The backstop is progress-gated per target: HP falling on the current target
    /// clears the actor-wide count too, so a working loop never trips.
    /// </summary>
    [Test]
    public async Task StallCounter_HpDecreaseOnCurrentTarget_ClearsBackstop()
    {
        const uint actorId = 0x6E41u;
        const uint target = 0x6E42u;
        QuestBehavior.ClearCombatStallMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 100, $"prog-{i}", out _)).IsFalse();
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 90, "prog-dmg", out var wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 90, "prog-restart", out wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(1);
        QuestBehavior.ClearCombatStallMemory();
    }
    /// <summary>
    /// FOREIGN-ONLY PROGRESS: an HP drop with no self attribution keeps counting
    /// toward the trip — the kill29 shape (bystander damage resets nothing).
    /// </summary>
    [Test]
    public async Task StallCounter_ForeignOnlyHpDecrease_KeepsCounting()
    {
        const uint actorId = 0x6E51u;
        const uint target = 0x6E52u;
        QuestBehavior.ClearCombatStallMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 100, $"fgn-{i}", out _, selfAttributed: false)).IsFalse();
        // Foreign damage drops HP but the streak keeps counting: the 10th wake trips.
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 90, "fgn-dmg", out var wakes, selfAttributed: false)).IsTrue();
        await Assert.That(wakes).IsEqualTo(QuestBehavior.CombatStallWakes);
        QuestBehavior.ClearCombatStallMemory();
    }

    /// <summary>
    /// SELF-ATTRIBUTED PROGRESS: an HP drop carrying the actor's own damage resets
    /// the streak (and the actor-wide backstop), so a working loop never trips.
    /// </summary>
    [Test]
    public async Task StallCounter_SelfAttributedHpDecrease_ResetsStreak()
    {
        const uint actorId = 0x6E61u;
        const uint target = 0x6E62u;
        QuestBehavior.ClearCombatStallMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 100, $"self-{i}", out _, selfAttributed: true)).IsFalse();
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 90, "self-dmg", out var wakes, selfAttributed: true)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 90, "self-restart", out wakes, selfAttributed: true)).IsFalse();
        await Assert.That(wakes).IsEqualTo(1);
        QuestBehavior.ClearCombatStallMemory();
    }

    /// <summary>
    /// FAIL-CLOSED: unreadable attribution (null) keeps the historical behavior and
    /// resets — unknown state never forces motion.
    /// </summary>
    [Test]
    public async Task StallCounter_UnreadableAttribution_ResetsLikeBefore()
    {
        const uint actorId = 0x6E71u;
        const uint target = 0x6E72u;
        QuestBehavior.ClearCombatStallMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 100, $"unk-{i}", out _, selfAttributed: null)).IsFalse();
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 90, "unk-dmg", out var wakes, selfAttributed: null)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        QuestBehavior.ClearCombatStallMemory();
    }

    /// <summary>
    /// Melee contact is untouched: inside the 3 m stop radius the Stop/hold-confirm
    /// path still owns the wake — the stall trip only fires from the in-band yield
    /// OUTSIDE the stop radius, so this wake never counts toward it.
    /// </summary>
    [Test]
    public async Task MeleeInContact_HoldConfirmPath_UnaffectedByStallTrip()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g6-stall-melee");
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g6-stall-melee-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
    }
    /// <summary>
    /// Fail-closed at the counter: unreadable HP (negative) clears the actor's streak
    /// and never trips — unknown state never forces motion.
    /// </summary>
    [Test]
    public async Task StallCounter_UnreadableHp_ResetsAndNeverTrips()
    {
        var actorId = 0x6E11u;
        var target = 0x6E12u;
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 100, $"u-{i}", out _)).IsFalse();
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, -1, "u-bad", out var wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        // Streak was dropped: counting restarts at one, not at the threshold.
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, target, 100, "u-next", out wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(1);
        QuestBehavior.ClearCombatStallMemory();
    }

    /// <summary>
    /// Fail-closed at the counter: target 0 (identity gone) clears and never trips.
    /// </summary>
    [Test]
    public async Task StallCounter_ZeroTarget_ClearsAndNeverTrips()
    {
        var actorId = 0x6E21u;
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actorId, 0x6E22u, 100, $"z-{i}", out _)).IsFalse();
        await Assert.That(QuestBehavior.CountCombatStallWake(actorId, 0, 100, "z-gone", out var wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        QuestBehavior.ClearCombatStallMemory();
    }
    /// <summary>
    /// STALE-PIN WATCHDOG TRIP (kill27 char 1): LeashEdge + static HP for N
    /// wakes trips the demotion so the next wake re-selects a killable target
    /// instead of holding the leash-dead pin.
    /// </summary>
    [Test]
    public async Task LeashStalePin_TripsAfterNStaticWakes()
    {
        var actorId = 0x6F11u;
        var staleTarget = 0x6F12u;
        var liveTarget = 0x6F13u;
        QuestBehavior.ClearLeashStaleMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, staleTarget, 100, $"leash-{i}", out _)).IsFalse();
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, staleTarget)).IsFalse();
        await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, staleTarget, 100, "leash-trip", out var wakes)).IsTrue();
        await Assert.That(wakes).IsEqualTo(QuestBehavior.CombatStallWakes);
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, staleTarget)).IsTrue();
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, liveTarget)).IsFalse();
        QuestBehavior.ClearLeashStaleMemory();
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, staleTarget)).IsFalse();
    }

    /// <summary>
    /// The watchdog is progress-gated: any HP decrease clears the streak, so a
    /// fight dealing damage never demotes — the char-2-style healthy take.
    /// </summary>
    [Test]
    public async Task LeashStalePin_HpDecrease_ClearsAndNeverTrips()
    {
        var actorId = 0x6F21u;
        var target = 0x6F22u;
        QuestBehavior.ClearLeashStaleMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, target, 100, $"leash-ok-{i}", out _)).IsFalse();
        await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, target, 90, "leash-dmg", out var wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, target)).IsFalse();
        await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, target, 90, "leash-restart", out wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(1);
        QuestBehavior.ClearLeashStaleMemory();
    }

    /// <summary>
    /// Fail-closed: a short LeashEdge run (below N wakes) never trips, and
    /// unreadable HP or a gone identity clears instead of tripping.
    /// </summary>
    [Test]
    public async Task LeashStalePin_ShortRunOrUnknownState_NeverTrips()
    {
        var actorId = 0x6F31u;
        var target = 0x6F32u;
        QuestBehavior.ClearLeashStaleMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 2; i++)
            await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, target, 100, $"leash-short-{i}", out _)).IsFalse();
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, target)).IsFalse();
        await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, target, -1, "leash-bad", out var wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, target)).IsFalse();
        await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, 0, 100, "leash-gone", out wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(0);
        QuestBehavior.ClearLeashStaleMemory();
    }

    /// <summary>
    /// Fail-closed on target switch: a streak accrued on one target never trips
    /// a different target — the new fight counts from one.
    /// </summary>
    [Test]
    public async Task LeashStalePin_TargetSwitch_ResetsTheStreak()
    {
        var actorId = 0x6F41u;
        QuestBehavior.ClearLeashStaleMemory();
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, 0x6F42u, 100, $"leash-sw-{i}", out _)).IsFalse();
        await Assert.That(QuestBehavior.CountLeashStaleWake(actorId, 0x6F43u, 100, "leash-sw-next", out var wakes)).IsFalse();
        await Assert.That(wakes).IsEqualTo(1);
        await Assert.That(QuestBehavior.IsLeashStaleTarget(actorId, 0x6F43u)).IsFalse();
        QuestBehavior.ClearLeashStaleMemory();
    }


    /// <summary>
    /// The CombatBrain increment's LIVE wiring, end to end on the real headless
    /// actor: with the rotation gate ON, the combat leg asks the brain for its
    /// verb and the brain's decision reaches the engine AND the published
    /// engagement facts. Without this the whole brain would be a pure unit surface
    /// with no production caller.
    /// </summary>
    [Test]
    public async Task CombatBrainRotation_OnDispatchesTheBrainsVerbAndPublishesTheEngagement()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-live");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        // Wake 1: range-hold (Stop, 24) still wins — no engagement published yet,
        // because the combat proposal never dispatched.
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-live-1", EnableCombatRotation = true });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(actor.ActorId)).IsFalse();

        // Wake 2: settled — the brain's Opener verb (a Cast on the committed objId)
        // lands through the engine, and the engagement is published.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-live-2", EnableCombatRotation = true });

        await Assert.That(second.WorkSelected).IsTrue();
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Cast);
        await Assert.That(second.Request!.TargetId).IsEqualTo(npcObjId);
        await Assert.That(second.Request!.SkillId == 0u).IsFalse();
        await Assert.That(second.Request!.State).IsEqualTo(ActorLifecycleState.Completed);

        // The combat leg's own diag carries the brain's decision (the lane joins on it).
        var combatDetail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail;
        await Assert.That(combatDetail.Contains("brain=arm=Opener")).IsTrue();
        await Assert.That(combatDetail.Contains("verb=Cast")).IsTrue();
        await Assert.That(combatDetail.Contains("band=1.00-3.50")).IsTrue();

        // The published engagement: the yield fact the pursuit Stop leg reads.
        await Assert.That(CombatBrainEngagement.TryGet(actor.ActorId, out var engagement)).IsTrue();
        await Assert.That(engagement.IncumbentObjId).IsEqualTo(npcObjId);
        await Assert.That(CombatBrainEngagement.ShouldYieldPursuit(actor.ActorId)).IsTrue();

        // And the landed cast seeded the rotation chain for the next wake.
        await Assert.That(engagement.LastSkillUsed).IsEqualTo(second.Request!.SkillId);

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// The frozen G6 lane is unchanged by the increment: with the rotation gate
    /// OFF (the default) the leg still dispatches the engine's auto-attack loop,
    /// so no lane that predates the brain observes a different verb.
    /// </summary>
    [Test]
    public async Task DefaultPolicy_KeepsTheFrozenAutoAttackOnlyVerb()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-frozen");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-frozen-1" });
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-frozen-2" });

        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(second.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Cast)).IsFalse();
        await Assert.That(CombatBrainEngagement.TryGet(actor.ActorId, out _)).IsTrue();

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// THE ADMISSION RADIUS IS ROLE-DERIVED, so a ranged actor is consulted from
    /// INSIDE its own band rather than only inside the pursuit stop radius. At 12 m
    /// the archer is inside its 12-22 m band: the brain's BAND arm does not apply, so
    /// the verdict is SUSTAIN — a verb the flat 3.0 m gate made unreachable, because
    /// there the brain was only ever shown distances BELOW the 4 m floor of its own
    /// weapon and so could never leave BackOff.
    /// </summary>
    [Test]
    public async Task RangedRole_InsideItsBand_ConsultsTheBrainAtRange()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-admit-ranged");
        // A RANGED role: its weapon floor is 4 m, so inside the old flat 3.0 m
        // gate it could never reach its own band.
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(12, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-admit-ranged-1" });

        // The brain really ran at range — its verdict is in the leg's own diag, at a
        // distance the flat 3.0 m gate could not reach.
        var detail = wake.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail;
        await Assert.That(detail).Contains("brain=arm=Sustain");
        await Assert.That(detail).Contains("band=12.00-22.00");
        await Assert.That(detail).Contains("distM=12.0");

        // AND THE ATTACK NOW WINS IT. Inside its band the closing leg has no work
        // left — the brain's BAND arm cannot ask for it here — so pursuit stands
        // down and combat (23) dispatches the verb the brain just chose. This is
        // the wake that used to end in a pursuit Move while the archer stood at a
        // distance its own weapon reaches.
        await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains(":dispatch=held:reason=inside-combat-band");
        await AssertReportsEngineVerdict(wake.Request, CombatDecisionTree.BasicRangedAutoAttackSkillId);
        await Assert.That(actor.Character.IsAutoAttack).IsTrue();

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// THE REACH THAT MATTERS: at 12 m a ranged role is INSIDE its band (sustain),
    /// while the same actor at 3 m is BELOW its floor (back off). The admission
    /// radius must therefore span the band, not stop at the pursuit radius — a bot
    /// that is only ever consulted below its floor can never fire.
    /// </summary>
    [Test]
    public async Task RangedRole_AdmissionRadiusCoversTheBandNotJustThePursuitRadius()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-admit-span");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(12, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        var inBand = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-admit-span-in" });
        await Assert.That(inBand.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
            .Contains("brain=arm=Sustain");

        // Crowded inside the floor: the SAME brain now asks for spacing. Both
        // Crowded inside the floor: the SAME brain now asks for spacing. Both
        // verdicts are reachable only because admission spans the band.
        CombatBrainEngagement.ClearAll();
        // The in-band attack landed on the wake above, so the auto-attack loop is
        // live and the leg's own already-live guard would answer first. Ending it
        // through the REAL actor seam is what the engine does between fights; it
        // is not a way of reaching a verdict the loop hides.
        actor.StopAutoAttack();
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new Vector3(1.5f, 0, 0));
        var crowded = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-admit-span-crowded" });
        await Assert.That(crowded.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
            .Contains("brain=arm=BackOff");

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// THE RANGE-HOLD MUST SURVIVE THE FIX. The in-band yield is scoped to the
    /// CLOSING arm; an actor still outside its band has real closing work, so
    /// pursuit keeps the wake and the brain is never even asked. This is the
    /// behaviour the yield must not eat.
    /// </summary>
    [Test]
    public async Task RangedRole_OutsideItsBand_PursuitStillCloses()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-band-outside");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        // 22 m is the archer's band CEILING: outside it by a hair, so the
        // closing leg is still the work and the brain's CloseRange arm agrees.
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(22.5f, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-band-outside-1" });

        await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.Move);
        var pursuit = wake.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail;
        await Assert.That(pursuit).Contains(":dispatch=move:reason=fresh");
        await Assert.That(wake.Request!.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
        // Out of the admission radius the combat leg is never consulted at all.
        await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
            .Contains("validate=out-of-range");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// A MELEE role is unaffected: its band ceiling is its own 3.5 m reach, so
    /// above it the closing leg still walks in (exactly as before), and at
    /// contact the Stop arm owns the first wake and combat the settled one. The
    /// yield must not turn every bot into a ranged role that shoots from
    /// whatever distance it happens to be standing at.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MeleeRole_EquippedBowUsesOrdinaryRange_IncludingHeight(bool elevated)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-melee-bow-" + elevated);
        actor.Character.Ability1 = AbilityType.Fight;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        const uint bowTemplateId = 90771;
        GameplayActorTestRig.SeedEquipItemTemplate(bowTemplateId,
            AAEmu.Game.Models.Game.Items.EquipmentItemSlotType.Ranged);
        var bow = (AAEmu.Game.Models.Game.Items.Templates.WeaponTemplate)ItemManager.Instance.GetTemplate(bowTemplateId);
        bow.HoldableTemplate.KindId = 29;
        bow.HoldableTemplate.Speed = 2000;
        bow.HoldableTemplate.MinRange = 0;
        bow.HoldableTemplate.MaxRange = 20;
        GameplayActorTestRig.StockItem(session, bowTemplateId, 1);
        await Assert.That(actor.Equip(bowTemplateId).State).IsEqualTo(ActorLifecycleState.Completed);

        // Canonical basic4 reach fields, no effects: this verifies choice and
        // normal skill submission, not damage or a live combat outcome.
        var templates = (Dictionary<uint, AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(SkillManager.Instance)!;
        templates.TryGetValue(4, out var previous);
        templates[4] = new AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate
        {
            Id = 4, MinRange = 4, MaxRange = 25, WeaponSlotForRangeId = 17,
            WeaponSlotForAutoAttackId = 17, TargetType = SkillTargetType.Hostile,
            TargetSelection = SkillTargetSelection.Target
        };
        try
        {
            var position = elevated ? new Vector3(2, 0, 14) : new Vector3(14, 0, 0);
            var id = SpawnBoar(session, actor, Vector3.Zero, position);
            Activate251(actor.Character);
            actor.Character.CurrentTarget = session.World.GetNpc(id);
            CombatBrainEngagement.ClearAll();
            var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "cb-melee-bow-range" });
            await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
            await Assert.That(wake.Request!.TargetId).IsEqualTo(id);
            await Assert.That(wake.Request.Detail).Contains("auto-attack started (4)");
            await Assert.That(actor.Character.AutoAttackTask!.Skill.Template.Id).IsEqualTo(4u);
            var task = actor.Character.AutoAttackTask;
            actor.AutoAttack(id);
            await Assert.That(ReferenceEquals(task, actor.Character.AutoAttackTask)).IsTrue();
            await Assert.That(actor.Character.Quests.HasQuestCompleted(251)).IsFalse();
        }
        finally
        {
            actor.StopAutoAttack();
            actor.PreemptCurrent("bow range fixture cleanup");
            CombatBrainEngagement.ClearAll();
            if (previous == null) templates.Remove(4); else templates[4] = previous;
        }
    }

    [Test]
    public async Task MeleeRole_ClosesThenAttacks_Unchanged()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-band-melee");
        actor.Character.Ability1 = AbilityType.Fight;
        actor.Character.Hp = actor.Character.MaxHp;
        // 10 m: far outside a 1.0-3.5 m melee band, so the closing leg works.
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        var closing = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-band-melee-far" });
        await Assert.That(closing.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(closing.Request!.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);

        // It arrived: inside the band now, so the attack is the wake's work.
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new Vector3(2, 0, 0));
        var inBand = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-band-melee-near" });
        await Assert.That(inBand.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
            .Contains("brain=arm=Sustain");

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// THE BOUNDARY IS THE ROLE'S OWN BAND, not a number fitted to one run: an
    /// archer standing 30 m out is still closing, and the same archer standing
    /// 12 m in is attacking. Both are pinned by the SAME derived rule the
    /// admission radius already reads.
    /// </summary>
    [Test]
    public async Task InBandYield_TracksTheRoleBandCeiling_NotAFittedDistance()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-band-boundary");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(20, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        // Inside its own 12-22 m band → the attack wins.
        var inBand = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-band-boundary-in" });
        await Assert.That(inBand.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);

        // The same actor, the same rules, 24 m out (beyond the 22 m ceiling):
        // the closing leg owns the wake again. The band, not a magic constant,
        // is what moved.
        CombatBrainEngagement.ClearAll();
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new Vector3(24, 0, 0));
        var outOfBand = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-band-boundary-out" });
        await Assert.That(outOfBand.SelectedAction).IsEqualTo(ActorActionType.Move);

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// THE RECORDED FREEZE, as a contract. The run log shows the archer frozen at
    /// 21.8 m (2-D) for sixteen consecutive cycles with zero damage while the
    /// target decayed 263→2 untouched: the band ceiling (22 m) admitted it, the
    /// closing leg stood down inside its own band, and the engine refused every
    /// cast at 20.195082 against a 20 m bow.
    ///
    /// Two causes had to be removed together. The ceiling now tracks the reach,
    /// and the gate reads the engine's own metric — so at that pose the archer is
    /// NO LONGER admitted (the engine cannot reach, so it must not think it can)
    /// and the closing leg is dispatched instead. This is the defect's teeth: an
    /// archer admitted at a distance its weapon cannot reach must never stand
    /// down the closing leg.
    /// </summary>
    [Test]
    public async Task ArcherBeyondItsOwnReach_GetsAClosingLeg_AndIsNotAdmittedToRefusedCasts()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-reach-teeth");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;

        // The frozen pose, reproduced on the metric the engine judges on: the archer
        // is out of reach, so it must be closing, not attacking.
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(22.5f, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        CombatBrainEngagement.ClearAll();

        // The engine's own reading of that pose — the number the cast is judged by.
        var engineDistance = actor.Character.GetDistanceTo(npc, true);
        var reach = CombatDecisionTree.GetSkillEnforcedRange(
            AAEmu.Game.Core.Managers.SkillManager.Instance.GetSkillTemplate(
                CombatDecisionTree.BasicRangedAutoAttackSkillId), actor.Character).MaxRange;
        await Assert.That(engineDistance).IsGreaterThan(reach)
            .Because("the fixture must reproduce an out-of-reach pose, or it proves nothing");

        var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-reach-teeth-1" });

        // (i) NOT admitted: the combat leg must decline with a named reason rather
        // than dispatch a cast the engine would refuse.
        await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
            .Contains("validate=out-of-range");
        // (ii) THE CLOSING LEG DISPATCHES — this is the arm that used to stand
        // down at 21.8 m and leave the archer frozen for sixteen cycles.
        await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(wake.Request!.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
        // (iii) and nothing fired: no refused cast, no idle Sustain.
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// DEFECT B's teeth, where the two authorities actually DISAGREE: a pose
    /// whose 2-D reading is inside the band but whose ENGINE reading — 3-D, less
    /// both model radii — is past the weapon's reach.
    ///
    /// The rig's world has zero model radii and zero elevation, where 2-D and 3-D
    /// coincide, so a radiusless fixture cannot tell the two metrics apart at all
    /// (verified: reverting the metric changed nothing). This seeds the real
    /// radius subtraction the engine performs, reproducing the ~1.6 m gap the run
    /// log recorded between `rangeM=21.8` (2-D) and `targetDist=20.195082`
    /// (engine) — and pins that the gate follows the ENGINE's number.
    /// </summary>
    [Test]
    public async Task AdmissionGate_FollowsTheEngineMetric_NotTheOptimistic2DReading()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-metric-teeth");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;

        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(0, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        CombatBrainEngagement.ClearAll();

        // A ZERO radius, and the separation carried entirely by ELEVATION. This is
        // the only divergence A cannot mask: A lowered the horizontal ceiling to
        // the reach, but the Z term exists in the engine's metric alone, so a
        // pose the flat reading calls inside the band is still out of reach on
        // the real one. (Verified: with radii in play, reverting the metric
        // changes nothing — A alone already refuses those poses.)
        const uint radiusModelId = 902_501;
        SeedActorModelRadius(radiusModelId, 0f);
        npc.ModelId = radiusModelId;
        actor.Character.ModelId = radiusModelId;

        var reach = CombatDecisionTree.GetSkillEnforcedRange(
            AAEmu.Game.Core.Managers.SkillManager.Instance.GetSkillTemplate(
                CombatDecisionTree.BasicRangedAutoAttackSkillId), actor.Character).MaxRange;
        var ceiling = QuestBehavior.CombatAdmissionRadiusM(actor.Character);

        // A pose INSIDE the admission ceiling on the flat reading: the archer
        // stands a little under its reach horizontally, with the prey on higher
        // ground — the terrain shape the recorded run kept hitting.
        // 19.9 m flat is inside the 20 m ceiling, but with the prey 4 m above the
        // engine's own reading is sqrt(19.9² + 4²) = 20.30 m — past the reach.
        const float flatSeparationM = 19.9f;
        const float elevationM = 4.0f;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new Vector3(flatSeparationM, 0, elevationM));

        var twoD = MathUtil.CalculateDistance(
            actor.Character.Transform.World.Position, npc.Transform.World.Position, false);
        var engineDistance = actor.Character.GetDistanceTo(npc, true);

        // Precondition 1: the flat reading calls this pose IN BAND...
        await Assert.That(twoD).IsLessThanOrEqualTo(ceiling)
            .Because($"the pose must read as inside the {ceiling:F0} m ceiling on the flat metric, " +
                      $"or the old gate would never have admitted it either");

        // ...while the engine's own metric puts it past the weapon's reach. This
        // is the divergence Defect B is about: same pose, two authorities, and
        // the 2-D one is the optimistic one.
        await Assert.That(engineDistance).IsGreaterThan(reach)
            .Because($"the engine must refuse here (reach {reach:F0} m, engine read {engineDistance:F3} m)");
        await Assert.That(engineDistance).IsGreaterThan(twoD)
            .Because("the Z term exists only in the engine's metric, so it must read longer here");

        // THE CONTRACT: the gate is judged on the ENGINE's number, so this pose is
        // NOT admitted and the closing leg owns the wake. Pre-fix the gate read
        // the flat distance, admitted the archer, stood the closing leg down, and
        // the engine refused every cast — the freeze.
        var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-metric-teeth-1" });

        await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
            .Contains("validate=out-of-range");
        await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(wake.Request!.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// Seeds one real <see cref="ActorModel"/> radius into the shared
    /// <c>ModelManager</c> so <c>BaseUnit.GetDistanceTo</c> has something to
    /// subtract — the arithmetic the engine's own range gate performs, and the
    /// reason the bot's 2-D reading is systematically optimistic. Additive and
    /// idempotent; the manager instance is restored on teardown so no sibling
    /// suite inherits the seeded radius.
    /// </summary>
    private static void SeedActorModelRadius(uint modelId, float radiusM)
    {
        var modelManager = Singleton<ModelManager>.PeekInstance ?? throw new InvalidOperationException("rig ModelManager missing");

        var modelTypes = (Dictionary<uint, ModelType>)typeof(ModelManager)
            .GetField("_modelTypes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(modelManager)!;
        var models = (Dictionary<string, Dictionary<uint, Model>>)typeof(ModelManager)
            .GetField("_models", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(modelManager)!;

        if (!models.TryGetValue("ActorModel", out var actorModels))
        {
            actorModels = [];
            models["ActorModel"] = actorModels;
        }

        modelTypes[modelId] = new ModelType { Id = modelId, SubType = "ActorModel", SubId = modelId };
        actorModels[modelId] = new ActorModel { Id = modelId, Radius = radiusM };
    }

    /// <summary>
    /// THE CLOSING BOUNDARY IS THE REACH: the same archer, same rules. Inside its
    /// own reach the attack wins; one step beyond, the closing leg owns the wake.
    /// The 2-D reading called the frozen 21.8 m pose "inside the 22 m band" —
    /// this pins that the boundary now follows the reach the engine enforces, so
    /// the two authorities can never again disagree about where the band ends.
    /// </summary>
    [Test]
    public async Task InBandYield_TracksTheEnforcedReach_SoNoAdmittedPoseIsUnreachable()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-reach-edge");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(12, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        CombatBrainEngagement.ClearAll();

        // The contract, stated as an invariant over the gate itself rather than a
        // distance: NO pose the gate admits is one the engine would refuse.
        var ceiling = QuestBehavior.CombatAdmissionRadiusM(actor.Character);
        var reach = CombatDecisionTree.GetSkillEnforcedRange(
            AAEmu.Game.Core.Managers.SkillManager.Instance.GetSkillTemplate(
                CombatDecisionTree.BasicRangedAutoAttackSkillId), actor.Character).MaxRange;
        await Assert.That(ceiling).IsLessThanOrEqualTo(reach)
            .Because("admitting past the enforced reach is what stranded the archer");

        // Inside the reach, the attack still wins and the closing leg stands down.
        var inReach = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-reach-edge-in" });
        await Assert.That(inReach.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(inReach.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains(":dispatch=held:reason=inside-combat-band");

        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();

        // Out of the reach: the closing leg must own the wake again. Pre-fix this
        // pose read as "inside the 22 m band" and the archer stood still here.
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, new Vector3(22.5f, 0, 0));
        var outOfReach = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-reach-edge-out" });
        await Assert.That(outOfReach.SelectedAction).IsEqualTo(ActorActionType.Move);

        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// THE FIX ITSELF. An archer that stands INSIDE its own combat band and
    /// elects to attack is no longer overridden by a pursuit Move on the same
    /// wake: the closing leg stands down and the AutoAttack dispatches. Before
    /// this, priority 24 outranked 23 unconditionally, so the brain's Sustain
    /// verdict was recorded at 12 m and then discarded every single wake, and
    /// the archer closed forever instead of shooting.
    /// </summary>
    [Test]
    public async Task InBandAttack_BeatsThePursuitMoveOnTheSameWake()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-inband-wins");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(13, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-inband-wins-1" });

        // The brain chose an attack at a distance its own weapon reaches…
        var combat = wake.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail;
        await Assert.That(combat).Contains("brain=arm=Sustain");
        await Assert.That(combat).Contains("verb=AutoAttack");
        // …and that verdict, not a closing Move, is what the wake dispatched.
        await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await AssertReportsEngineVerdict(wake.Request, CombatDecisionTree.BasicRangedAutoAttackSkillId);
        await Assert.That(actor.Character.IsAutoAttack).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Move)).IsFalse();

        CombatBrainEngagement.ClearAll();
    }
    /// <summary>
    /// THE PURSUIT STOP RADIUS KEEPS ITS JOB: inside 3.0 m the pursuit leg still
    /// owns the wake with the audited Stop and still yields to the combat brain
    /// only once the hold confirms. Neither the admission radius nor the in-band
    /// closing yield took the stop radius's work away from it — the Stop arm runs
    /// on its own branch, ahead of both.
    /// </summary>
    [Test]
    public async Task InsideThePursuitStopRadius_TheStopHoldStillOwnsTheFirstWake()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("cb-admit-stop");
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
        CombatBrainEngagement.ClearAll();

        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-admit-stop-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(first.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains(":dispatch=stop:reason=in-range");

        // And once it settles, combat still wins the wake (the stop's own yield).
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "cb-admit-stop-2" });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);

        CombatBrainEngagement.ClearAll();
    }

    [Test]
    public async Task PriorityLayout_CombatBelowPursuitAboveAdvance()
    {
        var opts = new QuestDecisionScenario.QuestOptions();
        await Assert.That(opts.ObjectiveCombatPriority).IsEqualTo(23);
        await Assert.That(opts.ObjectiveCombatPriority < opts.ObjectivePursuitPriority).IsTrue();
        await Assert.That(opts.ObjectiveCombatPriority > opts.AdvancePriority).IsTrue();
    }

    /// <summary>
    /// The observable contract of an AutoAttack wake: the completion detail
    /// reports the ENGINE'S VERDICT for this rig, not a fixed literal.
    ///
    /// <c>AutoAttack</c> forwards <c>skill.Use</c>'s <see cref="SkillResult"/>
    /// both into the detail text and into <c>Request.Result</c>, so the token
    /// a consumer reads can be DERIVED from the same production value instead
    /// of re-pinned as a string. That is the whole point: this rig's archer
    /// has no ranged weapon, and canonical <c>unit_reqs</c> row 26110 gates
    /// skill 4 on <c>kind_id=29 (EquipRanged)</c>, so the engine refuses here.
    /// A literal "auto-attack started" would pin the rig's accident (the
    /// refused-verdict wording) rather than the contract, and the enum NAME
    /// printed for numeric 88 is <c>UrkNobuff</c> — an id-space aliasing
    /// collision, not the semantic meaning.
    /// </summary>
    private static async Task AssertReportsEngineVerdict(ActorRequest request, uint expectedSkillId)
    {
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(request.Result).IsAssignableTo<SkillResult>();
        var skillResult = (SkillResult)request.Result!;

        // Whichever way the engine rules this rig, the detail must carry that
        // exact verdict — the refused branch names it, the accepted branch does
        // not claim one. A regression that dropped the engine's verdict (the
        // original bug: an unconditional "started") or invented a third
        // wording fails here.
        if (skillResult == SkillResult.Success)
            await Assert.That(request.Detail).DoesNotContain("refused");
        else
            await Assert.That(request.Detail).Contains($"auto-attack refused: {skillResult}");

        // Both branches must still name the skill and target they dispatched,
        // so a wording change can never quietly drop the correlation fields.
        // Checked as bare tokens, not a parenthesised shape: the accepted
        // branch renders "(4) on 1000", the refused one "(skill 4 on 1000)",
        // and only the identity of the ids is part of the contract.
        await Assert.That(request.Detail).Contains(expectedSkillId.ToString());
        await Assert.That(request.Detail).Contains($"on {request.TargetId}");
    }

    // ------------------------------------------------------------ fixture

    [Test]
    [Arguments(7f, false)]
    [Arguments(9f, false)]
    [Arguments(7f, true)]
    [Arguments(9f, true)]
    public async Task MeleeBow_InsideRangedFloor_UsesCombatSpacing(float distance, bool elevated)
        => await WithMeleeBowControl("bow-spacing", elevated
                ? new Vector3(2, 0, distance) : new Vector3(distance, 0, 0), 20,
            async (actor, session, id) =>
            {
                var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "bow-spacing" });
                await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.Move);
                await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
                    .Contains("brain=arm=BackOff");
                await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
                    .Contains("reason=inside-combat-band");
                await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();
                await Assert.That(actor.Character.Ability1).IsEqualTo(AbilityType.Fight);
            });

    [Test]
    [Arguments(21f, 20)]
    [Arguments(14f, -1)]
    [Arguments(14f, 0)]
    public async Task MeleeBow_UnreachableOrUnreadableBow_KeepsPursuit(float distance, int bowRange)
        => await WithMeleeBowControl("bow-no-reach", new Vector3(distance, 0, 0), bowRange,
            async (actor, session, id) =>
            {
                var nativeDistance = actor.Character.GetDistanceTo(session.World.GetNpc(id)!, true);
                await Assert.That(CombatDecisionTree.TryGetRangedAutoAttackRange(
                    actor.Character, nativeDistance, out _)).IsFalse();
                var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "bow-no-reach" });
                await Assert.That(wake.SelectedAction).IsEqualTo(ActorActionType.Move);
                await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail)
                    .Contains("validate=out-of-range");
                await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();
            });

    [Test]
    public async Task MeleeBow_ValidPendingPursuitMove_KeepsItsExactTrace()
        => await WithMeleeBowControl("bow-pending", new Vector3(14, 0, 0), 20,
            async (actor, session, id) =>
            {
                actor.SetPendingMoveOwner(TravelLegDispatch.PursuitMoveOwner);
                actor.SetPendingDecision("QUEST_PURSUIT", "bow-control", 1, 0, null, Quest251);
                var pending = actor.MoveToUnit(id, timeout: TimeSpan.FromSeconds(30));
                await Assert.That(pending.IsTerminal).IsFalse();
                await Assert.That(pending.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
                _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "bow-pending" });
                await Assert.That(actor.ActiveRequest!.TraceId).IsEqualTo(pending.TraceId);
                await Assert.That(actor.ActiveRequest.IsTerminal).IsFalse();
                await Assert.That(actor.ActiveRequest.TargetId).IsEqualTo(id);
                await Assert.That(actor.ActiveRequest.DecisionQuestId).IsEqualTo(Quest251);
                await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack)).IsFalse();
            });

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MeleeBow_LearnedSkillRejectsFalseFlatOrUnionReach(bool flatOnly)
        => await WithMeleeBowControl("bow-learned-reach", flatOnly
                ? new Vector3(2, 0, 14) : new Vector3(21, 0, 0), 20,
            async (actor, session, id) =>
            {
                var templates = CombatControlSkillTemplates();
                const uint learned = CombatDecisionTree.BattlerageChargeSkillId;
                templates.TryGetValue(learned, out var prior);
                templates[learned] = new AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate
                {
                    Id = learned, MinRange = 0, MaxRange = flatOnly ? 4 : 25,
                    WeaponSlotForRangeId = flatOnly ? 0 : 17,
                    TargetType = SkillTargetType.Hostile, TargetSelection = SkillTargetSelection.Target
                };
                try
                {
                    var npc = session.World.GetNpc(id)!;
                    var nativeDistance = actor.Character.GetDistanceTo(npc, true);
                    await Assert.That(nativeDistance).IsGreaterThan(flatOnly ? 4f : 20f);
                    await Assert.That(CombatDecisionTree.IsSkillInRangeAndReady(
                        actor.Character, npc, learned, nativeDistance)).IsFalse();
                    await Assert.That(CombatDecisionTree.SelectPrioritizedSkill(
                        actor.Character, npc, CombatRole.Melee, new uint[] { learned })).IsEqualTo(0u);
                    // Positive boundary: the same candidate remains preferred when reachable.
                    GameplayActorTestRig.SetNpcPosition(session, id, new Vector3(flatOnly ? 3 : 14, 0, 0));
                    await Assert.That(CombatDecisionTree.SelectPrioritizedSkill(
                        actor.Character, npc, CombatRole.Melee, new uint[] { learned })).IsEqualTo(learned);
                }
                finally
                {
                    if (prior == null) templates.Remove(learned); else templates[learned] = prior;
                }
            });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MeleeBow_ExhaustedApproachAndDamageStall_RetiresUntilOwnProgressOrNewTarget(bool newTarget)
        => await WithMeleeBowControl("bow-exhausted-stall", new Vector3(14, 0, 0), 20,
            async (actor, session, id) =>
            {
                var npc = session.World.GetNpc(id)!;
                var nativeDistance = actor.Character.GetDistanceTo(npc, true);
                TravelLegDispatch.EnsureJourney(actor, id, npc.Transform.World.Position, 3, 2,
                    TravelLegDispatch.PursuitMoveOwner);
                TravelIntentStore.SetPursuitPurpose(actor.ActorId, Quest251);
                TravelIntentStore.BankTerminal(actor.ActorId, TravelTerminal.Unreachable,
                    TravelReason.RepathExhausted, TravelLegDispatch.PursuitMoveOwner,
                    actor.Character.Transform.World.Position, npc.Transform.World.Position);
                await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, id, Quest251)).IsFalse();
                // Try the alternative through the whole quest proposal path.
                // Its availability must not erase the real failed approach.
                var attempt = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "bow-after-exhausted-approach" });
                await Assert.That(attempt.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
                await Assert.That(attempt.Request!.Detail).Contains("auto-attack started (4)");
                await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var retained,
                    TravelLegDispatch.PursuitMoveOwner)).IsTrue();
                await Assert.That(retained.FailedApproaches.Any(f => f.TargetObjId == id && f.QuestId == Quest251)).IsTrue();
                for (var wake = 1; wake <= QuestBehavior.CombatStallWakes; wake++)
                {
                    var tripped = QuestBehavior.CountCombatStallWake(actor.ActorId, id, npc.Hp,
                        $"bow-stall-{wake}", out var count, selfAttributed: false);
                    await Assert.That(count).IsEqualTo(wake);
                    await Assert.That(tripped).IsEqualTo(wake == QuestBehavior.CombatStallWakes);
                }
                await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character, nativeDistance, id)).IsFalse();
                await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, id, Quest251)).IsTrue();
                var giveUps = new List<QuestGiveUp>();
                for (var wake = 0; wake < 12; wake++)
                    giveUps.AddRange(QuestDecisionScenario.Run(actor, (_, _) => [],
                        new QuestDecisionScenario.QuestOptions { CycleId = $"bow-no-damage-retirement-{wake}" }).GiveUps);
                await Assert.That(giveUps.Any(g => g.QuestId == Quest251)).IsTrue();
                if (newTarget)
                {
                    var other = SpawnExtraBoar(session, new Vector3(14.5f, 0, 0));
                    var otherDistance = actor.Character.GetDistanceTo(session.World.GetNpc(other)!, true);
                    await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character, otherDistance, other)).IsTrue();
                    await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, other, Quest251)).IsFalse();
                }
                else
                {
                    // Synthetic attributed HP input proves reset semantics, not earned damage.
                    _ = QuestBehavior.CountCombatStallWake(actor.ActorId, id, npc.Hp - 1,
                        "bow-own-progress", out _, selfAttributed: true);
                    await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character, nativeDistance, id)).IsTrue();
                    await Assert.That(QuestBehavior.HasExhaustedPursuit(actor.Character, id, Quest251)).IsFalse();
                }
            });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MeleeBow_DeadActorOrInvisiblePrey_DoesNotAttack(bool invisiblePrey)
        => await WithMeleeBowControl("bow-illegal", new Vector3(14, 0, 0), 20,
            async (actor, session, id) =>
            {
                if (invisiblePrey) session.World.GetNpc(id)!.IsVisible = false;
                else actor.Character.Hp = 0;
                _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "bow-illegal" });
                await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack
                    || r.Action == ActorActionType.Cast)).IsFalse();
            });

    [Test]
    [Arguments(2f, 20)]
    [Arguments(14f, 20)]
    [Arguments(21f, 20)]
    [Arguments(2f, 15)]
    [Arguments(14f, 15)]
    [Arguments(21f, 15)]
    public async Task ArcherFallbackBow_UsesNativeReachForSpacing(float distance, int bowRange)
        => await WithMeleeBowControl("archer-fallback-reach", new Vector3(distance, 0, 0), bowRange,
            async (actor, session, id) =>
            {
                // Supplied native equipment and target; no learned attack is
                // available. This checks the real adapter, not pure inputs.
                actor.Character.Skills.Skills.Clear();
                var prepared = CombatBrainPlanner.Prepare(actor, null, id, allowSkillCasts: true, DateTime.UtcNow);
                var decision = CombatBrainPlanner.Decide(prepared);
                await Assert.That(prepared.Inputs.SelectedSkillId).IsEqualTo(0u);
                await Assert.That(decision.BandMaxM).IsEqualTo((float)bowRange);
                if (distance > bowRange)
                    await Assert.That(decision.Arm).IsEqualTo(CombatArm.CloseRange);
                else if (distance >= 12f)
                    await Assert.That(decision.Verb).IsEqualTo(CombatVerb.AutoAttack);
                else
                {
                    await Assert.That(decision.Arm).IsEqualTo(CombatArm.BackOff);
                    var landingDistance = Vector3.Distance(decision.Destination!.Value,
                        session.World.GetNpc(id)!.Transform.World.Position);
                    var admitted = QuestBehavior.CombatAdmissionRadiusM(actor.Character, landingDistance, id);
                    await Assert.That(landingDistance).IsLessThanOrEqualTo(admitted);
                    await Assert.That(landingDistance).IsLessThanOrEqualTo((float)bowRange);
                    // Supplied landing in this local rig, then the ordinary
                    // native attack must accept the pose chosen by the brain.
                    GameplayActorTestRig.SetPosition(actor, decision.Destination.Value);
                    var attack = actor.AutoAttack(id);
                    await Assert.That(attack.Result).IsEqualTo(SkillResult.Success);
                }
            }, AbilityType.Wild);

    private static Dictionary<uint, AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate> CombatControlSkillTemplates()
        => (Dictionary<uint, AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(SkillManager.Instance)!;

    // Same native actor/equipment/basic4 setup as the two existing14m positives.
    // bowRange=-1 deliberately omits the bow; zero tests readable but unusable range.
    [Test]
    [Arguments("absent")]
    [Arguments("missing-task")]
    [Arguments("cancelled")]
    public async Task ArcherInRange_NoExecutableAttack_IsNotForcedToContactByOldStall(string state)
        => await WithMeleeBowControl("archer-start-before-stall", new Vector3(18.7f, 0, 0), 20,
            async (actor, session, id) =>
            {
                actor.Character.Skills.Skills.Clear();
                var npc = session.World.GetNpc(id)!;
                if (state == "cancelled")
                {
                    await Assert.That(actor.AutoAttack(id).State).IsEqualTo(ActorLifecycleState.Completed);
                    actor.Character.AutoAttackTask!.Cancel();
                    await Assert.That(actor.Character.AutoAttackTask.Cancelled).IsTrue();
                }
                else if (state == "missing-task")
                    actor.Character.IsAutoAttack = true; // Supplied stale native flag, not an executing attack.

                // Retained no-damage history is the supplied live-failure precondition.
                // It must not prevent a native attack from starting at valid bow range.
                QuestBehavior.ClearLeashStaleMemory();
                for (var i = 0; i < QuestBehavior.CombatStallWakes; i++)
                    QuestBehavior.CountLeashStaleWake(actor.ActorId, id, npc.Hp,
                        $"separate-leash-{i}", out _);
                await Assert.That(QuestBehavior.IsLeashStaleTarget(actor.ActorId, id)).IsTrue();
                for (var i = 0; i < QuestBehavior.CombatStallWakes + 2; i++)
                    QuestBehavior.CountCombatStallWake(actor.ActorId, id, npc.Hp,
                        $"before-attack-{i}", out _, selfAttributed: false);
                var moveCount = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);
                var result = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = $"start-before-stall-{state}" });
                await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
                await Assert.That(result.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
                await Assert.That(result.Request.Detail).Contains("auto-attack started (4)");
                await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
                await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(moveCount);
                await Assert.That(QuestBehavior.IsLeashStaleTarget(actor.ActorId, id)).IsTrue()
                    .Because("attack startup does not erase separate failed-approach/leash evidence");
                QuestBehavior.ClearLeashStaleMemory();
            }, ability: AbilityType.Wild);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MeleeBow_PreviousRunningAttackFailure_KeepsContactRecoveryWhenLoopStops(bool cancelled)
        => await WithMeleeBowControl("melee-bow-stopped-failure", new Vector3(18.7f, 0, 0), 20,
            async (actor, session, id) =>
            {
                await Assert.That(actor.AutoAttack(id).State).IsEqualTo(ActorLifecycleState.Completed);
                var npc = session.World.GetNpc(id)!;
                for (var i = 0; i < QuestBehavior.CombatStallWakes; i++)
                    QuestBehavior.CountCombatStallWake(actor.ActorId, id, npc.Hp,
                        $"running-bow-failure-{i}", out _, selfAttributed: false);
                if (cancelled)
                    actor.Character.AutoAttackTask!.Cancel();
                else
                    actor.StopAutoAttack();
                // Melee falls back to contact after an observed failed bow
                // attempt. It has no archer BackOff arm to pull it out again.
                var before = actor.AuditTrace.Count(r => r.Action == ActorActionType.AutoAttack);
                var result = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "melee-stopped-bow-failure" });
                await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
                await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AutoAttack)).IsEqualTo(before);
            });

    [Test]
    public async Task ArcherRetreat_ThenInRange_StartsAttackBeforeOldStallCanPullItBack()
        => await WithMeleeBowControl("archer-retreat-attack", new Vector3(2.5f, 0, 0), 20,
            async (actor, session, id) =>
            {
                actor.Character.Skills.Skills.Clear();
                var npc = session.World.GetNpc(id)!;
                for (var i = 0; i < QuestBehavior.CombatStallWakes + 2; i++)
                    QuestBehavior.CountCombatStallWake(actor.ActorId, id, npc.Hp,
                        $"before-retreat-{i}", out _, selfAttributed: false);
                ActorRequest? retreat = null;
                for (var wake = 0; wake < 4 && retreat == null; wake++)
                {
                    var result = QuestDecisionScenario.Run(actor, (_, _) => [],
                        new QuestDecisionScenario.QuestOptions { CycleId = $"retreat-before-attack-{wake}" });
                    if (result.Request is { Action: ActorActionType.Move, MoveOwner: "COMBAT_KITE_MOVE" } move)
                        retreat = move;
                }
                await Assert.That(retreat).IsNotNull();
                for (var tick = 0; tick < 300 && !retreat!.IsTerminal; tick++)
                    actor.Tick(TimeSpan.FromMilliseconds(100));
                await Assert.That(retreat!.State).IsEqualTo(ActorLifecycleState.Completed);
                var distance = actor.Character.GetDistanceTo(npc, true);
                await Assert.That(distance).IsGreaterThanOrEqualTo(12f);
                await Assert.That(distance).IsLessThanOrEqualTo(20f);
                var settled = QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = "after-native-retreat" });
                await Assert.That(settled.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
                await Assert.That(settled.Request!.Detail).Contains("auto-attack started (4)");
            }, ability: AbilityType.Wild);

    private static async Task WithMeleeBowControl(string name, Vector3 targetPosition, int bowRange,
        Func<GameplayActor, HeadlessSession, uint, Task> check, AbilityType ability = AbilityType.Fight)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Ability1 = ability;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        var templates = CombatControlSkillTemplates();
        templates.TryGetValue(4, out var priorSkill);
        AAEmu.Game.Models.Game.Items.Templates.WeaponTemplate? bow = null;
        (uint Kind, int Speed, int Min, int Max) priorBow = default;
        try
        {
            if (bowRange >= 0)
            {
                const uint bowTemplateId = 90771;
                GameplayActorTestRig.SeedEquipItemTemplate(bowTemplateId,
                    AAEmu.Game.Models.Game.Items.EquipmentItemSlotType.Ranged);
                bow = (AAEmu.Game.Models.Game.Items.Templates.WeaponTemplate)ItemManager.Instance.GetTemplate(bowTemplateId);
                priorBow = (bow.HoldableTemplate.KindId, bow.HoldableTemplate.Speed,
                    bow.HoldableTemplate.MinRange, bow.HoldableTemplate.MaxRange);
                bow.HoldableTemplate.KindId = 29;
                bow.HoldableTemplate.Speed = 2000;
                bow.HoldableTemplate.MinRange = 0;
                bow.HoldableTemplate.MaxRange = bowRange;
                GameplayActorTestRig.StockItem(session, bowTemplateId, 1);
                await Assert.That(actor.Equip(bowTemplateId).State).IsEqualTo(ActorLifecycleState.Completed);
            }
            templates[4] = new AAEmu.Game.Models.Game.Skills.Templates.SkillTemplate
            {
                Id = 4, MinRange = 4, MaxRange = 25, WeaponSlotForRangeId = 17,
                WeaponSlotForAutoAttackId = 17, TargetType = SkillTargetType.Hostile,
                TargetSelection = SkillTargetSelection.Target
            };
            var id = SpawnBoar(session, actor, Vector3.Zero, targetPosition);
            Activate251(actor.Character);
            actor.Character.CurrentTarget = session.World.GetNpc(id);
            await check(actor, session, id);
            await Assert.That(actor.Character.Ability1).IsEqualTo(ability);
            await Assert.That(actor.Character.Quests.HasQuestCompleted(Quest251)).IsFalse();
        }
        finally
        {
            actor.StopAutoAttack();
            actor.PreemptCurrent("bow controls cleanup");
            QuestBehavior.ClearCombatStallMemory();
            CombatBrainEngagement.ClearAll();
            TravelLegDispatch.EndJourney(actor, TravelLegDispatch.PursuitMoveOwner);
            if (priorSkill == null) templates.Remove(4); else templates[4] = priorSkill;
            if (bow != null)
            {
                bow.HoldableTemplate.KindId = priorBow.Kind;
                bow.HoldableTemplate.Speed = priorBow.Speed;
                bow.HoldableTemplate.MinRange = priorBow.Min;
                bow.HoldableTemplate.MaxRange = priorBow.Max;
            }
        }
    }

    private static void Activate251(Character character)
    {
        character.Quests.ActiveQuests[Quest251] = new Quest(character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
    }

    private static uint SpawnBoar(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 npcPos)
    {
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = session.SpawnNpc(Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
        return npcObjId;
    }
    private static uint SpawnExtraBoar(HeadlessSession session, Vector3 npcPos)
    {
        // HeadlessSession.SpawnNpc dedups by template — the second 3475 is
        // built directly so the funnel has two live candidates to choose from.
        var objId = s_extraObjId++;
        var npc = new Npc
        {
            ObjId = objId,
            TemplateId = Boar3475,
            Template = new NpcTemplate { Id = Boar3475, Scale = 1f },
            Hp = 100,
            MaxHp = 100,
            IsVisible = true,
        };
        npc.Transform.Local.SetPosition(npcPos);
        session.World.AddObject(npc);
        var region = session.World.GetRegionByPos(npcPos);
        if (region != null)
        {
            region.AddObject(npc);
            npc.Region = region;
        }
        return objId;
    }

    /// <summary>
    /// Additive, missing-only static surface for the 251 funnel: skill-2
    /// template (AutoAttack dispatch), quest-251 Progress gather act for item
    /// 4058 (objective resolution), and the 3475→4530→4058 loot link (source
    /// resolution). Canonical pilot data is never clobbered — every row is
    /// added only when the lookup would otherwise resolve empty.
    /// </summary>
    private static void SeedCombatSurface()
    {
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        SeedQuest251GatherAct();
        SeedBoarLootLink();
    }

    private static void SeedQuest251GatherAct()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(Quest251, out var template))
        {
            template = new QuestTemplate { Id = Quest251, Level = 2 };
            questTemplates[Quest251] = template;
        }
        if (template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == Meat4058))
            return;

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        const uint progressComponentId = 2510901;
        if (!componentTemplates.TryGetValue(progressComponentId, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = progressComponentId,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[progressComponentId] = component;
        }
        if (!template.Components.ContainsKey(progressComponentId))
            template.Components[progressComponentId] = component;
        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ItemId == Meat4058))
        {
            var act = new QuestActObjItemGather(component)
            {
                ActId = GatherActId,
                DetailId = 616,
                DetailType = nameof(QuestActObjItemGather),
                Count = GatherNeed,
                ItemId = Meat4058,
            };
            component.ActTemplates.Add(act);
            var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
                .GetField("_actTemplatesByDetailType", flags)!.GetValue(manager)!;
            if (!actsByType.TryGetValue(nameof(QuestActObjItemGather), out var acts))
            {
                acts = [];
                actsByType[nameof(QuestActObjItemGather)] = acts;
            }
            acts[616] = act;
        }
    }

    private static void SeedBoarLootLink()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || !rows.Any(r => r.LootPackId == Pack4530))
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
}
