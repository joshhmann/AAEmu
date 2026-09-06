using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.UnitTests.Game.NPChar;

/// <summary>
/// PB-005 Phase 1 (audit-only): contract of the multi-source height stack
/// (hint &gt; max(terrain, nav-with-256 m planar cap)), the mover-time gates
/// (combat/teleport bypass, ε jitter guard), and the dry-run discipline
/// (legacy Z kept, dispositions counted).
/// New APIs: every test fails pre-change (symbols do not exist on develop).
/// </summary>
public class NpcGroundingPhase1Tests
{
    private static readonly DeckVolume SyntheticDeck = new("synthetic-test-deck",
        MinX: 1000f, MinY: 1000f, MinZ: 90f,
        MaxX: 1020f, MaxY: 1020f, MaxZ: 110f,
        FloorZ: 103.9f);

    [Test]
    [NotInParallel]
    public async Task MultiSource_UsesMaxOfTerrainAndNav()
    {
        // Nav carries the higher (deck-like) sample: clamp target is nav, not terrain.
        // Single-source legacy would clamp to 100.0f.
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, currentZ: 110f,
            terrainZ: 100f, navZ: 103.5f, navPlanarDistanceM: 10f, hintFloorZ: null, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
        await Assert.That(resolvedZ).IsEqualTo(103.5f);
    }

    [Test]
    [NotInParallel]
    public async Task MultiSource_NavBeyond256mPlanar_Ignored()
    {
        // Q1 cap: far-away nav (wrong-zone loader hazard) must not move the needle —
        // clamp target is terrain, and with no terrain at all it is NoGroundSample.
        var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, currentZ: 105.5f,
            terrainZ: 100f, navZ: 103.5f, navPlanarDistanceM: 500f, hintFloorZ: null, out var resolvedZ);
        await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
        await Assert.That(resolvedZ).IsEqualTo(100f);

        var blind = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, currentZ: 105.5f,
            terrainZ: 0f, navZ: 103.5f, navPlanarDistanceM: 500f, hintFloorZ: null, out var keptZ);
        await Assert.That(blind).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.NoGroundSample);
        await Assert.That(keptZ).IsEqualTo(105.5f);
    }

    [Test]
    [NotInParallel]
    public async Task HintFloor_WinsInsideBox()
    {
        try
        {
            DeckVolumeStore.SetVolumesForTests([SyntheticDeck]);
            await Assert.That(NpcGroundingPolicy.TryGetDeckFloor(1010f, 1010f, 100f, out var floor)).IsTrue();
            await Assert.That(floor).IsEqualTo(103.9f);

            var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, currentZ: 110f,
                terrainZ: 100f, navZ: 102f, navPlanarDistanceM: 5f, hintFloorZ: floor, out var resolvedZ);
            await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
            await Assert.That(resolvedZ).IsEqualTo(103.9f);
        }
        finally
        {
            DeckVolumeStore.ResetForTests();
        }
    }

    [Test]
    [NotInParallel]
    public async Task HintFloor_OutsideBox_Ignored()
    {
        try
        {
            DeckVolumeStore.SetVolumesForTests([SyntheticDeck]);
            await Assert.That(NpcGroundingPolicy.TryGetDeckFloor(5000f, 5000f, 100f, out _)).IsFalse();

            var action = NpcGroundingPolicy.ResolveSpawnZ(10082, canFly: false, currentZ: 110f,
                terrainZ: 100f, navZ: 0f, navPlanarDistanceM: float.MaxValue, hintFloorZ: null, out var resolvedZ);
            await Assert.That(action).IsEqualTo(NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround);
            await Assert.That(resolvedZ).IsEqualTo(100f);
        }
        finally
        {
            DeckVolumeStore.ResetForTests();
        }
    }

    [Test]
    [NotInParallel]
    public async Task MoverGate_InCombat_BypassesPolicy()
    {
        var before = NpcGroundingPolicy.GetMoverCount(NpcGroundingPolicy.MoverGroundingDisposition.BypassedCombat);
        var disposition = NpcGroundingPolicy.EvaluateMoverZ(10082, canFly: false, inCombat: true,
            teleportScale: false, currentZ: 110f, terrainZ: 100f, navZ: 0f,
            navPlanarDistanceM: float.MaxValue, hintFloorZ: null, legacyZ: 109f, out var auditZ);
        await Assert.That(disposition).IsEqualTo(NpcGroundingPolicy.MoverGroundingDisposition.BypassedCombat);
        await Assert.That(auditZ).IsEqualTo(109f); // legacy untouched — no chase vertical change
        // Delta-based: unrelated tests may evaluate movers concurrently (audit hooks fire on any MoveTowards).
        await Assert.That(NpcGroundingPolicy.GetMoverCount(NpcGroundingPolicy.MoverGroundingDisposition.BypassedCombat) - before).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task MoverGate_TeleportScale_BypassesPolicy()
    {
        var disposition = NpcGroundingPolicy.EvaluateMoverZ(10082, canFly: false, inCombat: false,
            teleportScale: true, currentZ: 200f, terrainZ: 100f, navZ: 0f,
            navPlanarDistanceM: float.MaxValue, hintFloorZ: null, legacyZ: 199f, out var auditZ);
        await Assert.That(disposition).IsEqualTo(NpcGroundingPolicy.MoverGroundingDisposition.BypassedTeleport);
        await Assert.That(auditZ).IsEqualTo(199f); // leash-return teleport preserved
    }

    [Test]
    [NotInParallel]
    public async Task DryRun_DefaultOn_WouldClampCounted_LegacyKept()
    {
        await Assert.That(NpcGroundingPolicy.DryRunMoverWrites).IsTrue();
        var wouldClampBefore = NpcGroundingPolicy.GetMoverCount(NpcGroundingPolicy.MoverGroundingDisposition.WouldClampToGround);

        // Severe float, legacy height far from ground: would-clamp, audit carries ground.
        var disposition = NpcGroundingPolicy.EvaluateMoverZ(10082, canFly: false, inCombat: false,
            teleportScale: false, currentZ: 110f, terrainZ: 100f, navZ: 0f,
            navPlanarDistanceM: float.MaxValue, hintFloorZ: null, legacyZ: 109f, out var auditZ);
        await Assert.That(disposition).IsEqualTo(NpcGroundingPolicy.MoverGroundingDisposition.WouldClampToGround);
        await Assert.That(auditZ).IsEqualTo(100f);
        await Assert.That(NpcGroundingPolicy.GetMoverCount(NpcGroundingPolicy.MoverGroundingDisposition.WouldClampToGround) - wouldClampBefore).IsEqualTo(1);

        // Jitter guard: resolution within ε of legacy counts as kept, not would-clamp.
        var jitter = NpcGroundingPolicy.EvaluateMoverZ(10082, canFly: false, inCombat: false,
            teleportScale: false, currentZ: 102f, terrainZ: 100f, navZ: 0f,
            navPlanarDistanceM: float.MaxValue, hintFloorZ: null, legacyZ: 100.02f, out _);
        await Assert.That(jitter).IsEqualTo(NpcGroundingPolicy.MoverGroundingDisposition.KeptSourceZ);

    }
}
