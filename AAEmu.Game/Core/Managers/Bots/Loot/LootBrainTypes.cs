#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Loot;

/// <summary>
/// Who holds the loot rights on a corpse, as the decision layer can establish
/// them from the actor's own frame.
///
/// The vocabulary is the ENGINE's own attribution authority
/// (<c>AAEmu.Game.Models.Game.NPChar.Tagging</c>): the tagger (a solo player
/// past the 50% damage threshold) and the tag team (a party past it), plus the
/// damage-contributor census the tag-share path uses. There is no
/// <c>Unknown</c>-as-<c>Self</c> shortcut: a claim that cannot be verified stays
/// <see cref="Unresolved"/> and the chain withholds (fail-closed).
///
/// <see cref="Self"/> is also the honest verdict for OUR OWN pinned corpse when
/// no rival claim was ever recorded: the corpse record itself is quest-owned and
/// same-ObjId (<c>QuestBehavior.NotePinnedCorpse</c> only records the fixture
/// row's prey resolving dead while OUR committed selection points at it), and
/// the engine's own post-death state has already cleared the taggers, so an
/// absence of evidence on that corpse is not a fabricated claim — it is the
/// absence of a rival. A POSITIVE claim naming somebody else is
/// <see cref="Foreign"/> and withholds.
/// </summary>
public enum LootOwnership
{
    /// <summary>A team claim exists but the team could not be verified — never upgraded to Self.</summary>
    Unresolved = 0,

    /// <summary>The actor itself holds the corpse (tagger, or sole contributor on our own pinned corpse).</summary>
    Self = 1,

    /// <summary>The actor's own party holds the corpse (tag team / in-range party contributors).</summary>
    Party = 2,

    /// <summary>Another player or another team holds the corpse — never loot it.</summary>
    Foreign = 3
}

/// <summary>
/// Whether the corpse's contents are worth taking, as the decision layer reads it
/// from the container itself (<see cref="AAEmu.Game.Core.Managers.Bots.BotBagManager.IsTrash"/>
/// for every entry, plus the optional copper floor the caller supplies).
///
/// <see cref="Unresolved"/> means the read could not be made; the chain PROCEEDS
/// on it rather than skipping, because a fabricated skip would strand a real drop
/// on a corpse whose ownership and lootability are already established. Only a
/// positive <see cref="Junk"/> verdict withholds.
/// </summary>
public enum LootWorth
{
    /// <summary>The contents could not be read — never fabricates a skip.</summary>
    Unresolved = 0,

    /// <summary>At least one entry is worth keeping (or the floor is clear).</summary>
    Worthwhile = 1,

    /// <summary>Every entry is vendor junk, or the whole container is below the caller's copper floor.</summary>
    Junk = 2
}

/// <summary>
/// Whether the bag can take the corpse's contents. Read from
/// <c>ItemContainer.FreeSlotCount</c>; an unreadable bag
/// (<see cref="Unresolved"/>) PROCEEDS, and only a positive
/// <see cref="Full"/> withholds — and that skip is terminal (the ledger banks it).
/// </summary>
public enum LootSpace
{
    /// <summary>The bag could not be read — never fabricates a skip.</summary>
    Unresolved = 0,

    /// <summary>At least one free slot: the loot verb may take what fits.</summary>
    Room = 1,

    /// <summary>No free slot — the corpse is skipped, terminally.</summary>
    Full = 2
}

/// <summary>The actor-face verb a loot decision asks for. The dispatcher maps these onto <see cref="ActorActionType"/>.</summary>
public enum LootVerb
{
    /// <summary>No actor call this wake (the leg withdraws with the decision as its evidence).</summary>
    Hold = 0,

    /// <summary>The canonical <c>IGameplayActor.Loot</c> take (the exact CSLootOpenBagPacket lootAll call).</summary>
    Loot = 1
}

/// <summary>
/// Which arm of the decision chain produced the wake's decision — the decision's
/// own vocabulary for diagnostics.
/// </summary>
public enum LootArm
{
    /// <summary>Nothing to decide.</summary>
    None = 0,

    /// <summary>Arm 1 (veto): a survival condition owns the actor's wake.</summary>
    SurvivalVeto = 1,

    /// <summary>Arm 2 (ownership): the corpse's loot rights are not the actor's.</summary>
    Ownership = 2,

    /// <summary>Arm 3 (probe): the corpse / container did not resolve (gone, recycled, empty, unreadable).</summary>
    Probe = 3,

