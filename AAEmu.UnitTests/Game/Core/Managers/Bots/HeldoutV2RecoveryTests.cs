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
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using Microsoft.Extensions.Time.Testing;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Held-out generalization suite V2, part B (scenarios 6-10): corpse/loot/
/// give-up/travel shapes on fresh 98_2xx fixtures (unclaimed repo-wide),
/// asserting WAKE VERDICTS through <see cref="QuestDecisionScenario.Run"/> on
/// the real headless actor (SelectedAction / dispatch / reason / pin state),
/// never world state. Every corpse scenario stages container state explicitly
/// (exists+0-items vs removed vs unlooted) with explicit tagging
/// (foreign/cohort/self/none) + region join; geometry, tagging, and cadence
/// vary across the family. Every travel scenario explicitly seeds or withholds
/// its spawner and asserts accordingly.
/// </summary>
[NotInParallel]
public class HeldoutV2RecoveryTests
{
    private const uint PreyQuest = 98_210;
    private const uint PreyComponent = 98_211;
    private const uint PreyActId = 98_212;
    private const uint PreyItem = 98_213;
    private const uint PreyTemplate = 98_214;
    private const uint PreyPack = 98_215;
    private const int PreyNeed = 3;
    private const uint ProbeItem = 98_240;

    private static uint s_extraObjId = 0x9C000;

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
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();
        QuestBehavior.ClearGiveUpMemory();
        LootLedger.ClearAll();
        LootBrainPlanner.CohortResolverForTests = null;
    }

    /// <summary>
    /// Spent-nearer excluded, fresh-farther wins: the actor's own take drains
    /// the near corpse (container exists + 0 items, self-tagged), a fresh
    /// corpse lands at 18 m with a seeded non-empty container, and the next
    /// wake takes the fresh one — never re-dispatching the spent take.
    /// </summary>
    [Test]
    public async Task Scenario6_SpentNearerExcluded_FreshFartherWins()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hv2b-spent-fresh");
        var spentObjId = SpawnPrey(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        StageQuest(actor);
        actor.Character.CurrentTarget = session.World.GetNpc(spentObjId);

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-6-1" });
        var spent = session.World.GetNpc(spentObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItem);
        GameplayActorTestRig.SeedLootContainer(spent, (ProbeItem, 1));
        spent.Hp = 0;

        var take = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-6-2" });
        await Assert.That(take.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(take.Request?.TargetId).IsEqualTo(spentObjId);
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, spentObjId)).IsTrue();
        // The real Loot verb drained it: container exists + 0 items, self-tagged take.
        await Assert.That(spent.LootingContainer.Items.Count).IsEqualTo(0);

        // A fresh corpse lands FARTHER out (18 m) than the spent one (2 m), so
        // nearest-first alone would keep handing back the corpse already taken.
        var freshObjId = SpawnExtraPrey(session, new Vector3(18, 0, 0));
        var fresh = session.World.GetNpc(freshObjId)!;
        GameplayActorTestRig.SeedLootContainer(fresh, (ProbeItem, 1));
        fresh.Hp = 0;

        var next = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-6-3" });

        await Assert.That(next.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(next.Request?.TargetId).IsEqualTo(freshObjId);
        await Assert.That(actor.AuditTrace.Last(r => r.Action == ActorActionType.Loot).TargetId).IsEqualTo(freshObjId);
        // The spent corpse is never taken a second time.
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot && r.TargetId == spentObjId)).IsEqualTo(1);
    }

    /// <summary>
    /// Cohort-tagged emptied corpse excluded: a fellow cohort bot's take leaves
    /// the corpse (container exists + 0 items, cohort-tagged) 8 m from this
    /// actor. This actor never pins it (no record, loot leg names no target),
    /// then closes on live prey at 18 m when it appears.
    /// </summary>
    [Test]
    public async Task Scenario7_CohortTaggedEmptiedCorpse_Excluded_RetargetsLivePrey()
    {
        const uint botAccount = 98_201;
        const uint fellowBotAccount = 98_203;
        try
        {
            var (first, session) = GameplayActorTestRig.CreateActor("hv2b-ce-first");
            var (second, _) = GameplayActorTestRig.CreateActor("hv2b-ce-second");
            GameplayActorTestRig.JoinActorWorld(session, second);
            // B stands 8 m from the corpse: outside loot range but inside the
            // census. The corpse is B's nearest dead fixture prey, so without
            // the terminal-empty exclusion B would camp its pin.
            GameplayActorTestRig.SetPosition(second, new Vector3(10, 0, 0));
            var guestRegion = session.World.GetRegionByPos(second.Character.Transform.World.Position)!;
            guestRegion.AddObject(second.Character);
            second.Character.Region = guestRegion;
            var corpseObjId = SpawnPrey(session, first, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
            var corpse = session.World.GetNpc(corpseObjId)!;
            corpse.Hp = 0;
            GameplayActorTestRig.SeedItemTemplate(ProbeItem);
            GameplayActorTestRig.SeedLootContainer(corpse, (ProbeItem, 1));

            // Fellow cohort bot takes it: the cohort seam names both accounts
            // bots, so B reads the tagger as cohort (Party), not Foreign.
            StageQuest(first);
            first.Character.CurrentTarget = corpse;
            second.Character.AccountId = botAccount;
            first.Character.AccountId = fellowBotAccount;
            LootBrainPlanner.CohortResolverForTests = accountId => accountId == botAccount || accountId == fellowBotAccount;
            corpse.CharacterTagging.AddTagger(first.Character, 10);
            var take = QuestDecisionScenario.Run(first, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-ce-take-1" });
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
                    new QuestDecisionScenario.QuestOptions { CycleId = $"hv2b-ce-second-{wake}" });
                lootDetail = run.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
                second.PreemptCurrent("unit-test wake settled");
                StageQuest(second);
            }

            await Assert.That(QuestBehavior.TryGetCorpse(second.ActorId, out _)).IsFalse();
            await Assert.That(lootDetail!).Contains("target=-");

            // Live prey appears at 18 m from B: B resolves it instead of
            // camping the emptied corpse.
            var liveObjId = SpawnSecondPrey(session, new Vector3(28, 0, 0));
            var live = session.World.GetNpc(liveObjId)!;
            var retargeted = false;
            for (var wake = 1; wake <= 5; wake++)
            {
                _ = QuestDecisionScenario.Run(second, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = $"hv2b-ce-live-{wake}" });
                if (second.Character.CurrentTarget == live)
                {
                    retargeted = true;
                    break;
                }
                second.PreemptCurrent("unit-test wake settled");
                StageQuest(second);
            }

            await Assert.That(retargeted).IsTrue();
        }
        finally
        {
            LootBrainPlanner.CohortResolverForTests = null;
        }
    }

    /// <summary>
    /// Dead-prey travel not suppressed (paired): with the spawner explicitly
    /// seeded, a dead prey-template corpse (container exists + 0 items,
    /// untagged) in the census still resolves travel to the spawner, and the
    /// wake's Target leg emits nothing for it — while a live prey of the same
    /// template suppresses travel and the Target leg emits for it.
    /// </summary>
    [Test]
    public async Task Scenario8_DeadPreyTravel_NotSuppressed_LiveSuppresses()
    {
        var (deadActor, deadSession) = GameplayActorTestRig.CreateActor("hv2b-prey-dead");
        GameplayActorTestRig.SetPosition(deadActor, new Vector3(0, 0, 0));
        JoinToRegion(deadActor, deadSession, new Vector3(0, 0, 0));
        StageQuest(deadActor);
        SeedNpcSpawner(deadActor, PreyTemplate, new Vector3(60, 0, 0));
        var deadObjId = SpawnPerceived(deadSession, PreyTemplate, new Vector3(3, 0, 0), alive: false);
        var deadNpc = deadSession.World.GetNpc(deadObjId)!;
        await Assert.That(deadNpc.LootingContainer.Items.Count).IsEqualTo(0);

        var (resolved, position, template, isPrey) = Resolve(deadActor);
        await Assert.That(resolved).IsTrue();
        await Assert.That(template).IsEqualTo(PreyTemplate);
        await Assert.That(position.X).IsEqualTo(60f);
        await Assert.That(isPrey).IsTrue();

        // Wake verdict: the dead prey is rejected by the funnel — the Target
        // leg emits nothing for it, and no give-up is earned on the first wake.
        var deadWake = QuestDecisionScenario.Run(deadActor,
            (_, _) => deadSession.World.GetAllNpcs(),
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-8-dead-1" });
        await Assert.That(deadWake.LegEvidence.Single(e => e.Leg == QuestLegId.Target).Emitted).IsFalse();
        await Assert.That(deadWake.GiveUps.Count).IsEqualTo(0);

        var (liveActor, liveSession) = GameplayActorTestRig.CreateActor("hv2b-prey-live");
        GameplayActorTestRig.SetPosition(liveActor, new Vector3(0, 0, 0));
        JoinToRegion(liveActor, liveSession, new Vector3(0, 0, 0));
        StageQuest(liveActor);
        SeedNpcSpawner(liveActor, PreyTemplate, new Vector3(60, 0, 0));
        SpawnPerceived(liveSession, PreyTemplate, new Vector3(3, 0, 0), alive: true);

        var (liveResolved, _, liveTemplate, _) = Resolve(liveActor);
        await Assert.That(liveResolved).IsFalse();
        await Assert.That(liveTemplate).IsEqualTo(0u);

        // Wake verdict: the live prey IS selected — the Target leg emits.
        var liveWake = QuestDecisionScenario.Run(liveActor,
            (_, _) => liveSession.World.GetAllNpcs(),
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-8-live-1" });
        await Assert.That(liveWake.LegEvidence.Single(e => e.Leg == QuestLegId.Target).Emitted).IsTrue();
        await Assert.That(liveWake.GiveUps.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Give-up withdraws at 10, holds at 9: with the spawner deliberately
    /// WITHHELD (never seeded) and nothing perceived, the first 9 wakes earn
    /// no verdict and the advance leg keeps entering; the 10th wake decides
    /// the named give-up; the 11th withdraws the advance behind
    /// give-up-threshold and selects no work.
    /// </summary>
    [Test]
    public async Task Scenario9_GiveUp_WithdrawsAtTen_HoldsAtNine()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hv2b-giveup");
        session.Character.Level = 3;
        session.Character.Hp = session.Character.MaxHp;
        StageQuest(actor);
        // No spawner is seeded for this quest and nothing is perceived: the
        // objective slice resolves no target on every wake.
        var opts = new QuestDecisionScenario.QuestOptions { GiveUpAfterUnresolvedWakes = 10 };

        for (var wake = 1; wake <= 9; wake++)
        {
            var early = QuestDecisionScenario.Run(actor, (_, _) => [],
                opts with { CycleId = $"hv2b-giveup-{wake}" });
            await Assert.That(early.GiveUps.Count).IsEqualTo(0);
            await Assert.That(early.LegEvidence.Single(e => e.Leg == QuestLegId.Advance).Entered).IsTrue();
        }
        var advancesBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest);
        await Assert.That(advancesBefore).IsGreaterThan(0);

        var given = QuestDecisionScenario.Run(actor, (_, _) => [],
            opts with { CycleId = "hv2b-giveup-10" });
        await Assert.That(given.GiveUps.Count).IsEqualTo(1);
        var giveUp = given.GiveUps[0];
        await Assert.That(giveUp.QuestId).IsEqualTo(PreyQuest);
        await Assert.That(giveUp.Wakes).IsEqualTo(10);
        await Assert.That(giveUp.Threshold).IsEqualTo(10);
        await Assert.That(giveUp.Reason).StartsWith($"GIVE-UP/UNRESOLVED-TARGET quest={PreyQuest} stage=SELECT");
        await Assert.That(QuestBehavior.TryGetGiveUpStreak(actor.ActorId, PreyQuest, out var streak)).IsTrue();
        await Assert.That(streak!.Wakes).IsEqualTo(10);

        var advancesAtVerdict = actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest);
        var suppressed = QuestDecisionScenario.Run(actor, (_, _) => [],
            opts with { CycleId = "hv2b-giveup-11" });
        var advance = suppressed.LegEvidence.Single(e => e.Leg == QuestLegId.Advance);
        await Assert.That(advance.Entered).IsFalse();
        await Assert.That(advance.Detail).IsEqualTo("give-up-threshold (funnel unresolved 10 wakes)");
        await Assert.That(suppressed.WorkSelected).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.AdvanceQuest))
            .IsEqualTo(advancesAtVerdict);
    }

    /// <summary>
    /// Seeded-spawner travel resolves (paired): with the prey spawner
    /// explicitly seeded, the travel arm resolves to it under the QUEST_TRAVEL
    /// move owner; with the spawner WITHHELD (never seeded), the arm fails
    /// closed. Both wakes earn no give-up and emit no target — the verdicts
    /// that keep the pair honest.
    /// </summary>
    [Test]
    public async Task Scenario10_SeededSpawnerTravelResolves_UnseededFailsClosed()
    {
        var (seededActor, _) = GameplayActorTestRig.CreateActor("hv2b-travel-seeded");
        GameplayActorTestRig.SetPosition(seededActor, new Vector3(0, 0, 0));
        StageQuest(seededActor);
        SeedNpcSpawner(seededActor, PreyTemplate, new Vector3(45, 0, 0));

        var (seededExecutor, seededRuntime) = CreateExecutorFor(seededActor);
        await Assert.That(Arm(seededExecutor, seededRuntime, seededActor)).IsTrue();
        var armed = seededExecutor.GetBotState(seededRuntime.CharacterId)!;
        await Assert.That(armed.QuestTravelTarget!.Value.X).IsEqualTo(45f);
        await Assert.That(armed.QuestTravelReason.StartsWith("walking to quest objective source")).IsTrue();
        await Assert.That(armed.PendingMoveOwner).IsEqualTo("QUEST_TRAVEL");

        var seededWake = QuestDecisionScenario.Run(seededActor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-10-seeded-1" });
        await Assert.That(seededWake.LegEvidence.Single(e => e.Leg == QuestLegId.Target).Emitted).IsFalse();
        await Assert.That(seededWake.GiveUps.Count).IsEqualTo(0);

        var (bareActor, _) = GameplayActorTestRig.CreateActor("hv2b-travel-bare");
        GameplayActorTestRig.SetPosition(bareActor, new Vector3(0, 0, 0));
        StageQuest(bareActor);
        // No spawner seeded anywhere for this quest: fail-closed.

        var (bareExecutor, bareRuntime) = CreateExecutorFor(bareActor);
        await Assert.That(Arm(bareExecutor, bareRuntime, bareActor)).IsFalse();
        var bare = bareExecutor.GetBotState(bareRuntime.CharacterId)!;
        await Assert.That(bare.QuestTravelTarget.HasValue).IsFalse();

        var bareWake = QuestDecisionScenario.Run(bareActor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2b-10-bare-1" });
        await Assert.That(bareWake.LegEvidence.Single(e => e.Leg == QuestLegId.Target).Emitted).IsFalse();
        await Assert.That(bareWake.GiveUps.Count).IsEqualTo(0);
        await Assert.That(bareWake.SelectedAction == ActorActionType.Move).IsFalse();
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

    private static void StageQuest(GameplayActor actor)
    {
        var template = QuestManager.Instance.GetTemplate(PreyQuest)
            ?? throw new InvalidOperationException($"quest template {PreyQuest} not seeded");
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
        quest.Objectives = [0, 0, 0, 0, 0];
        actor.Character.Quests.ActiveQuests[PreyQuest] = quest;
        QuestFixtureRow.InvalidateAll();
    }

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

    private static uint SpawnExtraPrey(HeadlessSession session, Vector3 npcPos)
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

    private static uint SpawnSecondPrey(HeadlessSession session, Vector3 npcPos)
        => SpawnExtraPrey(session, npcPos);

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

    private static void SeedNpcSpawner(GameplayActor actor, uint npcTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(98_600 + spawners.Count);
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

        var _ = LootGameData.Instance;
        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(PreyTemplate, out var rows) || rows.All(r => r.LootPackId != PreyPack))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = PreyTemplate,
                NpcId = PreyTemplate,
                LootPackId = PreyPack,
                DefaultPack = true,
            });
            map[PreyTemplate] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(PreyPack, out var pack) || pack.Loots.All(l => l.ItemId != PreyItem))
        {
            var loot = new Loot
            {
                Id = PreyPack,
                Group = 0,
                ItemId = PreyItem,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = PreyPack,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[PreyPack] = new LootPack
                {
                    Id = PreyPack,
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
        var row = QuestFixtureRow.FromQuestData(PreyQuest);
        if (row.PreyTemplate != PreyTemplate)
            throw new InvalidOperationException(
                $"fixture row resolved prey {row.PreyTemplate}, expected {PreyTemplate}");
    }
}
