using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// Sibling-zone population count for <see cref="NpcSpawner"/> (fix/npc-spawner-population-count).
///
/// Root cause: the sibling loop in AreOtherNpcsInSpawnZone added the CURRENT spawner's
/// CurrentSpawnCount + _scheduledCount once PER OCCUPIED SIBLING, so the zone count scaled
/// with the number of neighbors instead of with the neighbors' actual NPCs.
/// The fix contributes each sibling's own list count (npcs.Count).
///
/// No world/singleton seeding: the method under test touches only the shared SpawnedNpcs
/// map and the per-instance zone cache; list ELEMENTS are never dereferenced, so bare
/// `new Npc()` entries suffice. Each test uses a fresh spawner (fresh cache: 10 s TTL
/// keyed by SpawnerId) and a distinct SpawnerId band.
/// </summary>
public class NpcSpawnerSiblingCountTests
{
    private static readonly BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static (bool AreOthersInZone, int Count) ZoneCountOf(NpcSpawner probe)
    {
        var result = typeof(NpcSpawner)
            .GetMethod("AreOtherNpcsInSpawnZone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(probe, null);
        return ((bool, int))result!;
    }

    /// <summary>
    /// Builds a probe spawner plus one sibling CLONE (production MemberwiseClone semantics:
    /// the clone shares the SpawnedNpcs reference) on a single shared map.
    /// </summary>
    private static NpcSpawner ProbeWithSibling(
        uint probeId, int ownCount, int scheduled,
        uint siblingId, int siblingCount,
        ConcurrentDictionary<uint, List<Npc>>? shared = null)
    {
        shared ??= new ConcurrentDictionary<uint, List<Npc>>();
        var probe = new NpcSpawner { SpawnerId = probeId, SpawnedNpcs = shared };
        shared[probeId] = Enumerable.Range(0, ownCount).Select(_ => new Npc()).ToList();
        if (scheduled != 0)
            typeof(NpcSpawner).GetField("_scheduledCount", AnyInstance)?.SetValue(probe, scheduled);

        var sibling = NpcSpawner.Clone(probe); // shallow: shares SpawnedNpcs AND the zone cache
        sibling.SpawnerId = siblingId;         // cache is keyed by SpawnerId, so no cross-talk
        shared[siblingId] = Enumerable.Range(0, siblingCount).Select(_ => new Npc()).ToList();
        return probe;
    }

    [Test]
    public async Task AreOtherNpcsInSpawnZone_NoSiblings_ReturnsZeroAndFalse()
    {
        // Characterization guard: locks in self-exclusion (own key never counted).
        // Passes before AND after the fix (the old loop also found no qualifying sibling).
        var probe = ProbeWithSibling(probeId: 101, ownCount: 2, scheduled: 1,
            siblingId: 102, siblingCount: 0);
        probe.SpawnedNpcs.TryRemove(102, out _); // truly alone; empty list would also be skipped

        var (areOthersInZone, count) = ZoneCountOf(probe);

        await Assert.That(count).IsEqualTo(0);
        await Assert.That(areOthersInZone).IsFalse();
    }

    [Test]
    public async Task AreOtherNpcsInSpawnZone_SingleOccupiedSibling_ReturnsSiblingActuals()
    {
        // Own 2 + 1 scheduled must NOT leak into the sibling contribution (sibling holds 5).
        var probe = ProbeWithSibling(probeId: 201, ownCount: 2, scheduled: 1,
            siblingId: 202, siblingCount: 5);

        var (areOthersInZone, count) = ZoneCountOf(probe);

        await Assert.That(count).IsEqualTo(5);
        await Assert.That(areOthersInZone).IsTrue();
    }

    [Test]
    public async Task AreOtherNpcsInSpawnZone_MultipleSiblings_SumsEachSiblingOnce()
    {
        // Own 1, siblings hold 2 and 4 -> 6. Old code returned own(1) x 2 siblings = 2.
        var shared = new ConcurrentDictionary<uint, List<Npc>>();
        var probe = ProbeWithSibling(probeId: 301, ownCount: 1, scheduled: 0,
            siblingId: 302, siblingCount: 2, shared: shared);
        var second = NpcSpawner.Clone(probe);
        second.SpawnerId = 303;
        shared[303] = Enumerable.Range(0, 4).Select(_ => new Npc()).ToList();

        var (areOthersInZone, count) = ZoneCountOf(probe);

        await Assert.That(count).IsEqualTo(6);
        await Assert.That(areOthersInZone).IsTrue();
    }

    [Test]
    public async Task AreOtherNpcsInSpawnZone_EmptySiblingAndScheduledCount_ContributesNeither()
    {
        // Empty sibling list is skipped by the Count > 0 guard; _scheduledCount (2) must not
        // inflate the surviving sibling's contribution (4). Old code returned (3 + 2) x 1 = 5.
        var shared = new ConcurrentDictionary<uint, List<Npc>>();
        var probe = ProbeWithSibling(probeId: 401, ownCount: 3, scheduled: 2,
            siblingId: 402, siblingCount: 0, shared: shared);
        shared[403] = Enumerable.Range(0, 4).Select(_ => new Npc()).ToList(); // via shared map

        var (areOthersInZone, count) = ZoneCountOf(probe);

        await Assert.That(count).IsEqualTo(4);
        await Assert.That(areOthersInZone).IsTrue();
    }
}
