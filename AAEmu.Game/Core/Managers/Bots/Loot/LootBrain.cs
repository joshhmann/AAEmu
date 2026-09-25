#nullable enable

namespace AAEmu.Game.Core.Managers.Bots.Loot;

/// <summary>
/// The LOOT BRAIN — one wake's loot decision for one actor, derived in a fixed
/// chain from an immutable <see cref="LootBrainInputs"/>: the survival veto, the
/// ownership gate, the corpse/container probe, the loop release, loot-once, the
/// safety radius, worth, and bag space, ending in the one emit arm.
///
/// Scope (binding for this increment):
///  - PURE: no engine query, no world scan, no mutation, no clock read — every
///    number and verdict arrives on the inputs, and the same inputs always decide
///    the same wake. Every live read (the corpse resolve, the container probe, the
///    tag/ownership evidence, the wake census, the worth read, the bag slots)
///    lives in <see cref="LootBrainPlanner"/>; the per-actor memory lives in
///    <see cref="LootLedger"/> and arrives as inputs;
///  - VERBS ONLY: the decision names the canonical <c>IGameplayActor.Loot</c>
///    verb and its corpse objId. It never executes, never writes a Character
///    field, never opens a second gameplay path — the existing
///    <c>QuestBehavior.DispatchLoot</c> stays the single dispatcher;
///  - FAIL-CLOSED, in the direction each verdict's own evidence allows:
///    an unreadable safety census is NOT safe, an unreadable container is not an
///    empty one, an unverified team claim is never upgraded to Self, and an
///    unreadable worth/space never fabricates a skip (those two arms proceed —
///    the corpse's ownership and lootability are already established, so a
///    fabricated skip there would strand a real drop).
///
/// The chain, in evaluation order (the first arm that applies owns the wake):
///   1. SURVIVAL VETO      — a survival condition owns the actor's wake → hold;
///   2. OWNERSHIP          — the corpse's loot rights are somebody else's (or a
///                           team claim that could not be verified) → hold;
///   3. PROBE              — the corpse is unreadable/gone/recycled, or its
///                           container is missing/empty/out of loot range → hold;
///   4. LOOP RELEASE       — the auto-attack loop that owns the kill is still
///                           live → hold (releases by construction at teardown);
///   5. LOOT-ONCE          — this corpse already ran its Loot course, or the
///                           ledger banked a terminal skip → hold (terminal);
///   6. SAFE               — the wake census is unreadable, or a hostile stands
///                           inside the safe radius → hold;
///   7. WORTH              — every container entry is vendor junk, or the whole
///                           container is under the caller's copper floor → skip
///                           (terminal);
///   8. SPACE              — the bag has no free slot → skip (terminal);
///   9. EMIT               — the one verb: Loot this corpse.
///
/// Arms 2-6 withhold UNDECIDED (their causes can clear); arms 7-8 skip TERMINALLY
/// (they cannot, for this corpse). The emit arm banks the take.
/// </summary>
public static class LootBrain
{
    /// <summary>
    /// The default safe radius: a loot take inside this distance of a live
    /// hostile is not worth the risk. Quoted from the melee band ceiling's
    /// neighbourhood rather than a perception radius, and deliberately a flat
    /// radius on the wake's own census — the same shape the objective funnel's
    /// candidate legality uses (<c>CombatDecisionTree.IsHostileTarget</c> over
    /// <c>ObservedContext.NearbyNpcObjIds</c>).
    /// </summary>
    public const float DefaultSafeRadiusM = 15.0f;

    /// <summary>
    /// True for the reasons whose skip is TERMINAL for a corpse (the two the
    /// worth and space arms produce), i.e. exactly the reasons
    /// <see cref="LootLedger.Bank"/> accepts as a <see cref="LootDisposition.Skipped"/>.
    /// </summary>
    public static bool IsTerminalSkip(LootReason reason)
        => reason is LootReason.Junk or LootReason.NoBagRoom;