    /// <summary>
    /// Arm 4 (loop-release): the auto-attack loop that owns the kill has not torn
    /// down yet. Evaluated BEFORE the loot-once arm exactly as the production
    /// loop-release seam has always ordered it (the funnel's own
    /// <c>loop-live</c> arm), so a withheld wake's named cause is unchanged.
    /// </summary>
    LoopRelease = 4,

    /// <summary>
    /// Arm 5 (loot-once): this corpse already ran its Loot course, or the ledger
    /// banked a terminal skip for it. The last arm of the chain whose verdict is
    /// TERMINAL — a <see cref="LootReason.PriorSkip"/> decision names the banked
    /// cause on <see cref="LootDecision.Cause"/>.
    /// </summary>
    LootOnce = 5,

    /// <summary>Arm 6 (safe): a hostile is inside the safe radius (or the census could not be read).</summary>
    Safety = 6,

    /// <summary>Arm 7 (worth): nothing in the container is worth the slots.</summary>
    Worth = 7,

    /// <summary>Arm 8 (space): the bag cannot take the contents.</summary>
    Space = 8,

    /// <summary>The chain emitted the loot.</summary>
    Emit = 9
}

/// <summary>
/// The named reason a decision took its arm — an ENUM, not a built string: the
/// wake path formats text only when the funnel diagnostic actually reads it, so a
/// per-wake decision allocates nothing. <see cref="LootBrain.ReasonToken"/> maps
/// each reason onto the space-free token the funnel loot arm carries.
/// </summary>
public enum LootReason
{
    /// <summary>No reason recorded.</summary>
    None = 0,

    /// <summary>A survival condition owns the wake.</summary>
    SurvivalVetoed,

    /// <summary>The ledger banked a terminal skip for this corpse; the cause rides on the decision.</summary>
    PriorSkip,

    /// <summary>The actor holds the corpse (the emit arm's own reason).</summary>
    OwnershipSelf,

    /// <summary>The actor's party holds the corpse (the emit arm's own reason).</summary>
    OwnershipParty,

    /// <summary>Another player or team holds the corpse.</summary>
    OwnershipForeign,

    /// <summary>A team claim exists and could not be verified.</summary>
    OwnershipUnresolved,

    /// <summary>The corpse objId could not be read at all this wake.</summary>
    CorpseUnreadable,

    /// <summary>The recorded corpse objId left the world (despawned).</summary>
    CorpseGone,

    /// <summary>The recorded objId resolves live again, or is no longer our prey (engine ObjId recycling).</summary>
    CorpseRecycled,

    /// <summary>The corpse resolved but carries no readable container.</summary>
    ContainerMissing,

    /// <summary>The container resolved and is empty.</summary>
    ContainerEmpty,

    /// <summary>The container resolved, is non-empty, and is outside loot range.</summary>
    ContainerOutOfRange,

    /// <summary>The auto-attack loop is still live — loot releases only after its teardown.</summary>
    LoopLive,

    /// <summary>This corpse already ran its Loot course (loot-once memory).</summary>
    AlreadyLooted,

    /// <summary>The safety census could not be read.</summary>
    SafetyUnresolved,

    /// <summary>A hostile is inside the safe radius.</summary>
    Unsafe,

    /// <summary>Every entry is vendor junk (or the container is under the caller's copper floor).</summary>
    Junk,

    /// <summary>The bag has no free slot.</summary>
    NoBagRoom,

    /// <summary>The chain emitted the loot.</summary>
    Emitted
}

/// <summary>
/// What became of the corpse for this actor. Banked per (actor, corpse) in
/// <see cref="LootLedger"/> — bank-only: a disposition is recorded and read back,
/// never spent. <see cref="Skipped"/> is TERMINAL for the corpse (it is never
/// reconsidered); <see cref="Undecided"/> means the wake withheld for a cause
/// that may clear (loop teardown, a rival leaving, a hostile walking away).
/// </summary>
public enum LootDisposition
{
    /// <summary>No terminal disposition: the corpse may be reconsidered next wake.</summary>
    Undecided = 0,

    /// <summary>The Loot verb ran its terminal course for this corpse.</summary>
    Take = 1,

    /// <summary>A terminal skip (worth or space): the corpse is never reconsidered.</summary>
    Skipped = 2
}

/// <summary>
/// What the recorded corpse objId resolved to this wake. Deliberately NOT a
/// boolean pair: <see cref="Gone"/> (the world lost the objId) and
/// <see cref="Unreadable"/> (the frame itself could not be read) are distinct
/// facts, and <see cref="Recycled"/> keeps the engine's own ObjId-recycling rule
/// (a recorded objId that resolves live again is not our corpse).
/// </summary>
public enum LootCorpseState
{
    /// <summary>The corpse could not be read this wake (no actor frame).</summary>
    Unreadable = 0,

