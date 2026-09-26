using System.Reflection;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Loot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// LootBrain: the decision chain, its fail-closed directions, the ownership
/// classifier, and the two structural properties the increment claims — PURITY
/// (identical inputs decide identically) and NO WORLD ACCESS (the brain's IL
/// references nothing outside its own namespace and the BCL).
///
/// These tests drive the PURE surface (<see cref="LootBrain.Decide"/>,
/// <see cref="LootBrain.ClassifyOwnership"/>) from synthetic
/// <see cref="LootBrainInputs"/> — no world, no actor, no engine. The live
/// adapter's own reads are exercised by <c>LootBrainPlannerTests</c> against the
/// real headless actor; what is pinned here is the contract a consumer observes:
/// which arm fires, which verb it asks for, and which named reason it reports.
/// </summary>
[NotInParallel]
public class LootBrainTests
{
    private const uint CorpseObjId = 91_501;
    private static readonly DateTime Wake = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A wake that reaches the emit arm: our own pinned corpse, readable in-range
    /// non-empty container, no live loop, not yet looted, resolved-and-safe
    /// census, worthwhile contents and bag room. Every row below starts here and
    /// flips exactly ONE verdict.
    /// </summary>
    private static LootBrainInputs EmitReady() => new(
        ActorObjId: 4242,
        CorpseObjId: CorpseObjId,
        SurvivalVetoed: false,
        CorpseState: LootCorpseState.Dead,
        Ownership: LootOwnership.Self,
        ContainerReadable: true,
        ContainerItemCount: 3,
        ContainerInRange: true,
        LoopLive: false,
        AlreadyLooted: false,
        SafetyResolved: true,
        Safe: true,
        NearestHostileDistanceM: float.NaN,
        Worth: LootWorth.Worthwhile,
        Space: LootSpace.Room,
        PriorDisposition: LootDisposition.Undecided,
        PriorReason: LootReason.None,
        NowUtc: Wake);

    // ------------------------------------------------------------ the chain

