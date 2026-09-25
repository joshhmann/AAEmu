#nullable enable

using Snapshot = AAEmu.Game.Core.Managers.Bots.BotPerceptionSnapshot.Snapshot;

namespace AAEmu.Game.Core.Managers.Bots.Belief;

/// <summary>
/// The observer's OWN state that the census cannot carry, as plain values.
///
/// The perception stack deliberately excludes the observer from the census
/// (the radar drops the observer's own objId), so the three facts that are
/// about the observer rather than about a perceived row — which objId it is,
/// what it has targeted, and how full its bag is — have to be handed in by the
/// driver that owns the character. <see cref="BeliefInputs"/> is that handoff.
///
/// Honesty contract:
///  - these are VALUES copied at the caller's read boundary, never engine
///    queries: the interpreter stays a pure function of its arguments;
///  - a field the caller cannot establish keeps its "unknown" value
///    (<see cref="None"/>: objId 0, ratio NaN, bag null) and every fact derived
///    from it stays UNKNOWN — never a fabricated "not targeted" / "healthy" /
///    "bag has room";
///  - <see cref="SelfHpRatio"/> is a ratio in 0..1, not an hp integer: the band
///    boundaries are ratios, so the caller never has to hand in max-hp.
/// </summary>
/// <param name="SelfObjId">The observer's own objId; 0 when the caller could not establish it.</param>
/// <param name="CurrentTargetObjId">The observer's current target objId; 0 when nothing is targeted.</param>
/// <param name="SelfHpRatio">Own hp as a fraction of max hp (0..1); NaN when unreadable.</param>
/// <param name="BagFreeSlots">Free bag slots; null when fullness cannot be established (no bag, or an unlimited container — see <c>BotWorldStateProvider.ReadBagFreeSlots</c>).</param>
public readonly record struct BeliefInputs(
    uint SelfObjId,
    uint CurrentTargetObjId,
    float SelfHpRatio,
    int? BagFreeSlots)
{
    /// <summary>
    /// The all-unknown handoff: no self identity, no target, no hp ratio, no
    /// bag capacity. Every fact that depends on the observer's own state stays
    /// UNKNOWN, so a driver that has no self data can still read the purely
    /// census-derived facts without ever receiving a fabricated self verdict.
    /// </summary>
    public static BeliefInputs None => new(0u, 0u, float.NaN, null);
}

/// <summary>
/// Layer 4 of the perception stack — the BELIEF INTERPRETER.
///
/// Turns ONE perception frame pair (the frame plus the transition diff derived
/// from its predecessor) plus the caller's named place into
/// <see cref="BotBeliefState"/>: the derived facts, numbers, refs and threat
/// level one actor holds at that frame.
///
/// Derivation discipline (binding):
///  - ONE pass over the frame's rows (plus short linear walks over the diff's
///    transition lists); no LINQ, no dictionary, no closure, no string
///    building, no allocation;
///  - NO live re-resolution and NO engine query of any kind: nothing here
///    looks at a world, a character, a manager or a region. The only engine
///    values read are COMPILE-TIME constants (<see cref="ContactThreatRangeM"/>,
///    <see cref="EnemyBandM"/>) — the same "use the engine's own number, never
///    a local guess" precedent as <c>BotWorldStateProvider.TargetEngagementRange</c>;
///  - NEVER <c>CanAttack</c> (or any other relation/faction query): hostility is
///    read from the perception layer's own captured verdict
///    (<c>PerceivedNpc.Hostile</c>), which is the one place it was resolved;
///  - UNKNOWN propagates: a fact that cannot be established stays in the
///    <see cref="BotBeliefState.Unknown"/> mask with its flag false, so
///    <see cref="BotBeliefState.IsSet"/> can never report a fabricated true.
///
/// Surface boundary: this layer CONSUMES <c>Snapshot</c>/<c>PerceptionFrameDiff</c>
/// and never exposes them — <see cref="BotBeliefState"/> is made of primitives
/// (a 64-bit fact word, an enum, floats, objIds and the caller's place key), so
/// a planner-facing signature can take the belief without taking the perception
/// types. GOAP never takes the perception types.
///
/// The frame's read time is carried through to
/// <see cref="BotBeliefState.ObservedAtUtc"/>, so every derived fact stays
/// dateable by whoever reads it.
/// </summary>
public static class BeliefInterpreter
{
    /// <summary>
    /// Band (metres) inside which a hostile NPC is counted as an enemy.
    ///
    /// This is the CENSUS band — the radar's own perceived radius — because the
    /// census is the only census this layer has: an engine aggro range is
    /// per-template (<c>NpcTemplate.SightRangeScale</c>, with a facing and
    /// line-of-sight gate) and is not carried by a perceived row, so a row
    /// inside the perceived radius is the closest the census can come to "in
    /// aggro band". Rows beyond it (only reachable from a hand-built frame) are
    /// not counted, exactly as the radar would have dropped them.
    /// </summary>
    public const float EnemyBandM = BotRadarProjection.PerceivedRadiusM;

