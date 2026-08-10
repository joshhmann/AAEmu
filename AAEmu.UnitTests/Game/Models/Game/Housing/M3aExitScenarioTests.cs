using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

/// <summary>
/// M3a EXIT scenario (t_72c787c8) — ROADMAP §M3a exit condition:
/// "two players establish adjacent homesteads and use the curated objects during
/// ONE uninterrupted session."
///
/// ONE test method drives the whole loop on the REAL engine paths, in session
/// order, with no teardown/restart between player actions:
///
///   1. Placement  — both players place small houses (canonical template 172) on the
///      real w_solzreed_2 land zone (canonical HousingGameData + validator, the exact
///      call HousingManager.Build makes). Player B's plot is ADJACENT (16 m — the
///      7.5+7.5 m garden-radius minimum), and a 10 m attempt is rejected (overlap).
///   2. Construction — both houses built to completion via the real CraftEffect path
///      (canonical build steps: skills 14575/14575/14574 → CurrentStep -1).
///   3. Crops — player A plants potatoes on their own homestead land (canonical seed
///      15659 → doodad 2259), grows to mature (real growth tasks), harvests (real
///      Doodad.Use chain) and gets the canonical pack yield.
///   4. Storage — player A stores an item in the house coffer (real CofferContainer
///      put/get + capacity), opens/closes it (real OpenCofferDoodad), and the client
///      receives the contents packet (0x96).
///   5. Furniture — the coffer furniture is house-attached (real DoodadManager.Create
///      with a House owner) and Doodad.Use runs the real phase chain; the client gets
///      house + doodad state packets (0x69/0xbc/0x112).
///
/// Singleton discipline (t_4f11a519): seeds missing-only; only HousingGameData /
/// WorldManager / QuestManager are swapped and restored (construction-rig pattern).
/// The DoodadManager / ItemManager / HousingManager surfaces are extended
/// ADDITIVELY (crops-rig convention) — never replaced, never restored, so the
/// potato + coffer surfaces coexist for the rest of the suite.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class M3aExitScenarioTests
{
    // Canonical 1.2 ids (compact.sqlite3, verified 2026-08-10).
    private const uint SmallHouseDesignId = 172;   // 아담한 누이아 주택 (cat 1, garden 7.5)
    private const uint PotatoSeedItemId = 15659;   // 감자 씨앗
    private const uint PotatoItemId = 7992;        // 감자
    private const uint GoldenPotatoItemId = 19887; // 샛노란 감자
    private const uint PotatoDoodadId = 2259;      // 감자 doodad
    private const uint HarvestSkillId = 13980;     // 작물 수확
    private const uint CofferTemplateId = 5001;    // seeded coffer doodad (Capacity 20)

    private const uint HouseAId = 9001;
    private const uint HouseBId = 9002;

    private const float HouseASpawnX = 1000f;
    private const float HouseASpawnY = 1000f;
    private const float AdjacentOffset = 16f; // > 7.5+7.5 garden radii → legal adjacent pair

    private object _previousHousingGameData;
    private object _previousWorldManager;
    private object _previousQuestManager;

    private GameplayActor _playerA;
    private GameplayActor _playerB;
    private HeadlessSession _sessionA;
    private HeadlessSession _sessionB;
    private PacketCaptureSession _captureA;
    private PacketCaptureSession _captureB;

    private House _houseA;
    private House _houseB;
    private WorldInstance _worldA;
    private WorldInstance _worldB;

    [Before(Test)]
    public void SetUp()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();

        // Base + crops surface (missing-only, one-shot; never replaced).
        GameplayActorTestRig.Seed();
        CropHarvestLoopRig.Seed();

        // Coffer/furniture surface merged INTO the same DoodadManager (additive).
        AddCofferSurface();

        // Real canonical housing data (zones + templates + build steps) — construction-rig pattern.
        SeedRealHousingGameData();

        // CraftEffect needs the Building interaction group on the WorldManager.
        SeedBuildingInteractionGroup();

        // CraftEffect's trailing quest event needs a QuestManager surface.
        SeedQuestManager();

        // Two real sessions (M5-stand-in rule: bots may stand in for the second player).
        (_playerA, _sessionA) = GameplayActorTestRig.CreateActor("exit-player-a");
        (_playerB, _sessionB) = GameplayActorTestRig.CreateActor("exit-player-b");
        _worldA = _sessionA.World;
        _worldB = _sessionB.World;
        RegisterWorld(_worldA);
        RegisterWorld(_worldB);

        // Real craft surface on both players (Character.Load normally sets it).
        _playerA.Character.Craft = new CharacterCraft(_playerA.Character);
        _playerB.Character.Craft = new CharacterCraft(_playerB.Character);

        // Packet-capturing client connections (client-visible state assertions).
        _captureA = new PacketCaptureSession();
        _captureB = new PacketCaptureSession();
        _playerA.Character.Connection = new AAEmu.Game.Core.Network.Connections.GameConnection(_captureA);
        _playerB.Character.Connection = new AAEmu.Game.Core.Network.Connections.GameConnection(_captureB);
    }

    [After(Test)]
    public void TearDown()
    {
        var housingField = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        housingField?.SetValue(null, _previousHousingGameData);
        var worldField = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        if (_previousWorldManager != null)
            worldField?.SetValue(null, _previousWorldManager);
        var questField = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        questField?.SetValue(null, _previousQuestManager);
    }

    // ================================================================ THE EXIT SCENARIO

    [Test]
    public async Task TwoPlayers_AdjacentHomesteads_CuratedObjects_OneUninterruptedSession()
    {
        // ---- 1. PLACEMENT — two players, adjacent homesteads (one session) -----------------

        var landZone = HousingGameData.Instance.GetLandZoneByZoneName("w_solzreed_2");
        await Assert.That(landZone).IsNotNull();
        var template = HousingGameData.Instance.GetTemplate(SmallHouseDesignId);
        await Assert.That(template).IsNotNull();

        // Player A places at (1000, 1000) on the real zone with the real zone faction (148).
        var errorA = HousingPlacementValidator.ValidatePlacement(
            landZone, FactionsEnum.NuiaAlliance, template,
            new Vector3(HouseASpawnX, HouseASpawnY, 0),
            FactionsEnum.NuiaAlliance, characterOwnsHouses: false, []);
        await Assert.That(errorA).IsEqualTo(HousingPlacementError.None);

        _houseA = MakeRegisteredHouse(HouseAId, _playerA.Character, template, HouseASpawnX, HouseASpawnY);
        _worldA.AddObject(_houseA);

        // Player B places ADJACENT at 16 m — the legal adjacent pair (15 m required).
        var errorB = HousingPlacementValidator.ValidatePlacement(
            landZone, FactionsEnum.NuiaAlliance, template,
            new Vector3(HouseASpawnX + AdjacentOffset, HouseASpawnY, 0),
            FactionsEnum.NuiaAlliance, characterOwnsHouses: false, [_houseA]);
        await Assert.That(errorB).IsEqualTo(HousingPlacementError.None);

        _houseB = MakeRegisteredHouse(HouseBId, _playerB.Character, template, HouseASpawnX + AdjacentOffset, HouseASpawnY);
        _worldB.AddObject(_houseB);

        // A too-close attempt (10 m < 15 m required) is rejected — adjacency is enforced.
        var errorOverlap = HousingPlacementValidator.ValidatePlacement(
            landZone, FactionsEnum.NuiaAlliance, template,
            new Vector3(HouseASpawnX + 10f, HouseASpawnY, 0),
            FactionsEnum.NuiaAlliance, characterOwnsHouses: false, [_houseA]);
        await Assert.That(errorOverlap).IsEqualTo(HousingPlacementError.OverlapHouse);

        // ---- 2. CONSTRUCTION — both houses built to completion ------------------------------

        BuildToCompletion(_playerA.Character, _houseA, template);
        BuildToCompletion(_playerB.Character, _houseB, template);

        await Assert.That(_houseA.CurrentStep).IsEqualTo(-1);
        await Assert.That(_houseA.ModelId).IsEqualTo(template.MainModelId);
        await Assert.That(_houseB.CurrentStep).IsEqualTo(-1);
        await Assert.That(_houseB.ModelId).IsEqualTo(template.MainModelId);

        // ---- 3. CROPS — player A plants + grows + harvests on their own land ----------------

        _playerA.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, 5);
        var seedBefore = BagCount(_playerA.Character, PotatoSeedItemId);

        var crop = CropHarvestLoopRig.Plant(_playerA.Character, _worldA, _houseA);
        await Assert.That(crop).IsNotNull();
        await Assert.That(crop.OwnerDbId).IsEqualTo(HouseAId);
        await Assert.That(crop.OwnerType).IsEqualTo(DoodadOwnerType.Housing);
        await Assert.That(BagCount(_playerA.Character, PotatoSeedItemId)).IsEqualTo(seedBefore - 1);

        // Grow to mature via the REAL scheduled growth tasks (no wall clock).
        const uint maturePhase = 4457; // 감자 (mature)
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        (crop.FuncTask as DoodadFuncGrowthTask)?.Execute();
        await Assert.That(crop.FuncGroupId).IsEqualTo(maturePhase);

        // One real harvest interaction → canonical pack yield + plot reset.
        var seedBeforeHarvest = BagCount(_playerA.Character, PotatoSeedItemId);
        crop.Use(_playerA.Character, HarvestSkillId);

        await Assert.That(BagCount(_playerA.Character, PotatoItemId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BagCount(_playerA.Character, PotatoItemId)).IsLessThanOrEqualTo(4);
        await Assert.That(BagCount(_playerA.Character, GoldenPotatoItemId)).IsEqualTo(1);
        await Assert.That(BagCount(_playerA.Character, PotatoSeedItemId)).IsEqualTo(seedBeforeHarvest + 1);
        await Assert.That(_worldA.GetDoodad(crop!.ObjId)).IsNull(); // plot reset

        // ---- 4. STORAGE — player A stores/retrieves in the house coffer ---------------------

        var coffer = (DoodadCoffer)DoodadManager.Instance.Create(_worldA, 0, CofferTemplateId, _houseA, skipPhaseInitialization: true);
        _worldA.AddObject(coffer);
        coffer.InitializeCoffer(_playerA.Character.Id);

        var storageItem = new ItemMock(6001, 2001);
        await Assert.That(coffer.ItemContainer.AddOrMoveExistingItem(ItemTaskType.Gm, storageItem)).IsTrue();
        await Assert.That(coffer.ItemContainer.FreeSlotCount).IsEqualTo(19);

        var opened = DoodadManager.Instance.OpenCofferDoodad(_playerA.Character, coffer.ObjId);
        await Assert.That(opened).IsTrue();
        await Assert.That(coffer.OpenedBy).IsEqualTo(_playerA.Character);
        await Assert.That(CapturedOpcodes(_captureA)).Contains(SCOffsets.SCCofferContentsUpdatePacket);

        var removed = coffer.ItemContainer.RemoveItem(ItemTaskType.Gm, storageItem, false);
        await Assert.That(removed).IsTrue();
        await Assert.That(coffer.ItemContainer.FreeSlotCount).IsEqualTo(20);

        var closed = DoodadManager.Instance.CloseCofferDoodad(_playerA.Character, coffer.ObjId);
        await Assert.That(closed).IsTrue();
        await Assert.That(coffer.OpenedBy).IsNull();

        // ---- 5. FURNITURE — the house-attached coffer furniture + client-visible state ------

        // Furniture on player A's house (real Create path with a House owner).
        var furniture = DoodadManager.Instance.Create(_worldA, 0, CofferTemplateId, _houseA, skipPhaseInitialization: true);
        await Assert.That(furniture.OwnerType).IsEqualTo(DoodadOwnerType.Housing);
        await Assert.That(furniture.OwnerDbId).IsEqualTo(HouseAId);
        await Assert.That(furniture.ParentObjId).IsEqualTo(_houseA.ObjId);
        _worldA.AddObject(furniture);
        _houseA.AttachedDoodads.Add(furniture);

        // Doodad.Use runs the real phase chain (DoChangePhase → DoPhaseFuncs → DoodadFuncCoffer).
        var cofferFurniture = (DoodadCoffer)furniture;
        cofferFurniture.InitializeCoffer(_playerA.Character.Id);
        furniture.Use(_playerA.Character, 0);
        await Assert.That(cofferFurniture.OpenedBy).IsEqualTo(_playerA.Character);
        await Assert.That(CapturedOpcodes(_captureA)).Contains(SCOffsets.SCCofferContentsUpdatePacket);

        // Client-visible state: house + furniture packets reach the client.
        _houseA.AddVisibleObject(_playerA.Character);
        var opcodes = CapturedOpcodes(_captureA);
        await Assert.That(opcodes).Contains(SCOffsets.SCUnitStatePacket);
        await Assert.That(opcodes).Contains(SCOffsets.SCHouseStatePacket);
        await Assert.That(opcodes).Contains(SCOffsets.SCDoodadsCreatedPacket);

        // One uninterrupted session: both players' houses exist side by side at the end.
        await Assert.That(HousingManager.Instance.GetHouseById(HouseAId)).IsNotNull();
        await Assert.That(HousingManager.Instance.GetHouseById(HouseBId)).IsNotNull();
    }

    // ================================================================ rig helpers

    private static void BuildToCompletion(Character builder, House house, HousingTemplate template)
    {
        var effect = new CraftEffect { WorldInteraction = WorldInteractionType.Building };
        foreach (var step in template.BuildSteps.Values)
        {
            effect.Apply(builder, null, house, null,
                new CastSkill(step.SkillId, house.TlId), new EffectSource(), null, DateTime.UtcNow);
        }
    }

    private House MakeRegisteredHouse(uint id, Character owner, HousingTemplate template, float x, float y)
    {
        var house = new House
        {
            Id = id,
            ObjId = 0xC000 + id,
            TlId = (ushort)id,
            Template = template,
            TemplateId = template.Id,
            OwnerId = owner.Id,
            CoOwnerId = owner.Id,
            AccountId = owner.AccountId,
            Name = $"exit_house_{id}",
            Permission = HousingPermission.Private,
            AllowRecover = true,
            PlaceDate = DateTime.UtcNow,
            ProtectionEndDate = DateTime.UtcNow.AddDays(14)
        };
        house.Transform = new Transform(house, null, new Vector3(x, y, 0), Vector3.Zero);
        house.Transform.InstanceId = _worldA.Id;
        house.CurrentStep = template.BuildSteps.Count > 0 ? 0 : -1;

        var manager = HousingManager.Instance;
        SetPrivateField(manager, "_houses", MergeDict(GetPrivateField<Dictionary<uint, House>>(manager, "_houses"), house.Id, house));
        SetPrivateField(manager, "_housesTl", MergeDict(GetPrivateField<Dictionary<ushort, House>>(manager, "_housesTl"), house.TlId, house));
        return house;
    }

    private static Dictionary<TKey, TValue> MergeDict<TKey, TValue>(Dictionary<TKey, TValue> existing, TKey key, TValue value)
    {
        existing ??= [];
        existing[key] = value;
        return existing;
    }

    // --- singleton seeding ------------------------------------------------------

    private void SeedRealHousingGameData()
    {
        var field = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousHousingGameData = field?.GetValue(null);
        var gameData = new HousingGameData();
        using (var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly"))
        {
            connection.Open();
            gameData.Load(connection);
        }
        field?.SetValue(null, gameData);
    }

    private void SeedBuildingInteractionGroup()
    {
        var field = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousWorldManager = field?.GetValue(null);
        var worldManager = WorldManager.Instance;
        var groups = GetPrivateField<Dictionary<uint, WorldInteractionGroup>>(worldManager, "_worldInteractionGroups");
        if (groups == null)
        {
            groups = [];
            SetPrivateField(worldManager, "_worldInteractionGroups", groups);
        }
        groups[(uint)WorldInteractionType.Building] = WorldInteractionGroup.Building;
    }

    private void SeedQuestManager()
    {
        var field = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousQuestManager = field?.GetValue(null);
        if (_previousQuestManager != null)
            return; // already established — CraftEffect's event call is safe on a seeded manager
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        SetPrivateField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
        field?.SetValue(null, questManager);
    }

    /// <summary>
    /// Merges the M3a-4 coffer/furniture surface into the EXISTING DoodadManager
    /// (the one CropHarvestLoopRig seeded with the potato chain) — additive only,
    /// never replacing, so both curated surfaces coexist.
    ///
    /// ALSO swaps the object-id manager to an INCREMENTING fake: the crops rig's
    /// mock returns a constant 0x200000 for every GetNextId, so every doodad in
    /// this scenario would share an ObjId and GetDoodad would resolve the wrong
    /// object (the furniture coffer opening the storage coffer).
    /// </summary>
    private static void AddCofferSurface()
    {
        var manager = DoodadManager.Instance;

        // Incrementing objIds (storage-rig pattern) — start above the crops rig's constant.
        var objIdField = typeof(DoodadManager).GetField("<objectIdManager>P", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(DoodadManager).GetField("objectIdManager", BindingFlags.NonPublic | BindingFlags.Instance);
        if (objIdField != null)
        {
            var current = (IObjectIdManager)objIdField.GetValue(manager);
            if (current == null || current.GetNextId() == 0x200000)
            {
                objIdField.SetValue(manager, new FakeObjectIdManager(0x200100));
            }
        }

        // Coffer template + func-group wiring (mirrors the M3a-4 harness).
        var cofferFuncGroup = new DoodadFuncGroups
        {
            Id = 1,
            Almighty = CofferTemplateId,
            GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Start
        };
        var cofferTemplate = new DoodadCofferTemplate { Id = CofferTemplateId, Capacity = 20 };
        cofferTemplate.FuncGroups.Add(cofferFuncGroup);

        var templates = GetPrivateField<Dictionary<uint, DoodadTemplate>>(manager, "_templates") ?? [];
        templates.TryAdd(CofferTemplateId, cofferTemplate);
        SetPrivateField(manager, "_templates", templates);

        var allFuncGroups = GetPrivateField<Dictionary<uint, DoodadFuncGroups>>(manager, "_allFuncGroups") ?? [];
        allFuncGroups.TryAdd(1, cofferFuncGroup);
        SetPrivateField(manager, "_allFuncGroups", allFuncGroups);

        var funcsByGroups = GetPrivateField<Dictionary<uint, List<DoodadFunc>>>(manager, "_funcsByGroups") ?? [];
        funcsByGroups.TryAdd(1, [new DoodadFunc
        {
            GroupId = 1, FuncId = 1, FuncType = "DoodadFuncCoffer", NextPhase = 1, SkillId = 0
        }]);
        SetPrivateField(manager, "_funcsByGroups", funcsByGroups);

        var phaseFuncs = GetPrivateField<Dictionary<uint, List<DoodadPhaseFunc>>>(manager, "_phaseFuncs") ?? [];
        phaseFuncs.TryAdd(1, [new DoodadPhaseFunc { GroupId = 1, FuncId = 1, FuncType = "DoodadFuncCoffer" }]);
        SetPrivateField(manager, "_phaseFuncs", phaseFuncs);

        var phaseFuncTemplates = GetPrivateField<Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>>>(manager, "_phaseFuncTemplates") ?? [];
        if (!phaseFuncTemplates.TryGetValue("DoodadFuncCoffer", out var cofferTemplates))
            phaseFuncTemplates["DoodadFuncCoffer"] = cofferTemplates = [];
        cofferTemplates[1] = new DoodadFuncCoffer { Capacity = 20 };
        SetPrivateField(manager, "_phaseFuncTemplates", phaseFuncTemplates);
    }

    private void RegisterWorld(WorldInstance world)
    {
        if (world.Regions == null)
            world.Regions = new Region[world.Template.CellX * WorldManager.SECTORS_PER_CELL, world.Template.CellY * WorldManager.SECTORS_PER_CELL];
        var worlds = GetPrivateField<ConcurrentDictionary<uint, WorldInstance>>(WorldManager.Instance, "_worlds");
        worlds?.TryAdd(world.Id, world);
    }

    private static string CanonicalDbPath
    {
        get
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         Path.Combine(baseDir, "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(Directory.GetCurrentDirectory(), "AAEmu.Game", "Data", "compact.sqlite3"),
                         Path.Combine(baseDir, "..", "..", "..", "..", "..", "AAEmu.Game", "Data", "compact.sqlite3")
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            throw new FileNotFoundException("compact.sqlite3 not found in any expected test layout");
        }
    }

    private static int BagCount(Character character, uint templateId)
        => character.Inventory.Bag.Items.Where(i => i.TemplateId == templateId).Sum(i => i.Count);

    private static IEnumerable<ushort> CapturedOpcodes(PacketCaptureSession capture)
        => capture.CapturedPackets.Select(PacketOpcode);

    private static ushort PacketOpcode(byte[] frame)
    {
        var level = frame.Length > 3 ? frame[3] : (byte)0;
        var opcodeOffset = 4 + (level == 1 ? 2 : 0);
        return (ushort)(frame[opcodeOffset] | (frame[opcodeOffset + 1] << 8));
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        return field == null ? default : (T)field.GetValue(target);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(target, value);
    }
}