    /// <summary>
    /// The engine's own loot rights for a corpse, resolved from the tagging
    /// evidence — the ONE place ownership is classified, so the live adapter and
    /// the unit tests can never disagree.
    ///
    /// Order of precedence (mirrors <c>Npc.DoDie</c>/<c>LootingContainer.GenerateLoot</c>):
    ///   1. a verified tag team the actor is a member of → <see cref="LootOwnership.Party"/>;
    ///   2. a tag team that could not be verified → <see cref="LootOwnership.Unresolved"/>
    ///      (never upgraded to Self);
    ///   3. a tagger that is not the actor and not a party-mate → <see cref="LootOwnership.Foreign"/>;
    ///   4. a tagger that IS the actor (or a verified party-mate) → Self/Party;
    ///   5. no tagger: the readable contributor census decides —
    ///      the actor alone (or with party-mates only) → Self/Party, another
    ///      contributor present → Foreign, an unreadable census → Unresolved;
    ///   6. no evidence at all on OUR OWN pinned corpse → Self.
    ///
    /// The last rule is the honest verdict for the quest path's own corpse and is
    /// what keeps the G7c behaviour byte-identical: the corpse record is
    /// quest-owned and same-ObjId by construction, and the engine clears the
    /// taggers at death, so absence of a rival claim on that corpse is not
    /// evidence of a rival. <paramref name="corpseIsOurPinnedCorpse"/> names that
    /// fact explicitly so a caller can never reach this rule with a corpse it did
    /// not establish.
    /// </summary>
    public static LootOwnership ClassifyOwnership(
        in LootClaimEvidence evidence, bool corpseIsOurPinnedCorpse)
    {
        if (evidence.TagTeamId != 0)
        {
            if (!evidence.TagTeamResolved)
                return LootOwnership.Unresolved;
            if (evidence.ActorInTagTeam)
                return LootOwnership.Party;
            return LootOwnership.Foreign;
        }

        if (evidence.TaggerObjId != 0)
        {
            if (evidence.ActorIsTagger || evidence.TaggerObjId == evidence.ActorObjId)
                return LootOwnership.Self;
            if (evidence.TaggerSharesActorTeam)
                return LootOwnership.Party;
            return LootOwnership.Foreign;
        }

        if (evidence.ContributorsReadable && evidence.ContributorCount > 0)
        {
            if (!evidence.ActorIsContributor)
                return LootOwnership.Foreign;
            return evidence.ForeignContributorCount > 0 ? LootOwnership.Foreign : LootOwnership.Self;
        }

        // No claim was found. On our own pinned corpse that is the absence of a
        // rival (the record is quest-owned, and the engine cleared the taggers at
        // death); anywhere else it is simply unresolved.
        return corpseIsOurPinnedCorpse ? LootOwnership.Self : LootOwnership.Unresolved;
    }

    /// <summary>
    /// True when the ownership verdict permits the actor to take the corpse.
    /// <see cref="LootOwnership.Unresolved"/> and <see cref="LootOwnership.Foreign"/>
    /// both withhold.
    /// </summary>
    public static bool MayLoot(LootOwnership ownership)
        => ownership is LootOwnership.Self or LootOwnership.Party;

