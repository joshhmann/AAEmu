using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using System.Numerics;
using System.Reflection;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// STALE-LOOP REDISPATCH (train-side): when the funnel selection switches (or the
/// stall trip condemns the loop), CombatEmit re-dispatches AutoAttack on the new
/// selection instead of withdrawing as already-live. Full decision-stack coverage
/// through <see cref="QuestDecisionScenario.Run"/> on real headless actors.
/// </summary>
[NotInParallel]
public class LoopRedispatchTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static uint s_extraObjId = 0x7B000;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedCombatSurface();
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
    }

    [Test]
    public async Task LoopPinnedOnA_SelectionSwitchesToB_RedispatchesOnB()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lr-switch");
        var aObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var bObjId = SpawnExtraBoar(session, new Vector3(2.5f, 0, 0));
        Activate251(actor.Character);
        var a = session.World.GetNpc(aObjId)!;
        var b = session.World.GetNpc(bObjId)!;

        // Pin the live loop on A through the real dispatch path.
        actor.Character.CurrentTarget = a;
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lr-pin-1" });
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lr-pin-2" });
        await Assert.That(actor.Character.IsAutoAttack).IsTrue();
        var loopTarget = actor.Character.AutoAttackTask?.Skill?.InitialTarget?.ObjId;
        await Assert.That(loopTarget).IsEqualTo(aObjId);

        // Switch funnel selection to B by moving A out of perception and B to contact:
        // A is beyond the 100 m quest scan; the funnel selects B; Target leg re-points CurrentTarget=B
        // while the loop's struck target still names A (stale loop). Allow up to 3 wakes.
        GameplayActorTestRig.SetNpcPosition(session, aObjId, new Vector3(101, 0, 0));
        GameplayActorTestRig.SetNpcPosition(session, bObjId, new Vector3(2, 0, 0));
        QuestDecisionScenario.QuestRunResult? last = null;
        var redispatched = false;
        for (var wake = 1; wake <= 3; wake++)
        {
            last = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"lr-switch-{wake}" });
            actor.PreemptCurrent("unit-test wake settled");
            Activate251(actor.Character);
            if (actor.Character.CurrentTarget == b
                && last.SelectedAction == ActorActionType.AutoAttack
                && actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack && r.TargetId == bObjId))
            {
                redispatched = true;
                break;
            }
        }

        await Assert.That(redispatched).IsTrue();
        await Assert.That(last!.LegEvidence.Single(e => e.Leg == QuestLegId.Combat).Detail).Contains("verb=AutoAttack");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    [Test]
    public async Task StallTripped_SameTarget_RedispatchesInsteadOfHold()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("lr-stall");
        var npcObjId = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;

        // Pin the live loop on the target through the real dispatch path.
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lr-stall-pin-1" });
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "lr-stall-pin-2" });
        await Assert.That(actor.Character.IsAutoAttack).IsTrue();

        // Damageless loop: accrue stall wakes to the trip inside the 3 m band
        // (combat-leg Hold path — pursuit hold-confirms away on the settled wake).
        QuestDecisionScenario.QuestRunResult? last = null;
        var redispatched = false;
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes + 2; wake++)
        {
            npc.Hp = 100;
            actor.Character.IsAutoAttack = true;
            last = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"lr-stall-{wake}" });
            actor.PreemptCurrent("unit-test wake settled");
            Activate251(actor.Character);
            if (last.SelectedAction == ActorActionType.AutoAttack
                && last.Request?.TargetId == npcObjId
                && actor.AuditTrace.Any(r => r.Action == ActorActionType.AutoAttack && r.TargetId == npcObjId))
            {
                redispatched = true;
                break;
            }
        }

        await Assert.That(redispatched).IsTrue();

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
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

    private static uint SpawnExtraBoar(HeadlessSession session, Vector3 npcPos)
    {
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

        QuestFixtureRow.InvalidateAll();
    }
}
