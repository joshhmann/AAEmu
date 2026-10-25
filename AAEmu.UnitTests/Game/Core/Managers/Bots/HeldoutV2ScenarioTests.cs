using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models;
using AAEmu.Game.Utils;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using System.Numerics;
using System.Reflection;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Held-out generalization suite V2 (PART A, scenarios 1-5): fresh stall-shape
/// variations on the REAL headless actor through <see cref="QuestDecisionScenario.Run"/>
/// with a distinct CycleId per wake. EVERY assertion is a WAKE VERDICT
/// (SelectedAction / dispatch / reason / pin state) — never world state.
/// Quest-251 funnel compatibility pins the prey template to 3475; variation comes
/// from fresh positions, split cadences, and damage attribution instead.
/// </summary>
[NotInParallel]
public class HeldoutV2ScenarioTests
{
    private const uint Quest251 = 251;
    private const uint Prey3475 = 3475;
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

    /// <summary>
    /// Per-target stall trip: 10 stagnant hold-wakes on one in-band target (19 m)
    /// dispatches the pursuit Move close-in instead of yielding forever.
    /// </summary>
    [Test]
    public async Task Scenario1_PerTargetStall_TripsToCloseInMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hv2-stall-single");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnPrey(session, actor, new Vector3(5, 1, 0), new Vector3(24, 1, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        await Assert.That(actor.AutoAttack(actor.Character.CurrentTarget.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        QuestDecisionScenario.QuestRunResult? last = null;
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes; wake++)
        {
            Activate251(actor.Character);
            last = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"hv2-single-{wake}" });
            npc.Hp = 100;

        }

        await Assert.That(last!.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(last.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains("reason=stall-close-in");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// Cross-switch backstop: 6 stagnant wakes on A, then the switch to B. The
    /// 9th consecutive hold-wake actor-wide still holds; the 10th trips the same
    /// close-in — neither per-target run reaches the threshold alone.
    /// </summary>
    [Test]
    public async Task Scenario2_CrossSwitchBackstop_TripsAtActorWideSum()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hv2-stall-split");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var firstObjId = SpawnPrey(session, actor, new Vector3(0, 3, 0), new Vector3(17, 3, 0));
        var secondObjId = SpawnExtraPrey(session, new Vector3(15, 4, 0));
        var first = session.World.GetNpc(firstObjId)!;
        var second = session.World.GetNpc(secondObjId)!;
        actor.Character.CurrentTarget = first;
        await Assert.That(actor.AutoAttack(actor.Character.CurrentTarget.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        for (var wake = 1; wake <= 6; wake++)
        {
            Activate251(actor.Character);
            _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"hv2-split-a-{wake}" });
            first.Hp = 100;
            second.Hp = 100;

        }

        Activate251(actor.Character);
        actor.Character.CurrentTarget = second;
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2-split-b-1" });
        first.Hp = 100;
        second.Hp = 100;


        Activate251(actor.Character);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2-split-b-2" });
        first.Hp = 100;
        second.Hp = 100;


        // 9th consecutive hold-wake actor-wide: still holds, never trips.
        Activate251(actor.Character);
        var ninth = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2-split-b-3" });
        await Assert.That(ninth.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(ninth.LegEvidence.Last(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains(":dispatch=held:reason=inside-combat-band");
        first.Hp = 100;
        second.Hp = 100;


        // The 10th consecutive hold-wake actor-wide: the backstop trips the
        // pursuit Move close-in (verdict + dispatch, not counter internals).
        Activate251(actor.Character);
        var trip = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2-split-b-4" });
        await Assert.That(trip.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(trip.LegEvidence.Last(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains("reason=stall-close-in");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// Foreign-damage attribution: 9 flat wakes plus an HP drop tagged by a
    /// bystander keeps the streak counting, so the 10th wake still trips.
    /// </summary>
    [Test]
    public async Task Scenario3_ForeignDamageAttribution_KeepsCountingAndTrips()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hv2-stall-foreign");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnPrey(session, actor, new Vector3(2, -3, 0), new Vector3(20.5f, -3, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        await Assert.That(actor.AutoAttack(actor.Character.CurrentTarget.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        for (var wake = 1; wake < QuestBehavior.CombatStallWakes; wake++)
        {
            Activate251(actor.Character);
            _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"hv2-foreign-{wake}" });
            npc.Hp = 100;

        }

        // Bystander damage: a foreign tagger plus a visible HP drop on the trip wake.
        Activate251(actor.Character);
        var (_, bystanderSession) = GameplayActorTestRig.CreateActor("hv2-foreign-bystander");
        npc.Hp = 85;
        npc.CharacterTagging.AddTagger(bystanderSession.Character, 15);
        var last = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2-foreign-trip" });

        await Assert.That(last.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(last.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains("reason=stall-close-in");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// Self-damage reset: 9 flat wakes plus an HP drop carrying the actor's own
    /// damage clears the streak, so the 10th wake still holds with no trip.
    /// </summary>
    [Test]
    public async Task Scenario4_SelfDamageAttribution_ResetsStreakAndHolds()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hv2-stall-self");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnPrey(session, actor, new Vector3(4, 2, 0), new Vector3(20.5f, 2, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        await Assert.That(actor.AutoAttack(actor.Character.CurrentTarget.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        for (var wake = 1; wake < QuestBehavior.CombatStallWakes; wake++)
        {
            Activate251(actor.Character);
            _ = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"hv2-self-{wake}" });
            npc.Hp = 100;

        }

        // The actor's own damage landed: self tag plus a visible HP drop.
        Activate251(actor.Character);
        npc.Hp = 85;
        npc.CharacterTagging.AddTagger(actor.Character, 15);
        var last = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "hv2-self-progress" });

        await Assert.That(last.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(last.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains(":dispatch=held:reason=inside-combat-band");

        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        actor.StopAutoAttack();
    }

    /// <summary>
    /// Regen-flat HP verdict trip: incoming damage and regen cancel exactly, so
    /// the target sits at a flat 70 HP for all 10 wakes. No kill ever lands —
    /// the stall detector must trip the Move close-in anyway. (Verdict-only:
    /// no HP assertions of any kind.)
    /// </summary>
    [Test]
    public async Task Scenario5_RegenFlatHp_TripsToCloseInMove()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("hv2-regen-flat");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnPrey(session, actor, new Vector3(-3, 2, 0), new Vector3(15, 2, 0));
        Activate251(actor.Character);
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        await Assert.That(actor.AutoAttack(actor.Character.CurrentTarget.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.AutoAttackTask is { Cancelled: false }).IsTrue();
        // Flat from wake 1: pin BEFORE the loop so no wake ever observes a
        // drop (a 100→70 edge on wake 2 would read as progress and restart).
        npc.Hp = 70;
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();

        QuestDecisionScenario.QuestRunResult? last = null;
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes; wake++)
        {
            Activate251(actor.Character);
            last = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"hv2-regen-{wake}" });
            // Regen cancels DPS exactly: flat at 70, never falling, never a kill.
            npc.Hp = 70;

        }

        await Assert.That(last!.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(last.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
            .Contains("reason=stall-close-in");

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

    private static uint SpawnPrey(HeadlessSession session, GameplayActor actor, Vector3 actorPos, Vector3 npcPos)
    {
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = session.SpawnNpc(Prey3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = Prey3475, Scale = 1f };
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
        // HeadlessSession.SpawnNpc dedups by template — the second 3475 is
        // built directly so the funnel has two live candidates to choose from.
        var objId = s_extraObjId++;
        var npc = new Npc
        {
            ObjId = objId,
            TemplateId = Prey3475,
            Template = new NpcTemplate { Id = Prey3475, Scale = 1f },
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
    /// resolution). Canonical pilot data is never clobbered — every row is
    /// added only when the lookup would otherwise resolve empty.
    /// </summary>
    private static void SeedCombatSurface()
    {
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicRangedAutoAttackSkillId);
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
        if (!map.TryGetValue(Prey3475, out var rows) || !rows.Any(r => r.LootPackId == Pack4530))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = Prey3475,
                NpcId = Prey3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Prey3475] = rows;
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
