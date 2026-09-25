#nullable enable

using Snapshot = AAEmu.Game.Core.Managers.Bots.BotPerceptionSnapshot.Snapshot;

namespace AAEmu.Game.Core.Managers.Bots.Belief;

/// <summary>
/// Layer 4's ATTENTION RANKER: given one perception frame pair, the ordered
/// objIds the observer should attend to, best first.
///
/// INVARIANT — PERSONALITY NEVER ALTERS THE SCORED SET. The score of a row is a
/// pure function of the frame (plus the documented bands below) and of nothing
/// else. No archetype, mood, need, curiosity, fatigue, personality profile or
/// randomness is consulted, and no such input exists in this signature. Two
/// observers of any temperament that perceive the same frame MUST receive the
/// same ranking; personality may later re-ORDER how a consumer spends its
/// attention, but it may never add, drop or re-weight a row here or the layer
/// stops being reproducible from recorded frames.
///
/// Discipline (binding):
///  - deterministic: a total order (score desc, then objId ASC) with no ties
///    left unresolved, so the same frame always yields the same list;
///  - pure: no engine query, no live re-resolution, no region scan, no
///    mutation. Every band is read from the perception layer's own captured
///    verdicts (<c>IsTargetable</c>, <c>Corpse.Lootable</c>, <c>Npc.QuestGiver</c>,
///    <c>Npc.Merchant</c>, <c>Npc.Hostile</c>/<c>Alive</c>, <c>IsInteractable</c>);
///  - no fabrication: a band whose evidence the census does not carry can never
///    be awarded (see the reserved bands below) — an unreadable role flag never
///    promotes a row, it only leaves it at the attention floor;
///  - the frame's transition evidence is authoritative: a corpse whose
///    container emptied (or that left the census) this frame is not ranked as
///    loot, even when the stale row still claims a lootable container — the
///    same clear-evidence rule <see cref="BeliefInterpreter"/> applies.
/// </summary>
public static class AttentionScorer
{
    // ------------------------------------------------------------------- bands
    // The ladder is applied in the order declared here: the FIRST band a row
    // satisfies is its score (mutually exclusive, so a row can never bank two
    // bands), which keeps scores comparable across rows and ties meaningful.

    /// <summary>A legal attack target this frame (see <see cref="IsAttackTarget"/>). The census's top-of-ladder.</summary>
    public const int TargetBand = 100;

    /// <summary>
    /// RESERVED — a hostile the census shows as acting on the observer.
    ///
    /// No band source exists today: a perceived row carries no attack state, and
    /// re-deriving "is it attacking me" would need a live engine query this
    /// layer must not run. The weight is declared so the ladder documents its
    /// intended precedence; the arm lands when attack-state evidence joins the
    /// frame, and until then nothing can be awarded it.
    /// </summary>
    public const int AttackingMeBand = 100;

    /// <summary>A lootable corpse not cleared by this frame's transitions.</summary>
    public const int OwnedCorpseBand = 90;

    /// <summary>An NPC the perception layer established as a quest giver.</summary>
    public const int QuestTargetBand = 80;

    /// <summary>An entity the perception layer established as interactable at the observer's range.</summary>
    public const int PlanRequiredInteractableBand = 70;

    /// <summary>An NPC the perception layer established as a vendor (template merchant gate plus a real shop pack).</summary>
    public const int GoalNeededMerchantBand = 60;

    /// <summary>
    /// RESERVED — a party member. The census carries no party membership (a
    /// nearby character is a nearby character, not a proven groupmate), so no
    /// row is awarded this band today. Declared to fix its precedence slot.
    /// </summary>
    public const int PartyBand = 50;

    /// <summary>
    /// An alive hostile NPC inside the census aggro band.
    ///
    /// On a frame the perception layer built, its own <c>IsTargetable</c> keeps
    /// a live hostile at <see cref="TargetBand"/>, so this rung is not reached.
    /// It exists for the INCONSISTENT frame (a hand-built or partially-joined
    /// census that carries the hostility evidence but not the targetable flag):
    /// the row still ranks above neutral instead of dropping to the floor, which
    /// is the ladder degrading on incomplete input rather than discarding a
    /// hostile the census did, in fact, observe.
    /// </summary>
    public const int HostileInAggroBand = 40;

    /// <summary>
    /// The attention floor: any other perceived row. This is a priority floor
    /// over rows the census DID observe — never a hostility verdict, so an
    /// unresolved role flag can leave a row here but can never promote it.
    /// </summary>
    public const int NeutralBand = 5;

