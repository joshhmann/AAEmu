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
/// Challenge suite V1 (PART A, scenarios 1-5): harder stall-shape variations on
/// synthetic seeded headless engine rigs through <see cref="QuestDecisionScenario.Run"/>,
/// not live gameplay evidence, with a distinct CycleId per wake. Assertions
/// concern wake verdicts (SelectedAction / dispatch / reason / pin state).
/// Quest-251 funnel compatibility pins the prey template to 3475; variation comes
/// from fresh positions, longer cadences, wider rotations, interleaved damage
/// attribution, give-up recovery, and contended-corpse geometry. Scenario 4 runs
/// on a fresh 97_31x fixture (unclaimed repo-wide), never a copy of v1 or v2.
/// </summary>
[NotInParallel]
public class ChallengeV1ScenarioTests
{
    private const uint Quest251 = 251;
    private const uint Prey3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    private const uint CQuest = 97_310;
    private const uint CComponent = 97_311;
    private const uint CActId = 97_312;
    private const uint CItem = 97_313;
    private const uint CPrey = 97_314;
    private const uint CPack = 97_315;

    private static uint s_extraObjId = 0x9D000;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedCombatSurface();
        SeedCustomChain();
        BotBrainTuningStore.ResetForTests();
        QuestBehavior.ClearCombatStallMemory();
        QuestBehavior.ClearLeashStaleMemory();
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();
        QuestBehavior.ClearGiveUpMemory();
        CombatBrainEngagement.ClearAll();
        TravelIntentStore.ClearAll();
    }

    /// <summary>
    /// Stall trip under a longer cadence: with the stall threshold tuned to 14,
    /// 14 stagnant hold-wakes on one in-band target (19 m) dispatch the pursuit
    /// Move close-in — and wakes 9 and 10 still hold with no premature trip.
    /// </summary>
    [Test]
    public async Task Scenario1_LongerCadence_TripsAtFourteen_HoldsBeforeTen()
    {
        _ = BotBrainTuningStore.Apply(BotBrainTuningStore.ParsePatch("""{"combatStallWakes": 14}"""));
        try
        {
            var (actor, session) = GameplayActorTestRig.CreateActor("ch1-long-stall");
            actor.Character.Ability1 = AbilityType.Wild;
            actor.Character.Hp = actor.Character.MaxHp;
            var npcObjId = SpawnPrey(session, actor, new Vector3(7, -4, 0), new Vector3(26, -4, 0));
            Activate251(actor.Character);
            var npc = session.World.GetNpc(npcObjId)!;
            actor.Character.CurrentTarget = npc;
            actor.Character.IsAutoAttack = true;
            QuestBehavior.ClearCombatStallMemory();
            CombatBrainEngagement.ClearAll();

            var runs = new List<QuestDecisionScenario.QuestRunResult>();
            for (var wake = 1; wake <= 14; wake++)
            {
                Activate251(actor.Character);
                runs.Add(QuestDecisionScenario.Run(actor, (_, _) => [],
                    new QuestDecisionScenario.QuestOptions { CycleId = $"ch1-long-{wake}" }));
                npc.Hp = 100;
                actor.Character.IsAutoAttack = true;
            }

            await Assert.That(runs[8].SelectedAction == ActorActionType.Move).IsFalse();
            await Assert.That(runs[8].LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
                .Contains(":dispatch=held:reason=inside-combat-band");
            await Assert.That(runs[9].SelectedAction == ActorActionType.Move).IsFalse();
            await Assert.That(runs[9].LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
                .Contains(":dispatch=held:reason=inside-combat-band");
            await Assert.That(runs[13].SelectedAction).IsEqualTo(ActorActionType.Move);
            await Assert.That(runs[13].LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail)
                .Contains("reason=stall-close-in");

            QuestBehavior.ClearCombatStallMemory();
            CombatBrainEngagement.ClearAll();
            actor.StopAutoAttack();
        }
        finally
        {
            BotBrainTuningStore.ResetForTests();
            QuestBehavior.ClearCombatStallMemory();
            CombatBrainEngagement.ClearAll();
        }
    }

    /// <summary>
    /// Four live targets rotate every two stagnant wakes. The actor-wide
    /// backstop admits recovery within its existing bound, then retains it.
    /// </summary>
    [Test]
    public async Task Scenario2_FourTargetRotation_BackstopRecoveryIsRetained()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("ch1-rot4");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var aObjId = SpawnPrey(session, actor, new Vector3(0, 5, 0), new Vector3(19, 5, 0));
        var bObjId = SpawnExtraPrey(session, new Vector3(18, 6, 0));
        var cObjId = SpawnExtraPrey(session, new Vector3(18, 4, 0));
        var dObjId = SpawnExtraPrey(session, new Vector3(19, 7, 0));
        var targets = new[] { aObjId, bObjId, cObjId, dObjId };
        var visited = new HashSet<uint>();
        actor.Character.IsAutoAttack = true;
        ActorRequest? recovery = null;
        uint intendedTarget = 0;
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes && recovery == null; wake++)
        {
            intendedTarget = targets[((wake - 1) / 2) % targets.Length];
            var target = session.World.GetNpc(intendedTarget)!;
            await Assert.That(QuestBehavior.CombatOwnsInsideBand(actor.Character,
                Vector3.Distance(actor.Character.Transform.World.Position, target.Transform.World.Position))).IsTrue();
            visited.Add(intendedTarget);
            actor.Character.CurrentTarget = target;
            Activate251(actor.Character);
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1-rot4-{wake}" });
            if (run.Request is { Action: ActorActionType.Move, State: ActorLifecycleState.Running } move)
                recovery = move;
        }
        await Assert.That(visited.Count).IsEqualTo(4);
        await Assert.That(recovery, "Target churn must not postpone recovery beyond the existing actor-wide stall bound.").IsNotNull();
        await Assert.That(recovery!.TargetId).IsEqualTo(intendedTarget);
        await AssertRecoveryRetained(actor, recovery, "ch1-rot4-retained");
        actor.StopAutoAttack();
    }

    /// <summary>
    /// Own progress delays bounded recovery; foreign-only progress does not.
    /// Both cases retain the first admitted move rather than redispatch it.
    /// </summary>
    [Test]
    public async Task Scenario3_InterleavedSelfAndForeignDamage_RecoveryIsRetained()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("ch1-interleave");
        actor.Character.Ability1 = AbilityType.Wild;
        actor.Character.Hp = actor.Character.MaxHp;
        var npcObjId = SpawnPrey(session, actor, new Vector3(6, 1, 0), new Vector3(25, 1, 0));
        var npc = session.World.GetNpc(npcObjId)!;
        actor.Character.CurrentTarget = npc;
        actor.Character.IsAutoAttack = true;
        for (var wake = 1; wake <= 3; wake++)
        {
            Activate251(actor.Character);
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1-mix-flat-{wake}" });
            await Assert.That(run.SelectedAction == ActorActionType.Move).IsFalse();
        }
        var (_, bystanderSession) = GameplayActorTestRig.CreateActor("ch1-mix-bystander");
        npc.Hp = 85;
        npc.CharacterTagging.AddTagger(bystanderSession.Character, 15);
        Activate251(actor.Character);
        var foreign = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "ch1-mix-foreign" });
        await Assert.That(foreign.SelectedAction == ActorActionType.Move).IsFalse();

        npc.CharacterTagging.ClearAllTaggers();
        npc.Hp = 70;
        npc.CharacterTagging.AddTagger(actor.Character, 15);
        Activate251(actor.Character);
        var ownProgress = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "ch1-mix-self" });
        await Assert.That(ownProgress.SelectedAction == ActorActionType.Move).IsFalse();
        ActorRequest? recovery = null;
        for (var wake = 1; wake <= QuestBehavior.CombatStallWakes && recovery == null; wake++)
        {
            Activate251(actor.Character);
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1-mix-after-{wake}" });
            // The old absolute deadline is wake ten (five wakes after own progress).
            if (wake <= QuestBehavior.CombatStallWakes - 5)
                await Assert.That(run.SelectedAction == ActorActionType.Move).IsFalse();
            if (run.Request is { Action: ActorActionType.Move, State: ActorLifecycleState.Running } move)
                recovery = move;
        }
        await Assert.That(recovery, "Own progress must reset, not disable, bounded recovery.").IsNotNull();
        await Assert.That(recovery!.TargetId).IsEqualTo(npcObjId);
        await AssertRecoveryRetained(actor, recovery, "ch1-mix-retained");
        actor.StopAutoAttack();

        // Independent boundary control: foreign damage at the original deadline
        // must not postpone it. No manual counter read/reset stands in for behavior.
        var (foreignActor, foreignSession) = GameplayActorTestRig.CreateActor("ch1-foreign-boundary");
        foreignActor.Character.Ability1 = AbilityType.Wild;
        foreignActor.Character.Hp = foreignActor.Character.MaxHp;
        var foreignNpcId = SpawnPrey(foreignSession, foreignActor, new Vector3(6, 1, 0), new Vector3(25, 1, 0));
        var foreignNpc = foreignSession.World.GetNpc(foreignNpcId)!;
        foreignActor.Character.CurrentTarget = foreignNpc;
        foreignActor.Character.IsAutoAttack = true;
        for (var wake = 1; wake < QuestBehavior.CombatStallWakes; wake++)
        {
            Activate251(foreignActor.Character);
            var run = QuestDecisionScenario.Run(foreignActor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1-foreign-flat-{wake}" });
            await Assert.That(run.SelectedAction == ActorActionType.Move).IsFalse();
        }
        foreignNpc.Hp = 85;
        foreignNpc.CharacterTagging.AddTagger(bystanderSession.Character, 15);
        Activate251(foreignActor.Character);
        var boundary = QuestDecisionScenario.Run(foreignActor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "ch1-foreign-boundary" });
        await Assert.That(boundary.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(boundary.Request!.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(boundary.Request.TargetId).IsEqualTo(foreignNpcId);
        await AssertRecoveryRetained(foreignActor, boundary.Request, "ch1-foreign-retained");
        foreignActor.StopAutoAttack();
    }

    /// <summary>
    /// Give-up then recovery: with the spawner deliberately WITHHELD (never
    /// seeded) and nothing perceived, the quest earns the named give-up on the
    /// 10th unresolved wake — then a legal live target appears and the Target
    /// leg re-engages within 3 wakes with no further give-up.
    /// </summary>
    [Test]
    public async Task Scenario4_GiveUpThenRecovery_ReengagesWithinThreeWakes()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("ch1-giveup-back");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        JoinToRegion(actor, session, new Vector3(0, 0, 0));
        StageCustom(actor);
        // No spawner is seeded for this quest and nothing is perceived: the
        // objective slice resolves no target on every wake.
        var opts = new QuestDecisionScenario.QuestOptions { GiveUpAfterUnresolvedWakes = 10 };

        for (var w = 1; w <= 9; w++)
        {
            var early = QuestDecisionScenario.Run(actor, (_, _) => [],
                opts with { CycleId = $"ch1-recov-{w}" });
            await Assert.That(early.GiveUps.Count).IsEqualTo(0);
            await Assert.That(early.LegEvidence.Single(e => e.Leg == QuestLegId.Advance).Entered).IsTrue();
        }

        var given = QuestDecisionScenario.Run(actor, (_, _) => [],
            opts with { CycleId = "ch1-recov-10" });
        await Assert.That(given.GiveUps.Count).IsEqualTo(1);
        await Assert.That(given.GiveUps[0].QuestId).IsEqualTo(CQuest);
        await Assert.That(given.GiveUps[0].Wakes).IsEqualTo(10);
        await Assert.That(given.GiveUps[0].Reason).StartsWith($"GIVE-UP/UNRESOLVED-TARGET quest={CQuest} stage=SELECT");

        // A legal target appears: a live prey of the quest's own template
        // inside perception.
        var liveObjId = SpawnCustomExtra(session, CPrey, new Vector3(3, 0, 0));
        var live = session.World.GetNpc(liveObjId)!;
        var reengaged = false;
        for (var w = 1; w <= 3; w++)
        {
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                opts with { CycleId = $"ch1-recov-live-{w}" });
            await Assert.That(run.GiveUps.Count).IsEqualTo(0);
            if (run.LegEvidence.Any(e => e.Leg == QuestLegId.Target && e.Emitted)
                || actor.Character.CurrentTarget == live)
                reengaged = true;
            actor.PreemptCurrent("unit-test wake settled");
        }

        await Assert.That(reengaged).IsTrue();
    }

    /// <summary>
    /// Contended corpse with live prey FARTHER than the corpse: an emptied
    /// corpse (container exists + 0 items, foreign-tagged, region-joined) sits
    /// 4 m out while live prey stands 16 m out. The actor must step past the
    /// spent corpse — never pinning it, never looting — and commit to the live
    /// prey instead of camping.
    /// </summary>
    [Test]
    public async Task Scenario5_ContendedCorpse_LivePreyFarther_StepsPast()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("ch1-steppast");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 8, 0));
        var actorRegion = session.World.GetRegionByPos(new Vector3(0, 8, 0))!;
        actorRegion.AddObject(actor.Character);
        actor.Character.Region = actorRegion;

        var corpseObjId = SpawnExtraPrey(session, new Vector3(4, 8, 0));
        var corpse = session.World.GetNpc(corpseObjId)!;
        corpse.Hp = 0;
        // Explicit container staging: exists + 0 items (terminally spent).
        GameplayActorTestRig.SeedLootContainer(corpse);
        // Explicit foreign tagging: a stranger's claim, cohort seam default.
        var (_, strangerSession) = GameplayActorTestRig.CreateActor("ch1-steppast-stranger");
        corpse.CharacterTagging.AddTagger(strangerSession.Character, 10);

        var liveObjId = SpawnExtraPrey(session, new Vector3(16, 8, 0));
        var live = session.World.GetNpc(liveObjId)!;

        Activate251(actor.Character);
        actor.Character.CurrentTarget = corpse;

        string? lootDetail = null;
        for (var w = 1; w <= 2; w++)
        {
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1-steppast-{w}" });
            lootDetail = run.LegEvidence.Single(e => e.Leg == QuestLegId.Loot).Detail;
            await Assert.That(run.SelectedAction == ActorActionType.Loot).IsFalse();
            actor.PreemptCurrent("unit-test wake settled");
            Activate251(actor.Character);
        }

        // The spent corpse is never pinned and the loot leg names no target.
        await Assert.That(QuestBehavior.TryGetCorpse(actor.ActorId, out _)).IsFalse();
        await Assert.That(lootDetail!).Contains("target=-");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Loot)).IsFalse();

        // The live prey, though farther, wins the commitment within 5 wakes.
        var steppedPast = false;
        for (var w = 1; w <= 5; w++)
        {
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"ch1-steppast-live-{w}" });
            if ((run.SelectedAction == ActorActionType.Target && run.Request?.TargetId == liveObjId)
                || actor.Character.CurrentTarget == live)
            {
                steppedPast = true;
                break;
            }
            actor.PreemptCurrent("unit-test wake settled");
            Activate251(actor.Character);
        }

        await Assert.That(steppedPast).IsTrue();
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Loot)).IsFalse();
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

    private static void StageCustom(GameplayActor actor)
    {
        var template = QuestManager.Instance.GetTemplate(CQuest)
            ?? throw new InvalidOperationException($"quest template {CQuest} not seeded");
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
        quest.Objectives = [0, 0, 0, 0, 0];
        actor.Character.Quests.ActiveQuests[CQuest] = quest;
        QuestFixtureRow.InvalidateAll();
    }

    private static void JoinToRegion(GameplayActor actor, HeadlessSession session, Vector3 position)
    {
        var region = session.World.GetRegionByPos(position)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
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
        // HeadlessSession.SpawnNpc dedups by template — extras are built
        // directly so the census holds several live candidates.
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

    private static uint SpawnCustomExtra(HeadlessSession session, uint templateId, Vector3 npcPos)
    {
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
    /// resolution). Canonical pilot data is never clobbered — every row is
    /// added only when the lookup would otherwise resolve empty.
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

    private static void SeedCustomChain()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;

        var questTemplates = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate>)
            typeof(QuestManager).GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(CQuest, out var template))
        {
            template = new AAEmu.Game.Models.Game.Quests.Templates.QuestTemplate { Id = CQuest, Level = 10 };
            questTemplates[CQuest] = template;
        }

        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)
            typeof(QuestManager).GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(CComponent, out var component))
        {
            component = new QuestComponentTemplate(template)
            {
                Id = CComponent,
                KindId = QuestComponentKind.Progress,
            };
            componentTemplates[CComponent] = component;
        }
        if (!template.Components.ContainsKey(CComponent))
            template.Components[CComponent] = component;

        if (!component.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ActId == CActId))
        {
            component.ActTemplates.Add(new QuestActObjItemGather(component)
            {
                ActId = CActId,
                DetailId = CComponent,
                DetailType = nameof(QuestActObjItemGather),
                Count = 3,
                ItemId = CItem,
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
        if (!map.TryGetValue(CPrey, out var rows) || rows.All(r => r.LootPackId != CPack))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = CPrey,
                NpcId = CPrey,
                LootPackId = CPack,
                DefaultPack = true,
            });
            map[CPrey] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(CPack, out var pack) || pack.Loots.All(l => l.ItemId != CItem))
        {
            var loot = new Loot
            {
                Id = CPack,
                Group = 0,
                ItemId = CItem,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = CPack,
                GradeId = 0,
                AlwaysDrop = false,
            };
            if (pack == null)
            {
                packs[CPack] = new LootPack
                {
                    Id = CPack,
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
        var row = QuestFixtureRow.FromQuestData(CQuest);
        if (row.PreyTemplate != CPrey)
            throw new InvalidOperationException(
                $"fixture row resolved prey {row.PreyTemplate}, expected {CPrey}");
    }
    private static async Task AssertRecoveryRetained(GameplayActor actor, ActorRequest recovery, string cycleId)
    {
        await Assert.That(recovery.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
        await Assert.That(recovery.Timeout.HasValue && recovery.Timeout.Value > TimeSpan.Zero).IsTrue();
        await Assert.That(actor.ActiveRequest!.TraceId).IsEqualTo(recovery.TraceId);
        var terminalMovesBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);
        Activate251(actor.Character);
        var next = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = cycleId });
        await Assert.That(next.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(actor.ActiveRequest!.TraceId).IsEqualTo(recovery.TraceId);
        await Assert.That(actor.ActiveRequest.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(actor.ActiveRequest.MoveOwner).IsEqualTo(TravelLegDispatch.PursuitMoveOwner);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move)).IsEqualTo(terminalMovesBefore);
    }

}
