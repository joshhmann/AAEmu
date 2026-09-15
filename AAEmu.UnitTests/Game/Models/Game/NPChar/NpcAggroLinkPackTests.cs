using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.AI.v2.Behaviors.Common;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Models;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.NPChar;

/// <summary>
/// Canonical aggro-link pack membership (npc_aggro_links): a helper that shares ANY
/// aggro_link_id with the pulled NPC joins the hate list even when the legacy
/// distance/faction heuristic refuses (non-aggressive, rule None). All pre-existing
/// refusals (AcceptAggroLink, help_dist bound, sight check) stay in force — pack
/// membership only ever ADDS help, never removes it.
/// Data: canonical compact.sqlite3, read-only; rows discovered dynamically so the
/// tests track the shipped 1.2 data instead of hardcoded ids. Soft-skips when the
/// DB file is absent (bare worker clone).
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel] // seeds the shared WorldManager/SkillManager/FactionManager/ModelManager singletons + NpcGameData pack lookup
public class NpcAggroLinkPackTests
{
    private const uint TestZoneKey = 1000;
    private const uint TestWorldId = 1;
    private const uint TestInstanceId = 1;
    private const string TestWorldName = "test_world";
    private const float GroundHeight = 100f;
    private const uint TestModelId = 1000;
    private static uint _nextObjId = 100;

    private object _previousWorldManagerInstance;
    private object _previousZoneManagerInstance;
    private object _previousSkillManagerInstance;
    private object _previousFactionManagerInstance;
    private object _previousModelManagerInstance;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldManagerInstance = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousZoneManagerInstance = typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousSkillManagerInstance = typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousFactionManagerInstance = typeof(Singleton<FactionManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        _previousModelManagerInstance = typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        typeof(ZoneManager).GetField("_zones", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(zoneManager, new Dictionary<uint, Zone>());
        typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, zoneManager);

        var skillManager = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
        typeof(SkillManager).GetField("_taggedBuffs", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(skillManager, new Dictionary<uint, List<uint>>());
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, skillManager);

        var factionManager = new FactionManager(Mock.Of<ILocalizationManager>().Object);
        typeof(FactionManager).GetField("_systemFactions", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(factionManager, new Dictionary<FactionsEnum, SystemFaction>());
        typeof(Singleton<FactionManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, factionManager);

        var modelManager = new ModelManager();
        typeof(ModelManager).GetField("_modelTypes", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(modelManager, new Dictionary<uint, ModelType> { [TestModelId] = new ModelType { SubType = "test", SubId = 1 } });
        typeof(ModelManager).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(modelManager, new Dictionary<string, Dictionary<uint, Model>> { ["test"] = new Dictionary<uint, Model> { [1] = new ActorModel { Radius = 0f } } });
        typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, modelManager);

        FormulaManager.Instance.Load(); // idempotent; real formulas from canonical data (Npc.MaxHp via GetUnitFormula)

        SeedWorldManager();
    }

    [After(Test)]
    public void TearDown()
    {
        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousWorldManagerInstance);
        typeof(Singleton<ZoneManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousZoneManagerInstance);
        typeof(Singleton<SkillManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousSkillManagerInstance);
        typeof(Singleton<FactionManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousFactionManagerInstance);
        typeof(Singleton<ModelManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, _previousModelManagerInstance);
    }

    // ------------------------------------------------------------------
    // Pack semantics through the real UpdateAggroHelp seam
    // ------------------------------------------------------------------

