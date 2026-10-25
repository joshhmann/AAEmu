using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Game.Quests.Scenario;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// P0.4 RUNNING OWNERSHIP: the quest runtime is the single owner of a
/// dispatched-but-running job. A selected Running job survives across bot
/// turns without re-selection, its exact terminal is consumed once,
/// unrelated fallback never replaces it mid-run, and the safety flee still
/// preempts. Built on the P0.2 invariants (duplicate-before-busy, locked
/// records, retry-once-then-fault, generation stamps) — none of them move.
/// </summary>
[NotInParallel]
public class QuestRunningOwnershipTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;
    private const string ReturnMoveOwner = "RETURN_MOVE_TO_UNIT";

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        SurvivalVetoState.ClearAll();
        SeedQuest251GatherAct();
        SeedBoarLootLink();
    }

    [After(Test)]
    public void TearDown()
    {
        SurvivalVetoState.ClearAll();
        CombatBrainEngagement.ClearAll();
        TravelIntentStore.ClearAll();
        QuestBehavior.ClearCombatStallMemory();
    }

    /// <summary>
    /// (a) A selected Running job survives across bot turns without
    /// re-selection: wake 1 dispatches the pursuit Move, wake 2 (actor busy)
    /// holds the SAME request — no new Move dispatched, no audit row added.
    /// </summary>
    [Test]
    public async Task OwnedRunningJob_SurvivesAcrossTurns_WithoutReselection()
    {
        var (executor, actor, runtime, clock) = CreateQuestRig("own-hold", "quest.progress",
            new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        var movesAfterDispatch = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(owned);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move))
            .IsEqualTo(movesAfterDispatch)
            .Because("the hold wake must dispatch nothing — the owned leg is still Running");
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(actor.ActiveRequest)).IsTrue();
    }

    /// <summary>
    /// (b) The exact terminal is consumed once: driving the owned Move to
    /// arrival completes it, the next quest wake consumes the Completed
    /// terminal (lands, releases ownership), and the following wake
    /// re-decides instead of re-consuming.
    /// </summary>
    [Test]
    public async Task OwnedRunningJob_TerminalIsConsumedExactlyOnce()
    {
        var (executor, actor, runtime, clock) = CreateQuestRig("own-terminal", "quest.progress",
            new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false }).IsTrue();

        // Drive the owned leg to its exact arrival terminal through the
        // actor's own Tick (movement integration, not a test shortcut).
        for (var i = 0; i < 200 && !owned.IsTerminal; i++)
            actor.Tick(TimeSpan.FromMilliseconds(100));
        await Assert.That(owned.State).IsEqualTo(ActorLifecycleState.Completed);

        // Consume wake: the runtime reads the exact terminal and lands.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(actor.ActiveRequest)).IsFalse()
            .Because("consuming the terminal releases ownership");

        // Settle the consumed wake's follow-up (a fresh decision may have
        // dispatched a new leg or held) and prove no second consume happens:
        // the audit holds exactly one Completed row for the owned trace.
        var ownedRows = actor.AuditTrace.Where(r => r.TraceId == owned.TraceId).ToList();
        await Assert.That(ownedRows.Count).IsEqualTo(1);
        await Assert.That(ownedRows[0].Result).IsEqualTo(ActorLifecycleState.Completed);
    }

    /// <summary>
    /// (c) Unrelated fallback does not replace the owned job mid-run: while
    /// the pursuit Move is Running, no hunt acquisition, no butcher target,
    /// and no quest-travel route arms; a further wake issues no second Move.
    /// </summary>
    [Test]
    public async Task OwnedRunningJob_UnrelatedFallbackDoesNotReplaceIt()
    {
        var (executor, actor, runtime, clock, session) = CreateQuestRigWithWildlife("own-fallback",
            "quest.progress", new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(2, 0, 0));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();

        for (var wake = 0; wake < 3; wake++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await executor.StepAsync(runtime, CancellationToken.None);
        }

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(owned);
        await Assert.That(state.TargetNpcObjId).IsEqualTo(0u)
            .Because("wildlife in sight must not be acquired while the quest job is owned");
        await Assert.That(state.TargetButcherDoodadObjId).IsEqualTo(0u);
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull()
            .Because("quest travel must not arm against our own leg");
        var moves = actor.AuditTrace.Where(r => r.Action == ActorActionType.Move).ToList();
        await Assert.That(moves.Any(r => r.Result == ActorLifecycleState.Rejected)).IsFalse();
        _ = session;
    }

    /// <summary>
    /// (d) Safety interruption still preempts: a critical-in-fight wake
    /// retires the owned quest leg through the actor (Interrupted with the
    /// flee's own retrack detail) and the flee leg owns the wake.
    /// </summary>
    [Test]
    public async Task OwnedRunningJob_SafetyInterruptionStillPreempts()
    {
        var (executor, actor, runtime, clock, session) = CreateQuestRigWithWildlife("own-flee",
            "quest.progress", new Vector3(100f, 100f, 10f), new Vector3(110f, 100f, 10f), new Vector3(110f, 100f, 10f));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();

        // Critical hp WITH fight evidence: the flee owns the wake.
        actor.Character.Hp = Math.Max(0, (int)(actor.Character.MaxHp * SurvivalBrain.FleeHpThreshold * 0.5f));
        actor.Character.CurrentTarget = session.World.GetNpcByTemplateId(Boar3475);
        var traceBefore = actor.AuditTrace.Count;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        var state = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(state.SurvivalWakeOwned).IsTrue();
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(actor.ActiveRequest!.MoveOwner).IsEqualTo(BotRoamStepExecutor.SurvivalFleeMoveOwner);
        await Assert.That(actor.ActiveRequest).IsNotSameReferenceAs(owned);
        var retired = actor.AuditTrace.Skip(traceBefore)
            .Where(r => r.Action == ActorActionType.Move && r.TraceId == owned.TraceId).ToList();
        await Assert.That(retired.Count).IsEqualTo(1);
        await Assert.That(retired[0].Result).IsEqualTo(ActorLifecycleState.Interrupted);
        // Ownership is gone with the leg: the next quest wake re-decides.
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(actor.ActiveRequest)).IsFalse();
    }

    [Test]
    public async Task OwnedPursuit_TargetDies_StopsWithoutWaitingForNavigationTimeout()
    {
        var (executor, actor, runtime, clock, session) = CreateQuestRigWithWildlife("own-dead-prey",
            "quest.progress", new Vector3(100, 100, 10), new Vector3(130, 100, 10), null);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        var prey = session.World.GetNpc(owned.TargetId)!;
        var objectives = actor.Character.Quests.ActiveQuests[Quest251].Objectives.ToArray();

        // A competing player killing the prey is supplied world change. The
        // production executor, not the test, must retire the obsolete move.
        prey.Hp = 0;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var nextWake = await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(nextWake).IsEqualTo(executor.ActiveCadence)
            .Because("retiring a chase must promptly recheck combat or replacement prey");

        await Assert.That(owned.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(owned.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == owned.TraceId)).IsEqualTo(1);
        await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Objectives.SequenceEqual(objectives)).IsTrue();
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(owned)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnedPursuit_ReachesCombatRange_StopsBeforeWalkingOntoThePrey(bool ranged)
    {
        var (executor, actor, runtime, clock, session) = CreateQuestRigWithWildlife("own-combat-range",
            "quest.progress", new Vector3(100, 100, 10), new Vector3(130, 100, 10), null);
        if (ranged)
            actor.Character.Ability1 = AbilityType.Wild;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        var prey = session.World.GetNpc(owned.TargetId)!;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();

        // Native movement reaches the combat policy's own range. It still
        // has distance left to the exact unit position, so a blind hold loses
        // attack time even though the existing combat policy can take over.
        for (var i = 0; i < 100 && !owned.IsTerminal
            && !QuestBehavior.CombatOwnsInsideBand(actor.Character, actor.Character.GetDistanceTo(prey, true), prey.ObjId); i++)
            actor.Tick(TimeSpan.FromMilliseconds(100));
        await Assert.That(owned.IsTerminal).IsFalse();
        await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character,
            actor.Character.GetDistanceTo(prey, true), prey.ObjId)).IsTrue();
        var movesBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        var nextWake = await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(nextWake).IsEqualTo(executor.ActiveCadence)
            .Because("retiring a chase must promptly recheck combat or replacement prey");

        await Assert.That(owned.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == owned.TraceId)).IsEqualTo(1);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(movesBefore + 1);
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.LastProgressAt).IsNull()
            .Because("ending a chase is not quest progress or a fabricated arrival");

        try
        {
            for (var i = 0; i < 3 && !actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack); i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await executor.StepAsync(runtime, CancellationToken.None);
            }
            await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack
                && r.TargetId == prey.ObjId)).IsTrue()
                .Because("the normal combat action must be attempted after the retired chase; the engine still owns its result");
        }
        finally
        {
            actor.StopAutoAttack();
        }
    }

    [Test]
    public async Task OwnedPursuit_ContactRecoveryAcrossTargets_RemainsOwnedInsideRangedBand()
    {
        var (executor, actor, runtime, clock, session) = CreateQuestRigWithWildlife("own-contact-recovery",
            "quest.progress", new Vector3(100, 100, 10), new Vector3(118, 100, 10), null);
        actor.Character.Ability1 = AbilityType.Wild;
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicRangedAutoAttackSkillId);
        await Assert.That(actor.AutoAttack(actor.Character.CurrentTarget!.ObjId).State)
            .IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        // Supply past no-damage observations across DIFFERENT prey. The
        // production pursuit must preserve the actor-wide contact decision,
        // even when the new target has no target-specific stall streak yet.
        for (var i = 0; i < BotBrainTuningStore.ResolveCombatStallWakes() - 1; i++)
            _ = QuestBehavior.CountCombatStallWake(actor.ActorId, (uint)(0x72000 + i), 100, $"prior-{i}", out _);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();
        await Assert.That(owned.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
        var prey = session.World.GetNpc(owned.TargetId)!;
        await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character,
            actor.Character.GetDistanceTo(prey, true), prey.ObjId)).IsTrue();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(owned);
        await Assert.That(owned.IsTerminal).IsFalse()
            .Because("an intentional contact recovery must continue through the ranged band");
    }

    [Test]
    public async Task OwnedPursuit_ReplacedByAnotherMove_DoesNotStopTheNewOwner()
    {
        var (executor, actor, runtime, clock, session) = CreateQuestRigWithWildlife("own-replaced-prey",
            "quest.progress", new Vector3(100, 100, 10), new Vector3(130, 100, 10), null);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest!;
        var prey = session.World.GetNpc(owned.TargetId)!;
        actor.PreemptCurrent("operator replaced the pursuit");
        actor.SetPendingMoveOwner("OTHER:operator");
        var replacement = actor.MoveTo(new Vector3(160, 100, 10));
        prey.Hp = 0;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(replacement);
        await Assert.That(replacement.IsTerminal).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == owned.TraceId)).IsEqualTo(1);
    }

    // ------------------------------------------------------------ rig

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock)
        CreateQuestRig(string name, string activity, Vector3 actorPos, Vector3 preyPos)
    {
        var (executor, actor, runtime, clock, _) =
            CreateQuestRigWithWildlife(name, activity, actorPos, preyPos, null);
        return (executor, actor, runtime, clock);
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock, HeadlessSession Session)
        CreateQuestRigWithWildlife(string name, string activity, Vector3 actorPos, Vector3 preyPos, Vector3? wildlifePos)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        actor.Character.Hp = actor.Character.MaxHp;

        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var preyObjId = session.SpawnNpc(Boar3475);
        var prey = session.World.GetNpc(preyObjId)!;
        prey.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        prey.Hp = 100;
        prey.MaxHp = 100;
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, preyPos);
        region.AddObject(prey);
        prey.Region = region;

        actor.Character.Quests.ActiveQuests[Quest251] = new Quest(actor.Character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
        actor.Character.CurrentTarget = prey;

        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();
        Npc? wildlife = null;
        if (wildlifePos is { } pos)
        {
            // Attackable wildlife the fallback COULD acquire (faction 115 is
            // the roam's own wildlife band) — placed at contact range so only
            // ownership can explain it staying unacquired.
            wildlife = new Npc
            {
                ObjId = 0x71001,
                Hp = 100,
                MaxHp = 100,
                Faction = new SystemFaction { Id = (FactionsEnum)115 }
            };
            wildlife.Transform.Local.SetPosition(pos);
        }
        var seen = wildlife;
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            RoamSpeed = 2f,
            EnableWildlifeHunt = true,
            EnableWildlifeButcher = true,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => activity,
            NearbyNpcProvider = (_, _) => seen is null ? [] : [seen],
            NearbyDoodadProvider = (_, _) => [],
            UnitResolver = (c, id) => seen is not null && id == seen.ObjId ? seen : c.ParentWorld?.GetUnit(id),
            DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
        };
        return (executor, actor, runtime, clock, session);
    }

    private static void SeedQuest251GatherAct()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
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

        QuestFixtureRow.InvalidateAll();
    }
}
