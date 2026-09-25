using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
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
