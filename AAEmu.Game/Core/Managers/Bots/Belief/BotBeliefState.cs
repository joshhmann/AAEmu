#nullable enable

using System.Globalization;

namespace AAEmu.Game.Core.Managers.Bots.Belief;

/// <summary>
/// Threat level of the observer's PRESENT MOMENT — the single coarsest belief
/// a behavior branch reads when it decides whether this wake is an emergency.
///
/// <see cref="None"/> — nothing threatening was perceived.
/// <see cref="Ambient"/> — a hostile is perceived while the observer is still
/// healthy, unhurt and not outnumbered.
/// <see cref="Engaged"/> — fighting is on: a hostile is inside contact range,
/// or the observer took damage this frame, or it is outnumbered, or it is in
/// its wounded band.
/// <see cref="Critical"/> — the observer is in its critical band with a hostile
/// present.
///
/// There is deliberately NO <c>Unknown</c> member: the enum is the verdict of
/// the escalation rules, and those rules degrade toward the lower level when
/// their inputs are unreadable (see <see cref="BeliefInterpreter"/>), never
/// toward a fabricated emergency.
/// </summary>
public enum ThreatLevel
{
    /// <summary>No threatening signal (empty census, healthy observer, no enemy).</summary>
    None = 0,

    /// <summary>A threat is perceived but is not acting on the observer.</summary>
    Ambient = 1,

    /// <summary>The threat is acting on the observer, or outnumbers / out-healths it.</summary>
    Engaged = 2,

    /// <summary>Immediate danger: contact-range hostility or a critical observer.</summary>
    Critical = 3
}

/// <summary>
/// The belief layer's FACT BITS — the boolean half of <see cref="BotBeliefState"/>,
/// in the same 64-bit <c>flags + mask</c> idiom as
/// <see cref="AAEmu.Game.Core.Managers.Bots.Goap.BotWorldState"/>.
///
/// Bit budget (binding): bits 0..36 belong to <c>BotWorldState</c> (its highest
/// assigned flag is <c>PlotConstructed = 1 &lt;&lt; 36</c>). Belief facts are allocated
/// from <see cref="FirstBeliefBit"/> upward inside that SAME 64-bit word, so:
///  - a belief word and a planner word can never be confused or OR-ed into a
///    value that claims a planner flag the belief layer never established;
///  - the two remain ONE budget (no second word, no 65th fact);
///  - projecting belief facts onto planner goals stays an EXPLICIT mapping
///    decision (a later seam) instead of an accidental alias. Aliasing a belief
///    fact onto a same-sounding <c>BotWorldState</c> flag here would claim more
///    than the census observed — e.g. <c>MerchantNearby</c> is every merchant,
///    while <c>BotWorldState.NearSeedMerchant</c> is the one seed vendor.
///
/// Every fact is FAIL-CLOSED: a fact whose value cannot be established this
/// frame has its bit in <see cref="BotBeliefState.Unknown"/> and is never set
/// in <see cref="BotBeliefState.Facts"/>.
/// </summary>
public static class BeliefFacts
{
    /// <summary>No facts.</summary>
    public const ulong None = 0UL;

    /// <summary>First bit of the belief allocation (immediately above <c>BotWorldState</c>'s last flag).</summary>
    public const int FirstBeliefBit = 37;

    /// <summary>At least one hostile NPC is alive inside the aggro band (count in <see cref="BeliefNumbers.EnemyCount"/>).</summary>
    public const ulong EnemyNearby = 1UL << FirstBeliefBit;

    /// <summary>The observer's current target objId was perceived this frame.</summary>
    public const ulong TargetVisible = 1UL << (FirstBeliefBit + 1);

    /// <summary>An owned, lootable corpse of the observer is perceived (ref in <see cref="BeliefRefs.OwnedCorpseObjId"/>).</summary>
    public const ulong OwnedCorpseNearby = 1UL << (FirstBeliefBit + 2);

    /// <summary>A vendor-role NPC is perceived (ref in <see cref="BeliefRefs.MerchantObjId"/>).</summary>
    public const ulong MerchantNearby = 1UL << (FirstBeliefBit + 3);

