using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Director;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Quest director — Stage 2 (leg assembly), Stage 3 (fixture gate-check) and
/// Stage 4 (plan-driven run wiring). Assembles one quest's
/// <see cref="QuestPlan"/> from the behavior's leg <c>Emit</c> providers,
/// reproducing the leg set the wake path used to hard-wire:
///
///   - a bootstrap quest (no fixture row) carries Advance + TurnIn — the
///     pre-G4 shape, unchanged for every quest except the wired objective one;
///   - a quest with a derived fixture row additionally carries
///     Return / Target / Pursuit / Combat / Loot — the wired objective path.
///
/// The plan carries legs and data only. Every leg body, every per-actor memory
/// (hold/drift/pursuit/return issue, corpse, loot-once) and all dispatch stay in
/// <see cref="QuestBehavior"/>; the plan never stores per-wake state.
///
/// The gate-check runs between the fixture row and the first leg, so it precedes
/// every wake and every perception read: a pattern whose verbs are not all
/// registry-green, an act the pattern vocabulary does not recognize, or an
/// unresolvable objective source all produce a plan that carries a named
/// <c>HARNESS/UNPROVEN-*</c> failure and NO legs. The registry is the only
/// source of a verb key — the director never invents one.
///
/// <see cref="Run"/> is the wake's entry point: it perceives once, assembles the
/// wake's plan set through <see cref="PlanWake"/> (the data-driven wiring this
/// stage introduces — the quest-scoped constant below is the ONLY quest wiring
/// left) and hands that set to <see cref="QuestBehavior.Run"/>, which owns the
/// leg loop and the decision pipeline. The director owns the plan; the behavior
/// owns running it.
/// </summary>
public static class QuestDirector
{
    /// <summary>
    /// The quest whose objective path (G4-G8b) is wired into the plan set. The
    /// path's VALUES are all data-derived (<see cref="QuestFixtureRow"/>); only
    /// the wiring is quest-scoped for now — the last piece Stage 4 leaves
    /// hard-wired. Resolved per wake through the fixture row, never carried as a
    /// template constant.
    /// </summary>
    private const uint ObjectiveQuestId = 251;

    /// <summary>
    /// The two leg sets, built once: a <see cref="QuestLeg"/> is an id plus its
    /// static-method delegates, so it carries no per-actor or per-wake state and
    /// is shared across every plan and every actor.
    ///
    /// Declaration order is evaluation order (see <see cref="QuestLeg"/>), so
    /// the sets read top-to-bottom exactly as the wake path drives them: the
    /// objective legs are declared in the run loop's emission order — Return
    /// before TurnIn (TurnIn's enter gate reads the return slice the same wake),
    /// then the funnel group behind the funnel marker, then Target last.
    /// </summary>
    private static readonly IReadOnlyList<QuestLeg> BootstrapLegs = Array.AsReadOnly(new QuestLeg[]
    {
        new(QuestLegId.Advance, QuestBehavior.AdvanceEmit,
            Enter: QuestBehavior.AdvanceEnter),
        new(QuestLegId.TurnIn, QuestBehavior.TurnInEmit,
            Enter: QuestBehavior.TurnInEnter)
    });

    private static readonly IReadOnlyList<QuestLeg> ObjectiveLegs = Array.AsReadOnly(new QuestLeg[]
    {
        new(QuestLegId.Advance, QuestBehavior.AdvanceEmit,
            Enter: QuestBehavior.AdvanceEnter),
        new(QuestLegId.Return, QuestBehavior.ReturnEmit, QuestBehavior.ReturnExit),
        new(QuestLegId.TurnIn, QuestBehavior.TurnInEmit,
            Enter: QuestBehavior.TurnInEnter),
        new(QuestLegId.Pursuit, QuestBehavior.PursuitEmit, NeedsFunnel: true),
        new(QuestLegId.Combat, QuestBehavior.CombatEmit, NeedsFunnel: true),
        new(QuestLegId.Loot, QuestBehavior.LootEmit, NeedsFunnel: true),
        new(QuestLegId.Target, QuestBehavior.TargetEmit, NeedsFunnel: true)
    });

    private static readonly IReadOnlyList<QuestLeg> NoLegs = Array.AsReadOnly(Array.Empty<QuestLeg>());

    /// <summary>
    /// Runs one quest wake: perceive once, assemble the wake's plan set, and
    /// drive it through the behavior's leg loop. The single entry point for the
    /// quest decision path (<see cref="QuestDecisionScenario.Run"/> and the
    /// runtime-hosted <see cref="BotBehaviorRuntime"/> both land here).
    /// </summary>
    public static QuestDecisionScenario.QuestRunResult Run(
        IGameplayActor actor,
        Func<Character, float, IEnumerable<Npc>>? nearbyNpcs,
        QuestDecisionScenario.QuestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var opts = options ?? new QuestDecisionScenario.QuestOptions();
        try
        {
            // One perception for the whole wake: the plan set is assembled from
            // this snapshot, and the leg loop reads the same one (its own
            // post-emit re-capture stays behavior-side, immediately before
            // selection). A throw here reports the same RUN/FidelityError the
            // wake path has always reported for a failed run.
            var context = BotObservedContext.Capture(actor);
            return QuestBehavior.Run(actor, opts, context, PlanWake(context), nearbyNpcs);
        }
        catch (Exception ex)
        {
            return QuestBehavior.Fail("RUN", ActorFailureReason.FidelityError,
                $"{ex.GetType().Name}: {ex.Message}", actor, null, []);
        }
    }

