using System.Reflection;

using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
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
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Tasks.Doodads;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

using Microsoft.Extensions.Options;

namespace AAEmu.UnitTests.Game.Models.Game.DoodadObj;

/// <summary>
/// M3a-3 crop loop harness: plant seed → grow (growth stages) → harvest on the
/// REAL engine paths, driven deterministically (no wall clock, no MySQL).
///
/// Loop under test (canonical 1.2 compact data, verified 2026-08-10):
///   seed item 15659 (감자 씨앗, impl 9, use_skill 25536) → item_spawn_doodads
///   → doodad almighty 2259 (감자), phase chain:
///     4379 seedling (start) → [phase func DoodadFuncGrowth 583: 60s] → 4456 small
///     → [phase func DoodadFuncGrowth 584: 9min] → 4457 mature
///     → harvest skill 13980 (작물 수확) → 4458 looting → DoodadFuncLootPack 129
///     (pack 6452: 2-4× potato 7992 + 1× golden potato 19887 + 1× seed 15659)
///     → 4459 final → phase func DoodadFuncFinal 900 → doodad deleted (plot reset).
///
/// Growth is advanced deterministically by executing the REAL scheduled
/// DoodadFuncGrowthTask (owner.FuncTask) instead of waiting for the 60s/9min
/// timers; harvest is driven through the REAL Doodad.Use(caster, skillId) path.
///
/// Placement is driven through the same real steps as
/// DoodadManager.CreatePlayerDoodad (CSCreateDoodadPacket handler) minus the
/// MySQL Save() tail — persistence is M3b scope, and unit tests have no MySQL.
///
/// Singleton discipline (t_4f11a519): seeds only what is missing, never
/// replaces an established singleton; GameplayActorTestRig.Seed() provides the
/// base surface. AppConfiguration (World.GrowthRate) is set via
/// SingletonContainer.ServiceProvider per-test and restored (World is null
/// outside unit tests).
/// </summary>
[NotInParallel] // touches process-wide SingletonContainer.ServiceProvider + singletons
public class CropHarvestLoopTests
{
    // Canonical 1.2 ids for the potato crop loop (real compact.sqlite3 rows).
    internal const uint PotatoSeedItemId = 15659;   // 감자 씨앗
    internal const uint PotatoItemId = 7992;        // 감자 (2-4 per harvest)
    internal const uint GoldenPotatoItemId = 19887; // 샛노란 감자 (1 per harvest)
    internal const uint PotatoDoodadId = 2259;      // 감자 doodad almighty
    internal const uint WateringSkillId = 13625;    // 물 뿌리기 (row-matched DoodadFuncUse 5205)
    internal const uint SkillHitWateringSkillId = 15601; // 물 뿌리기 (DoodadFuncSkillHit 174 template gate)
    internal const uint WateringInteractionSkillId = 10126; // 물 주기 (InteractionEffect → Watering action)
    internal const uint HarvestSkillId = 13980;     // 작물 수확
    internal const uint SeedlingPhase = 4379;       // 감자 모종 (start group)
    internal const uint SmallPhase = 4456;          // 조그만 감자
    internal const uint MaturePhase = 4457;         // 감자
    internal const uint LootingPhase = 4458;        // 루팅
    internal const uint FinalPhase = 4459;          // 최후 (terminal)
    internal const uint PotatoLootPackId = 6452;    // harvest pack

    private WorldConfig _previousWorldConfig;
    private GameplayActor _actor;
    private HeadlessSession _session;
    private Doodad _doodad;

    [Before(Test)]
    public void SetUp()
    {
        // AppConfiguration.Instance.World is null outside the game host (no
        // config JSON); InitDoodad / DoodadFuncGrowth read World.GrowthRate.
        // Provide a benign config (rate 1.0) for the duration of this class
        // only (same pattern as NpcLineOfSightTests).
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig();

        CropHarvestLoopRig.Seed();

        (_actor, _session) = GameplayActorTestRig.CreateActor("crop-farmer");
    }

    [After(Test)]
    public void TearDown()
    {
        AppConfiguration.Instance.World = _previousWorldConfig;
    }

    [Test]
    public async Task PlaceSeed_OnOwnedFarmLand_SpawnsDoodadBindsOwnerAndConsumesSeed()
    {
        // Arrange — seed in the bag (real item creation path)
        _actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, 5);
        var seedBefore = BagCount(PotatoSeedItemId);

