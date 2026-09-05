using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils;
using WorldConfig = AAEmu.Game.Models.Game.WorldConfig;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// Terrain line-of-sight gate tests for NPC aggro acquisition (fix/npc-aggro-los,
/// recon t_52ebb23f T1).
///
/// Root cause: BaseUnit.CanSeeTarget is a stealth/visibility check only — there is
/// NO line-of-sight / raycast anywhere in the aggro path, so a mob in range+FOV
/// aggros regardless of terrain between it and the target (aggro through hills,
/// ridges, and cliffs). The fix gates aggro acquisition on
/// <see cref="Npc.HasLineOfSight"/> — a heightmap terrain sample along the sight
/// line — while keeping the existing radii and the short-range touch exemptions.
///
/// Test matrix:
/// - pure gate: open flat line visible, cliff between blocked, clearance edge,
///   sloped sight lines, missing heightmap data (legacy fallback), null template,
///   adjacent units (no sampling)
/// - wiring (Behavior.CheckAggression): aggro blocked behind a cliff, aggro works
///   in an open line, touch-range exemption still aggros through a cliff,
///   NpcLineOfSightCheck=false restores legacy through-wall aggro
/// - pack assist (UpdateAggroHelp): pack-link within AggroLinkHelpDist (6.0m)
///   still pulls linked mobs (no regression)
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // seeds the shared WorldManager/ZoneManager/SkillManager/FactionManager singletons + AppConfiguration — same convention as NpcMoveTowardsTests
public class NpcLineOfSightTests
{
    private const uint TestZoneKey = 1000;
    private const uint TestWorldId = 1;
    private const uint TestInstanceId = 1;
    private const string TestWorldName = "test_world";
    private const float GroundHeight = 100f;

    /// <summary>HeightMaxCoefficient for the test world: HeightMap value / 100 = meters.</summary>
    private const double HeightCoefficient = 100.0;

    /// <summary>Default LOS clearance: terrain may rise 2m above the sight line before counting as occlusion.</summary>
    private const float DefaultClearance = 2f;

    /// <summary>ModelId of the seeded actor model (Radius 2m → ModelSize 2, maxHeightGap 3).</summary>
    private const uint TestModelId = 1000;

    private object _previousWorldManagerInstance;
    private object _previousZoneManagerInstance;
    private object _previousSkillManagerInstance;
    private object _previousFactionManagerInstance;
    private object _previousModelManagerInstance;
    private WorldConfig _previousWorldConfig;

    private WorldTemplate _template;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManagerInstance = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousZoneManagerInstance = typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousSkillManagerInstance = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousFactionManagerInstance = typeof(Singleton<FactionManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousModelManagerInstance = typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        // No config JSON is loaded in unit tests: AppConfiguration.Instance.World is null
        // unless seeded (production loads it from Config.json / Configurations/*.json).
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig(); // NpcLineOfSightCheck defaults to true

        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        typeof(ZoneManager).GetField("_zones", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(zoneManager, new Dictionary<uint, Zone>());
        typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, zoneManager);

        var skillManager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        typeof(SkillManager).GetField("_taggedBuffs", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(skillManager, new Dictionary<uint, List<uint>>());
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, skillManager);

        // CanAttack resolves FactionManager.Instance.GetFaction for the zone faction —
        // FactionManager has no parameterless ctor, so seed it explicitly (empty system factions).
        var factionManager = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        typeof(FactionManager).GetField("_systemFactions", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(factionManager, new Dictionary<FactionsEnum, SystemFaction>());
        typeof(Singleton<FactionManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, factionManager);

        // Unit.ModelSize / BaseUnit.GetDistanceTo resolve ModelManager.Instance.GetActorModel —
        // seed one actor model (Radius 2m) so NPCs get a realistic ModelSize instead of 0.
        var modelManager = new ModelManager();
        typeof(ModelManager).GetField("_modelTypes", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(modelManager, new Dictionary<uint, ModelType> { [TestModelId] = new ModelType { SubType = "test", SubId = 1 } });
        typeof(ModelManager).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>> { ["test"] = new Dictionary<uint, Model> { [1] = new ActorModel { Radius = 2f } } });
        typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, modelManager);

        _template = SeedWorldManager();
    }