    /// <summary>
    /// Flat distance (metres) at which a hostile is read as an immediate threat.
    ///
    /// The census carries no attack state, so "attacking me" cannot be read
    /// directly: a hostile inside the engine's own melee reach
    /// (<see cref="CombatDecisionTree.DefaultMeleeMax"/>) is the closest the
    /// census comes to it — at that range the hostile can attack the observer
    /// this instant. This is documented as a PROXY, never presented as attack
    /// evidence.
    /// </summary>
    public const float ContactThreatRangeM = CombatDecisionTree.DefaultMeleeMax;

    /// <summary>Own hp ratio above which the observer is Healthy (band: ratio &gt; 0.50).</summary>
    public const float HealthyBandMin = 0.50f;

    /// <summary>Own hp ratio at or below which the observer is Critical (band: ratio &lt;= 0.20); between it and <see cref="HealthyBandMin"/> is Wounded.</summary>
    public const float CriticalBandMax = 0.20f;

    /// <summary>Enemy count at which a healthy observer is already Engaged (outnumbered).</summary>
    public const int OutnumberedEnemyCount = 2;

    /// <summary>
    /// The semantic place key that means "the observer is at its farm". The
    /// caller's place vocabulary is authoritative; this is the one key the
    /// belief layer has to recognise to derive <see cref="BeliefFacts.AtFarm"/>.
    /// </summary>
    public const string FarmPlaceKey = "farm";

    /// <summary>
    /// Interprets one frame pair with NO self state: every fact that depends on
    /// the observer's own identity/target/bag stays UNKNOWN (see
    /// <see cref="BeliefInputs.None"/>). For a driver that has self state, use
    /// the <see cref="BeliefInputs"/> overload.
    /// </summary>
    public static BotBeliefState Interpret(Snapshot snapshot, PerceptionFrameDiff diff, CurrentPlace place)
        => Interpret(snapshot, diff, place, BeliefInputs.None);

