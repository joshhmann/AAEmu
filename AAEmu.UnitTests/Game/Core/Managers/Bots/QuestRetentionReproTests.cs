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
/// Phase-1 ONLY reproduction (no production edits): does CurrentTarget stick
/// on a terminally-empty corpse after the child-2 exclusion drops the pin?
/// Geometry: emptied corpse (seeded container, 0 items) at 2m, live prey of
/// the same template at 8m. Actor B starts with CurrentTarget=corpse; 5 wakes.
/// Outcome classes: (i) pin gone + retargeted (retention hypothesis DEAD);
/// (ii) pin gone + CurrentTarget still corpse (retention CONFIRMED);
/// (iii) pin persists (child-2 exclusion not engaging here).
/// </summary>
[NotInParallel]
public class QuestRetentionReproTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static uint s_directObjId = 0x7B000;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedCombatSurface();
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();
    }

    [Test]
    public async Task EmptyCorpseAt2m_LivePreyAt8m_PinAndTargetOutcome()
    {
        var (actorA, session) = GameplayActorTestRig.CreateActor("ret-host-a");
        var (actorB, _) = GameplayActorTestRig.CreateActor("ret-subject-b");
        GameplayActorTestRig.JoinActorWorld(session, actorB);

        var origin = new Vector3(0, 0, 0);
        GameplayActorTestRig.SetPosition(actorA, new Vector3(0, 5, 0));
        GameplayActorTestRig.SetPosition(actorB, origin);
        var region = session.World.GetRegionByPos(origin)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actorA.Character);
        actorA.Character.Region = region;
        region.AddObject(actorB.Character);
        actorB.Character.Region = region;

        // Corpse at 2m: seeded container EXISTS with 0 items (child-2 terminal-empty shape).
        var corpseObjId = session.SpawnNpc(Boar3475);
        var corpse = session.World.GetNpc(corpseObjId)!;
        corpse.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        corpse.Hp = 0;
        corpse.MaxHp = 100;
        corpse.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, corpseObjId, new Vector3(2, 0, 0));
        region.AddObject(corpse);
        corpse.Region = region;
        GameplayActorTestRig.SeedLootContainer(corpse);

        // Live prey, same template, at 8m (direct construction: SpawnNpc dedups by template).
        var liveObjId = SpawnDirectNpc(session, region, new Vector3(8, 0, 0), alive: true);

        Activate251(actorB.Character);
        actorB.Character.CurrentTarget = corpse;

        for (var i = 1; i <= 5; i++)
        {
            _ = QuestDecisionScenario.Run(actorB, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ret-repro-{i}" });
        }

        var pinGone = !QuestBehavior.TryGetCorpse(actorB.ActorId, out var pin);
        var targetObjId = actorB.Character.CurrentTarget?.ObjId ?? 0;
        var retargeted = targetObjId == liveObjId;

        var verdict = pinGone && retargeted ? "(i) pin gone + retargeted — retention hypothesis DEAD"
            : pinGone ? "(ii) pin gone + CurrentTarget still on corpse — retention CONFIRMED"
            : "(iii) pin persists — child-2 exclusion not engaging in this geometry";
        Console.WriteLine($"RETRO-PROBE pinGone={pinGone} pinObjId={(pin?.ObjId ?? 0)} "
            + $"currentTarget={targetObjId} corpse={corpseObjId} live={liveObjId} => {verdict}");

        await Assert.That(pinGone).IsTrue();
        await Assert.That(retargeted).IsTrue();
    }
    private static uint SpawnDirectNpc(HeadlessSession session, dynamic region, Vector3 pos, bool alive)
    {
        var objId = s_directObjId++;
        var npc = new Npc
        {
            ObjId = objId,
            TemplateId = Boar3475,
            Template = new NpcTemplate { Id = Boar3475, Scale = 1f },
            Hp = alive ? 100 : 0,
            MaxHp = 100,
            IsVisible = true,
        };
        npc.Transform.Local.SetPosition(pos);
        session.World.AddObject(npc);
        var npcRegion = session.World.GetRegionByPos(pos) ?? region;
        npcRegion.AddObject(npc);
        npc.Region = npcRegion;
        return objId;
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
