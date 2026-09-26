#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots.Survival;

/// <summary>
/// The survival brain's LIVE ADAPTER: turns one wake's frozen observation plus the
/// combat layer's published facts into a <see cref="SurvivalBrainInputs"/>, runs
/// <see cref="SurvivalBrain.Decide"/>, and publishes the resulting fact through the
/// single publisher (<see cref="SurvivalVetoState"/>).
///
/// The split is deliberate: <see cref="SurvivalBrain"/> is pure and unit-testable
/// without a world, and every live read lives here — the actor's vitals, the
/// selected target, the combat engagement's commitment/retreat facts, and (only for
/// the flee leg) the one world resolve for the threat's position. No second
/// perception runs: the vitals and the selection come from the wake's OWN
/// <see cref="BotObservedContext"/>, exactly as the survival veto precondition
/// already reads them.
///
/// Two entry points, one rule:
///  - <see cref="SnapshotInputs"/> builds the FROZEN-fact inputs a veto decision
///    needs (vitals + fight evidence + the published retreat) and performs NO world
///    read — this is what a leg's veto precondition consumes, so evaluating it
///    cannot make the selector's walk non-deterministic;
///  - <see cref="Prepare"/> additionally resolves the threat's live position for a
///    caller that wants the FLEE leg's destination (the shared retreat anchor).
///
/// Fail-closed contract:
///  - an unreadable maximum (or a missing observation) yields a NaN ratio — never 0,
///    which would fabricate a veto;
///  - an unresolvable threat leaves a zero position, which the shared
///    <c>TravelBrain.SafeAnchor</c> already handles by its documented fallback — the
///    verb never receives a NaN destination;
///  - a decision that is never published leaves the actor's published fact
///    untouched, so a consumer reads only what the brain actually decided.
/// </summary>
public static class SurvivalBrainPlanner
{
    /// <summary>
    /// The wake's live measurement plus the decision it produced — the shape a
    /// caller reads to dispatch the flee leg, diagnose, and publish.
    /// </summary>
    public readonly record struct Prepared(
        SurvivalBrainInputs Inputs,
        SurvivalBrainDecision Decision)
    {
        /// <summary>True when this wake produced a leg the caller must dispatch.</summary>
        public bool HasLeg => Decision.HasVerb;

        /// <summary>True while a survival condition owns the wake (the published fact).</summary>
        public bool Vetoes => Decision.Veto;
    }

    /// <summary>
    /// THE FROZEN-FACT INPUT BUILDER: vitals, the selected target, the combat
    /// engagement's commitment, and its published retreat — nothing else, and NO
    /// world read. The threat POSITION is deliberately left at zero here because a
    /// veto decision never reads it (only the flee leg's destination does), and a
    /// caller evaluating a precondition must not scan the world to answer a
    /// yes/no question.
    ///
    /// The retreat flag is the combat layer's own published Disengaging fact — the
    /// same one <c>CombatBrainEngagement.IsSurvivalVetoed</c> exposes — read through
    /// the single <c>TryGet</c> that also supplies the commitment, so the two facts
    /// come from one consistent snapshot rather than two lookups that could straddle
    /// a publish.
    /// </summary>
    public static SurvivalBrainInputs SnapshotInputs(uint actorObjId, BotObservedContext? observation)
    {
        var retreatPublished = false;
        var committedTarget = 0u;
        if (CombatBrainEngagement.TryGet(actorObjId, out var engagement))
        {
            retreatPublished = engagement.Disengaging;
            committedTarget = engagement.IncumbentObjId;
        }

        return new SurvivalBrainInputs(
            ActorObjId: actorObjId,
            SelfHpRatio: Ratio(observation?.Hp ?? 0, observation?.MaxHp ?? 0),
            CombatRetreatPublished: retreatPublished,
            CommittedTargetObjId: committedTarget,
            SelectedTargetObjId: observation?.CurrentTargetObjId ?? 0u,
            SelfPosition: observation?.Position ?? Vector3.Zero,
            ThreatPosition: Vector3.Zero);
    }

    /// <summary>
    /// THE ELECTED-WAKE FROZEN SNAPSHOT: the same inputs <see cref="SnapshotInputs(uint, BotObservedContext?)"/>
    /// builds, from the raw vitals the caller already holds — no snapshot object and
    /// no world read, so the gate adds no heap of its own to the per-wake budget (the
    /// formula-backed <c>MaxHp</c> read does allocate, which is exactly why
    /// <see cref="CouldVeto"/> gates this call to the wakes where a veto arm can
    /// apply).
    ///
    /// An unreadable maximum (0 — a character whose formula-backed <c>MaxHp</c> could
    /// not be evaluated) reads as the named <see cref="SurvivalReason.VitalsUnreadable"/>
    /// hold, never a fabricated veto.
    ///
    /// <paramref name="hp"/> and <paramref name="maxHp"/> are read once by the caller
    /// and never stored: a veto decision never reads the actor's POSITION (only the
    /// flee leg's anchor does, and <see cref="Prepare(IGameplayActor, in SurvivalBrainInputs)"/>
    /// resolves it live), so a caller evaluating a precondition must not hand a stale
    /// or fabricated point to the decision.
    /// </summary>
    public static SurvivalBrainInputs SnapshotInputs(
        uint actorObjId, int hp, int maxHp, uint selectedTargetObjId)
    {
        var retreatPublished = false;
        var committedTarget = 0u;
        if (CombatBrainEngagement.TryGet(actorObjId, out var engagement))
        {
            retreatPublished = engagement.Disengaging;
            committedTarget = engagement.IncumbentObjId;
        }

        return new SurvivalBrainInputs(
            ActorObjId: actorObjId,
            SelfHpRatio: Ratio(hp, maxHp),
            CombatRetreatPublished: retreatPublished,
            CommittedTargetObjId: committedTarget,
            SelectedTargetObjId: selectedTargetObjId,
            SelfPosition: Vector3.Zero,
            ThreatPosition: Vector3.Zero);
    }

