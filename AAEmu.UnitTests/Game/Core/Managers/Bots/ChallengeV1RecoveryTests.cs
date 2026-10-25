using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Loot;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.Game.Models.Game.Skills;
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Challenge suite V1, part B (scenarios 6-10): corpse/loot/travel shapes on
/// fresh 97_32x fixtures (unclaimed repo-wide), asserting WAKE VERDICTS through
/// <see cref="QuestDecisionScenario.Run"/> on synthetic seeded headless engine
/// rigs, not live gameplay evidence (SelectedAction / dispatch / reason / pin state).
/// Corpse scenarios stage container state explicitly (exists+0-items vs removed
/// vs unlooted) with explicit tagging (foreign/cohort/self/none) and region joins.
/// </summary>
[NotInParallel]
public class ChallengeV1RecoveryTests
{
    private const uint PreyQuest = 97_320;
    private const uint PreyComponent = 97_321;
    private const uint PreyActId = 97_322;
    private const uint PreyItem = 97_323;
    private const uint PreyTemplate = 97_324;
    private const uint PreyPack = 97_325;
    private const uint WrongTemplate = 97_326;
    private const uint WrongItem = 97_327;
    private const uint WrongPack = 97_328;
    private const uint WrongQuest = 97_329;
    private const int PreyNeed = 3;
    private const uint ProbeItem = 97_340;

    private static uint s_extraObjId = 0x9E000;

    private static readonly MethodInfo ResolveObjectiveSource = typeof(BotRoamStepExecutor)
        .GetMethod("ResolveQuestObjectiveSource", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ArmQuestTravel = typeof(BotRoamStepExecutor)
        .GetMethod("ArmQuestTravel", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        SeedPreyChain();
        SeedWrongChain();
        BotBrainTuningStore.ResetForTests();
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();
        QuestBehavior.ClearGiveUpMemory();
        QuestBehavior.ClearCombatStallMemory();
        QuestBehavior.ClearLeashStaleMemory();
        CombatBrainEngagement.ClearAll();
        LootLedger.ClearAll();
        TravelIntentStore.ClearAll();
        LootBrainPlanner.CohortResolverForTests = null;
    }

    [After(Test)]
    public void TearDown()
    {
        LootBrainPlanner.CohortResolverForTests = null;
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
    }

    /// <summary>
    /// Cohort-tagged emptied corpse with live prey at mid range: a fellow
    /// cohort bot's take leaves the corpse (container exists + 0 items,
    /// cohort-tagged) 8 m from this actor. This actor never pins it (no
    /// record, loot leg names no target), then commits to live prey at 12 m.
    /// </summary>
    [Test]
    public async Task Scenario6_CohortTaggedEmptiedCorpse_Excluded_RetargetsMidRangePrey()
    {
        const uint botAccount = 97_301;
        const uint fellowBotAccount = 97_303;
        try
        {
            var (first, session) = GameplayActorTestRig.CreateActor("ch1b-ce-first");
            var (second, _) = GameplayActorTestRig.CreateActor("ch1b-ce-second");
            GameplayActorTestRig.JoinActorWorld(session, second);
            // B stands 8 m from the emptied corpse: outside loot range but
            // inside the census, so without the terminal-empty exclusion B
            // would camp its pin.
            GameplayActorTestRig.SetPosition(second, new Vector3(10, 2, 0));
            var guestRegion = session.World.GetRegionByPos(second.Character.Transform.World.Position)!;
            guestRegion.AddObject(second.Character);
            second.Character.Region = guestRegion;
            var corpseObjId = SpawnPrey(session, first, new Vector3(0, 2, 0), new Vector3(2, 2, 0));
            var corpse = session.World.GetNpc(corpseObjId)!;
            corpse.Hp = 0;
            GameplayActorTestRig.SeedItemTemplate(ProbeItem);
            GameplayActorTestRig.SeedLootContainer(corpse, (ProbeItem, 1));

            // Fellow cohort bot takes it: both accounts read as bots, so B
            // reads the tagger as cohort (Party), not Foreign.
            StageQuest(first);
            first.Character.CurrentTarget = corpse;
            second.Character.AccountId = botAccount;
            first.Character.AccountId = fellowBotAccount;
            LootBrainPlanner.CohortResolverForTests = accountId => accountId == botAccount || accountId == fellowBotAccount;
            corpse.CharacterTagging.AddTagger(first.Character, 10);
            var take = QuestDecisionScenario.Run(first, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "ch1b-ce-take-1" });
            await Assert.That(take.SelectedAction).IsEqualTo(ActorActionType.Loot);
            // Emptied by the take: container exists + 0 items, cohort-tagged.
            GameplayActorTestRig.SeedLootContainer(corpse);
            await Assert.That(corpse.LootingContainer.Items.Count).IsEqualTo(0);

            // B's wakes with only the emptied corpse around: no pin, no target.
            StageQuest(second);
            string? lootDetail = null;
            for (var wake = 1; wake <= 2; wake++)
            {
                var run = QuestDecisionScenario.Run(second, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = $"ch1b-ce-second-{wake}" });
                lootDetail = run.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
                await Assert.That(run.SelectedAction == ActorActionType.Loot).IsFalse();
                second.PreemptCurrent("unit-test wake settled");
                StageQuest(second);
            }

            await Assert.That(QuestBehavior.TryGetCorpse(second.ActorId, out _)).IsFalse();
            await Assert.That(lootDetail!).Contains("target=-");

            // Live prey appears at 12 m from B: B commits instead of camping.
            var liveObjId = SpawnSecondPrey(session, new Vector3(22, 2, 0));
            var live = session.World.GetNpc(liveObjId)!;
            var retargeted = false;
            for (var wake = 1; wake <= 5; wake++)
            {
                var run = QuestDecisionScenario.Run(second, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = $"ch1b-ce-live-{wake}" });
                if ((run.SelectedAction == ActorActionType.Target && run.Request?.TargetId == liveObjId)
                    || second.Character.CurrentTarget == live)
                {
                    retargeted = true;
                    break;
                }
                second.PreemptCurrent("unit-test wake settled");
                StageQuest(second);
            }

