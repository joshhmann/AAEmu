using System.Numerics;
using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Surveyor senses: pure sweep geometry, clamp fallback, and record shape.
/// Live world reads (Observe, heightmap, LOS) stay behind delegates and the
/// game-loop thread — these tests pin the math the sweep is built on.
/// </summary>
[NotInParallel]
public class BotSurveySensesTests
{
    [Test]
    public async Task BearingRadians_FullCircle_ClosesExactly()
    {
        await Assert.That(SurveySweepMath.BearingRadians(0)).IsEqualTo(0f);
        await Assert.That(SurveySweepMath.BearingRadians(SurveySweepMath.BearingCount))
            .IsEqualTo(2f * MathF.PI).Within(1e-5f);
    }

    [Test]
    public async Task StepPoint_EastBearing_MovesPositiveXOnly()
    {
        var p = SurveySweepMath.StepPoint(new Vector2(100, 200), 0f, 10f);
        await Assert.That(p.X).IsEqualTo(110f).Within(1e-4f);
        await Assert.That(p.Y).IsEqualTo(200f).Within(1e-4f);
    }

    [Test]
    public async Task IsRiseBlocked_ZeroSample_NeverBlocks()
    {
        await Assert.That(SurveySweepMath.IsRiseBlocked(100f, 0f, 0.5f)).IsFalse();
    }

    [Test]
    public async Task IsRiseBlocked_Downhill_NeverBlocks()
    {
        await Assert.That(SurveySweepMath.IsRiseBlocked(100f, 50f, 0.5f)).IsFalse();
    }

    [Test]
    public async Task IsRiseBlocked_RiseAboveStep_Blocks()
    {
        await Assert.That(SurveySweepMath.IsRiseBlocked(100f, 100.6f, 0.5f)).IsTrue();
        await Assert.That(SurveySweepMath.IsRiseBlocked(100f, 100.5f, 0.5f)).IsFalse();
    }

    [Test]
    public async Task SummarizeBearing_ZeroSamples_ExcludedFromProfile()
    {
        var (min, max, mean, blocked) = SurveySweepMath.SummarizeBearing(
            [0f, 120f, 0f, 140f], [false, false, false, true]);
        await Assert.That(min).IsEqualTo(120f);
        await Assert.That(max).IsEqualTo(140f);
        await Assert.That(mean).IsEqualTo(130f);
        await Assert.That(blocked).IsEqualTo(1);
    }

    [Test]
    public async Task SweepTerrain_HillToTheEast_BlocksOnlyEastBearings()
    {
        float Height(float x, float y) => x > 5f ? 200f : 100f;
        var sweep = BotSurveySenses.SweepTerrain(new Vector2(0, 0), 100f, Height, 0.5f,
            (_, _) => true);

        await Assert.That(sweep.Count).IsEqualTo(SurveySweepMath.BearingCount);
        var east = sweep[0];
        await Assert.That(east.StepBlocked.Contains(true)).IsTrue();
        var west = sweep[SurveySweepMath.BearingCount / 2];
        await Assert.That(west.StepBlocked.Contains(true)).IsFalse();
        await Assert.That(west.MinZ).IsEqualTo(100f);
    }

    [Test]
    public async Task SweepTerrain_OccludedEndpoint_ReportsNotVisible()
    {
        float Height(float x, float y) => 100f;
        var sweep = BotSurveySenses.SweepTerrain(new Vector2(0, 0), 100f, Height, 0.5f,
            (_, _) => false);
        await Assert.That(sweep[0].EndpointVisible).IsFalse();
    }

    [Test]
    public async Task SweepTerrain_ThrowingSampler_DegradesToNoData()
    {
        float Height(float x, float y) => throw new InvalidOperationException("no cell");
        var sweep = BotSurveySenses.SweepTerrain(new Vector2(0, 0), 100f, Height, 0.5f,
            (_, _) => true);
        await Assert.That(sweep[0].MinZ).IsEqualTo(0f);
        await Assert.That(sweep[0].StepBlocked.Contains(true)).IsFalse();
    }

    [Test]
    public async Task ClampEndpoint_ZeroProbe_KeepsInputZ()
    {
        var input = new Vector3(14000, 14500, 120.5f);
        var clamped = SurveyLegRunner.ClampEndpoint(input, 5, (_, _) => 0f);
        await Assert.That(clamped).IsEqualTo(input);
    }

    [Test]
    public async Task ClampEndpoint_LargeDeviation_SnapsToProbe()
    {
        var input = new Vector3(14000, 14500, 50f);
        var clamped = SurveyLegRunner.ClampEndpoint(input, 5, (_, _) => 120.5f);
        await Assert.That(clamped.Z).IsEqualTo(120.5f);
        await Assert.That(clamped.X).IsEqualTo(14000f);
    }

    [Test]
    public async Task ClampEndpoint_ThrowingProbe_KeepsInput()
    {
        var input = new Vector3(14000, 14500, 120.5f);
        var clamped = SurveyLegRunner.ClampEndpoint(input, 5,
            (_, _) => throw new InvalidOperationException("unloaded"));
        await Assert.That(clamped).IsEqualTo(input);
    }

    [Test]
    public async Task SurveyPoint_JsonRoundTrip_PreservesShape()
    {
        var point = new SurveyPoint(14000, 14500, 120.5f, 5, "Solzreed", 3, "Nuia",
            [new SurveyOccupant(SurveyOccupantKind.Npc, 7, 673, 14010, 14510, 119f, 14.1f, 500, "Afindelle")],
            [new SurveyBearing(0f, [100f, 101f], [false, false], true, 100f, 101f)],
            new SurveySensorFlags(false, true, 0.5f), DateTime.UtcNow);
        var json = JsonSerializer.Serialize(point);
        var back = JsonSerializer.Deserialize<SurveyPoint>(json);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Occupants.Count).IsEqualTo(1);
        await Assert.That(back.Occupants[0].TemplateId).IsEqualTo(673u);
        await Assert.That(back.Bearings.Count).IsEqualTo(1);
        await Assert.That(back.ZoneName).IsEqualTo("Solzreed");
    }
}