    [Test]
    public async Task EmitReady_EmitsTheLootVerb()
    {
        var decision = LootBrain.Decide(EmitReady());

        await Assert.That(decision.Arm).IsEqualTo(LootArm.Emit);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Loot);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.OwnershipSelf);
        await Assert.That(decision.CorpseObjId).IsEqualTo(CorpseObjId);
        await Assert.That(decision.Disposition).IsEqualTo(LootDisposition.Take);
        await Assert.That(decision.IsEmit).IsTrue();
        await Assert.That(decision.HasVerb).IsTrue();
        await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo("ok");
    }

    [Test]
    public async Task PartyOwnership_EmitsAndNamesThePartyReason()
    {
        var decision = LootBrain.Decide(EmitReady() with { Ownership = LootOwnership.Party });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.Emit);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Loot);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.OwnershipParty);
    }

    [Test]
    public async Task SurvivalVeto_WithholdsUndecided()
    {
        var decision = LootBrain.Decide(EmitReady() with { SurvivalVetoed = true });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.SurvivalVeto);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.SurvivalVetoed);
        await Assert.That(decision.Disposition).IsEqualTo(LootDisposition.Undecided);
        await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo("survival-veto");
    }

    [Test]
    public async Task OwnershipArm_WithholdsForForeignAndUnresolvedClaims()
    {
        var foreign = LootBrain.Decide(EmitReady() with { Ownership = LootOwnership.Foreign });
        await Assert.That(foreign.Arm).IsEqualTo(LootArm.Ownership);
        await Assert.That(foreign.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(foreign.Reason).IsEqualTo(LootReason.OwnershipForeign);
        await Assert.That(foreign.Disposition).IsEqualTo(LootDisposition.Undecided);
        await Assert.That(LootBrain.ValidateToken(foreign.Reason)).IsEqualTo("foreign-claim");

        var unresolved = LootBrain.Decide(EmitReady() with { Ownership = LootOwnership.Unresolved });
        await Assert.That(unresolved.Arm).IsEqualTo(LootArm.Ownership);
        await Assert.That(unresolved.Reason).IsEqualTo(LootReason.OwnershipUnresolved);
        await Assert.That(LootBrain.ValidateToken(unresolved.Reason)).IsEqualTo("claim-unresolved");
    }

    [Test]
    public async Task ProbeArm_NamesEveryUnreadableCorpseShape()
    {
        var rows = new (LootCorpseState State, LootReason Reason, string Token)[]
        {
            (LootCorpseState.Unreadable, LootReason.CorpseUnreadable, "no-corpse"),
            (LootCorpseState.Gone, LootReason.CorpseGone, "gone"),
            (LootCorpseState.Recycled, LootReason.CorpseRecycled, "recycled")
        };

        foreach (var (state, reason, token) in rows)
        {
            var decision = LootBrain.Decide(EmitReady() with { CorpseState = state });
            await Assert.That(decision.Arm).IsEqualTo(LootArm.Probe);
            await Assert.That(decision.Verb).IsEqualTo(LootVerb.Hold);
            await Assert.That(decision.Reason).IsEqualTo(reason);
            await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo(token);
        }
    }

    [Test]
    public async Task ProbeArm_NamesContainerFailureShapes()
    {
        var unreadable = LootBrain.Decide(EmitReady() with { ContainerReadable = false, ContainerItemCount = 0 });
        await Assert.That(unreadable.Arm).IsEqualTo(LootArm.Probe);
        await Assert.That(unreadable.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(unreadable.Reason).IsEqualTo(LootReason.ContainerMissing);

        var empty = LootBrain.Decide(EmitReady() with { ContainerItemCount = 0 });
        await Assert.That(empty.Arm).IsEqualTo(LootArm.Probe);
        await Assert.That(empty.Reason).IsEqualTo(LootReason.ContainerEmpty);

        var far = LootBrain.Decide(EmitReady() with { ContainerInRange = false });
        await Assert.That(far.Arm).IsEqualTo(LootArm.Probe);
        await Assert.That(far.Reason).IsEqualTo(LootReason.ContainerOutOfRange);
    }

    [Test]
    public async Task LoopRelease_WithholdsWhileTheAttackLoopIsLive()
    {
        var decision = LootBrain.Decide(EmitReady() with { LoopLive = true });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.LoopRelease);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.LoopLive);
        // Transient: the loop tears down by construction, so nothing is banked.
        await Assert.That(decision.Disposition).IsEqualTo(LootDisposition.Undecided);
        await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo("loop-live");
    }

    [Test]
    public async Task LootOnce_WithholdsForTakenAndBankedSkipCorpses()
    {
        var taken = LootBrain.Decide(EmitReady() with { AlreadyLooted = true });
        await Assert.That(taken.Arm).IsEqualTo(LootArm.LootOnce);
        await Assert.That(taken.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(taken.Reason).IsEqualTo(LootReason.AlreadyLooted);
        await Assert.That(LootBrain.ValidateToken(taken.Reason)).IsEqualTo("already-looted");

        // The ledger's own take reads exactly like the leg's loot-once memory.
        var bankedTake = LootBrain.Decide(EmitReady() with { PriorDisposition = LootDisposition.Take });
        await Assert.That(bankedTake.Arm).IsEqualTo(LootArm.LootOnce);
        await Assert.That(bankedTake.Reason).IsEqualTo(LootReason.AlreadyLooted);
    }

    [Test]
    public async Task LootOnce_BankedSkipReportsItsOriginalCause()
    {
        var decision = LootBrain.Decide(EmitReady() with
        {
            PriorDisposition = LootDisposition.Skipped,
            PriorReason = LootReason.NoBagRoom
        });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.LootOnce);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.PriorSkip);
        await Assert.That(decision.Cause).IsEqualTo(LootReason.NoBagRoom);
        await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo("prior-skip");
        await Assert.That(LootBrain.Describe(decision)).Contains("cause=space-skip");
    }

    // ------------------------------------------------------------ safety

    [Test]
    public async Task SafetyArm_FailsClosedOnAnUnreadableCensus()
    {
        // An unreadable census is NOT safe: the withhold is the honest verdict.
        var decision = LootBrain.Decide(EmitReady() with { SafetyResolved = false, Safe = false });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.Safety);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.SafetyUnresolved);
        await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo("unsafe");
        await Assert.That(decision.Disposition).IsEqualTo(LootDisposition.Undecided);
    }

    [Test]
    public async Task SafetyArm_WithholdsForAHostileInsideTheRadius()
    {
        var decision = LootBrain.Decide(EmitReady() with
        {
            Safe = false,
            NearestHostileDistanceM = 6.0f
        });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.Safety);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.Unsafe);
        await Assert.That(decision.Disposition).IsEqualTo(LootDisposition.Undecided);
    }

    // ------------------------------------------------------------ worth / space

    [Test]
    public async Task WorthArm_SkipsTerminallyForAJunkContainer()
    {
        var decision = LootBrain.Decide(EmitReady() with { Worth = LootWorth.Junk });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.Worth);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.Junk);
        await Assert.That(decision.Disposition).IsEqualTo(LootDisposition.Skipped);
        await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo("worth-skip");
        await Assert.That(LootBrain.IsTerminalSkip(decision.Reason)).IsTrue();
    }

    [Test]
    public async Task SpaceArm_SkipsTerminallyForAFullBag()
    {
        var decision = LootBrain.Decide(EmitReady() with { Space = LootSpace.Full });

        await Assert.That(decision.Arm).IsEqualTo(LootArm.Space);
        await Assert.That(decision.Verb).IsEqualTo(LootVerb.Hold);
        await Assert.That(decision.Reason).IsEqualTo(LootReason.NoBagRoom);
        await Assert.That(decision.Disposition).IsEqualTo(LootDisposition.Skipped);
        await Assert.That(LootBrain.ValidateToken(decision.Reason)).IsEqualTo("space-skip");
    }

    /// <summary>
    /// The two arms this increment adds may never fabricate a skip on an
    /// established corpse: an UNREADABLE worth or space proceeds, because the
    /// ownership and lootability of the corpse were already established.
    /// </summary>
    [Test]
    public async Task UnreadableWorthOrSpace_StillEmits()
    {
        var unreadableWorth = LootBrain.Decide(EmitReady() with { Worth = LootWorth.Unresolved });
        await Assert.That(unreadableWorth.Arm).IsEqualTo(LootArm.Emit);
        await Assert.That(unreadableWorth.Verb).IsEqualTo(LootVerb.Loot);

        var unreadableSpace = LootBrain.Decide(EmitReady() with { Space = LootSpace.Unresolved });
        await Assert.That(unreadableSpace.Arm).IsEqualTo(LootArm.Emit);
        await Assert.That(unreadableSpace.Verb).IsEqualTo(LootVerb.Loot);
    }

    /// <summary>
    /// Evaluation ORDER is part of the contract: a wake that satisfies several
    /// arms at once reports the FIRST one, so the funnel's named cause is stable.
    /// </summary>
    [Test]
    public async Task ChainOrder_IsFirstArmWins()
    {
        // A vetoed, foreign, unreadable-corpse wake reports the VETO.
        var vetoed = LootBrain.Decide(EmitReady() with
        {
            SurvivalVetoed = true,
            Ownership = LootOwnership.Foreign,
            CorpseState = LootCorpseState.Gone,
            LoopLive = true
        });
        await Assert.That(vetoed.Arm).IsEqualTo(LootArm.SurvivalVeto);

        // Foreign beats probe/loop/loot-once.
        var foreign = LootBrain.Decide(EmitReady() with
        {
            Ownership = LootOwnership.Foreign,
            CorpseState = LootCorpseState.Gone,
            LoopLive = true
        });
        await Assert.That(foreign.Arm).IsEqualTo(LootArm.Ownership);

        // Probe beats loop/loot-once.
        var probe = LootBrain.Decide(EmitReady() with
        {
            CorpseState = LootCorpseState.Gone,
            LoopLive = true,
            AlreadyLooted = true
        });
        await Assert.That(probe.Arm).IsEqualTo(LootArm.Probe);

        // Loop release beats loot-once and safety.
        var loop = LootBrain.Decide(EmitReady() with
        {
            LoopLive = true,
            AlreadyLooted = true,
            Safe = false
        });
        await Assert.That(loop.Arm).IsEqualTo(LootArm.LoopRelease);

        // Loot-once beats safety and worth.
        var once = LootBrain.Decide(EmitReady() with
        {
            AlreadyLooted = true,
            Safe = false,
            Worth = LootWorth.Junk,
            Space = LootSpace.Full
        });
        await Assert.That(once.Arm).IsEqualTo(LootArm.LootOnce);

        // Safety beats worth and space.
        var unsafeWake = LootBrain.Decide(EmitReady() with
        {
            Safe = false,
            Worth = LootWorth.Junk,
            Space = LootSpace.Full
        });
        await Assert.That(unsafeWake.Arm).IsEqualTo(LootArm.Safety);

        // Worth beats space.
        var junk = LootBrain.Decide(EmitReady() with { Worth = LootWorth.Junk, Space = LootSpace.Full });
        await Assert.That(junk.Arm).IsEqualTo(LootArm.Worth);
    }

    // ------------------------------------------------------------ ownership

    [Test]
    public async Task ClassifyOwnership_ReadsTheEnginesOwnClaimRules()
    {
        // A verified tag team the actor belongs to is OUR party's claim.
        var tagTeamOurs = Evidence() with
        {
            TagTeamId = 77,
            TagTeamResolved = true,
            ActorInTagTeam = true
        };
        await Assert.That(LootBrain.ClassifyOwnership(tagTeamOurs, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Party);

        // A tag team that could not be verified is NEVER upgraded to Self.
        var tagTeamUnverified = Evidence() with { TagTeamId = 77, TagTeamResolved = false };
        await Assert.That(LootBrain.ClassifyOwnership(tagTeamUnverified, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Unresolved);

        // A verified tag team we are not in is somebody else's.
        var tagTeamForeign = Evidence() with
        {
            TagTeamId = 77,
            TagTeamResolved = true,
            ActorInTagTeam = false
        };
        await Assert.That(LootBrain.ClassifyOwnership(tagTeamForeign, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Foreign);

        // Our own tagger is ours.
        var selfTagger = Evidence() with { TaggerObjId = 4242, ActorIsTagger = true, ActorObjId = 4242 };
        await Assert.That(LootBrain.ClassifyOwnership(selfTagger, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Self);

        // A tagger who is a verified party-mate is the party's claim.
        var mateTagger = Evidence() with { TaggerObjId = 9001, TaggerSharesActorTeam = true };
        await Assert.That(LootBrain.ClassifyOwnership(mateTagger, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Party);

        // Any other tagger is a foreign claim.
        var foreignTagger = Evidence() with { TaggerObjId = 9001 };
        await Assert.That(LootBrain.ClassifyOwnership(foreignTagger, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Foreign);

        // No tagger: the readable contributor census decides.
        var soloContributor = Evidence() with
        {
            ContributorsReadable = true,
            ContributorCount = 1,
            ActorIsContributor = true,
            ForeignContributorCount = 0
        };
        await Assert.That(LootBrain.ClassifyOwnership(soloContributor, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Self);

        var rivalContributor = soloContributor with { ForeignContributorCount = 1 };
        await Assert.That(LootBrain.ClassifyOwnership(rivalContributor, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Foreign);

        var notAContributor = soloContributor with { ActorIsContributor = false };
        await Assert.That(LootBrain.ClassifyOwnership(notAContributor, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Foreign);

        var unreadableCensus = Evidence() with { ContributorsReadable = false };
        // An unreadable census is "no claim found", not a claim that failed — so
        // it follows the same provenance rule as an empty one (Self on our own
        // pinned corpse, Unresolved anywhere else). What is forbidden is
        // upgrading a claim that EXISTS but could not be VERIFIED.
        await Assert.That(LootBrain.ClassifyOwnership(unreadableCensus, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Self);
        await Assert.That(LootBrain.ClassifyOwnership(unreadableCensus, corpseIsOurPinnedCorpse: false))
            .IsEqualTo(LootOwnership.Unresolved);
    }

    /// <summary>
    /// No claim at all on OUR OWN pinned corpse is the absence of a rival — the
    /// quest-owned record's own by-construction claim. On any OTHER corpse the
    /// same absence is simply unresolved, which withholds.
    /// </summary>
    [Test]
    public async Task ClassifyOwnership_NoClaimIsSelfOnlyOnOurOwnPinnedCorpse()
    {
        var noClaim = Evidence();

        await Assert.That(LootBrain.ClassifyOwnership(noClaim, corpseIsOurPinnedCorpse: true))
            .IsEqualTo(LootOwnership.Self);
        await Assert.That(LootBrain.ClassifyOwnership(noClaim, corpseIsOurPinnedCorpse: false))
            .IsEqualTo(LootOwnership.Unresolved);
    }

    [Test]
    public async Task MayLoot_AllowsOnlySelfAndParty()
    {
        await Assert.That(LootBrain.MayLoot(LootOwnership.Self)).IsTrue();
        await Assert.That(LootBrain.MayLoot(LootOwnership.Party)).IsTrue();
        await Assert.That(LootBrain.MayLoot(LootOwnership.Foreign)).IsFalse();
        await Assert.That(LootBrain.MayLoot(LootOwnership.Unresolved)).IsFalse();
    }

    private static LootClaimEvidence Evidence() => new(
        ActorObjId: 4242,
        TaggerObjId: 0,
        ActorIsTagger: false,
        TaggerSharesActorTeam: false,
        TagTeamId: 0,
        TagTeamResolved: false,
        ActorInTagTeam: false,
        ContributorsReadable: false,
        ContributorCount: 0,
        ForeignContributorCount: 0,
        ActorIsContributor: false);

    // ------------------------------------------------------------ purity

    /// <summary>
    /// The decision is a pure function of its inputs: the same inputs decide the
    /// same wake, every time, and two independently built input sets with equal
    /// values produce equal decisions — including the ledger facts, which arrive
    /// as inputs rather than being read here.
    /// </summary>
    [Test]
    public async Task Decide_IdenticalInputs_ProduceIdenticalDecisions()
    {
        foreach (var inputs in new[]
                 {
                     EmitReady(),
                     EmitReady() with { SurvivalVetoed = true },
                     EmitReady() with { Ownership = LootOwnership.Foreign },
                     EmitReady() with { CorpseState = LootCorpseState.Gone },
                     EmitReady() with { LoopLive = true },
                     EmitReady() with { PriorDisposition = LootDisposition.Skipped, PriorReason = LootReason.Junk },
                     EmitReady() with { Safe = false },
                     EmitReady() with { Worth = LootWorth.Junk },
                     EmitReady() with { Space = LootSpace.Full }
                 })
        {
            var first = LootBrain.Decide(inputs);
            var second = LootBrain.Decide(inputs);
            await Assert.That(second).IsEqualTo(first);
            await Assert.That(LootBrain.Describe(second)).IsEqualTo(LootBrain.Describe(first));
        }

        // Structurally equal inputs (the same values), not the same instance.
        await Assert.That(LootBrain.Decide(EmitReady())).IsEqualTo(LootBrain.Decide(EmitReady()));
    }

    /// <summary>
    /// NO WORLD ACCESS: the pure decision type's IL must not reference a single
    /// engine type — no world, no manager singleton, no Character/Npc/Item, and
    /// (per <c>PerceptionStackSurfaceTests</c>) nothing from the perception stack.
    /// This is checked at the ASSEMBLY level rather than by inspection: every
    /// method body of <see cref="LootBrain"/> is decoded and each type token
    /// resolved, and any engine reference fails the test naming the offender.
    ///
    /// <see cref="LootBrainPlanner"/> is deliberately NOT covered — it is the live
    /// adapter and its live reads are its whole job (proved by the planner rigs
    /// against the real headless actor).
    /// </summary>
    [Test]
    public async Task Decide_ReferencesNoEngineType()
    {
        var offenders = new List<string>();
        var scanned = 0;
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var method in typeof(LootBrain).GetMethods(flags))
        {
            if (method.GetMethodBody() == null)
                continue;
            scanned++;
            foreach (var referenced in ReferencedTypes(method))
            {
                if (referenced == null)
                    continue;
                if (IsEngineType(referenced))
                    offenders.Add($"{typeof(LootBrain).FullName}.{method.Name} → {referenced.FullName}");
            }
        }

        await Assert.That(scanned).IsGreaterThan(4)
            .Because("the IL scan must actually walk LootBrain's methods");
        await Assert.That(offenders).IsEmpty()
            .Because("LootBrain is pure: every live read belongs to LootBrainPlanner — offenders: "
                     + string.Join("; ", offenders.Take(20)));
    }

    /// <summary>True when a type lives outside the pure decision surface (engine models, managers, or the perception/belief stack).</summary>
    private static bool IsEngineType(Type type)
    {
        var name = type.FullName ?? "";
        if (name.StartsWith("AAEmu.Game.Models.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Core.Managers.", StringComparison.Ordinal)
            && !name.StartsWith("AAEmu.Game.Core.Managers.Bots.Loot.Loot", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.GameData.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Utils.", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static IEnumerable<Type?> ReferencedTypes(MethodBase method)
        => IlScan.ReferencedTypes(method);
}
