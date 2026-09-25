namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// The pattern→verb-key catalog: which registry-verified actor verbs a quest
/// plan of a given <see cref="QuestPattern"/> dispatches, and therefore which
/// verb keys <c>QuestDirector.Plan</c> gate-checks before it builds a single
/// leg. The catalog is the only place the plan's verb vocabulary is declared —
/// the registry (not the catalog) decides whether a key is proven.
///
/// Only the verbs already registry-green for the one wired pattern are listed;
/// a pattern with no proven verb set maps to EMPTY, so the plan-time gate
/// checks nothing for it and classification (not this catalog) is what refuses
/// it. That keeps the fail-closed direction explicit: an unrecognized pattern
/// fails as UNPROVEN-PATTERN, never as a guessed verb list.
///
/// The legs of a <see cref="QuestPattern.KillX"/> plan dispatch the kill-to-gather
/// set plus three verbs that are UNGATED BY DECLARATION — not gate-checked here
/// because no gate owns them yet, and listing them would fail the one clean plan
/// the migration depends on. Each is named with the proof it must land before it
/// graduates onto the kill-to-gather list (and gets a row in
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
/// (dialogue Completed naming the live reporter 3512), so it is now part of the
/// kill-to-gather set and is gate-checked like the rest.
/// </summary>
public static class PatternCatalog
{
    /// <summary>
    /// The kill-to-gather verb set — the ten registry-green verbs quest 251
    /// needs: travel to the prey, take it as the target, stop in range,
    /// observe it, accept the quest, kill it, loot the corpse, hand the
    /// turn-in to the reporter, hold the dialogue step's talk credit (Talk),
    /// and hold the return leg's dialogue with the reporter (InteractNpc,
    /// frozen with G8b; 251 carries no talk-family objective, so Talk alone
    /// would void-reject on the return leg).
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

    private static readonly IReadOnlyList<string> NoVerbs = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>
    /// The verb keys a plan of <paramref name="pattern"/> must resolve green;
    /// empty when the pattern has no proven verb set (including
    /// <see cref="QuestPattern.Unknown"/>).
    /// </summary>
    public static IReadOnlyList<string> VerbsFor(QuestPattern pattern)
        => pattern == QuestPattern.KillX ? KillXVerbs : NoVerbs;
}