    /// <summary>
    /// Interprets one frame pair into the observer's belief state.
    ///
    /// Derivation rules, per fact (all fail-closed):
    ///  - <see cref="BeliefFacts.EnemyNearby"/>/<see cref="BeliefNumbers.EnemyCount"/>:
    ///    perceived NPCs that the perception layer already established as alive
    ///    hostiles (<c>PerceivedNpc.Hostile == true &amp;&amp; Alive == true</c>) inside
    ///    <see cref="EnemyBandM"/>. A row whose hostility could not be resolved
    ///    is never counted as an enemy, and poisons the fact into UNKNOWN rather
    ///    than letting "we could not tell" read as "nothing is here";
    ///  - <see cref="BeliefFacts.TargetVisible"/>/<see cref="BeliefNumbers.TargetDistance"/>:
    ///    the row whose objId is the observer's current target. Found → set, and
    ///    the distance is the diff's own moved-to distance when this frame
    ///    recorded a move for it (otherwise the row's own measurement — the two
    ///    are the same reading on a real frame). Not found → the census says it
    ///    is not perceived, so the fact is a KNOWN false (a target that
    ///    disappeared and a target outside the band are both "not visible to
    ///    this census"). NO target at all → UNKNOWN, never a fabricated "far";
    ///  - <see cref="BeliefFacts.OwnedCorpseNearby"/>/<see cref="BeliefNumbers.OwnedCorpseDistance"/>/<see cref="BeliefRefs.OwnedCorpseObjId"/>:
    ///    a perceived corpse whose container is lootable
    ///    (<c>PerceivedCorpse.Lootable == true</c>) and whose owner is the
    ///    observer itself, nearest first. The diff CLEARS it: an objId that
    ///    became looted or disappeared this frame is excluded even if a stale
    ///    row still claims a lootable container. A corpse with an UNKNOWN owner
    ///    cannot be ruled out, so the fact goes UNKNOWN; an unknown self objId
    ///    makes it UNKNOWN too — an unknown self never claims an unowned corpse;
    ///  - <see cref="BeliefFacts.MerchantNearby"/>/<see cref="BeliefNumbers.NearestMerchantDistance"/>/
    ///    <see cref="BeliefRefs.MerchantObjId"/>: the nearest perceived NPC the
    ///    perception layer established as a VENDOR (<c>PerceivedNpc.Merchant ==
    ///    true</c> — template merchant gate plus a real shop pack). Distance
    ///    alone never resolves this: a near non-vendor does not count, and an
    ///    unresolved merchant flag leaves the fact UNKNOWN. The named vendor is
    ///    the nearest one the census could establish;
    ///  - <see cref="BeliefFacts.AtFarm"/>: the caller's
    ///    <see cref="CurrentPlace.SemanticPlace"/> equals
    ///    <see cref="FarmPlaceKey"/>. A null place key is UNKNOWN (a place-less
    ///    driver can never assert "not at the farm");
    ///  - <see cref="BeliefFacts.FarmHasOpenPlot"/>: ALWAYS UNKNOWN here. No
    ///    observation in this stack establishes an open plot, and a fabricated
    ///    false would read as "the farm is full". This is the documented seam
    ///    for a future plot-observation surface — deliberately not built;
    ///  - <see cref="BeliefFacts.InventoryFull"/>: the caller's bag free slots
    ///    (<c>0</c> → full, <c>&gt; 0</c> → known not full). A null (no bag, or
    ///    the engine's unlimited container) and a nonsense negative capacity are
    ///    UNKNOWN, never "full".
    ///
    /// <see cref="BeliefRefs.SemanticPlaceKey"/> is carried verbatim whenever
    /// the caller resolved one, and <see cref="BeliefRefs.RelevantObjId"/>/
    /// <see cref="BeliefNumbers.RelevantDistance"/> name the nearest entity the
    /// perception layer read as interactable — the census's approximation of a
    /// plan-relevant interactable (no plan input exists in this signature).
    ///
    /// <see cref="ThreatLevel"/> is evaluated only while an enemy is present (an
    /// empty census is always <see cref="ThreatLevel.None"/>, so a low hp bar
    /// with no hostile is not read as combat). <see cref="ThreatLevel.Critical"/>
    /// requires the observer's own critical hp band. Otherwise a hostile inside
    /// <see cref="ContactThreatRangeM"/>, or the observer's own health changing
    /// this frame (the diff's health-change list for the observer's own objId —
    /// the census normally excludes the observer, so this arm only fires on a
    /// frame that carries a self row), or being outnumbered, or the wounded band
    /// escalate to <see cref="ThreatLevel.Engaged"/>; a healthy, unhurt,
    /// non-engaged observer reads <see cref="ThreatLevel.Ambient"/>. An
    /// unreadable hp ratio makes no band claim and therefore never escalates on
    /// its own.
    ///
    /// ARCHETYPE WEIGHTING IS A LATER SEAM — NOT BUILT HERE. The hp bands above
    /// are flat thresholds on the raw ratio; a personality/archetype weighted
    /// band (a tank shrugging off what a mage flees from) would enter as an
    /// explicit weight applied at <see cref="HpBandOf"/> or as an added
    /// <see cref="BeliefInputs"/> field, and it must never change the
    /// fail-closed contract: weighting a band cannot manufacture a fact. This
    /// method is where that seam lands, and no caller today supplies a weight.
    /// </summary>
    public static BotBeliefState Interpret(
        Snapshot snapshot,
        PerceptionFrameDiff diff,
        CurrentPlace place,
        BeliefInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(diff);

        var facts = BeliefFacts.None;
        var unknown = 0UL;

        var enemyCount = 0;
        var enemyUnresolved = false;
        var contactHostile = false;

        PerceivedEntity? targetRow = null;
        PerceivedEntity? corpseRow = null;
        var corpseOwnerUnresolved = false;
        PerceivedEntity? merchantRow = null;
        var vendorUnresolved = false;
        PerceivedEntity? interactableRow = null;

        // ONE pass over the census. Every fact that is a function of a perceived
        // row is accumulated here; nothing below re-walks the rows.
        foreach (var entity in snapshot.Entities)
        {
            if (entity.Kind == RadarEntityKind.Npc && entity.Npc is { } npc)
            {
                // Alive-and-hostile is the perception layer's own verdict, so no
                // relation query runs here. An unresolved hostility on a live row
                // is tracked so the enemy fact can go UNKNOWN instead of false.
                if (npc.Hostile == true)
                {
                    if (npc.Alive == true && entity.Distance <= EnemyBandM)
                    {
                        enemyCount++;
                        if (entity.Distance <= ContactThreatRangeM)
                            contactHostile = true;
                    }
                }
                else if (npc.Hostile == null && npc.Alive != false)
                {
                    enemyUnresolved = true;
                }

                if (npc.Merchant == true)
                {
                    if (IsNearer(entity, merchantRow))
                        merchantRow = entity;
                }
                else if (npc.Merchant == null)
                {
                    vendorUnresolved = true;
                }
            }

            if (inputs.CurrentTargetObjId != 0 && entity.ObjId == inputs.CurrentTargetObjId)
                targetRow = entity;

            if (entity.Corpse is { Lootable: true } corpse)
            {
                if (corpse.Owner is { } owner)
                {
                    // Ownership is only comparable against a known self objId, and
                    // the diff's explicit clear beats a stale lootable row.
                    if (inputs.SelfObjId != 0 && owner == inputs.SelfObjId &&
                        !WasClearedThisFrame(diff, entity.ObjId) && IsNearer(entity, corpseRow))
                        corpseRow = entity;
                }
                else
                {
                    corpseOwnerUnresolved = true;
                }
            }

            if (entity.IsInteractable && IsNearer(entity, interactableRow))
                interactableRow = entity;
        }

        // --- facts, one at a time, each either set or unknown (never both).
        if (enemyCount > 0)
            facts |= BeliefFacts.EnemyNearby;
        else if (enemyUnresolved)
            unknown |= BeliefFacts.EnemyNearby;

        var targetDistance = float.NaN;
        if (inputs.CurrentTargetObjId == 0)
        {
            unknown |= BeliefFacts.TargetVisible;
        }
        else if (targetRow is { } target)
        {
            facts |= BeliefFacts.TargetVisible;
            targetDistance = MovedDistance(diff, target.ObjId) ?? target.Distance;
        }
        // else: the census does not carry the target — a KNOWN "not visible".

        var corpseDistance = float.NaN;
        var corpseObjId = 0u;
        if (corpseRow is { } ownedCorpse)
        {
            facts |= BeliefFacts.OwnedCorpseNearby;
            corpseDistance = ownedCorpse.Distance;
            corpseObjId = ownedCorpse.ObjId;
        }
        else if (inputs.SelfObjId == 0 || corpseOwnerUnresolved)
        {
            unknown |= BeliefFacts.OwnedCorpseNearby;
        }

        var merchantDistance = float.NaN;
        var merchantObjId = 0u;
        if (merchantRow is { } merchant)
        {
            facts |= BeliefFacts.MerchantNearby;
            merchantDistance = merchant.Distance;
            merchantObjId = merchant.ObjId;
        }
        else if (vendorUnresolved)
        {
            unknown |= BeliefFacts.MerchantNearby;
        }

        if (place.SemanticPlace is null)
            unknown |= BeliefFacts.AtFarm;
        else if (string.Equals(place.SemanticPlace, FarmPlaceKey, StringComparison.OrdinalIgnoreCase))
            facts |= BeliefFacts.AtFarm;

        // No observation in this stack establishes an open plot: unknown, always.
        unknown |= BeliefFacts.FarmHasOpenPlot;

        if (inputs.BagFreeSlots is { } freeSlots)
        {
            if (freeSlots < 0)
                unknown |= BeliefFacts.InventoryFull;
            else if (freeSlots == 0)
                facts |= BeliefFacts.InventoryFull;
        }
        else
        {
            unknown |= BeliefFacts.InventoryFull;
        }

        var interactableDistance = float.NaN;
        var interactableObjId = 0u;
        if (interactableRow is { } interactable)
        {
            interactableDistance = interactable.Distance;
            interactableObjId = interactable.ObjId;
        }

        return new BotBeliefState
        {
            Facts = facts,
            Unknown = unknown,
            Numbers = new BeliefNumbers
            {
                EnemyCount = enemyCount,
                TargetDistance = targetDistance,
                OwnedCorpseDistance = corpseDistance,
                NearestMerchantDistance = merchantDistance,
                RelevantDistance = interactableDistance
            },
            Refs = new BeliefRefs
            {
                TargetObjId = targetRow?.ObjId ?? 0u,
                OwnedCorpseObjId = corpseObjId,
                MerchantObjId = merchantObjId,
                RelevantObjId = interactableObjId,
                SemanticPlaceKey = place.SemanticPlace
            },
            Threat = ThreatOf(diff, inputs, enemyCount, contactHostile),
            ObservedAtUtc = diff.ObservedAtUtc,
            DeltaSeq = diff.ObservedAtUtc.Ticks
        };
    }

