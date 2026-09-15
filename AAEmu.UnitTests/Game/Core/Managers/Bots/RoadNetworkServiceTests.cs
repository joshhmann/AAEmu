using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

public class RoadNetworkServiceTests
{
    [Test]
    public async Task LoadNetwork_LoadsCorrectJunctionAndEdgeCount()
    {
        var service = new RoadNetworkService();

        await Assert.That(service.JunctionCount).IsEqualTo(136);
        await Assert.That(service.EdgeCount).IsEqualTo(603);

        var junctions = service.GetAllJunctions();
        await Assert.That(junctions.Count).IsEqualTo(136);

        // Verify known sample junction properties
        var j1 = service.GetJunctionById("rb-junction-arcum-iris-J1");
        await Assert.That(j1).IsNotNull();
        await Assert.That(j1!.Label).Contains("Arcum Iris J1");
        await Assert.That(MathF.Abs(j1.X - 21602.44f)).IsLessThan(0.01f);
        await Assert.That(MathF.Abs(j1.Y - 7325.04f)).IsLessThan(0.01f);
        await Assert.That(MathF.Abs(j1.Z - 236.39f)).IsLessThan(0.01f);
        await Assert.That(j1.Routes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FindNearestJunction_KnownPoint_ReturnsArcumIrisJ1()
    {
        var service = new RoadNetworkService();

        // Exact coordinates of Arcum Iris J1
        var exactPos = new Vector3(21602.44f, 7325.04f, 236.39f);
        var nearestExact = service.FindNearestJunction(exactPos);

        await Assert.That(nearestExact).IsNotNull();
        await Assert.That(nearestExact!.Id).IsEqualTo("rb-junction-arcum-iris-J1");
        await Assert.That(nearestExact.Label).Contains("Arcum Iris J1");

        // 2D query with Z = 0 (map plane search)
        var mapPos2D = new Vector3(21602.44f, 7325.04f, 0f);
        var nearest2D = service.FindNearestJunction(mapPos2D);

        await Assert.That(nearest2D).IsNotNull();
        await Assert.That(nearest2D!.Id).IsEqualTo("rb-junction-arcum-iris-J1");

        // Slightly offset position (~15 meters away)
        var offsetPos = new Vector3(21612f, 7335f, 236f);
        var nearestOffset = service.FindNearestJunction(offsetPos);

        await Assert.That(nearestOffset).IsNotNull();
        await Assert.That(nearestOffset!.Id).IsEqualTo("rb-junction-arcum-iris-J1");

        // Search with small radius that cannot reach any junction
        var farPos = new Vector3(0f, 0f, 0f);
        var noneFound = service.FindNearestJunction(farPos, maxRadius: 50f);

        await Assert.That(noneFound).IsNull();
    }

    [Test]
    public async Task FindRoadPath_ConnectedJunctionsAcrossZone_ReturnsValidPath()
    {
        var service = new RoadNetworkService();

        var j1 = service.GetJunctionById("rb-junction-arcum-iris-J1");
        var j15 = service.GetJunctionById("rb-junction-arcum-iris-J15");

        await Assert.That(j1).IsNotNull();
        await Assert.That(j15).IsNotNull();

        // Path across Arcum Iris from J1 to J15
        var path = service.FindRoadPath(j1!.Position, j15!.Position);

        await Assert.That(path.Count).IsEqualTo(4);
        await Assert.That(path[0].Id).IsEqualTo("rb-junction-arcum-iris-J1");
        await Assert.That(path[^1].Id).IsEqualTo("rb-junction-arcum-iris-J15");

        // Path across zones: Arcum Iris J1 to Tigerspine Mountains J1
        var tigerspineJ1 = service.GetJunctionById("rb-junction-tigerspine-J1");
        await Assert.That(tigerspineJ1).IsNotNull();

        var interZonePath = service.FindRoadPath(j1.Position, tigerspineJ1!.Position);

        await Assert.That(interZonePath.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(interZonePath[0].Id).IsEqualTo("rb-junction-arcum-iris-J1");
        await Assert.That(interZonePath[^1].Id).IsEqualTo("rb-junction-tigerspine-J1");
    }

    [Test]
    public async Task FindRoadPath_SameStartAndDestination_ReturnsSingleJunction()
    {
        var service = new RoadNetworkService();
        var j1 = service.GetJunctionById("rb-junction-arcum-iris-J1");
        await Assert.That(j1).IsNotNull();

        var path = service.FindRoadPath(j1!, j1!);

        await Assert.That(path.Count).IsEqualTo(1);
        await Assert.That(path[0].Id).IsEqualTo("rb-junction-arcum-iris-J1");
    }

    [Test]
    public async Task FindRoadPath_DisconnectedContinents_ReturnsEmptyList()
    {
        var service = new RoadNetworkService();

        // Haranya junction
        var haranyaJunction = service.GetJunctionById("rb-junction-arcum-iris-J1");
        await Assert.That(haranyaJunction).IsNotNull();

        // Nuia junction (e.g. Solzreed Peninsula)
        var nuiaJunction = service.GetAllJunctions().FirstOrDefault(j => j.Id.Contains("solzreed", StringComparison.OrdinalIgnoreCase));
        await Assert.That(nuiaJunction).IsNotNull();

        // There is no connected road network between different continents
        var path = service.FindRoadPath(haranyaJunction!, nuiaJunction!);

        await Assert.That(path.Count).IsEqualTo(0);
    }
}
