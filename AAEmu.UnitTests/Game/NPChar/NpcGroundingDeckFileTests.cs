using AAEmu.Game.Models.Game.NPChar;
using AAEmu.UnitTests.Game.Housing;

namespace AAEmu.UnitTests.Game.NPChar;

/// <summary>
/// PB-005 deck-hint file coverage: the shipped wardton_dock_pier AABB in
/// Data/Navigation/deck_volumes.json flows through the DeckVolumeStore /
/// TryGetDeckFloor / ResolveSpawnZ-hint path with top precedence — the deck
/// floor beats terrain inside the box and is ignored outside it. Loads the
/// REAL data file (no synthetic volumes, no new entries).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class NpcGroundingDeckFileTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null &&
               !Directory.Exists(Path.Combine(dir.FullName, ".git")) &&
               !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    [Test]
    [NotInParallel]
    public async Task ShippedWardtonPier_DeckFloorBeatsTerrainInside_IgnoredOutside()
    {
        var path = Path.Combine(FindRepoRoot(), "AAEmu.Game", "Data", "Navigation", "deck_volumes.json");
        await Assert.That(File.Exists(path)).IsTrue();
        var previous = DeckVolumeStore.DataFilePath;
        try
        {
            DeckVolumeStore.DataFilePath = path;
            DeckVolumeStore.ResetForTests(); // drop cached volumes so the lazy load reads the real file

            // Inside wardton_dock_pier (15150..15300, 13600..13700, 100..125): hint wins.
            await Assert.That(NpcGroundingPolicy.TryGetDeckFloor(15200f, 13650f, 110f, out var floor)).IsTrue();
            await Assert.That(floor).IsEqualTo(107.61f);

            var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, currentZ: 115f,
                terrainZ: 100f, navZ: 0f, navPlanarDistanceM: float.MaxValue, hintFloorZ: floor, out var resolvedZ);
            await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
            await Assert.That(resolvedZ).IsEqualTo(107.61f); // deck floor, not terrain 100

            // Outside the box: no hint, terrain decides.
            await Assert.That(NpcGroundingPolicy.TryGetDeckFloor(16000f, 13650f, 110f, out _)).IsFalse();
            var outside = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, currentZ: 115f,
                terrainZ: 100f, navZ: 0f, navPlanarDistanceM: float.MaxValue, hintFloorZ: null, out var outsideZ);
            await Assert.That(outside).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
            await Assert.That(outsideZ).IsEqualTo(100f);
        }
        finally
        {
            DeckVolumeStore.DataFilePath = previous;
            DeckVolumeStore.ResetForTests();
        }
    }
}
