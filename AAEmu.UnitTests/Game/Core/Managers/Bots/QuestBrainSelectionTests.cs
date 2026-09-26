using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestPattern = AAEmu.Game.Models.Game.Quests.Director.QuestPattern;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The quest BRAIN's selection layer, on the REAL headless actor through
/// <see cref="QuestDecisionScenario.Run"/>:
///
///   - the GIVE-UP rule — a quest whose plan keeps failing its gate, or whose
///     objective slice keeps resolving no target, is given up after N wakes with
///     the NAMED reason, never silently;
///   - the 252 item-use shape driven end to end through the wake path: the plan
///     derives, the UseItem leg dispatches, and the engine credits the objective.
///
/// The offering RANK's shape (band primary, reward/personality hooks only
/// breaking band ties) and the candidate bound are proved at the plan/proposal
/// surface in <see cref="QuestBrainRankTests"/>, where the hooks' ordering is
/// observable directly.
/// </summary>
[NotInParallel]
public class QuestBrainSelectionTests
{
    private const uint Quest251 = 251;
    private const uint Quest252 = 252;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestBehavior.ClearGiveUpMemory();
        SeedBoarLootLink();
    }

    // ------------------------------------------------ give-up: plan failures

    /// <summary>
    /// Branch 1: a quest whose plan fails its gate the same way for N
    /// consecutive wakes is given up — and the give-up names the PLAN'S OWN
    /// reason verbatim, so the decision is never silent. The N-1 wakes before
    /// the threshold report the failure but do not give up (the rule must not
    /// fire early).
    /// </summary>
    [Test]
    public async Task GiveUp_PlanFailsRepeatedly_NamesThePlanReasonAtThreshold()
    {
        const int threshold = 3;
        var (actor, session) = GameplayActorTestRig.CreateActor("giveup-plan");
        session.Character.Level = 3;
        session.Character.Hp = session.Character.MaxHp;
        // 251 active, its gather act present, but its loot source stripped: the
        // plan fails its gate every wake with UNPROVEN-SOURCE.
        StageQuest251Progress(actor);
        ClearBoarLootLink();
        var opts = new QuestDecisionScenario.QuestOptions { GiveUpAfterPlanFailures = threshold };

        for (var wake = 1; wake < threshold; wake++)
        {
            var early = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = $"giveup-plan-{wake}" });
            await Assert.That(early.PlanFailures.Count).IsEqualTo(1);
            await Assert.That(early.GiveUps.Count).IsEqualTo(0);
        }

        var result = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = $"giveup-plan-{threshold}" });

        await Assert.That(result.GiveUps.Count).IsEqualTo(1);
        var giveUp = result.GiveUps[0];
        await Assert.That(giveUp.QuestId).IsEqualTo(Quest251);
        await Assert.That(giveUp.Wakes).IsEqualTo(threshold);
        await Assert.That(giveUp.Threshold).IsEqualTo(threshold);
        // The reason IS the plan's own failure string — verbatim, not invented.
        await Assert.That(giveUp.Reason).IsEqualTo(result.PlanFailures[0].Reason);
        await Assert.That(giveUp.Reason).IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=251 act=10473 item=4058");
    }

    /// <summary>
    /// A plan that STOPS failing clears its streak: a data reload that fixes the
    /// gate must not leave the quest one failure away from a give-up. One
    /// failing wake, then a clean one, then more failing wakes — the count
    /// restarts, so the threshold is never reached on the stale streak.
    /// </summary>
    [Test]
    public async Task GiveUp_StreakClearedWhenThePlanRecovers()
    {
        const int threshold = 3;
        var (actor, session) = GameplayActorTestRig.CreateActor("giveup-clear");
        session.Character.Level = 3;
        session.Character.Hp = session.Character.MaxHp;
        StageQuest251Progress(actor);
        var opts = new QuestDecisionScenario.QuestOptions { GiveUpAfterPlanFailures = threshold };

        ClearBoarLootLink();
        var failing = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = "giveup-clear-1" });
        await Assert.That(failing.PlanFailures.Count).IsEqualTo(1);
        await Assert.That(failing.GiveUps.Count).IsEqualTo(0);

        SeedBoarLootLink();
        var clean = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = "giveup-clear-2" });
        await Assert.That(clean.PlanFailures.Count).IsEqualTo(0);
        await Assert.That(clean.GiveUps.Count).IsEqualTo(0);

        // Broken again: the streak restarts at one, so the next threshold-1
        // failing wakes still do not give up.
        ClearBoarLootLink();
        for (var wake = 3; wake < 3 + threshold - 1; wake++)
        {
            var again = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = $"giveup-clear-{wake}" });
            await Assert.That(again.GiveUps.Count).IsEqualTo(0);
        }
    }

    /// <summary>
    /// Threshold 0 disables the rule entirely: the failure is still reported,
    /// but no give-up is ever decided.
    /// </summary>
    [Test]
    public async Task GiveUp_ThresholdZero_DisabledButFailureStillReported()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("giveup-off");
        session.Character.Level = 3;
        session.Character.Hp = session.Character.MaxHp;
        StageQuest251Progress(actor);
        ClearBoarLootLink();
        var opts = new QuestDecisionScenario.QuestOptions { GiveUpAfterPlanFailures = 0 };

        for (var wake = 0; wake < 5; wake++)
        {
            var result = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = $"giveup-off-{wake}" });
            await Assert.That(result.PlanFailures.Count).IsEqualTo(1);
            await Assert.That(result.GiveUps.Count).IsEqualTo(0);
        }
    }

    // -------------------------------------------- give-up: unresolved target

    /// <summary>
    /// Branch 2: a plan that RUNS but whose objective slice resolves no target
    /// for N consecutive wakes is given up with the SLICE'S own named detail —
    /// no target was reachable in this world. The staged 251 quest is live and
    /// its loot link resolves, but no prey NPC is in perception, so the funnel
    /// selects nothing.
    /// </summary>
    [Test]
    public async Task GiveUp_ObjectiveResolvesNoTarget_NamesTheSliceDetail()
    {
        const int threshold = 2;
        var (actor, session) = GameplayActorTestRig.CreateActor("giveup-target");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        StageQuest251Progress(actor);
        var opts = new QuestDecisionScenario.QuestOptions { GiveUpAfterUnresolvedWakes = threshold };

        var first = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = "giveup-target-1" });
        await Assert.That(first.GiveUps.Count).IsEqualTo(0);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = "giveup-target-2" });

        await Assert.That(result.GiveUps.Count).IsEqualTo(1)
            .Because($"stage={result.FailStage} reason={result.FailReason} plans={result.PlanFailures.Count} planReason={string.Join(";", result.PlanFailures.Select(f => f.Reason))} evidence={string.Join("|", result.LegEvidence.Select(e => $"{e.Leg}:{e.Detail}"))}");
        var giveUp = result.GiveUps[0];
        await Assert.That(giveUp.QuestId).IsEqualTo(Quest251);
        await Assert.That(giveUp.Wakes).IsEqualTo(threshold);
        // The reason is the funnel's OWN stage vocabulary, not an invented phrase.
        await Assert.That(giveUp.Reason).StartsWith("GIVE-UP/UNRESOLVED-TARGET quest=251 stage=SELECT");
        await Assert.That(giveUp.Reason).Contains("raw=0");
    }

    /// <summary>
    /// A resolved target clears the unresolved streak: staging a live prey
    /// within perception makes the funnel select, and the counter must not
    /// carry over from the unresolved wakes before it — so the next wake cannot
    /// give up on the stale count.
    /// </summary>
    [Test]
    public async Task GiveUp_UnresolvedStreakClearedWhenTargetResolves()
    {
        const int threshold = 2;
        var (actor, session) = GameplayActorTestRig.CreateActor("giveup-target-clear");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        StageQuest251Progress(actor);
        JoinActorRegion(session);
        var opts = new QuestDecisionScenario.QuestOptions { GiveUpAfterUnresolvedWakes = threshold };

        var unresolved = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = "clear-1" });
        await Assert.That(unresolved.GiveUps.Count).IsEqualTo(0);

        // A live prey of the quest's own template inside perception: the funnel
        // selects, which must clear the streak.
        StagePerceivedBoar(actor, session);
        var resolved = QuestDecisionScenario.Run(actor,
            (_, _) => session.World.GetAllNpcs(), opts with { CycleId = "clear-2" });
        await Assert.That(resolved.LegEvidence.Single(e => e.Leg == QuestLegId.Target).Emitted).IsTrue();
        await Assert.That(resolved.GiveUps.Count).IsEqualTo(0);

        // Prey gone again: the streak restarts at one, so this wake does not
        // give up (the stale count must not carry through the resolve).
        var npc = session.World.GetNpc(session.World.GetAllNpcs().First(n => n != null).ObjId)!;
        session.World.RemoveObject(npc);
        var afterRemove = QuestDecisionScenario.Run(actor, (_, _) => [], opts with { CycleId = "clear-3" });
        await Assert.That(afterRemove.GiveUps.Count).IsEqualTo(0);
    }

    // -------------------------------------------------- 252 through the wake

    /// <summary>
    /// The item-use shape driven end to end through the wake path: 252 is
    /// accepted from its live giver, its UseItem leg dispatches through the real
    /// actor verb, the engine credits the objective, and the bag item is
    /// consumed — no director change, the same plan the derivation produced.
    /// </summary>
    [Test]
    public async Task Wake252_UseItemLegDispatchesAndCreditsObjective()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        GameplayActorTestRig.SeedItemTemplate(7738, 11596);
        var (actor, session) = GameplayActorTestRig.CreateActor("wake-252");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        character.Quests!.SetCompletedQuestFlag(Quest251, true);
        JoinActorRegion(session);
        var giverObjId = GameplayActorTestRig.SpawnNpc(session, 7653);
        GameplayActorTestRig.SetNpcPosition(session, giverObjId, new System.Numerics.Vector3(1, 0, 0));
        GameplayActorTestRig.SetMoney(actor, 0);

        var opts = new QuestDecisionScenario.QuestOptions { CycleId = "wake-252-1", BandMax = 10 };

        // Wake 1: discover + accept 252 from its live giver.
        var accept = QuestDecisionScenario.Run(actor, (_, _) => session.World.GetAllNpcs(), opts);
        await Assert.That(accept.SelectedAction).IsEqualTo(ActorActionType.AcceptQuest);
        await Assert.That(character.Quests.ActiveQuests.ContainsKey(Quest252)).IsTrue();

        // The plan the wake derives for 252 is the item-use shape.
        var plan = QuestDirector.Plan(Quest252, QuestFixtureRow.FromQuestData(Quest252));
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.UseItem);
        await Assert.That(plan.Leg(QuestLegId.UseItem)).IsNotNull();

        // The quest's own supply put the objective item in the bag.
        await Assert.That(GameplayActorTestRig.BagCount(actor, 7738)).IsGreaterThanOrEqualTo(1);

        // Wake 2: the item-use leg dispatches through the real actor verb, and
        // the engine credits the objective — the quest auto-completes.
        var use = QuestDecisionScenario.Run(actor, (_, _) => session.World.GetAllNpcs(),
            opts with { CycleId = "wake-252-2" });
        await Assert.That(use.SelectedAction).IsEqualTo(ActorActionType.UseItem);
        await Assert.That(use.Request?.State).IsEqualTo(ActorLifecycleState.Completed)
            .Because($"detail: {use.Request?.Detail} || evidence: {string.Join(" | ", use.LegEvidence.Select(e => $"{e.Leg}:{e.Detail}"))}");

        // The brain never sends a SECOND consume for a satisfied objective: on
        // the next wake the item-use leg withdraws with its OWN named reason
        // (the objective credited, or the quest having left Progress), and no
        // new UseItem lands. Every gate reason is the leg's own vocabulary —
        // the assertion names the contract, not a single phrase.
        var useDispatches = actor.AuditTrace.Count(r => r.Action == ActorActionType.UseItem);
        var afterUse = QuestDecisionScenario.Run(actor, (_, _) => session.World.GetAllNpcs(),
            opts with { CycleId = "wake-252-3" });
        var useLeg = afterUse.LegEvidence.Single(e => e.Leg == QuestLegId.UseItem);
        await Assert.That(useLeg.Emitted).IsFalse()
            .Because($"detail: {useLeg.Detail}");
        // The leg NAMED its withdraw (its enter gate's reason or its own
        // dispatch=withdrawn reason) — never the untouched default.
        await Assert.That(useLeg.Detail).IsNotEqualTo(QuestLegWake.NoProposal);
        await Assert.That(afterUse.SelectedAction).IsNotEqualTo(ActorActionType.UseItem);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.UseItem))
            .IsEqualTo(useDispatches);
    }

    /// <summary>
    /// The item-use leg withdraws with a NAMED reason when the bag is empty:
    /// nothing to consume, so no use is dispatched — the leg never sends the
    /// engine a use it would refuse.
    /// </summary>
    [Test]
    public async Task Wake252_EmptyBag_UseItemLegWithdrawsNamed()
    {
        M1M2ReplayScenarioRigTests.SeedReplaySurface();
        GameplayActorTestRig.SeedItemTemplate(7738, 11596);
        var (actor, session) = GameplayActorTestRig.CreateActor("wake-252-empty");
        var character = session.Character;
        character.Level = 3;
        character.Hp = character.MaxHp;
        // Stage 252 active/Progress with NO item in the bag.
        character.Quests.ActiveQuests[Quest252] = new Quest(character)
        {
            TemplateId = Quest252,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0]
        };
        await Assert.That(GameplayActorTestRig.BagCount(actor, 7738)).IsEqualTo(0);

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "wake-252-empty-1" });

        var evidence = result.LegEvidence.Single(e => e.Leg == QuestLegId.UseItem);
        await Assert.That(evidence.Emitted).IsFalse();
        await Assert.That(evidence.Detail).Contains("reason=no-item");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.UseItem)).IsFalse();
    }

    // ------------------------------------------------------------ fixtures

    /// <summary>Stages 251 active/Progress, so its plan derives and its funnel runs.</summary>
    private static void StageQuest251Progress(GameplayActor actor)
        => actor.Character.Quests.ActiveQuests[Quest251] = new Quest(actor.Character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0]
        };

    /// <summary>
    /// Spawns a live, visible boar (the 251 prey template) in the actor's own
    /// region so the wake's perception carries it and the funnel can select it —
    /// the exact shape the sibling objective-path rigs use.
    /// </summary>
    private static void StagePerceivedBoar(GameplayActor actor, HeadlessSession session)
    {
        var actorPos = actor.Character.Transform.World.Position;
        var region = session.World.GetRegionByPos(actorPos);
        region?.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = GameplayActorTestRig.SpawnNpc(session, Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, actorPos);
        region?.AddObject(npc);
        npc.Region = region;
    }

    /// <summary>Joins the character to its region grid so perception sees the world's NPCs.</summary>
    private static void JoinActorRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        region?.AddObject(character);
        character.Region = region;
    }

    /// <summary>Removes the 3475→4530→4058 loot link and invalidates the row memo (the plan-failure fixture).</summary>
    private static void ClearBoarLootLink()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        if (typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!.GetValue(ItemManager.Instance)
            is Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc>> map)
            map.Remove(Boar3475);
        if (typeof(LootGameData).GetField("_lootPacks", flags)!.GetValue(LootGameData.Instance)
            is Dictionary<uint, AAEmu.Game.Models.Game.Items.Loots.LootPack> packs)
            packs.Remove(Pack4530);
        QuestFixtureRow.InvalidateAll();
    }

    /// <summary>Additively restores the 3475→4530→4058 loot link and invalidates the memo.</summary>
    private static void SeedBoarLootLink()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(Boar3475, out var rows) || rows.All(r => r.LootPackId != Pack4530))
        {
            rows ??= [];
            rows.Add(new AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc
            {
                Id = Boar3475,
                NpcId = Boar3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Boar3475] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, AAEmu.Game.Models.Game.Items.Loots.LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(Pack4530, out var pack) || pack.Loots.All(l => l.ItemId != Meat4058))
        {
            var loot = new AAEmu.Game.Models.Game.Items.Loots.Loot
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
                packs[Pack4530] = new AAEmu.Game.Models.Game.Items.Loots.LootPack
                {
                    Id = Pack4530,
                    Loots = [loot],
                    LootsByGroupNo = new Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.Loot>> { [0] = [loot] },
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