    /// <summary>
    /// Evaluates the wake's decision. Pure over the inputs: the caller owns the
    /// ledger, the clock, and every world read that produced them.
    /// </summary>
    public static LootDecision Decide(in LootBrainInputs inputs)
    {
        // ------------------------------------------------ 1. SURVIVAL VETO
        if (inputs.SurvivalVetoed)
            return Withhold(LootArm.SurvivalVeto, LootReason.SurvivalVetoed, inputs);

        // ------------------------------------------------ 2. OWNERSHIP
        if (!MayLoot(inputs.Ownership))
        {
            return Withhold(LootArm.Ownership, inputs.Ownership switch
            {
                LootOwnership.Foreign => LootReason.OwnershipForeign,
                LootOwnership.Unresolved => LootReason.OwnershipUnresolved,
                _ => LootReason.OwnershipForeign
            }, inputs);
        }

        // ------------------------------------------------ 3. PROBE
        var probeReason = ProbeReason(inputs);
        if (probeReason != LootReason.None)
            return Withhold(LootArm.Probe, probeReason, inputs);

        // ------------------------------------------------ 4. LOOP RELEASE
        // The loop owns the corpse until it tears itself down (the engine's
        // auto-attack task stops on target null/dead and teardown clears the
        // target + generates the loot), so a Loot dispatched mid-loop would race
        // the kill legs' own teardown. Release is by construction, never a timer.
        if (inputs.LoopLive)
            return Withhold(LootArm.LoopRelease, LootReason.LoopLive, inputs);

        // ------------------------------------------------ 5. LOOT-ONCE
        // Terminal: a corpse whose Loot already ran its course, or whose ledger
        // disposition banked a skip, is never reconsidered — even if its container
        // refills. The banked cause rides on the decision.
        if (inputs.AlreadyLooted || inputs.PriorDisposition == LootDisposition.Take)
            return Withhold(LootArm.LootOnce, LootReason.AlreadyLooted, inputs);
        if (inputs.PriorDisposition == LootDisposition.Skipped)
        {
            var banked = inputs.PriorReason == LootReason.None ? LootReason.PriorSkip : inputs.PriorReason;
            return Withhold(LootArm.LootOnce, LootReason.PriorSkip, inputs) with { Cause = banked };
        }

        // ------------------------------------------------ 6. SAFE
        // Fail-closed: an unreadable census is NOT safe. The census is the wake's
        // own bounded perception, resolved live through the engine's own hostility
        // rule — never a fabricated "nothing is around".
        if (!inputs.SafetyResolved || !inputs.Safe)
        {
            return Withhold(LootArm.Safety,
                inputs.SafetyResolved ? LootReason.Unsafe : LootReason.SafetyUnresolved, inputs);
        }

        // ------------------------------------------------ 7. WORTH
        // Only a POSITIVE junk verdict skips (an unreadable worth proceeds).
        // Terminal: this corpse is never reconsidered.
        if (inputs.Worth == LootWorth.Junk)
            return Skip(LootArm.Worth, LootReason.Junk, inputs);

        // ------------------------------------------------ 8. SPACE
        // Only a POSITIVE full-bag verdict skips (an unreadable bag proceeds).
        if (inputs.Space == LootSpace.Full)
            return Skip(LootArm.Space, LootReason.NoBagRoom, inputs);

        // ------------------------------------------------ 9. EMIT
        return new LootDecision(
            LootArm.Emit,
            inputs.Ownership == LootOwnership.Party ? LootReason.OwnershipParty : LootReason.OwnershipSelf,
            LootVerb.Loot,
            inputs.CorpseObjId,
            inputs.Ownership,
            inputs.Worth,
            inputs.Space,
            LootDisposition.Take);
    }

    /// <summary>
    /// The probe arm's own predicate: everything about the corpse and its
    /// container that must resolve before a loot is even considered. Returns
    /// <see cref="LootReason.None"/> when the corpse is the lootable shape.
    /// </summary>
    public static LootReason ProbeReason(in LootBrainInputs inputs)
    {
        switch (inputs.CorpseState)
        {
            case LootCorpseState.Unreadable:
                return LootReason.CorpseUnreadable;
            case LootCorpseState.Gone:
                return LootReason.CorpseGone;
            case LootCorpseState.Recycled:
                return LootReason.CorpseRecycled;
        }

        if (!inputs.ContainerReadable)
            return LootReason.ContainerMissing;
        if (inputs.ContainerItemCount <= 0)
            return LootReason.ContainerEmpty;
        if (!inputs.ContainerInRange)
            return LootReason.ContainerOutOfRange;
        return LootReason.None;
    }