    /// <summary>
    /// The wake's plan set: one gate-checked plan per active quest, in
    /// ascending quest-id order (the decision order the wake has always used).
    /// A plan that failed its gate is present and legless — the run loop names
    /// it at run-result level instead of silently skipping the quest.
    /// </summary>
    public static IReadOnlyList<QuestPlan> PlanWake(BotObservedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plans = new List<QuestPlan>(context.ActiveQuestIds.Count);
        foreach (var questId in context.ActiveQuestIds.Order())
        {
            // The fixture row is derived ONCE per quest per wake and threaded
            // into the plan; no leg re-derives it.
            var fixture = questId == ObjectiveQuestId
                ? QuestFixtureRow.FromQuestData(questId)
                : null;
            plans.Add(Plan(questId, fixture));
        }
        return plans;
    }

    /// <summary>
    /// Builds the plan for one quest from its already-derived fixture row (null
    /// when the quest carries no objective path this wake — the row is resolved
    /// ONCE per wake by the caller and threaded here, never re-derived).
    /// <paramref name="registry"/> defaults to the production verb gate table;
    /// a test stages an unproven verb by passing its own implementation.
    /// </summary>
    public static QuestPlan Plan(
        uint questId,
        QuestFixtureRow? fixture,
        IVerifiedVerbRegistry? registry = null)
    {
        if (fixture == null)
            return new QuestPlan(questId, QuestPattern.Unknown, null, BootstrapLegs);

        // Classification stays the Stage 2 rule (act-type coverage grows it):
        // a row whose gather act resolved to a prey template through the loot
        // chain is a kill-to-gather quest.
        var pattern = fixture.PreyTemplate != 0 ? QuestPattern.KillX : QuestPattern.Unknown;

        // Gate-check, before a single leg exists. Each branch names what is
        // missing and fails closed with zero legs (no perception, no dispatch).
        if (pattern == QuestPattern.Unknown)
        {
            return Failed(questId, pattern, fixture,
                fixture.GatherActId == 0
                    // No recognized progress act: the pattern vocabulary does
                    // not cover this quest's objective shape yet.
                    ? $"HARNESS/UNPROVEN-PATTERN quest={fixture.QuestId} act=absent"
                    // A recognized act whose source did not resolve: the loot
                    // chain carries no row for the objective item.
                    : $"HARNESS/UNPROVEN-SOURCE quest={fixture.QuestId} act={fixture.GatherActId} item={fixture.PreyItem}");
        }

        // Every verb the pattern dispatches is checked here — except three that
        // are UNGATED BY DECLARATION, deliberately absent from the catalog's verb
        // set (see PatternCatalog for the full declaration and each graduation
        // requirement): AdvanceQuest (the step-machine's internal advance, not a
        // world verb the plan can misuse), TurnInDoodad (the distinct
        // doodad-reporter turn-in branch, which needs a doodad-reporter quest
        // probe to graduate), and AutoTurnIn (the no-target branch the scenario
        // rigs alone dispatch today, which needs a live auto-complete quest to
        // graduate). Any other verb a plan needs must resolve green here or the
        // plan fails as UNPROVEN-VERB.
        var gates = registry ?? VerifiedVerbRegistry.Instance;
        foreach (var verbKey in PatternCatalog.VerbsFor(pattern))
        {
            var gate = gates.Resolve(verbKey);
            if (!gate.IsGreen)
                return Failed(questId, pattern, fixture, UnprovenVerb(gate));
        }

        return new QuestPlan(questId, pattern, fixture, ObjectiveLegs);
    }

    /// <summary>
    /// The plan-time gate failure for a verb that is not registry-green: the
    /// missing/OPEN key, the gate that owns it (or <c>absent</c> when no gate
    /// does), and the evidence artifact — fixture evidence, never a claim.
    /// </summary>
    private static string UnprovenVerb(in VerbGate gate)
        => $"HARNESS/UNPROVEN-VERB verb={gate.VerbKey}" +
           $" gate={(gate.GateId.Length > 0 ? gate.GateId : "absent")}" +
           $" evidence={(gate.EvidencePath.Length > 0 ? gate.EvidencePath : "-")}";

    private static QuestPlan Failed(uint questId, QuestPattern pattern, QuestFixtureRow fixture, string reason)
        => new(questId, pattern, fixture, NoLegs, "FIXTURE", reason);
}
