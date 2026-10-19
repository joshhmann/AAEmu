using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class QuestBootstrapRetainTests
{
    private static readonly Vector3 SeedPos = new(32, 32, 0);

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    [After(Test)]
    public void TearDown()
    {
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    private static void JoinRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        if (region != null)
        {
            region.AddObject(character);
            character.Region = region;
        }
    }

    private static void Place(GameplayActor actor, HeadlessSession session)
    {
        GameplayActorTestRig.SetPosition(actor, SeedPos);
        JoinRegion(session);
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        QuestLegActor.Stage(actor.Character.Id, actor);
    }

    private static void Accept(GameplayActor actor, uint questId, uint offerNpc, string cycle)
    {
        var req = actor.AcceptQuest(questId, QuestAcceptorType.Npc, offerNpc, cycle);
        if (req.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept {questId} failed ({req.State} {req.Detail})");
    }

    private static void SeedGatherShape(uint questId, uint startComp, uint progressComp, uint offerNpc, uint itemId,
        uint supplyComp = 0, uint supplyAct = 0)
    {
        GameplayActorTestRig.SeedQuestOffer(questId, startComp, offerNpc, level: 1);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        var template = questTemplates[questId];
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", flags)!.GetValue(manager)!;
        var progress = new QuestComponentTemplate(template) { Id = progressComp, KindId = QuestComponentKind.Progress };
        componentTemplates[progressComp] = progress;
        template.Components[progressComp] = progress;
        progress.ActTemplates.Add(new QuestActObjItemGather(progress)
        {
            ActId = progressComp, DetailId = progressComp, DetailType = nameof(QuestActObjItemGather),
            Count = 3, ItemId = itemId, ThisComponentObjectiveIndex = 0,
        });
        if (supplyComp != 0)
        {
            var supply = new QuestComponentTemplate(template) { Id = supplyComp, KindId = QuestComponentKind.Supply };
            componentTemplates[supplyComp] = supply;
            template.Components[supplyComp] = supply;
            supply.ActTemplates.Add(new QuestActSupplyItem(supply)
            {
                ActId = supplyAct, DetailId = supplyComp, DetailType = nameof(QuestActSupplyItem),
                Count = 1, ItemId = itemId,
            });
        }
        QuestFixtureRow.InvalidateAll();
    }

    private static (QuestBootstrapActivityModule Module, PlayerBotRuntime Bot) Wired(GameplayActor actor)
    {
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        return (module, new PlayerBotRuntime(actor.Character, "quest-retain-tests"));
    }

    private static BotActivityContext Ctx(PlayerBotRuntime bot) => new() { Bot = bot, GameHour = 12f };

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FreshActiveChoice_CreditedProgressBeatsMissingSource_RegardlessOfIds(bool swapIds)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("useful-active-progress");
        Place(actor, session);
        const uint lower = 93_151, higher = 93_152, giver = 93_153;
        var credited = swapIds ? lower : higher;
        var waiting = swapIds ? higher : lower;
        SeedGatherShape(waiting, 93_154, 93_155, giver, 93_158);
        SeedGatherShape(credited, 93_156, 93_157, giver, 93_159);
        Accept(actor, waiting, giver, "choice-waiting");
        Accept(actor, credited, giver, "choice-credited");
        // Explicit credited-objective fixture: choice proof, not earned credit.
        actor.Character.Quests.ActiveQuests[credited].Objectives[0] = 3;
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(q => q.QuestId == credited).Leg).IsEqualTo("advance");
        await Assert.That(listing.ActiveWork.Single(q => q.QuestId == waiting).Leg).IsEqualTo("wait");
        var (module, bot) = Wired(actor);
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        Console.WriteLine($"CHOICE active credited={credited} missing-source={waiting} chosen={module.GetChosenQuest(bot.CharacterId)}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(credited);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(credited)).IsFalse();
        // Relevant facts change, but a valid purpose is retained until released.
        actor.Character.Quests.ActiveQuests[credited].Objectives[0] = 0;
        actor.Character.Quests.ActiveQuests[waiting].Objectives[0] = 3;
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(credited);
        // Explicit terminal-feedback seam; not an earned live completion.
        module.NotifyQuestTerminal(bot.CharacterId, credited, completed: false);
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(waiting);
    }

    [Test]
    public async Task FreshActiveChoice_PerceivedTalkTargetBeatsMissingTarget_ThenRetainsPurpose()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("useful-active-source");
        Place(actor, session);
        const uint low = 99_101, high = 99_102, giver = 99_103, lowTarget = 99_104, highTarget = 99_105;
        GameplayActorTestRig.SeedQuestTalkObjective(low, 99_111, 99_112, giver, lowTarget, level: 1);
        GameplayActorTestRig.SeedQuestTalkObjective(high, 99_113, 99_114, giver, highTarget, level: 1);
        QuestFixtureRow.InvalidateAll();
        Accept(actor, low, giver, "source-low");
        Accept(actor, high, giver, "source-high");
        var targetId = session.SpawnNpc(highTarget);
        var target = session.World.GetNpc(targetId)!;
        target.TemplateId = highTarget;
        target.Hp = 100;
        GameplayActorTestRig.SetNpcPosition(session, targetId, SeedPos + new Vector3(2, 0, 0));
        actor.Character.Region.AddObject(target);
        target.Region = actor.Character.Region;
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(q => q.QuestId == high).Reason).EndsWith("-target-perceived");
        await Assert.That(listing.ActiveWork.Single(q => q.QuestId == low).Reason).EndsWith("-no-perceived-target");
        var (module, bot) = Wired(actor);
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(high);
        GameplayActorTestRig.SetNpcPosition(session, targetId, SeedPos + new Vector3(100, 0, 0));
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(high);
    }

    [Test]
    public async Task AcceptedTalkQuest_NoOfferNoPrey_RemainsPicked_LegTalk()
    {
        const uint q = 93_101; const uint start = 93_111; const uint prog = 93_112;
        const uint offer = 93_121; const uint talkNpc = 93_122;
        var (actor, session) = GameplayActorTestRig.CreateActor("retain-talk");
        Place(actor, session);
        GameplayActorTestRig.SeedQuestTalkObjective(q, start, prog, offer, talkNpc, level: 1);
        QuestFixtureRow.InvalidateAll();
        Accept(actor, q, offer, "retain-talk-1");

        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.EligibleQuests.Any(x => x.QuestId == q)).IsFalse();
        await Assert.That(listing.ViableHunts.Any(h => h.QuestId == q)).IsFalse();
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("talk");

        var (module, bot) = Wired(actor);
        var first = module.CanActivate(Ctx(bot));
        var second = module.CanActivate(Ctx(bot));
        await Assert.That(first.CanActivate).IsTrue().Because($"why={first.WhyNot}");
        await Assert.That(second.CanActivate).IsTrue().Because($"why={second.WhyNot}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);
    }

    [Test]
    public async Task HeldQuest_BotIdleInOneSpot_GivenUpAfterIdleLimit_UntilItMoves()
    {
        // Populated-area run 6: a bot held one quest for 435 s at one spot
        // with no leg and no action. The hold now ends at the idle limit.
        const uint q = 93_131; const uint start = 93_141; const uint prog = 93_142;
        const uint offer = 93_151; const uint talkNpc = 93_152;
        var (actor, session) = GameplayActorTestRig.CreateActor("retain-idle");
        Place(actor, session);
        GameplayActorTestRig.SeedQuestTalkObjective(q, start, prog, offer, talkNpc, level: 1);
        QuestFixtureRow.InvalidateAll();
        Accept(actor, q, offer, "retain-idle-1");

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var module = new QuestBootstrapActivityModule(
            new QuestBootstrapModuleOptions { Enabled = true, IdleGiveUpAfter = TimeSpan.FromMinutes(3) },
            time: clock);
        var bot = new PlayerBotRuntime(actor.Character, "quest-retain-tests");

        // Wakes every 5 s for this many seconds, each one still holding the quest.
        async Task HoldFor(int seconds)
        {
            for (var t = 0; t < seconds; t += 5)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                var held = module.CanActivate(Ctx(bot));
                await Assert.That(held.CanActivate).IsTrue().Because($"why={held.WhyNot}");
            }
        }

        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await HoldFor(170);

        // Moving restarts the watch: the bot was doing something for the quest.
        GameplayActorTestRig.SetPosition(actor, SeedPos + new Vector3(5, 0, 0));
        await HoldFor(170);

        // Four minutes when the module was not asked (a fight, a rest) is not
        // idle time: the first hold afterwards restarts the watch.
        clock.Advance(TimeSpan.FromMinutes(4));
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await HoldFor(175);

        clock.Advance(TimeSpan.FromSeconds(10));
        var givenUp = module.CanActivate(Ctx(bot));
        await Assert.That(givenUp.CanActivate).IsFalse();
        await Assert.That(givenUp.WhyNot!).Contains($"gave up on quest {q}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
        // The quest stays accepted on the character; only the module lets go.
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(q)).IsTrue();

        var next = module.CanActivate(Ctx(bot));
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsNotEqualTo(q)
            .Because($"allowed={next.CanActivate} activity={next.ActivityName} why={next.WhyNot}");
    }

    [Test]
    public async Task AcceptedDeliveryQuest_NoOfferNoPrey_RemainsPicked_LegDelivery()
    {
        const uint q = 93_102; const uint start = 93_113; const uint prog = 93_114;
        const uint supplyComp = 93_115; const uint supplyAct = 93_116;
        const uint offer = 93_123; const uint item = 93_131;
        var (actor, session) = GameplayActorTestRig.CreateActor("retain-deliver");
        Place(actor, session);
        SeedGatherShape(q, start, prog, offer, item, supplyComp, supplyAct);
        Accept(actor, q, offer, "retain-deliver-1");

        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ViableHunts.Any(h => h.QuestId == q)).IsFalse();
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("delivery");

        var (module, bot) = Wired(actor);
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        var second = module.CanActivate(Ctx(bot));
        await Assert.That(second.CanActivate).IsTrue().Because($"why={second.WhyNot}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);
    }

    [Test]
    public async Task AcceptedGatherQuest_WithItem_NoPrey_RemainsPicked_LegGather()
    {
        const uint q = 93_103; const uint start = 93_117; const uint prog = 93_118;
        const uint offer = 93_124; const uint item = 93_132;
        var (actor, session) = GameplayActorTestRig.CreateActor("retain-gather");
        Place(actor, session);
        SeedGatherShape(q, start, prog, offer, item);
        Accept(actor, q, offer, "retain-gather-1");
        GameplayActorTestRig.GiveBagItem(actor, item, 1);

        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("gather");

        var (module, bot) = Wired(actor);
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);
    }

    [Test]
    public async Task AcceptedGatherQuest_NoItem_Waits_LegWait()
    {
        const uint q = 93_104; const uint start = 93_119; const uint prog = 93_120;
        const uint offer = 93_125; const uint item = 93_133;
        var (actor, session) = GameplayActorTestRig.CreateActor("retain-wait");
        Place(actor, session);
        SeedGatherShape(q, start, prog, offer, item);
        Accept(actor, q, offer, "retain-wait-1");

        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("wait");

        var (module, bot) = Wired(actor);
        var decision = module.CanActivate(Ctx(bot));
        await Assert.That(decision.CanActivate).IsTrue().Because($"why={decision.WhyNot}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);
    }

    [Test]
    public async Task ReadyQuest_NoOfferNoPrey_RemainsPicked_LegReturn()
    {
        const uint q = 93_105; const uint start = 93_141; const uint ready = 93_142;
        const uint offer = 93_126; const uint reporter = 93_127;
        var (actor, session) = GameplayActorTestRig.CreateActor("retain-return");
        Place(actor, session);
        GameplayActorTestRig.SeedQuestDelivery(q, start, ready, offer, reporter, level: 1);
        QuestFixtureRow.InvalidateAll();
        Accept(actor, q, offer, "retain-return-1");
        actor.Character.Quests.ActiveQuests[q]!.Status = QuestStatus.Ready;

        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.ActiveWork.Single(w => w.QuestId == q).Leg).IsEqualTo("return");

        var (module, bot) = Wired(actor);
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        var second = module.CanActivate(Ctx(bot));
        await Assert.That(second.CanActivate).IsTrue().Because($"why={second.WhyNot}");
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);
    }

    [Test]
    public async Task FailedQuest_Releases_WithNamedBlocker()
    {
        const uint q = 93_106; const uint start = 93_143; const uint prog = 93_144;
        const uint offer = 93_128; const uint talkNpc = 93_129;
        var (actor, session) = GameplayActorTestRig.CreateActor("retain-blocked");
        Place(actor, session);
        GameplayActorTestRig.SeedQuestTalkObjective(q, start, prog, offer, talkNpc, level: 1);
        QuestFixtureRow.InvalidateAll();
        Accept(actor, q, offer, "retain-blocked-1");

        var (module, bot) = Wired(actor);
        await Assert.That(module.CanActivate(Ctx(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(q);

        actor.Character.Quests.ActiveQuests[q]!.Status = QuestStatus.Failed;
        var listing = BotOpportunityListing.List(actor);
        var blocker = listing.BlockedWork.Single(b => b.QuestId == q).Blocker;
        await Assert.That(string.IsNullOrWhiteSpace(blocker)).IsFalse();

        var decision = module.CanActivate(Ctx(bot));
        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(decision.WhyNot!).Contains(blocker);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(0u);
    }
}
