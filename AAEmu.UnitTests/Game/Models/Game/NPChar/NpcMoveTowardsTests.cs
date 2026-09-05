using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using WorldConfig = AAEmu.Game.Models.Game.WorldConfig;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// Slope/step gate tests for <see cref="Npc.MoveTowards"/> (fix/npc-slope-gate).
///
/// Root cause (recon t_93ee94fb): MoveTowards is a straight-line XY step with Z
/// snapped to GetReferenceHeight each 100ms tick and NO obstacle/slope check, so
/// with empty navmesh the chase beeline walks NPCs straight up cliff faces
/// ("walking into hills"). The gate samples the terrain height at the tick's
/// destination and rejects steps that rise more than a walkable step height.
///
/// Test matrix:
/// - flat ground: gate passes, chase advances (legacy behavior preserved)
/// - cliff face:  destination 10m up — step rejected, NPC halts at the base
/// - gentle slope (0.3m over one step): still walkable, NPC advances
/// - missing heightmap data: gate skipped, legacy fallback movement kept
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // seeds the shared WorldManager singleton + AppConfiguration — same convention as QuestActCheckSphereTests
public class NpcMoveTowardsTests
{
    private const uint TestZoneKey = 1000;
    private const uint TestWorldId = 1;
    private const uint TestInstanceId = 1;
    private const string TestWorldName = "test_world";
    private const float GroundHeight = 100f;
    private const float MaxWalkableStep = 0.5f;

    /// <summary>HeightMaxCoefficient for the test world: HeightMap value / 100 = meters.</summary>
    private const double HeightCoefficient = 100.0;

