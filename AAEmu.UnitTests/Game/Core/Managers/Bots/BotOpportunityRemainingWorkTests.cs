using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Remaining-work observation: the listing must name what REMAINS, not the
/// first objective. Fixture-seeded (deterministic): real template tables +
/// real engine credit events (OnItemGather / DoOnMonsterHuntEvents / Talk),
/// real accept, the existing owner (QuestBootstrapActivityModule.CanActivate)
/// and the existing execution path (QuestDirector.Run). No completion
/// notification is faked: every credited objective flows through the engine.
/// </summary>
[NotInParallel]
public class BotOpportunityRemainingWorkTests
{
    private static readonly Vector3 SpawnPos = new(32, 32, 0);
    private object? _previousQuestManager;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        var questField = typeof(Singleton<QuestManager>).GetField("s_instance",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        _previousQuestManager = questField.GetValue(null);
        questField.SetValue(null, new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object));
        GameplayActorTestRig.Seed();
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
    }

    [After(Test)]
    public void TearDown()
    {
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
        QuestBehavior.ClearCombatStallMemory();
        CombatBrainEngagement.ClearAll();
        typeof(Singleton<QuestManager>).GetField("s_instance",
            BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, _previousQuestManager);
    }

    private static (GameplayActor Actor, HeadlessSession Session) CreateAreaActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, SpawnPos);
        var region = session.World.GetRegionByPos(SpawnPos)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        QuestLegActor.Stage(actor.Character.Id, actor);
        return (actor, session);
    }

    private static uint SpawnAreaNpc(HeadlessSession session, uint templateId, Vector3 pos)
    {
        var objId = session.SpawnNpc(templateId);
        var npc = session.World.GetNpc(objId)!;
        npc.TemplateId = templateId;
        npc.Template = new NpcTemplate { Id = templateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, objId, pos);
        var region = session.World.GetRegionByPos(pos)!;
        region.AddObject(npc);
        npc.Region = region;
        return objId;
    }

    private static void SeedMixedQuest(uint questId, uint startComp, uint progressComp, uint readyComp,
        uint offerNpc, uint huntPrey, int huntCount, uint gatherItem, int gatherCount, bool huntFirst)
    {
        // The live quest instance snapshots ActTemplates at accept time
        // (QuestComponent ctor) and subscribes on step entry — so the FULL
        // Progress shape must be seeded BEFORE AcceptQuest runs.
        GameplayActorTestRig.SeedQuestOffer(questId, startComp, offerNpc, level: 1);
        SeedProgressPair(questId, progressComp, readyComp, huntPrey, huntCount, gatherItem, gatherCount, huntFirst);
    }

    private static void SeedProgressPair(uint questId, uint progressComp, uint readyComp,
        uint huntPrey, int huntCount, uint gatherItem, int gatherCount, bool huntFirst)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        var template = questTemplates[questId];
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(progressComp, out var progress))
        {
            progress = new QuestComponentTemplate(template) { Id = progressComp, KindId = QuestComponentKind.Progress };
            componentTemplates[progressComp] = progress;
        }
        if (!template.Components.ContainsKey(progressComp))
            template.Components[progressComp] = progress;
        var huntActId = huntFirst ? progressComp : progressComp + 1;
        var gatherActId = huntFirst ? progressComp + 1 : progressComp;
        QuestActObjMonsterHunt? huntAct = null;
        QuestActObjItemGather? gatherAct = null;
        if (!progress.ActTemplates.OfType<QuestActObjMonsterHunt>().Any(a => a.NpcId == huntPrey))
        {
            huntAct = new QuestActObjMonsterHunt(progress)
            {
                DetailId = progressComp, ActId = huntActId, NpcId = huntPrey,
                Count = huntCount, ThisComponentObjectiveIndex = (byte)(huntFirst ? 0 : 1),
            };
            progress.ActTemplates.Add(huntAct);
        }
        if (!progress.ActTemplates.OfType<QuestActObjItemGather>().Any(a => a.ItemId == gatherItem))
        {
            gatherAct = new QuestActObjItemGather(progress)
            {
                DetailId = progressComp, ActId = gatherActId, ItemId = gatherItem,
                Count = gatherCount, ThisComponentObjectiveIndex = (byte)(huntFirst ? 1 : 0),
            };
            progress.ActTemplates.Add(gatherAct);
        }
        // Loader parity (AddActTemplate): the engine's live event fan-out
        // resolves acts through _actTemplatesByDetailType, so the seeded acts
        // must be registered there under the loader's own key.
        var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
            .GetField("_actTemplatesByDetailType", flags)!.GetValue(manager)!;
        foreach (var act in new QuestActTemplate?[] { huntAct, gatherAct })
        {
            if (act == null)
                continue;
            if (!actsByType.TryGetValue(act.GetType().Name, out var byDetail))
            {
                byDetail = [];
                actsByType[act.GetType().Name] = byDetail;
            }
            byDetail[act.DetailId] = act;
        }
        if (!componentTemplates.TryGetValue(readyComp, out var ready))
        {
            ready = new QuestComponentTemplate(template) { Id = readyComp, KindId = QuestComponentKind.Ready };
            componentTemplates[readyComp] = ready;
        }
        if (!template.Components.ContainsKey(readyComp))
            template.Components[readyComp] = ready;
        QuestFixtureRow.InvalidateAll();
    }

    private static void CreditHunt(GameplayActor actor, Npc prey, int times)
    {
        for (var i = 0; i < times; i++)
            QuestManager.Instance.DoOnMonsterHuntEvents(actor.Character, prey);
    }

    private static void CreditGather(GameplayActor actor, uint questId, uint itemId, int count)
    {
        GameplayActorTestRig.GiveBagItem(actor, itemId, count);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = questId, ItemId = itemId, Count = count,
        });
    }

    [Test]
    public async Task HuntCreditedGatherUnfinished_ListsGatherNotAdvance_HuntFirst()
    {
        const uint q = 94_101; const uint start = 94_111; const uint prog = 94_112; const uint ready = 94_113;
        const uint offer = 94_121; const uint prey = 94_122; const uint gatherItem = 94_131;
        var (actor, session) = CreateAreaActor("remain-hunt-first");
        SeedMixedQuest(q, start, prog, ready, offer, prey, huntCount: 2, gatherItem, gatherCount: 3, huntFirst: true);
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(q, QuestAcceptorType.Npc, offer, "remain-hunt-first-1");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var preyNpc = session.World.GetNpc(SpawnAreaNpc(session, prey, SpawnPos + new Vector3(8, 0, 0)))!;
        CreditHunt(actor, preyNpc, times: 2);

        var listing = BotOpportunityListing.List(actor);

        var work = listing.ActiveWork.Single(w => w.QuestId == q);
        await Assert.That(work.Leg == "gather" || work.Leg == "wait").IsTrue()
            .Because($"hunt credited but gathering unfinished: the REMAINING gathering work must surface, never advance-ready (got leg={work.Leg} reason={work.Reason})");
        await Assert.That(work.Reason).Contains(gatherItem.ToString());
    }

    [Test]
    public async Task GatherCreditedHuntUnfinished_ListsHuntNotAdvance_GatherFirst()
    {
        const uint q = 94_102; const uint start = 94_114; const uint prog = 94_115; const uint ready = 94_116;
        const uint offer = 94_123; const uint prey = 94_124; const uint gatherItem = 94_132;
        var (actor, session) = CreateAreaActor("remain-gather-first");
        SeedMixedQuest(q, start, prog, ready, offer, prey, huntCount: 2, gatherItem, gatherCount: 2, huntFirst: false);
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(q, QuestAcceptorType.Npc, offer, "remain-gather-first-1");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        CreditGather(actor, q, gatherItem, count: 2);
        SpawnAreaNpc(session, prey, SpawnPos + new Vector3(8, 0, 0));

        var quest = actor.Character.Quests.ActiveQuests[q]!;
        await Assert.That(quest.Objectives[0]).IsEqualTo(2)
            .Because("the gather act (index 0, gather-first) must carry its credit through the real engine event");

        var listing = BotOpportunityListing.List(actor);

        var work = listing.ActiveWork.Single(w => w.QuestId == q);
        await Assert.That(work.Leg).IsEqualTo("hunt")
            .Because($"reversed ordering: gathering credited but hunt unfinished must surface the hunt, never advance-ready (objectives=[{string.Join(",", quest.Objectives)}] reason={work.Reason})");
        await Assert.That(work.Reason).Contains(prey.ToString());
    }

    [Test]
    public async Task TalkObjectiveWithoutTarget_KeepsTalkWork_OwnerHoldsPurpose()
    {
        const uint q = 94_103; const uint start = 94_117; const uint prog = 94_118;
        const uint offer = 94_125; const uint talkNpc = 94_126;
        var (actor, session) = CreateAreaActor("remain-talk-missing");
        GameplayActorTestRig.SeedQuestTalkObjective(q, start, prog, offer, talkNpc, level: 1);
        QuestFixtureRow.InvalidateAll();
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(q, QuestAcceptorType.Npc, offer, "remain-talk-missing-1");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var listing = BotOpportunityListing.List(actor);

        // The talk target is nowhere in the census: the listing keeps talk work
        // with the TRUE condition named (no perceived target), and reports no
        // blocker for it (absence is not abandonment).
        var work = listing.ActiveWork.Single(w => w.QuestId == q);
        await Assert.That(work.Leg).IsEqualTo("talk");
        await Assert.That(work.Reason).Contains("no-perceived-target");
        await Assert.That(listing.BlockedWork.Any(b => b.QuestId == q)).IsFalse();

        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "remain-talk-owner");
        var first = module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });
        await Assert.That(first.CanActivate).IsTrue().Because($"why={first.WhyNot}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);
    }

    [Test]
    public async Task TalkObjectiveWithTarget_ReportsPerceived_AndExecutionServesIt()
    {
        const uint q = 94_104; const uint start = 94_119; const uint prog = 94_120;
        const uint offer = 94_127; const uint talkNpc = 94_128;
        var (actor, session) = CreateAreaActor("remain-talk-perceived");
        GameplayActorTestRig.SeedQuestTalkObjective(q, start, prog, offer, talkNpc, level: 1);
        QuestFixtureRow.InvalidateAll();
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(q, QuestAcceptorType.Npc, offer, "remain-talk-perceived-1");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var targetObjId = SpawnAreaNpc(session, talkNpc, SpawnPos + new Vector3(6, 0, 0));

        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("talk");
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Reason).Contains("target-perceived");

        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "remain-talk-owner");
        await Assert.That(module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f }).CanActivate).IsTrue();
        var runtime = new BotBehaviorRuntime();
        for (var wake = 0; wake < 6 && !actor.Character.Quests.HasQuestCompleted(q); wake++)
            runtime.Tick(actor, (_, _) => [], new QuestDecisionScenario.QuestOptions
            { CycleId = $"remain-talk-{wake}", ChosenQuestId = module.GetChosenQuest(bot.CharacterId) }, QuestBootstrapActivityModule.ActivityName);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Talk && r.TargetId == targetObjId
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(q)).IsTrue();
    }

    [Test]
    public async Task ReadyQuest_KeepsReturnWork_AfterOfferAndPreyDisappear()
    {
        const uint q = 94_105; const uint start = 94_141; const uint ready = 94_142;
        const uint offer = 94_129; const uint reporter = 94_130;
        var (actor, session) = CreateAreaActor("remain-ready");
        GameplayActorTestRig.SeedQuestDelivery(q, start, ready, offer, reporter, level: 1);
        QuestFixtureRow.InvalidateAll();
        var offerObjId = SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(q, QuestAcceptorType.Npc, offer, "remain-ready-1");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        actor.Character.Quests.ActiveQuests[q]!.Status = QuestStatus.Ready;
        session.World.RemoveObject(session.World.GetNpc(offerObjId)!);

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.EligibleQuests.Any(x => x.QuestId == q)).IsFalse();
        await Assert.That(listing.ViableHunts.Any(h => h.QuestId == q)).IsFalse();
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("return");

        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "remain-ready-owner");
        var decision = module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });
        await Assert.That(decision.CanActivate).IsTrue().Because($"why={decision.WhyNot}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);
        SpawnAreaNpc(session, reporter, SpawnPos + new Vector3(5, 0, 0));
        var runtime = new BotBehaviorRuntime();
        for (var wake = 0; wake < 8 && !actor.Character.Quests.HasQuestCompleted(q); wake++)
            runtime.Tick(actor, (_, _) => [], new QuestDecisionScenario.QuestOptions
            { CycleId = $"remain-ready-{wake}", ChosenQuestId = module.GetChosenQuest(bot.CharacterId) }, QuestBootstrapActivityModule.ActivityName);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.TurnInQuest
            && r.TargetId == q && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(q)).IsTrue();
    }

    [Test]
    public async Task TwoQuests_FirstObjectiveDone_NeitherHidesRemainingNorPins()
    {
        // Quest A: hunt credited, gathering unfinished (kept gather/wait work).
        const uint qa = 94_106; const uint sa = 94_143; const uint pa = 94_144; const uint ra = 94_145;
        const uint offerA = 94_151; const uint preyA = 94_152; const uint itemA = 94_153;
        // Quest B: unsupported craft objective (genuinely stuck — executor
        // cannot perform it), seeded additively on the same live template set.
        const uint qb = 94_107; const uint sb = 94_146; const uint pb = 94_147;
        const uint offerB = 94_154;
        var (actor, session) = CreateAreaActor("remain-two");
        SeedMixedQuest(qa, sa, pa, ra, offerA, preyA, huntCount: 1, itemA, gatherCount: 2, huntFirst: true);
        SeedUnsupportedCraft(qb, sb, pb, offerB);
        SpawnAreaNpc(session, offerA, SpawnPos + new Vector3(5, 0, 0));
        SpawnAreaNpc(session, offerB, SpawnPos + new Vector3(6, 0, 0));
        await Assert.That(actor.AcceptQuest(qa, QuestAcceptorType.Npc, offerA, "remain-two-a").State)
            .IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.AcceptQuest(qb, QuestAcceptorType.Npc, offerB, "remain-two-b").State)
            .IsEqualTo(ActorLifecycleState.Completed);
        var preyNpc = session.World.GetNpc(SpawnAreaNpc(session, preyA, SpawnPos + new Vector3(8, 0, 0)))!;
        CreditHunt(actor, preyNpc, times: 1);

        var listing = BotOpportunityListing.List(actor);

        // A keeps its REMAINING gather/wait work (not advance); B surfaces its
        // concrete limitation (unsupported craft act named).
        var workA = listing.ActiveWork.Single(w => w.QuestId == qa);
        await Assert.That(workA.Leg == "advance").IsFalse();
        await Assert.That(workA.Reason).Contains(itemA.ToString());
        var blockerB = listing.BlockedWork.Single(b => b.QuestId == qb).Blocker;
        await Assert.That(blockerB).Contains("QuestActObjCraft");

        // The owner picks the quest with remaining performable work (A), never
        // the stuck quest (B) — proven through the existing pick path.
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "remain-two-owner");
        var decision = module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });
        await Assert.That(decision.CanActivate).IsTrue().Because($"why={decision.WhyNot}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(qa);
    }

    [Test]
    public async Task UseItemWithoutItem_SurfacesMissingItem_AndNoUsableWork()
    {
        const uint q = 94_108; const uint start = 94_148; const uint prog = 94_149;
        const uint offer = 94_155; const uint item = 94_156;
        var (actor, session) = CreateAreaActor("remain-useitem-missing");
        SeedUseItemQuest(q, start, prog, offer, item, need: 1);
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(q, QuestAcceptorType.Npc, offer, "remain-useitem-1");
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.ActiveWork.Any(w => w.QuestId == q)).IsFalse();
        var blocker = listing.BlockedWork.Single(b => b.QuestId == q).Blocker;
        await Assert.That(blocker).Contains(item.ToString());

        // The owner denies (no actionable work: not active, not offered, not
        // hunted) and releases with the listing's no-actionable-work reason —
        // never pins the bot to work the executor cannot perform.
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "remain-useitem-owner");
        var decision = module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });
        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
    }

    [Test]
    public async Task SelectiveMissingTalkTarget_RemainsTalk_NotAdvance()
    {
        const uint q = 94_201, start = 94_211, progress = 94_212, offer = 94_221, target = 94_222;
        var (actor, session) = CreateAreaActor("selective-missing");
        GameplayActorTestRig.SeedQuestTalkObjective(q, start, progress, offer, target, level: 1);
        QuestManager.Instance.GetTemplate(q).Selective = true;
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        await Assert.That(actor.AcceptQuest(q, QuestAcceptorType.Npc, offer).State).IsEqualTo(ActorLifecycleState.Completed);
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("talk");
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Reason).Contains("no-perceived-target");
        await Assert.That(actor.AdvanceQuest(q).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests[q].Status).IsEqualTo(QuestStatus.Progress);
    }

    [Test]
    public async Task SelectiveCreditedComponent_IgnoresUnsupportedUnusedAlternative()
    {
        const uint q = 94_202, start = 94_213, progress = 94_214, ready = 94_215,
            alternative = 94_216, offer = 94_223, prey = 94_224, reporter = 94_225;
        var (actor, session) = CreateAreaActor("selective-credited");
        GameplayActorTestRig.SeedQuestHunt(q, start, progress, ready, offer, prey, reporter, huntCount: 1, level: 1);
        SeedUnsupportedCraft(q, start, alternative, offer);
        var template = QuestManager.Instance.GetTemplate(q);
        template.Selective = true;
        template.Components[alternative].ActTemplates.Single().ThisComponentObjectiveIndex = 1;
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        await Assert.That(actor.AcceptQuest(q, QuestAcceptorType.Npc, offer).State).IsEqualTo(ActorLifecycleState.Completed);
        var npc = session.World.GetNpc(SpawnAreaNpc(session, prey, SpawnPos + new Vector3(8, 0, 0)))!;
        CreditHunt(actor, npc, 1);
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("advance");
        await Assert.That(listing.BlockedWork.Any(w => w.QuestId == q)).IsFalse();
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "selective-credited-owner");
        await Assert.That(module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f }).CanActivate).IsTrue();
        await Assert.That(actor.AdvanceQuest(q).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests[q].Status).IsEqualTo(QuestStatus.Ready);
    }

    [Test]
    public async Task SelectiveUsableAlternative_BotTalksDespiteUnsupportedComponent()
    {
        const uint q = 94_205, start = 94_251, unsupported = 94_252, progress = 94_253,
            offer = 94_261, target = 94_262;
        var (actor, session) = CreateAreaActor("selective-usable");
        GameplayActorTestRig.SeedQuestTalkObjective(q, start, progress, offer, target, level: 1);
        SeedUnsupportedCraft(q, start, unsupported, offer);
        var template = QuestManager.Instance.GetTemplate(q);
        template.Selective = true;
        template.Components[unsupported].ActTemplates.Single().ThisComponentObjectiveIndex = 1;
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        var talkTarget = SpawnAreaNpc(session, target, SpawnPos + new Vector3(6, 0, 0));
        await Assert.That(actor.AcceptQuest(q, QuestAcceptorType.Npc, offer).State).IsEqualTo(ActorLifecycleState.Completed);
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("talk");
        await Assert.That(listing.BlockedWork.Any(w => w.QuestId == q)).IsFalse();
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "selective-usable-owner");
        await Assert.That(module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f }).CanActivate).IsTrue();
        var runtime = new BotBehaviorRuntime();
        for (var wake = 0; wake < 6 && !actor.Character.Quests.HasQuestCompleted(q); wake++)
            runtime.Tick(actor, (_, _) => [], new QuestDecisionScenario.QuestOptions
            { CycleId = $"selective-usable-{wake}", ChosenQuestId = module.GetChosenQuest(bot.CharacterId) }, QuestBootstrapActivityModule.ActivityName);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Talk && r.TargetId == talkTarget
            && r.Result == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(actor.Character.Quests.HasQuestCompleted(q)).IsTrue();
    }

    [Test]
    public async Task SelectiveComponent_TwoActs_RequiresBothCredits()
    {
        const uint q = 94_203, start = 94_217, progress = 94_218, ready = 94_219,
            offer = 94_226, prey = 94_227, item = 94_228;
        var (actor, session) = CreateAreaActor("selective-and");
        SeedMixedQuest(q, start, progress, ready, offer, prey, 1, item, 1, huntFirst: true);
        QuestManager.Instance.GetTemplate(q).Selective = true;
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        await Assert.That(actor.AcceptQuest(q, QuestAcceptorType.Npc, offer).State).IsEqualTo(ActorLifecycleState.Completed);
        CreditHunt(actor, session.World.GetNpc(SpawnAreaNpc(session, prey, SpawnPos + new Vector3(8, 0, 0)))!, 1);
        await Assert.That(BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == q).Leg == "advance").IsFalse();
        await Assert.That(actor.AdvanceQuest(q).State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests[q].Status).IsEqualTo(QuestStatus.Progress);
        CreditGather(actor, q, item, 1);
        await Assert.That(BotOpportunityListing.List(actor).ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("advance");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MixedQuest_BotExecutesUnfinishedGather_RegardlessOfObjectiveOrder(bool huntFirst)
    {
        const uint q = 94_204, start = 94_231, progress = 94_232, ready = 94_234,
            offer = 94_241, prey = 94_242, item = 94_243, doodad = 94_244, group = 94_245, func = 94_246;
        var (actor, session) = CreateAreaActor("mixed-exec-" + huntFirst);
        SeedMixedQuest(q, start, progress, ready, offer, prey, 1, item, 1, huntFirst);
        SpawnAreaNpc(session, offer, SpawnPos + new Vector3(5, 0, 0));
        await Assert.That(actor.AcceptQuest(q, QuestAcceptorType.Npc, offer).State).IsEqualTo(ActorLifecycleState.Completed);
        CreditHunt(actor, session.World.GetNpc(SpawnAreaNpc(session, prey, SpawnPos + new Vector3(8, 0, 0)))!, 1);
        // Both canonical source surfaces must exist, including an empty loot
        // pack catalog for this direct-item doodad source.
        _ = LootGameData.Instance;
        GameplayActorTestRig.SeedItemTemplate(item);
        var source = GameplayActorTestRig.SpawnGatherDoodad(session, doodad, group, func, item, SpawnPos + new Vector3(2, 0, 0));
        QuestFixtureRow.Invalidate(q);
        var gatherAct = QuestManager.Instance.GetTemplate(q).GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).OfType<QuestActObjItemGather>().Single();
        await Assert.That(QuestFixtureRow.FromQuestData(q, gatherAct).GatherDoodadTemplate).IsEqualTo(doodad);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "mixed-exec-owner");
        await Assert.That(module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f }).CanActivate).IsTrue();
        var runtime = new BotBehaviorRuntime();
        for (var wake = 0; wake < 6 && actor.Character.Inventory.GetItemsCount(item) == 0; wake++)
            runtime.Tick(actor, (_, _) => [], new QuestDecisionScenario.QuestOptions
            { CycleId = $"mixed-exec-{wake}", ChosenQuestId = module.GetChosenQuest(bot.CharacterId) }, QuestBootstrapActivityModule.ActivityName);
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Interact && r.TargetId == source
            && r.Result == ActorLifecycleState.Completed)).IsTrue().Because($"bot must select and dispatch gathering, not a direct test call: {runtime.LastResult?.FailReason}");
        await Assert.That(actor.Character.Inventory.GetItemsCount(item)).IsEqualTo(1);
        var gather = QuestManager.Instance.GetTemplate(q).GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates).OfType<QuestActObjItemGather>().Single();
        await Assert.That(gather.GetObjective(actor.Character.Quests.ActiveQuests[q])).IsEqualTo(1);
    }

    private static void SeedUnsupportedCraft(uint questId, uint startComp, uint progressComp, uint offerNpc)
    {
        GameplayActorTestRig.SeedQuestOffer(questId, startComp, offerNpc, level: 1);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        var template = questTemplates[questId];
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(progressComp, out var progress))
        {
            progress = new QuestComponentTemplate(template) { Id = progressComp, KindId = QuestComponentKind.Progress };
            componentTemplates[progressComp] = progress;
        }
        if (!template.Components.ContainsKey(progressComp))
            template.Components[progressComp] = progress;
        if (!progress.ActTemplates.OfType<QuestActObjCraft>().Any())
        {
            progress.ActTemplates.Add(new QuestActObjCraft(progress)
            {
                DetailId = progressComp, ActId = progressComp, CraftId = 94_199,
                Count = 1, ThisComponentObjectiveIndex = 0,
            });
        }
        QuestFixtureRow.InvalidateAll();
    }

    private static void SeedUseItemQuest(uint questId, uint startComp, uint progressComp, uint offerNpc, uint itemId, int need)
    {
        GameplayActorTestRig.SeedQuestOffer(questId, startComp, offerNpc, level: 1);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        var template = questTemplates[questId];
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(progressComp, out var progress))
        {
            progress = new QuestComponentTemplate(template) { Id = progressComp, KindId = QuestComponentKind.Progress };
            componentTemplates[progressComp] = progress;
        }
        if (!template.Components.ContainsKey(progressComp))
            template.Components[progressComp] = progress;
        if (!progress.ActTemplates.OfType<QuestActObjItemUse>().Any(a => a.ItemId == itemId))
        {
            progress.ActTemplates.Add(new QuestActObjItemUse(progress)
            {
                DetailId = progressComp, ActId = progressComp, ItemId = itemId,
                Count = need, ThisComponentObjectiveIndex = 0,
            });
        }
        QuestFixtureRow.InvalidateAll();
    }
}