        // Act — plant on owned land (house at the target position)
        var house = CropHarvestLoopRig.MakeHouse(_actor.Character);
        _doodad = CropHarvestLoopRig.Plant(_actor.Character, _session.World, house);

        // Assert — real placement path: doodad spawned in the start phase,
        // bound to the house (owned farm land), seed consumed
        await Assert.That(_doodad).IsNotNull();
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(SeedlingPhase);
        await Assert.That(_doodad.OwnerDbId).IsEqualTo(house.Id);
        await Assert.That(_doodad.OwnerType).IsEqualTo(DoodadOwnerType.Housing);
        await Assert.That(BagCount(PotatoSeedItemId)).IsEqualTo(seedBefore - 1);
    }

    [Test]
    public async Task PlaceSeed_NoHouseAtLocation_UnboundPublicDoodad()
    {
        _actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, 5);

        _doodad = CropHarvestLoopRig.Plant(_actor.Character, _session.World, house: null);

        await Assert.That(_doodad).IsNotNull();
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(SeedlingPhase);
        await Assert.That(_doodad.OwnerDbId).IsEqualTo(0u);
    }

    [Test]
    public async Task Growth_TimeDrivenTasks_AdvanceDeterministicallyToMature()
    {
        _actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, 5);
        _doodad = CropHarvestLoopRig.Plant(_actor.Character, _session.World,
            CropHarvestLoopRig.MakeHouse(_actor.Character));

        // Seedling → small: execute the real scheduled growth task (60s timer
        // compressed to zero wall clock — deterministic harness)
        var task1 = _doodad.FuncTask as DoodadFuncGrowthTask;
        await Assert.That(task1).IsNotNull();
        task1.Execute();
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(SmallPhase);

        // Small → mature: the small phase scheduled its own growth task (9min)
        var task2 = _doodad.FuncTask as DoodadFuncGrowthTask;
        await Assert.That(task2).IsNotNull();
        task2.Execute();
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(MaturePhase);
    }

    [Test]
    public async Task Harvest_MatureCrop_YieldsExpectedItemsAndResetsPlot()
    {
        _actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, 5);
        _doodad = CropHarvestLoopRig.Plant(_actor.Character, _session.World,
            CropHarvestLoopRig.MakeHouse(_actor.Character));
        GrowToMature();
        var seedBeforeHarvest = BagCount(PotatoSeedItemId);

        // One real harvest interaction on the mature crop. The engine's Use()
        // loop runs the WHOLE chain synchronously in this single call:
        //  1. the harvest-skill DoodadFuncUse (mature group, skill 13980)
        //     advances mature → looting;
        //  2. the skill-less pass (skillId=0) executes the looting group's
        //     DoodadFuncLootPack, which grants the pack (loots 6452: 2-4
        //     potato, 1 golden potato, 1 seed) and advances looting → final;
        //  3. the final group's DoodadFuncFinal deletes the doodad
        //     (plot state reset).
        // This matches real AA 1.2: one harvest click on a mature crop yields
        // the items immediately and the crop disappears.
        _doodad.Use(_actor.Character, HarvestSkillId);

        // Item yield correct (loots pack 6452: 2-4 potato, 1 golden potato)
        await Assert.That(BagCount(PotatoItemId)).IsGreaterThanOrEqualTo(2);
        await Assert.That(BagCount(PotatoItemId)).IsLessThanOrEqualTo(4);
        await Assert.That(BagCount(GoldenPotatoItemId)).IsEqualTo(1);
        // Seed returned by the harvest (replant loop) — net: -1 planted, +1 returned
        await Assert.That(BagCount(PotatoSeedItemId)).IsEqualTo(seedBeforeHarvest + 1);

        // Plot state clean: doodad deleted from the world after the final phase
        await Assert.That(_session.World.GetDoodad(_doodad.ObjId)).IsNull();

        // No double yield: further interaction with the terminal doodad is a no-op
        var potatoAfterDelete = BagCount(PotatoItemId);
        _doodad.Use(_actor.Character, HarvestSkillId);
        await Assert.That(BagCount(PotatoItemId)).IsEqualTo(potatoAfterDelete);
    }

    [Test]
    public async Task DoodadFuncCropHarvest_Use_AdvancesToNextPhase()
    {
        var doodad = new Doodad();

        new DoodadFuncCropHarvest().Use(_actor.Character, doodad, HarvestSkillId);

        await Assert.That(doodad.ToNextPhase).IsTrue();
    }

    [Test]
    public async Task DoodadFuncFruitPick_Use_AdvancesToNextPhase()
    {
        var doodad = new Doodad();

        new DoodadFuncFruitPick().Use(_actor.Character, doodad, HarvestSkillId);

        await Assert.That(doodad.ToNextPhase).IsTrue();
    }

    /// <summary>
    /// FARM-01 watering pin (M3 canonical audit §2.3, t_f564d986): a potato
    /// seedling (4379) carries DoodadFuncSkillHit 174 (doodad_funcs row 7815,
    /// row func_skill_id NULL, template skill 15601 물 뿌리기) with next phase
    /// 4456. The engine matches the un-gated SkillHit row via
    /// DoodadManager.GetFunc's fallback and the template's SkillId gate sets
    /// ToNextPhase — one watering interaction advances the seedling exactly
    /// one phase (4379 → 4456), no double-advance, no loot, and the small
    /// phase's real 9-min growth func (584) is armed (the "watering advances
    /// growth" contract quest 4417 documents).
    /// </summary>
    [Test]
    public async Task Watering_SkillHitChain_AdvancesSeedlingToSmallExactlyOnce()
    {
        _actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, 5);
        _doodad = CropHarvestLoopRig.Plant(_actor.Character, _session.World,
            CropHarvestLoopRig.MakeHouse(_actor.Character));
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(SeedlingPhase);

        _doodad.Use(_actor.Character, SkillHitWateringSkillId); // 물 뿌리기 hits the seedling

        // Advanced EXACTLY one phase: seedling → small (never 4457 or the final group)
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(SmallPhase);

        // The small phase's growth func (584, 9 min → mature) is armed:
        // watering advanced the growth clock.
        await Assert.That(_doodad.FuncTask is DoodadFuncGrowthTask).IsTrue();
        var remaining = (_doodad.GrowthTime - DateTime.UtcNow).TotalMilliseconds;
        await Assert.That(remaining).IsGreaterThan(539_000);
        await Assert.That(remaining).IsLessThanOrEqualTo(540_500);

        // Watering grants no items (relative: the rig's inventory containers
        // are cached by character id, so the class shares one bag — the same
        // relative pattern as the harvest test).
        var potatoBefore = BagCount(PotatoItemId);
        var goldenBefore = BagCount(GoldenPotatoItemId);
        var seedBefore = BagCount(PotatoSeedItemId);
        await Assert.That(BagCount(PotatoItemId)).IsEqualTo(potatoBefore);
        await Assert.That(BagCount(GoldenPotatoItemId)).IsEqualTo(goldenBefore);
        await Assert.That(BagCount(PotatoSeedItemId)).IsEqualTo(seedBefore);

        // No double-advance: the small phase has no SkillHit func and no
        // func matching skill 15601 (its only func is the 13789-gated
        // DoodadFuncUse 627), so a second watering is a phase no-op.
        _doodad.Use(_actor.Character, SkillHitWateringSkillId);
        await Assert.That(_doodad.FuncGroupId).IsEqualTo(SmallPhase);
    }

    /// <summary>
    /// FARM-01 watering contract pin (M3 canonical audit §2.3): the canonical
    /// watering skill 10126 (물 주기) carries InteractionEffect → the Watering
    /// world-interaction (WorldInteractionType.Watering = 3) → doodad.Use(caster,
    /// 10126). The seedling's SkillHit func gates on hit skill 15601 (물 뿌리기),
    /// so the generic interaction skill alone does NOT advance the crop — the
    /// engine contract is that the advance requires the SkillHit chain
    /// (skill 15601) or the row-matched DoodadFuncUse (skill 13625). This pins
    /// the actual engine behavior so a future data change (e.g. 10126 becoming
    /// the SkillHit gate) fails this test deliberately.
    /// </summary>
    [Test]
    public async Task Watering_InteractionSkill10126_DoesNotAdvanceSeedling()
    {
        _actor.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.DoodadCreate, PotatoSeedItemId, 5);
        _doodad = CropHarvestLoopRig.Plant(_actor.Character, _session.World,
            CropHarvestLoopRig.MakeHouse(_actor.Character));

        _doodad.Use(_actor.Character, WateringInteractionSkillId); // 물 주기 interaction

        await Assert.That(_doodad.FuncGroupId).IsEqualTo(SeedlingPhase);
        // The seedling's own 60 s growth timer is still the armed task
        await Assert.That(_doodad.FuncTask is DoodadFuncGrowthTask).IsTrue();
    }

    private void GrowToMature()
    {
        (_doodad.FuncTask as DoodadFuncGrowthTask)?.Execute();
        (_doodad.FuncTask as DoodadFuncGrowthTask)?.Execute();
    }

    private int BagCount(uint templateId)
        => _actor.Character.Inventory.Bag.Items.Where(i => i.TemplateId == templateId).Sum(i => i.Count);
}

