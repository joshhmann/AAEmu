using System.Numerics;
using System.Reflection;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Loot;
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
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// FOREIGN-EMPTIED CORPSE (train-side flip): a corpse actor A terminally looted
/// (container exists, 0 items, engine tagging names A) must never pin actor B's
/// census or B's loot leg — B resolves the nearby live prey instead of camping
/// a corpse whose loot rights are foreign to it. Full decision-stack coverage
/// through <see cref="QuestDecisionScenario.Run"/> on real headless actors.
/// </summary>
[NotInParallel]
public class ForeignEmptiedCorpseTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint ProbeItemTemplateId = 91_411;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private static uint s_secondObjId = 0x7A000;

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
    public async Task SecondActor_IgnoresForeignEmptiedCorpse_RetargetsLivePrey()
    {
        var (first, session) = GameplayActorTestRig.CreateActor("fe-first");
        var (second, _) = GameplayActorTestRig.CreateActor("fe-second");
        GameplayActorTestRig.JoinActorWorld(session, second);
        // Actor B stands 6m from the corpse: outside loot range (no take, only
        // the corpse-approach walk) but inside the 25m census. The corpse is
        // B's nearest dead fixture prey, so without the terminal-empty-foreign
        // exclusion B's loot leg pins it and walks the corpse every wake while
        // the live prey (10m, same bearing) starves; with the exclusion the
        // corpse is never a candidate and B closes on live prey instead.
        GameplayActorTestRig.SetPosition(second, new Vector3(8, 0, 0));
        // Region-graph join: JoinActorWorld moves ParentWorld, but Observe's
        // GetAround reads the region graph — without this the guest perceives
        // nothing.
        var guestRegion = session.World.GetRegionByPos(second.Character.Transform.World.Position)!;
        guestRegion.AddObject(second.Character);
        second.Character.Region = guestRegion;
        var corpseObjId = SpawnBoar(session, first, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var corpse = session.World.GetNpc(corpseObjId)!;
        corpse.Hp = 0;
        GameplayActorTestRig.SeedLootContainer(corpse, (ProbeItemTemplateId, 1));

        // First actor loots the corpse empty. Foreign provenance: A tags the
        // kill while B is a stranger to the claim. The cohort seam names only
        // B's account a bot, so the tagger-shares-team fallback cannot fire
        // and B reads Foreign — the terminal-empty shape the exclusion gates
        // on. The seam is cleared before B's wakes (production default).
        Activate251(first.Character);
        first.Character.CurrentTarget = corpse;
        const uint botAccount = 91_501;
        const uint strangerAccount = 91_502;
        second.Character.AccountId = botAccount;
        first.Character.AccountId = strangerAccount;
        LootBrainPlanner.CohortResolverForTests = accountId => accountId == botAccount;
        corpse.CharacterTagging.AddTagger(first.Character, 10);
        _ = QuestDecisionScenario.Run(first, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "fe-take-1" });
        GameplayActorTestRig.SeedLootContainer(corpse);
        LootBrainPlanner.CohortResolverForTests = null;

        // Phase A — pin/camp (no live prey): B's funnel selects nothing (the
        // only fixture prey is dead), so the pursuit withdraw names the corpse
        // through the census. WITHOUT the terminal-empty-foreign exclusion the
        // census re-pins the foreign-emptied corpse into B's LastCorpse slot
        // every wake (camped); WITH it the corpse is never a candidate and no
        // record is written. The loot leg reads the same slot: without the
        // fix its Detail keeps naming the corpse target, with the fix it
        // reports target=- (LootEmit pin-release drops the spent pin).
        Activate251(second.Character);
        string? lootDetail = null;
        for (var wake = 1; wake <= 2; wake++)
        {
            var run = QuestDecisionScenario.Run(second, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"fe-second-{wake}" });
            lootDetail = run.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
            second.PreemptCurrent("unit-test wake settled");
            Activate251(second.Character);
        }

        var pinned = QuestBehavior.TryGetCorpse(second.ActorId, out var corpseRec);
        LootBrainPlanner.CohortResolverForTests = null;
        // Intermediate: without the fix B camps the corpse pin (FAIL), with
        // the fix the slot stays empty (PASS) ...
        await Assert.That(pinned).IsFalse();
        // ... and the loot leg names no corpse target (FAIL names the corpse).
        await Assert.That(lootDetail!).Contains("target=-");

        // Phase B — live prey appears: B must resolve it. True in both
        // configurations (the fix never touches live selection); documents
        // that the exclusion only steps past the spent corpse.
        var liveObjId = SpawnSecondBoar(session, new Vector3(18, 0, 0));
        var live = session.World.GetNpc(liveObjId)!;
        var retargeted = false;
        for (var wake = 1; wake <= 5; wake++)
        {
            _ = QuestDecisionScenario.Run(second, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"fe-live-{wake}" });
            if (second.Character.CurrentTarget == live)
            {
                retargeted = true;
                break;
            }
            second.PreemptCurrent("unit-test wake settled");
            Activate251(second.Character);
        }

        await Assert.That(retargeted).IsTrue();
    }

    [Test]
    public async Task SecondActor_IgnoresCohortEmptiedCorpse_RetargetsLivePrey()
    {
        var (first, session) = GameplayActorTestRig.CreateActor("ce-first");
        var (second, _) = GameplayActorTestRig.CreateActor("ce-second");
        GameplayActorTestRig.JoinActorWorld(session, second);
        // Same shape as the foreign variant, except the tagger is a FELLOW
        // cohort bot: the cohort seam names both accounts bots, so the
        // tagger-shares-team fallback fires and B reads Party (not Foreign)
        // for the emptied corpse. The ownership-free terminal-empty exclusion
        // must still step past it — an empty corpse never refills regardless
        // of who emptied it. Without the generalization B camps the corpse
        // pin; with it B closes on live prey instead.
        GameplayActorTestRig.SetPosition(second, new Vector3(8, 0, 0));
        var guestRegion = session.World.GetRegionByPos(second.Character.Transform.World.Position)!;
        guestRegion.AddObject(second.Character);
        second.Character.Region = guestRegion;
        var corpseObjId = SpawnBoar(session, first, new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var corpse = session.World.GetNpc(corpseObjId)!;
        corpse.Hp = 0;
        GameplayActorTestRig.SeedLootContainer(corpse, (ProbeItemTemplateId, 1));

        Activate251(first.Character);
        first.Character.CurrentTarget = corpse;
        const uint botAccount = 91_501;
        const uint fellowBotAccount = 91_503;
        second.Character.AccountId = botAccount;
        first.Character.AccountId = fellowBotAccount;
        LootBrainPlanner.CohortResolverForTests = accountId => accountId == botAccount || accountId == fellowBotAccount;
        corpse.CharacterTagging.AddTagger(first.Character, 10);
        _ = QuestDecisionScenario.Run(first, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "ce-take-1" });
        GameplayActorTestRig.SeedLootContainer(corpse);
        // Cohort seam STAYS active through B's wakes (unlike the foreign
        // variant): the tagger-shares-team fallback then fires on B's own
        // reads, so B classifies the emptied corpse Party — the shape the
        // ownership-gated exclusion misses.

        Activate251(second.Character);
        string? lootDetail = null;
        for (var wake = 1; wake <= 2; wake++)
        {
            var run = QuestDecisionScenario.Run(second, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ce-second-{wake}" });
            lootDetail = run.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
            second.PreemptCurrent("unit-test wake settled");
            Activate251(second.Character);
        }

        var pinned = QuestBehavior.TryGetCorpse(second.ActorId, out var corpseRec);
        LootBrainPlanner.CohortResolverForTests = null;
        await Assert.That(pinned).IsFalse();
        await Assert.That(lootDetail!).Contains("target=-");

        var liveObjId = SpawnSecondBoar(session, new Vector3(18, 0, 0));
        var live = session.World.GetNpc(liveObjId)!;
        var retargeted = false;
        for (var wake = 1; wake <= 5; wake++)
        {
            _ = QuestDecisionScenario.Run(second, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ce-live-{wake}" });
            if (second.Character.CurrentTarget == live)
            {
                retargeted = true;
                break;
            }
            second.PreemptCurrent("unit-test wake settled");
            Activate251(second.Character);
        }

        await Assert.That(retargeted).IsTrue();
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