    /// <summary>The objId left the world.</summary>
    Gone = 1,

    /// <summary>The objId resolves dead and is the fixture row's prey — the lootable shape.</summary>
    Dead = 2,

    /// <summary>The objId resolves live, or is no longer our prey.</summary>
    Recycled = 3
}

/// <summary>
/// The loot-rights evidence for ONE corpse, as primitives only — no engine type,
/// so the classification stays a pure function (<see cref="LootBrain.ClassifyOwnership"/>)
/// and is unit-testable without a world.
///
/// The live adapter reads it from the engine's own tagging surface
/// (<c>Tagging.Tagger</c>, <c>Tagging.TagTeam</c> resolved through
/// <c>TeamManager</c>, and <c>Tagging.GetAllContributors</c> for the damage
/// census). All-zero evidence means "no claim was found", which is a distinct
/// fact from "a claim exists that we could not verify"
/// (<see cref="TagTeamId"/> != 0 with <see cref="TagTeamResolved"/> false).
/// </summary>
public readonly record struct LootClaimEvidence(
    uint ActorObjId,
    uint TaggerObjId,
    bool ActorIsTagger,
    bool TaggerSharesActorTeam,
    uint TagTeamId,
    bool TagTeamResolved,
    bool ActorInTagTeam,
    bool ContributorsReadable,
    int ContributorCount,
    int ForeignContributorCount,
    bool ActorIsContributor)
{
}

/// <summary>
/// Every input the loot decision chain reads for one wake.
///
/// Value type: the planner builds it once per wake from the live reads it owns
/// (the corpse resolve, the container probe, the tag/ownership evidence, the
/// safety census, the worth read, the bag's free slots) and the brain never
/// stores it. The ledger facts
/// (<see cref="PriorDisposition"/>/<see cref="PriorReason"/>) likewise arrive as
/// inputs, read from <see cref="LootLedger"/> by the caller — so the decision
/// function itself stays a pure function of its inputs.
///
/// Honesty contract (the decision layer's own fail-closed rule):
///  - an unreadable safety census is NOT safe (<see cref="SafetyResolved"/>
///    false withholds);
///  - an unreadable container is not an empty one
///    (<see cref="ContainerReadable"/> false withholds as a missing container);
///  - an unreadable WORTH or SPACE never fabricates a skip — those two arms
///    proceed on <see cref="LootWorth.Unresolved"/>/<see cref="LootSpace.Unresolved"/>,
///    because the corpse's ownership and lootability are already established;
///  - <see cref="LootOwnership.Unresolved"/> is never upgraded to Self.
/// </summary>
public readonly record struct LootBrainInputs(
    uint ActorObjId,
    uint CorpseObjId,
    bool SurvivalVetoed,
    LootCorpseState CorpseState,
    LootOwnership Ownership,
    bool ContainerReadable,
    int ContainerItemCount,
    bool ContainerInRange,
    bool LoopLive,
    bool AlreadyLooted,
    bool SafetyResolved,
    bool Safe,
    float NearestHostileDistanceM,
    LootWorth Worth,
    LootSpace Space,
    LootDisposition PriorDisposition,
    LootReason PriorReason,
    DateTime NowUtc)
{
    /// <summary>The container is readable, non-empty and in loot range — the one shape the loot verb can take.</summary>
    public bool ContainerLootable => ContainerReadable && ContainerItemCount > 0 && ContainerInRange;
}

/// <summary>
/// One wake's loot decision: the arm that fired, the named reason, the verb (and
/// the corpse it acts on), the three live verdicts the wake was evaluated
/// against, and the disposition to bank — everything a dispatcher and a lane log
/// need, and nothing that executes gameplay itself.
/// </summary>
public readonly record struct LootDecision(
    LootArm Arm,
    LootReason Reason,
    LootVerb Verb,
    uint CorpseObjId,
    LootOwnership Ownership,
    LootWorth Worth,
    LootSpace Space,
    LootDisposition Disposition)
{
    /// <summary>The banked cause a <see cref="LootReason.PriorSkip"/> decision is reporting.</summary>
    public LootReason Cause { get; init; } = LootReason.None;

    /// <summary>True when this decision asks the actor for the Loot verb.</summary>
    public bool IsEmit => Arm == LootArm.Emit;

    /// <summary>True when this decision asks the actor for a verb (false = the leg withdraws with this decision as evidence).</summary>
    public bool HasVerb => Verb == LootVerb.Loot;
}
