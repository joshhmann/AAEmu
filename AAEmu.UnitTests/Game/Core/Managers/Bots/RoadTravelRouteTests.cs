using System.Numerics;
using System.Text.Json;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

// Synthetic topology and explicitly supplied observations. No live road is certified here.
public class RoadTravelRouteTests
{
    internal static RoadNetworkService Network(bool evidenced = true, bool bidirectional = false,
        bool brokenJoin = false, string mode = "walk")
    {
        var junctions = new[]
        {
            new { id = "a", x = 0, y = 0, z = 10, world_id = 0 },
            new { id = "b", x = 100, y = 0, z = 10, world_id = 0 }
        };
        var edges = new[]
        {
            new { from = "a", to = "b", route = "bent", world_id = 0, travel_mode = mode,
                navigation_evidence = evidenced ? "synthetic-contract-only" : "", bidirectional,
                points = new[] { new[] { brokenJoin ? 2f : 0f, 0, 10 }, new[] { 50f, 100, 10 }, new[] { 100f, 0, 10 } } },
            new { from = "a", to = "b", route = "direct", world_id = 0, travel_mode = mode,
                navigation_evidence = evidenced ? "synthetic-contract-only" : "", bidirectional,
                points = new[] { new[] { brokenJoin ? 2f : 0f, 0, 10 }, new[] { 100f, 0, 10 } } }
        };
        return new RoadNetworkService(null, JsonSerializer.Serialize(new { junctions, edges }));
    }

    private static Vector3 Start => new(0, 0, 10);
    private static Vector3 Destination => new(100, 0, 10);
    internal static RoadTravelQuery Query(long revision = 0) => new(0, 0, 10)
    { Revision = revision, AdditionalSeconds = _ => 0 };

    [Test]
    public async Task CurvedRoad_CostIncludesEveryBendAndKeepsGeometry()
    {
        var route = Network().FindTravelRoute(Start, Destination, Query() with
        { AdditionalSeconds = e => e.Id == "direct" ? 100 : 0 });
        await Assert.That(route).IsNotNull();
        await Assert.That(route!.SegmentIds.Single()).IsEqualTo("bent");
        await Assert.That(route.Points.Count).IsEqualTo(3);
        await Assert.That(route.DistanceM).IsCloseTo(223.6068f, .01f);
        await Assert.That(route.CostSeconds).IsCloseTo(22.36068f, .01f);
        await Assert.That(route.Points[1]).IsEqualTo(new Vector3(50, 100, 10));
    }

    [Test]
    public async Task ChangedDanger_SelectsDifferentRoadWithoutChangingDestination()
    {
        var roads = Network();
        var calm = roads.FindTravelRoute(Start, Destination, Query(1));
        var danger = roads.FindTravelRoute(Start, Destination, Query(2) with
        { AdditionalSeconds = e => e.Id == "direct" ? 100 : 0 });
        await Assert.That(calm!.SegmentIds.Single()).IsEqualTo("direct");
        await Assert.That(danger!.SegmentIds.Single()).IsEqualTo("bent");
        await Assert.That(danger.Points[^1]).IsEqualTo(Destination);
        await Assert.That(danger.Revision).IsEqualTo(2);
    }

    [Test]
    public async Task UnknownOrInvalidDanger_DoesNotBecomeZeroCost()
    {
        foreach (var penalty in new float?[] { null, -1, float.NaN, float.PositiveInfinity })
        {
            var route = Network().FindTravelRoute(Start, Destination, Query() with { AdditionalSeconds = _ => penalty });
            await Assert.That(route).IsNull();
        }
    }

    [Test]
    public async Task CandidateRoadsBrokenJoinsAndUnsupportedModes_AreNotExecutable()
    {
        await Assert.That(Network(evidenced: false).FindTravelRoute(Start, Destination, Query())).IsNull();
        await Assert.That(Network(brokenJoin: true).FindTravelRoute(Start, Destination, Query())).IsNull();
        await Assert.That(Network(mode: "ship").FindTravelRoute(Start, Destination, Query())).IsNull();
        await Assert.That(new RoadNetworkService().FindTravelRoute(Start, Destination, Query())).IsNull();
    }

    [Test]
    public async Task DirectionAndWorld_AreExplicit()
    {
        await Assert.That(Network().FindTravelRoute(Destination, Start, Query())).IsNull();
        await Assert.That(Network(bidirectional: true).FindTravelRoute(Destination, Start, Query())).IsNotNull();
        await Assert.That(Network().FindTravelRoute(Start, Destination, Query() with { WorldId = 99 })).IsNull();
    }

    [Test]
    public async Task NearbyJunction_DoesNotProveApproachCanBeWalked()
    {
        var start = Start + new Vector3(0, -5, 0);
        await Assert.That(Network().FindTravelRoute(start, Destination, Query())).IsNull();
        var query = Query() with { FindConnector = (a, b) => new RoadTravelConnector(new[] { a, b }, 3) };
        var route = Network().FindTravelRoute(start, Destination, query);
        // Both ends may use a single junction and ordinary connector; all connector cost is retained.
        await Assert.That(route).IsNotNull();
        await Assert.That(route!.Points[0]).IsEqualTo(start);
        await Assert.That(route.Points[^1]).IsEqualTo(Destination);
        await Assert.That(route.CostSeconds).IsGreaterThanOrEqualTo(route.DistanceM / 10 + 3);
        await Assert.That(Network().FindTravelRoute(start, Destination,
            Query() with { FindConnector = (a, b) => new RoadTravelConnector(new[] { a, b }, float.NaN) })).IsNull();
    }

    [Test]
    public async Task InvalidSpeedOrCoordinates_WithdrawsRoute()
    {
        await Assert.That(Network().FindTravelRoute(Start, Destination, Query() with { SpeedMps = 0 })).IsNull();
        await Assert.That(Network().FindTravelRoute(new Vector3(float.NaN, 0, 0), Destination, Query())).IsNull();
    }
}