    /// <summary>The resolved semantic place is the farm place key.</summary>
    public const ulong AtFarm = 1UL << (FirstBeliefBit + 4);

    /// <summary>The farm place has an open plot. Always <see cref="BotBeliefState.Unknown"/> until an open-plot observation exists.</summary>
    public const ulong FarmHasOpenPlot = 1UL << (FirstBeliefBit + 5);

    /// <summary>Every bag slot is taken (read from the observer's own bag capacity).</summary>
    public const ulong InventoryFull = 1UL << (FirstBeliefBit + 6);
}

/// <summary>
/// The identity half of a belief: WHICH world object each belief fact is about.
/// Zero means "none" — never a fabricated objId (objId 0 is not a legal world
/// object, so it is safe as the sentinel).
/// </summary>
public readonly struct BeliefRefs
{
    /// <summary>objId of the observer's current target, or 0 when no visible target.</summary>
    public uint TargetObjId { get; init; }

    /// <summary>objId of the owned lootable corpse, or 0 when none.</summary>
    public uint OwnedCorpseObjId { get; init; }

    /// <summary>objId of the nearest vendor-role NPC, or 0 when none perceived.</summary>
    public uint MerchantObjId { get; init; }

    /// <summary>objId of the nearest interactable entity (the plan-required-interactable seam), or 0 when none.</summary>
    public uint RelevantObjId { get; init; }

    /// <summary>
    /// The resolved semantic place key this belief was interpreted in; null when
    /// the caller could not resolve a place. Carried verbatim (no normalization)
    /// so a consumer's own place vocabulary stays the single authority.
    /// </summary>
    public string? SemanticPlaceKey { get; init; }

    public override string ToString()
        => $"target={TargetObjId} corpse={OwnedCorpseObjId} merchant={MerchantObjId} " +
           $"relevant={RelevantObjId} place={SemanticPlaceKey ?? "unknown"}";
}

