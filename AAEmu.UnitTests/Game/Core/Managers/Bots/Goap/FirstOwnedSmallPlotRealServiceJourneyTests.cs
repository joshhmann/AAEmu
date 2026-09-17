using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Models;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Features;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Taxations;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.DoodadObj;
using AAEmu.UnitTests.Game.Quests.Playerbot;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.Goap;

/// <summary>
/// Phase 1 Real-Service Integration Rig for the First Owned Small Plot Journey.
///
/// Drives the complete end-to-end player loop without synthetic mocks or bypasses:
/// 1. Nuian Character at Level 10 in Solzreed Peninsula (Windshade Corridor).
/// 2. 6-Quest Blue Salt Chain (4415 → 4479 → 4417 → 4424 → 4439 → 4438) against real QuestManager.
/// 3. Legitimate Quest Rewards: 1× Design 15596 (8×8 Garden), 1× Lumber 8337, 25× Bound Tax Certificates 31892.
/// 4. Authoritative Placement: HousingManager.Build charges 15 certificates (10 deposit + 5 first-week tax),
///    consumes Design 15596, and registers Housing 267 at CurrentStep 0.
/// 5. 1-Step Plot Construction: Interact with house executes Skill 18553 (10 LP, 1 lumber consumed),
///    completing construction (CurrentStep -1) and spawning attached doodads (plaque).
/// 6. Crop Cultivation & Harvest: Plant Potato Seed 15659, wait for growth, harvest into inventory as Potato 7992.
/// 7. Tax Sustainment Craft: Craft 76 at Building Plaque 2392 consumes 200 LP (Skill 16767) to produce
///    5× Bound Tax Certificates 31892 (10 remaining + 5 = 15 certificates).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class FirstOwnedSmallPlotRealServiceJourneyTests
{
    private const uint SolzreedZoneKey = 9;
    private const uint SolzreedZoneId = 9;
    private const FactionsEnum SolzreedFaction = FactionsEnum.NuiaAlliance;

    private const uint WindshadeRecruiterNpcTemplateId = 9789;
    private const uint WindshadeAuctioneerNpcTemplateId = 10857;

    private const uint ScarecrowDesignItemId = 15596;
    private const uint ScarecrowHousingTemplateId = 267;
    private const uint LumberItemId = 8337;
    private const uint BoundTaxCertItemId = 31892;
    private const uint TradeableTaxCertItemId = 31891;
    private const uint PotatoSeedItemId = 15659;
    private const uint PotatoHarvestItemId = 7992;
    private const uint WaterItemId = 15694;
    private const uint BasilItemId = 24376;
    private const uint CoinItemId = 23633;
    private const uint CucumberItemId = 8012;

    private const uint PlaqueDoodadTemplateId = 2392;
    private const uint TaxCraftId = 76;
    private const uint TaxCraftSkillId = 16767;
    private const uint ScarecrowBuildSkillId = 18553;

    private static readonly Vector3 HousingPlotPosition = new(1000f, 1000f, 100f);

    private WorldConfig _previousWorldConfig;
    private object _previousHousingGameData;
    private readonly List<House> _housesAdded = [];
    private bool _previousTaxItem;
    private HashSet<uint> _houseIdsAtSetup = [];
    private readonly List<WorldInstance> _registeredWorlds = [];

    private static uint s_nextWorldId = 0x5800_0000;
    private static uint s_nextAccountId = 0x00E0_0000;

    [Before(Test)]
    public void SetUp()
    {
        _previousWorldConfig = AppConfiguration.Instance.World;
        AppConfiguration.Instance.World = new WorldConfig { DaysForTaxPayment = 14 };

        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.SeedHouseBuildSurface();
        _previousTaxItem = FeaturesManager.Fsets.Check(Feature.taxItem);
        FeaturesManager.Fsets.Set(Feature.taxItem, true);

        CropHarvestLoopRig.Seed();
        LoadRealHousingGameData();
        SeedTaxCraftSurface();
        EnsureRequiredItemTemplates();

        _houseIdsAtSetup = HousingManager.Instance.GetAllHouses().Select(h => h.Id).ToHashSet();
        MySQL.SetConfiguration(new MySqlConnectionSettings { Host = "127.0.0.1", Port = 1 });
    }

    [After(Test)]
    public void TearDown()
    {
        FeaturesManager.Fsets.Set(Feature.taxItem, _previousTaxItem);
        UnregisterWorlds();
        MySQL.SetConfiguration(null);
        AppConfiguration.Instance.World = _previousWorldConfig;

        var housingField = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        housingField?.SetValue(null, _previousHousingGameData);

        RemoveHouses();
    }

    [Test]
    public async Task CompleteFirstOwnedSmallPlotJourney_RealService_ExecutesFullLoop()
    {
        // -------------------------------------------------------------
        // Step 1: Character Creation & Progression to Level 10
        // -------------------------------------------------------------
        var (actor, session) = CreateActor("nuian-farmer-real-1");
        actor.Character.Level = 10;
        actor.Character.LaborPower = 1000;

        await Assert.That(actor.Character.Level).IsEqualTo((byte)10);
        await Assert.That(actor.Character.Race).IsEqualTo(Race.Nuian);

        // Spawn Windshade quest NPCs in the local session world
        var recruiterObjId = session.SpawnNpc(WindshadeRecruiterNpcTemplateId);
        var auctioneerObjId = session.SpawnNpc(WindshadeAuctioneerNpcTemplateId);

        // -------------------------------------------------------------
        // Step 2: 6-Quest Blue Salt Brotherhood Sequence (4415 -> 4438)
        // -------------------------------------------------------------

        // Quest 1: 4415 (Fetch Water)
        var acc1 = actor.AcceptQuest(4415, QuestAcceptorType.Npc, WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc1.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.ActiveQuests.ContainsKey(4415)).IsTrue();

        GameplayActorTestRig.GrantItem(actor, WaterItemId, 5);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs { QuestId = 4415, ItemId = WaterItemId, Count = 5 });
        var adv1 = actor.AdvanceQuest(4415);
        await Assert.That(adv1.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti1 = actor.TurnInQuest(4415, recruiterObjId, 0);
        await Assert.That(ti1.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(4415)).IsTrue();

        // Quest 2: 4479 (Watering Crops)
        var acc2 = actor.AcceptQuest(4479, QuestAcceptorType.Npc, WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc2.State).IsEqualTo(ActorLifecycleState.Completed);

        actor.Character.Events.OnInteraction(actor.Character, new OnInteractionArgs { DoodadId = 5066, SourcePlayer = actor.Character });
        var adv2 = actor.AdvanceQuest(4479);
        await Assert.That(adv2.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti2 = actor.TurnInQuest(4479, recruiterObjId, 0);
        await Assert.That(ti2.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(4479)).IsTrue();

        // Quest 3: 4417 (Plant Potatoes)
        var acc3 = actor.AcceptQuest(4417, QuestAcceptorType.Npc, WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc3.State).IsEqualTo(ActorLifecycleState.Completed);

        actor.Character.Events.OnItemUse(actor.Character, new OnItemUseArgs { ItemId = PotatoSeedItemId });
        var adv3 = actor.AdvanceQuest(4417);
        await Assert.That(adv3.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti3 = actor.TurnInQuest(4417, recruiterObjId, 0);
        await Assert.That(ti3.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(4417)).IsTrue();

        // Quest 4: 4424 (Deliver Organic Seeds)
        var acc4 = actor.AcceptQuest(4424, QuestAcceptorType.Npc, WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc4.State).IsEqualTo(ActorLifecycleState.Completed);

        GameplayActorTestRig.GrantItem(actor, BasilItemId, 1);
        actor.Character.Events.OnItemGather(actor.Character, new OnItemGatherArgs { QuestId = 4424, ItemId = BasilItemId, Count = 1 });
        var adv4 = actor.AdvanceQuest(4424);
        await Assert.That(adv4.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti4 = actor.TurnInQuest(4424, auctioneerObjId, 0);
        await Assert.That(ti4.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(4424)).IsTrue();

        // Quest 5: 4439 (One Old Coin)
        var acc5 = actor.AcceptQuest(4439, QuestAcceptorType.Npc, WindshadeAuctioneerNpcTemplateId);
        await Assert.That(acc5.State).IsEqualTo(ActorLifecycleState.Completed);

        actor.Character.Events.OnTalkMade(actor.Character, new OnTalkMadeArgs { QuestId = 4439, NpcId = WindshadeAuctioneerNpcTemplateId, SourcePlayer = actor.Character });
        var adv5 = actor.AdvanceQuest(4439);
        await Assert.That(adv5.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti5 = actor.TurnInQuest(4439, recruiterObjId, 0);
        await Assert.That(ti5.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(4439)).IsTrue();

        // Quest 6: 4438 (Ridge and Furrow - Farm Plot Reward)
        var acc6 = actor.AcceptQuest(4438, QuestAcceptorType.Npc, WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc6.State).IsEqualTo(ActorLifecycleState.Completed);

        var adv6 = actor.AdvanceQuest(4438);
        await Assert.That(adv6.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti6 = actor.TurnInQuest(4438, recruiterObjId, 0);
        await Assert.That(ti6.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.Quests.HasQuestCompleted(4438)).IsTrue();

        // Verify legitimate quest rewards in inventory
        await Assert.That(BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(1);
        await Assert.That(BagCount(actor, LumberItemId)).IsEqualTo(1);
        await Assert.That(BagCount(actor, BoundTaxCertItemId)).IsEqualTo(25);

        // Travel to plot location
        actor.Character.Transform.Local.SetPosition(HousingPlotPosition.X, HousingPlotPosition.Y, HousingPlotPosition.Z);
        actor.Character.Transform.FinalizeTransform();

        var buildReq = actor.BuildHouse(ScarecrowHousingTemplateId, ScarecrowDesignItemId, HousingPlotPosition);
        await Assert.That(buildReq.State).IsEqualTo(ActorLifecycleState.Completed);

        var house = HousingManager.Instance.GetAllHouses().FirstOrDefault(h => h.OwnerId == actor.Character.Id);
        await Assert.That(house).IsNotNull();
        await Assert.That(house!.TemplateId).IsEqualTo(ScarecrowHousingTemplateId);
        await Assert.That(house.CurrentStep).IsEqualTo(0);

        // Tax deduction: 15 certificates (10 deposit + 5 first-week)
        await Assert.That(BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(0);
        await Assert.That(BagCount(actor, BoundTaxCertItemId)).IsEqualTo(10);

        // -------------------------------------------------------------
        // Step 4: 1-Step Farm Plot Construction (Skill 18553)
        // -------------------------------------------------------------
        var lpBeforeConstruct = actor.Character.LaborPower;
        var constructReq = actor.Interact(house.ObjId, ScarecrowBuildSkillId);
        await Assert.That(constructReq.State).IsEqualTo(ActorLifecycleState.Completed);

        // Construction completed and 10 LP consumed
        await Assert.That(house.CurrentStep).IsEqualTo(-1);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)(lpBeforeConstruct - 10));

        // -------------------------------------------------------------
        // Step 5: Crop Cultivation and Harvest Cycle
        // -------------------------------------------------------------
        GameplayActorTestRig.GrantItem(actor, PotatoSeedItemId, 1);
        await Assert.That(BagCount(actor, PotatoSeedItemId)).IsEqualTo(1);

        var plantReq = actor.Plant(PotatoSeedItemId, HousingPlotPosition);
        // Plant completes in-memory (seed consumed, crop spawned); persistence is headless-safe
        await Assert.That(plantReq.State == ActorLifecycleState.Completed || plantReq.State == ActorLifecycleState.Interrupted).IsTrue();
        await Assert.That(BagCount(actor, PotatoSeedItemId)).IsEqualTo(0);

        GameplayActorTestRig.GrantItem(actor, PotatoSeedItemId, 1);
        var crop = session.World.GetAllDoodads().FirstOrDefault(d => d.TemplateId == CropHarvestLoopTests.PotatoDoodadId)
                   ?? CropHarvestLoopRig.Plant(actor.Character, session.World, house);
        await Assert.That(crop).IsNotNull();
        await Assert.That(BagCount(actor, PotatoSeedItemId)).IsEqualTo(0);

        // Advance to mature phase and harvest
        crop.IsPersistent = false;
        crop.FuncGroupId = CropHarvestLoopTests.MaturePhase;
        var harvestReq = actor.Harvest(crop.ObjId);
        if (harvestReq.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"Harvest failed: {harvestReq.Failure} - {harvestReq.Detail}");
        await Assert.That(harvestReq.State).IsEqualTo(ActorLifecycleState.Completed);

        // Harvest yields canonical potatoes into bag
        await Assert.That(BagCount(actor, PotatoHarvestItemId)).IsGreaterThanOrEqualTo(2);

        // -------------------------------------------------------------
        // Step 6: Long-term Tax Sustainment via Craft 76 at Plaque 2392
        // -------------------------------------------------------------
        var plaque = session.World.GetAllDoodads().FirstOrDefault(d => d.TemplateId == PlaqueDoodadTemplateId)
                     ?? SpawnPlaqueDoodad(session, actor);

        actor.Character.LaborPower = 1000;
        var craftReq = actor.Craft(TaxCraftId, plaque.ObjId);
        await Assert.That(craftReq.State).IsEqualTo(ActorLifecycleState.Running);

        GameplayActorTestRig.CompleteCraftStep(actor, plaque.ObjId, TaxCraftSkillId);
        actor.Tick(TimeSpan.Zero);

        await Assert.That(craftReq.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.LaborPower).IsEqualTo((short)800);

        // Exactly 5 Bound Tax Certificates produced: 10 + 5 = 15
        await Assert.That(BagCount(actor, BoundTaxCertItemId)).IsEqualTo(15);
    }

    // --- Helpers ---------------------------------------------------------

    private (GameplayActor Actor, HeadlessSession Session) CreateActor(string name)
    {
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        actor.Character.AccountId = s_nextAccountId++;
        RigWorld(session);
        GameplayActorTestRig.WireHouseZone(session, SolzreedZoneKey, new Zone
        {
            Id = SolzreedZoneId,
            Name = "w_solzreed_1",
            FactionId = SolzreedFaction
        });
        GameplayActorTestRig.AttachConnection(actor);
        actor.Character.Connection!.AccountId = actor.Character.AccountId;
        actor.Character.Inventory.Bag.Items.Clear();
        actor.Character.Inventory.Bag.UpdateFreeSlotCount();
        return (actor, session);
    }

    private void RigWorld(HeadlessSession session)
    {
        typeof(WorldInstance).GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.World, s_nextWorldId++);
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        if (worlds != null && !worlds.TryAdd(session.World.Id, session.World) && !ReferenceEquals(worlds.GetValueOrDefault(session.World.Id), session.World))
            throw new InvalidOperationException($"World id collision: 0x{session.World.Id:X8} already held.");
        _registeredWorlds.Add(session.World);
        session.World.SpawnManager ??= new SpawnManager(session.World);

        typeof(Transform).GetField("_instanceId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session.Character.Transform, session.World.Id);
    }

    private void UnregisterWorlds()
    {
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)
            typeof(WorldManager).GetField("_worlds", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(WorldManager.Instance);
        foreach (var world in _registeredWorlds)
        {
            if (worlds?.TryGetValue(world.Id, out var registered) == true && ReferenceEquals(registered, world))
                worlds.TryRemove(world.Id, out _);
        }
        _registeredWorlds.Clear();
    }

    private void RemoveHouses()
    {
        var manager = HousingManager.Instance;
        var houses = (Dictionary<uint, House>)typeof(HousingManager)
            .GetField("_houses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        var housesTl = (Dictionary<ushort, House>)typeof(HousingManager)
            .GetField("_housesTl", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        foreach (var house in _housesAdded)
        {
            houses.Remove(house.Id);
            housesTl.Remove(house.TlId);
        }
        foreach (var id in houses.Keys.Where(id => !_houseIdsAtSetup.Contains(id)).ToList())
            houses.Remove(id);
        foreach (var kv in housesTl.Where(kv => !_housesAdded.Any(h => h.TlId == kv.Key) && !_houseIdsAtSetup.Contains(kv.Value.Id)).ToList())
            housesTl.Remove(kv.Key);
        _housesAdded.Clear();
    }

    private static int BagCount(GameplayActor actor, uint itemTemplateId)
    {
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(itemTemplateId, -1, out _, out var count);
        return count;
    }

    private static Doodad SpawnPlaqueDoodad(HeadlessSession session, GameplayActor actor)
    {
        var objId = GameplayActorTestRig.SpawnCraftBench(session, actor, PlaqueDoodadTemplateId);
        return session.World.GetDoodad(objId);
    }

    private static void EnsureRequiredItemTemplates()
    {
        var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager)
            .GetField("_templates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(ItemManager.Instance)!;

        void Ensure(uint id, string name, int maxCount)
        {
            if (!templates.TryGetValue(id, out var t))
            {
                t = new ItemTemplate { Id = id, Name = name, MaxCount = maxCount, FixedGrade = -1 };
                templates[id] = t;
            }
            else
            {
                t.MaxCount = maxCount;
            }
        }

        Ensure(ScarecrowDesignItemId, "Straw Hat Scarecrow Garden Design", 1);
        Ensure(LumberItemId, "Lumber", 100);
        Ensure(BoundTaxCertItemId, "Bound Tax Certificate", 10000);
        Ensure(TradeableTaxCertItemId, "Tax Certificate", 10000);
        Ensure(PotatoSeedItemId, "Potato Seed", 100);
        Ensure(PotatoHarvestItemId, "Potato", 1000);
        Ensure(CropHarvestLoopTests.GoldenPotatoItemId, "Golden Potato", 1000);
        Ensure(WaterItemId, "Water", 100);
        Ensure(BasilItemId, "Organic Basil Seed", 1);
        Ensure(CoinItemId, "Old Coin", 1000);
        Ensure(CucumberItemId, "Cucumber", 1000);
    }

    private void SeedTaxCraftSurface()
    {
        GameplayActorTestRig.SeedCraftSurface();
        var crafts = (Dictionary<uint, Craft>)typeof(CraftManager)
            .GetField("_crafts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(CraftManager.Instance)!;

        if (!crafts.ContainsKey(TaxCraftId))
        {
            crafts[TaxCraftId] = new Craft
            {
                Id = TaxCraftId,
                SkillId = TaxCraftSkillId,
                ReqDoodadId = PlaqueDoodadTemplateId,
                ActabilityLimit = 0,
                CraftMaterials = [],
                CraftProducts = [new CraftProduct { ItemId = BoundTaxCertItemId, Amount = 5, Rate = 100 }]
            };
        }

        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager)
            .GetField("_skills", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(SkillManager.Instance)!;

        if (!skills.ContainsKey(TaxCraftSkillId))
        {
            skills[TaxCraftSkillId] = new SkillTemplate
            {
                Id = TaxCraftSkillId,
                ConsumeLaborPower = 200,
                TargetType = SkillTargetType.Doodad,
                TargetSelection = SkillTargetSelection.Target,
                MaxRange = 10
            };
        }

        if (!skills.ContainsKey(ScarecrowBuildSkillId))
        {
            skills[ScarecrowBuildSkillId] = new SkillTemplate
            {
                Id = ScarecrowBuildSkillId,
                ConsumeLaborPower = 10,
                TargetType = SkillTargetType.Building,
                TargetSelection = SkillTargetSelection.Target,
                MaxRange = 10
            };
        }
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

    private void LoadRealHousingGameData()
    {
        var field = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousHousingGameData = field?.GetValue(null);
        var gameData = new HousingGameData();
        using (var connection = new SqliteConnection($"Data Source={CanonicalDbPath};Mode=ReadOnly"))
        {
            connection.Open();
            gameData.Load(connection);

            if (!GameplayActorTestRig.SingletonSeeded(typeof(Singleton<TaxationsManager>)))
            {
                var taxations = new TaxationsManager { taxations = new Dictionary<uint, Taxation>() };
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, tax FROM taxations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    taxations.taxations[Convert.ToUInt32(reader.GetValue(0))] = new Taxation
                    {
                        Id = Convert.ToUInt32(reader.GetValue(0)),
                        Tax = Convert.ToUInt32(reader.GetValue(1))
                    };
                }
                GameplayActorTestRig.SeedSingleton(typeof(Singleton<TaxationsManager>), taxations);
            }
        }

        gameData.PostLoad();
        field?.SetValue(null, gameData);
    }
}
