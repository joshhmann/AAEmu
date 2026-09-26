using System.Numerics;
using System.Reflection;

using AAEmu.Game.Core.Managers.Bots.Needs;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// NeedsBrain: the six-verdict needs-farm chain (SEEK-SOIL / TRAVEL / PLANT /
/// WAIT-MATURITY / HARVEST / REPLANT plus HOLD), the fail-closed unreadable-farm
/// state holds, the maturity wait → harvest transition, and the named-reason
/// vocabulary.
///
/// These tests drive the PURE decision surface (<see cref="NeedsBrain.Decide"/>)
/// from synthetic <see cref="NeedsBrainInputs"/> — no world, no actor, no engine.
/// What is pinned here is the contract a consumer observes: which verdict fires,
/// which named reason it reports, which verb (if any) it names, and whether a
/// destination is fabricated.
/// </summary>
[NotInParallel]
public class NeedsBrainTests
{
    private const uint ActorObjId = 7777;
    private const uint CropObjId = 91_501;
    private const uint CropTemplateId = 2259;
    private static readonly Vector3 Self = new(1000f, 1000f, 100f);
    private static readonly Vector3 Soil = new(1012f, 1000f, 100f);
    private static readonly Vector3 CropPos = new(1002f, 1000f, 100f);

    /// <summary>
    /// A wake with nothing to do: no seed read yet, no crop, no soil. Every row
    /// below starts here and flips exactly ONE fact.
    /// </summary>
    private static NeedsBrainInputs Blank() => new(
        ActorObjId: ActorObjId,
        SelfPosition: Self,
        SoilReadable: true,
        SeedReadable: true,
        SeedCount: 0,
        OnValidSoil: false,
        CropState: NeedsCropState.None,
        CropMature: false,
        CropDistanceM: float.NaN,
        CropPosition: Vector3.Zero,
        CropEnRoute: false,
        CropObjId: 0,
        CropTemplateId: 0,
        CropApproachAttempts: 0,
        MaxCropApproachAttempts: NeedsBrain.DefaultMaxCropApproachAttempts,
        HarvestJustLanded: false,
        SoilEnRoute: false,
        SoilResolved: false,
        SoilDestination: Vector3.Zero,
        SoilAttempts: 0,
        MaxSoilAttempts: NeedsBrain.DefaultMaxSoilAttempts,
        HarvestRangeM: NeedsBrain.DefaultHarvestRangeM);

    /// <summary>A live tracked crop at <paramref name="distanceM"/> with the given maturity.</summary>
    private static NeedsBrainInputs WithCrop(float distanceM, bool mature) => Blank() with
    {
        CropState = NeedsCropState.Live,
        CropMature = mature,
        CropDistanceM = distanceM,
        CropPosition = CropPos,
        CropObjId = CropObjId,
        CropTemplateId = CropTemplateId
    };

    // ------------------------------------------------------------ SEEK-SOIL

