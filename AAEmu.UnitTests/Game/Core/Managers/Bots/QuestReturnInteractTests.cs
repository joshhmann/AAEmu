using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using AAEmu.UnitTests.Game.Quests.Scenario;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// G8b quest-owned return: Ready 251 + live reporter 3512 drives a
/// Move (outside 25 m) → Stop (inside, unsettled) → InteractNpc (settled)
/// chain through <see cref="QuestDecisionScenario.Run"/> on the REAL headless
/// actor (Move/Stop/InteractNpc all execute the engine verbs — no fakes).
/// The return goal (quest.return) is NOT the turn-in goal, so the E2E withhold
/// seam never swallows the leg; while the return slice owns the wake the
/// priority-30 TurnIn is wake-suppressed (it would otherwise win every Ready
/// wake and the leg could never dispatch). Reporter lost/recycled withdraws
/// with a named reason and dispatches nothing.
/// </summary>
[NotInParallel]
public class QuestReturnInteractTests
{
    private const uint Quest251 = 251;
    private const uint Reporter3512 = 3512;
    private const uint Meat4058 = 4058;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        TravelIntentStore.ClearAll();
        // The Stage 3 plan gate derives the 251 row's prey/pack through the
        // loot chain; without the 3475→4530→4058 link seeded the row classifies
        // UNPROVEN-SOURCE and the plan carries no legs (nothing to drive).
        SeedBoarLootLink();
    }

    [Test]
    public async Task OutsideRange_MoveProposed_WithholdDoesNotSwallow()
    {
        var (actor, session) = CreateReadyActor("g8b-return-far", new Vector3(0, 0, 0), new Vector3(30, 0, 0));
        var reporterObjId = session.World.GetNpcByTemplateId(Reporter3512)!.ObjId;

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-far-1", WithholdTurnIn = true });

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.Request!.TargetId).IsEqualTo(reporterObjId);
        await Assert.That(result.Request.MoveOwner).IsEqualTo("RETURN_MOVE_TO_UNIT");
        // TurnIn suppressed this wake: no WITHHELD observable, the leg owns it.
        await Assert.That(result.FailStage).IsNotEqualTo("WITHHELD");
        await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Status).IsEqualTo(QuestStatus.Ready);
        // THE TRAVEL BRAIN DECIDED IT (first real dispatch caller): the leg's own
        // lane tokens are unchanged, and the additive :travel= fragment names the
        // brain's arm/verb/mode/terminal/reason. A unit target selects Follow.
        var detail = result.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail;
        await Assert.That(detail).Contains(":dispatch=move:");
        await Assert.That(detail).Contains(":travel=arm=Follow:verb=MoveToUnit:mode=Follow:terminal=none:");
        await Assert.That(detail).Contains(":routed=true");
    }

    [Test]
    public async Task InGate_StopThenInteract_RideTheBrainArrivalArm()
    {
        var (actor, _) = CreateReadyActor("g8b-brain-near", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-brain-near-1", WithholdTurnIn = true });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        var stopDetail = first.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail;
        await Assert.That(stopDetail).Contains(":dispatch=stop:reason=in-range");
        // The brain's ARRIVAL arm owns the Stop (a keeping-station follow: the
        // intent stays live, so no arrival is banked and the next wake re-decides).
        await Assert.That(stopDetail).Contains(":travel=arm=Arrival:verb=Stop:");
        await Assert.That(stopDetail).Contains(":reason=FollowInPosition:");

        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-brain-near-2", WithholdTurnIn = true });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.InteractNpc);
        var interactDetail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail;
        await Assert.That(interactDetail).Contains(":dispatch=interact:reason=settled");
        await Assert.That(interactDetail).Contains(":travel=arm=Arrival:verb=Stop:");
    }

    [Test]
    public async Task InsideRange_StopFirstThenInteractNpcAfterSettle()
    {
        var (actor, session) = CreateReadyActor("g8b-return-near", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var reporterObjId = session.World.GetNpcByTemplateId(Reporter3512)!.ObjId;

        // Wake 1: inside 25 m but unsettled — audited Stop wins (TurnIn suppressed).
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-near-1", WithholdTurnIn = true });
        await Assert.That(first.WorkSelected).IsTrue();
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(first.Request!.TargetId).IsEqualTo(0u); // Stop is targetless by engine design (NewRequest(Stop, 0)); the reporter rides the proposal + hold memory
        await Assert.That(first.Request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(first.Request.Detail).Contains("stopped");

        // Wake 2: nobody moved — Stop hold-confirms away, InteractNpc fires.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-near-2", WithholdTurnIn = true });
        await Assert.That(second.WorkSelected).IsTrue();
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.InteractNpc);
        await Assert.That(second.Request!.TargetId).IsEqualTo(reporterObjId);
        await Assert.That(second.Request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(second.Request.Detail).Contains("3512");
        // Dialogue fallback, never Talk: no quest credit, quest untouched.
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Talk)).IsFalse();
        await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Status).IsEqualTo(QuestStatus.Ready);
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.TurnInQuest
            or ActorActionType.TurnInDoodad or ActorActionType.AutoTurnIn)).IsFalse();
    }

    [Test]
    public async Task LiveReturnLeg_DriftHeld_TurnInStillWithheld()
    {
        var (actor, _) = CreateReadyActor("g8b-return-held", new Vector3(0, 0, 0), new Vector3(30, 0, 0));

        // Wake 1: return Move dispatches (leg live afterwards — no ticks headless).
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-held-1", WithholdTurnIn = true });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Move);

        // Wake 2: leg live + reporter static (drift 0) — return withdraws
        // (drift-held), TurnIn flows and the withhold seam catches it:
        // observable proposal, null dispatch, quest stays Ready.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-held-2", WithholdTurnIn = true });
        await Assert.That(second.WorkSelected).IsFalse();
        await Assert.That(second.FailStage).IsEqualTo("WITHHELD");
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.TurnInQuest);
        await Assert.That(second.Request).IsNull();
        await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Status).IsEqualTo(QuestStatus.Ready);
    }

    [Test]
    public async Task ReporterLost_WithdrawsReturn_NoDispatch_QuestStaysReady()
    {
        var (actor, session) = CreateReadyActor("g8b-return-lost", new Vector3(0, 0, 0), new Vector3(30, 0, 0));
        // Recycle the reporter away: GetNpcByTemplateId(3512) now resolves
        // null (named reason "reporter-lost" in the funnel return diag).
        session.World.GetNpcByTemplateId(Reporter3512)!.TemplateId = 9999;

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-lost-1", WithholdTurnIn = true });

        await Assert.That(result.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(result.SelectedAction == ActorActionType.Stop).IsFalse();
        await Assert.That(result.SelectedAction == ActorActionType.InteractNpc).IsFalse();
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Move
            or ActorActionType.Stop or ActorActionType.InteractNpc
            or ActorActionType.TurnInQuest or ActorActionType.TurnInDoodad or ActorActionType.AutoTurnIn)).IsFalse();
        await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Status).IsEqualTo(QuestStatus.Ready);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(Quest251)).IsTrue();
    }

    /// <summary>
    /// THE ABANDONMENT VOCABULARY, observed by the caller: a leg the brain cannot
    /// walk (here a foreign preemption of every closing leg) spends the repath
    /// budget and then abandons UNREACHABLE. The intent then carries that NAMED
    /// terminal (the store's sticky verdict, which is what a consumer re-reads), no
    /// fourth leg is ever dispatched to the abandoned destination, and the wake is
    /// released to TurnIn rather than reported as a bare navigation failure.
    /// </summary>
    [Test]
    public async Task SpentRepathBudget_AbandonsUnreachable_NamedTerminal_NeverABareFail()
    {
        var (actor, _) = CreateReadyActor("g8b-brain-unreachable", new Vector3(0, 0, 0), new Vector3(30, 0, 0));

        // Wakes 1-3: each closing leg is preempted by something OUTSIDE the travel
        // intent (the brain's Interrupted leg-failure evidence). The budget is two
        // repaths, so the THIRD failure abandons.
        var diags = new List<string>();
        for (var wake = 1; wake <= 3; wake++)
        {
            var run = QuestDecisionScenario.Run(actor, (_, _) => [],
                new QuestDecisionScenario.QuestOptions { CycleId = $"g8b-brain-unreach-{wake}", WithholdTurnIn = true });
            diags.Add(run.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail);
            await Assert.That(run.SelectedAction).IsEqualTo(ActorActionType.Move);
            await Assert.That(actor.PreemptCurrent($"unit-test foreign preempt {wake}")).IsTrue();
        }
        await Assert.That(diags[0]).Contains(":travel=arm=Follow:verb=MoveToUnit:mode=Follow:terminal=none:");
        await Assert.That(diags[1]).Contains(":reason=Repathed:");
        await Assert.That(diags[1]).Contains(":repaths=1");
        await Assert.That(diags[2]).Contains(":reason=Repathed:");
        await Assert.That(diags[2]).Contains(":repaths=2");

        // Wake 4: the budget is spent — the journey is abandoned with the NAMED
        // terminal, and the leg dispatches nothing (TurnIn takes the wake back).
        var abandon = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-brain-unreach-4", WithholdTurnIn = true });
        await Assert.That(abandon.SelectedAction == ActorActionType.Move).IsFalse();
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var abandoned, TravelLegDispatch.ReturnMoveOwner)).IsTrue();
        await Assert.That(abandoned.Terminal).IsEqualTo(TravelTerminal.Unreachable);
        await Assert.That(abandoned.Reason).IsEqualTo(TravelReason.RepathExhausted);
        await Assert.That(TravelBrain.Token(abandoned.Terminal)).IsEqualTo("unreachable");

        // STICKY: a later wake re-reads the same named verdict instead of re-deriving
        // a bare failure, and still dispatches nothing.
        QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-brain-unreach-5", WithholdTurnIn = true });
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var reRead, TravelLegDispatch.ReturnMoveOwner)).IsTrue();
        await Assert.That(reRead.Terminal).IsEqualTo(TravelTerminal.Unreachable);

        // Exactly three legs were ever issued, and the reporter was never dialled.
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move
            && r.MoveOwner == "RETURN_MOVE_TO_UNIT")).IsEqualTo(3);
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.InteractNpc)).IsEqualTo(0);
    }

    /// <summary>
    /// THE CALLER'S OWN RETRACK IS NOT A FAILURE: when the reporter walks off the
    /// point the live leg was issued against, the leg preempts that leg ITSELF (the
    /// pre-brain retrack). If that self-preemption were read as a foreign
    /// interruption, every retrack would spend a repath and a healthy journey would
    /// abandon after two — so this pins that the budget survives a retrack and the
    /// leg holds once it is re-issued on the new point.
    /// </summary>
    [Test]
    public async Task CallerRetrack_DoesNotSpendTheRepathBudget()
    {
        var (actor, session) = CreateReadyActor("g8b-brain-retrack", new Vector3(0, 0, 0), new Vector3(30, 0, 0));
        var reporterObjId = session.World.GetNpcByTemplateId(Reporter3512)!.ObjId;

        // Wake 1: the closing leg goes out (fresh journey, no repath).
        var first = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-retrack-1", WithholdTurnIn = true });
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Move);
        var firstDetail = first.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail;
        await Assert.That(firstDetail).Contains(":reason=FollowSelected:");
        await Assert.That(firstDetail).Contains(":repaths=0");

        // The reporter walks off the point the leg was issued against, but stays
        // outside the 25 m gate (so the leg is re-issued rather than halted).
        GameplayActorTestRig.SetNpcPosition(session, reporterObjId, new Vector3(40, 0, 0));

        // Wake 2 re-issued the leg on the moving reporter: the caller's own retrack
        // retired the first leg (Interrupted by the caller's own preemption, NOT a
        // navigation failure) and the budget is untouched.
        var second = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-retrack-2", WithholdTurnIn = true });
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Move);
        var secondDetail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail;
        await Assert.That(secondDetail).Contains(":repaths=0");
        await Assert.That(secondDetail).Contains(":travel=arm=Follow:verb=MoveToUnit:");
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var armed, TravelLegDispatch.ReturnMoveOwner)).IsTrue();
        await Assert.That(armed.RepathCount).IsEqualTo(0);
        await Assert.That(armed.Terminal).IsEqualTo(TravelTerminal.None);
        // The leg that ran before the retrack was retired by the CALLER itself.
        var retired = actor.AuditTrace.Single(r => r.Action == ActorActionType.Move
            && r.MoveOwner == "RETURN_MOVE_TO_UNIT");
        await Assert.That(retired.Result).IsEqualTo(ActorLifecycleState.Interrupted);
        await Assert.That(retired.Detail).Contains("quest return retrack");

        // Wake 3: the re-issued leg now serves the new point, so the brain HOLDS it
        // (no restart) and the return leg withdraws — TurnIn takes the wake back
        // (the same withheld release the pre-brain drift hold produced).
        var third = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-retrack-3", WithholdTurnIn = true });
        await Assert.That(third.FailStage).IsEqualTo("WITHHELD");
        await Assert.That(TravelIntentStore.TryGet(actor.ActorId, out var still, TravelLegDispatch.ReturnMoveOwner)).IsTrue();
        await Assert.That(still.RepathCount).IsEqualTo(0);
        await Assert.That(still.Terminal).IsEqualTo(TravelTerminal.None);
        // Still exactly one retirement — the hold issued no new leg.
        await Assert.That(actor.AuditTrace.Count(r => r.Action == ActorActionType.Move
            && r.MoveOwner == "RETURN_MOVE_TO_UNIT")).IsEqualTo(1);
    }

    /// <summary>
    /// The ADDITIVE FALLBACK: an actor with no object identity cannot own a journey
    /// (<see cref="TravelIntentStore.Arm"/> refuses id 0), so the leg keeps its own
    /// pre-brain rule byte-for-byte — the same Move, the same dispatch/reason tokens,
    /// and the diag names the fallback rather than a brain arm.
    /// </summary>
    [Test]
    public async Task NoJourneyIdentity_FallsBackToThePreBrainRule()
    {
        var (actor, session) = CreateReadyActor("g8b-brain-fallback", new Vector3(0, 0, 0), new Vector3(30, 0, 0));
        var reporterObjId = session.World.GetNpcByTemplateId(Reporter3512)!.ObjId;
        // An actor that owns no journey key: the arm is refused, so the caller's own
        // rule decides (the world registry keeps the old key, so the engine verbs the
        // leg dispatches still resolve).
        actor.Character.ObjId = 0;

        var result = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "g8b-brain-fallback-1", WithholdTurnIn = true });

        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        await Assert.That(result.Request!.TargetId).IsEqualTo(reporterObjId);
        await Assert.That(result.Request.MoveOwner).IsEqualTo("RETURN_MOVE_TO_UNIT");
        var detail = result.LegEvidence.Single(e => e.Leg == QuestLegId.Return).Detail;
        await Assert.That(detail).Contains(":dispatch=move:reason=fresh");
        await Assert.That(detail).Contains(":travel=fallback");
        await Assert.That(TravelIntentStore.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PriorityLayout_ReturnBelowPursuitCombatLoot_AboveAdvance()
    {
        var opts = new QuestDecisionScenario.QuestOptions();
        await Assert.That(opts.ObjectiveReturnPriority).IsEqualTo(21);
        await Assert.That(opts.ObjectiveReturnPriority < opts.ObjectiveLootPriority).IsTrue();
        await Assert.That(opts.ObjectiveReturnPriority < opts.ObjectiveCombatPriority).IsTrue();
        await Assert.That(opts.ObjectiveReturnPriority < opts.ObjectivePursuitPriority).IsTrue();
        await Assert.That(opts.ObjectiveReturnPriority > opts.AdvancePriority).IsTrue();
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// Drives quest 251 to Ready through the real engine surfaces (accept +
    /// real acquisition + gather event + advance, the
    /// GameplayActorQuestActionsTests shape), spawns the live reporter 3512,
    /// and poses actor/reporter. Returns the actor with 251 active Ready.
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateReadyActor(
        string name, Vector3 actorPos, Vector3 reporterPos)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 2;
        PlayerbotPilotRig.RegisterQuestItems(LoadManifest());
        GameplayActorTestRig.AttachCaptureConnection(actor);

        var accept = actor.AcceptQuest(Quest251, QuestAcceptorType.Npc, Reporter3512);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");
        GameplayActorTestRig.GrantItem(actor, Meat4058, 3);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs
        {
            QuestId = Quest251,
            ItemId = Meat4058,
            Count = 3
        });
        var advance = actor.AdvanceQuest(Quest251);
        if (advance.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"advance failed: {advance.State} {advance.Detail}");
        if (actor.Character.Quests.ActiveQuests.GetValueOrDefault(Quest251)?.Status != QuestStatus.Ready)
            throw new InvalidOperationException("quest 251 did not reach Ready");

        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var reporterObjId = session.SpawnNpc(Reporter3512);
        var reporter = session.World.GetNpc(reporterObjId)!;
        reporter.Hp = 100;
        reporter.MaxHp = 100;
        GameplayActorTestRig.SetNpcPosition(session, reporterObjId, reporterPos);
        region.AddObject(reporter);
        reporter.Region = region;
        return (actor, session);
    }

    /// <summary>
    /// Additive, missing-only seed of the 3475→4530→4058 loot link the 251
    /// fixture row resolves its prey/pack through (the same shape the sibling
    /// objective-path rigs use). Canonical pilot data is never clobbered: each
    /// row is added only when the lookup would otherwise resolve empty.
    /// </summary>
    private static void SeedBoarLootLink()
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

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

        // The 251 row memo is derived from exactly these two tables: a staged
        // link must never be shadowed by a row memoized from the prior staging.
        QuestFixtureRow.InvalidateAll();
    }

    private static QuestScenarioManifest LoadManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null &&
               !Directory.Exists(Path.Combine(dir.FullName, ".git")) &&
               !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t1", $"{Quest251}.json");
        return QuestScenarioManifest.LoadFromFile(path);
    }
}
