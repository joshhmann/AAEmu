using System.Numerics;
using System.Reflection;
using System.Text.Json;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Goal-owner wiring: the EXISTING QuestBootstrap module owns WHICH quest
/// (BotBehaviorRuntime owns the running job once dispatched — that split is
/// load-bearing). The module picks from the read-only opportunity listing
/// with a small deterministic policy, keeps the purpose during ordinary
/// waiting, reconsiders on terminal results or changed conditions, and the
/// pick executes through the running-hold. No third selector, no silent
/// ownership change: recovery still outranks quest via priority, the arbiter
/// still owns the activity name, the quest leg still owns dispatch.
/// </summary>
[NotInParallel]
public class QuestGoalOwnerTests
{
    private static readonly Vector3 SpawnPos = new(32, 32, 0);
    private const uint OfferNpcTemplate = 91_740;
    private const uint PreyTemplate = 91_741;
    private const uint ReportNpcTemplate = 91_743;
    private const uint OpenQuestId = 91_751;
    private const uint SecondQuestId = 91_752;
    private const uint HuntQuestId = 91_753;
    private const uint StartComponent = 91_760;
    private const uint SecondStartComponent = 91_761;
    private const uint HuntStartComponent = 91_762;
    private const uint HuntProgressComponent = 91_763;
    private const uint HuntReadyComponent = 91_764;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
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
    }

    private static QuestBootstrapActivityModule EnabledModule()
        => new(new QuestBootstrapModuleOptions { Enabled = true });

    private static PlayerBotRuntime NewBot(GameplayActor actor)
        => new(actor.Character, "goal-owner-tests");

    private static BotActivityContext ContextFor(PlayerBotRuntime bot, string? active = null)
        => new() { Bot = bot, GameHour = 12f, ActiveActivity = active };

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

    private static uint SpawnAreaNpc(HeadlessSession session, uint templateId, Vector3 pos)
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
        return objId;
    }

    [Test]
    public async Task Owner_PicksListedQuest_AllowsQuestActivity()
    {
        var (actor, session) = CreateAreaActor("owner-pick");
        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(OpenQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        var module = EnabledModule();
        var decision = module.CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsTrue().Because($"why={decision.WhyNot}");
        await Assert.That(decision.ActivityName).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(OpenQuestId);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task FreshOffers_NearerSupportedQuestWins_RegardlessOfIds(bool swapIds, bool swapPositions)
    {
        var (actor, session) = CreateAreaActor("useful-offer-distance");
        var seed = 98_400u + (swapIds ? 100u : 0u) + (swapPositions ? 200u : 0u);
        var nearQuest = seed + (swapIds ? 1u : 2u);
        var farQuest = seed + (swapIds ? 2u : 1u);
        var nearGiver = seed + 3;
        var farGiver = seed + 4;
        GameplayActorTestRig.SeedQuestHunt(farQuest, seed + 5, seed + 6,
            seed + 7, farGiver, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        GameplayActorTestRig.SeedQuestHunt(nearQuest, seed + 8, seed + 9, seed + 10,
            nearGiver, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.InvalidateAll();
        SpawnAreaNpc(session, farGiver, SpawnPos + new Vector3(swapPositions ? 3 : 18, 0, 0));
        SpawnAreaNpc(session, nearGiver, SpawnPos + new Vector3(swapPositions ? 18 : 3, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.EligibleQuests.Single(q => q.QuestId == nearQuest).DistanceM).IsEqualTo(swapPositions ? 18f : 3f);
        await Assert.That(listing.EligibleQuests.Single(q => q.QuestId == farQuest).DistanceM).IsEqualTo(swapPositions ? 3f : 18f);
        var module = EnabledModule();
        await Assert.That(module.CanActivate(ContextFor(NewBot(actor))).CanActivate).IsTrue();
        var expected = swapPositions ? farQuest : nearQuest;
        Console.WriteLine($"CHOICE offers expected-nearest={expected}/3m chosen={module.GetChosenQuest(actor.Character.Id)} ids-swapped={swapIds} positions-swapped={swapPositions}");
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(expected);
        await Assert.That(actor.Character.Quests.ActiveQuests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FreshOffers_NearerIneligibleOfferCannotWin()
    {
        var (actor, session) = CreateAreaActor("useful-offer-eligibility");
        GameplayActorTestRig.SeedQuestHunt(98_801, 98_811, 98_812, 98_813,
            98_821, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 50);
        GameplayActorTestRig.SeedQuestComponentLevelRequirement(98_811, 50);
        GameplayActorTestRig.SeedQuestHunt(98_802, 98_814, 98_815, 98_816,
            98_822, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.InvalidateAll();
        SpawnAreaNpc(session, 98_821, SpawnPos + new Vector3(2, 0, 0));
        SpawnAreaNpc(session, 98_822, SpawnPos + new Vector3(18, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        await Assert.That(BotOpportunityListing.List(actor).EligibleQuests.Any(q => q.QuestId == 98_801)).IsFalse();
        var module = EnabledModule();
        await Assert.That(module.CanActivate(ContextFor(NewBot(actor))).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(98_802u);
    }

    [Test]
    public async Task FreshOffers_EqualDistanceUsesQuestId_ThenRetainsRunningPurpose()
    {
        var (actor, session) = CreateAreaActor("useful-offer-tie-retain");
        GameplayActorTestRig.SeedQuestHunt(98_901, 98_911, 98_912, 98_913,
            98_921, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        GameplayActorTestRig.SeedQuestHunt(98_902, 98_914, 98_915, 98_916,
            98_922, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.InvalidateAll();
        SpawnAreaNpc(session, 98_921, SpawnPos + new Vector3(5, 0, 0));
        var other = SpawnAreaNpc(session, 98_922, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        var module = EnabledModule();
        var bot = NewBot(actor);
        await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(98_901u);
        GameplayActorTestRig.SetNpcPosition(session, other, SpawnPos + new Vector3(1, 0, 0));
        // A newly cheaper candidate does not steal the existing purpose.
        await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(98_901u);
        var running = actor.MoveTo(SpawnPos + new Vector3(10, 0, 0), 1f);
        for (var wake = 0; wake < 3; wake++)
            await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
        await Assert.That(actor.ActiveRequest).IsEqualTo(running);
        await Assert.That(running.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(98_901u);
        await Assert.That(actor.Interrupt(running.TraceId)).IsTrue();
        await Assert.That(module.CanActivate(ContextFor(bot)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(bot.CharacterId)).IsEqualTo(98_901u);
        await Assert.That(actor.AuditTrace.Count(r => r.TraceId == running.TraceId)).IsEqualTo(1);
    }

    [Test]
    public async Task Purpose_TwoLegalQuests_ChosenDeterminesDispatchedIdentity()
    {
        // HIGH 1(a): with two legal quests, the recorded choice determines
        // PlanWake + leg loop, never around it.
        var (actor, session) = CreateAreaActor("purpose-two-legal");
        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(OpenQuestId);
        GameplayActorTestRig.SeedQuestHunt(SecondQuestId, SecondStartComponent, 91_765, 91_766,
            OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(SecondQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);

        var module = EnabledModule();
        _ = module.CanActivate(ContextFor(NewBot(actor)));
        var chosen = module.GetChosenQuest(actor.Character.Id);
        await Assert.That(chosen).IsNotEqualTo(0u);
        var other = chosen == OpenQuestId ? SecondQuestId : OpenQuestId;

        // Accept BOTH through the real engine path: both quests are legal
        // ordinary work, so an unscoped wake could serve either.
        await Assert.That(actor.AcceptQuest(OpenQuestId, QuestAcceptorType.Npc, OfferNpcTemplate).State)
            .IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.AcceptQuest(SecondQuestId, QuestAcceptorType.Npc, OfferNpcTemplate).State)
            .IsEqualTo(ActorLifecycleState.Completed);

        var context = BotObservedContext.Capture(actor);
        var unscoped = QuestDirector.PlanWake(context);
        await Assert.That(unscoped.Select(p => p.QuestId)).IsEquivalentTo(new[] { OpenQuestId, SecondQuestId });

        var scoped = QuestDirector.PlanWake(context, chosen);
        await Assert.That(scoped.Select(p => p.QuestId)).IsEquivalentTo(new[] { chosen });

        var result = QuestDirector.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "purpose-two-legal-1", ChosenQuestId = chosen });
        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedQuestId).IsEqualTo(chosen);
        await Assert.That(result.PurposeSwitchReason).IsEqualTo("");
        await Assert.That(QuestBehavior.QuestIdOf(result.SelectedProposal!)).IsEqualTo(chosen);
        await Assert.That(result.Request is { TargetId: var target } && target == other).IsFalse();
    }

    [Test]
    public async Task Purpose_UnseenChoice_NamesExplicitHold_NeverSilent()
    {
        // HIGH 1(b) (direct QuestDirector.Run call, no scheduler): a carried
        // purpose for a quest this bounded sweep did not see (empty sweep
        // here — the offer itself stays valid server-side) is KEPT by the
        // decision path (the result only ADVISES the hold by naming it; the
        // owner's own field is untouched — real release is owned by
        // QuestBootstrapActivityModule and proven on GetChosenQuest, never
        // by these advisory fields): the plan set is empty, no unrelated
        // quest action is dispatched, and the wake names the hold explicitly
        // in the result.
        var (actor, session) = CreateAreaActor("purpose-stale");
        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(OpenQuestId);
        GameplayActorTestRig.SeedQuestHunt(SecondQuestId, SecondStartComponent, 91_765, 91_766,
            OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(SecondQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);

        // Only the second quest is active; the carried purpose names the first.
        await Assert.That(actor.AcceptQuest(SecondQuestId, QuestAcceptorType.Npc, OfferNpcTemplate).State)
            .IsEqualTo(ActorLifecycleState.Completed);

        var context = BotObservedContext.Capture(actor);
        var scoped = QuestDirector.PlanWake(context, OpenQuestId);
        await Assert.That(scoped.Count).IsEqualTo(0);

        var result = QuestDirector.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "purpose-stale-1", ChosenQuestId = OpenQuestId });
        await Assert.That(result.WorkSelected).IsFalse();
        await Assert.That(result.Request).IsNull();
        await Assert.That(result.SelectedQuestId).IsEqualTo(0u);
        await Assert.That(result.PurposeSwitchReason).Contains($"purpose-unseen chosen={OpenQuestId}");
    }

    [Test]
    public async Task Purpose_SafetyVeto_StillPreempts_NoOrdinaryDispatch()
    {
        // HIGH 1(c): safety interruption still preempts — a vetoed wake
        // dispatches no ordinary quest work even with a purpose carried.
        // Staged at the DECISION layer (QuestDirector.Run): critical hp WITH
        // fight evidence (selected target = the survival chain's own
        // fight-evidence contract) vetoes every ordinary quest proposal
        // through its own per-leg survival-veto precondition.
        var (actor, session) = CreateAreaActor("purpose-safety");
        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(OpenQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);

        var module = EnabledModule();
        _ = module.CanActivate(ContextFor(NewBot(actor)));
        var chosen = module.GetChosenQuest(actor.Character.Id);
        await Assert.That(chosen).IsEqualTo(OpenQuestId);
        await Assert.That(actor.AcceptQuest(OpenQuestId, QuestAcceptorType.Npc, OfferNpcTemplate).State)
            .IsEqualTo(ActorLifecycleState.Completed);

        // Fight evidence: a live hostile target selected, plus critical hp.
        var preyObjId = session.SpawnNpc(PreyTemplate);
        var prey = session.World.GetNpc(preyObjId)!;
        prey.TemplateId = PreyTemplate;
        prey.Template = new NpcTemplate { Id = PreyTemplate, Scale = 1f };
        prey.Hp = 100;
        prey.MaxHp = 100;
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, SpawnPos + new Vector3(5, 0, 0));
        var region = session.World.GetRegionByPos(SpawnPos)!;
        region.AddObject(prey);
        prey.Region = region;
        actor.Character.CurrentTarget = prey;
        actor.Character.Hp = Math.Max(0, (int)(actor.Character.MaxHp * 0.10f));
        var result = QuestDirector.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "purpose-safety-1", ChosenQuestId = chosen });
        await Assert.That(result.WorkSelected).IsFalse();
        await Assert.That(result.Request).IsNull();
        await Assert.That(result.Rejections.Count).IsGreaterThan(0);
        await Assert.That(result.Rejections.All(r => r.Reason.Contains("survival-veto-clear"))).IsTrue();
    }

    [Test]
    public async Task Owner_KeepsPurpose_DuringOrdinaryWaiting()
    {
        var (actor, session) = CreateAreaActor("owner-keep");
        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(OpenQuestId);
        GameplayActorTestRig.SeedQuestHunt(SecondQuestId, SecondStartComponent, 91_765, 91_766,
            OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(SecondQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);

        var module = EnabledModule();
        var bot = NewBot(actor);
        _ = module.CanActivate(ContextFor(bot));
        var first = module.GetChosenQuest(actor.Character.Id);
        await Assert.That(first).IsNotEqualTo(0u);

        // Ordinary waiting: nothing completed, nothing disappeared — the
        // purpose stays picked without re-ranking.
        var again = module.CanActivate(ContextFor(bot, QuestBootstrapActivityModule.ActivityName));

        await Assert.That(again.CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(first);
    }

    [Test]
    public async Task Owner_Reconsiders_OnTerminalResult()
    {
        var (actor, session) = CreateAreaActor("owner-reconsider");
        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(OpenQuestId);
        GameplayActorTestRig.SeedQuestHunt(SecondQuestId, SecondStartComponent, 91_765, 91_766,
            OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(SecondQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);

        var module = EnabledModule();
        var bot = NewBot(actor);
        _ = module.CanActivate(ContextFor(bot));
        var first = module.GetChosenQuest(actor.Character.Id);

        // Terminal feedback: the picked quest completed through the real
        // engine path — the next pick must move on, never re-pick it.
        var accept = actor.AcceptQuest(OpenQuestId, QuestAcceptorType.Npc, OfferNpcTemplate);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        module.NotifyQuestTerminal(actor.Character.Id, OpenQuestId, completed: true);

        var next = module.CanActivate(ContextFor(bot, QuestBootstrapActivityModule.ActivityName));

        await Assert.That(next.CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(SecondQuestId);
    }

    [Test]
    public async Task Owner_YieldsToRecovery_NeedsRecovery()
    {
        var (actor, session) = CreateAreaActor("owner-recovery");
        GameplayActorTestRig.SeedQuestHunt(OpenQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        QuestFixtureRow.Invalidate(OpenQuestId);
        SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        actor.Character.Hp = (int)(actor.Character.MaxHp * 0.2f);

        var module = EnabledModule();
        var decision = module.CanActivate(ContextFor(NewBot(actor)));

        await Assert.That(decision.CanActivate).IsFalse();
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(0u);
    }

    [Test]
    public async Task Owner_Pick_ExecutesThroughRunningHold()
    {
        // Deterministic integration: the owner's pick executes through the
        // production quest leg + running-hold — wake 1 dispatches the pursuit
        // Move, wake 2 holds the SAME request, no fallback replaces it.
        // The pick itself runs on the eligible-offer channel (unaccepted 251
        // offered by the in-range NPC); the ACTIVE hunt shape below is what
        // the leg dispatches on once accepted.
        var (executor, actor, runtime, clock, session) = CreatePursuitRig("owner-exec", activeQuest: false);
        var module = EnabledModule();
        QuestLegActor.Stage(actor.Character.Id, actor);
        var pickDecision = module.CanActivate(ContextFor(runtime));
        await Assert.That(pickDecision.CanActivate).IsTrue().Because($"why={pickDecision.WhyNot}");
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsNotEqualTo(0u);
        // Accept through the real engine path, then run the leg.
        var accept = actor.AcceptQuest(251, QuestAcceptorType.Npc, 3512);
        await Assert.That(accept.State).IsEqualTo(ActorLifecycleState.Completed);
        _ = session;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var owned = actor.ActiveRequest;
        await Assert.That(owned is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(owned);
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(actor.ActiveRequest)).IsTrue();
    }
    [Test]
    public async Task OwnerChosenFallback_DoesNotWalkUnrelatedReadyQuest()
    {
        // Synthetic world; the real module chooses the only offered quest
        // before another, lower-ID Ready quest becomes active.
        var (actor, session) = CreateAreaActor("owner-fallback-purpose");
        const uint chosenQuestId = 91_755;
        const uint chosenStartComponent = 91_771;
        const uint chosenReadyComponent = 91_772;
        const uint chosenOfferNpc = 91_770;
        const uint chosenReporter = 91_744;
        const uint otherQuestId = 91_750;
        const uint otherStartComponent = 91_773;
        const uint otherReadyComponent = 91_774;
        const uint otherOfferNpc = 91_775;
        const uint otherReporter = 91_776;
        await Assert.That(QuestManager.Instance.GetTemplate(chosenQuestId)).IsNull();
        await Assert.That(QuestManager.Instance.GetTemplate(otherQuestId)).IsNull();
        var components = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(QuestManager.Instance)!;
        foreach (var componentId in new[] { chosenStartComponent, chosenReadyComponent, otherStartComponent, otherReadyComponent })
            await Assert.That(components.ContainsKey(componentId)).IsFalse();
        var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
            .GetField("_actTemplatesByDetailType", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(QuestManager.Instance)!;
        foreach (var (type, key) in new[]
        {
            (nameof(QuestActConAcceptNpc), chosenStartComponent),
            (nameof(QuestActConAcceptNpc), otherStartComponent),
            (nameof(QuestActConReportNpc), chosenReadyComponent),
            (nameof(QuestActConReportNpc), otherReadyComponent),
        })
            await Assert.That(actsByType.TryGetValue(type, out var acts) && acts.ContainsKey(key)).IsFalse();
        GameplayActorTestRig.SeedQuestDelivery(chosenQuestId, chosenStartComponent, chosenReadyComponent,
            chosenOfferNpc, chosenReporter, level: 3);
        SpawnAreaNpc(session, chosenOfferNpc, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        var runtime = NewBot(actor);
        var module = EnabledModule();
        await Assert.That(module.CanActivate(ContextFor(runtime)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(chosenQuestId);
        var accepted = actor.AcceptQuest(chosenQuestId, QuestAcceptorType.Npc, chosenOfferNpc);
        await Assert.That(accepted.State).IsEqualTo(ActorLifecycleState.Completed);
        var chosenQuest = actor.Character.Quests.ActiveQuests[chosenQuestId];
        chosenQuest.SkipUpdatePackets();
        chosenQuest.Step = QuestComponentKind.Ready;
        chosenQuest.Status = QuestStatus.Ready;

        GameplayActorTestRig.SeedQuestDelivery(otherQuestId, otherStartComponent, otherReadyComponent,
            otherOfferNpc, otherReporter, level: 3);
        var otherQuest = new Quest(QuestManager.Instance.GetTemplate(otherQuestId), actor.Character);
        otherQuest.SkipUpdatePackets();
        otherQuest.Step = QuestComponentKind.Ready;
        otherQuest.Status = QuestStatus.Ready;
        actor.Character.Quests.ActiveQuests[otherQuestId] = otherQuest;
        var ownTarget = SpawnPos + new Vector3(40, 0, 0);
        var unrelatedTarget = SpawnPos + new Vector3(-30, 0, 0);
        SeedPurposeSpawner(actor, chosenReporter, ownTarget);
        SeedPurposeSpawner(actor, otherReporter, unrelatedTarget);
        QuestFixtureRow.InvalidateAll();
        await Assert.That(module.CanActivate(ContextFor(runtime, QuestBootstrapActivityModule.ActivityName)).CanActivate).IsTrue();
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(chosenQuestId);

        var clock = new FakeTimeProvider();
        var executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor, TimeProvider = clock, RoamSpeed = 2f,
            ActiveActivityProvider = _ => QuestBootstrapActivityModule.ActivityName,
            QuestChosenProvider = module.GetChosenQuest,
            NearbyNpcProvider = (_, _) => [], NearbyDoodadProvider = (_, _) => [],
        };
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var move = actor.ActiveRequest;
        await Assert.That(move is { Action: ActorActionType.Move, State: ActorLifecycleState.Running }).IsTrue();
        await Assert.That(move!.MoveOwner).IsEqualTo("QUEST_TRAVEL");
        await Assert.That(move.Destination).IsEqualTo((Vector3?)ownTarget);
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)!.Waypoints[^1]).IsEqualTo(ownTarget);
        actor.PreemptCurrent("interrupted by controller");
        var row = actor.AuditTrace.Single(r => r.TraceId == move.TraceId);
        using var json = JsonDocument.Parse(row.ToJson());
        var hasParent = json.RootElement.TryGetProperty("decision_quest_id", out var parent);
        await Assert.That(hasParent).IsTrue();
        await Assert.That(hasParent ? parent.GetUInt32() : 0u).IsEqualTo(chosenQuestId);
    }

    [Test]
    public async Task OwnerChosenFallback_SameQuestProgressToReadyRetiresRunningObjectiveRoute()
    {
        // Synthetic initialized Interaction objective and far doodad source:
        // the real Advance gate withdraws only for gather/interact DOODADS,
        // not the prey-only fixture whose non-applied Advance was landing.
        // The fallback Move, phase change and following tick are real actions.
        const uint questId = 91_754;
        var (executor, actor, runtime, clock, session) = CreatePursuitRig("owner-phase-retire", activeQuest: false);
        var prey = session.World.GetNpcByTemplateId(3475)!;
        GameplayActorTestRig.SetNpcPosition(session, prey.ObjId, new Vector3(1000, 0, 0));
        actor.Character.CurrentTarget = null;
        var source = new Vector3(40, 0, 0);
        SeedPurposeInteractionQuest(actor, questId, 91_747, source);
        executor.QuestChosenProvider = _ => questId;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var victim = actor.ActiveRequest!;
        await Assert.That(victim).IsNotNull();
        await Assert.That(victim.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(victim.MoveOwner).IsEqualTo("QUEST_TRAVEL");
        await Assert.That(victim.Destination).IsEqualTo((Vector3?)source);
        await Assert.That(victim.DecisionQuestId).IsEqualTo((uint?)questId);
        var cycle = victim.DecisionCycleId;

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(victim);
        await Assert.That(victim.DecisionCycleId).IsEqualTo(cycle);
        var before = actor.Character.Transform.World.Position;
        var quest = actor.Character.Quests.ActiveQuests[questId];
        quest.SkipUpdatePackets();
        quest.Step = QuestComponentKind.Ready;
        quest.Status = QuestStatus.Ready;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);

        await Assert.That(victim.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(before);
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull();
        var row = actor.AuditTrace.Single(r => r.TraceId == victim.TraceId);
        await Assert.That(row.DecisionQuestId).IsEqualTo((uint?)questId);
        await Assert.That(row.DecisionCycleId).IsEqualTo(cycle);
    }

    [Test]
    public async Task OwnerChosenFallback_OfferedQuestBecomingActiveRetiresOfferRoute()
    {
        // Synthetic offered/active states, actual fallback resolution and Move.
        var (actor, _) = CreateAreaActor("owner-offer-phase-retire");
        const uint questId = 91_753;
        const uint offerer = 91_745;
        GameplayActorTestRig.SeedQuestDelivery(questId, 91_768, 91_769,
            offerer, 91_746, level: 3);
        SeedPurposeSpawner(actor, offerer, SpawnPos + new Vector3(40, 0, 0));
        QuestFixtureRow.InvalidateAll();
        var runtime = NewBot(actor);
        var clock = new FakeTimeProvider();
        var executor = new BotRoamStepExecutor
        {
            ActorFactory = _ => actor, TimeProvider = clock, RoamSpeed = 2f,
            ActiveActivityProvider = _ => QuestBootstrapActivityModule.ActivityName,
            QuestChosenProvider = _ => questId,
            NearbyNpcProvider = (_, _) => [], NearbyDoodadProvider = (_, _) => [],
        };
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        var victim = actor.ActiveRequest!;
        await Assert.That(victim.State).IsEqualTo(ActorLifecycleState.Running);
        await Assert.That(victim.MoveOwner).IsEqualTo("QUEST_TRAVEL");
        await Assert.That(victim.DecisionQuestId).IsEqualTo((uint?)questId);
        var before = actor.Character.Transform.World.Position;
        var active = new Quest(QuestManager.Instance.GetTemplate(questId), actor.Character);
        active.SkipUpdatePackets();
        active.Step = QuestComponentKind.Progress;
        active.Status = QuestStatus.Progress;
        actor.Character.Quests.ActiveQuests[questId] = active;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(victim.State).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(actor.Character.Transform.World.Position).IsEqualTo(before);
        await Assert.That(executor.GetRoamRoute(runtime.CharacterId)).IsNull();
        await Assert.That(actor.AuditTrace.Single(r => r.TraceId == victim.TraceId).DecisionQuestId)
            .IsEqualTo((uint?)questId);
    }

    private static void SeedPurposeInteractionQuest(GameplayActor actor, uint questId, uint doodadTemplateId, Vector3 position)
    {
        GameplayActorTestRig.SeedQuestDelivery(questId, questId + 100, questId + 200,
            questId + 300, 3512, level: 2);
        var template = QuestManager.Instance.GetTemplate(questId);
        var component = new QuestComponentTemplate(template)
        {
            Id = questId + 400,
            KindId = QuestComponentKind.Progress,
        };
        component.ActTemplates.Add(new QuestActObjInteraction(component)
        {
            ActId = questId + 500,
            DetailId = component.Id,
            DetailType = nameof(QuestActObjInteraction),
            DoodadId = doodadTemplateId,
            Count = 1,
            ThisComponentObjectiveIndex = 0,
        });
        template.Components[component.Id] = component;
        var components = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(QuestManager.Instance)!;
        components[component.Id] = component;
        var quest = new Quest(template, actor.Character);
        quest.SkipUpdatePackets();
        quest.Step = QuestComponentKind.Progress;
        quest.Status = QuestStatus.Progress;
        actor.Character.Quests.ActiveQuests[questId] = quest;

        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, DoodadSpawner>)typeof(SpawnManager)
            .GetProperty("DoodadSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(9000 + spawners.Count);
        spawners[key] = new DoodadSpawner
        {
            Id = key,
            UnitId = doodadTemplateId,
            Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
        };
        QuestFixtureRow.InvalidateAll();
    }

    private static void SeedPurposeSpawner(GameplayActor actor, uint npcTemplateId, Vector3 position)
    {
        var world = actor.Character.ParentWorld!;
        world.SpawnManager ??= new SpawnManager(world);
        var spawners = (Dictionary<uint, List<NpcSpawner>>)typeof(SpawnManager)
            .GetProperty("NpcSpawners", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(world.SpawnManager)!;
        var key = (uint)(91_780 + spawners.Count);
        spawners[key] =
        [
            new NpcSpawner
            {
                Id = key, UnitId = npcTemplateId,
                Position = new WorldSpawnPosition { X = position.X, Y = position.Y, Z = position.Z },
            }
        ];
    }

    // ------------------------------------------------------------ rig

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, FakeTimeProvider Clock, HeadlessSession Session)
        CreatePursuitRig(string name, bool activeQuest = true)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        actor.Character.Hp = actor.Character.MaxHp;
        actor.Character.Mp = actor.Character.MaxMp;
        var actorPos = new Vector3(0, 0, 0);
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        const uint Boar3475 = 3475;
        const uint Pack4530 = 4530;
        const uint Meat4058 = 4058;
        SeedPursuitShape();
        QuestFixtureRow.Invalidate(251);
        // Eligible-offer channel for the owner pick: the 251 offer NPC
        // in-range (the hunt channel needs a live prey too — seeded below).
        var offerObjId = session.SpawnNpc(3512);
        var offer = session.World.GetNpc(offerObjId)!;
        offer.TemplateId = 3512;
        offer.Template = new NpcTemplate { Id = 3512, Scale = 1f };
        offer.Hp = 100;
        offer.MaxHp = 100;
        offer.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, offerObjId, new Vector3(5, 0, 0));
        var offerRegion = session.World.GetRegionByPos(new Vector3(5, 0, 0)) ?? region;
        offerRegion.AddObject(offer);
        offer.Region = offerRegion;
        var preyObjId = session.SpawnNpc(Boar3475);
        var prey = session.World.GetNpc(preyObjId)!;
        prey.TemplateId = Boar3475;
        prey.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        prey.Hp = 100;
        prey.MaxHp = 100;
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, new Vector3(10, 0, 0));
        region.AddObject(prey);
        prey.Region = region;

        if (activeQuest)
            actor.Character.Quests.ActiveQuests[251] = new Quest(actor.Character)
            {
                TemplateId = 251,
                Status = QuestStatus.Progress,
                Step = QuestComponentKind.Progress,
                Objectives = [0, 0, 0, 0, 0],
            };
        actor.Character.CurrentTarget = prey;

        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            ActorFactory = _ => actor,
            TimeProvider = clock,
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f,
            EnableWildlifeHunt = true,
            EnableWildlifeButcher = true,
            HuntScanInterval = TimeSpan.FromMilliseconds(100),
            ButcherScanInterval = TimeSpan.FromMilliseconds(100),
            HuntCastInterval = TimeSpan.FromMilliseconds(100),
            GroundHeightProvider = (_, _) => 0f,
            ActiveActivityProvider = _ => QuestBootstrapActivityModule.ActivityName,
            NearbyNpcProvider = (_, _) => [],
            NearbyDoodadProvider = (_, _) => [],
            UnitResolver = (c, id) => c.ParentWorld?.GetUnit(id),
            DoodadResolver = (c, id) => c.ParentWorld?.GetDoodad(id)
        };
        return (executor, actor, runtime, clock, session);
    }

    private static void SeedPursuitShape()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(251, out var template))
        {
            template = new QuestTemplate { Id = 251, Level = 2 };
            questTemplates[251] = template;
        }
        if (!template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == 4058))
        {
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
            var act = new QuestActObjItemGather(component)
            {
                ActId = 10473,
                DetailId = 616,
                DetailType = nameof(QuestActObjItemGather),
                Count = 3,
                ItemId = 4058,
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

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(3475, out var rows) || rows.All(r => r.LootPackId != 4530))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = 3475,
                NpcId = 3475,
                LootPackId = 4530,
                DefaultPack = true,
            });
            map[3475] = rows;
        }

        var packField = typeof(LootGameData).GetField("_lootPacks", flags)!;
        if (packField.GetValue(LootGameData.Instance) is not Dictionary<uint, LootPack> packs)
        {
            packs = [];
            packField.SetValue(LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(4530, out var pack) || pack.Loots.All(l => l.ItemId != 4058))
        {
            var loot = new Loot
            {
                Id = 4530,
                Group = 0,
                ItemId = 4058,
                DropRate = 10_000_000,
                MinAmount = 1,
                MaxAmount = 1,
                LootPackId = 4530,
                GradeId = 0,
                AlwaysDrop = false,
            };
            packs[4530] = new LootPack
            {
                Id = 4530,
                Loots = [loot],
                LootsByGroupNo = new Dictionary<uint, List<Loot>> { [0] = [loot] },
                Groups = [],
                ActabilityGroups = [],
                GroupCount = 1,
            };
        }
        QuestFixtureRow.InvalidateAll();
    }
}