    /// <summary>
    /// Builds a verb-less, undecided decision (the leg withdraws with it as
    /// evidence). Undecided is the fail-safe disposition: a cause that may clear
    /// must never bank a terminal skip.
    /// </summary>
    private static LootDecision Withhold(LootArm arm, LootReason reason, in LootBrainInputs inputs)
        => new(arm, reason, LootVerb.Hold, inputs.CorpseObjId, inputs.Ownership,
            inputs.Worth, inputs.Space, LootDisposition.Undecided);

    /// <summary>Builds a verb-less TERMINAL skip (worth or space: the corpse is never reconsidered).</summary>
    private static LootDecision Skip(LootArm arm, LootReason reason, in LootBrainInputs inputs)
        => new(arm, reason, LootVerb.Hold, inputs.CorpseObjId, inputs.Ownership,
            inputs.Worth, inputs.Space, LootDisposition.Skipped);

    /// <summary>
    /// The decision's <c>validate=</c> token, which the funnel loot arm and every
    /// G7c/G7d harness scanner key on. The established vocabulary is preserved
    /// exactly (a corpse whose Loot already ran reads <c>already-looted</c>, an
    /// empty or out-of-range container reads <c>not-lootable</c>, an unreachable
    /// corpse reads <c>no-corpse</c>/<c>gone</c>/<c>recycled</c>, the live loop
    /// reads <c>loop-live</c>, and the emit reads <c>ok</c>), and the arms this
    /// increment ADDS name themselves: <c>foreign-claim</c>,
    /// <c>claim-unresolved</c>, <c>unsafe</c>, <c>worth-skip</c>,
    /// <c>space-skip</c>, <c>prior-skip</c> and <c>survival-veto</c>.
    ///
    /// Three container verdicts deliberately keep the frozen <c>not-lootable</c>
    /// token: an EMPTY container, one OUT OF RANGE, and one that did not expose a
    /// container at all. The frozen <c>ProbeCorpse</c> predicate collapsed exactly
    /// those three into one <c>Lootable</c> flag, so no scanner can regress; they
    /// remain distinct REASONS on the decision, which is what the lane reads.
    /// </summary>
    public static string ValidateToken(LootReason reason) => reason switch
    {
        LootReason.OwnershipSelf or LootReason.OwnershipParty or LootReason.Emitted => "ok",
        LootReason.ContainerEmpty or LootReason.ContainerOutOfRange => "not-lootable",
        LootReason.CorpseUnreadable => "no-corpse",
        LootReason.CorpseGone => "gone",
        LootReason.CorpseRecycled => "recycled",
        LootReason.LoopLive => "loop-live",
        LootReason.AlreadyLooted => "already-looted",
        LootReason.SafetyUnresolved or LootReason.Unsafe => "unsafe",
        LootReason.OwnershipForeign => "foreign-claim",
        LootReason.OwnershipUnresolved => "claim-unresolved",
        LootReason.PriorSkip => "prior-skip",
        LootReason.SurvivalVetoed => "survival-veto",
        // A corpse that resolved but exposed no container reads the same token
        // the frozen probe printed for it (its `Lootable` flag was false); the
        // reason stays distinct on the decision for the lane.
        LootReason.ContainerMissing => "not-lootable",
        LootReason.Junk => "worth-skip",
        LootReason.NoBagRoom => "space-skip",
        _ => "none"
    };

    /// <summary>
    /// Compact, space-free diagnostic token for the wake line. Carries no spaces
    /// or square brackets so the DECIDE bracket and the funnel line keep
    /// parsing. Built on read only.
    /// </summary>
    public static string Describe(in LootDecision decision)
        => $"arm={decision.Arm}:verb={decision.Verb}:validate={ValidateToken(decision.Reason)}" +
           $":ownership={decision.Ownership}:worth={decision.Worth}:space={decision.Space}" +
           $":disposition={decision.Disposition}" +
           (decision.Cause == LootReason.None ? "" : $":cause={ValidateToken(decision.Cause)}");
}
