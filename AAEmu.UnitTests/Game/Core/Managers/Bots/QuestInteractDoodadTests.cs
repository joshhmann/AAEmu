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
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Director;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.UnitTests.Game.Quests.Playerbot;
using QuestPattern = AAEmu.Game.Models.Game.Quests.Director.QuestPattern;
using QuestComponentKind = AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind;
using QuestStatus = AAEmu.Game.Models.Game.Quests.Static.QuestStatus;
using QuestAcceptorType = AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType;
namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Interact-with-doodad (4479 water the crops): the quest BRAIN's fourth
/// objective shape, on the REAL headless actor through
/// <see cref="QuestDecisionScenario.Run"/>.
///
///   - the 4479 fixture row derives InteractDoodad + the 5066 seedling + the
///     watering skill off canonical quest data plus a missing-only seedling
///     func seed (no hand-typed ids flow into the plan — the seed stages the
///     same canonical func tables the pilot's Load fills in production: group
///     13169's FakeUse row 2045 carries fake_skill_id 18320);
///   - the plan carries Advance/Interact/TurnIn and the catalog maps the shape
///     to its six-verb set, all registry-green (the SAME Interact key G9a
///     graduated for the gather shape — reuse, never a second verb proof);
///   - with the skill resolved the Interact leg drives Move (outside 25 m) →
///     Stop (inside, unsettled) → InteractWith (settled — the verb derives
///     skill 18320 from the seedling's own func tables) through the real
///     engine verbs, and the engine credits the objective through
///     InteractionEffect (no new actor verb — the skill-bound pipeline, like
///     the client cast).
/// </summary>
[NotInParallel]
public class QuestInteractDoodadTests
{
    private const uint Quest4479 = 4479;
    private const uint Seedling5066 = 5066;
    private const uint SeedlingStartGroup13169 = 13169;
    private const uint SeedlingFakeFunc11167 = 11167;
    private const uint SeedlingFakeTemplate2045 = 2045;
    private const uint WaterSeedling18320 = 18320;
    private const uint InteractAct26579 = 26579;
    private const int InteractNeed = 1;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
        // The TestRig's private SeedDoodadManager owns the singleton
        // construction; seed one loot interaction so the instance exists before
        // the seedling link stages its rows (same missing-only discipline).
        GameplayActorTestRig.SeedDoodadLootInteraction(99_101, 99_301, 91_002);
        TravelIntentStore.ClearAll();
        QuestBehavior.ClearGatherMemory();
        QuestBehavior.ClearInteractMemory();
        QuestBehavior.ClearGiveUpMemory();
        SeedSeedlingSkillLink();
    }

    // ------------------------------------------------------------ row + plan

    [Test]
    public async Task Row4479_DerivesInteractDoodadShape_OffCanonicalData()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4479);

        await Assert.That(fixture.QuestId).IsEqualTo(Quest4479);
        await Assert.That(fixture.InteractActId).IsEqualTo(InteractAct26579);
        await Assert.That(fixture.InteractDoodadTemplate).IsEqualTo(Seedling5066);
        await Assert.That(fixture.InteractNeed).IsEqualTo(InteractNeed);
        // The watering skill comes off the seedling's OWN FakeUse row — never hard-coded.
        await Assert.That(fixture.InteractUseSkill).IsEqualTo(WaterSeedling18320);
        await Assert.That(fixture.ObjectivePattern).IsEqualTo(QuestPattern.InteractDoodad);
        await Assert.That(fixture.ObjectiveActType).IsEqualTo(nameof(QuestActObjInteraction));
    }

    [Test]
    public async Task Plan4479_ProductionRegistry_InteractGreen_BuildsInteractPlan()
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4479);

        // The SAME Interact key the gather shape rides (G9a): the production
        // row is green, citing the skill-bound draw probe — the precondition
        // the passing plan depends on. No second verb proof.
        var gate = VerifiedVerbRegistry.Instance.Resolve("Interact");
        await Assert.That(gate.IsGreen).IsTrue();
        await Assert.That(gate.GateId).IsEqualTo("G9a");
        await Assert.That(gate.EvidencePath).IsEqualTo("g9a-skill-reprobe-report.20260927T220439Z.json");

        var plan = QuestDirector.Plan(Quest4479, fixture);

        await Assert.That(plan.HasFailed).IsFalse();
        await Assert.That(plan.Unserved).IsEqualTo("");
        await Assert.That(plan.Pattern).IsEqualTo(QuestPattern.InteractDoodad);
        await Assert.That(plan.Legs.Select(l => l.Id)).IsEquivalentTo(new[]
        {
            QuestLegId.Advance, QuestLegId.Interact, QuestLegId.TurnIn
        });
        await Assert.That(plan.Leg(QuestLegId.Interact)).IsNotNull();
        await Assert.That(plan.Leg(QuestLegId.Combat)).IsNull();
        await Assert.That(plan.Leg(QuestLegId.Gather)).IsNull();
        await Assert.That(plan.Leg(QuestLegId.UseItem)).IsNull();
    }

    [Test]
    public async Task PatternCatalog_InteractDoodad_MapsSixVerbs_AllRegistryGreen()
    {
        await Assert.That(PatternCatalog.VerbsFor(QuestPattern.InteractDoodad)).IsEquivalentTo(new[]
        {
            "MoveTo", "Stop", "Observe", "AcceptQuest", "Interact", "TurnInQuest"
        });

        // The SAME Interact key as the gather shape — all six resolve green.
        foreach (var verbKey in PatternCatalog.VerbsFor(QuestPattern.InteractDoodad))
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
    public async Task Plan_InteractDoodadWithoutSource_FailsUnprovenSource()
    {
        var fixture = new QuestFixtureRow(Quest4479, 0, 0, 0, 0, 0, 9789, 0)
        {
            ObjectivePattern = QuestPattern.InteractDoodad,
            ObjectiveActType = nameof(QuestActObjInteraction),
            InteractActId = InteractAct26579,
            InteractNeed = InteractNeed,
            InteractUseSkill = WaterSeedling18320
        };

        var plan = QuestDirector.Plan(Quest4479, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo($"HARNESS/UNPROVEN-SOURCE quest={Quest4479} act={InteractAct26579} doodad=0");
        await Assert.That(plan.Legs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Plan_InteractDoodadWithoutSkill_FailsUnprovenSource()
    {
        // The seedling skill binding is resolved by the sibling worker: until
        // its func-table row lands, the plan holds fail-closed — never a
        // skill-less Use that could not emit the quest interaction event.
        var fixture = new QuestFixtureRow(Quest4479, 0, 0, 0, 0, 0, 9789, 0)
        {
            ObjectivePattern = QuestPattern.InteractDoodad,
            ObjectiveActType = nameof(QuestActObjInteraction),
            InteractActId = InteractAct26579,
            InteractDoodadTemplate = Seedling5066,
            InteractNeed = InteractNeed,
            InteractUseSkill = 0
        };

        var plan = QuestDirector.Plan(Quest4479, fixture);

        await Assert.That(plan.HasFailed).IsTrue();
        await Assert.That(plan.FailStage).IsEqualTo("FIXTURE");
        await Assert.That(plan.FailReason)
            .IsEqualTo($"HARNESS/UNPROVEN-SOURCE quest={Quest4479} act={InteractAct26579} doodad={Seedling5066} skill=0");
        await Assert.That(plan.Legs.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PriorityLayout_InteractAboveGather_BelowTurnIn()
    {
        var opts = new QuestDecisionScenario.QuestOptions();
        await Assert.That(opts.ObjectiveInteractPriority).IsEqualTo(28);
        await Assert.That(opts.ObjectiveInteractPriority > opts.AdvancePriority).IsTrue();
        await Assert.That(opts.ObjectiveInteractPriority < opts.TurnInPriority).IsTrue();
        await Assert.That(opts.ObjectiveInteractPriority).IsNotEqualTo(opts.ObjectiveGatherPriority);
        await Assert.That(opts.ObjectiveInteractPriority).IsNotEqualTo(opts.ObjectiveUseItemPriority);
        await Assert.That(opts.ObjectiveInteractPriority).IsNotEqualTo(opts.ObjectivePursuitPriority);
        await Assert.That(opts.ObjectiveInteractPriority).IsNotEqualTo(opts.ObjectiveCombatPriority);
        await Assert.That(opts.ObjectiveInteractPriority).IsNotEqualTo(opts.ObjectiveLootPriority);
        await Assert.That(opts.ObjectiveInteractPriority).IsNotEqualTo(opts.ObjectiveReturnPriority);
    }

    // ------------------------------------------------------------ leg dispatch
    //
    // Interact reuses the production-graduated G9a key (same row the gather
    // shape rides), so these wakes run through QuestBehavior.Run with the
    // production plan — the same legs, the same selector, the same private
    // Dispatch — proving the leg body and the dispatch arms end to end.

    [Test]
    public async Task OutsideRange_MoveDispatched_OwnerTagged()
    {
        var (actor, session) = CreateWateringActor("interact-far", new Vector3(0, 0, 0), new Vector3(30, 0, 0));
        var seedling = session.World.GetAllDoodads().First(d => d.TemplateId == Seedling5066);
        var seedlingPos = seedling.Transform.World.Position;
        // The seedling stands outside the 25 m perception census but was perceived
        // on an earlier wake (stale snapshot): the leg still walks to its live
        // position — perception order, never a re-sort.
        var snapshot = BotObservedContext.Capture(actor) with { NearbyDoodadObjIds = new[] { seedling.ObjId } };

        var result = RunInteract(actor, "interact-far-1", snapshot);

        await Assert.That(result.WorkSelected).IsTrue();
        await Assert.That(result.SelectedAction).IsEqualTo(ActorActionType.Move);
        // A doodad is BaseUnit, never Unit: the leg rides the position leg, so
        // the request carries the live destination (not the objId) + the owner.
        await Assert.That(result.Request!.Destination.HasValue).IsTrue();
        await Assert.That(Vector3.Distance(result.Request.Destination!.Value, seedlingPos)).IsLessThan(0.5f);
        await Assert.That(result.Request.MoveOwner).IsEqualTo("INTERACT_MOVE_TO_UNIT");
        var detail = result.LegEvidence.Single(e => e.Leg == QuestLegId.Interact).Detail;
        await Assert.That(detail).Contains(":dispatch=move:reason=fresh");
        await Assert.That(detail).Contains($"target={seedling.ObjId}");
    }

    [Test]
    public async Task InsideRange_StopFirstThenInteractAfterSettle()
    {
        var (actor, session) = CreateWateringActor("interact-near", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var seedlingObjId = session.World.GetAllDoodads().First(d => d.TemplateId == Seedling5066).ObjId;

        // The fixture guarantees the wake's own census carries the seedling
        // BEFORE the first wake: a region-join miss fails here (not as a
        // leg-withdraw mystery downstream).
        var census = BotObservedContext.Capture(actor);
        if (!census.NearbyDoodadObjIds.Contains(seedlingObjId))
            throw new InvalidOperationException(
                $"seedling {seedlingObjId} not in wake census (nearby=[{string.Join(",", census.NearbyDoodadObjIds)}])");

        var first = RunInteract(actor, "interact-near-1");
        await Assert.That(first.WorkSelected).IsTrue();
        await Assert.That(first.SelectedAction).IsEqualTo(ActorActionType.Stop);
        await Assert.That(first.Request!.State).IsEqualTo(ActorLifecycleState.Completed);
        var stopDetail = first.LegEvidence.Single(e => e.Leg == QuestLegId.Interact).Detail;
        await Assert.That(stopDetail).Contains(":dispatch=stop:reason=in-range");

        var second = RunInteract(actor, "interact-near-2");
        await Assert.That(second.WorkSelected).IsTrue();
        await Assert.That(second.SelectedAction).IsEqualTo(ActorActionType.InteractWith);
        await Assert.That(second.Request!.TargetId).IsEqualTo(seedlingObjId);
        await Assert.That(second.Request.State).IsEqualTo(ActorLifecycleState.Completed);
        var interactDetail = second.LegEvidence.Single(e => e.Leg == QuestLegId.Interact).Detail;
        await Assert.That(interactDetail).Contains(":dispatch=interact-with:reason=settled");
        await Assert.That(actor.AuditTrace.Any(r => r.Action == ActorActionType.InteractWith)).IsTrue();
    }

    [Test]
    public async Task InteractWatersSeedling_AndCreditsObjective()
    {
        var (actor, _) = CreateWateringActor("interact-credit", new Vector3(0, 0, 0), new Vector3(10, 0, 0));

        // Wake 1 settles (Stop), wake 2 waters (InteractWith through the
        // skill-bound pipeline — the verb derives the row's watering skill
        // from the seedling's own func tables and the engine credits the
        // objective through InteractionEffect).
        RunInteract(actor, "interact-credit-1");
        var water = RunInteract(actor, "interact-credit-2");
        await Assert.That(water.SelectedAction).IsEqualTo(ActorActionType.InteractWith);

        // The engine credited the objective off the live quest object.
        var quest = actor.Character.Quests!.ActiveQuests[Quest4479];
        var interaction = QuestManager.Instance.GetTemplate(Quest4479)!
            .GetComponents(QuestComponentKind.Progress)
            .SelectMany(c => c.ActTemplates)
            .OfType<QuestActObjInteraction>()
            .First(a => a.ActId == InteractAct26579);
        await Assert.That(interaction.GetObjective(quest)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task NoSourcePerceived_InteractWithdrawsNamed_NoDispatch()
    {
        var (actor, _) = CreateWateringActor("interact-nosource", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        // Drain the world of seedlings: the leg has no source to walk to.
        foreach (var doodad in actor.Character.ParentWorld!.GetAllDoodads().ToList())
            actor.Character.ParentWorld.RemoveObject(doodad);

        var result = RunInteract(actor, "interact-nosource-1");

        await Assert.That(result.LegEvidence.Any(e => e.Leg == QuestLegId.Interact)).IsTrue();
        var evidence = result.LegEvidence.First(e => e.Leg == QuestLegId.Interact);
        await Assert.That(evidence.Entered).IsFalse();
        await Assert.That(evidence.Detail).IsEqualTo("interact-no-source");
        await Assert.That(actor.AuditTrace.Any(r => r.Action is ActorActionType.Move
            or ActorActionType.Stop or ActorActionType.InteractWith)).IsFalse();
    }

    [Test]
    public async Task RowDerivation_FollowsFuncTablePrecedence()
    {
        // The seedling's start group carries the FakeUse row first: the derived
        // watering skill is the fake_skill_id — never hard-coded.
        var fixture = QuestFixtureRow.FromQuestData(Quest4479);
        await Assert.That(fixture.InteractDoodadTemplate).IsEqualTo(Seedling5066);
        await Assert.That(fixture.InteractUseSkill).IsEqualTo(WaterSeedling18320);
        // A row with no doodad source carries no watering skill.
        var bare = new QuestFixtureRow(Quest4479, 0, 0, 0, 0, 0, 9789, 0)
        {
            ObjectivePattern = QuestPattern.InteractDoodad,
            ObjectiveActType = nameof(QuestActObjInteraction),
            InteractActId = InteractAct26579,
            InteractNeed = InteractNeed
        };
        await Assert.That(bare.InteractUseSkill).IsEqualTo(0u);
    }

    [Test]
    public async Task Interact_UnknownSkill_RejectsNamingSkill()
    {
        // No learned-skill requirement (template-existence only): an UNKNOWN
        // skill id still fails closed, naming the skill.
        var (actor, session) = CreateWateringActor("interact-badskill", new Vector3(0, 0, 0), new Vector3(10, 0, 0));
        var seedlingObjId = session.World.GetAllDoodads().First(d => d.TemplateId == Seedling5066).ObjId;
        const uint unknownSkill = 19_999_991;
        await Assert.That(AAEmu.Game.Core.Managers.SkillManager.Instance.GetSkillTemplate(unknownSkill)).IsNull();
        var request = actor.Interact(seedlingObjId, unknownSkill, "interact-badskill-1");
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Detail).Contains($"unknown interaction skill {unknownSkill}");
    }

    /// <summary>
    /// One interact wake through the behavior's own leg loop with the production
    /// plan (Interact graduated G9a): the same selector and the same private
    /// dispatch the production wake runs. An explicit snapshot simulates a
    /// stale perception (the doodad moved since the wake perceived); omitted,
    /// the wake perceives fresh.
    /// </summary>
    private static QuestDecisionScenario.QuestRunResult RunInteract(
        GameplayActor actor, string cycle, BotObservedContext? snapshot = null)
    {
        var fixture = QuestFixtureRow.FromQuestData(Quest4479);
        var plan = QuestDirector.Plan(Quest4479, fixture);
        if (plan.HasFailed)
            throw new InvalidOperationException($"production interact plan failed: {plan.FailReason}");
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
    /// Stages 4479 active/Progress through the real engine surfaces, spawns the
    /// live seedling 5066, and poses actor/seedling so the wake's perception carries the
    /// source. Returns the actor mid-watering.
    /// </summary>
    private static (GameplayActor Actor, HeadlessSession Session) CreateWateringActor(
        string name, Vector3 actorPos, Vector3 seedlingPos)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.Level = 10;
        actor.Character.Hp = actor.Character.MaxHp;
        GameplayActorTestRig.SetPosition(actor, actorPos);
        var region = session.World.GetRegionByPos(actorPos)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;
        // Start 19557 carries CompleteQuestContext(4415): the engine accept
        // gate refuses 4479 until the well-draw chain is complete.
        actor.Character.Quests!.SetCompletedQuestFlag(4415, true);
        var accept = actor.AcceptQuest(Quest4479, QuestAcceptorType.Npc, 9789);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.State} {accept.Detail}");
        // Accept lands pre-Progress: drain the step machine through the REAL
        // engine advances (None→Supply→Progress) so the wake starts on the
        // live Progress shape — the gather suite's staging discipline.
        var staged = actor.Character.Quests.ActiveQuests.GetValueOrDefault(Quest4479)
            ?? throw new InvalidOperationException("quest 4479 not active after accept");
        for (var step = 0; step < 5 && staged.Status != QuestStatus.Progress; step++)
            actor.AdvanceQuest(Quest4479);
        if (staged.Status != QuestStatus.Progress)
            throw new InvalidOperationException($"quest 4479 never reached Progress (status {staged.Status}, step {staged.Step})");
        staged.Objectives = [0, 0, 0, 0, 0];

        // The canonical watering skill requires OwnItem(15694) — the 4415
        // water vessel. Grant through the real acquisition path so the
        // headless skill pipeline passes its unit_reqs gate like the client's.
        GameplayActorTestRig.GrantItem(actor, 15694, 1);

        SpawnSeedlingDoodad(session, seedlingPos);
        return (actor, session);
    }

    /// <summary>
    /// Spawns the live seedling 5066 on its start phase: a raw world object
    /// joined to the region graph at <paramref name="position"/> so wake
    /// perception carries it. The template's func bindings come from
    /// <see cref="SeedSeedlingSkillLink"/> (missing-only, canonical).
    /// </summary>
    private static void SpawnSeedlingDoodad(HeadlessSession session, Vector3 position)
    {
        var doodadObjId = session.SpawnDoodad(Seedling5066);
        var doodad = session.World.GetDoodad(doodadObjId)!;
        doodad.FuncGroupId = SeedlingStartGroup13169;
        // Same one-shot shape as the gather rig's SpawnGatherDoodad: empty
        // FuncGroups keeps the seedling on its watering phase through DoFunc's
        // start-only rule (the full canonical template carries Normal-kind
        // groups that would consume the draw into a phase change).
        doodad.Template = new DoodadTemplate { Id = Seedling5066, FuncGroups = [] };
        var region = session.World.GetRegionByPos(position);
        doodad.Transform.Local.SetPosition(position);
        if (region != null)
        {
            region.AddObject(doodad);
            doodad.Region = region;
        }
    }

    /// <summary>
    /// Additive, missing-only seed of the 5066→13169(FakeUse 18320) chain the
    /// 4479 row resolves its seedling AND its watering skill through. Mirrors
    /// the canonical compact.sqlite3 rows: group 13169's FakeUse row (func key
    /// 11167 → template 2045) carries fake_skill_id 18320. Canonical pilot
    /// data is never clobbered: each row is added only when the lookup would
    /// otherwise resolve empty, and group bindings are added only when the
    /// template carries no such group. The 4479 row memo derives from exactly
    /// these tables, so the seed also seeds the 18320 skill template (the
    /// Interact template-existence gate) and invalidates the memo.
    /// </summary>
    private static void SeedSeedlingSkillLink()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        // ResolveDoodadUseSkill reads PeekInstance: the rig owns the
        // DoodadManager construction (seeded above). The watering skill needs
        // its InteractionEffect surface so the headless interact credits the
        // quest through the real skill pipeline.
        var manager = Singleton<DoodadManager>.PeekInstance;
        if (manager == null)
            return;

        var funcsByGroups = (Dictionary<uint, List<DoodadFunc>>)typeof(DoodadManager)
            .GetField("_funcsByGroups", flags)!.GetValue(manager)!;
        var funcsById = (Dictionary<uint, DoodadFunc>)typeof(DoodadManager)
            .GetField("_funcsById", flags)!.GetValue(manager)!;
        var funcTemplates = (Dictionary<string, Dictionary<uint, DoodadFuncTemplate>>)typeof(DoodadManager)
            .GetField("_funcTemplates", flags)!.GetValue(manager)!;

        if (!funcsById.ContainsKey(SeedlingFakeFunc11167))
            funcsById[SeedlingFakeFunc11167] = new DoodadFunc
            {
                GroupId = SeedlingStartGroup13169,
                FuncId = SeedlingFakeTemplate2045,
                FuncKey = SeedlingFakeFunc11167,
                FuncType = "DoodadFuncFakeUse",
                NextPhase = 13170,
                SkillId = 0
            };
        if (!funcsByGroups.TryGetValue(SeedlingStartGroup13169, out var startGroup))
        {
            startGroup = [];
            funcsByGroups[SeedlingStartGroup13169] = startGroup;
        }
        if (startGroup.All(f => f.FuncId != SeedlingFakeTemplate2045))
            startGroup.Add(funcsById[SeedlingFakeFunc11167]);

        if (!funcTemplates.TryGetValue("DoodadFuncFakeUse", out var fakeTemplates))
        {
            fakeTemplates = [];
            funcTemplates["DoodadFuncFakeUse"] = fakeTemplates;
        }
        if (!fakeTemplates.ContainsKey(SeedlingFakeTemplate2045))
        {
            fakeTemplates[SeedlingFakeTemplate2045] = new DoodadFuncFakeUse
            {
                FakeSkillId = WaterSeedling18320
            };
        }
        var templates = (Dictionary<uint, DoodadTemplate>)typeof(DoodadManager)
            .GetField("_templates", flags)!.GetValue(manager)!;

        if (!templates.TryGetValue(Seedling5066, out var template))
        {
            template = new DoodadTemplate { Id = Seedling5066, FuncGroups = [] };
            templates[Seedling5066] = template;
        }
        if (template.FuncGroups.All(g => g.Id != SeedlingStartGroup13169))
            template.FuncGroups.Add(new DoodadFuncGroups
            {
                Id = SeedlingStartGroup13169,
                Almighty = Seedling5066,
                GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
            });

        // The 4479 row memo is derived from exactly these tables: a staged link
        // must never be shadowed by a row memoized from the prior staging. The
        // watering skill needs a template row for the Interact existence gate
        // plus its real InteractionEffect (WI Use) so the headless draw credits
        // the quest through the skill pipeline — and the vessel item 15694 the
        // canonical OwnItem req names, granted through the real acquisition
        // path at fixture time (see CreateWateringActor).
        SeedWateringSkillTemplate();
        GameplayActorTestRig.SeedItemTemplate(15694);
        QuestFixtureRow.InvalidateAll();
    }

    /// <summary>
    /// Seeds the 18320 watering skill as a doodad-targeted cast carrying the
    /// real InteractionEffect — the same (TargetType Doodad, Use execution)
    /// shape the canonical skill carries, missing-only so the pilot's real
    /// load (when present) is never clobbered.
    /// </summary>
    private static void SeedWateringSkillTemplate()
    {
        var manager = SkillManager.Instance;
        var skills = (Dictionary<uint, SkillTemplate>)GameplayActorTestRig.GetField(manager, "_skills");
        if (!skills.TryGetValue(WaterSeedling18320, out var template))
        {
            template = new SkillTemplate
            {
                Id = WaterSeedling18320,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 25,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target
            };
            template.Effects.Add(new SkillEffect
            {
                EffectId = 1,
                Template = new InteractionEffect
                {
                    Id = 1,
                    WorldInteraction = AAEmu.Game.Models.Game.World.WorldInteractionType.Use,
                    DoodadId = 0
                },
                Friendly = true,
                NonFriendly = true,
                StartLevel = 0,
                EndLevel = byte.MaxValue,
                Chance = 100,
                ApplicationMethod = SkillEffectApplicationMethod.Target
            });
            skills[WaterSeedling18320] = template;
        }
    }
}
