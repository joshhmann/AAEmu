using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

public class WildFarmPoiRegistryTests
{
    [Test]
    public async Task Registry_LoadsEmbeddedPois_WhenNoFileSpecified()
    {
        var registry = new WildFarmPoiRegistry("non_existent_file.json");
        await Assert.That(registry.Count).IsGreaterThanOrEqualTo(6);

        var all = registry.GetAllPois();
        await Assert.That(all.Any(p => p.Id == "wild-farm-solzreed-plateau")).IsTrue();
        await Assert.That(all.Any(p => p.Id == "wild-farm-arcum-canyon")).IsTrue();
    }

    [Test]
    public async Task FindNearest_ReturnsClosestWildFarm_WithinRadius()
    {
        var registry = new WildFarmPoiRegistry();

        // Query near Solzreed secret plateau (14210.5, 14680.2)
        var searchPos = new Vector3(14200.0f, 14650.0f, 130.0f);
        var nearest = registry.FindNearest(searchPos, maxRadius: 500f);

        await Assert.That(nearest).IsNotNull();
        await Assert.That(nearest!.Id).IsEqualTo("wild-farm-solzreed-plateau");
        await Assert.That(nearest.X).IsEqualTo(14210.5f);
        await Assert.That(nearest.Y).IsEqualTo(14680.2f);
    }

    [Test]
    public async Task FindNearest_ReturnsNull_WhenBeyondRadius()
    {
        var registry = new WildFarmPoiRegistry();

        // Far away point in the ocean (0, 0)
        var searchPos = new Vector3(0f, 0f, 0f);
        var nearest = registry.FindNearest(searchPos, maxRadius: 100f);

        await Assert.That(nearest).IsNull();
    }

    [Test]
    public async Task GetByZone_FiltersCorrectly()
    {
        var registry = new WildFarmPoiRegistry();

        // Solzreed zone = 179
        var solzreedPois = registry.GetByZone(179);
        await Assert.That(solzreedPois).IsNotEmpty();
        await Assert.That(solzreedPois.All(p => p.ZoneId == 179 || p.ZoneId == 0)).IsTrue();
    }
}
