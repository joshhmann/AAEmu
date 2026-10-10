using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
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
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Read-only loot-container probes and terminal empty-corpse exclusion.
/// Decision-stack coverage through <see cref="QuestDecisionScenario.Run"/>
/// uses synthetic seeded headless engine rigs, not live gameplay evidence.
/// </summary>
[NotInParallel]
public class QuestCorpseObserveTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint OtherTemplate9001 = 9001;
    private const uint ProbeItemTemplateId = 91_411;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static uint s_extraObjId = 0x77000;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedCombatSurface();
        QuestBehavior.ClearCorpseMemory();
    }

    // ------------------------------------------------------------ recognition

    [Test]
    public async Task EmptyAndIrrelevantCorpses_DoNotBlockFreshPreyOrDispatchLoot()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g7b-empty-to-fresh");
        var emptyId = SpawnBoar(session, actor, Vector3.Zero, new Vector3(2, 0, 0));
        var empty = session.World.GetNpc(emptyId)!;
        empty.Hp = 0;
        var irrelevantId = SpawnExtraNpc(session, OtherTemplate9001, new Vector3(2.5f, 0, 0));
        session.World.GetNpc(irrelevantId)!.Hp = 0;
        Activate251(actor.Character);
        actor.Character.CurrentTarget = empty;
        var bagBefore = GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId);

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7b-empty" });
        await Assert.That(QuestBehavior.TryGetCorpse(actor.ActorId, out _)).IsFalse();
        await Assert.That(empty.LootingContainer.Items.Count).IsEqualTo(0);

        var freshId = SpawnExtraNpc(session, Boar3475, new Vector3(3, 0, 0));
        var next = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7b-fresh" });
        await Assert.That(next.SelectedAction).IsEqualTo(ActorActionType.Target);
        await Assert.That(next.Request!.TargetId).IsEqualTo(freshId);
        await Assert.That(next.Request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.CurrentTarget).IsEqualTo(session.World.GetNpc(freshId));
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Loot)).IsFalse();
        await Assert.That(GameplayActorTestRig.BagCount(actor, ProbeItemTemplateId)).IsEqualTo(bagBefore);
    }

    // ------------------------------------------------------------ probe

    [Test]
    public async Task ContainerProbe_NonEmpty_Lootable()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g7b-probe-full");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;

        var probe = QuestBehavior.ProbeCorpse(actor.Character, npc);

        await Assert.That(probe.ContainerExists).IsTrue();
        await Assert.That(probe.ItemCount).IsEqualTo(1);
        await Assert.That(probe.Lootable).IsTrue();
    }

    [Test]
    public async Task ContainerProbe_Empty_NotLootable()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g7b-probe-empty");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Hp = 0;

        var probe = QuestBehavior.ProbeCorpse(actor.Character, npc);

        await Assert.That(probe.ContainerExists).IsTrue();
        await Assert.That(probe.ItemCount).IsEqualTo(0);
        await Assert.That(probe.Lootable).IsFalse();
    }


    // ------------------------------------------------------------ fixture

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

    private static uint SpawnExtraNpc(HeadlessSession session, uint templateId, Vector3 npcPos)
    {
        // HeadlessSession.SpawnNpc dedups by template — a non-3475 decoy is
        // built directly so the funnel resolves it live.
        var objId = s_extraObjId++;
        var npc = new Npc
        {
            ObjId = objId,
            TemplateId = templateId,
            Template = new NpcTemplate { Id = templateId, Scale = 1f },
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
    /// resolution). Canonical pilot data is never clobbered.
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
