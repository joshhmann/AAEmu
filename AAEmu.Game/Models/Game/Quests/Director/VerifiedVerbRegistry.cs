namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// The production verb gate table: one row per actor verb some gate proved
/// against the live engine, keyed by the plan's verb key. <see cref="Resolve"/>
/// is a pure dictionary read — a plan may call it for every verb of every
/// pattern on every wake without allocating or touching the world.
///
/// Only verbs with a real proof row appear here. A key that is absent resolves
/// OPEN (fail-closed), which is exactly what an invented verb must get: the
/// director never guesses a verb into existence.
///
/// The rows are the registry-green set the two wired patterns need between
/// them (the freeze dossiers under <c>scorecard-explorations/mechanics</c>
/// carry each gate's behavior proof; the evidence path is the gate's report
/// artifact):
///   MoveTo      FRESH  G1       g1-move-convergence-report.json
///   Target      FRESH  G5       g5-moving-start-report.json
///   Stop        FRESH  G5       g5-moving-start-report.json
///   Observe     FRESH  G5/G6    g5-moving-start-report.json;g6-first-combat-report.json
///   AcceptQuest FROZEN G3       g3-refill-gate-report.json
///   AutoAttack  FRESH  G6       g6-first-combat-report.json
///   Loot        FRESH  G7c      g7c-loot-report.json
///   TurnInQuest FRESH  G8c      g8c-turnin-report.json
///   Talk        FROZEN G2       g2-talk-capability-report.json
///   InteractNpc FRESH  G8b      g8b-return-report.json
///   UseItem     FRESH  COMBAT-01 recovery-heal-report.json
///
/// Two rows are worth their history: AcceptQuest keeps its FROZEN G3 status
/// (the freeze is the gate's doctrine) but its evidence path is repointed to
/// the newest PASS artifact of that gate — <c>g3-refill-gate-report.json</c>
/// (PASS-CANONICAL, production scheduler autonomously accepting in-range 251 at
/// the true giver 3512) — because <c>g3-quest-autonomy-3512-report.json</c> has
/// carried FAIL content since 09-19, and a row must never point a plan's
/// failure message at a failing artifact. InteractNpc is the new G8b row: the
/// return leg's dialogue Completed naming the live reporter 3512 is the
/// behavior proof, which is why the return verb is listed in the
/// <see cref="PatternCatalog"/> kill-to-gather set instead of sitting on the
/// catalog's ungated-permitted list.
///
/// UseItem is the row the item-use pattern added: its gate is the COMBAT-01
/// recovery leg, which drives the real <c>GameplayActor.UseItem</c> contract
/// (the SkillItem caster branch) to a Completed request through the live engine
/// and observes the effect land (the potion's heal), which is the same class of
/// behavior proof every other row carries — a real actor verb completing
/// against the live engine, not a claim. It joins the gate-checked set rather
/// than the catalog's ungated declarations, so a plan that consumes an item is
/// gated exactly like a plan that kills for one.
///
/// The table is deliberately NOT the whole IGameplayActor vocabulary
/// (Interact/Buy/Sell/Plant/Harvest/Mount and friends have no gate yet): those
/// keys stay absent, so any plan that needs one fails as UNPROVEN-VERB instead
/// of dispatching unproven behavior. The verbs a plan dispatches that are
/// ungated by declaration (AdvanceQuest, TurnInDoodad, AutoTurnIn) are named —
/// with the proof each must land to graduate here — on
/// <see cref="PatternCatalog"/> and at the plan-time loop in
/// <c>QuestDirector.Plan</c>, never left implicit.
/// </summary>
public sealed class VerifiedVerbRegistry : IVerifiedVerbRegistry
{
    /// <summary>The shared production registry (stateless — safe to share).</summary>
    public static readonly IVerifiedVerbRegistry Instance = new VerifiedVerbRegistry();

    private static readonly Dictionary<string, VerbGate> Gates = new(StringComparer.Ordinal)
    {
        ["MoveTo"] = new("MoveTo", VerbGateStatus.Fresh, "G1", "g1-move-convergence-report.json"),
        ["Target"] = new("Target", VerbGateStatus.Fresh, "G5", "g5-moving-start-report.json"),
        ["Stop"] = new("Stop", VerbGateStatus.Fresh, "G5", "g5-moving-start-report.json"),
        ["Observe"] = new("Observe", VerbGateStatus.Fresh, "G5/G6", "g5-moving-start-report.json;g6-first-combat-report.json"),
        ["AcceptQuest"] = new("AcceptQuest", VerbGateStatus.Frozen, "G3", "g3-refill-gate-report.json"),
        ["AutoAttack"] = new("AutoAttack", VerbGateStatus.Fresh, "G6", "g6-first-combat-report.json"),
        ["Loot"] = new("Loot", VerbGateStatus.Fresh, "G7c", "g7c-loot-report.json"),
        ["TurnInQuest"] = new("TurnInQuest", VerbGateStatus.Fresh, "G8c", "g8c-turnin-report.json"),
        ["Talk"] = new("Talk", VerbGateStatus.Frozen, "G2", "g2-talk-capability-report.json"),
        ["InteractNpc"] = new("InteractNpc", VerbGateStatus.Fresh, "G8b", "g8b-return-report.json"),
        ["UseItem"] = new("UseItem", VerbGateStatus.Fresh, "COMBAT-01", "recovery-heal-report.json")
    };

    /// <inheritdoc />
    public VerbGate Resolve(string verbKey)
        => verbKey != null && Gates.TryGetValue(verbKey, out var gate) ? gate : VerbGate.Absent(verbKey);
}
