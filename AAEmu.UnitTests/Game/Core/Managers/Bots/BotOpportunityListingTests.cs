using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Opportunity-listing tests: a read-only path that tells the bot what it can
/// actually try in one supported area — eligible quest work from actual quest
/// state (not fixtures), nearby hunting from actual spawn/population state
/// (not coordinates from a list), and normal recovery from actual vitals.
///
/// Supported area: the Solzreed starter corridor around the natural spawn
/// (SolzreedStarterCorridorProfile.StarterSpawnPosition) — the smallest area
/// with real quest + hunting + recovery: the field-test spawn (E-48), quest
/// NPCs within interaction range, and wildlife prey in the same census. The
/// tests seed the area's quest/NPC shapes (offer linkage, hunt objective,
/// wildlife faction) and place them at starter-corridor positions; the listing
/// itself reads only live census + quest state + vitals.
///
/// Unknown stays unknown: an unscanned resource is never marked exhausted,
/// and an ineligible quest is never listed.
/// </summary>
[NotInParallel]
public class BotOpportunityListingTests
{
    // Area shapes (starter-corridor RELATIVE layout; decisions never reference them).
    // NOTE: the headless rig world is a small 2x2-cell region graph, so the area is
    // staged at the rig-world origin with the starter corridor's relative shape
    // (offerer 5 m east, prey 8 m east of the actor — the same layout as the quest
    // NPCs around the natural spawn, translated into valid rig region bounds).
    // Real coordinates live in SolzreedStarterCorridorProfile; what matters here is
    // that offerer and prey share the actor's bounded census.
    private static readonly Vector3 SpawnPos = new(32, 32, 0);
    private const uint OfferNpcTemplate = 90_740;
    private const uint PreyTemplate = 90_741;
    private const uint UnrelatedNpcTemplate = 90_742;
    private const uint GatedQuestId = 90_750;
    private const uint OpenQuestId = 90_751;
    private const uint HuntQuestId = 90_752;
    private const uint StartComponent = 90_760;
    private const uint GatedStartComponent = 90_761;
    private const uint HuntStartComponent = 90_762;
    private const uint HuntProgressComponent = 90_763;
    private const uint HuntReadyComponent = 90_764;
    private const uint ReportNpcTemplate = 90_743;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
    }

    private static (GameplayActor Actor, HeadlessSession Session) CreateAreaActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, SpawnPos);
        var region = session.World.GetRegionByPos(SpawnPos)!;
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        return (actor, session);
    }

    private static uint SpawnAreaNpc(HeadlessSession session, GameplayActor actor, uint templateId, Vector3 pos)
    {
        var objId = session.SpawnNpc(templateId);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate { Id = templateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        // Suite convention (SpawnBoar precedent): leave Faction null so the engine's
        // own null-faction branch reads attackable — no fixture faction invention.
        GameplayActorTestRig.SetNpcPosition(session, objId, pos);
        var region = session.World.GetRegionByPos(pos)!;
        region.AddObject(npc);
        npc.Region = region;
        _ = actor;
        return objId;
    }

    [Test]
    public async Task Listing_IsReadOnly_NoGameplayEffects()
    {
        var (actor, session) = CreateAreaActor("opp-readonly");
        GameplayActorTestRig.SeedQuestOffer(OpenQuestId, StartComponent, OfferNpcTemplate, level: 1);
        var offerObjId = SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        var auditBefore = actor.AuditTrace.Count;
        var questsBefore = actor.Character.Quests.ActiveQuests.Count;
        var hpBefore = actor.Character.Hp;

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(actor.Character.Quests.ActiveQuests.Count).IsEqualTo(questsBefore);
        await Assert.That(actor.Character.Hp).IsEqualTo(hpBefore);
        await Assert.That(actor.AuditTrace.Count).IsGreaterThan(auditBefore);
        await Assert.That(actor.AuditTrace.All(r => r.Action is ActorActionType.Observe
            or ActorActionType.DiscoverQuests or ActorActionType.DiscoverSelfQuests)).IsTrue();
        await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == OpenQuestId && q.SourceObjId == offerObjId)).IsTrue();
    }

    [Test]
    public async Task EligibleQuest_ListsOfferPassingRealGate_WithReason()
    {
        var (actor, session) = CreateAreaActor("opp-eligible");
        GameplayActorTestRig.SeedQuestOffer(OpenQuestId, StartComponent, OfferNpcTemplate, level: 1);
        var offerObjId = SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        var listing = BotOpportunityListing.List(actor);

        var row = listing.EligibleQuests.Single(q => q.QuestId == OpenQuestId);
        await Assert.That(row.AcceptorTemplateId).IsEqualTo(OfferNpcTemplate);
        await Assert.That(row.SourceObjId).IsEqualTo(offerObjId);
        await Assert.That(row.DistanceM <= GameplayActor.MaxQuestDiscoverRange).IsTrue();
        await Assert.That(string.IsNullOrWhiteSpace(row.Reason)).IsFalse();
    }

    [Test]
    public async Task IneligibleQuest_IsNotListed_LevelGated()
    {
        var (actor, session) = CreateAreaActor("opp-ineligible");
        GameplayActorTestRig.SeedQuestOffer(GatedQuestId, GatedStartComponent, OfferNpcTemplate, level: 1);
        GameplayActorTestRig.SeedQuestComponentLevelRequirement(GatedStartComponent, minLevel: 50);
        SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        actor.Character.Level = 1;

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == GatedQuestId)).IsFalse();
        await Assert.That(listing.CoverageValid).IsTrue();
    }

    [Test]
    public async Task ActiveQuest_IsNotRelisted_AsEligible()
    {
        var (actor, session) = CreateAreaActor("opp-active-hidden");
        GameplayActorTestRig.SeedQuestOffer(OpenQuestId, StartComponent, OfferNpcTemplate, level: 1);
        SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(OpenQuestId, QuestAcceptorType.Npc, OfferNpcTemplate);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == OpenQuestId)).IsFalse();
    }

    [Test]
    public async Task ViableHunt_ListsLivePreyServingActiveQuest_WithReason()
    {
        var (actor, session) = CreateAreaActor("opp-hunt");
        GameplayActorTestRig.SeedQuestHunt(HuntQuestId, HuntStartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(HuntQuestId);
        SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(HuntQuestId, QuestAcceptorType.Npc, OfferNpcTemplate);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var preyObjId = SpawnAreaNpc(session, actor, PreyTemplate, SpawnPos + new Vector3(8, 0, 0));

        var listing = BotOpportunityListing.List(actor);

        var hunt = listing.ViableHunts.Single(h => h.TargetObjId == preyObjId);
        await Assert.That(hunt.QuestId).IsEqualTo(HuntQuestId);
        await Assert.That(hunt.NeedCount).IsEqualTo(3);
        await Assert.That(string.IsNullOrWhiteSpace(hunt.Reason)).IsFalse();
    }

    [Test]
    public async Task AcceptedQuest_TransitionsFromEligibleToHuntRow()
    {
        // Owner-chain continuity: the SAME quest the listing offers as eligible
        // pre-accept appears as a viable hunt post-accept through the REAL
        // engine accept path (no ActiveQuests injection) once live prey of
        // the quest's hunt template shares the census. The consumer's
        // keep-purpose hunt branch leans on exactly this transition.
        var (actor, session) = CreateAreaActor("opp-accept-hunt");
        GameplayActorTestRig.SeedQuestHunt(HuntQuestId, HuntStartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(HuntQuestId);
        SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        var before = BotOpportunityListing.List(actor);
        await Assert.That(before.EligibleQuests.Any(q => q.QuestId == HuntQuestId)).IsTrue();
        await Assert.That(before.ViableHunts.Any(h => h.QuestId == HuntQuestId)).IsFalse();

        var accept = actor.AcceptQuest(HuntQuestId, QuestAcceptorType.Npc, OfferNpcTemplate);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var preyObjId = SpawnAreaNpc(session, actor, PreyTemplate, SpawnPos + new Vector3(8, 0, 0));

        var after = BotOpportunityListing.List(actor);
        await Assert.That(after.EligibleQuests.Any(q => q.QuestId == HuntQuestId)).IsFalse();
        var hunt = after.ViableHunts.Single(h => h.TargetObjId == preyObjId);
        await Assert.That(hunt.QuestId).IsEqualTo(HuntQuestId);
        await Assert.That(hunt.HaveCount).IsEqualTo(0);
        await Assert.That(hunt.NeedCount).IsEqualTo(3);
    }

    [Test]
    public async Task DeadPrey_IsNotListed_AsViableHunt()
    {
        var (actor, session) = CreateAreaActor("opp-dead-prey");
        GameplayActorTestRig.SeedQuestHunt(HuntQuestId, HuntStartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(HuntQuestId);
        SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(HuntQuestId, QuestAcceptorType.Npc, OfferNpcTemplate);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var preyObjId = SpawnAreaNpc(session, actor, PreyTemplate, SpawnPos + new Vector3(8, 0, 0));
        session.World.GetNpc(preyObjId)!.Hp = 0;

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.ViableHunts.Any(h => h.TargetObjId == preyObjId)).IsFalse();
    }

    [Test]
    public async Task UnrelatedNpc_IsNotListed_AsViableHunt()
    {
        var (actor, session) = CreateAreaActor("opp-unrelated");
        GameplayActorTestRig.SeedQuestHunt(HuntQuestId, HuntStartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(HuntQuestId);
        SpawnAreaNpc(session, actor, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        var accept = actor.AcceptQuest(HuntQuestId, QuestAcceptorType.Npc, OfferNpcTemplate);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        var otherObjId = SpawnAreaNpc(session, actor, UnrelatedNpcTemplate, SpawnPos + new Vector3(8, 0, 0));

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.ViableHunts.Any(h => h.TargetObjId == otherObjId)).IsFalse();
    }

    [Test]
    public async Task UnseenResource_IsNotMarkedExhausted_CoverageInvalid()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("opp-unseen");
        actor.Character.Region = null!;

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.CoverageValid).IsFalse();
        await Assert.That(listing.ViableHunts.Count).IsEqualTo(0);
        await Assert.That(listing.EligibleQuests.Count).IsEqualTo(0);
        await Assert.That(listing.CoverageReason.Contains("exhaust")).IsFalse();
        await Assert.That(listing.CoverageReason.Contains("unavailable") || listing.CoverageReason.Contains("UNKNOWN") || listing.CoverageReason.Contains("no-region")).IsTrue();
    }

    [Test]
    public async Task EmptyArea_IsValidEmpty_NotExhausted()
    {
        var (actor, _) = CreateAreaActor("opp-empty");

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.CoverageValid).IsTrue();
        await Assert.That(listing.ViableHunts.Count).IsEqualTo(0);
        await Assert.That(listing.CoverageReason.Contains("exhaust")).IsFalse();
    }

    [Test]
    public async Task Recovery_HealthyVitals_NeedsNothing_WithReason()
    {
        var (actor, _) = CreateAreaActor("opp-healthy");
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.Recovery.NeedsRecovery).IsFalse();
        await Assert.That(string.IsNullOrWhiteSpace(listing.Recovery.Reason)).IsFalse();
    }

    [Test]
    public async Task Recovery_LowVitals_NeedsRecovery_WithReason()
    {
        var (actor, _) = CreateAreaActor("opp-hurt");
        actor.Character.Hp = (int)(actor.Character.MaxHp * 0.2f);
        actor.Character.Mp = actor.Character.MaxMp;

        var listing = BotOpportunityListing.List(actor);

        await Assert.That(listing.Recovery.NeedsRecovery).IsTrue();
        await Assert.That(listing.Recovery.HpRatio < 0.75f).IsTrue();
        await Assert.That(string.IsNullOrWhiteSpace(listing.Recovery.Reason)).IsFalse();
    }
}