    [Test]
    public async Task SharedPackMember_JoinsHateList_WhileUnpackedTwin_DoesNot()
    {
        // Same gates for both candidates (non-aggressive, rule None, sight check on
        // against a visible abuser, well inside help_dist): the legacy heuristic
        // refuses BOTH. Only the one sharing a canonical pack with the pulled NPC
        // must join the hate list.
        var pack = OpenPackTriple();
        if (pack == null)
        {
            Console.WriteLine("[AggroLinkPack] SKIPPED — canonical compact.sqlite3 not present");
            return;
        }

        var owner = CreateOwner(pack.Value.PulledId, 30f, 30f);
        var abuser = CreateTarget(33f, 34f);
        var packed = CreateHelper(pack.Value.PackedId, 32f, 30f);
        var unpacked = CreateHelper(pack.Value.LoneId, 28f, 30f);

        var behavior = new IdleBehavior { Ai = owner.Ai };
        behavior.UpdateAggroHelp(abuser);
        await Assert.That(packed.AggroTable.ContainsKey(abuser.ObjId)).IsTrue();
        await Assert.That(unpacked.AggroTable.ContainsKey(abuser.ObjId)).IsFalse();
    }

    [Test]
    public async Task PackedMember_BeyondHelpDist_DoesNotHelp()
    {
        // Pack membership never overrides the help_dist bound.
        var pack = OpenPackTriple();
        if (pack == null)
        {
            Console.WriteLine("[AggroLinkPack] SKIPPED — canonical compact.sqlite3 not present");
            return;
        }

        var owner = CreateOwner(pack.Value.PulledId, 30f, 30f);
        var abuser = CreateTarget(33f, 34f);
        var farPacked = CreateHelper(pack.Value.PackedId, 60f, 30f); // 30m out, help_dist is 10m

        var behavior = new IdleBehavior { Ai = owner.Ai };
        behavior.UpdateAggroHelp(abuser);

        await Assert.That(farPacked.AggroTable.ContainsKey(abuser.ObjId)).IsFalse();
    }

    [Test]
    public async Task PackedMember_DecliningAggroLink_DoesNotHelp()
    {
        // Pack membership never overrides AcceptAggroLink=false.
        var pack = OpenPackTriple();
        if (pack == null)
        {
            Console.WriteLine("[AggroLinkPack] SKIPPED — canonical compact.sqlite3 not present");
            return;
        }

        var owner = CreateOwner(pack.Value.PulledId, 30f, 30f);
        var abuser = CreateTarget(33f, 34f);
        var declining = CreateHelper(pack.Value.PackedId, 32f, 30f, acceptAggroLink: false);

        var behavior = new IdleBehavior { Ai = owner.Ai };
        behavior.UpdateAggroHelp(abuser);

        await Assert.That(declining.AggroTable.ContainsKey(abuser.ObjId)).IsFalse();
    }

    [Test]
    public async Task AggressiveUnpackedHelper_StillHelps()
    {
        // The legacy aggressive fast path is untouched by pack membership.
        var pack = OpenPackTriple();
        if (pack == null)
        {
            Console.WriteLine("[AggroLinkPack] SKIPPED — canonical compact.sqlite3 not present");
            return;
        }

        var owner = CreateOwner(pack.Value.PulledId, 30f, 30f);
        var abuser = CreateTarget(33f, 34f);
        var aggressive = CreateHelper(pack.Value.LoneId, 32f, 30f, aggression: true);

        var behavior = new IdleBehavior { Ai = owner.Ai };
        behavior.UpdateAggroHelp(abuser);

        await Assert.That(aggressive.AggroTable.ContainsKey(abuser.ObjId)).IsTrue();
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    /// <summary>
    /// Discovers (pulled, packed-mate, lone-outsider) template ids from the canonical
    /// DB and seeds the production NpcGameData lookup through its real Load path.
    /// Read-only; null when the DB file is absent.
    /// </summary>
    private static (uint PulledId, uint PackedId, uint LoneId)? OpenPackTriple()
    {
        var dbPath = Path.Combine(AAEmu.Commons.IO.FileManager.AppPath, "Data", "compact.sqlite3");
        if (!File.Exists(dbPath))
            return null;

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        var byLink = new Dictionary<long, List<uint>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT npc_id, aggro_link_id FROM npc_aggro_links";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var npcId = (uint)reader.GetInt64(0);
                var linkId = reader.GetInt64(1);
                if (!byLink.TryGetValue(linkId, out var members))
                    byLink.Add(linkId, members = []);
                if (!members.Contains(npcId))
                    members.Add(npcId);
            }
        }

