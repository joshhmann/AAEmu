using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests.Director;

namespace AAEmu.Game.Core.Managers.Bots;

/// Quest director — the quest BRAIN's plan assembly and gate-check.
///
/// Assembles one <see cref="QuestPlan"/> per active quest, from the behavior's
/// leg <c>Emit</c> providers, over EVERY quest: the shape is decided by the
/// quest's derived <see cref="QuestFixtureRow"/>, never by a quest id. Four
/// shapes exist, and they are exhaustive by construction:
///
///   - a bootstrap quest (no Progress objective act) carries Advance + TurnIn —
///     the pre-G4 shape, unchanged;
///   - a gather-from-prey quest (an item-gather objective whose loot chain
///     resolves) additionally carries Return / Target / Pursuit / Combat /
///     Loot — the kill-to-gather path, unchanged for 251;
///   - an item-use quest (an item-use objective) additionally carries UseItem —
///     the consume-an-item path (252 is its proof);
///   - a gather-from-doodad quest (an item-gather objective whose npc loot
///     chain does NOT resolve but whose doodad func chain DOES) carries
///     Gather — the well-draw path (4415 is its first quest). The shape's
///     <c>Interact</c> verb has no registry row yet, so the plan fails as
///     <c>HARNESS/UNPROVEN-VERB</c> until the live gate proves the real path
///     and graduates the row.
/// The plan carries legs and data only. Every leg body, every per-actor memory
/// (hold/drift/pursuit/return issue, corpse, loot-once, give-up streak) and all
/// dispatch stay in <see cref="QuestBehavior"/>; the plan never stores per-wake
/// state.
///
/// The gate-check runs between the fixture row and the first leg, so it precedes
/// every wake and every perception read: a pattern whose verbs are not all
/// registry-green, an objective act the pattern vocabulary does not serve, or an
/// unresolvable objective source all produce a plan that carries a named
/// <c>HARNESS/UNPROVEN-*</c> failure and NO legs. The registry is the only
/// source of a verb key — the director never invents one.
///
/// <see cref="Run"/> is the wake's entry point: it perceives once, assembles the
/// wake's plan set through <see cref="PlanWake"/> and hands that set to
/// <see cref="QuestBehavior.Run"/>, which owns the leg loop and the decision
/// pipeline. The director owns the plan; the behavior owns running it.
/// </summary>
public static class QuestDirector
{
    /// <summary>
    /// The four leg sets, built once: a <see cref="QuestLeg"/> is an id plus its
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

    /// <summary>
    /// The item-use leg set: Advance, the item-use objective leg, then TurnIn.
    /// No Return leg — the shape's quest may complete on its own
    /// (<see cref="QuestFixtureRow.AutoComplete"/>), and a shape that still
    /// needs a reporter is turned in by the ordinary TurnIn leg against the
    /// row's <see cref="QuestFixtureRow.ReporterTemplate"/>. No funnel legs:
    /// the objective needs no world target (the item is in the bag and the
    /// wake's own perception carries the counts).
    /// </summary>
    private static readonly IReadOnlyList<QuestLeg> UseItemLegs = Array.AsReadOnly(new QuestLeg[]
    {
        new(QuestLegId.Advance, QuestBehavior.AdvanceEmit,
            Enter: QuestBehavior.AdvanceEnter),
        new(QuestLegId.UseItem, QuestBehavior.UseItemEmit,
            Enter: QuestBehavior.UseItemEnter),
        new(QuestLegId.TurnIn, QuestBehavior.TurnInEmit,
            Enter: QuestBehavior.TurnInEnter)
    });

