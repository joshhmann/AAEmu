using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
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
/// Deterministic (fixture-seeded, no live autonomy) chosen-quest acceptance
/// control: the carried purpose scopes ordinary quest ACCEPTANCE as well as
/// accepted-quest work. T1-T4 call QuestDirector.Run DIRECTLY (no scheduler,
/// no arbiter in these paths — "real decision/execution path" below means the
/// production QuestDirector/QuestBehavior dispatch on the real engine, not a
/// live scheduler wake); T5 drives the choice owner through the real per-wake
/// arbitration entry (BotGoalArbiter.Arbitrate, the production caller of
/// QuestBootstrapActivityModule.CanActivate) and asserts on the owner's own
/// retained choice (GetChosenQuest), never on the advisory result fields.
/// Each test would fail on the pre-fix code, which ranked and proposed every
/// in-band offer and dispatched whatever the selector picked before any
/// purpose note was stamped.
/// </summary>
[NotInParallel]
public class QuestChosenAcceptanceTests
{
    private static readonly Vector3 SpawnPos = new(32, 32, 0);
    // Dedicated offer-NPC namespace for THIS file (93745/93746/93747 — no
    // other suite seeds quests on these templates): the offer NPC serves
    // every quest seeded on its template, so sharing 93740 with
    // BotOpportunityListingBusyTests (which seeds quest 93751 on it)
    // polluted this file's sweeps in full-gate runs. T1-T4 share one NPC
    // (both offers, same as before); T5 splits per-quest NPCs.
    private const uint OfferNpcTemplate = 93_747;
    private const uint ChosenOfferNpcTemplate = 93_745;
    private const uint OtherOfferNpcTemplate = 93_746;
    private const uint PreyTemplate = 93_741;
    private const uint ReportNpcTemplate = 93_743;
    private const uint RewardItemTemplate = 93_744;
    // T1 pair: the CHOSEN quest is the HIGHER-level HIGHER-id offer with NO
    // reward; the OTHER quest is the lower-level rewarded offer the old
    // ranking demonstrably prefers (separate fixture from the shared pair).
    private const uint PreferredQuestId = 93_753;
    private const uint OtherQuestId = 93_754;
    private const uint StartComponent = 93_773;
    private const uint SecondStartComponent = 93_776;
    private const uint HuntProgressComponent = 93_774;
    private const uint HuntReadyComponent = 93_775;
    private const uint SecondProgressComponent = 93_777;
    private const uint SecondReadyComponent = 93_778;
    // Supply-component reward for the OTHER offer (dedicated ids, never
    // shared with the start/progress/ready components above).
    private const uint OtherSupplyComponent = 93_779;
    private const uint OtherSupplyAct = 93_780;
    // T4b pursuit rig (mirrors QuestRunningOwnershipTests): quest 251 +
    // gather act + npc→pack→item loot link so the funnel source resolves.
    private const uint PursuitQuestId = 251;
    private const uint PursuitOfferNpc = 3512;
    private const uint PursuitPreyTemplate = 3475;
    private const uint PursuitPackId = 4530;
    private const uint PursuitItemId = 4058;
    private const uint PursuitGatherActId = 10473;