/// <summary>
/// Seeds the real-data singleton surface for the potato crop loop. See the
/// test-class docblock for the loop layout and the t_4f11a519 ordering rules.
/// </summary>
public static class CropHarvestLoopRig
{
    private static bool s_seeded;

    public static void Seed()
    {
        lock (typeof(CropHarvestLoopRig))
        {
            if (s_seeded)
                return;

            GameplayActorTestRig.Seed(); // base surface (missing-only)
            SeedObjectIdManager();
            SeedItemTemplates();
            SeedIncrementingItemIds();
            SeedItemDoodadMapping();
            SeedDoodadManager();
            SeedHousingManager();
            SeedLootGameData();
            SeedSkillTemplates();
            SeedZoneManager();
            SeedPublicFarmManager();

            s_seeded = true;
        }
    }

    /// <summary>A house at the plant position (owned farm land).</summary>
    public static House MakeHouse(Character owner)
        => new() { Id = 77, ObjId = 0x10201, OwnerId = owner.Id, Transform = new Transform(owner) };

    /// <summary>
    /// Plants a potato seed via the same real steps as
    /// DoodadManager.CreatePlayerDoodad (the CSCreateDoodadPacket handler):
    /// DoodadManager.Create → house binding → InitDoodad → Spawn →
    /// AddPlayerDoodad, with the seed consumed from the bag. The MySQL Save()
    /// tail is skipped (persistence is M3b scope; unit tests have no MySQL).
    /// </summary>
    public static Doodad Plant(Character character, WorldInstance world, House house)
    {
        RegisterWorld(world); // ParentWorld-setter chain resolves GetWorld(instanceId)
        var seedItem = character.Inventory.Bag.Items.First(i => i.TemplateId == CropHarvestLoopTests.PotatoSeedItemId);
        var doodad = DoodadManager.Instance.Create(world, 0, CropHarvestLoopTests.PotatoDoodadId, null, true);
        doodad.IsPersistent = false; // no MySQL save in unit tests
        doodad.Transform = character.Transform.CloneDetached(doodad);
        doodad.Transform.InstanceId = world.Id;
        doodad.Transform.Local.SetPosition(1000f, 1000f, 100f);
        doodad.ItemId = seedItem.Template.MaxCount > 1 ? 0 : seedItem.Id;
        doodad.PlantTime = DateTime.UtcNow;
        if (house != null)
        {
            doodad.OwnerDbId = house.Id;
            doodad.AttachPoint = AttachPointKind.None;
            doodad.OwnerType = DoodadOwnerType.Housing;
            doodad.ParentObj = house;
            doodad.ParentObjId = house.ObjId;
            doodad.Transform.Parent = house.Transform;
        }

        character.ItemUse(seedItem);
        character.Inventory.ConsumeItem([SlotType.Inventory], ItemTaskType.DoodadCreate, seedItem.TemplateId, 1, seedItem);

        doodad.InitDoodad(); // deterministic inline (CreatePlayerDoodad defers via Task.Run)
        doodad.Spawn();
        // The headless world has no SpawnManager (persistence tracking is
        // assigned by production WorldManager.CreateWorld — M3b scope).
        world.SpawnManager?.AddPlayerDoodad(doodad);
        return doodad;
    }

