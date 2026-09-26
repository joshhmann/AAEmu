using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Needs;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The needs brain's first live dispatch caller seam
/// (<see cref="NeedsFarmLegDispatch"/>): the ONE place the needs decision and the
/// needs-farm leg's own routing meet.
///
/// What is pinned here is the mapping's fail-closed direction, over hand-built
/// decisions — no world, no actor, no engine read: ONLY the soil-travel arm (the
/// existing MoveTo verb to a point the bounded spiral produced) routes; every other
/// verdict — a crop arm this leg has no crop to serve, a named unreadable hold, the
/// bounded-spiral demand, the brain's absence — falls back to the leg's pre-brain
/// rule, and a fallback NEVER carries a destination (so no consumer can walk a point
/// the brain never asked for).
///
/// The live end-to-end routing (the leg actually arming a route to the spiral's
/// destination, and falling back on an unreadable surface) is covered against the
/// real executor in <c>NeedsFarmModuleTests</c>.
/// </summary>
[NotInParallel]
public class NeedsFarmLegDispatchTests
{
    private static readonly Vector3 Destination = new(1010f, 1000f, 100f);

    private static NeedsBrainPlanner.Prepared Prepared(in NeedsBrainDecision decision, Vector3? destination)
        => new(
            Inputs: default,
            Decision: decision,
            SoilDestination: destination,
            SoilProbesReadable: destination.HasValue ? 1 : 0,
            Crop: null);

    [Test]
    public async Task SoilTravelVerdict_RoutesAndCarriesTheSpiralsDestination()
    {
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Travel, NeedsReason.SoilResolved, NeedsVerb.MoveTo, 0, 0, Destination, float.NaN),
            Destination));

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsTravelVerb.WalkSoil);
        await Assert.That(verdict.HasLeg).IsTrue();
        await Assert.That(verdict.Destination).IsEqualTo(Destination);
        await Assert.That(verdict.Routed).IsTrue();
        await Assert.That(verdict.DispatchToken).IsEqualTo("walk");
        await Assert.That(verdict.Reason).IsEqualTo("soil-resolved");
    }

    [Test]
    public async Task CropApproachVerdict_FallsBack_ThisLegHasNoCropToServe()
    {
        // The SAME MoveTo verb carries the crop approach — and this leg is the soil
        // leg: routing it would dispatch a travel leg toward a crop the wake never
        // claimed. The fallback carries no destination for exactly that reason.
        var destination = new Vector3(2000f, 2000f, 100f);
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Travel, NeedsReason.CropApproach, NeedsVerb.MoveTo, 0x1234, 2259, destination, 30f),
            destination));

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsTravelVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Destination).IsNull();
        await Assert.That(verdict.Routed).IsFalse();
        await Assert.That(verdict.Reason).IsEqualTo("crop-approach");
    }

    [Test]
    public async Task NamedUnreadableHold_FallsBackAndNeverFabricatesADestination()
    {
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Hold, NeedsReason.SoilUnreadable, NeedsVerb.Hold, 0, 0, null, float.NaN),
            null));

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsTravelVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Destination).IsNull();
        await Assert.That(verdict.Reason).IsEqualTo("soil-unreadable");
    }

    [Test]
    public async Task SeedUnreadableHold_FallsBack()
    {
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Hold, NeedsReason.SeedUnreadable, NeedsVerb.Hold, 0, 0, null, float.NaN),
            null));

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsTravelVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Reason).IsEqualTo("seed-unreadable");
    }

    [Test]
    public async Task SeekSoilDemand_FallsBackWithTheSearchAlreadyCovered()
    {
        // The demand means the planner walked THIS leg's own spiral with THIS leg's
        // probe and found no plantable candidate — so the leg must not run that same
        // search again for the same answer.
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.SeekSoil, NeedsReason.NoSoilNearby, NeedsVerb.Hold, 0, 0, null, float.NaN),
            null));

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsTravelVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.SearchCovered).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo("no-soil-nearby");
    }

    [Test]
    public async Task UnreadableSurfaceHold_LeavesTheSearchToTheLeg()
    {
        // The named unreadable arm is the point read failing, which says nothing
        // about the spiral's candidates — so this leg's own rule still owns them.
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Hold, NeedsReason.SoilUnreadable, NeedsVerb.Hold, 0, 0, null, float.NaN),
            null));

        await Assert.That(verdict.SearchCovered).IsFalse();
    }

    [Test]
    public async Task MoveToVerbWithoutADestination_FallsBack()
    {
        // A consumer must never read a point the brain did not name: the same
        // travel verdict with no destination is a fallback, not a walk to nowhere.
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Travel, NeedsReason.SoilResolved, NeedsVerb.MoveTo, 0, 0, null, float.NaN),
            null));

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsTravelVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Destination).IsNull();
    }

    [Test]
    public async Task NoDecisionAtAll_FallsBackNamingTheAbsence()
    {
        var verdict = NeedsFarmLegDispatch.DecideNeedsTravelLeg(null);

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsTravelVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Decision).IsNull();
        await Assert.That(verdict.Reason).IsEqualTo("brain-absent");
        await Assert.That(verdict.DispatchToken).IsEqualTo("fallback");
    }

    [Test]
    public async Task WakeDescription_NamesTheDecisionRoutingAndSearchState()
    {
        // The observability a lane line carries: the brain's own space-free
        // description plus whether this leg routed and whether the search was
        // already covered — built on read, nothing allocated per decision.
        var routed = NeedsFarmLegDispatch.Describe(NeedsFarmLegDispatch.DecideNeedsTravelLeg(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Travel, NeedsReason.SoilResolved, NeedsVerb.MoveTo, 0, 0, Destination, float.NaN),
            Destination)));
        await Assert.That(routed).Contains("verdict=travel:reason=soil-resolved:verb=move-to");
        await Assert.That(routed).Contains("routed=true:dispatch=walk:search-covered=true");

        var absent = NeedsFarmLegDispatch.Describe(NeedsFarmLegDispatch.DecideNeedsTravelLeg(null));
        await Assert.That(absent).Contains("verdict=none:reason=brain-absent:routed=false:dispatch=fallback");
    }
}
