using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Travel;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestPattern = AAEmu.Game.Models.Game.Quests.Director.QuestPattern;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;
using QuestAcceptorType = AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Gather-from-doodad (4415 well-draw): the quest BRAIN's third objective shape,
/// on the REAL headless actor through <see cref="QuestDecisionScenario.Run"/>.
///
///   - the 4415 fixture row derives GatherDoodad + the granting well SET
///     (2306 lowest, plus the 3215–3222 almighty wells and the 3621/4543/5539/
///     7263/5061 sources — all canonical loot-func granters of the water) + the
///     draw skill off canonical quest data plus a missing-only doodad func seed
///     (no hand-typed ids flow into the plan — the seed only stages the same
///     func tables the pilot's Load fills in production: group 4581's FakeUse
///     row carries fake_skill_id 13154, group 4583's loot row grants the water,
///     plus the 3221/3220 almighty links for the nearest-well tests);
///   - the plan carries Advance/Gather/TurnIn and the catalog maps the shape to
///     its six-verb set, all registry-green since the G9a skill-bound draw
///     graduated Interact (a stub override still exercises the same legs —
///     belt-and-braces on the override seam, not a fallback for a refusal);
///   - with Interact graduated the Gather leg drives Move (outside 25 m) →
///     Stop (inside, unsettled) → Interact (settled, SkillId 13154) through the
///     real engine verbs, and the engine credits the objective (no new actor
///     verb — a skill-bound Doodad.Use draw, like the client cast).
///   - the leg resolves the NEAREST perceived granting well (any of the set —
///     the quest's 1784 alias accepts every well) with that template's own draw
///     skill, and the travel resolver walks the nearest spawner across the set:
///     at giver-A (21917,8003) the 3221 well 17.3 m away wins over 2306 3.9 km
///     away; at giver-B (12556,15369) the 2306 well 28 m away wins.
/// </summary>
[NotInParallel]
public class QuestGatherDoodadTests
{
    private const uint Quest4415 = 4415;
    private const uint Water15694 = 15694;
    private const uint Well2306 = 2306;
    private const uint Almighty3221 = 3221;
    private const uint Almighty3220 = 3220;
    private const uint WellStartGroup4581 = 4581;
    private const uint WellFakeFunc3853 = 3853;
    private const uint WellFakeTemplate511 = 511;
    private const uint DrawWater13154 = 13154;
    private const uint WellGroup4583 = 4583;
    private const uint WellFunc1256 = 1256;
    private const uint GatherAct26186 = 26186;
    private const int GatherNeed = 5;
    // 3221's canonical loot chain (compact.sqlite3): group 7349's loot row
    // (func key 6563 → template 1879) grants the water x1-5; group 7348's
    // FakeUse row (func key 6562 → template 1306) carries fake_skill_id 13154.
    private const uint Well3221StartGroup7348 = 7348;
    private const uint Well3221FakeFunc6562 = 6562;
    private const uint Well3221FakeTemplate1306 = 1306;
    private const uint Well3221Group7349 = 7349;
    private const uint Well3221Func6563 = 6563;
    private const uint Well3221Loot1879 = 1879;
    // 3220's canonical loot chain: group 7346's loot row (func key 6561 →
    // template 1878) grants the water; group 7345's FakeUse row (func key 6560
    // → template 1305) carries fake_skill_id 13154.
    private const uint Well3220StartGroup7345 = 7345;
    private const uint Well3220FakeFunc6560 = 6560;
    private const uint Well3220FakeTemplate1305 = 1305;
    private const uint Well3220Group7346 = 7346;
    private const uint Well3220Func6561 = 6561;
    private const uint Well3220Loot1878 = 1878;
    private const uint GiverA9789 = 9789;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        // The TestRig's private SeedDoodadManager owns the singleton
        // construction; seed one loot interaction so the instance exists before
        // the well link stages its rows (same missing-only discipline).
        GameplayActorTestRig.SeedDoodadLootInteraction(99_101, 99_301, 91_002);
        TravelIntentStore.ClearAll();
        QuestBehavior.ClearGatherMemory();
        QuestBehavior.ClearGiveUpMemory();
        SeedWellLootLink();
    }

    // ------------------------------------------------------------ row + plan

    [Test]
    public async Task Row4415_DerivesGatherDoodadShape_OffCanonicalData()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4415);

        await Assert.That(fixture.QuestId).IsEqualTo(Quest4415);
        await Assert.That(fixture.GatherActId).IsEqualTo(GatherAct26186);
        await Assert.That(fixture.PreyItem).IsEqualTo(Water15694);
        await Assert.That(fixture.Need).IsEqualTo(GatherNeed);
        await Assert.That(fixture.GatherDoodadTemplate).IsEqualTo(Well2306);
        // The SET carries every granting well (lowest id first): 2306 plus the
        // 3215–3222 almighty wells and the further canonical granters. The
        // draw skill comes off the well's OWN FakeUse row — never hard-coded.
        await Assert.That(fixture.GatherDoodadTemplates.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(fixture.GatherDoodadTemplates[0]).IsEqualTo(Well2306);
        await Assert.That(fixture.GatherDoodadTemplates.Contains(Almighty3221)).IsTrue();
        await Assert.That(fixture.GatherDoodadTemplates.Contains(Almighty3220)).IsTrue();
        await Assert.That(fixture.GatherUseSkill).IsEqualTo(DrawWater13154);
        await Assert.That(QuestFixtureRow.ResolveDoodadUseSkill(Almighty3221)).IsEqualTo(DrawWater13154);
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.GatherDoodad);
        await Assert.That(fixture.ObjectiveActType).IsEqualTo(nameof(QuestActObjItemGather));
        // The water comes from no npc loot pack — the doodad chain is the only source.
        await Assert.That(fixture.PreyTemplate).IsEqualTo(0u);
    }
    [Test]
    public async Task Plan4415_ProductionRegistry_InteractGreen_BuildsGatherPlan()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4415);

        // G9a graduated Interact: the production row is green, citing the
        // skill-bound draw probe — the precondition the passing plan depends on.
        var gate = VerifiedVerbRegistry.Instance.Resolve("Interact");
        await Assert.That(gate.IsGreen).IsTrue();
        await Assert.That(gate.GateId).IsEqualTo("G9a");
        await Assert.That(gate.EvidencePath).IsEqualTo("g9a-skill-reprobe-report.20260927T220439Z.json");

        var plan = QuestDirector.Plan(Quest4415, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.GatherDoodad);
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.Gather, QuestLegId.TurnIn
        });
        await Assert.That(plan.Leg(QuestLegId.Gather)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.Combat)).IsNull();
        await Assert.That(plan.Leg(QuestLegId.UseItem)).IsNull();
    }

    [Test]
    public async Task PatternCatalog_GatherDoodad_MapsSixVerbs_AllRegistryGreen()
    {
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.GatherDoodad)).IsEquivalentTo(new[]
        {
            "MoveTo", "Stop", "Observe", "AcceptQuest", "Interact", "TurnInQuest"
        });

        // All six resolve green since G9a graduated Interact — including the
        // graduated row's gate/evidence identity (same shape as the UseItem
        // row's gate assertion in the gate tests).
        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.GatherDoodad))
        {
            var gate = VerifiedVerbRegistry.Instance.Resolve(verbKey);
            await Assert.That(gate.IsGreen).IsTrue();
            await Assert.That(gate.GateId.Length).IsGreaterThan(0);
            await Assert.That(gate.EvidencePath.Length).IsGreaterThan(0);
        }
        var interact = VerifiedVerbRegistry.Instance.Resolve("Interact");
        await Assert.That(interact.GateId).IsEqualTo("G9a");
        await Assert.That(interact.EvidencePath).IsEqualTo("g9a-skill-reprobe-report.20260927T220439Z.json");
    }

    [Test]
    public async Task Plan_GatherDoodadWithoutSource_FailsUnprovenSource()
    {
        var fixture = new QuestFixtureRow(Quest4415, GatherAct26186, Water15694, GatherNeed, 0, 0, 9789, 0)
        {
            ObjectivePattern = QuestPattern.GatherDoodad,
            ObjectiveActType = nameof(QuestActObjItemGather),
        };

        var plan = QuestDirector.Plan(Quest4415, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo($"HARNESS/UNPROVEN-SOURCE quest={Quest4415} act={GatherAct26186} item={Water15694}");
        await Assert.That(plan.Legs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PriorityLayout_GatherAboveAdvance_BelowTurnIn()
    {
        var opts = new QuestDecisionScenario.QuestOptions();
        await Assert.That(opts.ObjectiveGatherPriority).IsEqualTo(27);
        await Assert.That(opts.ObjectiveGatherPriority > opts.AdvancePriority).IsTrue();
        await Assert.That(opts.ObjectiveGatherPriority < opts.TurnInPriority).IsTrue();
        await Assert.That(opts.ObjectiveGatherPriority).IsNotEqualTo(opts.ObjectiveUseItemPriority);
        await Assert.That(opts.ObjectiveGatherPriority).IsNotEqualTo(opts.ObjectivePursuitPriority);
        await Assert.That(opts.ObjectiveGatherPriority).IsNotEqualTo(opts.ObjectiveCombatPriority);
        await Assert.That(opts.ObjectiveGatherPriority).IsNotEqualTo(opts.ObjectiveLootPriority);
        await Assert.That(opts.ObjectiveGatherPriority).IsNotEqualTo(opts.ObjectiveReturnPriority);
    }

    // ------------------------------------------------------------ leg dispatch
    //
    // Interact is production-graduated (G9a above), so these wakes run through
    // QuestBehavior.Run with the production plan — the same legs, the same
    // selector, the same private Dispatch — proving the leg body and the
    // dispatch arms end to end. RunGather keeps a stub override layered on the
    // same green row (belt-and-braces on the override seam, not a fallback).

    [Test]
    public async Task OutsideRange_MoveDispatched_OwnerTagged()
    {
        var (actor, session) = CreateGatheringActor("gather-far", new Vector3(0, 0, 0), new Vector3(30, 0, 0));
        var well = session.World.GetAllDoodads().First(d => d.TemplateId == Well2306);
        var wellPos = well.Transform.World.Position;
        // The well stands outside the 25 m perception census but was perceived
        // on an earlier wake (stale snapshot): the leg still walks to its live
        // position — perception order, never a re-sort.
        var snapshot = BotObservedContext.Capture(actor) with { NearbyDoodadObjIds = new[] { well.ObjId } };

        var result = RunGather(actor, "gather-far-1", snapshot);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        // A doodad is BaseUnit, never Unit: the leg rides the position leg, so
        // the request carries the live destination (not the objId) + the owner.
        await Assert.That(result.Request!.Destination.HasValue).IsTrue();
        await Assert.That(Vector3.Distance(result.Request.Destination!.Value, wellPos)).IsLessThan(0.5f);
        await Assert.That(result.Request.MoveOwner).IsEqualTo("GATHER_MOVE_TO_UNIT");
        var detail = result.LegEvidence.Single(e => e.Leg == QuestLegId.Gather).Detail;
        await Assert.That(detail).Contains(":dispatch=move:reason=fresh");
        await Assert.That(detail).Contains($"target={well.ObjId}");
    }

    [Test]
    public async Task InsideRange_StopFirstThenInteractAfterSettle()
    {
        var (actor, session) = CreateGatheringActor("gather-near", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var wellObjId = session.World.GetAllDoodads().First(d => d.TemplateId == Well2306).ObjId;

        var first = RunGather(actor, "gather-near-1");
        await Assert.That(first.WorkSelected).IsTrue();
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(first.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
        var stopDetail = first.LegEvidence.Single(e => e.Leg == QuestLegId.Gather).Detail;
        await Assert.That(stopDetail).Contains(":dispatch=stop:reason=in-range");

        var second = RunGather(actor, "gather-near-2");
        await Assert.That(second.WorkSelected).IsTrue();
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Interact);
        await Assert.That(second.Request!.TargetId).IsEqualTo(wellObjId);
        await Assert.That(second.Request.SkillId).IsEqualTo(DrawWater13154);
        await Assert.That(second.Request.State).IsEqualTo(ActorLifecycleState.Completed);
        var interactDetail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Gather).Detail;
        await Assert.That(interactDetail).Contains(":dispatch=interact:reason=settled");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.Interact)).IsTrue();
    }

    [Test]
    public async Task NearestGrantingWell_WinsOverLowestId_PerTemplateSkill()
    {
        // Local-grid mirror of the giver-A shape (the headless world is 2x2
        // cells = 2048 m across, so canonical giver coords fall outside it): the
        // 3221 well ~17 m away (inside, settled on wake 2 → Interact) vs the
        // 2306 well ~24 m away (inside the census too, so the perceived count
        // stays 2) — the NEAREST rule (not the lowest id) picks 3221 with that
        // template's own draw skill (both carry 13154 off their own FakeUse rows).
        var actorPos = new Vector3(0, 0, 0);
        var nearPos = new Vector3(17.3f, 0, 0);
        var farPos = new Vector3(24f, 0, 0);
        var (actor, session) = CreateGatheringActor("gather-nearest-a", actorPos,
            Almighty3221, Well3221StartGroup7348, Well3221FakeFunc6562, nearPos);
        GameplayActorTestRig.SpawnGatherDoodad(
            session, Well2306, WellStartGroup4581, WellFakeFunc3853, Water15694, farPos);
        var nearObjId = session.World.GetAllDoodads().First(d => d.TemplateId == Almighty3221).ObjId;

        var fixture = QuestFixtureRow.FromQuestData(Quest4415);
        var intel = QuestBehavior.ReadGatherSourceIntel(
            new QuestLegContext(actor, new QuestDecisionScenario.QuestOptions(), Quest4415, fixture, null)
            {
                Observation = BotObservedContext.Capture(actor)
            }, fixture);
        // Requirements → nearby → distance, frozen per wake: two perceived
        // granters plus the surroundings census around them.
        await Assert.That(intel.PerceivedGranting.Count).IsEqualTo(2);
        await Assert.That(intel.PerceivedGranting[0].ObjId).IsEqualTo(nearObjId);
        await Assert.That(intel.PerceivedGranting[0].TemplateId).IsEqualTo(Almighty3221);
        await Assert.That(intel.NearbyDoodadCount).IsEqualTo(2);

        var first = RunGather(actor, "gather-nearest-a-1");
        await Assert.That(first.WorkSelected).IsTrue();
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        var second = RunGather(actor, "gather-nearest-a-2");
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.Interact);
        await Assert.That(second.Request!.TargetId).IsEqualTo(nearObjId);
        await Assert.That(second.Request.SkillId).IsEqualTo(DrawWater13154);
        var detail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Gather).Detail;
        await Assert.That(detail).Contains($"doodad={Almighty3221}");
    }

    [Test]
    public async Task NearestGrantingWell_GiverB_Picks2306()
    {
        // Local-grid mirror of the giver-B shape (same headless-grid reason as
        // above, preserving the original leg outcome): the 2306 well ~20 m away
        // (inside the gate → settled Stop here would need a second wake; the
        // far 3221 well co-perceives at ~24 m so the count stays honest) — the
        // same nearest rule picks 2306 here.
        var actorPos = new Vector3(0, 100, 0);
        var nearPos = new Vector3(20, 100, 0);
        var farPos = new Vector3(24, 100, 0);
        var (actor, session) = CreateGatheringActor("gather-nearest-b", actorPos,
            Well2306, WellStartGroup4581, WellFakeFunc3853, nearPos);
        GameplayActorTestRig.SpawnGatherDoodad(
            session, Almighty3221, Well3221StartGroup7348, Well3221FakeFunc6562, Water15694, farPos);
        var nearObjId = session.World.GetAllDoodads().First(d => d.TemplateId == Well2306).ObjId;
        var fixture = QuestFixtureRow.FromQuestData(Quest4415);
        var intel = QuestBehavior.ReadGatherSourceIntel(
            new QuestLegContext(actor, new QuestDecisionScenario.QuestOptions(), Quest4415, fixture, null)
            {
                Observation = BotObservedContext.Capture(actor)
            }, fixture);
        await Assert.That(intel.PerceivedGranting.Count).IsEqualTo(2);
        await Assert.That(intel.PerceivedGranting[0].ObjId).IsEqualTo(nearObjId);
        await Assert.That(intel.PerceivedGranting[0].TemplateId).IsEqualTo(Well2306);
        await Assert.That(intel.NearbyDoodadCount).IsEqualTo(2);

        var result = RunGather(actor, "gather-nearest-b-1");
        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Stop);
        var detail = result.LegEvidence.Single(e => e.Leg == QuestLegId.Gather).Detail;
        await Assert.That(detail).Contains($"doodad={Well2306}");
        await Assert.That(detail).Contains($"target={nearObjId}");
    }

    [Test]
    public async Task InteractDeliversWater_AndCreditsObjective_NoSecondDraw()
    {
        GameplayActorTestRig.SeedItemTemplate(Water15694);
        var (actor, _) = CreateGatheringActor("gather-credit", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        // Wake 1 settles (Stop), wake 2 draws (Interact with the row's draw skill).
        RunGather(actor, "gather-credit-1");
        var draw = RunGather(actor, "gather-credit-2");
        await Assert.That(draw.SelectedAction).IsEqualTo(ActorActionType.Interact);
        await Assert.That(draw.Request!.SkillId).IsEqualTo(DrawWater13154);

        // The engine credited the objective off the live quest object.
        var quest = actor.Character.Quests!.ActiveQuests[Quest4415];
        var gather = QuestManager.Instance.GetTemplate(Quest4415)!
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjItemGather>()
            .First(a => a.ActId == GatherAct26186);
        await Assert.That(gather.GetObjective(quest)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task NoSourcePerceived_GatherWithdrawsNamed_NoDispatch()
    {
        var (actor, _) = CreateGatheringActor("gather-nosource", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        // Drain the world of wells: the leg has no source to walk to.
        foreach (var doodad in actor.Character.ParentWorld!.GetAllDoodads().ToList())
            actor.Character.ParentWorld.RemoveObject(doodad);

        var result = RunGather(actor, "gather-nosource-1");

        await Assert.That(result.LegEvidence.Any(e => e.Leg == QuestLegId.Gather)).IsTrue();
        var evidence = result.LegEvidence.First(e => e.Leg == QuestLegId.Gather);
        await Assert.That(evidence.Entered).IsFalse();
        await Assert.That(evidence.Detail).IsEqualTo("gather-no-source");
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Move
            or ActorActionType.Stop or ActorActionType.Interact)).IsFalse();
    }

    [Test]
    public async Task RowDerivation_FollowsFuncTablePrecedence()
    {
        // The well's start group carries the FakeUse row first: the derived draw
        // skill is the fake_skill_id, not the loot row (which carries none).
        var fixture = QuestFixtureRow.FromQuestData(Quest4415);
        await Assert.That(fixture.GatherDoodadTemplate).IsEqualTo(Well2306);
        await Assert.That(fixture.GatherUseSkill).IsEqualTo(DrawWater13154);
        // A row with no doodad source carries no draw skill.
        var bare = new QuestFixtureRow(Quest4415, GatherAct26186, Water15694, GatherNeed, 0, 0, 9789, 0)
        {
            ObjectivePattern = QuestPattern.GatherDoodad,
            ObjectiveActType = nameof(QuestActObjItemGather),
            GatherDoodadTemplate = 0
        };
        await Assert.That(bare.GatherUseSkill).IsEqualTo(0u);
    }

    [Test]
    public async Task HandBuiltRow_WithoutUseSkill_DispatchesSkillLessInteract()
    {
        // Regression: a hand-built row (GatherUseSkill 0) keeps the old
        // skill-less Interact — the leg never invents a skill id.
        var (actor, session) = CreateGatheringActor("gather-skill0", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var wellObjId = session.World.GetAllDoodads().First(d => d.TemplateId == Well2306).ObjId;
        var bareRow = new QuestFixtureRow(Quest4415, GatherAct26186, Water15694, GatherNeed, 0, 0, 9789, 0)
        {
            ObjectivePattern = QuestPattern.GatherDoodad,
            ObjectiveActType = nameof(QuestActObjItemGather),
            GatherDoodadTemplate = Well2306
        };
        var plan = QuestDirector.Plan(Quest4415, bareRow);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production gather plan failed: {plan.FailReason}");
        QuestBehavior.Run(actor, new QuestDecisionScenario.QuestOptions { CycleId = "gather-skill0-1" },
            BotObservedContext.Capture(actor), [plan], (_, _) => []);
        var draw = QuestBehavior.Run(actor, new QuestDecisionScenario.QuestOptions { CycleId = "gather-skill0-2" },
            BotObservedContext.Capture(actor), [plan], (_, _) => []);
        await Assert.That(draw.SelectedAction).IsEqualTo(ActorActionType.Interact);
        await Assert.That(draw.Request!.TargetId).IsEqualTo(wellObjId);
        await Assert.That(draw.Request!.SkillId).IsEqualTo(0u);
    }

    [Test]
    public async Task Interact_UnknownSkill_RejectsNamingSkill()
    {
        // No learned-skill requirement (template-existence only): an UNKNOWN
        // skill id still fails closed, naming the skill.
        var (actor, session) = CreateGatheringActor("gather-badskill", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var wellObjId = session.World.GetAllDoodads().First(d => d.TemplateId == Well2306).ObjId;
        const uint unknownSkill = 19_999_991;
        await Assert.That(AAEmu.Game.Core.Managers.SkillManager.Instance.GetSkillTemplate(unknownSkill)).IsNull();
        var request = actor.Interact(wellObjId, unknownSkill, "gather-badskill-1");
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Detail).Contains($"unknown interaction skill {unknownSkill}");
    }

    /// <summary>
    /// One gather wake through the behavior's own leg loop with the production
    /// plan (Interact graduated G9a): the same selector and the same private
    /// dispatch the production wake runs. An explicit snapshot simulates a
    /// stale perception (the doodad moved since the wake perceived); omitted,
    /// the wake perceives fresh.
    /// </summary>
    private static QuestDecisionScenario.QuestRunResult RunGather(
        GameplayActor actor, string cycle, BotObservedContext? snapshot = null)
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4415);
        var plan = QuestDirector.Plan(Quest4415, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production gather plan failed: {plan.FailReason}");
        var context = snapshot ?? BotObservedContext.Capture(actor);
        return QuestBehavior.Run(
            actor,
            new QuestDecisionScenario.QuestOptions { CycleId = cycle },
            context,
            [plan],
            (_, _) => []);
    }

    // ------------------------------------------------------------ fixture

    /// <summary>
    /// Stages 4415 active/Progress with the live 2306 well (the pre-set-shape
    /// convenience over the template-parameterized helper below).
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateGatheringActor(
        string name, Vector3 actorPos, Vector3 wellPos)
        => CreateGatheringActor(name, actorPos, Well2306, WellStartGroup4581, WellFakeFunc3853, wellPos);

    /// <summary>
    /// Stages 4415 active/Progress through the real engine surfaces, spawns the
    /// live granting well of the given template, and poses actor/well so the
    /// wake's perception carries the source. Returns the actor mid-gather.
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateGatheringActor(
        string name, Vector3 actorPos, uint wellTemplate, uint startGroup, uint fakeFunc, Vector3 wellPos)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 10;
        actor.Character.Hp = actor.Character.MaxHp;
        GameplayActorTestRig.SeedItemTemplate(Water15694);

        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        // Accept through the real engine path (level-gated Start 19287 needs
        // Level >= 10), then drain the automatic Start/Supply steps so the quest
        // sits at Progress — the live shape the wake drives.
        var accept = actor.AcceptQuest(Quest4415, QuestAcceptorType.Npc, 9789);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");
        var staged = actor.Character.Quests.ActiveQuests.GetValueOrDefault(Quest4415)
            ?? throw new InvalidOperationException("quest 4415 not active after accept");
        if (staged.Status == QuestStatus.Progress)
            staged.Objectives = [0, 0, 0, 0, 0];

        GameplayActorTestRig.SpawnGatherDoodad(
            session, wellTemplate, startGroup, fakeFunc, Water15694, wellPos);
        var well = session.World.GetAllDoodads().First(d => d.TemplateId == wellTemplate);
        well.FuncGroupId = startGroup;
        if (well.ObjId == 0)
            throw new InvalidOperationException($"well {wellTemplate} not in world after spawn");
        return (actor, session);
    }

    /// <summary>
    /// Additive, missing-only seed of the 2306→4581(FakeUse 13154)→4583→1256→15694
    /// doodad chain the 4415 row resolves its well AND its draw skill through.
    /// Mirrors the canonical compact.sqlite3 rows: group 4581's FakeUse row
    /// (func key 3853 → template 511) carries fake_skill_id 13154, group 4583's
    /// loot row (func key 3854 → template 1256) grants the water. Canonical
    /// pilot data is never clobbered: each row is added only when the lookup
    /// would otherwise resolve empty, and group bindings are added only when
    /// the template carries no such group. The 4415 row memo derives from
    /// exactly these tables, so the seed also seeds the 13154 skill template
    /// (the Interact template-existence gate) and invalidates the memo.
    private static void SeedWellLootLink()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        // ResolveDoodadSource reads PeekInstance for both surfaces: the rig
        // owns the DoodadManager construction (seeded above), but nothing seeds
        // LootGameData here — touching Instance creates the empty surface so the
        // direct LootItem path (no pack lookup) resolves. Same discipline as the
        // sibling 251 loot-link rigs, which stage Instance tables directly.
        var _ = LootGameData.Instance;
        var manager = Singleton<DoodadManager>.PeekInstance;
        if (manager == null)
            return;

        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)typeof(DoodadManager)
            .GetField("_funcsByGroups", flags)!.GetValue(manager)!;
        var funcsById = (Dictionary<uint, DoodadFunc>)typeof(DoodadManager)
            .GetField("_funcsById", flags)!.GetValue(manager)!;
        var funcTemplates = (Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)typeof(DoodadManager)
            .GetField("_funcTemplates", flags)!.GetValue(manager)!;

        if (!funcsById.ContainsKey(WellFakeFunc3853))
            funcsById[WellFakeFunc3853] = new DoodadFunc
            {
                GroupId = WellStartGroup4581,
                FuncId = WellFakeTemplate511,
                FuncKey = WellFakeFunc3853,
                FuncType = "DoodadFuncFakeUse",
                NextPhase = 4583,
                SkillId = 0
            };
        if (!funcsByGroups.TryGetValue(WellStartGroup4581, out var startGroup))
        {
            startGroup = [];
            funcsByGroups[WellStartGroup4581] = startGroup;
        }
        if (startGroup.All(f => f.FuncId != WellFakeTemplate511))
            startGroup.Add(funcsById[WellFakeFunc3853]);

        if (!funcTemplates.TryGetValue("DoodadFuncFakeUse", out var fakeTemplates))
        {
            fakeTemplates = [];
            funcTemplates["DoodadFuncFakeUse"] = fakeTemplates;
        }
        if (!fakeTemplates.ContainsKey(WellFakeTemplate511))
        {
            fakeTemplates[WellFakeTemplate511] = new DoodadFuncFakeUse
            {
                FakeSkillId = DrawWater13154
            };
        }
        var templates = (Dictionary<uint, DoodadTemplate>)typeof(DoodadManager)
            .GetField("_templates", flags)!.GetValue(manager)!;
        SeedAlmightyWell(funcsByGroups, funcsById, funcTemplates, templates,
            Almighty3221, Well3221StartGroup7348, Well3221FakeFunc6562, Well3221FakeTemplate1306,
            Well3221Group7349, Well3221Func6563, Well3221Loot1879);
        SeedAlmightyWell(funcsByGroups, funcsById, funcTemplates, templates,
            Almighty3220, Well3220StartGroup7345, Well3220FakeFunc6560, Well3220FakeTemplate1305,
            Well3220Group7346, Well3220Func6561, Well3220Loot1878);
        // Group 4583's loot row grants the water (the FakeUse NextPhase target
        // — without this func row the 2306 chain has a template but no grant).
        if (!funcsById.ContainsKey(WellFunc1256))
            funcsById[WellFunc1256] = new DoodadFunc
            {
                GroupId = WellGroup4583,
                FuncId = WellFunc1256,
                FuncKey = WellFunc1256,
                FuncType = "DoodadFuncLootItem",
                NextPhase = -1,
                SkillId = 0
            };
        if (!funcsByGroups.TryGetValue(WellGroup4583, out var lootGroup))
        {
            lootGroup = [];
            funcsByGroups[WellGroup4583] = lootGroup;
        }
        if (lootGroup.All(f => f.FuncId != WellFunc1256))
            lootGroup.Add(funcsById[WellFunc1256]);

        if (!funcTemplates.TryGetValue("DoodadFuncLootItem", out var lootTemplates))
        {
            lootTemplates = [];
            funcTemplates["DoodadFuncLootItem"] = lootTemplates;
        }
        if (!lootTemplates.ContainsKey(WellFunc1256))
        {
            lootTemplates[WellFunc1256] = new DoodadFuncLootItem
            {
                ItemId = Water15694,
                CountMin = 1,
                CountMax = 2, // Random.Next(1, 2) == always exactly 1
                Percent = 10_000, // chance roll [0,10000) <= Percent → always
                RemainTime = 0
            };
        }
        SeedWell2306Template(templates);

        // The 4415 row memo is derived from exactly these tables: a staged link
        // must never be shadowed by a row memoized from the prior staging. The
        // draw skill needs a template row for the Interact existence gate.
        GameplayActorTestRig.SeedSkillTemplate(DrawWater13154);
        QuestFixtureRow.InvalidateAll();
    }
    /// <summary>
    /// Additive, missing-only seed of one almighty well's canonical chain
    /// (start FakeUse carrying 13154 + loot group granting the water), so the
    /// 4415 row's granting set includes it and its per-template draw skill
    /// resolves off its OWN func tables. Same discipline as the 2306 seed.
    /// </summary>
    private static void SeedAlmightyWell(
        Dictionary<uint, List<DoodadFunc>> funcsByGroups,
        Dictionary<uint, DoodadFunc> funcsById,
        Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> funcTemplates,
        Dictionary<uint, DoodadTemplate> templates,
        uint almighty, uint startGroup, uint fakeFunc, uint fakeTemplate,
        uint lootGroup, uint lootFunc, uint lootTemplate)
    {
        if (!funcsById.ContainsKey(fakeFunc))
            funcsById[fakeFunc] = new DoodadFunc
            {
                GroupId = startGroup,
                FuncId = fakeTemplate,
                FuncKey = fakeFunc,
                FuncType = "DoodadFuncFakeUse",
                NextPhase = (int)lootGroup,
                SkillId = 0
            };
        if (!funcsByGroups.TryGetValue(startGroup, out var start))
        {
            start = [];
            funcsByGroups[startGroup] = start;
        }
        if (start.All(f => f.FuncId != fakeTemplate))
            start.Add(funcsById[fakeFunc]);
        if (!funcTemplates.TryGetValue("DoodadFuncFakeUse", out var fakeTemplates))
        {
            fakeTemplates = [];
            funcTemplates["DoodadFuncFakeUse"] = fakeTemplates;
        }
        if (!fakeTemplates.ContainsKey(fakeTemplate))
            fakeTemplates[fakeTemplate] = new DoodadFuncFakeUse { FakeSkillId = DrawWater13154 };
        if (!funcsById.ContainsKey(lootFunc))
            funcsById[lootFunc] = new DoodadFunc
            {
                GroupId = lootGroup,
                FuncId = lootTemplate,
                FuncKey = lootFunc,
                FuncType = "DoodadFuncLootItem",
                NextPhase = -1,
                SkillId = 0
            };
        if (!funcsByGroups.TryGetValue(lootGroup, out var group))
        {
            group = [];
            funcsByGroups[lootGroup] = group;
        }
        if (group.All(f => f.FuncId != lootTemplate))
            group.Add(funcsById[lootFunc]);
        if (!funcTemplates.TryGetValue("DoodadFuncLootItem", out var almLootTemplates))
        {
            almLootTemplates = [];
            funcTemplates["DoodadFuncLootItem"] = almLootTemplates;
        }
        if (!almLootTemplates.ContainsKey(lootTemplate))
        {
            almLootTemplates[lootTemplate] = new DoodadFuncLootItem
            {
                ItemId = Water15694,
                CountMin = 1,
                CountMax = 5,
                Percent = 10_000,
                RemainTime = 100000
            };
        }
        if (!templates.TryGetValue(almighty, out var almTemplate))
        {
            almTemplate = new DoodadTemplate { Id = almighty, FuncGroups = [] };
            templates[almighty] = almTemplate;
        }
        if (almTemplate.FuncGroups.All(g => g.Id != startGroup))
            almTemplate.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = startGroup,
                Almighty = almighty,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });
        if (almTemplate.FuncGroups.All(g => g.Id != lootGroup))
            almTemplate.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = lootGroup,
                Almighty = almighty,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });
    }

    private static void SeedWell2306Template(Dictionary<uint, DoodadTemplate> templates)
    {
        if (!templates.TryGetValue(Well2306, out var template))
        {
            template = new DoodadTemplate { Id = Well2306, FuncGroups = [] };
            templates[Well2306] = template;
        }
        if (template.FuncGroups.All(g => g.Id != WellStartGroup4581))
            template.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = WellStartGroup4581,
                Almighty = Well2306,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });
        if (template.FuncGroups.All(g => g.Id != WellGroup4583))
            template.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = WellGroup4583,
                Almighty = Well2306,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });

        // The 4415 row memo is derived from exactly these tables: a staged link
        // must never be shadowed by a row memoized from the prior staging. The
        // draw skill needs a template row for the Interact existence gate.
        GameplayActorTestRig.SeedSkillTemplate(DrawWater13154);
        QuestFixtureRow.InvalidateAll();
    }
}