    [After(Test)]
    public void TearDown()
    {
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousWorldManagerInstance);
        typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousZoneManagerInstance);
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousSkillManagerInstance);
        typeof(Singleton<FactionManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousFactionManagerInstance);
        typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousModelManagerInstance);
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    // ------------------------------------------------------------------
    // Pure gate: Npc.HasLineOfSight
    // ------------------------------------------------------------------

    [Test]
    public async Task HasLineOfSight_OpenFlatGround_Visible()
    {
        // No terrain above the sight line — 18m line at GroundHeight over flat ground
        var result = Npc.HasLineOfSight(_template, new Vector3(30f, 30f, GroundHeight), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasLineOfSight_CliffBetween_Blocked()
    {
        // 5m ridge across the line (terrain 105 vs sight line 100, clearance 2 → blocked)
        SetRidge(38f, 44f, 30f, GroundHeight + 5f);

        var result = Npc.HasLineOfSight(_template, new Vector3(30f, 30f, GroundHeight), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasLineOfSight_RidgeWithinClearance_Visible()
    {
        // Ridge rises 1.5m above the sight line — inside the 2m clearance, so not occluded
        SetRidge(38f, 44f, 30f, GroundHeight + 1.5f);

        var result = Npc.HasLineOfSight(_template, new Vector3(30f, 30f, GroundHeight), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasLineOfSight_RidgeBeyondClearance_Blocked()
    {
        // Ridge rises 2.5m above the sight line — beyond the 2m clearance → occluded
        SetRidge(38f, 44f, 30f, GroundHeight + 2.5f);

        var result = Npc.HasLineOfSight(_template, new Vector3(30f, 30f, GroundHeight), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasLineOfSight_DownhillSightLine_LowRidgeVisible()
    {
        // Sight line descends 110 → 100; a 3m ridge stays below the line + clearance
        // (at the ridge the line is at ~104.4, clearance 2 → 106.4), so still visible.
        SetRidge(38f, 44f, 30f, GroundHeight + 3f);

        var result = Npc.HasLineOfSight(_template, new Vector3(30f, 30f, GroundHeight + 10f), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasLineOfSight_DownhillSightLine_HighRidgeBlocked()
    {
        // Same descending line; a 7m ridge pokes above line + clearance (106.4) → blocked
        SetRidge(38f, 44f, 30f, GroundHeight + 7f);

        var result = Npc.HasLineOfSight(_template, new Vector3(30f, 30f, GroundHeight + 10f), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasLineOfSight_MissingHeightmapData_Visible()
    {
        // All-zero heightmap cell = no terrain data → every sample falls back (legacy):
        // nothing may block.
        var emptyTemplate = CreateEmptyTemplate();

        var result = Npc.HasLineOfSight(emptyTemplate, new Vector3(30f, 30f, GroundHeight), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasLineOfSight_NullTemplate_Visible()
    {
        // No world/terrain source at all → legacy behavior (never block)
        var result = Npc.HasLineOfSight(null, new Vector3(30f, 30f, GroundHeight), new Vector3(48f, 30f, GroundHeight));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasLineOfSight_AdjacentUnits_Visible()
    {
        // 1m apart — no meaningful occlusion possible, gate short-circuits without sampling
        var result = Npc.HasLineOfSight(_template, new Vector3(10f, 10f, GroundHeight), new Vector3(11f, 10f, GroundHeight));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasLineOfSight_OutOfBoundsSamples_FallbackVisible()
    {
        // Line crosses the cell boundary (x=1024) into a cell that does not exist in the
        // test template: GetHeight throws → sample skipped (no data) → legacy fallback.
        var result = Npc.HasLineOfSight(_template, new Vector3(1000f, 30f, GroundHeight), new Vector3(1060f, 30f, GroundHeight));

        await Assert.That(result).IsTrue();
    }

    // ------------------------------------------------------------------
    // Wiring: Behavior.CheckAggression
    // ------------------------------------------------------------------

    [Test]
    public async Task CheckAggression_OpenLine_Aggroes()
    {
        var (npc, behavior) = CreateAggressiveOwner();
        CreateTarget(48f, 30f, GroundHeight);

        var aggroed = behavior.CheckAggression();

        await Assert.That(aggroed).IsTrue();
        await Assert.That(npc.AggroTable.Count).IsEqualTo(1); // OnEnemySeen fired
    }

    [Test]
    public async Task CheckAggression_CliffBetween_DoesNotAggro()
    {
        SetRidge(38f, 44f, 30f, GroundHeight + 5f);
        var (npc, behavior) = CreateAggressiveOwner();
        CreateTarget(48f, 30f, GroundHeight);

        var aggroed = behavior.CheckAggression();

        await Assert.That(aggroed).IsFalse(); // target in range+FOV but occluded by the cliff
        await Assert.That(npc.AggroTable.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CheckAggression_TouchRangeBehindCliff_StillAggroes()
    {
        // The <1m touch exemption is preserved: a unit breathing down the mob's neck
        // aggros even though terrain samples would block a ranged sight line.
        SetRidge(30.2f, 30.4f, 30f, GroundHeight + 5f);
        var (npc, behavior) = CreateAggressiveOwner();
        CreateTarget(30.4f, 30f, GroundHeight);

        var aggroed = behavior.CheckAggression();

        await Assert.That(aggroed).IsTrue();
        await Assert.That(npc.AggroTable.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckAggression_KnobDisabled_AggroesThroughCliff()
    {
        // NpcLineOfSightCheck=false restores the legacy distance+FOV acquisition —
        // the mob aggros through the cliff again, proving the knob is what gates it.
        SetRidge(38f, 44f, 30f, GroundHeight + 5f);
        AppConfiguration.Instance.World.NpcLineOfSightCheck = false;
        var (npc, behavior) = CreateAggressiveOwner();
        CreateTarget(48f, 30f, GroundHeight);

        var aggroed = behavior.CheckAggression();

        await Assert.That(aggroed).IsTrue();
        await Assert.That(npc.AggroTable.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CheckAggression_MissingHeightmap_StillAggroes()
    {
        // Heightmap strip between owner and target reads 0 (no terrain data) → the LOS
        // gate falls back to legacy (visible): the mob still aggros when in range+FOV.
        SetRidge(32f, 46f, 30f, 0f);
        var (npc, behavior) = CreateAggressiveOwner();
        CreateTarget(48f, 30f, GroundHeight);

        var aggroed = behavior.CheckAggression();

        await Assert.That(aggroed).IsTrue();
        await Assert.That(npc.AggroTable.Count).IsEqualTo(1);
    }

    // ------------------------------------------------------------------
    // Pack assist regression: UpdateAggroHelp
    // ------------------------------------------------------------------

    [Test]
    public async Task UpdateAggroHelp_PackLinkWithinHelpDist_StillPulls()
    {
        // Pack assist must keep working: when the owner is hit, linked pack members
        // within AggroLinkHelpDist (6.0m) join the fight — the LOS gate must NOT
        // affect this path (it only gates passive acquisition).
        var owner = CreateAggressiveOwner().Item1;
        var abuser = CreateTarget(33f, 34f, GroundHeight); // 5m from owner — within help dist
        var helper = CreatePackMember(36f, 30f, GroundHeight); // 6m from owner — at the help-dist edge

        var behavior = new IdleBehavior { Ai = owner.Ai };
        behavior.UpdateAggroHelp(abuser);

        await Assert.That(helper.AggroTable.Count).IsEqualTo(1); // helper pulled into combat
        await Assert.That(owner.AggroTable.Count).IsEqualTo(0);   // owner's own aggro unchanged by the help call
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private (Npc Npc, IdleBehavior Behavior) CreateAggressiveOwner()
    {
        var npc = new Npc { ObjId = 1, Hp = 1, MaxHp = 1, ModelId = TestModelId };
        npc.Template = new NpcTemplate
        {
            Aggression = true,
            AttackStartRangeScale = 2f, // acquisition radius = 2 * 10 = 20m
            SightRangeScale = 5f,
            SightFovScale = 2f, // IsFront always true — keeps the test focused on LOS
            Scale = 1f // Npc.Scale resolves from Template.Scale — ModelSize = Radius(2) * Scale(1)
        };
        npc.IsVisible = true;
        npc.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        npc.Transform.Local.SetPosition(30f, 30f, GroundHeight);
        npc.Transform.ZoneId = TestZoneKey;
        var ai = new DefaultAiCharacter { Owner = npc };
        npc.Ai = ai;
        npc.ParentWorld = WorldManager.Instance.GetWorld(TestInstanceId);
        WorldManager.Instance.AddVisibleObject(npc);
        return (npc, new IdleBehavior { Ai = ai });
    }

    private Unit CreateTarget(float x, float y, float z)
    {
        var unit = new Unit { ObjId = 2, Hp = 100, MaxHp = 100 };
        unit.IsVisible = true; // GameObject.IsVisible defaults false; production sets it on spawn
        unit.Faction = new SystemFaction { Id = FactionsEnum.Neutral };
        unit.Transform.Local.SetPosition(x, y, z);
        unit.Transform.ZoneId = TestZoneKey;
        unit.ParentWorld = WorldManager.Instance.GetWorld(TestInstanceId);
        WorldManager.Instance.AddVisibleObject(unit);
        return unit;
    }

    private Npc CreatePackMember(float x, float y, float z)
    {
        var npc = new Npc { ObjId = 3, Hp = 100, MaxHp = 100, ModelId = TestModelId };
        npc.Template = new NpcTemplate
        {
            Aggression = true,
            AcceptAggroLink = true,
            AggroLinkHelpDist = 6f,
            AggroLinkSpecialRuleId = AggroLinkSpecialRuleKind.None,
            AttackStartRangeScale = 2f,
            SightRangeScale = 5f,
            SightFovScale = 2f,
            Scale = 1f
        };
        npc.IsVisible = true;
        npc.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        npc.Transform.Local.SetPosition(x, y, z);
        npc.Transform.ZoneId = TestZoneKey;
        var ai = new DefaultAiCharacter { Owner = npc };
        npc.Ai = ai;
        npc.ParentWorld = WorldManager.Instance.GetWorld(TestInstanceId);
        WorldManager.Instance.AddVisibleObject(npc);
        return npc;
    }

    /// <summary>
    /// Sets the raw heightmap sample (2m grid) covering world coordinate (x, y).
    /// Even coordinates hit a sample exactly, so GetHeight returns the value verbatim.
    /// </summary>
    private void SetTerrainHeight(float x, float y, float height)
    {
        var cell = _template.Cells[0, 0];
        var sampleX = ((int)x % WorldManager.CELL_SIZE) / 2;
        var sampleY = ((int)y % WorldManager.CELL_SIZE) / 2;
        cell.HeightMap[sampleX, sampleY] = (ushort)(height * HeightCoefficient);
    }

    /// <summary>
    /// Raises a straight ridge of <paramref name="height"/> across y=<paramref name="rowY"/>
    /// from x=<paramref name="fromX"/> to x=<paramref name="toX"/> (inclusive, even coords).
    /// </summary>
    private void SetRidge(float fromX, float toX, float rowY, float height)
    {
        for (var x = (int)fromX; x <= (int)toX; x += 2)
            SetTerrainHeight(x, rowY, height);
    }

    private WorldTemplate SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        // Test world with a flat heightmap at GroundHeight (no client data in unit tests).
        // 1x1 cells (16x16 sectors) — enough for the aggro rig (region sector 0,0).
        var template = new WorldTemplate
        {
            Id = TestWorldId,
            Name = TestWorldName,
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = HeightCoefficient,
            ZoneKeyByRegions = new uint[16, 16]
        };
        var cell = new WorldCell(0, 0, template);
        cell.VerifyCellLoaded(); // loads an all-zero heightmap (no client files)
        for (var y = 0; y < cell.HeightMap.GetLength(1); y++)
        for (var x = 0; x < cell.HeightMap.GetLength(0); x++)
            cell.HeightMap[x, y] = (ushort)(GroundHeight * HeightCoefficient);
        template.Cells[0, 0] = cell;

        worldManager.WorldTemplates[TestWorldName] = template;
        SetField(worldManager, "_worldIdByZoneKey", new Dictionary<uint, uint> { [TestZoneKey] = TestWorldId });
        // WorldNames is indexed by world template id: index 0 is a placeholder so id 1 lands at index 1
        typeof(WorldManager).GetProperty("WorldNames", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(worldManager, new List<string> { string.Empty, TestWorldName });
        var world = new WorldInstance(template, 0, false, TestInstanceId);
        world.Regions = new Region[16, 16];
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [TestInstanceId] = world });

        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, worldManager);
        return template;
    }

    /// <summary>
    /// A template whose only cell has an all-zero heightmap — every GetHeight sample
    /// returns 0, i.e. "no terrain data" for the LOS gate.
    /// </summary>
    private static WorldTemplate CreateEmptyTemplate()
    {
        var template = new WorldTemplate
        {
            Id = TestWorldId,
            Name = "empty_world",
            CellX = 1,
            CellY = 1,
            HeightMaxCoefficient = HeightCoefficient
        };
        var cell = new WorldCell(0, 0, template);
        cell.VerifyCellLoaded();
        template.Cells[0, 0] = cell;
        return template;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }
}