    private object _previousWorldManagerInstance;
    private object _previousZoneManagerInstance;
    private object _previousSkillManagerInstance;
    private bool _previousHeightMapsEnable;
    private WorldConfig _previousWorldConfig;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManagerInstance = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousZoneManagerInstance = typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousSkillManagerInstance = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousHeightMapsEnable = AppConfiguration.Instance.HeightMapsEnable;
        // No config JSON is loaded in unit tests: AppConfiguration.Instance.World is null
        // unless seeded (production loads it from Config.json / Configurations/*.json).
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();
        AppConfiguration.Instance.HeightMapsEnable = true; // enable terrain height queries for the gate
        // Unit.OnZoneChange resolves ZoneManager.Instance when the transform's zone changes
        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        typeof(ZoneManager).GetField("_zones", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(zoneManager, new Dictionary<uint, Zone>());
        typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, zoneManager);
        // MoveTowards resolves SkillManager.Instance for the Shackle/Snare checks
        var skillManager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        typeof(SkillManager).GetField("_taggedBuffs", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(skillManager, new Dictionary<uint, List<uint>>());
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, skillManager);
        SeedWorldManager();
    }

    [After(Test)]
    public void TearDown()
    {
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousWorldManagerInstance);
        typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousZoneManagerInstance);
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousSkillManagerInstance);
        AppConfiguration.Instance.HeightMapsEnable = _previousHeightMapsEnable;
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task MoveTowards_FlatGround_AdvancesTowardTarget()
    {
        var npc = CreateNpc(10f, 10f, GroundHeight);
        var target = new Vector3(20f, 10f, GroundHeight);

        var result = npc.MoveTowards(target, 5f);

        // Not in range yet, but the step must have been committed on flat ground
        await Assert.That(result).IsFalse();
        await Assert.That(npc.Transform.Local.Position.X > 14.9f && npc.Transform.Local.Position.X < 15.1f).IsTrue();
        await Assert.That(npc.Transform.Local.Position.Y).IsEqualTo(10f);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(GroundHeight);
    }

    [Test]
    public async Task MoveTowards_CliffFace_DoesNotMoveUpTheCliff()
    {
        // The first 5m step from x=10 lands at x=14.47: raise the whole sample row so the
        // destination terrain is 10m above the current position.
        SetTerrainHeight(12f, 10f, GroundHeight + 10f);
        SetTerrainHeight(14f, 10f, GroundHeight + 10f);
        SetTerrainHeight(16f, 10f, GroundHeight + 10f);
        var npc = CreateNpc(10f, 10f, GroundHeight);
        var target = new Vector3(30f, 10f, GroundHeight + 10f);

        var result = npc.MoveTowards(target, 5f);

        // Step rejected: NPC must halt at the cliff base, position unchanged
        await Assert.That(result).IsFalse();
        await Assert.That(npc.Transform.Local.Position.X).IsEqualTo(10f);
        await Assert.That(npc.Transform.Local.Position.Y).IsEqualTo(10f);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(GroundHeight);
    }

    [Test]
    public async Task MoveTowards_GentleSlopeWithinWalkableStep_Advances()
    {
        // 0.3m rise at the exact destination sample (x=14) — under the 0.5m
        // walkable step, so the step must be committed. Target keeps the same Z
        // so the XY step is axis-aligned and lands exactly on the sample.
        SetTerrainHeight(14f, 10f, GroundHeight + 0.3f);
        var npc = CreateNpc(10f, 10f, GroundHeight);
        var target = new Vector3(30f, 10f, GroundHeight);

        var result = npc.MoveTowards(target, 4f);

        await Assert.That(result).IsFalse();
        await Assert.That(npc.Transform.Local.Position.X > 13.9f && npc.Transform.Local.Position.X < 14.1f).IsTrue();
        await Assert.That(MathF.Abs(npc.Transform.Local.Position.Z - (GroundHeight + 0.3f)) < 0.001f).IsTrue();
    }

    [Test]
    public async Task MoveTowards_MissingHeightmap_FallsBackToLegacyAndMoves()
    {
        // Cell (1,0) has no heightmap data in the test world: GetHeight returns 0,
        // GetReferenceHeight falls back to the current Z — gate must be skipped
        // (legacy behavior) instead of blocking the step.
        var npc = CreateNpc(1030f, 10f, GroundHeight);
        var target = new Vector3(1040f, 10f, GroundHeight);

        var result = npc.MoveTowards(target, 5f);

        await Assert.That(result).IsFalse();
        await Assert.That(npc.Transform.Local.Position.X > 1034.9f && npc.Transform.Local.Position.X < 1035.1f).IsTrue();
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(GroundHeight);
    }

    [Test]
    public async Task MoveTowards_StepGateDisabled_ClimbsCliffLikeLegacy()
    {
        // With NpcMaxStepHeight = 0 the gate is disabled entirely — legacy behavior
        // (the mob climbs the cliff face), proving the gate itself is what halts it.
        SetTerrainHeight(12f, 10f, GroundHeight + 10f);
        SetTerrainHeight(14f, 10f, GroundHeight + 10f);
        SetTerrainHeight(16f, 10f, GroundHeight + 10f);
        AppConfiguration.Instance.World.NpcMaxStepHeight = 0f;
        var npc = CreateNpc(10f, 10f, GroundHeight);
        var target = new Vector3(30f, 10f, GroundHeight + 10f);

        var result = npc.MoveTowards(target, 5f);

        await Assert.That(result).IsFalse();
        await Assert.That(npc.Transform.Local.Position.X > 14.3f && npc.Transform.Local.Position.X < 14.6f).IsTrue();
        await Assert.That(MathF.Abs(npc.Transform.Local.Position.Z - (GroundHeight + 10f)) < 0.001f).IsTrue();
    }

    [Test]
    public async Task MoveTowards_TeleportScaleReturn_SkipsGateAndSnapsHome()
    {
        // t_26de2672 repro: mob de-aggros in the valley (Z=90) while its spawn sits on
        // a cliff top (Z=110). The per-tick walk back is blocked by the gate (rise
        // 20m >> NpcMaxStepHeight 0.5) — the mob halts at the base — but the
        // leash-timeout teleport (MoveTowards(idle, 1e6)) must NOT be gated: it has to
        // snap the mob home or the mob stays stranded at the cliff base forever.
        SetTerrainHeight(20f, 10f, GroundHeight + 10f);
        var npc = CreateNpc(10f, 10f, GroundHeight - 10f);
        var idle = new Vector3(20f, 10f, GroundHeight + 10f);

        // 1. Per-tick walk step toward the cliff top: gate still blocks it (halts at base)
        npc.MoveTowards(idle, 5f);
        await Assert.That(npc.Transform.Local.Position.X).IsEqualTo(10f);
        await Assert.That(npc.Transform.Local.Position.Y).IsEqualTo(10f);
        await Assert.That(npc.Transform.Local.Position.Z).IsEqualTo(GroundHeight - 10f);

        // 2. Teleport-scale return (distance 1e6, as issued by
        //    ReturnStateBehavior.OnCompletedReturn after the 20s leash timeout):
        //    gate exempt, mob snaps home despite the 20m cliff
        npc.MoveTowards(idle, 1000000.0f);
        await Assert.That(npc.Transform.Local.Position.X).IsEqualTo(20f);
        await Assert.That(npc.Transform.Local.Position.Y).IsEqualTo(10f);
        await Assert.That(MathF.Abs(npc.Transform.Local.Position.Z - (GroundHeight + 10f)) < 0.001f).IsTrue();
    }

    [Test]
    [Arguments(0f, 0.3f, 0.5f)]
    [Arguments(100f, 100.5f, 0.5f)]
    public async Task IsStepBlocked_RiseWithinWalkableStep_NotBlocked(float currentZ, float destinationZ, float maxStep)
    {
        await Assert.That(Npc.IsStepBlocked(currentZ, destinationZ, maxStep)).IsFalse();
    }

    [Test]
    public async Task IsStepBlocked_CliffFaceRise_Blocked()
    {
        await Assert.That(Npc.IsStepBlocked(100f, 110f, 0.5f)).IsTrue();
    }

    [Test]
    public async Task IsStepBlocked_DownwardStep_NeverBlocked()
    {
        // Walking off a ledge / down a slope must not be blocked
        await Assert.That(Npc.IsStepBlocked(100f, 80f, 0.5f)).IsFalse();
    }

    [Test]
    public async Task IsStepBlocked_MissingTerrainData_NotBlocked()
    {
        // Terrain height 0 = no heightmap/navmesh data — gate falls back to legacy
        await Assert.That(Npc.IsStepBlocked(100f, 0f, 0.5f)).IsFalse();
    }

    private static Npc CreateNpc(float x, float y, float z)
    {
        var npc = new Npc { Buffs = new Buffs(), Hp = 1, MaxHp = 1 }; // Hp defaults to 0 → IsDead would be true
        npc.Transform.Local.SetPosition(x, y, z);
        npc.Transform.ZoneId = TestZoneKey;
        var ai = new DefaultAiCharacter { Owner = npc };
        npc.Ai = ai;
        npc.ParentWorld = WorldManager.Instance.GetWorld(TestInstanceId); // wires Transform.InstanceId so region lookups resolve
        return npc;
    }

    /// <summary>
    /// Sets the raw heightmap sample (2m grid) covering world coordinate (x, y).
    /// Even coordinates hit a sample exactly, so GetHeight returns the value verbatim.
    /// </summary>
    private static void SetTerrainHeight(float x, float y, float height)
    {
        var template = WorldManager.Instance.WorldTemplates[TestWorldName];
        var cell = template.Cells[0, 0];
        var sampleX = ((int)x % WorldManager.CELL_SIZE) / 2;
        var sampleY = ((int)y % WorldManager.CELL_SIZE) / 2;
        cell.HeightMap[sampleX, sampleY] = (ushort)(height * HeightCoefficient);
    }

    private static void SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        // Test world with a flat heightmap at GroundHeight (no client data in unit tests)
        var template = new WorldTemplate { Id = TestWorldId, Name = TestWorldName, HeightMaxCoefficient = HeightCoefficient };
        var cell = new WorldCell(0, 0, template);
        cell.VerifyCellLoaded(); // loads an all-zero heightmap (no client files) — safe with GeoDataMode off
        for (var y = 0; y < cell.HeightMap.GetLength(1); y++)
        for (var x = 0; x < cell.HeightMap.GetLength(0); x++)
            cell.HeightMap[x, y] = (ushort)(GroundHeight * HeightCoefficient);
        template.Cells[0, 0] = cell;

        worldManager.WorldTemplates[TestWorldName] = template;
        SetField(worldManager, "_worldIdByZoneKey", new Dictionary<uint, uint> { [TestZoneKey] = TestWorldId });
        // WorldNames is indexed by world template id: index 0 is a placeholder so id 1 lands at index 1
        typeof(WorldManager).GetProperty("WorldNames", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(worldManager, new List<string> { string.Empty, TestWorldName });
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [TestInstanceId] = new WorldInstance(template, 0, false, TestInstanceId) });

        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, worldManager);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }
}
