using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// G5 pursuit rides the TRAVEL BRAIN (the return leg's twin): the closing leg of
/// <see cref="QuestBehavior.PursuitEmit"/> is decided by
/// <see cref="TravelBrain.Decide"/> through <see cref="TravelLegDispatch"/>, so the
/// pursuit gets the same discipline the return leg proved — one arming per prey, the
/// brain's drift hold, a real repath budget, and the named abandonment terminals.
///
/// The leg's own lane vocabulary and dispatch shapes are unchanged (pinned here), and
/// the brain's decision rides additively in the <c>:travel=</c> fragment. The journey
/// is OWNER-TAGGED so the return leg's own boundary can never disarm it.
/// </summary>
[NotInParallel]
public class QuestPursuitTravelTests
{
    private const uint Quest251 = 251;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;
    private const uint Meat4058 = 4058;
    private const uint GatherActId = 10473;
    private const int GatherNeed = 3;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        CombatBrainEngagement.ClearAll();
        GameplayActorTestRig.SeedSkillTemplate(CombatDecisionTree.BasicMeleeAutoAttackSkillId);
        SeedQuest251GatherAct();
        SeedBoarLootLink();
    }

    // ------------------------------------------------------------ decisions

    [Test]
    public async Task OutsideRange_MoveRidesTheTravelBrain_AdditiveFragment()
    {
        var (actor, session) = CreatePursuingActor("g5-travel-far", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var preyObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-far-1" });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.Request!.TargetId).IsEqualTo(preyObjId);
        await Assert.That(result.Request.MoveOwner).IsEqualTo("PURSUIT_MOVE_TO_UNIT");

        // The leg's own lane tokens are unchanged; the brain's arm/verb/mode/
        // terminal/reason ride additively. A unit target selects Follow.
        var detail = result.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail;
        await Assert.That(detail).Contains(":dispatch=move:");
        await Assert.That(detail).Contains(":travel=arm=Follow:verb=MoveToUnit:mode=Follow:terminal=none:");
        await Assert.That(detail).Contains(":reason=FollowSelected:");
        await Assert.That(detail).Contains(":routed=true");

        // The journey is armed for the prey, owned by the pursuit leg, unspent.
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var armed, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(armed.TargetObjId).IsEqualTo(preyObjId);
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);
        await Assert.That(armed.RepathCount).IsEqualTo(0);
    }

    [Test]
    public async Task InGate_StopRidesTheArrivalArm_HoldConfirmThenCombatStillWins()
    {
        var (actor, session) = CreatePursuingActor("g5-travel-near", new Vector3(0, 0, 0), new Vector3(2, 0, 0));
        var preyObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;

        // Wake 1: the brain's ARRIVAL arm owns the Stop — a keeping-station follow,
        // so the intent stays live and no arrival is banked.
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-near-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        var stopDetail = first.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail;
        await Assert.That(stopDetail).Contains(":dispatch=stop:reason=in-range");
        await Assert.That(stopDetail).Contains(":travel=arm=Arrival:verb=Stop:");
        await Assert.That(stopDetail).Contains(":reason=FollowInPosition:");
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var armed, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);

        // Wake 2: settled — the leg's OWN hold-confirm yields, so combat fires.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-near-2" });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.AutoAttack);
        await Assert.That(second.Request!.TargetId).IsEqualTo(preyObjId);
    }

    [Test]
    public async Task LiveLegOnAStaticPrey_BrainHoldsTheLeg()
    {
        var (actor, _) = CreatePursuingActor("g5-travel-held", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-held-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Move);

        // The leg is live and the prey has not moved: the brain's LEG HOLD keeps it.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-held-2" });
        await Assert.That(second.SelectedAction == ActorActionType.Move).IsFalse();
        var detail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail;
        await Assert.That(detail).Contains(":dispatch=held:reason=drift-held(drift=0.0m)");
        await Assert.That(detail).Contains(":travel=arm=LegHold:verb=Hold:");
        await Assert.That(detail).Contains(":reason=LegLive:");
        // The intent's own counters prove the hold restarted nothing.
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var still, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(still.RepathCount).IsEqualTo(0);
    }

    /// <summary>
    /// THE CALLER'S OWN RETRACK IS NOT A FAILURE: when the prey walks off the point
    /// the live leg was issued against, the leg preempts that leg ITSELF. If that
    /// self-preemption were read as a foreign interruption, every retrack would spend
    /// a repath and a healthy pursuit would abandon after two — so this pins that the
    /// budget survives the retrack.
    /// </summary>
    [Test]
    public async Task CallerRetrack_DoesNotSpendTheRepathBudget()
    {
        var (actor, session) = CreatePursuingActor("g5-travel-retrack", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var preyObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;

        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-retrack-1" });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Move);

        // The prey walks off the issued point, but stays outside the 3.0 m radius.
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, new Vector3(20, 0, 0));

        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-retrack-2" });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Move);
        var detail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail;
        await Assert.That(detail).Contains(":reason=retrack");
        await Assert.That(detail).Contains(":repaths=0");
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var armed, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(armed.RepathCount).IsEqualTo(0);
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);

        // The leg that ran before the retrack was retired by the CALLER itself — the
        // preemption detail is the shared constant, so the two can never drift.
        var retired = actor.AuditTrace.Single(r => r.Action == ActorActionType.Move
            && r.MoveOwner == "PURSUIT_MOVE_TO_UNIT");
        await Assert.That(retired.Result).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(retired.Detail).Contains(TravelLegDispatch.PursuitRetrackDetail);
    }

    /// <summary>
    /// THE ABANDONMENT VOCABULARY, observed by the caller: a pursuit leg the brain
    /// cannot walk (here a foreign preemption of every closing leg) spends the repath
    /// budget and then abandons UNREACHABLE — never a bare navigation failure. The
    /// intent carries that named terminal stickily and no fourth leg is dispatched.
    /// </summary>
    [Test]
    public async Task SpentRepathBudget_AbandonsUnreachable_NamedTerminal()
    {
        var (actor, _) = CreatePursuingActor("g5-travel-unreach", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        // Wakes 1-3: each closing leg is preempted by something OUTSIDE the travel
        // intent (the brain's Interrupted leg-failure evidence). The budget is two
        // repaths, so the THIRD failure abandons.
        var diags = new List<string>();
        for (var wake = 1; wake <= 3; wake++)
        {
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"g5-travel-unreach-{wake}" });
            diags.Add(run.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail);
            await Assert.That(run.SelectedAction).IsEqualTo(ActorActionType.Move);
            await Assert.That(actor.PreemptCurrent($"unit-test foreign preempt {wake}")).IsTrue();
        }
        await Assert.That(diags[0]).Contains(":reason=FollowSelected:");
        await Assert.That(diags[1]).Contains(":reason=Repathed:");
        await Assert.That(diags[2]).Contains(":reason=Repathed:");
        await Assert.That(diags[2]).Contains(":repaths=2");

        // Wake 4: the budget is spent — the journey is abandoned with the NAMED
        // terminal, and the leg dispatches nothing.
        var abandon = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-unreach-4" });
        await Assert.That(abandon.SelectedAction == ActorActionType.Move).IsFalse();
        var abandonDetail = abandon.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail;
        await Assert.That(abandonDetail).Contains(":dispatch=withdrawn:reason=terminal-unreachable");
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var abandoned, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(abandoned.Terminal).IsEqualTo(TravelTerminal.Unreachable);
        await Assert.That(abandoned.Reason).IsEqualTo(TravelReason.RepathExhausted);

        // STICKY: a later wake re-reads the same named verdict, still dispatching nothing.
        var reRead = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-unreach-5" });
        await Assert.That(reRead.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var still, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(still.Terminal).IsEqualTo(TravelTerminal.Unreachable);

        // Exactly three legs were ever issued (no leg to the abandoned destination).
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move
            && r.MoveOwner == "PURSUIT_MOVE_TO_UNIT")).IsEqualTo(3);
    }

    /// <summary>
    /// The ADDITIVE FALLBACK: an actor with no object identity cannot own a journey
    /// (<see cref="TravelIntentStore.Arm"/> refuses id 0), so the leg keeps its own
    /// pre-brain rule — the same Move, the same tokens, and the diag names the
    /// fallback rather than a brain arm.
    /// </summary>
    [Test]
    public async Task NoJourneyIdentity_FallsBackToThePreBrainRule()
    {
        var (actor, session) = CreatePursuingActor("g5-travel-fallback", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var preyObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;
        actor.Character.ObjId = 0;

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g5-travel-fallback-1" });

        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.Request!.TargetId).IsEqualTo(preyObjId);
        var detail = result.LegEvidence.Single(e => e.Leg == QuestLegId.Pursuit).Detail;
        await Assert.That(detail).Contains(":dispatch=move:reason=fresh");
        await Assert.That(detail).Contains(":travel=fallback");
        await Assert.That(TravelIntentStore.Count).IsEqualTo(0);
    }

    /// <summary>
    /// THE OWNER-KEYED JOURNEY (why the store keys by owning leg): the quest wake runs
    /// TWO travel legs, and the return leg's own boundary must not disarm the pursuit
    /// leg's armed journey — otherwise the pursuit's budget would reset every wake and
    /// its named abandonment terminal would be unreachable. Each leg's boundary drops
    /// its OWN journey and nothing else.
    /// </summary>
    [Test]
    public async Task JourneyIsOwnerScoped_OneLegsBoundaryNeverDisarmsAnothers()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("g5-travel-owner");

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Unit, Boar3475, new Vector3(10, 0, 0),
            followRequested: true, legOwner: TravelLegDispatch.PursuitMoveOwner);
        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Unit, 3512, new Vector3(30, 0, 0),
            followRequested: true, legOwner: TravelLegDispatch.ReturnMoveOwner);
        await Assert.That(TravelIntentStore.Count).IsEqualTo(2);

        // The return leg's own boundary (the quest stopped being Ready) ...
        await Assert.That(TravelLegDispatch.EndJourney(actor, TravelLegDispatch.ReturnMoveOwner)).IsTrue();
        // ... leaves the pursuit leg's journey armed ...
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out var pursuit, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(pursuit.TargetObjId).IsEqualTo(Boar3475);
        await Assert.That(TravelIntentStore.TryGet(
            actor.ActorId, out _, TravelLegDispatch.ReturnMoveOwner)).IsFalse();

        // ... and the pursuit leg's own boundary drops only its own.
        await Assert.That(TravelLegDispatch.EndJourney(actor, TravelLegDispatch.PursuitMoveOwner)).IsTrue();
        await Assert.That(TravelIntentStore.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PriorityLayout_PursuitStillAboveCombatBelowAssignment()
    {
        var opts = new QuestDecisionScenario.QuestOptions();
        await Assert.That(opts.ObjectivePursuitPriority).IsEqualTo(24);
        await Assert.That(opts.ObjectivePursuitPriority < opts.ObjectiveTargetPriority).IsTrue();
        await Assert.That(opts.ObjectivePursuitPriority > opts.ObjectiveCombatPriority).IsTrue();
    }

    /// <summary>
    /// THE TWO QUEST VERDICT TABLES ARE ONE VOCABULARY: the pursuit table is the
    /// return table's twin, and every brain-routed arm must produce the same
    /// dispatch/reason tokens for both callers. The ONE deliberate difference is the
    /// FALLBACK in-gate arm: the return leg's reporter is an NPC to talk to (interact
    /// once settled), while the pursuit leg's prey is a unit to hold off (never a
    /// conversation) — so a diverging fallback is the contract, not a drift.
    /// </summary>
    [Test]
    public async Task BrainRoutedArms_ShareOneTokenVocabulary_TwinTables()
    {
        var (actor, session) = CreatePursuingActor("g5-travel-twin", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var preyObjId = session.World.GetNpcByTemplateId(Boar3475)!.ObjId;

        TravelBrainPlanner.Arm(actor.ActorId, TravelTargetKind.Unit, preyObjId, new Vector3(10, 0, 0),
            followRequested: true, legOwner: TravelLegDispatch.PursuitMoveOwner);
        var prepared = TravelLegDispatch.Prepare(
            actor, preyObjId, new Vector3(10, 0, 0), 3.0f, legLive: false,
            TravelLegOutcome.Running, legOwner: TravelLegDispatch.PursuitMoveOwner);
        await Assert.That(prepared).IsNotNull();

        var pursuit = TravelLegDispatch.DecidePursuitLeg(prepared, false, false, "fresh", "fresh");
        var ret = TravelLegDispatch.DecideReturnLeg(prepared, false, false, false, "fresh", "fresh");
        await Assert.That(pursuit.DispatchToken).IsEqualTo(ret.DispatchToken);
        await Assert.That(pursuit.Reason).IsEqualTo(ret.Reason);
        await Assert.That(pursuit.Terminal).IsEqualTo(ret.Terminal);
        await Assert.That(pursuit.HasLeg).IsEqualTo(ret.HasLeg);

        // The abandoned-journey arm names the same terminal token for both.
        TravelIntentStore.BankTerminal(
            actor.ActorId, TravelTerminal.Unreachable, TravelReason.RepathExhausted,
            TravelLegDispatch.PursuitMoveOwner);
        var settled = TravelLegDispatch.Prepare(
            actor, preyObjId, new Vector3(10, 0, 0), 3.0f, legLive: false,
            TravelLegOutcome.None, legOwner: TravelLegDispatch.PursuitMoveOwner);
        var pursuitAbandon = TravelLegDispatch.DecidePursuitLeg(settled, false, false, "fresh", "fresh");
        var retAbandon = TravelLegDispatch.DecideReturnLeg(settled, false, false, false, "fresh", "fresh");
        await Assert.That(pursuitAbandon.DispatchToken).IsEqualTo("withdrawn");
        await Assert.That(pursuitAbandon.Reason).IsEqualTo(retAbandon.Reason);
        await Assert.That(pursuitAbandon.Reason).IsEqualTo("terminal-unreachable");

        // The deliberate divergence: the PRE-BRAIN in-gate arm (hold-and-stay for the
        // prey, hold-then-dial for the reporter).
        var pursuitFallback = TravelLegDispatch.DecidePursuitLeg(null, true, false, "fresh", "fresh");
        var retFallback = TravelLegDispatch.DecideReturnLeg(null, true, false, false, "fresh", "fresh");
        await Assert.That(pursuitFallback.DispatchToken).IsEqualTo("stop");
        await Assert.That(retFallback.DispatchToken).IsEqualTo("stop");
        var retSettled = TravelLegDispatch.DecideReturnLeg(null, true, true, false, "fresh", "fresh");
        await Assert.That(retSettled.DispatchToken).IsEqualTo("interact");
        await Assert.That(retSettled.Verb).IsEqualTo(TravelLegDispatch.ReturnLegVerb.InteractNpc);
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// Drives 251 to Progress (the kill-to-gather shape) through the real engine
    /// surfaces, spawns the live prey 3475, assigns it as the committed target (the
    /// Target-25 leg's own job, pre-staged as the sibling G6 rig does), and poses
    /// actor/prey. Returns the actor mid-pursuit.
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreatePursuingActor(
        string name, Vector3 actorPos, Vector3 preyPos)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        // A healthy actor: a low HP ratio would publish the survival flee/recover
        // facts (and the veto precondition would rightly reject the pursuit leg).
        actor.Character.Hp = actor.Character.MaxHp;

        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var preyObjId = session.SpawnNpc(Boar3475);
        var prey = session.World.GetNpc(preyObjId)!;
        prey.Template = new NpcTemplate { Id = Boar3475, Scale = 1f };
        prey.Hp = 100;
        prey.MaxHp = 100;
        prey.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, preyObjId, preyPos);
        region.AddObject(prey);
        prey.Region = region;

        actor.Character.Quests.ActiveQuests[Quest251] = new Quest(actor.Character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };
        actor.Character.CurrentTarget = prey;
        return (actor, session);
    }

    /// <summary>
    /// Additive, missing-only static surface for the 251 funnel: the Progress gather
    /// act for item 4058 (objective resolution) and the 3475→4530→4058 loot link
    /// (source resolution). Canonical pilot data is never clobbered.
    /// </summary>
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
        if (!map.TryGetValue(Boar3475, out var rows) || rows.All(r => r.LootPackId != Pack4530))
        {
            rows ??= [];
            rows.Add(new LootPackDroppingNpc
            {
                Id = Boar3475,
                NpcId = Boar3475,
                LootPackId = Pack4530,
                DefaultPack = true,
            });
            map[Boar3475] = rows;
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

        // The 251 row memo is derived from exactly these two tables: a staged link
        // must never be shadowed by a row memoized from the prior staging.
        QuestFixtureRow.InvalidateAll();
    }
}
