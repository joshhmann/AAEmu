namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// The pattern→verb-key catalog: which registry-verified actor verbs a quest
/// plan of a given <see cref="QuestPattern"/> dispatches, and therefore which
/// verb keys <c>QuestDirector.Plan</c> gate-checks before it builds a single
/// leg. The catalog is the only place the plan's verb vocabulary is declared —
/// the registry (not the catalog) decides whether a key is proven.
///
/// One entry per pattern the director can classify from a derived
/// <see cref="QuestFixtureRow"/> (the row's <c>ObjectivePattern</c>): the
/// kill-to-gather shape, the item-use shape, the gather-from-doodad shape, and
/// the interact-with-doodad shape (both doodad shapes ride the SAME
/// <c>Interact</c> key the G9a skill-bound draw graduated into
/// <see cref="VerifiedVerbRegistry"/>).
/// A pattern with no proven verb set maps to EMPTY, so the plan-time gate
/// checks nothing for it and classification (not this catalog) is what refuses
/// it. That keeps the fail-closed direction explicit: an unrecognized pattern
/// fails as UNPROVEN-PATTERN, never as a guessed verb list.
///
/// VERBS UNGATED BY DECLARATION. A plan's legs dispatch three verbs that no
/// gate owns yet, so they are deliberately absent from every list below —
/// listing one would fail the plan that needs it. Each is named with the proof
/// it must land before it graduates onto a gate-checked list (and gets a row in
/// <see cref="VerifiedVerbRegistry"/>):
///
///   - <c>AdvanceQuest</c> — the step-machine's internal advance. It is the
///     engine's own per-step evaluation, not a world verb the plan can misuse;
///     it graduates only if a gate proves a plan-visible advance that the live
///     quest does not already perform on its own wake.
///   - <c>TurnInDoodad</c> — the distinct doodad-reporter turn-in branch,
///     reached only when the fixture resolves a report doodad. It graduates
///     when a gate probes a doodad-reporter quest end to end (reporter doodad
///     perceived, ranged, turned in) on the live engine.
///   - <c>AutoTurnIn</c> — the no-target turn-in branch, dispatched only by the
///     scenario rigs today; no quest in the wired path uses it. It graduates
///     when a gate drives a live auto-complete quest through the wake path to
///     completion.
///
/// The return leg's <c>InteractNpc</c> is NOT on that list: G8b proved it
/// (dialogue Completed naming the live reporter 3512), so it is part of the
/// kill-to-gather set and is gate-checked like the rest.
///
/// The item-use set's <c>UseItem</c> is a first-class row, not an ungated
/// declaration: the verb is proven (see <see cref="VerifiedVerbRegistry"/>), so
/// a plan that dispatches it is gate-checked exactly like a kill-to-gather
/// plan. Adding the second shape therefore needed no gate-check relaxation —
/// which is the point of keeping the catalog per-shape.
/// </summary>
public static class PatternCatalog
{
    /// <summary>
    /// The kill-to-gather verb set — the ten registry-green verbs the
    /// gather-from-prey plan needs: travel to the prey, take it as the target,
    /// stop in range, observe it, accept the quest, kill it, loot the corpse,
    /// hand the turn-in to the reporter, hold the dialogue step's talk credit
    /// (Talk), and hold the return leg's dialogue with the reporter
    /// (InteractNpc, frozen with G8b; the shape carries no talk-family
    /// objective here, so Talk alone would void-reject on the return leg).
    /// </summary>
    private static readonly IReadOnlyList<string> KillXVerbs = Array.AsReadOnly(new[]
    {
        "MoveTo",
        "Target",
        "Stop",
        "Observe",
        "AcceptQuest",
        "AutoAttack",
        "Loot",
        "TurnInQuest",
        "Talk",
        "InteractNpc"
    });

    /// <summary>
    /// The item-use verb set — the two registry-green verbs the
    /// consume-an-item plan needs: consume the objective item through the real
    /// <c>UseItem</c> contract, and (for a quest that does not auto-complete)
    /// hand the turn-in to its NPC reporter. The legs read the inventory from
    /// the wake's own perception snapshot, so no movement/observation verb is
    /// in the set; the objective needs no world target.
    /// </summary>
    private static readonly IReadOnlyList<string> UseItemVerbs = Array.AsReadOnly(new[]
    {
        "UseItem",
        "TurnInQuest"
    });

/// The gather-from-doodad verb set — the verbs the well-draw path needs:
/// travel to the doodad (<c>MoveTo</c>), stop in range (<c>Stop</c>),
/// observe it (<c>Observe</c>), accept the quest (<c>AcceptQuest</c>), draw
/// from it through the real <c>Interact</c> contract, and hand the turn-in
/// to the NPC reporter (<c>TurnInQuest</c>). <c>Interact</c> is a first-class
/// row (G9a skill-bound draw, same class of proof as the UseItem row), so a
/// plan that draws from a doodad is gate-checked exactly like a plan that
/// kills for one.
    private static readonly IReadOnlyList<string> GatherDoodadVerbs = Array.AsReadOnly(new[]
    {
        "MoveTo",
        "Stop",
        "Observe",
        "AcceptQuest",
        "Interact",
        "TurnInQuest"
    });

/// The interact-with-doodad verb set — the verbs the seedling-watering path
/// needs: travel to the doodad (<c>MoveTo</c>), stop in range (<c>Stop</c>),
/// observe it (<c>Observe</c>), accept the quest (<c>AcceptQuest</c>), work it
/// through the real skill-bound <c>Interact</c> contract, and hand the turn-in
/// to the NPC reporter (<c>TurnInQuest</c>). The SAME <c>Interact</c> verb key
/// the gather-from-doodad shape rides (registry-green since G9a), so the plan
/// gates exactly like the gather shape — but fail-closed on the skill: the
/// skill-less Use never emits the quest interaction event, so a leg with no
/// derived skill is refused at plan time (the director's own gate), not by
/// guessing a second verb.
    private static readonly IReadOnlyList<string> InteractDoodadVerbs = Array.AsReadOnly(new[]
    {
        "MoveTo",
        "Stop",
        "Observe",
        "AcceptQuest",
        "Interact",
        "TurnInQuest"
    });
    private static readonly IReadOnlyList<string> NoVerbs = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>
    /// The verb keys a plan of <paramref name="pattern"/> must resolve green;
    /// empty when the pattern has no proven verb set (including
    /// <see cref="QuestPattern.Unknown"/>).
    public static IReadOnlyList<string> VerbsFor(QuestPattern pattern)
        => pattern switch
        {
            QuestPattern.KillX => KillXVerbs,
            QuestPattern.UseItem => UseItemVerbs,
            QuestPattern.GatherDoodad => GatherDoodadVerbs,
            QuestPattern.InteractDoodad => InteractDoodadVerbs,
            _ => NoVerbs
        };
}
