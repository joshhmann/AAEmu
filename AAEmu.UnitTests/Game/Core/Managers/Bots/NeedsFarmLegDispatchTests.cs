using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Needs;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// The needs brain's live dispatch caller seam
/// (<see cref="NeedsFarmLegDispatch"/>): the ONE place the needs decision and the
/// needs-farm leg's own routing meet — in two tables, one per arm kind.
///
/// What is pinned here is each mapping's fail-closed direction, over hand-built
/// decisions — no world, no actor, no engine read:
///  - the TRAVEL table routes ONLY the soil-travel arm (the existing MoveTo verb to a
///    point the bounded spiral produced); every other verdict — a crop arm this leg
///    has no crop to serve, a named unreadable hold, the bounded-spiral demand, the
///    brain's absence — falls back to the leg's pre-brain rule, and a fallback NEVER
///    carries a destination (so no consumer can walk a point the brain never asked
///    for);
///  - the ACTION table routes ONLY the three rows the leg's own dispatch sites can
///    serve — Harvest on a named crop, Plant on the soil underfoot, and the no-verb
///    maturity wait — scoped by the arm so a mature-crop wake can never reach the
///    planter (the starvation guard); a Harvest with no crop, a named hold, the
///    crop-approach travel and the re-evaluation all fall back.
///
/// The live end-to-end routing (the leg actually arming a route to the spiral's
/// destination, planting through the brain-decided arm, and holding on the named
/// wait) is covered against the real executor in <c>NeedsFarmModuleTests</c>.
/// </summary>
[NotInParallel]
public class NeedsFarmLegDispatchTests
{
    private const uint CropObjId = 91_601;
    private const uint CropTemplateId = 2259;
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

    // ------------------------------------------------------------------
    // THE ACTION ARM TABLE (harvest / plant / the named maturity wait)
    // ------------------------------------------------------------------

