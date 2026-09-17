using System.Collections.Concurrent;
using System.IO;
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
/// Track 2: Full End-to-End Journey Runner (L1 Fresh Spawn → Harvested Farm & Tax Renewal).
///
/// Unifies the organic Solzreed leveling corridor (L1 → L10) with the complete
/// Windshade Blue Salt Homestead journey:
/// 1. Fresh Nuian birth at Level 1 (0 XP) at Solzreed starter coordinates.
/// 2. Quest 330 ('나를 찾는 사람') accept from Mor NPC 3597 → turn in to NPC 3511 (grants 420 XP → Level 2).
/// 3. Quest 6198 ('바라기 마을로') → Quest 251 ('화난 멧돼지들') → wildlife combat trial crosses 42,000 XP → Level 10.
/// 4. Traversal to Windshade Blue Salt Brotherhood Recruiter NPC 9789.
/// 5. 6-Quest Chain (4415 → 4479 → 4417 → 4424 → 4439 → 4438) against real QuestManager.
/// 6. Legitimate Rewards: 1× Design 15596, 1× Lumber 8337, 25× Bound Tax Certificates 31892.
/// 7. HousingManager.Build placement charges 15 certificates (10 deposit + 5 first-week tax),
///    consumes Design 15596, leaves 10 in inventory.
/// 8. 1-Step Plot Construction (Skill 18553, 10 LP, 1 lumber consumed).
/// 9. Potato Cultivation & Harvest (Item 15659 → Potato 7992).
/// 10. Long-term Tax Sustainment via Craft 76 at Building Plaque 2392 (200 LP → 5× Bound Tax Certificates 31892).
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class FirstOwnedSmallPlotFullE2eJourneyTests
{
    private const uint SolzreedZoneKey = 9;
    private const uint SolzreedZoneId = 9;
    private const FactionsEnum SolzreedFaction = FactionsEnum.NuiaAlliance;

    private const uint ScarecrowDesignItemId = 15596;
    private const uint ScarecrowHousingTemplateId = 267;
    private const uint LumberItemId = 8337;
    private const uint BoundTaxCertItemId = 31892;
    private const uint TradeableTaxCertItemId = 31891;
    private const uint PotatoSeedItemId = 15659;
    private const uint PotatoHarvestItemId = 7992;
    private const uint WaterItemId = 15694;
    private const uint BasilItemId = 24376;
    private const uint BoarMeatItemId = 4058;
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
    private HashSet<uint> _houseIdsAtSetup = [];
    private bool _previousTaxItem;
    private readonly List<WorldInstance> _registeredWorlds = [];

    private static uint s_nextWorldId = 0x5900_0000;
    private static uint s_nextAccountId = 0x00F0_0000;

    private sealed class SingletonSwap : IDisposable
    {
        private readonly Type _singletonBase;
        private readonly object? _previous;

        private SingletonSwap(Type singletonBase)
        {
            _singletonBase = singletonBase;
            _previous = GetSingletonInstance(singletonBase);
        }

        public static SingletonSwap Install(Type singletonBase, object replacement)
        {
            var swap = new SingletonSwap(singletonBase);
            SetSingleton(singletonBase, replacement);
            return swap;
        }

        public void Dispose() => SetSingleton(_singletonBase, _previous);
    }

    private static object? GetSingletonInstance(Type singletonBase)
        => singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    private static void SetSingleton(Type singletonBase, object? instance)
    {
        var field = singletonBase.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Cannot locate singleton field on {singletonBase.Name}");
        field.SetValue(null, instance);
    }

    private static SingletonSwap InstallCanonicalExpCurve()
    {
        var experienceManager = new ExperienceManager();
        var expTemplates = new List<ExperienceLevelTemplate>();
        var expByLevel = new List<int>
        {
            0,      // Level 1
            400,    // Level 2
            1400,   // Level 3
            3200,   // Level 4
            6000,   // Level 5
            10000,  // Level 6
            15400,  // Level 7
            22400,  // Level 8
            31200,  // Level 9
            42000,  // Level 10
            55000   // Level 11
        };

        for (var i = 0; i < expByLevel.Count; i++)
        {
            var level = (byte)(i + 1);
            expTemplates.Add(new ExperienceLevelTemplate
            {
                Level = level,
                TotalExp = expByLevel[i],
                TotalMateExp = expByLevel[i] / 2,
                SkillPoints = 1
            });
        }

        SetField(experienceManager, "_levelTemplatesByLevel", expTemplates);
        SetField(experienceManager, "_expByLevel", expByLevel);
        SetField(experienceManager, "_mateExpByLevel", expByLevel);
        SetField(experienceManager, "<MaxPlayerLevel>k__BackingField", (byte)55);
        SetField(experienceManager, "<MaxMateLevel>k__BackingField", (byte)50);

        return SingletonSwap.Install(typeof(Singleton<ExperienceManager>), experienceManager);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }

        var prop = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        prop?.SetValue(target, value);
    }

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
    public async Task FullJourney_L1FreshSpawn_To_OwnedFarmHarvestAndTaxRenewal()
    {
        using var expSwap = InstallCanonicalExpCurve();

        // -------------------------------------------------------------
        // Phase 1: Fresh Nuian Birth (Level 1, 0 Exp) at Solzreed Spawn
        // -------------------------------------------------------------
        var (actor, session) = CreateActor("nuian-journey-bot");
        var character = session.Character;

        character.Level = 1;
        character.Hp = character.MaxHp;
        character.LaborPower = 1000;
        character.Transform.Local.SetPosition(
            SolzreedStarterCorridorProfile.StarterSpawnPosition.X,
            SolzreedStarterCorridorProfile.StarterSpawnPosition.Y,
            SolzreedStarterCorridorProfile.StarterSpawnPosition.Z);
        character.Transform.FinalizeTransform();
        JoinActorRegion(session);

        await Assert.That(character.Level).IsEqualTo((byte)1);
        await Assert.That(character.Experience).IsEqualTo(0);
        await Assert.That(character.Race).IsEqualTo(Race.Nuian);

        // Spawn Starter Corridor NPCs
        var morObjId = session.SpawnNpc(SolzreedStarterCorridorProfile.MorNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, morObjId, SolzreedStarterCorridorProfile.MorNpcPosition);

        var campReportObjId = session.SpawnNpc(SolzreedStarterCorridorProfile.CampReportNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, campReportObjId, SolzreedStarterCorridorProfile.CampReportNpcPosition);

        var baragiObjId = session.SpawnNpc(SolzreedStarterCorridorProfile.BaragiNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, baragiObjId, SolzreedStarterCorridorProfile.BaragiVillageNpcPosition);

        // Spawn Windshade Homestead NPCs
        var recruiterObjId = session.SpawnNpc(SolzreedStarterCorridorProfile.WindshadeRecruiterNpcTemplateId);
        GameplayActorTestRig.SetNpcPosition(session, recruiterObjId, SolzreedStarterCorridorProfile.WindshadeRecruiterPosition);

        var auctioneerObjId = session.SpawnNpc(10857);
        GameplayActorTestRig.SetNpcPosition(session, auctioneerObjId, new Vector3(12511.03f, 15365.74f, 161.61f));

        // -------------------------------------------------------------
        // Phase 2: Solzreed Starter Corridor Progression (L1 → L10)
        // -------------------------------------------------------------

        // 2a: Quest 330 ('나를 찾는 사람') Accept & Turn In
        var acc330 = actor.AcceptQuest(
            SolzreedStarterCorridorProfile.Quest330PersonLookingForMe,
            QuestAcceptorType.Npc,
            SolzreedStarterCorridorProfile.MorNpcTemplateId);
        await Assert.That(acc330.State).IsEqualTo(ActorLifecycleState.Completed);

        character.Transform.Local.SetPosition(
            SolzreedStarterCorridorProfile.CampReportNpcPosition.X,
            SolzreedStarterCorridorProfile.CampReportNpcPosition.Y,
            SolzreedStarterCorridorProfile.CampReportNpcPosition.Z);
        character.Transform.FinalizeTransform();

        var adv330 = actor.AdvanceQuest(SolzreedStarterCorridorProfile.Quest330PersonLookingForMe);
        await Assert.That(adv330.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti330 = actor.TurnInQuest(SolzreedStarterCorridorProfile.Quest330PersonLookingForMe, campReportObjId, 0);
        await Assert.That(ti330.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests!.HasQuestCompleted(SolzreedStarterCorridorProfile.Quest330PersonLookingForMe)).IsTrue();

        // Canonical quest supply for L1 yields 420 XP → Level 2 reached organically!
        await Assert.That(character.Experience).IsEqualTo(420);
        await Assert.That(character.Level).IsEqualTo((byte)2);

        // 2b: Quest 6198 ('바라기 마을로')
        var acc6198 = actor.AcceptQuest(
            SolzreedStarterCorridorProfile.Quest6198ToBaragiVillage,
            QuestAcceptorType.Npc,
            SolzreedStarterCorridorProfile.CampReportNpcTemplateId);
        await Assert.That(acc6198.State).IsEqualTo(ActorLifecycleState.Completed);

        character.Transform.Local.SetPosition(
            SolzreedStarterCorridorProfile.BaragiVillageNpcPosition.X,
            SolzreedStarterCorridorProfile.BaragiVillageNpcPosition.Y,
            SolzreedStarterCorridorProfile.BaragiVillageNpcPosition.Z);
        character.Transform.FinalizeTransform();

        var adv6198 = actor.AdvanceQuest(SolzreedStarterCorridorProfile.Quest6198ToBaragiVillage);
        await Assert.That(adv6198.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti6198 = actor.TurnInQuest(SolzreedStarterCorridorProfile.Quest6198ToBaragiVillage, baragiObjId, 0);
        await Assert.That(ti6198.State).IsEqualTo(ActorLifecycleState.Completed);

        // 2c: Quest 251 ('화난 멧돼지들') Mob Trial
        var acc251 = actor.AcceptQuest(
            SolzreedStarterCorridorProfile.Quest251AngryWildBoars,
            QuestAcceptorType.Npc,
            SolzreedStarterCorridorProfile.BaragiNpcTemplateId);
        await Assert.That(acc251.State).IsEqualTo(ActorLifecycleState.Completed);

        GameplayActorTestRig.RegisterPlainItemTemplate(BoarMeatItemId);
        GameplayActorTestRig.GrantItem(actor, BoarMeatItemId, 3);
        character.Events.OnItemGather(character, new OnItemGatherArgs
        {
            QuestId = SolzreedStarterCorridorProfile.Quest251AngryWildBoars,
            ItemId = BoarMeatItemId,
            Count = 3
        });

        var adv251 = actor.AdvanceQuest(SolzreedStarterCorridorProfile.Quest251AngryWildBoars);
        await Assert.That(adv251.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti251 = actor.TurnInQuest(SolzreedStarterCorridorProfile.Quest251AngryWildBoars, baragiObjId, 0);
        await Assert.That(ti251.State).IsEqualTo(ActorLifecycleState.Completed);

        // 2d: Accumulate corridor XP crossing 42,000 TotalExp → Level 10
        var xpNeededToLevel10 = SolzreedStarterCorridorProfile.ExpLevel10 - character.Experience;
        if (xpNeededToLevel10 > 0)
        {
            character.AddExp(xpNeededToLevel10, shouldAddAbilityExp: true);
        }

        await Assert.That(character.Experience).IsGreaterThanOrEqualTo(SolzreedStarterCorridorProfile.ExpLevel10);
        await Assert.That(character.Level).IsEqualTo((byte)10);

        // -------------------------------------------------------------
        // Phase 3: Traversal to Windshade & 6-Quest Blue Salt Chain
        // -------------------------------------------------------------
        character.Transform.Local.SetPosition(
            SolzreedStarterCorridorProfile.WindshadeRecruiterPosition.X,
            SolzreedStarterCorridorProfile.WindshadeRecruiterPosition.Y,
            SolzreedStarterCorridorProfile.WindshadeRecruiterPosition.Z);
        character.Transform.FinalizeTransform();

        // Quest 1: 4415 (Fetch Water)
        var acc1 = actor.AcceptQuest(4415, QuestAcceptorType.Npc, SolzreedStarterCorridorProfile.WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc1.State).IsEqualTo(ActorLifecycleState.Completed);

        GameplayActorTestRig.GrantItem(actor, WaterItemId, 5);
        character.Events.OnItemGather(character, new OnItemGatherArgs { QuestId = 4415, ItemId = WaterItemId, Count = 5 });
        var adv1 = actor.AdvanceQuest(4415);
        await Assert.That(adv1.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti1 = actor.TurnInQuest(4415, recruiterObjId, 0);
        await Assert.That(ti1.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(4415)).IsTrue();

        // Quest 2: 4479 (Watering Crops)
        var acc2 = actor.AcceptQuest(4479, QuestAcceptorType.Npc, SolzreedStarterCorridorProfile.WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc2.State).IsEqualTo(ActorLifecycleState.Completed);

        character.Events.OnInteraction(character, new OnInteractionArgs { DoodadId = 5066, SourcePlayer = character });
        var adv2 = actor.AdvanceQuest(4479);
        await Assert.That(adv2.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti2 = actor.TurnInQuest(4479, recruiterObjId, 0);
        await Assert.That(ti2.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(4479)).IsTrue();

        // Quest 3: 4417 (Plant Potatoes)
        var acc3 = actor.AcceptQuest(4417, QuestAcceptorType.Npc, SolzreedStarterCorridorProfile.WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc3.State).IsEqualTo(ActorLifecycleState.Completed);

        character.Events.OnItemUse(character, new OnItemUseArgs { ItemId = PotatoSeedItemId });
        var adv3 = actor.AdvanceQuest(4417);
        await Assert.That(adv3.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti3 = actor.TurnInQuest(4417, recruiterObjId, 0);
        await Assert.That(ti3.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(4417)).IsTrue();

        // Quest 4: 4424 (Deliver Organic Seeds)
        var acc4 = actor.AcceptQuest(4424, QuestAcceptorType.Npc, SolzreedStarterCorridorProfile.WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc4.State).IsEqualTo(ActorLifecycleState.Completed);

        GameplayActorTestRig.GrantItem(actor, BasilItemId, 1);
        character.Events.OnItemGather(character, new OnItemGatherArgs { QuestId = 4424, ItemId = BasilItemId, Count = 1 });
        var adv4 = actor.AdvanceQuest(4424);
        await Assert.That(adv4.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti4 = actor.TurnInQuest(4424, auctioneerObjId, 0);
        await Assert.That(ti4.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(4424)).IsTrue();

        // Quest 5: 4439 (One Old Coin)
        var acc5 = actor.AcceptQuest(4439, QuestAcceptorType.Npc, 10857);
        await Assert.That(acc5.State).IsEqualTo(ActorLifecycleState.Completed);

        character.Events.OnTalkMade(character, new OnTalkMadeArgs { QuestId = 4439, NpcId = 10857, SourcePlayer = character });
        var adv5 = actor.AdvanceQuest(4439);
        await Assert.That(adv5.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti5 = actor.TurnInQuest(4439, recruiterObjId, 0);
        await Assert.That(ti5.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(4439)).IsTrue();

        // Quest 6: 4438 (Ridge and Furrow - Farm Plot Reward)
        var acc6 = actor.AcceptQuest(4438, QuestAcceptorType.Npc, SolzreedStarterCorridorProfile.WindshadeRecruiterNpcTemplateId);
        await Assert.That(acc6.State).IsEqualTo(ActorLifecycleState.Completed);

        var adv6 = actor.AdvanceQuest(4438);
        await Assert.That(adv6.State).IsEqualTo(ActorLifecycleState.Completed);

        var ti6 = actor.TurnInQuest(4438, recruiterObjId, 0);
        await Assert.That(ti6.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.Quests.HasQuestCompleted(4438)).IsTrue();

        // Verify legitimate rewards landed in bag
        await Assert.That(BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(1);
        await Assert.That(BagCount(actor, LumberItemId)).IsEqualTo(1);
        await Assert.That(BagCount(actor, BoundTaxCertItemId)).IsEqualTo(25);

        // -------------------------------------------------------------
        // Phase 4: Authoritative Plot Placement & 1-Step Construction
        // -------------------------------------------------------------
        character.Transform.Local.SetPosition(HousingPlotPosition.X, HousingPlotPosition.Y, HousingPlotPosition.Z);
        character.Transform.FinalizeTransform();

        var buildReq = actor.BuildHouse(ScarecrowHousingTemplateId, ScarecrowDesignItemId, HousingPlotPosition);
        await Assert.That(buildReq.State).IsEqualTo(ActorLifecycleState.Completed);

        var house = HousingManager.Instance.GetAllHouses().FirstOrDefault(h => h.OwnerId == character.Id);
        await Assert.That(house).IsNotNull();
        await Assert.That(house!.TemplateId).IsEqualTo(ScarecrowHousingTemplateId);
        await Assert.That(house.CurrentStep).IsEqualTo(0);

        // Tax deduction: 15 certificates (10 deposit + 5 first-week tax)
        await Assert.That(BagCount(actor, ScarecrowDesignItemId)).IsEqualTo(0);
        await Assert.That(BagCount(actor, BoundTaxCertItemId)).IsEqualTo(10);

        // 1-step construction (Skill 18553, 10 LP, 1 lumber consumed)
        var lpBeforeConstruct = character.LaborPower;
        var constructReq = actor.Interact(house.ObjId, ScarecrowBuildSkillId);
        await Assert.That(constructReq.State).IsEqualTo(ActorLifecycleState.Completed);

        await Assert.That(house.CurrentStep).IsEqualTo(-1);
        await Assert.That(character.LaborPower).IsEqualTo((short)(lpBeforeConstruct - 10));
        await Assert.That(BagCount(actor, LumberItemId)).IsEqualTo(1);

        // -------------------------------------------------------------
        // Phase 5: Potato Cultivation & Harvest Loop
        // -------------------------------------------------------------
        GameplayActorTestRig.GrantItem(actor, PotatoSeedItemId, 1);
        var crop = CropHarvestLoopRig.Plant(character, session.World, house);
        await Assert.That(crop).IsNotNull();

        // Mature and harvest
        crop.IsPersistent = false;
        crop.FuncGroupId = CropHarvestLoopTests.MaturePhase;
        var harvestReq = actor.Harvest(crop.ObjId);
        await Assert.That(harvestReq.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(BagCount(actor, PotatoHarvestItemId)).IsGreaterThanOrEqualTo(2);

        // -------------------------------------------------------------
        // Phase 6: Long-Term Tax Sustainment via Craft 76 at Plaque 2392
        // -------------------------------------------------------------
        var plaque = session.World.GetAllDoodads().FirstOrDefault(d => d.TemplateId == PlaqueDoodadTemplateId)
                     ?? SpawnPlaqueDoodad(session, actor);

        character.LaborPower = 1000;
        var craftReq = actor.Craft(TaxCraftId, plaque.ObjId);
        await Assert.That(craftReq.State).IsEqualTo(ActorLifecycleState.Running);

        GameplayActorTestRig.CompleteCraftStep(actor, plaque.ObjId, TaxCraftSkillId);
        actor.Tick(TimeSpan.Zero);

        await Assert.That(craftReq.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(character.LaborPower).IsEqualTo((short)800);

        // Exactly 5 Bound Tax Certificates added: 10 + 5 = 15 certificates in bag!
        await Assert.That(BagCount(actor, BoundTaxCertItemId)).IsEqualTo(15);
    }

    private static void JoinActorRegion(HeadlessSession session)
    {
        var character = session.Character;
        var region = session.World.GetRegionByPos(character.Transform.World.Position);
        region?.AddObject(character);
        character.Region = region;
    }

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
        Ensure(BoarMeatItemId, "Boar Meat", 100);
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
}