    /// <summary>
    /// Own-hp band. <see cref="SelfHpBand.Unknown"/> (a NaN ratio) makes NO band
    /// claim at all, so the threat rules below never escalate on an unreadable
    /// ratio — the fail-safe direction.
    ///
    /// The archetype-weighting seam documented on
    /// <see cref="Interpret(Snapshot, PerceptionFrameDiff, CurrentPlace, BeliefInputs)"/>
    /// would enter here (an explicit weight on the raw ratio); nothing today
    /// supplies one, and the thresholds are the flat constants above.
    /// </summary>
    private static SelfHpBand HpBandOf(float ratio)
    {
        if (float.IsNaN(ratio))
            return SelfHpBand.Unknown;
        if (ratio <= CriticalBandMax)
            return SelfHpBand.Critical;
        return ratio <= HealthyBandMin ? SelfHpBand.Wounded : SelfHpBand.Healthy;
    }

    /// <summary>
    /// The threat verdict: <see cref="ThreatLevel.None"/> without an enemy (a
    /// threatened-only-if-threatened rule, so a low hp bar with an empty census
    /// is not read as combat); otherwise the max of the escalation signals —
    /// the critical hp band, a hostile inside <see cref="ContactThreatRangeM"/>
    /// (the documented attacking-me proxy), the observer's own health changing
    /// this frame, the wounded band, and being outnumbered.
    /// </summary>
    private static ThreatLevel ThreatOf(
        PerceptionFrameDiff diff,
        BeliefInputs inputs,
        int enemyCount,
        bool contactHostile)
    {
        if (enemyCount == 0)
            return ThreatLevel.None;

        // The only arm that reaches Critical: the observer's own hp is in the
        // band where the next exchange can end it, with an enemy present.
        if (HpBandOf(inputs.SelfHpRatio) == SelfHpBand.Critical)
            return ThreatLevel.Critical;

        // Fighting is ON: a hostile in reach, or the observer took damage this
        // frame, or it is outnumbered, or it is already wounded.
        if (contactHostile || SelfHurtThisFrame(diff, inputs.SelfObjId) ||
            HpBandOf(inputs.SelfHpRatio) == SelfHpBand.Wounded ||
            enemyCount >= OutnumberedEnemyCount)
            return ThreatLevel.Engaged;

        return ThreatLevel.Ambient;
    }

