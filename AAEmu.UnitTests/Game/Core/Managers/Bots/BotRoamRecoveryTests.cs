using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Core.Managers.World;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class BotRoamRecoveryTests
{
    private readonly List<(BotRoamStepExecutor Executor, GameplayActor Actor)> _rigs = [];
    private HashSet<uint> _previousTasks = [];
    private readonly List<WorldInstance> _registeredWorlds = [];
    private readonly List<(FieldInfo Field, object? Previous)> _replacedSingletons = [];
    private static uint s_nextWorldId = 0xD711_0000;
    private static ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task> Tasks
        => (ConcurrentDictionary<uint, AAEmu.Game.Models.Tasks.Task>)typeof(TaskManager)
            .GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(TaskManager.Instance)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        _previousTasks = Tasks.Keys.ToHashSet();
    }

    [After(Test)]
    public void TearDown()
    {
        foreach (var (executor, actor) in _rigs)
        {
            executor.ReleaseRecovery(actor.Character.Id, "recovery regression teardown");
            actor.PreemptCurrent("recovery regression teardown");
            foreach (var buff in actor.Character.Buffs.GetEffectsByType(typeof(BuffTemplate)))
                buff.SetInUse(false, false);
        }
        foreach (var task in Tasks.Where(x => !_previousTasks.Contains(x.Key)).Select(x => x.Value).ToArray())
            TaskManager.Instance.Cancel(task);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(WorldManager.Instance)!;
        foreach (var world in _registeredWorlds)
            ((ICollection<KeyValuePair<uint, WorldInstance>>)worlds).Remove(new(world.Id, world));
        _registeredWorlds.Clear();
        foreach (var (field, previous) in _replacedSingletons.AsEnumerable().Reverse())
            field.SetValue(null, previous);
        _replacedSingletons.Clear();
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    [Test]
    public async Task ContinuousWakes_HoldRecoveryAndBlockRouteHuntFallback_WithoutGrantingPoints()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-continuous");
        var origin = actor.Character.Transform.World.Position;
        var wildlife = new Npc { ObjId = 0x74001, Hp = 100, MaxHp = 100,
            Faction = new SystemFaction { Id = (FactionsEnum)115 } };
        wildlife.Transform.Local.SetPosition(origin + new Vector3(2, 0, 0));
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest, wildlife);
        executor.SetRoamRoute(actor.Character, new BotPath([origin + new Vector3(20, 0, 0)]));
        var hp = actor.Character.Hp;
        var mp = actor.Character.Mp;

        for (var wake = 0; wake < 10; wake++)
        {
            var cadence = await Step(executor, bot, clock);
            await Assert.That(cadence).IsEqualTo(executor.ActiveCadence);
            await Assert.That(actor.Character.Hp).IsEqualTo(hp);
            await Assert.That(actor.Character.Mp).IsEqualTo(mp);
            await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(origin);
        }
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryActive).IsTrue();
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Sit);
        await Assert.That(actor.AuditTrace).IsEmpty();
        await Assert.That(executor.GetBotState(bot.CharacterId)!.TargetNpcObjId).IsEqualTo(0u);
    }

    [Test]
    public async Task ArbitratedRecovery_UnchangedElectionStaysContinuous_ThenNativeCompletionCleansOwnedRest()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-arbitrated");
        BotRoamStepExecutor? executor = null;
        var module = new OutOfCombatRecoveryModule
        {
            RecoveryEligibilityProvider = bot => executor?.RecoveryBlockedReason(bot.CharacterId, bot.Generation)
        };
        var arbiter = new BotGoalArbiter([module]);
        var clock = new FakeTimeProvider();
        executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor, TimeProvider = clock, ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false, EnableWildlifeButcher = false, GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = arbiter.GetActiveActivity
        };
        _rigs.Add((executor, actor));
        var bot = new PlayerBotRuntime(actor.Character, "rig");
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);
        var hp = actor.Character.Hp;
        var mp = actor.Character.Mp;
        for (var wake = 0; wake < 5; wake++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Assert.That(await steps.StepAsync(bot, CancellationToken.None)).IsEqualTo(executor.ActiveCadence);
            await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo(OutOfCombatRecoveryModule.ActivityNameRest);
            await Assert.That(actor.Character.Hp).IsEqualTo(hp);
            await Assert.That(actor.Character.Mp).IsEqualTo(mp);
        }
        for (var tick = 0; tick < 1000 && !OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character); tick++)
            actor.Character.OnActiveRegionTick(TimeSpan.FromSeconds(1));
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character)).IsTrue();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await steps.StepAsync(bot, CancellationToken.None);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryActive).IsFalse();
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
    }

    /// <summary>
    /// Plan T5 (audit L4-a remainder): a supersede retires the outgoing
    /// activity through exactly one owner. The decorator used to release
    /// recovery on its own read of the election in the same wake, which is the
    /// duplicate this card removed; the surviving owner is the executor's own
    /// read, which stamps its own reason on the interrupted recovery.
    /// </summary>
    [Test]
    public async Task SupersededRecovery_IsReleasedByTheExecutorsOwnElectionRead()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-supersede-owner");
        BotRoamStepExecutor? executor = null;
        var module = new OutOfCombatRecoveryModule
        {
            RecoveryEligibilityProvider = bot => executor?.RecoveryBlockedReason(bot.CharacterId, bot.Generation)
        };
        var probe = new SupersedeProbeModule();
        var arbiter = new BotGoalArbiter([probe, module]);
        var clock = new FakeTimeProvider();
        executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor, TimeProvider = clock, ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false, EnableWildlifeButcher = false, GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = arbiter.GetActiveActivity
        };
        _rigs.Add((executor, actor));
        var bot = new PlayerBotRuntime(actor.Character, "rig");
        var steps = new BotGoalArbiterStepExecutor(arbiter, executor, () => 12f);

        for (var wake = 0; wake < 5; wake++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await steps.StepAsync(bot, CancellationToken.None);
        }
        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo(OutOfCombatRecoveryModule.ActivityNameRest);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryActive).IsTrue();

        probe.Allow = true;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await steps.StepAsync(bot, CancellationToken.None);

        await Assert.That(arbiter.GetActiveActivity(bot.CharacterId)).IsEqualTo("probe.work");
        var state = executor.GetBotState(bot.CharacterId)!;
        await Assert.That(state.RecoveryActive).IsFalse();
        await Assert.That(state.RecoveryResult!.Detail)
            .IsEqualTo("recovery.interrupted: recovery superseded or unsafe");
    }


    [Test]
    public async Task ForeignPendingItem_KeepsExactIdentityAndQuantity_AcrossRecoveryWakes()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-foreign-item");
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98511, 20, castTime: 5000);
        var foreign = actor.UseItem(item.TemplateId, actor.Character.ObjId, "foreign-owned-use");
        await Assert.That(foreign.State).IsEqualTo(ActorLifecycleState.Running);
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        var hp = actor.Character.Hp;

        for (var wake = 0; wake < 5; wake++)
        {
            await Step(executor, bot, clock);
            await Assert.That(actor.ActiveRequest).IsSameReferenceAs(foreign);
            await Assert.That(foreign.IsTerminal).IsFalse();
            await Assert.That(item.Count).IsEqualTo(2);
            await Assert.That(actor.Character.Hp).IsEqualTo(hp);
            await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        }
        executor.ReleaseRecovery(bot.CharacterId, "foreign boundary must not be preempted");
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(foreign);
        await Assert.That(foreign.IsTerminal).IsFalse();
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryResult!.Detail).IsEqualTo("recovery.waiting-safe-boundary");
    }

    [Test]
    public async Task ForeignMove_IsTickedNormally_AndRecoveryStartsOnlyAfterItsNativeTerminal()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-foreign-move");
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        var start = actor.Character.Transform.World.Position;
        var foreign = actor.MoveTo(start + new Vector3(5, 0, 0), 5f, TimeSpan.FromSeconds(10));
        await Assert.That(foreign.IsTerminal).IsFalse();
        await Step(executor, bot, clock);
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(foreign);
        await Assert.That(actor.Character.Transform.World.Position).IsNotEqualTo(start);
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        for (var wake = 0; wake < 100 && !foreign.IsTerminal; wake++)
            await Step(executor, bot, clock);
        await Assert.That(foreign.State).IsEqualTo(ActorLifecycleState.Completed);
        await Step(executor, bot, clock);
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Sit);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryActive).IsTrue();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(1);
    }

    [Test]
    public async Task OrdinaryInstantHeal_ConsumesOnceThroughNativeEffects_NotSyntheticManaOrCompletion()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-native-item", 0.50f, 0.40f);
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98512, 20);
        var hp = actor.Character.Hp;
        var mp = actor.Character.Mp;
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);

        await Step(executor, bot, clock);

        await Assert.That(actor.Character.Hp).IsGreaterThan(hp);
        await Assert.That(actor.Character.Mp).IsEqualTo(mp);
        await Assert.That(item.Count).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.UseItem && r.Result == ActorLifecycleState.Completed)).IsEqualTo(1);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryResult!.Status).IsEqualTo(RecoveryStatus.Eating);
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character)).IsFalse();
    }

    [Test]
    public async Task OwnedDelayedItem_RemainsPendingAcrossWakes_AndSupersessionCancelsWithoutConsumption()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-owned-pending");
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98513, 20, castTime: 5000);
        var activity = OutOfCombatRecoveryModule.ActivityNameRest;
        var (executor, bot, clock) = CreateExecutor(actor, () => activity);
        await Step(executor, bot, clock);
        var owned = executor.GetBotState(bot.CharacterId)!.RecoveryRequest!;
        await Assert.That(owned.State).IsEqualTo(ActorLifecycleState.Running);
        for (var wake = 0; wake < 5; wake++)
        {
            await Step(executor, bot, clock);
            await Assert.That(actor.ActiveRequest).IsSameReferenceAs(owned);
            await Assert.That(item.Count).IsEqualTo(2);
        }
        activity = "schedule.travel";
        await Step(executor, bot, clock);
        await Assert.That(owned.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(item.Count).IsEqualTo(2);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryRequest).IsNull();
    }

    [Test]
    public async Task TimedHealingBuff_IsNotReconsumedOrMistakenForFullyRecoveredVitals()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-active-buff");
        var item = OrdinaryRecoveryTestRig.StockTimedHeal(actor, 98514, 98614);
        using var scheduler = new GameplayActorUseItemPacketTests.NativeScheduler();
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        var hp = actor.Character.Hp;
        await Step(executor, bot, clock);
        await Assert.That(actor.Character.Buffs.CheckBuff(98614)).IsTrue();
        await Assert.That(item.Count).IsEqualTo(1);
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character)).IsFalse();
        for (var wake = 0; wake < 5; wake++)
            await Step(executor, bot, clock);
        await Assert.That(item.Count).IsEqualTo(1);
        await Assert.That(actor.Character.Hp).IsEqualTo(hp); // No native buff callback was driven by these actor wakes.
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryResult!.Status).IsEqualTo(RecoveryStatus.Resting);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.UseItem)).IsEqualTo(1);
    }

    [Test]
    public async Task CooldownRefusal_IsConservedBlockedAndNotRetriedUntilRelevantReadinessChanges()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-refusal");
        var item = OrdinaryRecoveryTestRig.StockHeal(actor, 98515, 20);
        SkillManager.Instance.GetSkillTemplate(item.Template.UseSkillId).CooldownTime = 90000;
        actor.Character.Cooldowns.AddCooldown(item.Template.UseSkillId, 90000); // Fixture initial ordinary readiness.
        var hp = actor.Character.Hp;
        var mp = actor.Character.Mp;
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        await Step(executor, bot, clock);
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation)).IsNotNull();
        await Assert.That(item.Count).IsEqualTo(2);
        await Assert.That(actor.Character.Hp).IsEqualTo(hp);
        await Assert.That(actor.Character.Mp).IsEqualTo(mp);
        for (var wake = 0; wake < 5; wake++)
            await Step(executor, bot, clock);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.UseItem)).IsEqualTo(1);
        actor.Character.OnActiveRegionTick(TimeSpan.FromSeconds(1));
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation)).IsNotNull();
        actor.Character.Cooldowns.RemoveCooldown(item.Template.UseSkillId);
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation)).IsNull();
        await Step(executor, bot, clock);
        await Assert.That(item.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ProtectedAndNonHealingSupply_IsNeverDrawnByContinuousOwner()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-conserved-supplies");
        var protectedItem = OrdinaryRecoveryTestRig.StockHeal(actor, 98516, 20);
        OrdinaryRecoveryTestRig.SeedItemQuest(actor, 98616, protectedItem.TemplateId);
        var nonHealing = OrdinaryRecoveryTestRig.StockBuff(actor, 98517,
            new BuffTemplate { Id = 98617, Duration = 30000, Kind = BuffKind.Good });
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        for (var wake = 0; wake < 5; wake++)
            await Step(executor, bot, clock);
        await Assert.That(protectedItem.Count).IsEqualTo(2);
        await Assert.That(nonHealing.Count).IsEqualTo(2);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.UseItem)).IsFalse();
    }

    [Test]
    public async Task NativeCharacterRegionTicks_ProduceProgressAndCompletion_WhileOwnerOnlyObserves()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-native-rest");
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        var hp = actor.Character.Hp;
        var mp = actor.Character.Mp;
        await Step(executor, bot, clock);
        await Assert.That(actor.Character.Hp).IsEqualTo(hp);
        await Assert.That(actor.Character.Mp).IsEqualTo(mp);
        actor.Character.OnActiveRegionTick(TimeSpan.FromSeconds(1));
        await Assert.That(actor.Character.Hp).IsGreaterThan(hp);
        await Assert.That(actor.Character.Mp).IsGreaterThan(mp);
        for (var tick = 0; tick < 1000 && !OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character); tick++)
        {
            await Step(executor, bot, clock);
            actor.Character.OnActiveRegionTick(TimeSpan.FromSeconds(1));
        }
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character)).IsTrue();
        await Step(executor, bot, clock);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryActive).IsFalse();
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryResult!.Status).IsEqualTo(RecoveryStatus.Completed);
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.UseItem)).IsFalse();
    }

    [Test]
    public async Task NativeNoProgress_ReleasesWithNamedBlocker_AndReadmitsOnlyChangedNativeFacts()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-no-progress");
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        await Step(executor, bot, clock);
        clock.Advance(BotRoamStepExecutor.RecoveryNoProgressTimeout);
        await Step(executor, bot, clock);
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation)).IsEqualTo("recovery.native-no-progress");
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryActive).IsFalse();
        await Step(executor, bot, clock);
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        actor.Character.OnActiveRegionTick(TimeSpan.FromSeconds(1));
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation)).IsNull();
        await Step(executor, bot, clock);
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Sit);
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation + 1)).IsNull();
    }

    [Test]
    public async Task UnsupportedNativeShortage_IsBlockedAndConservesInventory()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-shortage");
        actor.Character.Breath = 0; // Fixture initial native no-regeneration condition; no fake rates.
        var item = OrdinaryRecoveryTestRig.Stock(actor, 98518);
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        await Step(executor, bot, clock);
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation)).IsEqualTo("recovery.unsupported-shortage");
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        await Assert.That(item.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("combat")]
    [Arguments("death")]
    [Arguments("supersession")]
    [Arguments("teardown")]
    public async Task Interruption_CleansOwnedPostureWithoutEligibilityProbe(string interruption)
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-interruption");
        var activity = OutOfCombatRecoveryModule.ActivityNameRest;
        var (executor, bot, clock) = CreateExecutor(actor, () => activity);
        await Step(executor, bot, clock);
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Sit);
        switch (interruption)
        {
            case "combat":
                actor.Character.IsInBattle = true;
                await Step(executor, bot, clock);
                break;
            case "death":
                actor.Character.Hp = 0; // Death fixture, not recovery success/progress.
                executor.ReleaseQuestHoldOnDeath(bot.CharacterId, "death watch");
                break;
            case "supersession":
                activity = "schedule.travel";
                await Step(executor, bot, clock);
                break;
            case "teardown":
                executor.ReleaseRecovery(bot.CharacterId, "deactivated");
                break;
        }
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        await Assert.That(actor.Character.IdleStatus).IsFalse();
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryActive).IsFalse();
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryRequest).IsNull();
    }

    [Test]
    public async Task GenerationReplacement_ReleasesOldOwnedPostureAndCannotCarryItsBlockedEpisode()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-generation");
        var activity = OutOfCombatRecoveryModule.ActivityNameRest;
        var (executor, bot, clock) = CreateExecutor(actor, () => activity);
        await Step(executor, bot, clock);
        var old = executor.GetBotState(bot.CharacterId)!;
        activity = "schedule.travel";
        bot.Generation++;
        await Step(executor, bot, clock);
        await Assert.That(old.RecoveryActive).IsFalse();
        await Assert.That(old.RecoveryOwnsSit).IsFalse();
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Stand);
        await Assert.That(executor.GetBotState(bot.CharacterId)).IsNotSameReferenceAs(old);
        await Assert.That(executor.RecoveryBlockedReason(bot.CharacterId, bot.Generation)).IsNull();
    }

    [Test]
    public async Task ForeignSittingPosture_IsNotClaimedOrCleanedByRecovery()
    {
        var actor = OrdinaryRecoveryTestRig.CreateActor("recovery-foreign-posture");
        actor.Character.Stance = UnitStance.Sit; // Initial foreign-owned posture.
        var (executor, bot, clock) = CreateExecutor(actor, () => OutOfCombatRecoveryModule.ActivityNameRest);
        await Step(executor, bot, clock);
        executor.ReleaseRecovery(bot.CharacterId, "superseded");
        await Assert.That(actor.Character.Stance).IsEqualTo(UnitStance.Sit);
        await Assert.That(executor.GetBotState(bot.CharacterId)!.RecoveryOwnsSit).IsFalse();
    }

    [Test]
    public async Task RetainedQuestPurpose_SurvivesRecoveryAndResumesWithFreshOrdinaryPlanning()
    {
        const uint questId = 98701;
        const uint preyTemplate = 98702;
        var (actor, session) = GameplayActorTestRig.CreateActor("recovery-retained-purpose");
        typeof(WorldInstance).GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(WorldManager.Instance)!;
        if (!worlds.TryAdd(session.World.Id, session.World))
            throw new InvalidOperationException("Recovery fixture world identity collision");
        _registeredWorlds.Add(session.World);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, session.World.Id);
        var factionManager = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        ReplaceSingleton(typeof(Singleton<FactionManager>), factionManager);
        factionManager.Load(); // Canonical read-only relations, not a forced attack predicate.
        var zoneManager = new ZoneManager(WorldManager.Instance);
        typeof(ZoneManager).GetField("_zones", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(zoneManager, new Dictionary<uint, Zone>());
        ReplaceSingleton(typeof(Singleton<ZoneManager>), zoneManager);
        actor.Character.Faction = factionManager.GetFaction(FactionsEnum.Nuian);
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        actor.Character.Breath = 1;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        var origin = new Vector3(32, 32, 0);
        GameplayActorTestRig.SetPosition(actor, origin);
        var region = session.World.GetRegionByPos(origin)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        GameplayActorTestRig.SeedQuestHunt(questId, 98711, 98712, 98713, 98703, preyTemplate, 98703, level: 1);
        var accepted = actor.AcceptQuest(questId, QuestAcceptorType.Npc, 98703, "recovery-retained-purpose-fixture");
        await Assert.That(accepted.State).IsEqualTo(ActorLifecycleState.Completed);
        var quest = actor.Character.Quests.ActiveQuests[questId];
        var preyId = session.SpawnNpc(preyTemplate);
        var prey = session.World.GetNpc(preyId)!;
        prey.Template = new NpcTemplate { Id = preyTemplate, Scale = 1f };
        prey.Hp = prey.MaxHp = 100000;
        prey.Faction = factionManager.GetFaction((FactionsEnum)115);
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyId, origin + new Vector3(20, 0, 0));
        region.AddObject(prey);
        prey.Region = region;
        await Assert.That(actor.Character.CanAttack(prey)).IsTrue();
        await Assert.That(actor.Character.CanSeeTarget(prey)).IsTrue();
        actor.Character.CurrentTarget = prey;
        QuestFixtureRow.InvalidateAll();
        var owner = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var activity = QuestBootstrapActivityModule.ActivityName;
        var bot = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();
        var retirements = 0;
        var executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor, TimeProvider = clock, ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = false, EnableWildlifeButcher = false, GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => activity, QuestChosenProvider = owner.GetChosenQuest,
            QuestTerminalSink = owner.NotifyQuestTerminal, QuestGiveUpSink = (_, _) => retirements++,
            NearbyNpcProvider = (_, _) => [prey], NearbyDoodadProvider = (_, _) => []
        };
        _rigs.Add((executor, actor));
        executor.GetOrCreateActor(actor.Character);
        QuestLegActor.SetResolver(executor.TryGetActor);
        var context = new BotActivityContext { Bot = bot, GameHour = 12f };
        var initialElection = owner.CanActivate(context);
        await Assert.That(initialElection.CanActivate).IsTrue().Because(
            $"{initialElection.WhyNot}; {System.Text.Json.JsonSerializer.Serialize(BotOpportunityListing.List(actor))}");
        owner.Activate(context);
        await Assert.That(owner.GetChosenQuest(bot.CharacterId)).IsEqualTo(questId);
        // Remaining fixture initial state: retained purpose needs recovery before
        // any scenario action. No post-action HP/MP or quest completion assignment.
        actor.Character.Hp = actor.Character.MaxHp / 2;
        actor.Character.Mp = actor.Character.MaxMp / 2;
        await Step(executor, bot, clock);
        await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var pendingRuntime)).IsTrue();
        var pending = pendingRuntime!.CurrentActorRequest!;
        await Assert.That(pending is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        activity = OutOfCombatRecoveryModule.ActivityNameRest;
        await Step(executor, bot, clock);
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(pending);
        await Assert.That(pendingRuntime.CurrentActorRequest).IsSameReferenceAs(pending);
        for (var wake = 0; wake < 300 && !pending.IsTerminal; wake++)
            await Step(executor, bot, clock);
        await Assert.That(pending.State).IsEqualTo(ActorLifecycleState.Completed);
        await Step(executor, bot, clock);
        await Assert.That(pendingRuntime.CurrentActorRequest).IsNull();
        await Assert.That(owner.GetChosenQuest(bot.CharacterId)).IsEqualTo(questId);
        for (var tick = 0; tick < 1000 && !OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character); tick++)
        {
            actor.Character.OnActiveRegionTick(TimeSpan.FromSeconds(1));
            await Step(executor, bot, clock);
        }
        await Assert.That(OutOfCombatRecoveryModule.IsRecoveryComplete(actor.Character)).IsTrue();
        await Step(executor, bot, clock);
        await Assert.That(owner.GetChosenQuest(bot.CharacterId)).IsEqualTo(questId);
        activity = QuestBootstrapActivityModule.ActivityName;
        var resumedElection = owner.CanActivate(context);
        await Assert.That(resumedElection.CanActivate).IsTrue().Because(resumedElection.WhyNot ?? "");
        var auditCountBeforeResume = actor.AuditTrace.Count;
        for (var wake = 0; wake < 3 && !actor.Character.IsAutoAttack; wake++)
            await Step(executor, bot, clock);
        await Assert.That(executor.TryGetQuestRuntime(bot.CharacterId, out var runtime)).IsTrue();
        await Assert.That(runtime!.LastResult).IsNotNull();
        var resumed = actor.AuditTrace.Skip(auditCountBeforeResume)
            .Single(request => request.Action == ActorActionType.AutoAttack && request.TargetId == prey.ObjId);
        await Assert.That(resumed.Result).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(resumed.DecisionQuestId).IsEqualTo((uint?)questId);
        await Assert.That(resumed.TraceId).IsNotEqualTo(pending.TraceId);
        await Assert.That(actor.Character.IsAutoAttack).IsTrue();
        await Assert.That(actor.Character.Quests.ActiveQuests[questId]).IsSameReferenceAs(quest);
        await Assert.That(quest.Status).IsEqualTo(QuestStatus.Progress);
        await Assert.That(quest.Objectives.All(value => value == 0)).IsTrue();
        await Assert.That(retirements).IsEqualTo(0);
    }

    private (BotRoamStepExecutor Executor, PlayerBotRuntime Bot, FakeTimeProvider Clock)
        CreateExecutor(GameplayActor actor, Func<string> activity, Npc? wildlife = null)
    {
        var clock = new FakeTimeProvider();
        var executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor, TimeProvider = clock, ActiveCadence = TimeSpan.FromMilliseconds(100),
            EnableWildlifeHunt = true, EnableWildlifeButcher = true,
            ActiveActivityProvider = _ => activity(), GroundHeightProvider = (_, _) => 0f,
            NearbyNpcProvider = (_, _) => wildlife == null ? [] : [wildlife], NearbyDoodadProvider = (_, _) => [],
            UnitResolver = (character, id) => wildlife != null && id == wildlife.ObjId ? wildlife : character.ParentWorld?.GetUnit(id)
        };
        _rigs.Add((executor, actor));
        return (executor, new PlayerBotRuntime(actor.Character, "rig"), clock);
    }

    private static async Task<TimeSpan?> Step(BotRoamStepExecutor executor, PlayerBotRuntime bot, FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMilliseconds(100));
        return await executor.StepAsync(bot, CancellationToken.None);
    }

    /// <summary>
    /// Higher-band module that starts by denying, so a rig can supersede an
    /// active recovery mid-run and observe which owner released the state.
    /// </summary>
    private sealed class SupersedeProbeModule : IBotActivityModule
    {
        public string Name => "SupersedeProbe";
        public int Priority => 90;
        public bool Allow { get; set; }

        public BotActivityDecision CanActivate(BotActivityContext context) =>
            Allow ? BotActivityDecision.Allow("probe.work") : BotActivityDecision.Deny("probe idle");

        public BotActivity Activate(BotActivityContext context) => new("probe.work", Name);
    }

    private void ReplaceSingleton(Type singletonType, object replacement)
    {
        var field = singletonType.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        _replacedSingletons.Add((field, field.GetValue(null)));
        field.SetValue(null, replacement);
    }

}