/// <summary>
/// The numeric half of a belief. Every DISTANCE is NaN when unknown — never 0,
/// which would read as "at zero metres". <see cref="EnemyCount"/> is a count,
/// so 0 is a legitimate, known value.
///
/// Because an <c>init</c>-only struct's default is 0, the interpreter writes
/// EVERY distance explicitly (NaN or measured): a value left at the struct
/// default would be a fabricated distance claim.
/// </summary>
public readonly struct BeliefNumbers
{
    /// <summary>Alive hostile NPCs inside the aggro band (0 when none or unreadable).</summary>
    public int EnemyCount { get; init; }

    /// <summary>Flat distance to the visible target; NaN when no target is visible.</summary>
    public float TargetDistance { get; init; }

    /// <summary>Flat distance to the owned lootable corpse; NaN when none.</summary>
    public float OwnedCorpseDistance { get; init; }

    /// <summary>Flat distance to the nearest vendor-role NPC; NaN when none perceived.</summary>
    public float NearestMerchantDistance { get; init; }

    /// <summary>Flat distance to the nearest interactable entity; NaN when none.</summary>
    public float RelevantDistance { get; init; }

    public override string ToString()
        => $"enemies={EnemyCount} target={Format(TargetDistance)} corpse={Format(OwnedCorpseDistance)} " +
           $"merchant={Format(NearestMerchantDistance)} relevant={Format(RelevantDistance)}";

    private static string Format(float value)
        => float.IsNaN(value) ? "unknown" : value.ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// Layer 4 of the perception stack — the BELIEF STATE: the derived, fail-closed
/// fact word one actor holds about the world at one frame.
///
/// Shape and honesty contract:
///  - <see cref="Facts"/> is the 64-bit fact word (<see cref="BeliefFacts"/>),
///    drawn from the SAME bit budget as <c>BotWorldState</c> (see
///    <see cref="BeliefFacts.FirstBeliefBit"/>) — one budget, no aliasing;
///  - <see cref="Unknown"/> is the MASK of facts whose value could not be
///    established this frame. The same convention as <c>BotWorldState.Mask</c>:
///    an unknown fact is never set, and a consumer must read
///    <see cref="IsSet"/> (not the raw bits) to avoid treating "unknown" as
///    "false";
///  - every number is a live measurement or NaN, every ref is an objId or 0;
///  - <see cref="ObservedAtUtc"/> carries the FRAME's read time through the
///    derivation, so a consumer can always tell how stale the belief is;
///    <see cref="DeltaSeq"/> is that frame's monotonic order key and covers
///    every number/ref in the state (they were all derived from the same frame);
///  - <see cref="SnapshotId"/> is the frame identity a diagnostic joins on. It
///    is COMPUTED from the carried time rather than stored, so the interpreter
///    stays allocation-free per frame (formatting happens only if a diagnostic
///    actually reads it).
///
/// Value type: the state is copied into behavior code every wake. It is a
/// struct with no reference of its own beyond the place key string.
/// </summary>
public readonly struct BotBeliefState
{
    /// <summary>Allocation-free/empty state: every fact unknown, every number NaN, no threat.</summary>
    public static BotBeliefState Empty => new()
    {
        Facts = BeliefFacts.None,
        Unknown = UnknownAll,
        Numbers = new BeliefNumbers
        {
            TargetDistance = float.NaN,
            OwnedCorpseDistance = float.NaN,
            NearestMerchantDistance = float.NaN,
            RelevantDistance = float.NaN
        },
        Refs = default
    };

    /// <summary>Mask of every belief fact (the fail-closed default for <see cref="Unknown"/>).</summary>
    public const ulong UnknownAll = BeliefFacts.EnemyNearby | BeliefFacts.TargetVisible |
                                    BeliefFacts.OwnedCorpseNearby | BeliefFacts.MerchantNearby |
                                    BeliefFacts.AtFarm | BeliefFacts.FarmHasOpenPlot |
                                    BeliefFacts.InventoryFull;

    /// <summary>Facts established this frame (only bits outside <see cref="Unknown"/> are meaningful).</summary>
    public ulong Facts { get; init; }

    /// <summary>Mask of facts whose value could not be established (fail-closed).</summary>
    public ulong Unknown { get; init; }

    /// <summary>Measured numbers (distances NaN when unknown).</summary>
    public BeliefNumbers Numbers { get; init; }

    /// <summary>Identity of the objects the facts are about (0 = none).</summary>
    public BeliefRefs Refs { get; init; }

    /// <summary>The escalated threat verdict for this frame.</summary>
    public ThreatLevel Threat { get; init; }

    /// <summary>UTC read time of the frame this belief was derived from.</summary>
    public DateTime ObservedAtUtc { get; init; }

    /// <summary>
    /// Monotonic order key of the frame this belief was derived from (the frame
    /// diff's own UTC stamp). The perception ring carries no counter, and the
    /// frame's UTC stamp orders frames identically, so this is the frame
    /// sequence without a second source of truth.
    /// </summary>
    public long DeltaSeq { get; init; }

    /// <summary>
    /// Frame identity for diagnostics/log correlation: the carried frame time
    /// as a round-trip ISO-8601 stamp. Computed on read (never stored) so the
    /// interpreter allocates nothing per frame.
    /// </summary>
    public string SnapshotId => ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// True when every bit in <paramref name="fact"/> was established this frame
    /// (the fact is neither unknown nor partially unknown).
    /// </summary>
    public bool IsKnown(ulong fact) => fact != 0UL && (Unknown & fact) == 0UL;

    /// <summary>
    /// True when every bit in <paramref name="fact"/> was established AND set.
    /// Fail-closed by construction: a fact that is unknown can never read as
    /// set, so a consumer can never act on a fabricated "true".
    /// </summary>
    public bool IsSet(ulong fact) => IsKnown(fact) && (Facts & fact) == fact;

    /// <summary>True when any bit in <paramref name="fact"/> could not be established this frame.</summary>
    public bool IsUnknown(ulong fact) => fact != 0UL && (Unknown & fact) != 0UL;

    public override string ToString()
        => $"[{SnapshotId} seq={DeltaSeq} facts=0x{Facts:X} unknown=0x{Unknown:X} " +
           $"threat={Threat} {Numbers} {Refs}]";
}