    /// <summary>
    /// True when this frame recorded the observer itself taking a health change
    /// — the diff's own transition evidence, and the second arm of the
    /// "attacking me" signal. The census excludes the observer, so this is only
    /// ever true for a frame that carries a self row.
    /// </summary>
    private static bool SelfHurtThisFrame(PerceptionFrameDiff diff, uint selfObjId)
    {
        if (selfObjId == 0)
            return false;

        var changes = diff.HealthChanged;
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].ObjId == selfObjId)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The diff's own moved-to distance for one objId, or null when this frame
    /// recorded no move for it. On a frame produced by
    /// <c>PerceptionDelta.Derive</c> this is the current row's own measurement;
    /// the walk exists so the named rule — a Moved transition rewrites the
    /// target's distance — is literally the code path.
    /// </summary>
    private static float? MovedDistance(PerceptionFrameDiff diff, uint objId)
    {
        var moved = diff.Moved;
        for (var i = 0; i < moved.Count; i++)
        {
            if (moved[i].ObjId == objId)
                return moved[i].ToDistance;
        }

        return null;
    }

    /// <summary>
    /// True when the diff carries explicit CLEAR evidence for one objId: its
    /// container was emptied (became looted) or it left the census
    /// (disappeared). The diff is the authority on transitions, so a stale row
    /// cannot resurrect a corpse this frame already cleared.
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

    /// <summary>
    /// Nearest-candidate test with a deterministic tie-break on the lower objId,
    /// so a rank over equal distances is stable across frames.
    /// </summary>
    private static bool IsNearer(PerceivedEntity candidate, PerceivedEntity? incumbent)
        => incumbent is null
           || candidate.Distance < incumbent.Distance
           || (candidate.Distance == incumbent.Distance && candidate.ObjId < incumbent.ObjId);

    private enum SelfHpBand
    {
        Unknown = 0,
        Healthy = 1,
        Wounded = 2,
        Critical = 3
    }
}
