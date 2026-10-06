using System.Numerics;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Loot;
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
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Held-out generalization suite, part B: corpse/loot/deconfliction shapes on
/// fresh 98_xxx fixtures (unclaimed repo-wide), asserting wake verdicts through
/// <see cref="QuestDecisionScenario.Run"/> on the real headless actor.
/// </summary>
[NotInParallel]
public class HeldoutRecoveryTests
{
    private const uint PreyQuest = 98_010;
    private const uint PreyComponent = 98_011;
    private const uint PreyActId = 98_012;
    private const uint PreyItem = 98_013;
    private const uint PreyTemplate = 98_014;
    private const uint PreyPack = 98_015;
    private const int PreyNeed = 3;
    private const uint ProbeItem = 98_040;

    private static uint s_extraObjId = 0x9B000;

    private static readonly MethodInfo ResolveObjectiveSource = typeof(BotRoamStepExecutor)
        .GetMethod("ResolveQuestObjectiveSource", BindingFlags.NonPublic | BindingFlags.Static)!;

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

    [Test]
    public async Task Scenario6_SpentCorpse_ResolvesFresh()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("heldB-spent-fresh");
        var spentObjId = SpawnPrey(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        StageQuest(actor);
        actor.Character.CurrentTarget = session.World.GetNpc(spentObjId);

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "heldB-6-1" });
        var spent = session.World.GetNpc(spentObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItem);
        GameplayActorTestRig.SeedLootContainer(spent, (ProbeItem, 1));
        spent.Hp = 0;

        var take = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "heldB-6-2" });
        await Assert.That(take.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, spentObjId)).IsTrue();

        var freshObjId = SpawnExtraPrey(session, new Vector3(8, 0, 0));
        var fresh = session.World.GetNpc(freshObjId)!;
        GameplayActorTestRig.SeedLootContainer(fresh, (ProbeItem, 1));
        fresh.Hp = 0;

        var next = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "heldB-6-3" });

        await Assert.That(next.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(actor.AuditTrace.Last(r => r.Action == ActorActionType.Loot).TargetId).IsEqualTo(freshObjId);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot && r.TargetId == spentObjId)).IsEqualTo(1);
    }

    [Test]
    public async Task Scenario7_ContendedCorpseRace_SecondActorRetargetsLivePrey()
    {
        var (first, session) = GameplayActorTestRig.CreateActor("heldB-race-first");
        var (second, _) = GameplayActorTestRig.CreateActor("heldB-race-second");
        GameplayActorTestRig.SetPosition(second, new Vector3(0, 0, 0));
        var npcObjId = SpawnPrey(session, first, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var corpse = session.World.GetNpc(npcObjId)!;
        corpse.Hp = 0;
        GameplayActorTestRig.SeedItemTemplate(ProbeItem);
        GameplayActorTestRig.SeedLootContainer(corpse, (ProbeItem, 1));
        var liveObjId = SpawnExtraPrey(session, new Vector3(3, 0, 0));
        var live = session.World.GetNpc(liveObjId)!;

        // First actor loots the corpse empty.
        StageQuest(first);
        first.Character.CurrentTarget = corpse;
        _ = QuestDecisionScenario.Run(first, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "heldB-7-loot" });
        GameplayActorTestRig.SeedLootContainer(corpse);

        // Second actor arrives after the loot: must re-target live prey within 5 wakes.
        StageQuest(second);
        second.Character.CurrentTarget = corpse;
        var retargeted = false;
        for (var wake = 1; wake <= 5; wake++)
        {
            _ = QuestDecisionScenario.Run(second, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"heldB-7-second-{wake}" });
            if (second.Character.CurrentTarget == live)
            {
                retargeted = true;
                break;
            }
            StageQuest(second);
        }

        await Assert.That(retargeted).IsTrue();
    }


    [Test]
    public async Task Scenario8_DeadPreyTravel_NotSuppressed_LiveSuppresses()
    {
        var (deadActor, deadSession) = GameplayActorTestRig.CreateActor("heldB-prey-dead");
        GameplayActorTestRig.SetPosition(deadActor, new Vector3(0, 0, 0));
        JoinToRegion(deadActor, deadSession, new Vector3(0, 0, 0));
        StageQuest(deadActor);
        SeedNpcSpawner(deadActor, PreyTemplate, new Vector3(51, 0, 0));
        SpawnPerceived(deadSession, PreyTemplate, new Vector3(3, 0, 0), alive: false);

        var (resolved, position, template, isPrey) = Resolve(deadActor);

        await Assert.That(resolved).IsTrue();
        await Assert.That(template).IsEqualTo(PreyTemplate);
        await Assert.That(position.X).IsEqualTo(51f);
        await Assert.That(isPrey).IsTrue();

        var (liveActor, liveSession) = GameplayActorTestRig.CreateActor("heldB-prey-live");
        GameplayActorTestRig.SetPosition(liveActor, new Vector3(0, 0, 0));
        JoinToRegion(liveActor, liveSession, new Vector3(0, 0, 0));
        StageQuest(liveActor);
        SeedNpcSpawner(liveActor, PreyTemplate, new Vector3(51, 0, 0));
        SpawnPerceived(liveSession, PreyTemplate, new Vector3(3, 0, 0), alive: true);

        var (liveResolved, _, liveTemplate, _) = Resolve(liveActor);

        await Assert.That(liveResolved).IsFalse();
        await Assert.That(liveTemplate).IsEqualTo(0u);
    }

    [Test]
    public async Task Scenario9_CohortLoot_PartyTakes_StrangerWithholds()
    {
        const uint botAccount = 98_101;
        const uint strangerAccount = 98_102;
        try
        {
            LootBrainPlanner.CohortResolverForTests = accountId => accountId == botAccount;

            var (actor, session) = GameplayActorTestRig.CreateActor("heldB-cohort-party");
            var npcObjId = SpawnPrey(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
            StageQuest(actor);
            actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);
            _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "heldB-9-1" });

            var npc = session.World.GetNpc(npcObjId)!;
            GameplayActorTestRig.SeedItemTemplate(ProbeItem);
            GameplayActorTestRig.SeedLootContainer(npc, (ProbeItem, 1));
            npc.Hp = 0;

            var (mate, _) = GameplayActorTestRig.CreateActor("heldB-cohort-mate");
            actor.Character.AccountId = botAccount;
            mate.Character.AccountId = botAccount;
            npc.CharacterTagging.AddTagger(mate.Character, 10);

            var take = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "heldB-9-2" });
            await Assert.That(take.SelectedAction).IsEqualTo(ActorActionType.Loot);
            await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot)).IsEqualTo(1);

            var (outsider, outsiderSession) = GameplayActorTestRig.CreateActor("heldB-cohort-foreign");
            var foreignObjId = SpawnPrey(outsiderSession, outsider, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
            StageQuest(outsider);
            outsider.Character.CurrentTarget = outsiderSession.World.GetNpc(foreignObjId);
            _ = QuestDecisionScenario.Run(outsider, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "heldB-9-3" });

            var foreign = outsiderSession.World.GetNpc(foreignObjId)!;
            GameplayActorTestRig.SeedLootContainer(foreign, (ProbeItem, 1));
            foreign.Hp = 0;
            var (stranger, _) = GameplayActorTestRig.CreateActor("heldB-cohort-stranger");
            outsider.Character.AccountId = botAccount;
            stranger.Character.AccountId = strangerAccount;
            foreign.CharacterTagging.AddTagger(stranger.Character, 10);

            var withheld = QuestDecisionScenario.Run(outsider, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = "heldB-9-4" });
            await Assert.That(withheld.SelectedAction == ActorActionType.Loot).IsFalse();
            await Assert.That(outsider.AuditTrace.Any(r => r.Action == ActorActionType.Loot)).IsFalse();
        }
        finally
        {
            LootBrainPlanner.CohortResolverForTests = null;
        }
    }

    [Test]
    public async Task Scenario10_TravelArm_PreyAt26m_ResolvesToPreySpawner()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("heldB-travel26");
        var npcObjId = SpawnPrey(session, actor, new Vector3(0, 0, 0), new Vector3(26, 0, 0));
        StageQuest(actor);
        actor.Character.CurrentTarget = session.World.GetNpc(npcObjId);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "heldB-10-travel26" });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.Request!.Destination.HasValue).IsTrue();
    }

    // ------------------------------------------------------------ harness

    private static (bool Resolved, Vector3 Position, uint Template, bool IsPrey) Resolve(GameplayActor actor)
    {
        var args = new object?[] { actor, default(Vector3), 0u, false };
        var resolved = (bool)ResolveObjectiveSource.Invoke(null, args)!;
        return (resolved, (Vector3)args[1]!, (uint)args[2]!, (bool)args[3]!);
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

    private static void JoinToRegion(GameplayActor actor, HeadlessSession session, Vector3 position)
    {
        var region = session.World.GetRegionByPos(position)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
    }

    private static void SpawnPerceived(HeadlessSession session, uint npcTemplateId, Vector3 npcPos, bool alive)
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