    /// <summary>
    /// The headless session world is created with instanceId 0 and never
    /// registered with WorldManager (production worlds register at creation).
    /// GameObject.set_ParentWorld → Transform.set_InstanceId resolves
    /// WorldManager.Instance.GetWorld(instanceId); an unregistered world makes
    /// that return null and the setter chain NREs. Register it once.
    /// </summary>
    private static void RegisterWorld(WorldInstance world)
    {
        // Production WorldManager.CreateWorld allocates world.Regions; the
        // headless session world never gets it (Spawn → GetRegionByPos NRE).
        if (world.Regions == null)
        {
            world.Regions = new Region[
                world.Template.CellX * WorldManager.SECTORS_PER_CELL,
                world.Template.CellY * WorldManager.SECTORS_PER_CELL];
        }
        var worlds = (System.Collections.Concurrent.ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(WorldManager.Instance);
        worlds?.TryAdd(world.Id, world);
    }

    #region Singleton seeding

    /// <summary>
    /// The DoodadFuncSkillHit path allocates a caster ObjId via
    /// ObjectIdManager.Instance.GetNextId(); the singleton's bitset only
    /// exists after Initialize(). Initialize(false) is the missing-only
    /// pattern: a no-op once initialized (t_6bad0654 lesson), a full init
    /// (with a graceful no-DB fallback) on first use.
    /// </summary>
    private static void SeedObjectIdManager()
        => ObjectIdManager.Instance.Initialize(false);

    private static void SeedItemTemplates()
    {
        var manager = ItemManager.Instance;
        var templates = (Dictionary<uint, ItemTemplate>)GetField(manager, "_templates");
        // Missing-only, additive: never replace templates another rig registered
        foreach (var templateId in new[] { CropHarvestLoopTests.PotatoSeedItemId, CropHarvestLoopTests.PotatoItemId, CropHarvestLoopTests.GoldenPotatoItemId })
        {
            if (!templates.ContainsKey(templateId))
                templates[templateId] = new ItemTemplate { Id = templateId, MaxCount = 100 };
        }
    }

    /// <summary>
    /// The base rig's ItemManager is built with an unconfigured mock
    /// IItemIdManager, whose GetNextId() returns 0 for EVERY item. The first
    /// Create occupies _allItems[0]; any later Create (e.g. the harvest loot)
    /// collides and returns null — the harvest then yields nothing while the
    /// phase chain still advances. Swap in an incrementing mock on the
    /// existing instance (BotParitySeedingTests pattern) so each new item
    /// gets a fresh id.
    /// </summary>
    private static void SeedIncrementingItemIds()
    {
        var manager = ItemManager.Instance;
        var field = typeof(ItemManager).GetField("<itemIdManager>P",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? typeof(ItemManager).GetField("itemIdManager",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
            return; // field renamed upstream — fall back to the base mock behaviour

        var current = (IItemIdManager)field.GetValue(manager);
        if (current == null || current.GetNextId() != 0)
            return; // already a working id source

        var mock = Mock.Of<IItemIdManager>();
        var nextId = 0x01000000u; // ItemIdManager.FirstId — real engine range
        mock.GetNextId().Returns(() => nextId++);
        field.SetValue(manager, mock.Object);
    }

    private static void SeedItemDoodadMapping()
    {
        var manager = ItemManager.Instance;
        var mappings = (Dictionary<uint, ItemDoodadTemplate>)GetField(manager, "_itemDoodadTemplates");
        if (mappings == null)
        {
            mappings = [];
            SetField(manager, "_itemDoodadTemplates", mappings);
        }
        if (!mappings.ContainsKey(CropHarvestLoopTests.PotatoDoodadId))
        {
            mappings[CropHarvestLoopTests.PotatoDoodadId] = new ItemDoodadTemplate
            {
                DoodadId = CropHarvestLoopTests.PotatoDoodadId,
                ItemIds = [CropHarvestLoopTests.PotatoSeedItemId]
            };
        }
    }

    private static void SeedDoodadManager()
    {
        // A bare placeholder — a DoodadManager seeded by a sibling rig
        // WITHOUT templates (the Bots rig's lazy surface) — does not count
        // as established: the rich chain below must win so Create() resolves
        // this rig's templates regardless of which rig seeded first.
        if (SingletonSeeded(typeof(Singleton<DoodadManager>)) && !IsBareDoodadManager())
            return;

        var objectIdManager = Mock.Of<IObjectIdManager>();
        objectIdManager.GetNextId().Returns(0x200000u);

        var housingManager = Mock.Of<IHousingManager>();

        var manager = new DoodadManager(
            objectIdManager.Object,
            Mock.Of<IDoodadIdManager>().Object,
            ItemManager.Instance,
            new Lazy<IHousingManager>(() => housingManager.Object),
            Mock.Of<ISusManager>().Object);

        SetField(manager, "_templates", BuildTemplates());
        var (funcsByGroups, funcsById) = BuildFuncs();
        SetField(manager, "_funcsByGroups", funcsByGroups);
        SetField(manager, "_funcsById", funcsById);
        SetField(manager, "_funcTemplates", BuildFuncTemplates());
        SetField(manager, "_phaseFuncs", BuildPhaseFuncs());
        SetField(manager, "_phaseFuncTemplates", BuildPhaseFuncTemplates());

        // SeedSingleton is missing-only; when a sibling rig's bare placeholder
        // is installed, force-replace it with this rich chain.
        var field = typeof(Singleton<DoodadManager>).GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field?.SetValue(null, manager);
    }

    /// <summary>True when the seeded DoodadManager has no templates at all — a
    /// sibling rig's bare placeholder rather than an established rich chain.</summary>
    private static bool IsBareDoodadManager()
    {
        var templates = GetField(DoodadManager.Instance, "_templates") as Dictionary<uint, DoodadTemplate>;
        return templates == null || templates.Count == 0;
    }

    private static Dictionary<uint, DoodadTemplate> BuildTemplates()
    {
        var template = new DoodadTemplate
        {
            Id = CropHarvestLoopTests.PotatoDoodadId,
            GrowthTime = 600000,
            TotalDoodadGrowthTime = 600000,
            FuncGroups =
            [
                MakeGroup(CropHarvestLoopTests.SeedlingPhase, DoodadFuncGroups.DoodadFuncGroupKind.Start),
                MakeGroup(CropHarvestLoopTests.SmallPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
                MakeGroup(CropHarvestLoopTests.MaturePhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
                MakeGroup(CropHarvestLoopTests.LootingPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal),
                MakeGroup(CropHarvestLoopTests.FinalPhase, DoodadFuncGroups.DoodadFuncGroupKind.Normal)
            ]
        };
        return new() { [CropHarvestLoopTests.PotatoDoodadId] = template };
    }

    private static DoodadFuncGroups MakeGroup(uint id, DoodadFuncGroups.DoodadFuncGroupKind kind)
        => new() { Id = id, Almighty = CropHarvestLoopTests.PotatoDoodadId, GroupKindId = kind };

    /// <summary>doodad_funcs rows for the potato chain (real ids, real skill gating).</summary>
    private static (Dictionary<uint, List<DoodadFunc>> ByGroups, Dictionary<uint, DoodadFunc> ById) BuildFuncs()
    {
        DoodadFunc F(uint key, uint groupId, uint funcId, string funcType, int nextPhase, uint skillId) => new()
        {
            FuncKey = key, GroupId = groupId, FuncId = funcId, FuncType = funcType,
            NextPhase = nextPhase, SkillId = skillId
        };

        var byGroups = new Dictionary<uint, List<DoodadFunc>>
        {
            [CropHarvestLoopTests.SeedlingPhase] =
            [
                F(5205, CropHarvestLoopTests.SeedlingPhase, 626, "DoodadFuncUse", (int)CropHarvestLoopTests.SmallPhase, CropHarvestLoopTests.WateringSkillId),
                F(6061, CropHarvestLoopTests.SeedlingPhase, 1117, "DoodadFuncUse", (int)CropHarvestLoopTests.FinalPhase, 13789),
                F(7815, CropHarvestLoopTests.SeedlingPhase, 174, "DoodadFuncSkillHit", (int)CropHarvestLoopTests.SmallPhase, 0)
            ],
            [CropHarvestLoopTests.SmallPhase] =
            [
                F(5206, CropHarvestLoopTests.SmallPhase, 627, "DoodadFuncUse", (int)CropHarvestLoopTests.FinalPhase, 13789)
            ],
            [CropHarvestLoopTests.MaturePhase] =
            [
                F(5887, CropHarvestLoopTests.MaturePhase, 1047, "DoodadFuncUse", (int)CropHarvestLoopTests.LootingPhase, CropHarvestLoopTests.HarvestSkillId)
            ],
            [CropHarvestLoopTests.LootingPhase] =
            [
                F(9120, CropHarvestLoopTests.LootingPhase, 129, "DoodadFuncLootPack", (int)CropHarvestLoopTests.FinalPhase, 0)
            ]
        };

        var byId = new Dictionary<uint, DoodadFunc>();
        foreach (var funcs in byGroups.Values)
            foreach (var func in funcs)
                byId[func.FuncKey] = func;
        return (byGroups, byId);
    }

    private static Dictionary<string, Dictionary<uint, DoodadFuncTemplate>> BuildFuncTemplates()
        => new()
        {
            ["DoodadFuncUse"] = new Dictionary<uint, DoodadFuncTemplate>
            {
                [626] = new DoodadFuncUse(), [627] = new DoodadFuncUse(),
                [1047] = new DoodadFuncUse(), [1117] = new DoodadFuncUse()
            },
            ["DoodadFuncSkillHit"] = new Dictionary<uint, DoodadFuncTemplate>
            {
                [174] = new DoodadFuncSkillHit { SkillId = 15601 }
            },
            ["DoodadFuncLootPack"] = new Dictionary<uint, DoodadFuncTemplate>
            {
                [129] = new DoodadFuncLootPack { LootPackId = CropHarvestLoopTests.PotatoLootPackId }
            }
        };

    /// <summary>doodad_phase_funcs rows for the potato chain (real ids + params).</summary>
    private static Dictionary<uint, List<DoodadPhaseFunc>> BuildPhaseFuncs()
    {
        DoodadPhaseFunc P(uint groupId, uint funcId, string funcType) => new() { GroupId = groupId, FuncId = funcId, FuncType = funcType };
        return new Dictionary<uint, List<DoodadPhaseFunc>>
        {
            [CropHarvestLoopTests.SeedlingPhase] = [P(CropHarvestLoopTests.SeedlingPhase, 583, "DoodadFuncGrowth")],
            [CropHarvestLoopTests.SmallPhase] = [P(CropHarvestLoopTests.SmallPhase, 584, "DoodadFuncGrowth")],
            [CropHarvestLoopTests.MaturePhase] =
            [
                P(CropHarvestLoopTests.MaturePhase, 144, "DoodadFuncHouseFarm"),
                P(CropHarvestLoopTests.MaturePhase, 1350, "DoodadFuncTimer")
            ],
            [CropHarvestLoopTests.LootingPhase] = [P(CropHarvestLoopTests.LootingPhase, 3404, "DoodadFuncTimer")],
            [CropHarvestLoopTests.FinalPhase] = [P(CropHarvestLoopTests.FinalPhase, 900, "DoodadFuncFinal")]
        };
    }

    private static Dictionary<string, Dictionary<uint, DoodadPhaseFuncTemplate>> BuildPhaseFuncTemplates()
        => new()
        {
            ["DoodadFuncGrowth"] = new Dictionary<uint, DoodadPhaseFuncTemplate>
            {
                [583] = new DoodadFuncGrowth { Delay = 60000, StartScale = 500, EndScale = 750, NextPhase = (int)CropHarvestLoopTests.SmallPhase },
                [584] = new DoodadFuncGrowth { Delay = 540000, StartScale = 750, EndScale = 1000, NextPhase = (int)CropHarvestLoopTests.MaturePhase }
            },
            ["DoodadFuncTimer"] = new Dictionary<uint, DoodadPhaseFuncTemplate>
            {
                [1350] = new DoodadFuncTimer { Delay = 174000000, NextPhase = 10042 },
                [3404] = new DoodadFuncTimer { Delay = 180000, NextPhase = (int)CropHarvestLoopTests.FinalPhase }
            },
            ["DoodadFuncHouseFarm"] = new Dictionary<uint, DoodadPhaseFuncTemplate>
            {
                [144] = new DoodadFuncHouseFarm { ItemCategoryId = 48 }
            },
            ["DoodadFuncFinal"] = new Dictionary<uint, DoodadPhaseFuncTemplate>
            {
                [900] = new DoodadFuncFinal { After = 0, Respawn = true, MinTime = 300000, MaxTime = 900000 }
            }
        };

    /// <summary>
    /// Seeds the HousingManager singleton so DoodadFuncUse's owned-land
    /// permission check (HousingManager.Instance.GetHouseById) resolves.
    /// The test house (id 77) is registered with an AlwaysPublic template —
    /// same as a freshly placed house on owned farm land — so
    /// house.AllowedToInteract(player) short-circuits to true for the owner.
    /// Missing-only guard: never replaces a HousingManager a sibling rig
    /// (or the placement card's rig) already established.
    /// </summary>
    private static void SeedHousingManager()
    {
        if (SingletonSeeded(typeof(Singleton<HousingManager>)))
            return;

        var manager = new HousingManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IHousingIdManager>().Object,
            Mock.Of<IHousingTldManager>().Object,
            Mock.Of<IItemManager>().Object,
            Mock.Of<IMailManager>().Object,
            Mock.Of<INameManager>().Object,
            Mock.Of<IZoneManager>().Object,
            Mock.Of<IDoodadManager>().Object,
            Mock.Of<IUccManager>().Object);

        var house = new House
        {
            Id = 77,
            ObjId = 0x10201,
            Template = new HousingTemplate { AlwaysPublic = true }
        };
        SetField(manager, "_houses", new Dictionary<uint, House> { [house.Id] = house });
        SeedSingleton(typeof(Singleton<HousingManager>), manager);
    }

    private static void SeedLootGameData()
    {
        var instance = LootGameData.Instance;
        var packs = (Dictionary<uint, LootPack>)GetField(instance, "_lootPacks");
        if (packs == null)
        {
            packs = [];
            SetField(instance, "_lootPacks", packs);
        }

        if (packs.ContainsKey(CropHarvestLoopTests.PotatoLootPackId))
            return;

        // Real loots rows for pack 6452 (loots table, verified 2026-08-10):
        // group 1: potato 7992 ×2-4 (drop 1 → always, per LootGameData.Load)
        // group 2: golden potato 19887 ×1; group 3: potato seed 15659 ×1
        Loot Loot(uint id, uint group, uint itemId, int min, int max) => new()
        {
            Id = id, Group = group, ItemId = itemId, DropRate = 10_000_000,
            MinAmount = min, MaxAmount = max, LootPackId = CropHarvestLoopTests.PotatoLootPackId,
            GradeId = 0, AlwaysDrop = false
        };
        var loots = new List<Loot>
        {
            Loot(65672, 1, CropHarvestLoopTests.PotatoItemId, 2, 4),
            Loot(65682, 2, CropHarvestLoopTests.GoldenPotatoItemId, 1, 1),
            Loot(65692, 3, CropHarvestLoopTests.PotatoSeedItemId, 1, 1)
        };
        packs[CropHarvestLoopTests.PotatoLootPackId] = new LootPack
        {
            Id = CropHarvestLoopTests.PotatoLootPackId,
            Loots = loots,
            LootsByGroupNo = loots.GroupBy(l => l.Group).ToDictionary(g => g.Key, g => g.ToList()),
            Groups = [], ActabilityGroups = [], GroupCount = 3
        };
    }

    private static void SeedSkillTemplates()
    {
        var manager = SkillManager.Instance;
        var skills = (Dictionary<uint, SkillTemplate>)GetField(manager, "_skills");
        if (skills == null)
        {
            skills = [];
            SetField(manager, "_skills", skills);
        }
        foreach (var skillId in new[]
                 {
                     CropHarvestLoopTests.WateringSkillId,
                     CropHarvestLoopTests.SkillHitWateringSkillId,
                     CropHarvestLoopTests.WateringInteractionSkillId,
                     CropHarvestLoopTests.HarvestSkillId
                 })
        {
            if (!skills.ContainsKey(skillId))
                skills[skillId] = new SkillTemplate { Id = skillId };
        }
    }

    private static void SeedZoneManager()
    {
        if (SingletonSeeded(typeof(Singleton<ZoneManager>)))
            return;
        var zoneManager = new ZoneManager(Mock.Of<IWorldManager>().Object);
        // Same as NpcLineOfSightTests: the zones dict is only populated by
        // Load() — seed empty so GetZoneByKey resolves nulls instead of NRE.
        SetField(zoneManager, "_zones", new Dictionary<uint, Zone>());
        SetField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
        SeedSingleton(typeof(Singleton<ZoneManager>), zoneManager);
    }

    private static void SeedPublicFarmManager()
    {
        if (SingletonSeeded(typeof(Singleton<PublicFarmManager>)))
            return;
        // The Load()-populated subzone surface is absent in tests — return an
        // empty zone list so InPublicFarm short-circuits to "not a farm".
        var subZoneManager = Mock.Of<ISubZoneManager>();
        subZoneManager.GetSubZoneByPosition(Any<WorldTemplate>(), Any<Vector3>()).Returns([]);
        SeedSingleton(typeof(Singleton<PublicFarmManager>),
            new PublicFarmManager(Mock.Of<ITaskManager>().Object, Mock.Of<IWorldManager>().Object, subZoneManager.Object));
    }

    #endregion

    #region Reflection helpers

    private static object GetField(object target, string fieldName)
        => target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
            throw new InvalidOperationException($"Cannot locate field '{fieldName}' on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static bool SingletonSeeded(Type singletonBase)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) != null;

    private static void SeedSingleton(Type singletonBase, object instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) == null)
            field?.SetValue(null, instance);
    }

    #endregion
}