    /// <summary>
    /// THE CHEAP PRECONDITION: could a veto arm possibly apply to this wake? Reads
    /// only facts that are free per wake (the hp field, the selection, and the combat
    /// engagement's published facts) and deliberately NOT the formula-backed
    /// <c>Character.MaxHp</c>, whose read allocates. A false answer means the wake
    /// decides a non-vetoing verdict (healthy, a non-critical fight, or the recovery
    /// demand), so a caller can skip the whole evaluation — and a caller that owns a
    /// published fact from an earlier wake can clear it, because the cause is gone.
    ///
    /// Fail-closed toward evaluation: an unreadable hp (0) reads as "evaluate", which
    /// lands on the incapacitated hold or the named unreadable hold — never on a
    /// silently skipped wake.
    /// </summary>
    public static bool CouldVeto(uint actorObjId, int hp, uint selectedTargetObjId)
    {
        if (hp <= 0 || selectedTargetObjId != 0)
            return true;
        return CombatBrainEngagement.TryGet(actorObjId, out var engagement)
               && (engagement.Disengaging || engagement.IncumbentObjId != 0);
    }

    /// <summary>
    /// Evaluates the wake's decision from the actor's OWN frozen observation and the
    /// combat layer's published facts, resolving the threat's live position for the
    /// flee leg. Writes nothing: the caller publishes through <see cref="Publish"/>
    /// (or <see cref="SurvivalVetoState"/>), so a decision that loses selection
    /// cannot leave a fact behind.
    /// </summary>
    /// <param name="actor">The live actor.</param>
    /// <param name="observation">
    /// The wake's OWN frozen observation snapshot, or null when the caller has none
    /// — in which case the vitals read NaN and the decision holds with
    /// <see cref="SurvivalReason.VitalsUnreadable"/>, fabricating no veto.
    /// </param>
    public static Prepared Prepare(IGameplayActor actor, BotObservedContext? observation)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Prepare(actor, SnapshotInputs(actor.ActorId, observation));
    }

    /// <summary>
    /// The same wake evaluation from inputs the caller ALREADY froze (the
    /// allocation-free elected-wake path): the verdict is decided from exactly the
    /// facts the caller published, and only the flee leg's threat position is
    /// resolved live. No snapshot object is built, so this path stays out of the
    /// per-wake heap budget.
    /// </summary>
    public static Prepared Prepare(IGameplayActor actor, in SurvivalBrainInputs frozen)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var character = actor.Character;
        var inputs = frozen with
        {
            SelfPosition = character?.Transform.World.Position ?? Vector3.Zero,
            ThreatPosition = ResolvePosition(character, frozen.ThreatObjId)
        };

        return new Prepared(inputs, SurvivalBrain.Decide(inputs));
    }

    /// <summary>
    /// Banks a decided wake's fact through the SINGLE publisher, so a leg's veto
    /// precondition and the lane's recorded verdict are the same fact by
    /// construction. Called by the caller the moment it has the decision.
    /// </summary>
    public static bool Publish(uint actorObjId, in Prepared prepared)
    {
        SurvivalVetoState.Publish(actorObjId, prepared.Decision);
        return prepared.Decision.Veto;
    }

    /// <summary>
    /// THE CONSUMER SEAM: evaluate the one rule over a frozen snapshot, publish the
    /// fact, and report whether a survival condition owns the wake. This is exactly
    /// what a leg's veto precondition needs, and it is the single place the rule
    /// runs — no consumer can reach a second version of it.
    /// </summary>
    public static bool EvaluateAndPublish(uint actorObjId, BotObservedContext? observation)
        => SurvivalVetoState.Publish(actorObjId, SnapshotInputs(actorObjId, observation), out _);

    // ------------------------------------------------------------------ live reads

    /// <summary>
    /// The threat's live position, resolved by objId through the actor's OWN world
    /// (never a scan). An unresolvable threat leaves a ZERO position, which the
    /// shared retreat anchor already handles by its documented fail-closed path —
    /// this method never invents a position.
    /// </summary>
    private static Vector3 ResolvePosition(Character? character, uint objId)
    {
        if (character?.ParentWorld == null || objId == 0)
            return Vector3.Zero;
        return character.ParentWorld.GetNpc(objId)?.Transform.World.Position
               ?? character.ParentWorld.GetUnit(objId)?.Transform.World.Position
               ?? Vector3.Zero;
    }

    private static float Ratio(int value, int max)
        => max > 0 ? (float)value / max : float.NaN;
}