    /// <summary>
    /// Ranks the frame's perceived entities, best first.
    /// </summary>
    /// <param name="snapshot">The frame to rank (its census is the whole candidate set).</param>
    /// <param name="diff">The frame's transition set — the clear-evidence authority (see <see cref="OwnedCorpseBand"/>).</param>
    /// <param name="topK">Maximum rows to return; 0 or negative returns an empty list.</param>
    /// <returns>
    /// Up to <paramref name="topK"/> objIds in rank order. Deterministic: equal
    /// scores break on the LOWER objId, so the total order is stable across
    /// frames, processes and runs.
    /// </returns>
    public static IReadOnlyList<uint> Rank(Snapshot snapshot, PerceptionFrameDiff diff, int topK)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(diff);

        var entities = snapshot.Entities;
        var limit = Math.Min(entities.Count, topK);
        if (limit <= 0)
            return [];

        var ranked = new List<uint>(limit);

        // Bounded selection, best-first: each pass takes the best row strictly
        // after the previous pick in the total order (score desc, objId asc).
        // O(rows x topK) score evaluations, no intermediate buffer and no
        // comparer closure: the caller's topK bounds the work, and every row is
        // scored from values the frame already carries.
        var hasCursor = false;
        var cursorScore = 0;
        var cursorObjId = 0u;

        for (var picked = 0; picked < limit; picked++)
        {
            var bestScore = 0;
            var bestObjId = 0u;
            var found = false;

            foreach (var entity in entities)
            {
                var score = ScoreOf(entity, diff);

                if (hasCursor && (score > cursorScore || (score == cursorScore && entity.ObjId <= cursorObjId)))
                    continue;

                if (!found || score > bestScore || (score == bestScore && entity.ObjId < bestObjId))
                {
                    bestScore = score;
                    bestObjId = entity.ObjId;
                    found = true;
                }
            }

            if (!found)
                break;

            ranked.Add(bestObjId);
            hasCursor = true;
            cursorScore = bestScore;
            cursorObjId = bestObjId;
        }

        return ranked;
    }

    /// <summary>
    /// The attention score of one perceived row: the first band it satisfies in
    /// the ladder's declared precedence, or <see cref="NeutralBand"/>.
    /// </summary>
    private static int ScoreOf(PerceivedEntity entity, PerceptionFrameDiff diff)
    {
        if (IsAttackTarget(entity))
            return TargetBand;

        // AttackingMeBand: reserved — no census evidence exists (see its docs).

        if (entity.Corpse is { Lootable: true } && !WasClearedThisFrame(diff, entity.ObjId))
            return OwnedCorpseBand;

        if (entity.Npc?.QuestGiver == true)
            return QuestTargetBand;

        if (entity.IsInteractable)
            return PlanRequiredInteractableBand;

        if (entity.Npc?.Merchant == true)
            return GoalNeededMerchantBand;

        // PartyBand: reserved — the census carries no party membership.

        if (entity.Npc is { Hostile: true, Alive: true } && entity.Distance <= BeliefInterpreter.EnemyBandM)
            return HostileInAggroBand;

        return NeutralBand;
    }

    /// <summary>
    /// Whether a row is a legal ATTACK target.
    ///
    /// The perception layer's own definition is kind-dependent
    /// (<c>BotPerceptionSnapshot.IsTargetable</c>): an NPC row must be alive AND
    /// hostile, a character row must be alive. The band re-asserts the row's own
    /// captured hostility evidence on top of the flag, so a frame that sets
    /// <c>IsTargetable</c> on a row whose hostility is absent or unresolved
    /// cannot promote a mere bystander to the top of the ladder — an NPC whose
    /// hostility is unknown is not an attack target this layer may claim.
    /// </summary>
    private static bool IsAttackTarget(PerceivedEntity entity) => entity.Kind switch
    {
        RadarEntityKind.Npc => entity.IsTargetable && entity.Npc?.Hostile == true,
        RadarEntityKind.Character => entity.IsTargetable,
        _ => false
    };

    /// <summary>
    /// True when the diff carries explicit CLEAR evidence for one objId: its
    /// container was emptied (became looted) or it left the census
    /// (disappeared). Mirrors the interpreter's rule so the ranker and the
    /// belief facts can never disagree about whether loot is still there.
    /// </summary>
    private static bool WasClearedThisFrame(PerceptionFrameDiff diff, uint objId)
    {
        var looted = diff.BecameLooted;
        for (var i = 0; i < looted.Count; i++)
        {
            if (looted[i].ObjId == objId)
                return true;
        }

        var disappeared = diff.Disappeared;
        for (var i = 0; i < disappeared.Count; i++)
        {
            if (disappeared[i].ObjId == objId)
                return true;
        }

        return false;
    }
}
