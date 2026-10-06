using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Stage 3 fixture gate: <c>QuestDirector.Plan</c> checks every verb a pattern
/// needs against the verified-verb registry BEFORE it builds a leg, so a plan
/// that needs an unproven verb (or an unrecognized act, or an unresolvable
/// source) fails closed with a named <c>HARNESS/UNPROVEN-*</c> reason and ZERO
/// legs — nothing to perceive, nothing to propose, nothing to dispatch.
///
/// The clean case runs the real 251 fixture row off the loaded quest data (the
/// row's prey/pack come through the loot chain, seeded missing-only like the
/// sibling objective-path rigs) against the production registry.
/// </summary>
[NotInParallel]
public class QuestDirectorGateTests
{
    private const uint Quest251 = 251;
    private const uint Quest252 = 252;
    private const uint Boar3475 = 3475;
    private const uint Pack4530 = 4530;

    // --- Real objective data read off AAEmu.Game/Data/compact.sqlite3 (md5
    // 78b3bdbf038db3b927056106efdf91af) — the canonical tables the game server
    // itself loads, not invented fixtures.
    private const uint Quest60 = 60;   // Progress: QuestActObjMonsterHunt act 807 -> npc 611, count 1
    private const uint Quest10 = 10;   // Progress: QuestActObjMonsterGroupHunt act 13979 -> group 84, count 3
    private const uint Quest532 = 532; // Progress: QuestActObjTalk act 3230 -> npc 2426 (first objective act)
    private const uint HuntAct807 = 807u;
    private const uint HuntPrey611 = 611u;
    private const uint GroupHuntAct13979 = 13979u;
    private const uint GroupHuntGroup84 = 84u;
    private const uint TalkAct3230 = 3230u;
    private const uint TalkNpc2426 = 2426u;
    private const uint Meat4058 = 4058;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        SeedBoarLootLink();
    }

    // ------------------------------------------------------- clean 251 plan

    [Test]
    public async Task Plan251_AllVerbsProven_NoFailure_SevenObjectiveLegs()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(fixture.PreyTemplate).IsEqualTo(Boar3475);
        await Assert.That(fixture.PreyPack).IsEqualTo(Pack4530);

        // Every verb the kill-to-gather pattern needs resolves green in the
        // production table — the precondition the clean plan depends on.
        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.KillX))
        {
            var gate = VerifiedVerbRegistry.Instance.Resolve(verbKey);
            await Assert.That(gate.IsGreen).IsTrue();
            await Assert.That(gate.GateId.Length).IsGreaterThan(0);
            await Assert.That(gate.EvidencePath.Length).IsGreaterThan(0);
        }

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.FailStage).IsEqualTo("");
        await Assert.That(plan.FailReason).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.KillX);
        await Assert.That(plan.Legs.Count).IsEqualTo(7);
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.TurnIn, QuestLegId.Target, QuestLegId.Pursuit,
            QuestLegId.Combat, QuestLegId.Loot, QuestLegId.Return
        });
    }

    // --------------------------------------------------- forced-OPEN verb

    [Test]
    public async Task Plan_VerbForcedOpen_FailsFixture_UnprovenVerb_ZeroLegs()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        var registry = new StubVerbRegistry().Force("Loot", VerbGateStatus.Open, "G7c", "g7c-loot-report.json");

        var plan = QuestDirector.Plan(Quest251, fixture, registry);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo("HARNESS/UNPROVEN-VERB verb=Loot gate=G7c evidence=g7c-loot-report.json");
        await AssertNoLegs(plan);
    }

    [Test]
    public async Task Plan_VerbAbsentFromRegistry_FailsFixture_GateAbsent()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        var registry = new StubVerbRegistry().Remove("Loot");

        var plan = QuestDirector.Plan(Quest251, fixture, registry);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo("HARNESS/UNPROVEN-VERB verb=Loot gate=absent evidence=-");
        await AssertNoLegs(plan);
    }

    /// <summary>
    /// A failed plan carries no leg for ANY id, so the wake path has nothing it
    /// could evaluate — the gate precedes leg construction, therefore precedes
    /// the first perception read and every proposal/dispatch.
    /// </summary>
    private static async Task AssertNoLegs(QuestPlan plan)
    {
        foreach (var id in Enum.GetValues<QuestLegId>())
            await Assert.That(plan.Leg(id)).IsNull();
        await Assert.That(plan.Legs.Count).IsEqualTo(0);
    }

    // ------------------------------------------- pattern / source branches

    /// <summary>
    /// A quest with NO Progress objective act is the pure BOOTSTRAP shape: the
    /// generalized director derives a row for every active quest, so "no act"
    /// must be the ordinary bootstrap floor (Advance + TurnIn), not a failure —
    /// otherwise every bootstrap quest in the game would go legless. Nothing is
    /// named as unserved either: there is no gap, the shape is simply
    /// objective-less.
    /// </summary>
    [Test]
    public async Task Plan_NoObjectiveAct_IsBootstrapFloor_NoFailure()
    {
        var fixture = new QuestFixtureRow(Quest251, 0, 0, 0, 0, 0, 0, 0);

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.Unknown);
        await Assert.That(plan.Legs.Count).IsEqualTo(2);
        await Assert.That(plan.Leg(QuestLegId.Advance)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.TurnIn)).IsNotNull();
    }

    /// <summary>
    /// An objective act this vocabulary cannot serve yet keeps the bootstrap
    /// floor — the work the step machine can always do, byte-identical to what
    /// every non-251 quest got before the director was generalized — but the gap
    /// is NAMED (<c>HARNESS/UNPROVEN-PATTERN</c>) rather than silent: the
    /// behavior logs it and the give-up rule reads it. The act type is named, so
    /// the refusal says WHICH act could not be served.
    /// </summary>
    [Test]
    public async Task Plan_UnservedObjectiveAct_KeepsBootstrapFloor_NamesTheGap()
    {
        var fixture = new QuestFixtureRow(Quest251, 0, 0, 0, 0, 0, 0, 0)
        {
            ObjectiveActType = "QuestActObjMonsterHunt"
        };

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved)
            .IsEqualTo("HARNESS/UNPROVEN-PATTERN quest=251 act=QuestActObjMonsterHunt");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.Unknown);
        await Assert.That(plan.Legs.Count).IsEqualTo(2);
        await Assert.That(plan.Leg(QuestLegId.Advance)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.TurnIn)).IsNotNull();
    }

    [Test]
    public async Task Plan_GatherActWithoutResolvableSource_FailsUnprovenSource()
    {
        // A recognized gather act whose item has no loot-chain row: the prey
        // template stays 0 because the source never resolved.
        var fixture = new QuestFixtureRow(Quest251, 10473, Meat4058, 3, 0, 0, 3512, 18791);

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason).IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=251 act=10473 item=4058");
        await AssertNoLegs(plan);
    }

    [Test]
    public async Task Plan_ItemUseActWithoutItem_FailsUnprovenSource()
    {
        // An item-use objective whose act resolved no item template: there is
        // nothing to consume, so the plan fails closed naming the act.
        var fixture = new QuestFixtureRow(Quest252, 0, 0, 0, 0, 0, 7653, 0)
        {
            ObjectivePattern = QuestPattern.UseItem,
            ObjectiveActType = "QuestActObjItemUse",
            UseItemActId = 1600
        };

        var plan = QuestDirector.Plan(Quest252, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason).IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=252 act=1600 item=0");
        await AssertNoLegs(plan);
    }

    // -------------------------------------------------- runtime fail-closed

    /// <summary>
    /// The acceptance at the wake path, on the real headless actor and the
    /// production registry: a 251 quest whose objective source does not resolve
    /// (the loot chain carries no row for item 4058) fails the plan gate as
    /// UNPROVEN-SOURCE and dispatches NOTHING — no selected action, no request,
    /// no audit record, quest still Progress. Re-seeding the loot link in the
    /// same test makes the SAME staged quest dispatch, so the gate (not the
    /// stage) is what suppressed it.
    /// </summary>
    [Test]
    public async Task Run_SourceUnresolved_PlanFailsClosed_NoDispatch()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("stage3-fail-closed");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        actor.Character.Quests.ActiveQuests[Quest251] = new Quest(actor.Character)
        {
            TemplateId = Quest251,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };

        // Stage the unresolvable source: the row still carries its gather act
        // (10473) but the loot chain no longer links 4058 to a prey.
        ClearBoarLootLink();
        var fixture = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(fixture.GatherActId).IsEqualTo(10473u);
        await Assert.That(fixture.PreyTemplate).IsEqualTo(0u);

        var plan = QuestDirector.Plan(Quest251, fixture);
        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason).IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=251 act=10473 item=4058");

        var blocked = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "stage3-blocked-1" });

        await Assert.That(blocked.WorkSelected).IsFalse();
        await Assert.That(blocked.SelectedAction).IsNull();
        await Assert.That(blocked.Request).IsNull();
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Move
            or ActorActionType.Stop or ActorActionType.Target or ActorActionType.AutoAttack
            or ActorActionType.Loot or ActorActionType.InteractNpc)).IsFalse();
        await Assert.That(actor.Character.Quests.ActiveQuests[Quest251].Status)
            .IsEqualTo(QuestStatus.Progress);

        // Same stage, source resolved: the quest leg now dispatches, proving
        // the gate was the suppressor.
        SeedBoarLootLink();
        await Assert.That(QuestDirector.Plan(Quest251, QuestFixtureRow.FromQuestData(Quest251)).HasFailed).IsFalse();
        var live = QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "stage3-blocked-2" });
        await Assert.That(live.SelectedAction).IsNotNull();
    }

    // ---------------------------------------------------------- row memo

    /// <summary>
    /// The wake path derives the 251 row on EVERY wake, so the derivation must
    /// be memoized: two calls return the SAME row, and that row still carries
    /// the real canonical facts (a cache serving a zero row would satisfy a
    /// reference check while breaking the plan it feeds).
    /// </summary>
    [Test]
    public async Task FromQuestData_SameQuestTwice_ReturnsTheSameMemoizedRow()
    {
        var first = QuestFixtureRow.FromQuestData(Quest251);
        var second = QuestFixtureRow.FromQuestData(Quest251);

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(second.PreyTemplate).IsEqualTo(Boar3475);
        await Assert.That(second.PreyPack).IsEqualTo(Pack4530);
        await Assert.That(second.GatherActId).IsEqualTo(10473u);
        await Assert.That(second.Need).IsEqualTo(3);
    }

    /// <summary>
    /// The memo is cleared ONLY by explicit invalidation, and invalidation
    /// must make the next call re-read the live tables: a staged surface change
    /// is invisible while the row is memoized (that is the point of the cache),
    /// and becomes visible the moment the seam invalidates. Serves the
    /// "never a stale row across a data reload" contract — the reload seams call
    /// exactly this invalidation.
    /// </summary>
    [Test]
    public async Task InvalidateAll_AfterStagedDataChange_DerivesTheNewRow()
    {
        var memoized = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(memoized.PreyTemplate).IsEqualTo(Boar3475);

        // Stage the surface change WITHOUT invalidating: the memo still serves
        // the row derived before the change.
        RemoveBoarLootLinkSilently();
        await Assert.That(QuestFixtureRow.FromQuestData(Quest251)).IsSameReferenceAs(memoized);

        // The explicit invalidation seam drops it; the next call sees the
        // staged data (the 251 source no longer resolves).
        QuestFixtureRow.InvalidateAll();
        var refreshed = QuestFixtureRow.FromQuestData(Quest251);
        await Assert.That(refreshed).IsNotSameReferenceAs(memoized);
        await Assert.That(refreshed.PreyTemplate).IsEqualTo(0u);
        await Assert.That(refreshed.PreyPack).IsEqualTo(0u);
        await Assert.That(refreshed.GatherActId).IsEqualTo(10473u);

        // Leave the canonical link (and its memo) in place for the next test.
        SeedBoarLootLink();
        QuestFixtureRow.InvalidateAll();
    }

    // ------------------------------------------------------ catalog contract

    [Test]
    public async Task PatternCatalog_KillXCoversTheTenGreenVerbs_UnknownHasNone()
    {
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.KillX)).IsEquivalentTo(new[]
        {
            "MoveTo", "Target", "Stop", "Observe", "AcceptQuest",
            "AutoAttack", "Loot", "TurnInQuest", "Talk", "InteractNpc"
        });
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.Unknown).Count).IsEqualTo(0);

        // The return leg's dialogue verb joined the set because G8b proved it —
        // so it must resolve green here like every other listed verb, never
        // fall back to the ungated-permitted list.
        var interactNpc = VerifiedVerbRegistry.Instance.Resolve("InteractNpc");
        await Assert.That(interactNpc.IsGreen).IsTrue();
        await Assert.That(interactNpc.GateId).IsEqualTo("G8b");
        await Assert.That(interactNpc.EvidencePath).IsEqualTo("g8b-return-report.json");

        // The three ungated-by-declaration verbs stay off the gate-checked set.
        foreach (var ungated in new[] { "AdvanceQuest", "TurnInDoodad", "AutoTurnIn" })
            await Assert.That(PatternCatalog.VerbsFor(QuestPattern.KillX)).DoesNotContain(ungated);
    }

    /// <summary>
    /// The item-use shape's own verb set: the two verbs its legs dispatch, both
    /// registry-green. The verb keys are distinct from the kill-to-gather set,
    /// so a plan cannot inherit a proof it does not need — and the director's
    /// gate has no ungated-by-declaration exceptions to hide behind.
    /// </summary>
    [Test]
    public async Task PatternCatalog_UseItemCoversItsTwoGreenVerbs()
    {
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.UseItem)).IsEquivalentTo(new[]
        {
            "UseItem", "TurnInQuest"
        });

        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.UseItem))
        {
            var gate = VerifiedVerbRegistry.Instance.Resolve(verbKey);
            await Assert.That(gate.IsGreen).IsTrue();
            await Assert.That(gate.GateId.Length).IsGreaterThan(0);
            await Assert.That(gate.EvidencePath.Length).IsGreaterThan(0);
        }

        var useItem = VerifiedVerbRegistry.Instance.Resolve("UseItem");
        await Assert.That(useItem.GateId).IsEqualTo("COMBAT-01");
        await Assert.That(useItem.EvidencePath).IsEqualTo("recovery-heal-report.json");

        // A pattern with no proven set stays EMPTY (fail-closed by
        // classification, never by a guessed verb list).
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.Unknown).Count).IsEqualTo(0);
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.Craft).Count).IsEqualTo(0);
    }

    // ------------------------------- hunt + talk objective classification

    /// <summary>
    /// Quest 60's Progress component carries exactly one objective act —
    /// <c>QuestActObjMonsterHunt</c> (act 807, npc template 611, count 1) — so
    /// it is a pure hunt shape straight off the canonical quest data, with no
    /// gather act and therefore no loot chain to invert. Before this shape was
    /// classified the row resolved <see cref="QuestPattern.Unknown"/> and the
    /// quest sat on the bootstrap floor; now it derives the kill-to-gather
    /// shape and builds the real objective leg set.
    /// </summary>
    [Test]
    public async Task Plan_HuntObjective_MonsterHunt_DerivesKillShapeWithRealLegs()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest60);

        // The classification and the identity facts are read off the act itself.
        await Assert.That(fixture.ObjectiveActType).IsEqualTo("QuestActObjMonsterHunt");
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.KillX);
        await Assert.That(fixture.HuntActId).IsEqualTo(HuntAct807);
        await Assert.That(fixture.HuntNpcTemplate).IsEqualTo(HuntPrey611);
        await Assert.That(fixture.HuntNeed).IsEqualTo(1);
        // A kill objective carries no gather act at all — the row's gather facts
        // stay zero, which is exactly why the loot-chain source gate must not
        // apply to this shape.
        await Assert.That(fixture.GatherActId).IsEqualTo(0u);
        await Assert.That(fixture.PreyItem).IsEqualTo(0u);

        var plan = QuestDirector.Plan(Quest60, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.KillX);
        // Real legs, not the bootstrap floor.
        await Assert.That(plan.Legs.Count).IsEqualTo(7);
        await Assert.That(plan.Leg(QuestLegId.Combat)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.TurnIn)).IsNotNull();
    }

    /// <summary>
    /// The group hunt reads its target from the act's OWN group id, never a
    /// membership dump and never a hand-typed template. Quest 10's objective is
    /// <c>QuestActObjMonsterGroupHunt</c> (act 13979, group 84, count 3).
    /// </summary>
    [Test]
    public async Task Plan_HuntObjective_MonsterGroupHunt_CarriesItsOwnGroup()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest10);

        await Assert.That(fixture.ObjectiveActType).IsEqualTo("QuestActObjMonsterGroupHunt");
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.KillX);
        await Assert.That(fixture.HuntActId).IsEqualTo(GroupHuntAct13979);
        await Assert.That(fixture.HuntNpcGroup).IsEqualTo(GroupHuntGroup84);
        await Assert.That(fixture.HuntNpcTemplate).IsEqualTo(0u);

        var plan = QuestDirector.Plan(Quest10, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.KillX);
    }

    /// <summary>
    /// Quest 532's Progress component's FIRST objective act (by component id,
    /// act id) is <c>QuestActObjTalk</c> (act 3230, npc template 2426) — it
    /// also carries a second talk act (3584) and a check-complete act, so the
    /// ordering rule is exercised by real data, not a hand-built row. The shape
    /// derives the talk chain and builds real legs where the quest used to sit
    /// on the bootstrap floor.
    /// </summary>
    [Test]
    public async Task Plan_TalkObjective_Talk_DerivesTalkChainWithRealLegs()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest532);

        await Assert.That(fixture.ObjectiveActType).IsEqualTo("QuestActObjTalk");
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.TalkChain);
        // The FIRST objective act wins — the same (component id, act id) order
        // the gather/use/interact shapes use.
        await Assert.That(fixture.TalkActId).IsEqualTo(TalkAct3230);
        await Assert.That(fixture.TalkNpcTemplate).IsEqualTo(TalkNpc2426);
        await Assert.That(fixture.TalkNpcGroup).IsEqualTo(0u);

        var plan = QuestDirector.Plan(Quest532, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.TalkChain);
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.Talk, QuestLegId.TurnIn
        });
        await Assert.That(plan.Leg(QuestLegId.Talk)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.TurnIn)).IsNotNull();
    }

    /// <summary>
    /// The talk chain's verb set is a STRICT SUBSET of keys the production
    /// registry already proves — every one of them resolves green with a real
    /// gate id and a real evidence artifact, so serving the talk family added
    /// no ungated verb. <c>Talk</c> itself is the FROZEN G2 row, the same verb
    /// the return leg's dialogue fallback rides.
    /// </summary>
    [Test]
    public async Task PatternCatalog_TalkChainIsGreenOnEveryVerbItLists()
    {
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.TalkChain)).IsEquivalentTo(new[]
        {
            "MoveTo", "Stop", "Observe", "AcceptQuest", "Talk", "TurnInQuest"
        });

        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.TalkChain))
        {
            var gate = VerifiedVerbRegistry.Instance.Resolve(verbKey);
            await Assert.That(gate.IsGreen).IsTrue();
            await Assert.That(gate.GateId.Length).IsGreaterThan(0);
            await Assert.That(gate.EvidencePath.Length).IsGreaterThan(0);
        }

        var talk = VerifiedVerbRegistry.Instance.Resolve("Talk");
        await Assert.That(talk.GateId).IsEqualTo("G2");
        await Assert.That(talk.EvidencePath).IsEqualTo("g2-talk-capability-report.json");

        // No catalog key anywhere may name a verb the registry does not carry —
        // an absent key would fail every plan that lists it, closed, at plan
        // time rather than at dispatch.
        foreach (var pattern in Enum.GetValues<QuestPattern>())
        foreach (var verbKey in PatternCatalog.VerbsFor(pattern))
            await Assert.That(VerifiedVerbRegistry.Instance.Resolve(verbKey).IsGreen).IsTrue();
    }

    /// <summary>
    /// A talk objective that names neither an npc nor a group has no
    /// conversation the engine could ever credit, so the plan fails closed by
    /// name with zero legs — the same fail-closed shape every other source
    /// gate uses.
    /// </summary>
    [Test]
    public async Task Plan_TalkObjectiveWithoutTarget_FailsUnprovenSource()
    {
        var fixture = new QuestFixtureRow(Quest532, 0, 0, 0, 0, 0, 2425, 0)
        {
            ObjectivePattern = QuestPattern.TalkChain,
            ObjectiveActType = "QuestActObjTalk",
            TalkActId = 3230
        };

        var plan = QuestDirector.Plan(Quest532, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=532 act=3230 npc=0 group=0");
        await AssertNoLegs(plan);
    }

    /// <summary>
    /// A hunt objective that names neither a template nor a group fails closed
    /// the same way. This is the guard that keeps a zone-scoped kill — which
    /// deliberately stays unserved — from being served as "attack the nearest
    /// arbitrary hostile".
    /// </summary>
    [Test]
    public async Task Plan_HuntObjectiveWithoutTarget_FailsUnprovenSource()
    {
        var fixture = new QuestFixtureRow(Quest60, 0, 0, 0, 0, 0, 6490, 0)
        {
            ObjectivePattern = QuestPattern.KillX,
            ObjectiveActType = "QuestActObjMonsterHunt",
            HuntActId = HuntAct807
        };

        var plan = QuestDirector.Plan(Quest60, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo("HARNESS/UNPROVEN-SOURCE quest=60 act=807 prey=0 group=0");
        await AssertNoLegs(plan);
    }

    /// <summary>
    /// THE FAIL-CLOSED PATH MUST NOT HAVE LOOSENED. An objective shape this
    /// change does not serve — <c>QuestActObjCraft</c> (104 real quests) and
    /// <c>QuestActObjZoneKill</c> (106) among them — must still keep the
    /// bootstrap floor, still name its act type, and still build no objective
    /// leg. A zone kill is the sharp case: it shares the kill engine fan-out,
    /// so mapping it was possible; it is left unmapped deliberately because it
    /// names no template and no group.
    /// </summary>
    [Test]
    [Arguments("QuestActObjCraft")]
    [Arguments("QuestActObjZoneKill")]
    [Arguments("QuestActObjZoneMonsterHunt")]
    [Arguments("QuestActObjSphere")]
    [Arguments("QuestActObjAggro")]
    [Arguments("QuestActObjDistance")]
    [Arguments("QuestActObjExpressFire")]
    [Arguments("QuestActObjCompleteQuest")]
    public async Task Plan_StillUnservedAct_KeepsBootstrapFloor_NamesTheGap(string actType)
    {
        var fixture = new QuestFixtureRow(Quest251, 0, 0, 0, 0, 0, 0, 0)
        {
            ObjectiveActType = actType
        };

        var plan = QuestDirector.Plan(Quest251, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved)
            .IsEqualTo($"HARNESS/UNPROVEN-PATTERN quest=251 act={actType}");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.Unknown);
        await Assert.That(plan.Legs.Count).IsEqualTo(2);
        await Assert.That(plan.Leg(QuestLegId.Advance)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.TurnIn)).IsNotNull();
        // No objective leg may exist on an unserved plan.
        await Assert.That(plan.Leg(QuestLegId.Talk)).IsNull();
        await Assert.That(plan.Leg(QuestLegId.Combat)).IsNull();
    }

    // ------------------------------------------------- dispatch routing

    /// <summary>
    /// THE SILENT NO-OP GUARD. A proposal whose goal has no route in
    /// <c>QuestBehavior.Dispatch</c> returns null and drops the whole wake
    /// without a single error — a green build, a correctly classified quest, and
    /// zero behavior. So this pins the ROUTE, not just the plan: a live talk
    /// quest with its npc in reach must actually reach the actor's Talk audit
    /// trail. Quest 532's row and npc 2426 are the real canonical ones.
    ///
    /// Two wakes by design: the leg holds the stop-before-talk discipline every
    /// other travelling leg holds, so the first in-range wake settles with an
    /// audited Stop and the second — now that the hold is confirmed — talks.
    /// </summary>
    [Test]
    public async Task Run_TalkObjectiveInRange_ReachesTheActorTalkVerb()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("talk-routing");
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, TalkNpc2426);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Hp = 100;
        // Place the actor + npc into the region grid the way the ENGINE does
        // (AddVisibleObject → Region.AddObject), so the nearby-list query has
        // real data to read — the established mechanism
        // GameplayActorM53CoreSurfaceTests.Observe_SnapshotEqualsDirectWorldManagerQueries
        // uses. The actor must be joined too, or the query reads an empty region.
        var region = session.World.GetRegionByPos(actor.Character.Transform.World.Position);
        if (region == null)
            Assert.Fail("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        // Well inside the 25 m Talk engine gate, so the leg is in the
        // stop-then-talk branch from the very first wake.
        JoinNpcRegion(session, npcObjId,
            actor.Character.Transform.World.Position + new System.Numerics.Vector3(3f, 0f, 0f));
        actor.Character.Quests.ActiveQuests[Quest532] = new Quest(actor.Character)
        {
            TemplateId = Quest532,
            Status = QuestStatus.Progress,
            Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };

        QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talk-routing-1" });
        QuestDecisionScenario.Run(actor, (_, _) => [],
            new QuestDecisionScenario.QuestOptions { CycleId = "talk-routing-2" });

        // The wake reached the Talk verb. Whatever else it did, a Talk proposal
        // with no executor route could not leave this record — that is the exact
        // failure this pins.
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Talk)).IsTrue();
    }

    /// <summary>
    /// THE FAIL-CLOSED PROOF for the talk leg, and the one case the headless rig
    /// supports natively. With NOTHING perceived the leg must withdraw by name
    /// (<c>no-target</c>) and emit no proposal at all — it must never reach for a
    /// conversation the engine cannot credit, and never dispatch an unguarded
    /// verb. This is the mirror of the routing test: one proves it acts when it
    /// can see its target, this proves it refuses when it cannot.
    /// </summary>
    [Test]
    public async Task TalkEmit_NoPerceivedNpc_WithdrawsByName_EmitsNoProposal()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("talk-no-target");
        actor.Character.Quests.ActiveQuests[Quest532] = new Quest(actor.Character)
        {
    TemplateId = Quest532,
      Status = QuestStatus.Progress,
    Step = QuestComponentKind.Progress,
            Objectives = [0, 0, 0, 0, 0],
        };

        var fixture = QuestFixtureRow.FromQuestData(Quest532);
        // The wake's OWN perception, which sees no npc at all in this stage.
     var observation = BotObservedContext.Capture(actor);
        await Assert.That(observation.NearbyNpcObjIds).IsEmpty();

        var legCtx = new QuestLegContext(actor,
            new QuestDecisionScenario.QuestOptions { CycleId = "talk-no-target" },
 Quest532, fixture, null)
        {
     Observation = observation
        };

  var diag = QuestLegWake.NoProposal;
        var hpBefore = -1;
 var proposal = QuestBehavior.TalkEmit(legCtx, ref diag, ref hpBefore);

      await Assert.That(proposal).IsNull();
        await Assert.That(diag).Contains("dispatch=withdrawn");
        await Assert.That(diag).Contains("reason=no-target");
  }

    /// <summary>
    /// Joins a spawned npc to its world region so the wake's OWN perception
    /// snapshot can see it — the same registration the perception-stack rigs
    /// use. Without it the region graph reports no nearby npc and the talk leg
    /// correctly (but unobservably) withdraws for want of a target.
    /// </summary>
    private static void JoinNpcRegion(HeadlessSession session, uint npcObjId, System.Numerics.Vector3 position)
    {
        var npc = session.World.GetNpc(npcObjId);
        if (npc == null)
            return;
        npc.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region == null)
            return;
        region.AddObject(npc);
        npc.Region = region;
    }

    // ------------------------------------------------- 252 second proof

    /// <summary>
    /// The second shape, derived with NO director change: quest 252's row comes
    /// off the canonical quest data (an item-use objective consuming 7738 ×1,
    /// auto-complete on the Reward step), classifies as
    /// <see cref="QuestPattern.UseItem"/> from its own act, and builds a real
    /// leg set whose verbs the registry proves. Nothing in the director names
    /// 252 — the same derivation that serves 251 serves this.
    /// </summary>
    [Test]
    public async Task Plan252_ItemUseObjective_DerivesUseItemPlan()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest252);

        await Assert.That(fixture.UseItemActId).IsEqualTo(1600u);
        await Assert.That(fixture.UseItemTemplateId).IsEqualTo(7738u);
        await Assert.That(fixture.UseItemNeed).IsEqualTo(1);
        await Assert.That(fixture.AutoComplete).IsTrue();
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.UseItem);
        await Assert.That(fixture.ObjectiveActType).IsEqualTo("QuestActObjItemUse");
        await Assert.That(fixture.GatherActId).IsEqualTo(0u);

        var plan = QuestDirector.Plan(Quest252, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.UseItem);
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.UseItem, QuestLegId.TurnIn
        });
        await Assert.That(plan.Leg(QuestLegId.UseItem)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.Combat)).IsNull();
    }

    /// <summary>
    /// The wake path derives a plan for EVERY active quest, not just the wired
    /// one: two active quests of two shapes yield two plans in quest-id order,
    /// each carrying its own shape's legs. This is the selection surface the
    /// brain ranks over — no quest id is special-cased anywhere in it.
    /// </summary>
    [Test]
    public async Task PlanWake_TwoActiveQuests_TwoShapeCorrectPlans()
    {
        var context = new BotObservedContext { ActiveQuestIds = [Quest252, Quest251] };

        var plans = QuestDirector.PlanWake(context);

        await Assert.That(plans.Select(p => p.QuestId)).IsEquivalentTo(new[] { Quest251, Quest252 });
        await Assert.That(plans[0].Pattern).IsEqualTo(QuestPattern.KillX);
        await Assert.That(plans[0].Legs.Count).IsEqualTo(7);
        await Assert.That(plans[1].Pattern).IsEqualTo(QuestPattern.UseItem);
        await Assert.That(plans[1].Legs.Count).IsEqualTo(3);
        await Assert.That(plans.All(p => !p.HasFailed)).IsTrue();
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// Removes the 3475→4530→4058 loot link in this process so the 251 row's
    /// source cannot resolve (the fail-closed staging for the source branch).
    /// Invalidates the row memo: the row is derived from exactly these two
    /// tables, so a staged change must never be shadowed by a memoized row.
    /// </summary>
    private static void ClearBoarLootLink()
    {
        RemoveBoarLootLinkSilently();
        QuestFixtureRow.InvalidateAll();
    }

    /// <summary>
    /// The raw table edit behind <see cref="ClearBoarLootLink"/>, WITHOUT the
    /// memo invalidation — the memo test needs the silent half to show what the
    /// cache serves before an explicit invalidation.
    /// </summary>
    private static void RemoveBoarLootLinkSilently()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        if (typeof(ItemManager).GetField("_lootPackDroppingNpc", flags)!.GetValue(ItemManager.Instance)
            is Dictionary<uint, List<LootPackDroppingNpc>> map)
            map.Remove(Boar3475);
        if (typeof(LootGameData).GetField("_lootPacks", flags)!.GetValue(LootGameData.Instance)
            is Dictionary<uint, LootPack> packs)
            packs.Remove(Pack4530);
    }

    /// <summary>
    /// Additive, missing-only seed of the 3475→4530→4058 loot link the 251
    /// fixture row resolves its prey/pack through (same shape/guards as the
    /// sibling objective-path rigs; canonical pilot data is never clobbered).
    /// </summary>
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

        // Same seam rule as the sibling rigs: the 251 row memo is derived from
        // these two tables, so a (re)staged link must not be shadowed by a row
        // memoized from the prior staging.
        QuestFixtureRow.InvalidateAll();
    }
}
