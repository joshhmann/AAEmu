using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// HIGH 3: observe opportunities while the actor is busy. A running Move leg
/// owns the actor (TryBegin busy-refuses every competing request), so the
/// listing's DiscoverQuests/DiscoverSelfQuests observations fail mid-leg.
/// The listing must carry that as explicit unknown — never as disappearance
/// or exhaustion — and the goal-owner module must hold its purpose (or deny
/// as unknown, never as exhausted) until an authoritative scan.
/// </summary>
[NotInParallel]
public class BotOpportunityListingBusyTests
{
    private static readonly Vector3 SpawnPos = new(32, 32, 0);
    private const uint OfferNpcTemplate = 93_740;
    private const uint OpenQuestId = 93_751;
    private const uint StartComponent = 93_761;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    [After(Test)]
    public void TearDown()
    {
        QuestLegActor.ClearAll();
        QuestLegActor.SetResolver(null);
    }

    private static (GameplayActor Actor, HeadlessSession Session) CreateAreaActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, SpawnPos);
        var region = session.World.GetRegionByPos(SpawnPos)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        actor.Character.Level = 3;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        return (actor, session);
    }

    private static void SpawnAreaNpc(HeadlessSession session, uint templateId, Vector3 pos)
    {
        var objId = session.SpawnNpc(templateId);
        var npc = session.World.GetNpc(objId)!;
        npc.TemplateId = templateId;
        npc.Template = new NpcTemplate { Id = templateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        GameplayActorTestRig.SetNpcPosition(session, objId, pos);
        var region = session.World.GetRegionByPos(pos)!;
        region.AddObject(npc);
        npc.Region = region;
    }

    private static ActorRequest StartOwnedMove(GameplayActor actor)
    {
        actor.BroadcastMovement = false;
        var leg = actor.MoveTo(SpawnPos + new Vector3(500, 0, 0), speed: 5f);
        return leg;
    }

    [Test]
    public async Task BusyObserve_DoesNotReplaceMeaningfulAuditWithKnownBusyRefusals()
    {
        var (actor, session) = CreateAreaActor("busy-observe-audit");
        GameplayActorTestRig.SeedQuestOffer(OpenQuestId, StartComponent, OfferNpcTemplate, level: 1);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        actor.BroadcastMovement = false;
        var leg = actor.MoveTo(SpawnPos + new Vector3(4, 0, 0), speed: 5f);
        await Assert.That(leg.IsTerminal).IsFalse();
        var before = actor.AuditTrace.Count;
        for (var observation = 0; observation < 32; observation++)
        {
            var listing = BotOpportunityListing.List(actor);
            await Assert.That(listing.QuestOffersUnknown).IsTrue();
            await Assert.That(actor.ActiveRequest).IsSameReferenceAs(leg);
        }
        await Assert.That(actor.AuditTrace.Count).IsEqualTo(before);
        for (var tick = 0; tick < 100 && !leg.IsTerminal; tick++)
            actor.Tick(TimeSpan.FromMilliseconds(100));
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Completed);
        var resumed = BotOpportunityListing.List(actor);
        await Assert.That(resumed.QuestOffersUnknown).IsFalse();
        await Assert.That(resumed.EligibleQuests.Any(q => q.QuestId == OpenQuestId)).IsTrue();
    }

    [Test]
    public async Task BusyObserve_KeepsOwnedMove_AndMarksOffersUnknown()
    {
        var (actor, session) = CreateAreaActor("busy-observe-unknown");
        GameplayActorTestRig.SeedQuestOffer(OpenQuestId, StartComponent, OfferNpcTemplate, level: 1);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        var leg = StartOwnedMove(actor);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(ReferenceEquals(actor.ActiveRequest, leg)).IsTrue();

        var listing = BotOpportunityListing.List(actor);

        // The exact owned request is untouched — observation never preempts.
        await Assert.That(ReferenceEquals(actor.ActiveRequest, leg)).IsTrue();
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);
        // Valid work is known-or-explicitly-unknown: the busy refusal is
        // flagged, never read as an empty (authoritative) eligible set.
        await Assert.That(listing.QuestOffersUnknown).IsTrue();
        await Assert.That(listing.CoverageValid).IsTrue();
    }

    [Test]
    public async Task BusyObserve_ModuleHoldsChosenPurpose_NoFalseAbandonment()
    {
        var (actor, session) = CreateAreaActor("busy-observe-hold");
        GameplayActorTestRig.SeedQuestOffer(OpenQuestId, StartComponent, OfferNpcTemplate, level: 1);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "busy-observe-hold");

        var first = module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });
        await Assert.That(first.CanActivate).IsTrue().Because($"why={first.WhyNot}");
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(OpenQuestId);

        // The quest leg dispatches its owned Move; the next wake observes
        // while busy — the purpose must hold, not abandon or re-pick.
        var leg = StartOwnedMove(actor);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);

        var again = module.CanActivate(new BotActivityContext
        {
            Bot = bot,
            GameHour = 12f,
            ActiveActivity = QuestBootstrapActivityModule.ActivityName
        });

        await Assert.That(ReferenceEquals(actor.ActiveRequest, leg)).IsTrue();
        await Assert.That(again.CanActivate).IsTrue().Because($"why={again.WhyNot}");
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(OpenQuestId);
    }

    [Test]
    public async Task BusyObserve_NoPurpose_EntersDiscovery_NeverAsExhausted()
    {
        var (actor, session) = CreateAreaActor("busy-observe-nopick");
        GameplayActorTestRig.SeedQuestOffer(OpenQuestId, StartComponent, OfferNpcTemplate, level: 1);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "busy-observe-nopick");

        // Busy from the first wake: no purpose yet, observation unknown.
        var leg = StartOwnedMove(actor);
        await Assert.That(leg.State).IsEqualTo(ActorLifecycleState.Running);

        var decision = module.CanActivate(new BotActivityContext { Bot = bot, GameHour = 12f });

        // Observation never preempts, and the unknown offers are answered with
        // the walk that produces an authoritative scan — holding for one here
        // would starve the owner forever behind a never-finishing patrol leg.
        await Assert.That(ReferenceEquals(actor.ActiveRequest, leg)).IsTrue();
        await Assert.That(decision.CanActivate).IsTrue().Because($"why={decision.WhyNot}");
        await Assert.That(decision.ActivityName).IsEqualTo(QuestBootstrapActivityModule.DiscoveryActivityName);
        await Assert.That(decision.WhyNot).IsNull();
        await Assert.That(decision.WhyNot?.Contains("exhaust")).IsNotEqualTo(true);
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(0u);
    }
}