        var home = byLink.OrderBy(kv => kv.Key).FirstOrDefault(kv => kv.Value.Count >= 2).Value;
        var away = byLink.OrderBy(kv => kv.Key).FirstOrDefault(kv => kv.Value.Count >= 1 && kv.Value[0] != home?[0] && !home.Contains(kv.Value[0])).Value;
        if (home == null || away == null)
            return null;

        // Production load path (also seeds the pack lookup under test).
        NpcGameData.Instance.Load(connection);

        return (home[0], home[1], away[0]);
    }

    private static Npc CreateOwner(uint templateId, float x, float y)
    {
        var npc = new Npc { ObjId = _nextObjId++, Hp = 100, MaxHp = 100, ModelId = TestModelId, TemplateId = templateId };
        npc.Template = new NpcTemplate
        {
            Id = templateId,
            Aggression = false,
            AttackStartRangeScale = 0.1f, // acquisition radius = 0.1 * 200 = 20m, fits the test region
            SightRangeScale = 5f,
            SightFovScale = 2f,
            Scale = 1f
        };
        WireUp(npc, x, y);
        return npc;
    }

    private static Npc CreateHelper(uint templateId, float x, float y, bool acceptAggroLink = true, bool aggression = false)
    {
        var npc = new Npc { ObjId = _nextObjId++, Hp = 100, MaxHp = 100, ModelId = TestModelId, TemplateId = templateId };
        npc.Template = new NpcTemplate
        {
            Id = templateId,
            Aggression = aggression,
            AcceptAggroLink = acceptAggroLink,
            AggroLinkHelpDist = 10f,
            AggroLinkSpecialRuleId = AggroLinkSpecialRuleKind.None,
            AggroLinkSightCheck = true, // canonical default-true: the help path requires a passed sight gate
            AttackStartRangeScale = 2f,
            SightRangeScale = 5f,
            SightFovScale = 2f,
            Scale = 1f
        };
        WireUp(npc, x, y);
        return npc;
    }

    private static Unit CreateTarget(float x, float y)
    {
        var unit = new Unit { ObjId = _nextObjId++, Hp = 100, MaxHp = 100 };
        unit.IsVisible = true; // GameObject.IsVisible defaults false; production sets it on spawn
        unit.Faction = new SystemFaction { Id = FactionsEnum.Neutral };
        unit.Transform.Local.SetPosition(x, y, GroundHeight);
        unit.ParentWorld = WorldManager.Instance.GetWorld(TestInstanceId);
        WorldManager.Instance.AddVisibleObject(unit);
        return unit;
    }

    private static void WireUp(Npc npc, float x, float y)
    {
        npc.IsVisible = true;
        npc.Faction = new SystemFaction { Id = FactionsEnum.Hostile };
        npc.Transform.Local.SetPosition(x, y, GroundHeight);
        npc.Ai = new DefaultAiCharacter { Owner = npc };
        npc.ParentWorld = WorldManager.Instance.GetWorld(TestInstanceId);
        WorldManager.Instance.AddVisibleObject(npc);
    }

    private static void SeedWorldManager()
    {
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        var template = new WorldTemplate
        {
            Id = TestWorldId,
            Name = TestWorldName,
            CellX = 1,
            CellY = 1,
            ZoneKeyByRegions = new uint[16, 16]
        };
        worldManager.WorldTemplates[TestWorldName] = template;
        SetField(worldManager, "_worldIdByZoneKey", new Dictionary<uint, uint> { [TestZoneKey] = TestWorldId });
        typeof(WorldManager).GetProperty("WorldNames", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(worldManager, new List<string> { string.Empty, TestWorldName });
        var world = new WorldInstance(template, 0, false, TestInstanceId);
        world.Regions = new Region[16, 16];
        SetField(worldManager, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [TestInstanceId] = world });

        typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, worldManager);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }
}
