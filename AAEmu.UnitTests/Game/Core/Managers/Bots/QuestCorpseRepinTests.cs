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
/// Available fresh corpses can replace spent loot, and every real take remains
/// exactly once through <see cref="QuestDecisionScenario.Run"/>.
/// These are synthetic seeded headless engine rigs, not live gameplay evidence.
/// </summary>
[NotInParallel]
public class QuestCorpseRepinTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint ProbeItemTemplateId = 91_411;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static uint s_secondObjId = 0x78000;

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
    public async Task LootedCorpses_NeverReproposed_AcrossRepin()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g7d-repin-once");
        var boar1 = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(boar1);

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-once-1" });
        var npc1 = session.World.GetNpc(boar1)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc1, (ProbeItemTemplateId, 1));
        npc1.Hp = 0;
        var loot1 = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-once-2" });
        await Assert.That(loot1.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, boar1)).IsTrue();

        var boar2 = SpawnSecondBoar(session, new Vector3(2, 0, 0));
        actor.Character.CurrentTarget = session.World.GetNpc(boar2);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-once-3" });
        var npc2 = session.World.GetNpc(boar2)!;
        GameplayActorTestRig.SeedLootContainer(npc2, (ProbeItemTemplateId, 1));
        npc2.Hp = 0;
        var loot2 = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-once-4" });
        await Assert.That(loot2.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, boar2)).IsTrue();

        // Re-ticks: neither corpse re-dispatches — exactly one Loot each.
        var fifth = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-once-5" });
        var sixth = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-once-6" });
        await Assert.That(fifth.SelectedAction == ActorActionType.Loot).IsFalse();
        await Assert.That(sixth.SelectedAction == ActorActionType.Loot).IsFalse();
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Loot)).IsEqualTo(2);
        // The second take spent its pin (spent-corpse release): loot-once still
        // holds off both re-dispatches, but the LastCorpse record is gone.
        await Assert.That(QuestBehavior.TryGetCorpse(actor.ActorId, out var rec)).IsFalse();
    }

    /// <summary>
    /// NEGATIVE GUARD for the spent-corpse exclusion (kill25): only a corpse
    /// THIS actor terminally looted is skipped. An unlooted corpse nearer than
    /// a spent one is still the corpse this leg is owed — the record names it,
    /// its container is probed, and its take is proposed — so the exclusion
    /// steps past the spent corpse in the scan instead of ending the scan.
    /// </summary>
    [Test]
    public async Task UnlootedNearerCorpse_StillPreemptsTheSpentOne()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("g7d-unlootednearer");
        var spent = SpawnBoar(session, actor, new Vector3(0, 0, 0), new Vector3(8, 0, 0));
        Activate251(actor.Character);
        actor.Character.CurrentTarget = session.World.GetNpc(spent);

        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-unlooted-1" });
        var spentNpc = session.World.GetNpc(spent)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(spentNpc, (ProbeItemTemplateId, 1));
        spentNpc.Hp = 0;
        var take = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-unlooted-2" });
        await Assert.That(take.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(QuestBehavior.IsLootDispatched(actor.ActorId, spent)).IsTrue();

        // A dead, never-taken boar lands NEARER than the spent corpse.
        var fresh = SpawnSecondBoar(session, new Vector3(3, 0, 0));
        var freshNpc = session.World.GetNpc(fresh)!;
        GameplayActorTestRig.SeedLootContainer(freshNpc, (ProbeItemTemplateId, 1));
        freshNpc.Hp = 0;

        var next = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g7d-unlooted-3" });

        await Assert.That(QuestBehavior.TryGetCorpse(actor.ActorId, out var corpse)).IsTrue();
        await Assert.That(corpse!.ObjId).IsEqualTo(fresh);
        await Assert.That(next.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(actor.AuditTrace.Last(r => r.Action == ActorActionType.Loot).TargetId).IsEqualTo(fresh);
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

    private static uint SpawnSecondBoar(HeadlessSession session, Vector3 npcPos)
    {
        // HeadlessSession.SpawnNpc dedups by template — the second 3475 is
        // built directly so the funnel resolves it live beside the corpse.
        var objId = s_secondObjId++;
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