    // QuestManager instance displaced by sibling suites that overwrite
    // Singleton<QuestManager>.s_instance without restore (per-test class
    // instance, same as M3aExitScenarioTests._previousQuestManager).
    private object? _previousQuestManager;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        // Order-independent QuestManager: FORCE-install a fresh instance
        // (sibling QuestAct suites overwrite the singleton with EMPTY
        // tables and never restore, and Seed-if-absent never repairs a
        // non-null poisoned instance — so capture + overwrite here, restore
        // in TearDown, leaving no trace for the rest of the process).
        var questField = typeof(Singleton<QuestManager>).GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        _previousQuestManager = questField.GetValue(null);
        var fresh = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<AAEmu.Game.Core.Managers.World.IZoneManager>().Object);
        questField.SetValue(null, fresh);
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
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, _previousQuestManager);
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

    private static void SeedBothOffers()
    {
        // Old acceptance ranking is band-primary: priority =
        // AcceptPriority + (BandMax - level) (QuestBehavior.AcceptRank,
        // QuestBehavior.cs:3152-3162); the reward hook rides only the
        // second ordering key (selector: BotDecisionProposal.cs:319-322 —
        // priority desc, then weight desc, then tie-break quest id asc), so
        // a HIGHER-level HIGHER-id offer with NO reward is ranked strictly
        // BELOW a lower-level rewarded one even with a purpose carried: the
        // unrestricted control picks the OTHER quest. The reward lives on a
        // Supply component (QuestFixtureRow derives RewardItem from
        // Supply/Reward QuestActSupplyItem, QuestFixtureRow.cs:486-488).
        GameplayActorTestRig.SeedQuestHunt(PreferredQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 2);
        QuestFixtureRow.Invalidate(PreferredQuestId);
        GameplayActorTestRig.SeedQuestHunt(OtherQuestId, SecondStartComponent, SecondProgressComponent,
            SecondReadyComponent, OfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        SeedSupplyReward(OtherQuestId, OtherSupplyComponent, OtherSupplyAct, RewardItemTemplate);
        QuestFixtureRow.Invalidate(OtherQuestId);
    }

    private static void SeedChosenAndOtherOffers()
    {
        // Per-quest offer NPCs for the T5 release tests: the CHOSEN offer
        // (PreferredQuestId, level 2, no reward) is served ONLY by the
        // chosen-offer NPC; the OTHER offer (OtherQuestId, level 1,
        // rewarded) ONLY by the other-offer NPC. Same levels/ids/reward as
        // SeedBothOffers (same old-ranking preference), split offerers so
        // one offer can go ineligible while the other stays.
        GameplayActorTestRig.SeedQuestHunt(PreferredQuestId, StartComponent, HuntProgressComponent,
            HuntReadyComponent, ChosenOfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 2);
        QuestFixtureRow.Invalidate(PreferredQuestId);
        GameplayActorTestRig.SeedQuestHunt(OtherQuestId, SecondStartComponent, SecondProgressComponent,
            SecondReadyComponent, OtherOfferNpcTemplate, PreyTemplate, ReportNpcTemplate, huntCount: 3, level: 1);
        SeedSupplyReward(OtherQuestId, OtherSupplyComponent, OtherSupplyAct, RewardItemTemplate);
        QuestFixtureRow.Invalidate(OtherQuestId);
    }

    private static void SeedSupplyReward(uint questId, uint supplyComponentId, uint supplyActId, uint itemId)
    {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", Flags)!.GetValue(manager)!;
        var template = questTemplates[questId];
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", Flags)!.GetValue(manager)!;
        if (!componentTemplates.TryGetValue(supplyComponentId, out var supply))
        {
            supply = new QuestComponentTemplate(template) { Id = supplyComponentId, KindId = QuestComponentKind.Supply };
            componentTemplates[supplyComponentId] = supply;
        }
        if (!template.Components.ContainsKey(supplyComponentId))
            template.Components[supplyComponentId] = supply;
        if (!supply.ActTemplates.OfType<QuestActSupplyItem>().Any(a => a.ItemId == itemId))
            supply.ActTemplates.Add(new QuestActSupplyItem(supply)
            {
                ActId = supplyActId, DetailId = supplyComponentId, DetailType = nameof(QuestActSupplyItem),
                Count = 1, ItemId = itemId,
            });
    }

    private static (BotRoamStepExecutor Executor, GameplayActor Actor, PlayerBotRuntime Runtime, Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock)
        CreatePursuitRig(string name)
    {
        // Mirrors QuestRunningOwnershipTests.CreateQuestRigWithWildlife
        // (quest 251 gather shape + npc→pack→item loot link): the ONLY rig
        // in this file whose pursuit leg dispatches a Running Move.
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

        var preyObjId = session.SpawnNpc(PursuitPreyTemplate);
        var prey = session.World.GetNpc(preyObjId)!;
        prey.TemplateId = PursuitPreyTemplate;
        prey.Template = new NpcTemplate { Id = PursuitPreyTemplate, Scale = 1f };
        prey.Hp = 100;
        prey.MaxHp = 100;
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, new Vector3(10, 0, 0));
        region.AddObject(prey);
        prey.Region = region;

        actor.Character.Quests.ActiveQuests[PursuitQuestId] = new Quest(actor.Character)
        {
            TemplateId = PursuitQuestId,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
        actor.Character.CurrentTarget = prey;

        SeedPursuitShape();
        QuestFixtureRow.InvalidateAll();

        var runtime = new PlayerBotRuntime(actor.Character, "rig");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
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
        return (executor, actor, runtime, clock);
    }

    private static void SeedPursuitShape()
    {
        // Same shape as QuestRunningOwnershipTests.SeedQuest251GatherAct +
        // SeedBoarLootLink (gather act on Progress + npc→pack→item link).
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", Flags)!.GetValue(manager)!;
        if (!questTemplates.TryGetValue(PursuitQuestId, out var template))
        {
            template = new QuestTemplate { Id = PursuitQuestId, Level = 2 };
            questTemplates[PursuitQuestId] = template;
        }
        if (!template.GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .Any(a => a.ItemId == PursuitItemId))
        {
            var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
                .GetField("_componentTemplates", Flags)!.GetValue(manager)!;
            const uint ProgressComponentId = 2510901;
            if (!componentTemplates.TryGetValue(ProgressComponentId, out var component))
            {
                component = new QuestComponentTemplate(template) { Id = ProgressComponentId, KindId = QuestComponentKind.Progress };
                componentTemplates[ProgressComponentId] = component;
            }
            if (!template.Components.ContainsKey(ProgressComponentId))
                template.Components[ProgressComponentId] = component;
            var act = new QuestActObjItemGather(component)
            {
                ActId = PursuitGatherActId, DetailId = 616, DetailType = nameof(QuestActObjItemGather),
                Count = 3, ItemId = PursuitItemId,
            };
            component.ActTemplates.Add(act);
            var actsByType = (Dictionary<string, Dictionary<uint, QuestActTemplate>>)typeof(QuestManager)
                .GetField("_actTemplatesByDetailType", Flags)!.GetValue(manager)!;
            if (!actsByType.TryGetValue(nameof(QuestActObjItemGather), out var acts))
            {
                acts = [];
                actsByType[nameof(QuestActObjItemGather)] = acts;
            }
            acts[616] = act;
        }

        var mapField = typeof(ItemManager).GetField("_lootPackDroppingNpc", Flags)!;
        if (mapField.GetValue(ItemManager.Instance) is not Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc>> map)
        {
            map = [];
            mapField.SetValue(ItemManager.Instance, map);
        }
        if (!map.TryGetValue(PursuitPreyTemplate, out var rows) || rows.All(r => r.LootPackId != PursuitPackId))
        {
            rows ??= [];
            rows.Add(new AAEmu.Game.Models.Game.Items.Loots.LootPackDroppingNpc
            {
                Id = PursuitPreyTemplate, NpcId = PursuitPreyTemplate, LootPackId = PursuitPackId, DefaultPack = true,
            });
            map[PursuitPreyTemplate] = rows;
        }

        var packField = typeof(AAEmu.Game.GameData.LootGameData).GetField("_lootPacks", Flags)!;
        if (packField.GetValue(AAEmu.Game.GameData.LootGameData.Instance) is not Dictionary<uint, AAEmu.Game.Models.Game.Items.Loots.LootPack> packs)
        {
            packs = [];
            packField.SetValue(AAEmu.Game.GameData.LootGameData.Instance, packs);
        }
        if (!packs.TryGetValue(PursuitPackId, out var pack) || pack.Loots.All(l => l.ItemId != PursuitItemId))
        {
            var loot = new AAEmu.Game.Models.Game.Items.Loots.Loot
            {
                Id = PursuitPackId, Group = 0, ItemId = PursuitItemId, DropRate = 10_000_000,
                MinAmount = 1, MaxAmount = 1, LootPackId = PursuitPackId, GradeId = 0, AlwaysDrop = false,
            };
            packs[PursuitPackId] = new AAEmu.Game.Models.Game.Items.Loots.LootPack
            {
                Id = PursuitPackId, Loots = [loot],
                LootsByGroupNo = new Dictionary<uint, List<AAEmu.Game.Models.Game.Items.Loots.Loot>> { [0] = [loot] },
                Groups = [], ActabilityGroups = [], GroupCount = 1,
            };
        }
        QuestFixtureRow.InvalidateAll();
    }

    private static Func<Character, float, IEnumerable<Npc>> NearbyFrom(HeadlessSession session, uint offerObjId)
        => (_, _) => [session.World.GetNpc(offerObjId)!];

    [Test]
    public async Task T1_TwoOffers_ChoosesNonPreferred_AcceptsChosenOnly()
    {
        // T1 (direct QuestDirector.Run calls, no scheduler): the CHOSEN quest
        // (93753, level 2, no reward) is what the OLD unrestricted ranking
        // demonstrably would NOT prefer: priority = AcceptPriority +
        // (BandMax - level) is strictly lower than the OTHER offer's
        // (93754, level 1), and the reward hook only breaks priority ties
        // (QuestBehavior.AcceptRank + BotDecisionSelector order: priority
        // desc, weight desc, quest-id tie-break asc). (a) On a SEPARATE
        // CLEAN fixture with ChosenQuestId=0 the unrestricted run accepts
        // the OTHER quest; (b) on a FRESH actor with the purpose carried,
        // the run accepts ONLY the chosen quest (the other is never
        // accepted: absent from ActiveQuests and from the completed
        // AcceptQuest audit rows). The two runs never share mutated state.
        SeedBothOffers();
        var (controlActor, controlSession) = CreateAreaActor("chosen-accept-t1-control");
        var controlOffer = SpawnAreaNpc(controlSession, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        // Re-seed synchronously immediately before the run: sibling suites
        // swap Singleton<QuestManager> mid-process (fresh empty tables), so
        // seeds staged before an await gap may be gone by Run time. All
        // Seed* helpers are idempotent (same keys/values), and seeds touch
        // templates only — never character quest state.
        SeedBothOffers();
        var control = QuestDirector.Run(controlActor, NearbyFrom(controlSession, controlOffer),
            new QuestDecisionScenario.QuestOptions { CycleId = "chosen-accept-t1-control", ChosenQuestId = 0 });
        await Assert.That(control.WorkSelected).IsTrue().Because($"fail={control.FailStage}: {control.FailReason}");
        await Assert.That(control.SelectedAction).IsEqualTo(ActorActionType.AcceptQuest);
        await Assert.That(QuestBehavior.QuestIdOf(control.SelectedProposal!)).IsEqualTo(OtherQuestId)
            .Because("the unrestricted control must prefer the lower-level rewarded lower-id offer");
        await Assert.That(controlActor.Character.Quests!.ActiveQuests.ContainsKey(OtherQuestId)).IsTrue();
        await Assert.That(controlActor.Character.Quests!.ActiveQuests.ContainsKey(PreferredQuestId)).IsFalse();

        var (actor, session) = CreateAreaActor("chosen-accept-t1");
        var offerObjId = SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        SeedBothOffers(); // re-seed after the await gap (singleton-swap race, see above)
        var result = QuestDirector.Run(actor, NearbyFrom(session, offerObjId),
            new QuestDecisionScenario.QuestOptions { CycleId = "chosen-accept-t1", ChosenQuestId = PreferredQuestId });

        await Assert.That(result.WorkSelected).IsTrue().Because($"fail={result.FailStage}: {result.FailReason}");
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.AcceptQuest);
        await Assert.That(result.SelectedQuestId).IsEqualTo(PreferredQuestId);
        await Assert.That(result.PurposeSwitchReason).IsEqualTo("");
        await Assert.That(QuestBehavior.QuestIdOf(result.SelectedProposal!)).IsEqualTo(PreferredQuestId);
        await Assert.That(actor.Character.Quests!.ActiveQuests.ContainsKey(PreferredQuestId)).IsTrue()
            .Because("the chosen offer must be accepted through the real engine path");
        await Assert.That(actor.Character.Quests!.ActiveQuests.ContainsKey(OtherQuestId)).IsFalse()
            .Because("the unchosen offer must NOT be accepted while a purpose is carried");
        var acceptedTargets = actor.AuditTrace
            .Where(r => r.Action == ActorActionType.AcceptQuest && r.Result == ActorLifecycleState.Completed)
            .Select(r => r.TargetId)
            .ToList();
        await Assert.That(acceptedTargets.Contains(OtherQuestId)).IsFalse();
        await Assert.That(acceptedTargets.Contains(PreferredQuestId)).IsTrue();
    }

    [Test]
    public async Task T2_ChosenActivePlusAttractiveOffer_WorkServesChosen_NoUnrelatedAccept()
    {
        // T2: a chosen active quest and another attractive offer coexist;
        // ordinary work serves the chosen quest without accepting the
        // unrelated offer.
        var (actor, session) = CreateAreaActor("chosen-accept-t2");
        SeedBothOffers();
        var offerObjId = SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        await Assert.That(actor.AcceptQuest(PreferredQuestId, QuestAcceptorType.Npc, OfferNpcTemplate).State)
            .IsEqualTo(ActorLifecycleState.Completed);

        SeedBothOffers(); // re-seed after the await gap (singleton-swap race, see T1)
        var result = QuestDirector.Run(actor, NearbyFrom(session, offerObjId),
            new QuestDecisionScenario.QuestOptions { CycleId = "chosen-accept-t2", ChosenQuestId = PreferredQuestId });

        await Assert.That(result.WorkSelected).IsTrue().Because($"fail={result.FailStage}: {result.FailReason}");
        await Assert.That(QuestBehavior.QuestIdOf(result.SelectedProposal!)).IsEqualTo(PreferredQuestId);
        await Assert.That(result.SelectedQuestId).IsEqualTo(PreferredQuestId);
        await Assert.That(result.PurposeSwitchReason).IsEqualTo("");
        await Assert.That(actor.Character.Quests!.ActiveQuests.ContainsKey(OtherQuestId)).IsFalse()
            .Because("the unrelated attractive offer must NOT be accepted while the chosen quest is active");
        var acceptedTargets = actor.AuditTrace
            .Where(r => r.Action == ActorActionType.AcceptQuest && r.Result == ActorLifecycleState.Completed)
            .Select(r => r.TargetId)
            .ToList();
        await Assert.That(acceptedTargets.Contains(OtherQuestId)).IsFalse();
        await Assert.That(result.Request is { TargetId: var target } && target == OtherQuestId).IsFalse();
    }

    [Test]
    public async Task T3_ChosenOfferUnavailable_NoUnrelatedAction_ActualReason()
    {
        // T3 (direct QuestDirector.Run call, no scheduler): the chosen offer
        // becomes unavailable (completed non-repeatable here — the engine's
        // own accept gate). No unrelated quest action is dispatched, and the
        // result names the ACTUAL engine-gate reason. (Advisory only: the
        // SelectedQuestId/PurposeSwitchReason fields below have NO production
        // reader — the read-only review confirmed zero consumers in
        // AAEmu.Game. Real release is owned by QuestBootstrapActivityModule
        // and proven by T5 on GetChosenQuest, not by these fields.)
        var (actor, session) = CreateAreaActor("chosen-accept-t3");
        SeedBothOffers();
        var offerObjId = SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        await Assert.That(actor.AcceptQuest(PreferredQuestId, QuestAcceptorType.Npc, OfferNpcTemplate).State)
            .IsEqualTo(ActorLifecycleState.Completed);
        actor.Character.Quests!.DropQuest(PreferredQuestId, true);
        actor.Character.Quests!.SetCompletedQuestFlag(PreferredQuestId, true);

        SeedBothOffers(); // re-seed after the await gap (singleton-swap race, see T1; templates only — completed flag on the character is untouched)
        var result = QuestDirector.Run(actor, NearbyFrom(session, offerObjId),
            new QuestDecisionScenario.QuestOptions { CycleId = "chosen-accept-t3", ChosenQuestId = PreferredQuestId });

        await Assert.That(result.WorkSelected).IsFalse();
        await Assert.That(result.Request).IsNull()
            .Because("no unrelated quest action may be dispatched when the chosen quest is unavailable");
        await Assert.That(result.PurposeSwitchReason).Contains($"chosen={PreferredQuestId}");
        await Assert.That(result.PurposeSwitchReason).Contains("COMPLETED_NON_REPEATABLE")
            .Because("the result names the ACTUAL engine-gate reason, not a generic stale label");
        var questActions = actor.AuditTrace
            .Where(r => r.Action is ActorActionType.AcceptQuest or ActorActionType.AdvanceQuest
                or ActorActionType.TurnInQuest or ActorActionType.TurnInDoodad or ActorActionType.AutoTurnIn
                && r.Result == ActorLifecycleState.Completed)
            .Select(r => (r.Action, r.TargetId))
            .ToList();
        await Assert.That(questActions.Any(q => q.TargetId == OtherQuestId)).IsFalse()
            .Because("no unrelated quest action may be dispatched for the unavailable choice");
    }

    [Test]
    public async Task T3b_ChosenOfferStillAvailable_NeverLabeledStale()
    {
        // A valid fresh offer that has not been accepted is NOT a stale
        // choice: the wake proposes it without any stale label.
        var (actor, session) = CreateAreaActor("chosen-accept-t3b");
        SeedBothOffers();
        var offerObjId = SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));

        SeedBothOffers(); // re-seed immediately before the run (singleton-swap race, see T1)
        var result = QuestDirector.Run(actor, NearbyFrom(session, offerObjId),
            new QuestDecisionScenario.QuestOptions { CycleId = "chosen-accept-t3b", ChosenQuestId = OtherQuestId });

        await Assert.That(result.WorkSelected).IsTrue().Because($"fail={result.FailStage}: {result.FailReason}");
        await Assert.That(QuestBehavior.QuestIdOf(result.SelectedProposal!)).IsEqualTo(OtherQuestId);
        await Assert.That(result.PurposeSwitchReason).IsEqualTo("");
        await Assert.That(result.PurposeSwitchReason.Contains("stale")).IsFalse();
    }

    [Test]
    public async Task T4a_SafetyVeto_Preempts_NoOrdinaryDispatch()
    {
        // T4a (direct QuestDirector.Run call, no scheduler): safety
        // interrupts the quest — a vetoed wake dispatches no ordinary quest
        // work even with a purpose carried.
        var (actor, session) = CreateAreaActor("chosen-accept-t4a");
        SeedBothOffers();
        var offerObjId = SpawnAreaNpc(session, OfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        await Assert.That(actor.AcceptQuest(PreferredQuestId, QuestAcceptorType.Npc, OfferNpcTemplate).State)
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

        SeedBothOffers(); // re-seed after the await gap (singleton-swap race, see T1)
        var vetoed = QuestDirector.Run(actor, NearbyFrom(session, offerObjId),
            new QuestDecisionScenario.QuestOptions { CycleId = "chosen-accept-t4a", ChosenQuestId = PreferredQuestId });
        await Assert.That(vetoed.WorkSelected).IsFalse();
        await Assert.That(vetoed.Request).IsNull();
        await Assert.That(vetoed.Rejections.Count).IsGreaterThan(0);
        await Assert.That(vetoed.Rejections.All(r => r.Reason.Contains("survival-veto-clear"))).IsTrue();
    }

    [Test]
    public async Task T4b_RunningRequest_SurvivesNextWake_SameReference()
    {
        // T4b (BotRoamStepExecutor quest-leg path, the production executor
        // the scheduler drives; direct StepAsync calls because T1-T4 own no
        // scheduler): with the purpose carried, REQUIRE a nonterminal
        // request first (FAIL when pursuit never starts), THEN verify the
        // exact SAME request reference survives the next wake (no
        // re-selection, no dispatch). Rig mirrors the proven
        // QuestRunningOwnershipTests pursuit shape (quest 251 + gather act +
        // npc→pack→item loot link): the hunt fixture SeedQuestHunt alone
        // cannot resolve the funnel source (no loot link), so the pursuit
        // leg would never dispatch here — that rigging is the documented
        // precondition, not a shortcut.
        var (executor, actor, runtime, clock) = CreatePursuitRig("chosen-accept-t4b");
        // The rig injects the ACTIVE quest directly (same as
        // QuestRunningOwnershipTests): no AcceptQuest call — already work.
        await Assert.That(actor.Character.Quests!.ActiveQuests.ContainsKey(PursuitQuestId)).IsTrue();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.ActiveRequest is { IsTerminal: false, Action: ActorActionType.Move }).IsTrue()
            .Because("pursuit MUST start: a nonterminal Move is required before the hold can be proven — FAIL when it never starts");
        var running = actor.ActiveRequest!;
        await Assert.That(executor.TryGetQuestRuntime(runtime.CharacterId, out var questRuntime)).IsTrue();
        await Assert.That(questRuntime!.OwnsRunningRequest(running)).IsTrue();
        var movesBefore = actor.AuditTrace.Count(r => r.Action == ActorActionType.Move);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await executor.StepAsync(runtime, CancellationToken.None);
        await Assert.That(actor.ActiveRequest).IsSameReferenceAs(running);
        await Assert.That(questRuntime.OwnsRunningRequest(actor.ActiveRequest)).IsTrue()
            .Because("a running request remains owned until it ends");
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move))
            .IsEqualTo(movesBefore)
            .Because("the hold wake must dispatch nothing — the owned leg is still Running");
    }
    [Test]
    public async Task T5a_OwnerReselects_AfterNonCompletionIneligibility_ThroughArbiter()
    {
        // T5a (tightest production path available: BotGoalArbiter.Arbitrate
        // is CanActivate's production caller, BotGoalArbiter.cs:164; the
        // module is registered as in Program.cs:383): per-quest offer NPCs —
        // the owner picks the lowest eligible id first (PickQuest's
        // lowest-eligible rule, QuestBootstrapActivityModule.cs:234-238:
        // PreferredQuestId 93753 < OtherQuestId 93754), then the PICKED
        // offer's NPC leaves the world (a NON-completion eligibility change
        // by its own server state: no NPC in census, so no EligibleQuest
        // row) while the OTHER offer NPC stays. A later real arbitration
        // turn MUST change the owner's retained choice (GetChosenQuest) to
        // the other quest. Asserts on the module's own field (_chosenQuest
        // via GetChosenQuest), never on the advisory
        // SelectedQuestId/PurposeSwitchReason (zero production readers). No
        // manual choice clear, no NotifyQuestTerminal call.
        var (actor, session) = CreateAreaActor("chosen-accept-t5a");
        SeedChosenAndOtherOffers();
        var chosenOfferObjId = SpawnAreaNpc(session, ChosenOfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        var otherOfferObjId = SpawnAreaNpc(session, OtherOfferNpcTemplate, SpawnPos + new Vector3(-5, 0, 0));
        _ = otherOfferObjId;
        QuestLegActor.Stage(actor.Character.Id, actor);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "chosen-accept-t5a");
        var arbiter = new BotGoalArbiter([module]);
        SeedChosenAndOtherOffers(); // re-seed immediately before the turn (singleton-swap race, see T1)
        await Assert.That(arbiter.Arbitrate(bot, 12f).Activity?.Name).IsEqualTo(QuestBootstrapActivityModule.ActivityName);

        // The picked offer's NPC leaves the world: the next listing turn is
        // authoritative (CoverageValid, offers known) and the kept quest is
        // absent from ActiveWork + EligibleQuests + ViableHunts, so
        // CanActivate releases it (QuestBootstrapActivityModule.cs:188-193)
        // and re-picks the remaining offer.
        session.World.RemoveObject(session.World.GetNpc(chosenOfferObjId)!);
        SeedChosenAndOtherOffers(); // re-seed after the await gap (templates only — the removed NPC stays removed)
        var listing = BotOpportunityListing.List(actor);
        await Assert.That(listing.CoverageValid).IsTrue();
        await Assert.That(listing.QuestOffersUnknown).IsFalse();
        await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == PreferredQuestId)).IsFalse();
        await Assert.That(listing.EligibleQuests.Any(q => q.QuestId == OtherQuestId)).IsTrue();
        await Assert.That(arbiter.Arbitrate(bot, 12f).Activity?.Name).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(OtherQuestId)
            .Because("the picked offer went ineligible without completing — the owner must re-pick the remaining offer");
    }

    [Test]
    public async Task T5b_OwnerReselects_AfterCompletion_ThroughArbiter()
    {
        // T5b (same arbiter path as T5a): completed-quest coverage through
        // the PRODUCTION terminal channel. The owner picks; the picked quest
        // completes through the REAL engine path (accept, engine hunt
        // credit, AdvanceQuest to Ready, TurnIn landing through a
        // BotBehaviorRuntime whose QuestCompletedSink is assigned EXACTLY as
        // Program.cs:336-337 assigns the executor's QuestTerminalSink —
        // (characterId, questId, completed) => module.NotifyQuestTerminal(...)
        // — which the executor copies onto every quest runtime
        // (BotRoamStepExecutor.cs:2076). The landed TurnIn feeds the sink
        // with the engine-confirmed completion (BotBehaviorRuntime
        // FeedQuestTerminals), the module records it, and the next
        // arbitration turn re-picks. NO direct module.NotifyQuestTerminal
        // call anywhere below; asserts on GetChosenQuest only.
        var (actor, session) = CreateAreaActor("chosen-accept-t5b");
        SeedChosenAndOtherOffers();
        SpawnAreaNpc(session, ChosenOfferNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        SpawnAreaNpc(session, OtherOfferNpcTemplate, SpawnPos + new Vector3(-5, 0, 0));
        var reportObjId = SpawnAreaNpc(session, ReportNpcTemplate, SpawnPos + new Vector3(5, 0, 0));
        QuestLegActor.Stage(actor.Character.Id, actor);
        var module = new QuestBootstrapActivityModule(new QuestBootstrapModuleOptions { Enabled = true });
        var bot = new PlayerBotRuntime(actor.Character, "chosen-accept-t5b");
        var arbiter = new BotGoalArbiter([module]);
        SeedChosenAndOtherOffers(); // re-seed immediately before the turn (singleton-swap race, see T1)
        await Assert.That(arbiter.Arbitrate(bot, 12f).Activity?.Name).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(PreferredQuestId);

        await Assert.That(actor.AcceptQuest(PreferredQuestId, QuestAcceptorType.Npc, ChosenOfferNpcTemplate).State)
            .IsEqualTo(ActorLifecycleState.Completed);
        // Engine hunt credit (the same path real kills take:
        // QuestManagerEvents -> OnMonsterHunt -> AddObjective), then advance
        // to Ready through the real actor path.
        SeedChosenAndOtherOffers(); // re-seed after the await gap (templates only — active quest state is untouched)
        for (var k = 0; k < 3; k++)
            actor.Character.Events.OnMonsterHunt(actor.Character,
                new OnMonsterHuntArgs { NpcId = PreyTemplate, Count = 1 });
        var advance = actor.AdvanceQuest(PreferredQuestId);
        await Assert.That(advance.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests!.ActiveQuests[PreferredQuestId].Status).IsEqualTo(QuestStatus.Ready);

        // TurnIn through the runtime with the production sink shape: the
        // landed turn-in feeds (id, quest, engine-confirmed completed) into
        // the module — never a direct NotifyQuestTerminal call here.
        var questRuntime = new BotBehaviorRuntime
        {
            QuestCompletedSink = (characterId, questId, completed) =>
                module.NotifyQuestTerminal(characterId, questId, completed),
        };
        Func<Character, float, IEnumerable<Npc>> nearby = (_, _) =>
            [session.World.GetNpc(reportObjId)!];
        SeedChosenAndOtherOffers(); // re-seed after the await gap (templates only — Ready quest state is untouched)
        // Turn-in converges over wakes (the return InteractNpc settles
        // first; TurnIn fires on a later settled wake — the ReturnExit
        // contract): pump bounded ticks until the SERVER marks completion.
        for (var turnIn = 0; turnIn < 6 && !actor.Character.Quests!.HasQuestCompleted(PreferredQuestId); turnIn++)
        {
            var ok = questRuntime.Tick(actor, nearby,
                new QuestDecisionScenario.QuestOptions { CycleId = $"chosen-accept-t5b-turnin-{turnIn}", ChosenQuestId = PreferredQuestId },
                QuestBootstrapActivityModule.ActivityName);
            await Assert.That(ok).IsTrue().Because($"fail={questRuntime.LastResult?.FailStage}: {questRuntime.LastResult?.FailReason}");
        }
        await Assert.That(actor.Character.Quests!.HasQuestCompleted(PreferredQuestId)).IsTrue()
            .Because("the turn-in must land the server-side completion through the real engine path");

        SeedChosenAndOtherOffers(); // re-seed after the await gap (templates only — completed flag on the character is untouched)
        await Assert.That(arbiter.Arbitrate(bot, 12f).Activity?.Name).IsEqualTo(QuestBootstrapActivityModule.ActivityName);
        await Assert.That(module.GetChosenQuest(actor.Character.Id)).IsEqualTo(OtherQuestId)
            .Because("the completed quest must never re-pick — the owner moves on");
    }
}