    [Test]
    public async Task HarvestVerdict_RoutesTheHarvestVerbOnTheTrackedCrop()
    {
        var verdict = NeedsFarmLegDispatch.DecideNeedsFarmAction(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Harvest, NeedsReason.CropMature, NeedsVerb.Harvest, CropObjId, CropTemplateId, null, 1.5f),
            null), NeedsFarmLegDispatch.NeedsFarmArm.Harvest);

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.Harvest);
        await Assert.That(verdict.HasLeg).IsTrue();
        await Assert.That(verdict.Routed).IsTrue();
        await Assert.That(verdict.CropObjId).IsEqualTo(CropObjId);
        await Assert.That(verdict.Reason).IsEqualTo("crop-mature");
        await Assert.That(verdict.DispatchToken).IsEqualTo("harvest");
    }

    [Test]
    public async Task PlantVerdict_RoutesThePlantVerbOnlyOnThePlantArm()
    {
        var prepared = Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Plant, NeedsReason.SeedOnSoil, NeedsVerb.Plant, 0, 0, null, float.NaN),
            null);

        var plant = NeedsFarmLegDispatch.DecideNeedsFarmAction(prepared, NeedsFarmLegDispatch.NeedsFarmArm.Plant);
        var harvest = NeedsFarmLegDispatch.DecideNeedsFarmAction(prepared, NeedsFarmLegDispatch.NeedsFarmArm.Harvest);

        await Assert.That(plant.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.Plant);
        await Assert.That(plant.HasLeg).IsTrue();
        await Assert.That(plant.Reason).IsEqualTo("seed-on-soil");
        await Assert.That(plant.DispatchToken).IsEqualTo("plant");
        // The arm scoping is the starvation guard: the harvest arm has no soil site,
        // so a Plant verdict can never route a plant into it.
        await Assert.That(harvest.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.Fallback);
        await Assert.That(harvest.HasLeg).IsFalse();
    }

    [Test]
    public async Task HarvestVerdict_NeverRoutesAPlantIntoThePlantArm()
    {
        // The mirror of the guard above: a mature-crop wake must never reach the
        // planter — that is the live plant-forever-and-starve-the-crop finding.
        var verdict = NeedsFarmLegDispatch.DecideNeedsFarmAction(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Harvest, NeedsReason.CropMature, NeedsVerb.Harvest, CropObjId, CropTemplateId, null, 1.5f),
            null), NeedsFarmLegDispatch.NeedsFarmArm.Plant);

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
    }

    [Test]
    public async Task WaitMaturityVerdict_HoldsOnEitherArmAndDispatchesNothing()
    {
        var prepared = Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.WaitMaturity, NeedsReason.CropImmature, NeedsVerb.Hold, CropObjId, CropTemplateId, null, 1.2f),
            null);

        foreach (var arm in new[]
                 {
                     NeedsFarmLegDispatch.NeedsFarmArm.Harvest,
                     NeedsFarmLegDispatch.NeedsFarmArm.Plant
                 })
        {
            var verdict = NeedsFarmLegDispatch.DecideNeedsFarmAction(prepared, arm);
            await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.WaitMaturity);
            await Assert.That(verdict.IsWait).IsTrue();
            await Assert.That(verdict.HasLeg).IsFalse();
            await Assert.That(verdict.Routed).IsTrue();
            await Assert.That(verdict.CropObjId).IsEqualTo(CropObjId);
            await Assert.That(verdict.Reason).IsEqualTo("crop-immature");
            await Assert.That(verdict.DispatchToken).IsEqualTo("wait-maturity");
        }
    }

    [Test]
    public async Task HarvestVerdictWithoutACrop_FallsBackRatherThanHarvestingZero()
    {
        // Fail-closed: a Harvest with no objId cannot be addressed. The fallback
        // leaves the wake to the leg's own rule instead of dispatching a harvest of
        // doodad 0.
        var verdict = NeedsFarmLegDispatch.DecideNeedsFarmAction(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Harvest, NeedsReason.CropMature, NeedsVerb.Harvest, 0, 0, null, 1.5f),
            null), NeedsFarmLegDispatch.NeedsFarmArm.Harvest);

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Routed).IsFalse();
    }

    [Test]
    public async Task NamedHoldsAndCropApproach_FallBackOnTheActionArms()
    {
        // Additive: every verdict that is not one of the three routed rows — a named
        // unreadable hold, the crop-approach travel, the re-evaluation after a landed
        // harvest — leaves the action arm to its pre-brain rule.
        var rows = new[]
        {
            new NeedsBrainDecision(
                NeedsVerdict.Hold, NeedsReason.SoilUnreadable, NeedsVerb.Hold, 0, 0, null, float.NaN),
            new NeedsBrainDecision(
                NeedsVerdict.Hold, NeedsReason.SeedUnreadable, NeedsVerb.Hold, 0, 0, null, float.NaN),
            new NeedsBrainDecision(
                NeedsVerdict.Hold, NeedsReason.CropUnreadable, NeedsVerb.Hold, CropObjId, CropTemplateId, null, float.NaN),
            new NeedsBrainDecision(
                NeedsVerdict.Hold, NeedsReason.CropUnreachable, NeedsVerb.Hold, CropObjId, CropTemplateId, null, 300f),
            new NeedsBrainDecision(
                NeedsVerdict.Travel, NeedsReason.CropApproach, NeedsVerb.MoveTo, CropObjId, CropTemplateId, Destination, 30f),
            new NeedsBrainDecision(
                NeedsVerdict.Replant, NeedsReason.HarvestCompleted, NeedsVerb.Hold, CropObjId, CropTemplateId, null, float.NaN),
            new NeedsBrainDecision(
                NeedsVerdict.SeekSoil, NeedsReason.NoSoilNearby, NeedsVerb.Hold, 0, 0, null, float.NaN)
        };

        foreach (var row in rows)
        {
            var verdict = NeedsFarmLegDispatch.DecideNeedsFarmAction(Prepared(row, null),
                NeedsFarmLegDispatch.NeedsFarmArm.Harvest);
            await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.Fallback);
            await Assert.That(verdict.HasLeg).IsFalse();
            await Assert.That(verdict.Decision).IsNotNull();
            await Assert.That(verdict.Reason).IsEqualTo(NeedsBrain.Token(row.Reason));
        }
    }

    [Test]
    public async Task NoDecisionAtAll_FallsBackOnTheActionArmsToo()
    {
        var verdict = NeedsFarmLegDispatch.DecideNeedsFarmAction(null, NeedsFarmLegDispatch.NeedsFarmArm.Plant);

        await Assert.That(verdict.Verb).IsEqualTo(NeedsFarmLegDispatch.NeedsFarmActionVerb.Fallback);
        await Assert.That(verdict.HasLeg).IsFalse();
        await Assert.That(verdict.Decision).IsNull();
        await Assert.That(verdict.Reason).IsEqualTo("brain-absent");
        await Assert.That(verdict.DispatchToken).IsEqualTo("fallback");
    }

    [Test]
    public async Task ActionWakeDescription_NamesTheDecisionRoutingAndDispatch()
    {
        var routed = NeedsFarmLegDispatch.Describe(NeedsFarmLegDispatch.DecideNeedsFarmAction(Prepared(
            new NeedsBrainDecision(
                NeedsVerdict.Harvest, NeedsReason.CropMature, NeedsVerb.Harvest, CropObjId, CropTemplateId, null, 1.5f),
            null), NeedsFarmLegDispatch.NeedsFarmArm.Harvest));
        await Assert.That(routed).Contains("verdict=harvest:reason=crop-mature:verb=harvest");
        await Assert.That(routed).Contains($"crop={CropObjId}");
        await Assert.That(routed).Contains("routed=true:dispatch=harvest");

        var absent = NeedsFarmLegDispatch.Describe(
            NeedsFarmLegDispatch.DecideNeedsFarmAction(null, NeedsFarmLegDispatch.NeedsFarmArm.Plant));
        await Assert.That(absent).Contains("verdict=none:reason=brain-absent:routed=false:dispatch=fallback");
    }
}