    [Test]
    public async Task SeedOnHandOffSoil_SeeksSoilWithNoVerbAndNoDestination()
    {
        // Seed on hand, standing off soil, nothing resolved yet: the wake's demand
        // is the RESOLVE. It deliberately names no verb and carries no destination —
        // the caller owns the bounded spiral, and a verdict that invented a point
        // here would walk the bot somewhere nobody proved plantable.
        var decision = NeedsBrain.Decide(Blank() with { SeedCount = 1 });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.SeekSoil);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.NoSoilNearby);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.HasDestination).IsFalse();
        await Assert.That(decision.IsSeekSoil).IsTrue();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("no-soil-nearby");
    }

    [Test]
    public async Task SoilResolved_WalksTheResolvedDestination()
    {
        // The destination reaches the chain ONLY through SoilResolved (produced by
        // the bounded spiral), and the verdict asks for the existing MoveTo verb to
        // exactly that point.
        var decision = NeedsBrain.Decide(Blank() with
        {
            SeedCount = 1,
            SoilResolved = true,
            SoilDestination = Soil
        });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Travel);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SoilResolved);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.MoveTo);
        await Assert.That(decision.HasVerb).IsTrue();
        await Assert.That(decision.Destination).IsEqualTo(Soil);
        await Assert.That(decision.IsTravel).IsTrue();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("soil-resolved");
    }

    [Test]
    public async Task SoilResolvedAndAlreadyEnRoute_HoldsTheLegWithoutAReArm()
    {
        // A leg already walking the resolved point is progress: the verdict is
        // still Travel (the wake belongs to the journey) but it carries NO verb, so
        // the caller cannot restart the leg every wake.
        var decision = NeedsBrain.Decide(Blank() with
        {
            SeedCount = 1,
            SoilResolved = true,
            SoilDestination = Soil,
            SoilEnRoute = true
        });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Travel);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SoilEnRoute);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.Destination).IsEqualTo(Soil);
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("soil-en-route");
    }

    [Test]
    public async Task SoilResolveBudgetSpent_HoldsRatherThanSpinning()
    {
        var decision = NeedsBrain.Decide(Blank() with
        {
            SeedCount = 1,
            SoilAttempts = NeedsBrain.DefaultMaxSoilAttempts + 1
        });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SoilResolveBudgetSpent);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.HasDestination).IsFalse();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("soil-resolve-budget-spent");
    }

    // ------------------------------------------------------------ PLANT

    [Test]
    public async Task SeedOnValidSoil_Plants()
    {
        var decision = NeedsBrain.Decide(Blank() with { SeedCount = 1, OnValidSoil = true });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Plant);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SeedOnSoil);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.Plant);
        await Assert.That(decision.HasVerb).IsTrue();
        await Assert.That(decision.HasDestination).IsFalse();
        await Assert.That(NeedsBrain.Token(decision.Verb)).IsEqualTo("plant");
    }

    [Test]
    public async Task SoilReadableButNoSeedOnHand_HoldsForTheBuyLeg()
    {
        // The seed count is read BEFORE the soil surface: a bot with no seed has no
        // soil question, so the wake must name the seed fact (the existing buy leg
        // owns it), never a soil failure that would misname the cause.
        var decision = NeedsBrain.Decide(Blank() with { SeedCount = 0, OnValidSoil = false });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SeedAbsent);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("seed-absent");
    }

    // ------------------------------------------------------------ WAIT MATURITY

    [Test]
    public async Task LiveImmatureCrop_WaitsAtThePlotAndIssuesNoHarvest()
    {
        var decision = NeedsBrain.Decide(WithCrop(distanceM: 1.0f, mature: false));

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.WaitMaturity);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.CropImmature);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.IsWaitMaturity).IsTrue();
        await Assert.That(decision.CropObjId).IsEqualTo(CropObjId);
        await Assert.That(decision.CropTemplateId).IsEqualTo(CropTemplateId);
        await Assert.That(decision.DistanceM).IsEqualTo(1.0f);
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("crop-immature");
        await Assert.That(NeedsBrain.Token(decision.Verdict)).IsEqualTo("wait-maturity");
    }

    // ------------------------------------------------------------ MATURITY → HARVEST

    [Test]
    public async Task MatureCropInRange_HarvestsThatCrop()
    {
        // The maturity wait → harvest transition: the SAME live track, now mature
        // and inside the interaction range, flips the verdict from WaitMaturity to
        // Harvest and names the existing Harvest verb on the tracked objId.
        var immature = NeedsBrain.Decide(WithCrop(distanceM: 1.0f, mature: false));
        var mature = NeedsBrain.Decide(WithCrop(distanceM: 1.0f, mature: true));

        await Assert.That(immature.Verdict).IsEqualTo(NeedsVerdict.WaitMaturity);
        await Assert.That(mature.Verdict).IsEqualTo(NeedsVerdict.Harvest);
        await Assert.That(mature.Reason).IsEqualTo(NeedsReason.CropMature);
        await Assert.That(mature.Verb).IsEqualTo(NeedsVerb.Harvest);
        await Assert.That(mature.CropObjId).IsEqualTo(CropObjId);
        await Assert.That(mature.IsHarvest).IsTrue();
        await Assert.That(NeedsBrain.Token(mature.Reason)).IsEqualTo("crop-mature");
        await Assert.That(NeedsBrain.Token(mature.Verb)).IsEqualTo("harvest");
    }

    [Test]
    public async Task MatureCropAtTheHarvestRangeBoundary_IsInRange()
    {
        // Harvest fires AT the interaction range (the live leg's own boundary), so
        // the two agree instead of fighting over the metre.
        var decision = NeedsBrain.Decide(WithCrop(NeedsBrain.DefaultHarvestRangeM, mature: true));

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Harvest);
    }

    [Test]
    public async Task MatureCropOutOfRange_ApproachesInsteadOfHarvesting()
    {
        var decision = NeedsBrain.Decide(WithCrop(distanceM: 40f, mature: true));

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Travel);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.CropApproach);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.MoveTo);
        await Assert.That(decision.Destination).IsEqualTo(CropPos);
        await Assert.That(decision.CropObjId).IsEqualTo(CropObjId);
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("crop-approach");
    }

    [Test]
    public async Task MatureCropOutOfRangeAlreadyEnRoute_HoldsTheLeg()
    {
        var decision = NeedsBrain.Decide(WithCrop(distanceM: 40f, mature: true) with { CropEnRoute = true });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Travel);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.CropApproachEnRoute);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.Destination).IsEqualTo(CropPos);
    }

    [Test]
    public async Task MatureCropUnreachable_AfterTheApproachBudget_HoldsNoHarvest()
    {
        var decision = NeedsBrain.Decide(WithCrop(distanceM: 300f, mature: true) with
        {
            CropApproachAttempts = NeedsBrain.DefaultMaxCropApproachAttempts
        });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.CropUnreachable);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.CropObjId).IsEqualTo(CropObjId);
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("crop-unreachable");
    }

    // ------------------------------------------------------------ REPLANT

    [Test]
    public async Task LandedHarvest_NamesTheReEvaluationNotAScriptedReplant()
    {
        // A harvest that landed this wake leaves the wake at the RE-EVALUATION: no
        // verb, no destination — never a harvest-then-plant chain inside one wake.
        var decision = NeedsBrain.Decide(Blank() with { HarvestJustLanded = true, CropObjId = CropObjId });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Replant);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.HarvestCompleted);
        await Assert.That(decision.Verb).IsEqualTo(NeedsVerb.Hold);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.HasDestination).IsFalse();
        await Assert.That(decision.IsReplant).IsTrue();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("harvest-completed");
    }

    [Test]
    public async Task LandedHarvest_BeatsEveryOtherArm()
    {
        // A landed harvest is the terminal fact of the wake: even with a mature crop
        // in range and seed on soil, the wake is the re-evaluation.
        var decision = NeedsBrain.Decide(WithCrop(distanceM: 1.0f, mature: true) with { HarvestJustLanded = true });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Replant);
    }

    // ------------------------------------------------------------ FAIL-CLOSED

    [Test]
    public async Task UnreadableSoilWithSeed_HoldsWithANamedReasonAndNoDestination()
    {
        // THE GATE: seed on hand but the plantable surface could not be read. NO
        // destination is fabricated (a fabricated one would walk the bot somewhere
        // the spiral never proved plantable) and no Plant is issued on an unread
        // surface.
        var decision = NeedsBrain.Decide(Blank() with { SeedCount = 1, SoilReadable = false });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SoilUnreadable);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.HasDestination).IsFalse();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("soil-unreadable");
    }

    [Test]
    public async Task UnreadableSoil_IsNamedRatherThanAFabricatedNoSoil()
    {
        // The distinction that matters: an unreadable surface is NOT "no farm
        // nearby" — a consumer reading the lane can tell the two apart.
        var unreadable = NeedsBrain.Decide(Blank() with { SeedCount = 1, SoilReadable = false });
        var resolved = NeedsBrain.Decide(Blank() with { SeedCount = 1, SoilReadable = true });

        await Assert.That(unreadable.Reason).IsEqualTo(NeedsReason.SoilUnreadable);
        await Assert.That(resolved.Reason).IsEqualTo(NeedsReason.NoSoilNearby);
        await Assert.That(unreadable.Reason).IsNotEqualTo(resolved.Reason);
    }

    [Test]
    public async Task UnreadableSeedCount_HoldsWithItsOwnNamedReason()
    {
        var decision = NeedsBrain.Decide(Blank() with { SeedReadable = false });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SeedUnreadable);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("seed-unreadable");
    }

    [Test]
    public async Task UnreadableCropRead_HoldsAndNeverFabricatesAGoneCrop()
    {
        // The crop's live read failed: a NAMED hold that keeps the track (no drop,
        // no seed arm) — a fabricated "gone" would abandon a real crop.
        var decision = NeedsBrain.Decide(Blank() with
        {
            CropState = NeedsCropState.Unreadable,
            CropObjId = CropObjId,
            CropTemplateId = CropTemplateId
        });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.CropUnreadable);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(decision.CropObjId).IsEqualTo(CropObjId);
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("crop-unreadable");
    }

    [Test]
    public async Task UnreadableCropDistance_NeverAuthorisesAHarvest()
    {
        // A live mature crop whose distance could not be measured: an unmeasured
        // distance is NOT zero — harvesting on a fabricated 0 would harvest a crop
        // across the zone.
        var decision = NeedsBrain.Decide(WithCrop(distanceM: float.NaN, mature: true));

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Hold);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.CropDistanceUnreadable);
        await Assert.That(decision.HasVerb).IsFalse();
        await Assert.That(float.IsNaN(decision.DistanceM)).IsTrue();
        await Assert.That(NeedsBrain.Token(decision.Reason)).IsEqualTo("crop-distance-unreadable");
    }

    [Test]
    public async Task AStaleCropTrack_FallsThroughToTheSeedArms()
    {
        // A provable stale track is NOT a verdict of its own: the caller drops it and
        // the wake re-evaluates (seed + soil), never a wait on a crop that is gone.
        var decision = NeedsBrain.Decide(Blank() with
        {
            CropState = NeedsCropState.Stale,
            CropObjId = 0,
            SeedCount = 1,
            SoilResolved = true,
            SoilDestination = Soil
        });

        await Assert.That(decision.Verdict).IsEqualTo(NeedsVerdict.Travel);
        await Assert.That(decision.Reason).IsEqualTo(NeedsReason.SoilResolved);
    }

    // ------------------------------------------------------------ vocabulary

    [Test]
    public async Task VerdictAndReasonTokens_AreSpaceFreeAndNamedForEveryMember()
    {
        // Every member names its own cause: no bare "failed", no bare token.
        foreach (var verdict in Enum.GetValues<NeedsVerdict>())
        {
            var token = NeedsBrain.Token(verdict);
            await Assert.That(token).IsNotEmpty();
            await Assert.That(token.Contains(' ')).IsFalse();
        }
        foreach (var reason in Enum.GetValues<NeedsReason>())
        {
            if (reason == NeedsReason.None)
                continue; // the sentinel, not a cause (mirrors SurvivalReason.None / LootReason.None)
            var token = NeedsBrain.Token(reason);
            await Assert.That(token).IsNotEmpty();
            await Assert.That(token.Contains(' ')).IsFalse();
            await Assert.That(token).IsNotEqualTo("none")
                .Because($"reason {reason} must name its own cause, not fall through");
        }
    }

    [Test]
    public async Task Decide_IsPureOverItsInputs()
    {
        // Same inputs, same wake — the decision is a function of the inputs alone.
        var inputs = WithCrop(distanceM: 42f, mature: true);

        var first = NeedsBrain.Decide(inputs);
        var second = NeedsBrain.Decide(inputs);

        await Assert.That(second).IsEqualTo(first);
    }

    // ------------------------------------------------------------ purity

    /// <summary>
    /// NO WORLD ACCESS: <see cref="NeedsBrain"/> is the pure decision surface, and
    /// its IL must not reference a single engine type — no world, no manager
    /// singleton, no Character/Doodad/Item. Everything a wake needs arrives on
    /// <see cref="NeedsBrainInputs"/> (which itself carries only primitives and a
    /// <see cref="Vector3"/>).
    ///
    /// <see cref="NeedsBrainPlanner"/>, <see cref="NeedsSoil"/> and
    /// <see cref="NeedsCrop"/> are deliberately NOT covered — they are the live
    /// surface and their live reads are their whole job.
    /// </summary>
    [Test]
    public async Task Decide_ReferencesNoEngineType()
    {
        var offenders = new List<string>();
        var scanned = 0;
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var method in typeof(NeedsBrain).GetMethods(flags))
        {
            if (method.GetMethodBody() == null)
                continue;
            scanned++;
            foreach (var referenced in IlScan.ReferencedTypes(method))
            {
                if (referenced == null)
                    continue;
                if (IsEngineType(referenced))
                    offenders.Add($"{method.Name} → {referenced.FullName}");
            }
        }

        await Assert.That(scanned).IsGreaterThan(4)
            .Because("the IL scan must actually walk the needs decision surface");
        await Assert.That(offenders).IsEmpty()
            .Because("the needs decision surface is pure: every live read belongs to NeedsBrainPlanner — offenders: "
                     + string.Join("; ", offenders.Take(20)));
    }

    private static bool IsEngineType(Type type)
    {
        var name = type.FullName ?? "";
        if (name.StartsWith("AAEmu.Game.Models.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Core.Managers.", StringComparison.Ordinal)
            && !name.StartsWith("AAEmu.Game.Core.Managers.Bots.Needs.Needs", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.GameData.", StringComparison.Ordinal))
            return true;
        if (name.StartsWith("AAEmu.Game.Utils.", StringComparison.Ordinal))
            return true;
        return false;
    }
}
