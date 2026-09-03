using System.Numerics;
using AAEmu.Game.Core.Managers;

namespace AAEmu.UnitTests.Game.Core.Managers;

[NotInParallel]
public class ObstacleManagerTests
{
    private string _tempDir = null!;

    [Before(Test)]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aaemu-obstacle-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        ObstacleManager.Instance.DataDirectory = _tempDir;
    }

    [After(Test)]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Test]
    public async Task AddObstacle_AndQuery_DetectsCollisionsAccurately()
    {
        var manager = new ObstacleManager();
        var obs = new NavObstacle(101, "Stone Wall", "wall", new Vector3(100f, 100f, 50f), 0f, 3.0f);
        manager.AddObstacle(obs);

        await Assert.That(manager.TotalObstacleCount).IsEqualTo(1);

        // Point inside radius (distance = 2m < 3m)
        var inside = manager.IsBlocked(new Vector3(102f, 100f, 50f));
        await Assert.That(inside).IsTrue();

        // Point outside radius (distance = 4m > 3m)
        var outside = manager.IsBlocked(new Vector3(104f, 100f, 50f));
        await Assert.That(outside).IsFalse();

        // Point with large Z delta (outside vertical tolerance)
        var highZ = manager.IsBlocked(new Vector3(101f, 100f, 70f));
        await Assert.That(highZ).IsFalse();
    }

    [Test]
    public async Task IntersectsObstacle_DetectsCrossingRay()
    {
        var manager = new ObstacleManager();
        // Place a gate at (200, 200) with radius 3m
        manager.AddObstacle(new NavObstacle(202, "Fortress Gate", "gate", new Vector3(200f, 200f, 10f), 0f, 3.0f));

        // Line passing directly through the gate (from 190, 200 to 210, 200)
        var cutsThrough = manager.IntersectsObstacle(new Vector3(190f, 200f, 10f), new Vector3(210f, 200f, 10f));
        await Assert.That(cutsThrough).IsTrue();

        // Line running parallel but offset by 10m (from 190, 210 to 210, 210)
        var misses = manager.IntersectsObstacle(new Vector3(190f, 210f, 10f), new Vector3(210f, 210f, 10f));
        await Assert.That(misses).IsFalse();
    }

    [Test]
    public async Task LoadObstaclesFromFile_ParsesJsonCatalogCorrectly()
    {
        var catalogJson = """
        {
          "zone": "test_zone",
          "totalObstacles": 2,
          "obstacles": [
            { "templateId": 501, "name": "Wooden Fence", "category": "fence", "x": 500.0, "y": 600.0, "z": 20.0, "yaw": 0.5, "keepOutRadius": 2.5 },
            { "templateId": 502, "name": "Town Watchtower", "category": "building", "x": 550.0, "y": 650.0, "z": 22.0, "yaw": 1.2, "keepOutRadius": 8.0 }
          ]
        }
        """;

        var filePath = Path.Combine(_tempDir, "test_zone_obstacles.json");
        await File.WriteAllTextAsync(filePath, catalogJson);

        var manager = new ObstacleManager { DataDirectory = _tempDir };
        manager.Load();

        await Assert.That(manager.TotalObstacleCount).IsEqualTo(2);

        // Verify nearby query
        var nearby = manager.GetNearbyObstacles(new Vector3(500f, 600f, 20f), 15f);
        await Assert.That(nearby.Count).IsEqualTo(1);
        await Assert.That(nearby[0].Name).IsEqualTo("Wooden Fence");
    }
}