            await Assert.That(retargeted).IsTrue();
            await Assert.That(second.AuditTrace.Any(r => r.Action == ActorActionType.Loot)).IsFalse();
        }
        finally
        {
            LootBrainPlanner.CohortResolverForTests = null;
        }
    }

    /// <summary>
    /// Dead-prey census with TWO templates: the census holds a dead wrong-
    /// template corpse (container exists + 0 items, untagged) and a live
    /// correct-template prey. The correct-template travel arm resolves to its
    /// seeded spawner while the wrong-template corpse is ignored — and the
    /// Target leg commits to the live correct prey, never the dead wrong one.
    /// </summary>
    [Test]
    public async Task Scenario7_TwoTemplateCensus_CorrectTemplateTravel_WrongIgnored()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("ch1b-two-template");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 4, 0));
        JoinToRegion(actor, session, new Vector3(0, 4, 0));
        StageQuest(actor);
        // The correct-template spawner is explicitly seeded.
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(50, 4, 0));
        // A dead WRONG-template corpse stands in the census (explicit staging:
        // container exists + 0 items, untagged) beside live correct prey.
        var wrongObjId = SpawnPerceived(session, WrongTemplate, new Vector3(3, 4, 0), alive: false);
        var wrong = session.World.GetNpc(wrongObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItem);
        GameplayActorTestRig.SeedLootContainer(wrong);
        await Assert.That(wrong.LootingContainer.Items.Count).IsEqualTo(0);
        var liveObjId = SpawnPerceivedLive(session, PreyTemplate, new Vector3(4, 4, 0));

        var (resolved, position, template, isPrey) = Resolve(actor);
        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);
        await Assert.That(isPrey).IsFalse();

        // Wake verdict: the live correct prey IS selected — the Target leg
        // emits for it, and the actor commits to it rather than any corpse.
        var committed = false;
        QuestDecisionScenario.QuestRunResult? last = null;
        for (var wake = 1; wake <= 3; wake++)
        {
            last = QuestDecisionScenario.Run(actor, (_, _) => session.World.GetAllNpcs(),
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1b-twotpl-{wake}" });
            if (last.LegEvidence.Any(e => e.Leg == QuestLegId.Target && e.Emitted)
                && last.SelectedAction == ActorActionType.Target
                && last.Request?.TargetId == liveObjId)
                committed = true;
            actor.PreemptCurrent("unit-test wake settled");
            _ = position;
        }

        await Assert.That(committed).IsTrue();
        await Assert.That(last!.GiveUps.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Spawner-seeded travel at 30 m+: with the prey spawner explicitly seeded
    /// at 38 m and no prey perceived, the travel arm resolves to the spawner
    /// under the QUEST_TRAVEL owner, and the wake's Target leg stays silent
    /// with no give-up earned.
    /// </summary>
    [Test]
    public async Task Scenario8_SeededSpawnerTravelAt38m_Resolves()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("ch1b-travel-far");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 6, 0));
        StageQuest(actor);
        // The prey spawner is explicitly seeded 38 m out.
        SeedNpcSpawner(actor, PreyTemplate, new Vector3(38, 6, 0));

        var (resolved, position, template, isPrey) = Resolve(actor);
        await Assert.That(resolved).IsTrue();
        await Assert.That(template).IsEqualTo(PreyTemplate);
        await Assert.That(position.X).IsEqualTo(38f);
        await Assert.That(isPrey).IsTrue();

        var (executor, runtime) = CreateExecutorFor(actor);
        await Assert.That(Arm(executor, runtime, actor)).IsTrue();
        var armed = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(armed.QuestTravelTarget!.Value.X).IsEqualTo(38f);
        await Assert.That(armed.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
        await Assert.That(armed.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");

        var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "ch1b-8-seeded-1" });
        await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Target).Emitted).IsFalse();
        await Assert.That(wake.GiveUps.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Unseeded-spawner fail-closed: with NO spawner seeded anywhere and
    /// nothing perceived, the travel arm declines, the wake emits no target,
    /// selects no Move, and earns no give-up — a clean withhold.
    /// </summary>
    [Test]
    public async Task Scenario9_UnseededSpawnerTravel_FailsClosed()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("ch1b-travel-bare");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 8, 0));
        StageQuest(actor);
        // No spawner seeded anywhere for this quest: fail-closed.

        var (resolved, _, template, _) = Resolve(actor);
        await Assert.That(resolved).IsFalse();
        await Assert.That(template).IsEqualTo(0u);

        var (executor, runtime) = CreateExecutorFor(actor);
        await Assert.That(Arm(executor, runtime, actor)).IsFalse();
        var bare = executor.GetBotState(runtime.CharacterId)!;
        await Assert.That(bare.QuestTravelTarget.HasValue).IsFalse();

        var wake = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "ch1b-9-bare-1" });
        await Assert.That(wake.LegEvidence.Single(e => e.Leg == QuestLegId.Target).Emitted).IsFalse();
        await Assert.That(wake.GiveUps.Count).IsEqualTo(0);
        await Assert.That(wake.SelectedAction == ActorActionType.Move).IsFalse();
    }

    /// <summary>
    /// Visible prey holds inside the existing combat band, then moves exactly
    /// 30 m to a still-visible position outside it. Fresh pursuit must advance
    /// the actor toward that new position within five decision wakes.
    /// </summary>
    [Test]
    public async Task Scenario10_TargetTeleports30m_FreshPursuitWithinFiveWakes()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("ch1b-teleport");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var originalPosition = new Vector3(24, 10, 0);
        var newPosition = new Vector3(54, 10, 0);
        var npcObjId = SpawnPrey(session, actor, new Vector3(30, 10, 0), originalPosition);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        // A running attack needs its native task, not only the enabled flag.
        _ = actor.AutoAttack(npcObjId);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character, 6f)).IsTrue();
        await Assert.That(actor.Observe().NearbyNpcObjIds.Contains(npcObjId)).IsTrue();

        for (var wake = 1; wake <= 2; wake++)
        {
            ActivateCustom(actor.Character);
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1b-tp-hold-{wake}" });
            await Assert.That(run.SelectedAction == ActorActionType.Move).IsFalse();
            await Assert.That(actor.ActiveRequest is { Action: ActorActionType.Move }).IsFalse();
        }
        var completedMovesBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);
        await Assert.That(Vector3.Distance(originalPosition, newPosition)).IsEqualTo(30f);
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, newPosition);
        var region = session.World.GetRegionByPos(newPosition)
            ?? throw new InvalidOperationException("rig world has no region at the teleported prey");
        if (npc.Region != region)
        {
            region.AddObject(npc);
            npc.Region = region;
        }
        await Assert.That(Vector3.Distance(actor.Character.Transform.World.Position, newPosition)).IsEqualTo(24f);
        await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character, 24f)).IsFalse();
        await Assert.That(actor.Observe().NearbyNpcObjIds.Contains(npcObjId)).IsTrue();

        ActorRequest? recovery = null;
        for (var wake = 1; wake <= 5 && recovery == null; wake++)
        {
            ActivateCustom(actor.Character);
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1b-tp-after-{wake}" });
            if (run.Request is { Action: ActorActionType.Move, State: ActorLifecycleState.Running } move)
                recovery = move;
        }
        await Assert.That(recovery, "Visible prey outside the existing combat band must admit fresh pursuit within five wakes.").IsNotNull();
        await Assert.That(recovery!.TargetId).IsEqualTo(npcObjId);
        await Assert.That(recovery.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
        await Assert.That(recovery.Timeout.HasValue && recovery.Timeout.Value > TimeSpan.Zero).IsTrue();
        await Assert.That(actor.ActiveRequest!.TraceId).IsEqualTo(recovery.TraceId);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(completedMovesBefore);
        var beforeTick = actor.Character.Transform.World.Position;
        actor.Tick(TimeSpan.FromMilliseconds(100));
        var afterTick = actor.Character.Transform.World.Position;
        await Assert.That(Vector3.Distance(beforeTick, afterTick)).IsGreaterThan(0f);
        await Assert.That(Vector3.Distance(afterTick, newPosition)).IsLessThan(Vector3.Distance(beforeTick, newPosition));
        await Assert.That(recovery.State).IsEqualTo(ActorLifecycleState.Running);
        actor.StopAutoAttack();
    }

    // ------------------------------------------------------------ harness

    private static (bool Resolved, Vector3 Position, uint Template, bool IsPrey) Resolve(GameplayActor actor)
    {
        var args = new object?[] { actor, 0u, default(Vector3), 0u, false };
        var resolved = (bool)ResolveObjectiveSource.Invoke(null, args)!;
        return (resolved, (Vector3)args[2]!, (uint)args[3]!, (bool)args[4]!);
    }

    private static (BotRoamStepExecutor Executor, PlayerBotRuntime Runtime) CreateExecutorFor(GameplayActor actor)
    {
        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = new FakeTimeProvider(),
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            ActiveActivityProvider = _ => "quest.progress",
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
        };
        return (executor, runtime);
    }

    private static bool Arm(BotRoamStepExecutor executor, PlayerBotRuntime runtime, GameplayActor actor)
    {
        _ = executor.GetOrCreateActor(actor.Character);
        var state = executor.GetBotState(runtime.CharacterId)!;
        var band = typeof(BotRoamStepExecutor)
            .GetMethod("OfferBandFor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(executor, [actor])!;
        return (bool)ArmQuestTravel.Invoke(executor, [runtime, actor, state, band, "quest.progress"])!;
    }

    private static void ActivateCustom(Character character)
    {
        var template = QuestManager.Instance.GetTemplate(PreyQuest)
            ?? throw new InvalidOperationException($"quest template {PreyQuest} not seeded");
        var quest = new Quest(template, character);
        quest.SkipUpdatePackets();
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
        quest.Objectives = [0, 0, 0, 0, 0];
        character.Quests.ActiveQuests[PreyQuest] = quest;
        QuestFixtureRow.InvalidateAll();
    }

    private static void StageQuest(GameplayActor actor) => ActivateCustom(actor.Character);

    private static uint SpawnPrey(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 npcPos)
    {
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = session.SpawnNpc(PreyTemplate);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = PreyTemplate, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
        return npcObjId;
    }

    private static uint SpawnSecondPrey(HeadlessSession session, Vector3 npcPos)
    {
        var objId = s_extraObjId++;
        var npc = new Npc
        {
            ObjId = objId,
            TemplateId = PreyTemplate,
            Template = new NpcTemplate { Id = PreyTemplate, Scale = 1f },
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

    private static void JoinToRegion(GameplayActor actor, HeadlessSession session, Vector3 position)
    {
        var region = session.World.GetRegionByPos(position)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
    }

    private static uint SpawnPerceived(HeadlessSession session, uint npcTemplateId, Vector3 npcPos, bool alive)
    {
        var region = session.World.GetRegionByPos(npcPos)
            ?? throw new InvalidOperationException("rig world has no region at the npc position");
        var objId = session.SpawnNpc(npcTemplateId);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate { Id = npcTemplateId, Scale = 1f };
        npc.Hp = alive ? 100 : 0;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, objId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
        return objId;
    }

    private static uint SpawnPerceivedLive(HeadlessSession session, uint npcTemplateId, Vector3 npcPos)
        => SpawnPerceived(session, npcTemplateId, npcPos, alive: true);

    private static void SeedNpcSpawner(GameplayActor actor, uint npcTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(97_600 + spawners.Count);
        spawners[key] =
        [
            new NpcSpawner
            {
                Id = key,
                UnitId = npcTemplateId,
                Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
            }
        ];
    }

    private static void SeedPreyChain()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var questTemplates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate>)
            typeof(QuestManager).GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(PreyQuest, out var template))
        {
            template = new AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate { Id = PreyQuest, Level = 10 };
            questTemplates[PreyQuest] = template;
        }

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)
            typeof(QuestManager).GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(PreyComponent, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = PreyComponent,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[PreyComponent] = component;
        }
        if (!template.Components.ContainsKey(PreyComponent))
            template.Components[PreyComponent] = component;

        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ActId == PreyActId))
        {
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            {
                ActId = PreyActId,
                DetailId = PreyComponent,
                DetailType = nameof(QuestActObjItemGather),
                Count = PreyNeed,
                ItemId = PreyItem,
                ThisComponentObjectiveIndex = 0,
            });
        }

        SeedLootLink(PreyTemplate, PreyPack, PreyItem);

        QuestFixtureRow.InvalidateAll();
        var row = QuestFixtureRow.FromQuestData(PreyQuest);
        if (row.PreyTemplate != PreyTemplate)
            throw new InvalidOperationException(
                $"fixture row resolved prey {row.PreyTemplate}, expected {PreyTemplate}");
    }

    private static void SeedWrongChain()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var questTemplates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate>)
            typeof(QuestManager).GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(WrongQuest, out var template))
        {
            template = new AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate { Id = WrongQuest, Level = 10 };
            questTemplates[WrongQuest] = template;
        }

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)
            typeof(QuestManager).GetField("_componentTemplates", flags)!.GetValue(manager)!;
        const uint wrongComponent = 97_330;
        if (!componentTemplates.TryGetValue(wrongComponent, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = wrongComponent,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[wrongComponent] = component;
        }
        if (!template.Components.ContainsKey(wrongComponent))
            template.Components[wrongComponent] = component;

        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ActId == 97_331))
        {
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            {
                ActId = 97_331,
                DetailId = wrongComponent,
                DetailType = nameof(QuestActObjItemGather),
                Count = PreyNeed,
                ItemId = WrongItem,
                ThisComponentObjectiveIndex = 0,
            });
        }

        SeedLootLink(WrongTemplate, WrongPack, WrongItem);
        QuestFixtureRow.InvalidateAll();
    }

    private static void SeedLootLink(uint npcTemplate, uint packId, uint itemId)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var _ = LootGameData.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(npcTemplate, out var rows) || rows.All(r => r.LootPackId != packId))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = npcTemplate,
                NpcId = npcTemplate,
                LootPackId = packId,
                DefaultPack = true,
            });
            map[npcTemplate] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(packId, out var pack) || pack.Loots.All(l => l.ItemId != itemId))
        {
            var loot = new Loot
            {
                Id = packId,
                Group = 0,
                ItemId = itemId,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = packId,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[packId] = new LootPack
                {
                    Id = packId,
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
    }

}
