using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Existing accepted260 setup and exact reference-row mirrors are disclosed.
/// Scripted native death creates loot; the production quest/corpse/actor path
/// must select and grant it with inventory-derived credit. No earned attack,
/// first acceptance, turn-in or autonomous follow-up is claimed by this rig.
/// </summary>
[NotInParallel]
public class QuestNpcDropSourceBehaviorTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();
    }

    [Test]
    [Arguments(3497u)]
    [Arguments(7675u)]
    public async Task AlternateNativeDeath_OrdinaryCorpseSelectionAndLootCreditsOnlyRecipient(uint templateId)
    {
        using var sources = new CanonicalSources();
        var (actor, session) = GameplayActorTestRig.CreateActor("alternate-source-credit");
        var (foreign, _) = GameplayActorTestRig.CreateActor("alternate-source-foreign");
        GameplayActorTestRig.JoinActorWorld(session, foreign);
        actor.Character.Level = foreign.Character.Level = 20; // Exclude NPC XP.
        actor.Character.Hp = actor.Character.MaxHp;
        foreign.Character.Hp = foreign.Character.MaxHp;
        var quest = StageAcceptedQuest(actor);
        var foreignQuest = StageAcceptedQuest(foreign);
        var row = QuestFixtureRow.FromQuestData(260);
        var npc = Spawn(session, templateId, new Vector3(2, 0, 0));
        var selection = QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "alternate-alive", row);
        await Assert.That(selection.SelectedObjId).IsEqualTo(npc.ObjId);
        await Assert.That(actor.Character.CurrentTarget).IsNull();
        await Assert.That(GameplayActorTestRig.BagCount(actor, 8128)).IsEqualTo(0);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        // Native death and the NPC's own pack generate the quest item. This
        // scripted tag/death is service input, never an earned-combat claim.
        npc.CharacterTagging.AddTagger(actor.Character, int.MaxValue / 4);
        npc.Hp = 0;
        npc.DoDie(actor.Character, KillReason.Damage);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
        await Assert.That(npc.LootingContainer.Items.Values.Single().Item.TemplateId).IsEqualTo(8128u);
        var refused = foreign.Loot(npc.ObjId);
        await Assert.That(refused.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That((int)refused.Result!).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(foreign, 8128)).IsEqualTo(0);
        await Assert.That(foreignQuest.Objectives[0]).IsEqualTo(0);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);

        // No externally pinned corpse or loot request: ordinary quest census
        // recognition emits the actual-template probe and ordinary Loot action.
        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "alternate-dead", ChosenQuestId = 260 });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(result.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(GameplayActorTestRig.BagCount(actor, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(foreignQuest.Objectives[0]).IsEqualTo(0);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        await Assert.That(QuestBehavior.TryGetCorpse(actor.ActorId, out var corpse)).IsTrue();
        await Assert.That(corpse!.TemplateId).IsEqualTo(templateId);
        var takes = actor.AuditTrace.Count(a => a.Action == ActorActionType.Loot);
        _ = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "alternate-spent", ChosenQuestId = 260 });
        await Assert.That(actor.AuditTrace.Count(a => a.Action == ActorActionType.Loot)).IsEqualTo(takes);
        await Assert.That(GameplayActorTestRig.BagCount(actor, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
    }

    [Test]
    [Arguments(0u)]
    [Arguments(1u)]
    public async Task DifferentFinalKiller_EntitledQuestOwnerReceivesOrdinaryQuestDrop(uint group)
    {
        // Group1 is a synthetic collateral guard; default group0 mirrors canonical rows.
        using var sources = new CanonicalSources(group);
        var (owner, session) = GameplayActorTestRig.CreateActor("quest-loot-owner");
        var (finisher, _) = GameplayActorTestRig.CreateActor("quest-loot-finisher");
        GameplayActorTestRig.JoinActorWorld(session, finisher);
        owner.Character.Level = finisher.Character.Level = 20;
        owner.Character.Hp = owner.Character.MaxHp;
        finisher.Character.Hp = finisher.Character.MaxHp;
        var quest = StageAcceptedQuest(owner);
        GameplayActorTestRig.SetPosition(finisher, Vector3.Zero);
        var npc = Spawn(session, 3497, new Vector3(2, 0, 0));
        // Scripted native damage, with ordinary damage/aggro ordering. No
        // manual tag, corpse/item injection, objective counter or final DoDie.
        npc.ReduceCurrentHp(owner.Character, 60);
        npc.OnDamageReceived(owner.Character, 60);
        await Assert.That(npc.CharacterTagging.Tagger).IsEqualTo(owner.Character);
        await Assert.That(finisher.Character.Quests.HasQuest(260)).IsFalse();
        npc.ReduceCurrentHp(finisher.Character, 40);
        npc.OnDamageReceived(finisher.Character, 40);
        await Assert.That(npc.Hp).IsEqualTo(0);
        await Assert.That(npc.LootingContainer.GetNpcLootPermission(owner.Character)).IsEqualTo(true);
        await Assert.That(npc.LootingContainer.GetNpcLootPermission(finisher.Character)).IsEqualTo(false);
        await Assert.That(npc.LootingContainer.Items.Values.Count(i => i.Item.TemplateId == 8128)).IsEqualTo(1);
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "different-final-killer", ChosenQuestId = 260 });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(finisher, 8128)).IsEqualTo(0);
    }


    [Test]
    public async Task OwnGeneratedCorpse_WithAnotherUsefulLiveMob_LootBeforeStartingFreshFight()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("corpse-before-fresh-prey");
        owner.Character.Level = 20;
        owner.Character.Hp = owner.Character.MaxHp;
        owner.Character.Mp = owner.Character.MaxMp;
        var quest = StageAcceptedQuest(owner);
        var npc = Spawn(session, 3497, new Vector3(2, 0, 0));
        _ = owner.SetTarget(npc.ObjId);
        npc.ReduceCurrentHp(owner.Character, 60);
        npc.OnDamageReceived(owner.Character, 60);
        npc.ReduceCurrentHp(owner.Character, 40);
        npc.OnDamageReceived(owner.Character, 40);
        await Assert.That(npc.LootingContainer.GetNpcLootPermission(owner.Character)).IsEqualTo(true);
        await Assert.That(npc.LootingContainer.Items.Values.Count(i => i.Item.TemplateId == 8128)).IsEqualTo(1);
        var other = Spawn(session, 7675, new Vector3(30, 0, 0));
        await Assert.That(other.Hp > 0).IsTrue();
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-with-live-alternative", ChosenQuestId = 260 });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(other.Hp).IsEqualTo(100);
    }

    [Test]
    public async Task UsefulCorpse_ValidLivingFightKeepsItsTargetAndAction()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("corpse-retained-fight");
        var quest = StageHealthyQuest(owner);
        var corpse = NativeCorpse(owner, session);
        var living = Spawn(session, 7675, new Vector3(2, 0, 0));
        _ = owner.SetTarget(living.ObjId);
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-retained-fight", ChosenQuestId = 260 });
        // Existing pursuit first confirms its Stop hold, then ordinary combat
        // starts on the following wake. Both keep the same living commitment.
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
        var combat = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-retained-fight-settled", ChosenQuestId = 260 });
        await Assert.That(combat.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(owner.Character.CurrentTarget!.ObjId).IsEqualTo(living.ObjId);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(0);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        await Assert.That(corpse.LootingContainer.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RemovedLivingTarget_DoesNotHideUsefulOwnCorpse()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("corpse-stale-target");
        var quest = StageHealthyQuest(owner);
        _ = NativeCorpse(owner, session);
        var removed = Spawn(session, 7675, new Vector3(2, 0, 0));
        _ = owner.SetTarget(removed.ObjId);
        removed.Region.RemoveObject(removed);
        await Assert.That(session.World.RemoveObject(removed)).IsTrue();
        await Assert.That(removed.Hp).IsEqualTo(100);
        await Assert.That(session.World.GetBaseUnit(removed.ObjId)).IsNull();
        _ = Spawn(session, 7675, new Vector3(30, 0, 0));
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-stale-target", ChosenQuestId = 260 });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
    }

    [Test]
    public async Task ForeignCorpse_WithLivePreyDoesNotStealOrBlockFreshWork()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("corpse-foreign-recovery");
        var (foreign, _) = GameplayActorTestRig.CreateActor("corpse-legitimate-owner");
        GameplayActorTestRig.JoinActorWorld(session, foreign);
        var quest = StageHealthyQuest(owner);
        StageHealthyQuest(foreign);
        var corpse = NativeCorpse(foreign, session);
        var living = Spawn(session, 7675, new Vector3(30, 0, 0));
        await Assert.That(corpse.LootingContainer.GetNpcLootPermission(owner.Character)).IsEqualTo(false);
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-foreign-recovery", ChosenQuestId = 260 });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Target);
        await Assert.That(owner.Character.CurrentTarget!.ObjId).IsEqualTo(living.ObjId);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(0);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        await Assert.That(corpse.LootingContainer.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EmptyOwnCorpse_WithLivePreyDoesNotDuplicateItsCredit()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("corpse-empty-recovery");
        var quest = StageHealthyQuest(owner);
        var corpse = NativeCorpse(owner, session);
        _ = owner.Loot(corpse.ObjId); // Ordinary take, not a synthetic empty container.
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(corpse.LootingContainer.Items.Count).IsEqualTo(0);
        var living = Spawn(session, 7675, new Vector3(30, 0, 0));
        var takes = owner.AuditTrace.Count(a => a.Action == ActorActionType.Loot);
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-empty-recovery", ChosenQuestId = 260 });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Target);
        await Assert.That(owner.Character.CurrentTarget!.ObjId).IsEqualTo(living.ObjId);
        await Assert.That(owner.AuditTrace.Count(a => a.Action == ActorActionType.Loot)).IsEqualTo(takes);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
    }

    [Test]
    public async Task UnsafeOwnCorpse_DoesNotBypassTheExistingDangerGate()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("corpse-unsafe-recovery");
        var quest = StageHealthyQuest(owner);
        var corpse = NativeCorpse(owner, session, new Vector3(15, 0, 0));
        _ = Spawn(session, 7675, new Vector3(16, 0, 0));
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions
                { CycleId = "corpse-unsafe-recovery", ChosenQuestId = 260, LootSafeRadiusM = 10 });
        await Assert.That(result.SelectedAction).IsNotEqualTo(ActorActionType.Loot);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(0);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
        await Assert.That(corpse.LootingContainer.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeadCorpseAttackLoop_EndsNormallyBeforeLootWithoutRetargeting()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("corpse-native-loop-end");
        var quest = StageHealthyQuest(owner);
        var corpse = Spawn(session, 3497, new Vector3(2, 0, 0));
        _ = owner.SetTarget(corpse.ObjId);
        _ = owner.AutoAttack(corpse.ObjId);
        var nativeLoop = owner.Character.AutoAttackTask!;
        await Assert.That(nativeLoop).IsNotNull();
        corpse.ReduceCurrentHp(owner.Character, 60);
        corpse.OnDamageReceived(owner.Character, 60);
        corpse.ReduceCurrentHp(owner.Character, 40);
        corpse.OnDamageReceived(owner.Character, 40);
        var living = Spawn(session, 7675, new Vector3(30, 0, 0));
        await Assert.That(corpse.Hp).IsEqualTo(0);
        await Assert.That(corpse.LootingContainer.Items.Count).IsEqualTo(1);
        _ = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-loop-await", ChosenQuestId = 260 });
        await Assert.That(owner.Character.CurrentTarget!.ObjId).IsEqualTo(corpse.ObjId);
        await Assert.That(owner.Character.AutoAttackTask).IsEqualTo(nativeLoop);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(0);
        // Exercise the existing loop's ordinary next tick. No new timer or
        // externally dispatched StopAutoAttack is supplied by the quest owner.
        nativeLoop.Execute();
        await Assert.That(owner.Character.IsAutoAttack).IsFalse();
        var result = QuestDecisionScenario.Run(owner, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "corpse-loop-ended", ChosenQuestId = 260 });
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Loot);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(living.Hp).IsEqualTo(100);
    }

    private static Quest StageHealthyQuest(GameplayActor actor)
    {
        actor.Character.Level = 20;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        return StageAcceptedQuest(actor);
    }

    private static Npc NativeCorpse(GameplayActor owner, HeadlessSession session, Vector3? position = null)
    {
        var npc = Spawn(session, 3497, position ?? new Vector3(2, 0, 0));
        npc.ReduceCurrentHp(owner.Character, 60);
        npc.OnDamageReceived(owner.Character, 60);
        npc.ReduceCurrentHp(owner.Character, 40);
        npc.OnDamageReceived(owner.Character, 40);
        return npc;
    }

    [Test]
    public async Task AlternateObservedPrey_OwnsWake_NearestValidSourceTravelKeepsActualTemplate()
    {
        using var sources = new CanonicalSources();
        var (actor, session) = GameplayActorTestRig.CreateActor("alternate-source-travel");
        StageAcceptedQuest(actor);
        var row = QuestFixtureRow.FromQuestData(260);
        session.World.SpawnManager ??= new SpawnManager(session.World);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", PrivateInstance)!.GetValue(session.World.SpawnManager)!;
        spawners[990011] = [new NpcSpawner { Id = 990011, UnitId = 3484,
            Position = new WorldSpawnPosition { X = 120, Y = 0, Z = 0 } }];
        spawners[990012] = [new NpcSpawner { Id = 990012, UnitId = 3497,
            Position = new WorldSpawnPosition { X = 80, Y = 0, Z = 0 } }];
        spawners[990013] = [new NpcSpawner { Id = 990013, UnitId = 7675,
            Position = new WorldSpawnPosition { X = 100, Y = 0, Z = 0 } }];
        // Execute the production resolver, not just a helper returning a point.
        var resolver = typeof(BotRoamStepExecutor).GetMethod("ResolveQuestObjectiveSource",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { actor, 260u, default(Vector3), 0u, false };
        await Assert.That((bool)resolver.Invoke(null, args)!).IsTrue();
        await Assert.That((uint)args[3]!).IsEqualTo(3497u);
        await Assert.That(((Vector3)args[2]!).X).IsEqualTo(80f);
        await Assert.That((bool)args[4]!).IsTrue();
        var npc = Spawn(session, 7675, new Vector3(50, 0, 0));
        await Assert.That(QuestBehavior.HasPerceivedLivingPrey(actor, BotObservedContext.Capture(actor), row)).IsTrue();
        args = [actor, 260u, default(Vector3), 0u, false];
        await Assert.That((bool)resolver.Invoke(null, args)!).IsFalse();
        // A stale source map cannot make an unrelated pack a useful source.
        sources.Mappings.Remove(7675);
        await Assert.That(QuestBehavior.HasPerceivedLivingPrey(actor, BotObservedContext.Capture(actor), row)).IsFalse();
        await Assert.That(QuestObjectiveTargetSelector.Evaluate(actor.Character,
            BotObservedContext.Capture(actor), "alternate-invalid-pack", row).SelectedObjId).IsEqualTo(0u);
        await Assert.That(npc.Hp > 0).IsTrue();
    }

    private static Quest StageAcceptedQuest(GameplayActor actor)
    {
        GameplayActorTestRig.SetPosition(actor, Vector3.Zero);
        var region = actor.Character.ParentWorld!.GetRegionByPos(Vector3.Zero)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        // Reconstruct existing accepted state through native steps/actions;
        // never use this fixture to assert first-acceptance prerequisite proof.
        var quest = new Quest(QuestManager.Instance.GetTemplate(260)!, actor.Character);
        quest.SkipUpdatePackets();
        actor.Character.Quests.ActiveQuests[260] = quest;
        quest.Status = QuestStatus.Progress;
        quest.Step = QuestComponentKind.Progress;
        return quest;
    }


    [Test]
    public async Task ForeignFinisherWithQuest_CannotQualifyUnentitledQuestDrop()
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("no-quest-tagger");
        var (finisher, _) = GameplayActorTestRig.CreateActor("unentitled-quest-finisher");
        GameplayActorTestRig.JoinActorWorld(session, finisher);
        owner.Character.Level = finisher.Character.Level = 20;
        StageAcceptedQuest(owner);
        owner.Character.Quests.ActiveQuests.TryRemove(260, out _);
        var quest = StageAcceptedQuest(finisher);
        var npc = Spawn(session, 3497, new Vector3(2, 0, 0));
        npc.ReduceCurrentHp(owner.Character, 60);
        npc.OnDamageReceived(owner.Character, 60);
        npc.ReduceCurrentHp(finisher.Character, 40);
        npc.OnDamageReceived(finisher.Character, 40);
        await Assert.That(npc.LootingContainer.GetNpcLootPermission(owner.Character)).IsEqualTo(true);
        await Assert.That(npc.LootingContainer.GetNpcLootPermission(finisher.Character)).IsEqualTo(false);
        await Assert.That(npc.LootingContainer.Items.Values.Count(i => i.Item.TemplateId == 8128)).IsEqualTo(0);
        finisher.Loot(npc.ObjId);
        await Assert.That(GameplayActorTestRig.BagCount(finisher, 8128)).IsEqualTo(0);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NativeParty_QuestHolderQualifiesOneSharedDrop_RecipientGateStillApplies(bool bothNeedQuest)
    {
        using var sources = new CanonicalSources();
        var (owner, session) = GameplayActorTestRig.CreateActor("party-quest-owner");
        var (finisher, _) = GameplayActorTestRig.CreateActor("party-finisher");
        GameplayActorTestRig.JoinActorWorld(session, finisher);
        owner.Character.Level = finisher.Character.Level = 20;
        owner.Character.Hp = owner.Character.MaxHp;
        var quest = StageAcceptedQuest(owner);
        StageAcceptedQuest(finisher);
        if (!bothNeedQuest) finisher.Character.Quests.ActiveQuests.TryRemove(260, out _);
        GameplayActorTestRig.ForceSeedTeamManager();
        await Assert.That(owner.PartyInvite(finisher.Character.ObjId).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(finisher.PartyAccept().State).IsEqualTo(ActorLifecycleState.Completed);
        // Disclosed ordinary FreeForAll party rule; distribution modes are not changed.
        TeamManager.Instance.GetTeamByObjId(owner.Character.ObjId).LootingRule.LootMethod =
            AAEmu.Game.Models.Game.Team.LootingRuleMethod.FreeForAll;
        var npc = Spawn(session, 3497, new Vector3(2, 0, 0));
        npc.ReduceCurrentHp(owner.Character, 60);
        npc.OnDamageReceived(owner.Character, 60);
        npc.ReduceCurrentHp(finisher.Character, 40);
        npc.OnDamageReceived(finisher.Character, 40);
        await Assert.That(npc.LootingContainer.GetNpcLootPermission(owner.Character)).IsEqualTo(true);
        await Assert.That(npc.LootingContainer.GetNpcLootPermission(finisher.Character)).IsEqualTo(true);
        await Assert.That(npc.LootingContainer.Items.Values.Count(i => i.Item.TemplateId == 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(0); // Generation is not credit.
        if (!bothNeedQuest)
        {
            finisher.Loot(npc.ObjId);
            await Assert.That(GameplayActorTestRig.BagCount(finisher, 8128)).IsEqualTo(0);
            await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(1);
        }
        owner.Loot(npc.ObjId);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
        finisher.Loot(npc.ObjId);
        owner.Loot(npc.ObjId);
        await Assert.That(GameplayActorTestRig.BagCount(finisher, 8128)).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(owner, 8128)).IsEqualTo(1);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(0);
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
    }

    [Test]
    public async Task QuestOnlyPositiveGroup_OrdinaryGenerationHasOneDrop()
    {
        using var sources = new CanonicalSources();
        var (owner, _) = GameplayActorTestRig.CreateActor("quest-only-group");
        StageAcceptedQuest(owner);
        var loot = new Loot { Id = 990001, Group = 1, ItemId = 8128, DropRate = 10_000_000,
            MinAmount = 1, MaxAmount = 1, GradeId = 0, AlwaysDrop = false };
        var pack = new LootPack { Id = 990001, Loots = [loot], LootsByGroupNo = new() { [1] = [loot] },
            Groups = [], ActabilityGroups = [], GroupCount = 1 };
        var result = pack.GeneratePackNewV2(1f, 1f, owner.Character, AAEmu.Game.Models.Game.Char.ActabilityType.None);
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].itemId).IsEqualTo(8128u);
        await Assert.That(result[0].count).IsEqualTo(1);
    }


    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LegacyAndNpcRoll_NonQuestCurrencyAndGradeStayOrdinary(bool needsQuest)
    {
        using var sources = new CanonicalSources();
        var (owner, _) = GameplayActorTestRig.CreateActor("ordinary-pack-inputs");
        StageAcceptedQuest(owner);
        if (!needsQuest) owner.Character.Quests.ActiveQuests.TryRemove(260, out _);
        GameplayActorTestRig.SeedItemTemplate(500);
        GameplayActorTestRig.SeedItemTemplate(29203);
        var quest = new Loot { Id = 990011, Group = 0, ItemId = 8128, DropRate = 10_000_000,
            MinAmount = 1, MaxAmount = 1, GradeId = 0 };
        var coins = new Loot { Id = 990012, Group = 0, ItemId = 500, DropRate = 10_000_000,
            MinAmount = 100, MaxAmount = 100, GradeId = 0 };
        var ordinary = new Loot { Id = 990013, Group = 0, ItemId = 29203, DropRate = 10_000_000,
            MinAmount = 2, MaxAmount = 2, GradeId = 3 };
        var pack = new LootPack { Id = 990011, Loots = [quest, coins, ordinary],
            LootsByGroupNo = new() { [0] = [quest, coins, ordinary] },
            Groups = [], ActabilityGroups = [], GroupCount = 1 };
        var legacy = pack.GeneratePackNewV2(1f, 2f, owner.Character, AAEmu.Game.Models.Game.Char.ActabilityType.None, 4);
        var npc = pack.GeneratePackNewV2(1f, 2f, owner.Character, AAEmu.Game.Models.Game.Char.ActabilityType.None, 4, _ => false);
        await Assert.That(legacy.Count(i => i.itemId == 8128)).IsEqualTo(needsQuest ? 1 : 0);
        await Assert.That(npc.Count(i => i.itemId == 8128)).IsEqualTo(0);
        await Assert.That(npc.SequenceEqual(legacy.Where(i => i.itemId != 8128))).IsTrue();
        await Assert.That(npc.All(i => i.grade == 4)).IsTrue();
        var nonNpc = pack.GeneratePack(owner.Character, AAEmu.Game.Models.Game.Char.ActabilityType.None);
        await Assert.That(nonNpc.Count(i => i.itemId == 8128)).IsEqualTo(needsQuest ? 1 : 0);
        var unownedLegacy = pack.GeneratePackNewV2(1f, 2f, null, AAEmu.Game.Models.Game.Char.ActabilityType.None);
        var unownedFallback = pack.GeneratePackNewV2(1f, 2f, null, AAEmu.Game.Models.Game.Char.ActabilityType.None, null, null);
        await Assert.That(unownedFallback.SequenceEqual(unownedLegacy)).IsTrue();
        await Assert.That(unownedLegacy.Count(i => i.itemId == 8128)).IsEqualTo(1);
        var noRoll = pack.GeneratePackNewV2(0f, 2f, owner.Character, AAEmu.Game.Models.Game.Char.ActabilityType.None, null, _ => true);
        await Assert.That(noRoll.Count).IsEqualTo(0); // Eligibility never bypasses the roll.
    }

    private static Npc Spawn(HeadlessSession session, uint templateId, Vector3 position)
    {
        var npc = session.World.GetNpc(session.SpawnNpc(templateId))!;
        typeof(Transform).GetField("_instanceId", PrivateInstance)!.SetValue(npc.Transform, session.World.Id);
        typeof(GameObject).GetField("_parentWorld", PrivateInstance)!.SetValue(npc, session.World);
        npc.Template = new NpcTemplate { Id = templateId, Scale = 1f };
        npc.Level = 1;
        npc.Hp = npc.MaxHp = 100;
        npc.IsVisible = true;
        npc.Buffs = new Buffs(npc);
        npc.CharacterTagging = new Tagging(npc);
        npc.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position)!;
        region.AddObject(npc);
        npc.Region = region;
        return npc;
    }

    private sealed class CanonicalSources : IDisposable
    {
        private readonly FieldInfo _maps = typeof(ItemManager).GetField("_lootPackDroppingNpc", PrivateInstance)!;
        private readonly FieldInfo _packs = typeof(LootGameData).GetField("_lootPacks", PrivateInstance)!;
        private readonly object? _oldMaps;
        private readonly object? _oldPacks;
        private readonly uint _oldQuest;
        private readonly double _oldRate;
        public Dictionary<uint, List<LootPackDroppingNpc>> Mappings { get; } = [];

        public CanonicalSources(uint group = 0)
        {
            _oldMaps = _maps.GetValue(ItemManager.Instance);
            _oldPacks = _packs.GetValue(LootGameData.Instance);
            GameplayActorTestRig.SeedItemTemplate(8128);
            _oldQuest = ItemManager.Instance.GetTemplate(8128).LootQuestId;
            ItemManager.Instance.GetTemplate(8128).LootQuestId = 260; // Exact canonical item eligibility.
            _oldRate = AppConfiguration.Instance.World.LootRate;
            AppConfiguration.Instance.World.LootRate = 1;
            var packs = new Dictionary<uint, LootPack>();
            foreach (var s in new (uint Npc, uint Pack, uint Mapping, uint Loot)[]
                { (3484,4533,2096,5807), (3497,5746,17961,18068), (7675,5745,17960,18067) })
            {
                Mappings[s.Npc] = [new LootPackDroppingNpc
                    { Id = s.Mapping, NpcId = s.Npc, LootPackId = s.Pack, DefaultPack = true }];
                var loot = new Loot { Id = s.Loot, Group = group, ItemId = 8128, DropRate = 10_000_000,
                    MinAmount = 1, MaxAmount = 1, LootPackId = s.Pack, GradeId = 0, AlwaysDrop = false };
                packs[s.Pack] = new LootPack { Id = s.Pack, Loots = [loot],
                    LootsByGroupNo = new() { [group] = [loot] }, Groups = [], ActabilityGroups = [], GroupCount = 1 };
            }
            _maps.SetValue(ItemManager.Instance, Mappings);
            _packs.SetValue(LootGameData.Instance, packs);
            QuestFixtureRow.Invalidate(260);
        }

        public void Dispose()
        {
            _maps.SetValue(ItemManager.Instance, _oldMaps);
            _packs.SetValue(LootGameData.Instance, _oldPacks);
            ItemManager.Instance.GetTemplate(8128).LootQuestId = _oldQuest;
            AppConfiguration.Instance.World.LootRate = _oldRate;
            QuestFixtureRow.Invalidate(260);
        }
    }
}