    /// <summary>
    /// The gather-from-doodad leg set: Advance, the gather objective leg, then
    /// TurnIn. The ordinary TurnIn leg serves the Ready step against the row's
    /// <see cref="QuestFixtureRow.ReporterTemplate"/> (4415 reports to the
    /// same NPC that gave it); Advance serves the step machine as always. No
    /// funnel legs: the objective's target is a doodad resolved per wake from
    /// the row's own <see cref="QuestFixtureRow.GatherDoodadTemplate"/>, never
    /// the npc funnel.
    /// </summary>
    private static readonly IReadOnlyList<QuestLeg> GatherDoodadLegs = Array.AsReadOnly(new QuestLeg[]
    {
        new(QuestLegId.Advance, QuestBehavior.AdvanceEmit,
            Enter: QuestBehavior.AdvanceEnter),
        new(QuestLegId.Gather, QuestBehavior.GatherEmit,
            Enter: QuestBehavior.GatherEnter),
        new(QuestLegId.TurnIn, QuestBehavior.TurnInEmit,
            Enter: QuestBehavior.TurnInEnter)
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
    /// Every active quest gets a row derived from its own data — the quest id is
    /// never compared against a wired constant, so a quest the vocabulary can
    /// serve derives its plan the moment it is active. A plan that failed its
    /// gate is present and legless — the run loop names it at run-result level
    /// instead of silently skipping the quest.
    /// </summary>
    public static IReadOnlyList<QuestPlan> PlanWake(BotObservedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plans = new List<QuestPlan>(context.ActiveQuestIds.Count);
        foreach (var questId in context.ActiveQuestIds.Order())
        {
            // The fixture row is derived ONCE per quest per wake (memoized in
            // the row) and threaded into the plan; no leg re-derives it.
            plans.Add(Plan(questId, QuestFixtureRow.FromQuestData(questId)));
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

        // Classification is the row's own derived shape — no act vocabulary is
        // re-read here, and no quest id is special-cased. A row built by hand
        // (a test staging one fact) carries no derived pattern, so the shape
        // falls back to the row's OWN objective facts under the same rule the
        // derivation applies, so both paths agree.
        var pattern = fixture.ObjectivePattern != QuestPattern.Unknown
            ? fixture.ObjectivePattern
            : fixture.UseItemActId != 0
                ? QuestPattern.UseItem
                : fixture.GatherActId != 0 && fixture.GatherDoodadTemplate != 0
                    ? QuestPattern.GatherDoodad
                    : fixture.GatherActId != 0
                        ? QuestPattern.KillX
                        : QuestPattern.Unknown;

        // An objective the vocabulary cannot SERVE yet keeps the bootstrap floor
        // and names the gap. It is not a gate failure (the registry never
        // questioned it) and not silence (the gap must be visible): the behavior
        // logs it and the give-up rule reads it. A quest with no Progress
        // objective act at all is the pure bootstrap shape — same legs, no gap.
        if (pattern == QuestPattern.Unknown)
        {
            var unserved = fixture.ObjectiveActType.Length == 0
                ? ""
                : $"HARNESS/UNPROVEN-PATTERN quest={fixture.QuestId} act={fixture.ObjectiveActType}";
            return new QuestPlan(questId, pattern, fixture, BootstrapLegs, Unserved: unserved);
        }
        // A gather-shaped act whose loot chain did not resolve has no prey to
        // pursue: the source is the missing piece, named as such, and the plan
        // fails closed with zero legs rather than reaching the leg loop with no
        // target to find. (This is the shape's own precondition — the loot
        // chain is what makes the objective completable at all.)
        if (pattern == QuestPattern.KillX && fixture.PreyTemplate == 0)
            return Failed(questId, pattern, fixture,
                $"HARNESS/UNPROVEN-SOURCE quest={fixture.QuestId} act={fixture.GatherActId} item={fixture.PreyItem}");

        // A gather-from-doodad act whose doodad func chain did not resolve has
        // no well to draw from: same fail-closed shape as the prey path, naming
        // the same act and item so the gap reads identically in the lane.
        if (pattern == QuestPattern.GatherDoodad && fixture.GatherDoodadTemplate == 0)
            return Failed(questId, pattern, fixture,
                $"HARNESS/UNPROVEN-SOURCE quest={fixture.QuestId} act={fixture.GatherActId} item={fixture.PreyItem}");

        // An item-use objective names its item and its count; without either
        // there is nothing to consume, so the plan fails as an unresolvable
        // source exactly as the gather shape does.
        if (pattern == QuestPattern.UseItem && fixture.UseItemTemplateId == 0)
            return Failed(questId, pattern, fixture,
                $"HARNESS/UNPROVEN-SOURCE quest={fixture.QuestId} act={fixture.UseItemActId} item=0");

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

        return new QuestPlan(questId, pattern, fixture, pattern switch
        {
            QuestPattern.KillX => ObjectiveLegs,
            QuestPattern.UseItem => UseItemLegs,
            QuestPattern.GatherDoodad => GatherDoodadLegs,
            _ => NoLegs
        });
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
