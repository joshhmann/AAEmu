using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// M3a-1 scenario rig — homestead placement zone validation + ownership + permissions
/// (feat/homestead-placement). Drives the REAL engine rules (HousingPlacementValidator +
/// the HousingLandZoneInfo data join) with 1.2-shaped data:
///   - land zones joined from housing_areas → housing_groups/housing_group_categories by name
///   - zone-type rules: allowed house categories, faction gate, houseless-only zones
///   - no-overlap rule: garden radius sum (MinHouseSpacing floor)
///   - ownership: owner-only manage; Private/Family/Guild/Public interaction gates
/// The rig builds land zones through the same BuildFromData path the engine uses at boot.
/// </summary>
public class HomesteadPlacementScenarioTests
{
    private const FactionsEnum Nuian = FactionsEnum.NuiaAlliance;
    private const FactionsEnum Haranya = FactionsEnum.HaranyaAlliance;

    private const uint OwnerCharId = 501;
    private const uint OwnerAccountId = 7001;
    private const uint GuestCharId = 502;
    private const uint GuestAccountId = 7002;

    // --- 1.2-shaped fixture data -------------------------------------------------------

    private static HousingGroup Group(uint id, bool houseless = false)
        => new() { Id = id, Name = $"group_{id}", HouselessOnly = houseless, CanExtend = true };

    private static Dictionary<uint, HousingGroup> DefaultGroups() => new()
    {
        [1] = Group(1),           // 일반 주거 지역 — general residential (everything)
        [5] = Group(5),           // 고급 주택 지역 — luxury houses only
        [11] = Group(11),         // 아무것도 지을 수 없는 터 — nothing can be built (no categories)
        [12] = Group(12, houseless: true), // 무주택자 전용 — homeless-only
        [14] = Group(14)          // 작은 주택 지역 — small houses only
    };

    private static Dictionary<uint, HashSet<uint>> DefaultGroupCategories() => new()
    {
        [1] = [1, 8, 9, 10, 11, 12, 16, 17, 18],
        [5] = [10, 11, 12],
        [12] = [1],
        [14] = [1, 16, 17]
        // group 11 deliberately has NO categories → everything rejected
    };

    private static HousingTemplate SmallHouse() => new()
    {
        Id = 110, Name = "small_house", CategoryId = 1, GardenRadius = 7.5f, HousingBindingDoodad = []
    };
    private static HousingTemplate Mansion() => new()
    {
        Id = 136, Name = "mansion", CategoryId = 12, GardenRadius = 22f, HousingBindingDoodad = []
    };
    private static HousingTemplate PlaceholderDesign() => new()
    {
        Id = 1, Name = "house_design_1", CategoryId = 1, GardenRadius = 0f, HousingBindingDoodad = []
    };

    private static Dictionary<string, HousingLandZoneInfo> BuildZones(
        Dictionary<uint, HousingGroup> groups,
        Dictionary<uint, HashSet<uint>> groupCategories,
        params (string Name, uint GroupId)[] areas)
    {
        var areaRows = areas.Select((a, i) => new HousingAreas { Id = (uint)(i + 1), Name = a.Name, GroupId = a.GroupId });
        return HousingLandZoneInfo.BuildFromData(areaRows, groups, groupCategories);
    }

    private static House HouseAt(uint ownerId, HousingTemplate template, float x, float y,
        HousingPermission permission = HousingPermission.Private, int currentStep = -1)
    {
        var house = new House
        {
            OwnerId = ownerId,
            AccountId = OwnerAccountId,
            Template = template,
            Permission = permission,
            CurrentStep = currentStep
        };
        house.Transform = new Transform(house, null, new Vector3(x, y, 0), Vector3.Zero);
        return house;
    }

    private static Character Character(uint id, uint accountId, uint family = 0, Expedition expedition = null)
        => new(new UnitCustomModelParams()) { Id = id, AccountId = accountId, Family = family, Expedition = expedition };

    /// <summary>Hand-rolled recording fake (suite convention — TUnit.Mocks, no Moq).</summary>
    private sealed class FakeNameManager : INameManager
    {
        private readonly Dictionary<uint, uint> _characterAccounts = [];

        public FakeNameManager WithCharacterAccount(uint characterId, uint accountId)
        {
            _characterAccounts[characterId] = accountId;
            return this;
        }

        public string GetCharacterName(uint characterId) => $"char_{characterId}";
        public uint GetCharacterId(string normalizedCharacterName) => 0;
        public uint GetCharacterAccount(uint characterId) => _characterAccounts.GetValueOrDefault(characterId);
        public CharacterCreateError ValidateCharacterName(string name) => CharacterCreateError.Ok;
        public void AddCharacter(uint characterId, string name, uint accountId) { }
        public void RemoveCharacterId(uint characterId) { }
        public bool NoNamesRegistered() => true;
        public void Load() { }
    }

    private static INameManager NameManagerReturning(uint ownerId, uint ownerAccountId)
        => new FakeNameManager().WithCharacterAccount(ownerId, ownerAccountId);

    // --- Placement zone validation -----------------------------------------------------

    [Test]
    public async Task Place_OnValidLandZoneWithMatchingRules_Accepted()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(),
            ("w_solzreed_1", 1), ("w_solzreed_1", 14));
        var landZone = zones["w_solzreed_1"];

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(100, 100, 0), Nuian, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task Place_OnUnknownZone_RejectedInvalidArea()
    {
        var error = HousingPlacementValidator.ValidatePlacement(
            null, Nuian, SmallHouse(), new Vector3(100, 100, 0), Nuian, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    [Test]
    public async Task Place_OnForbiddenZoneWithNoAllowedCategories_RejectedInvalidArea()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_deadland_1", 11));
        var landZone = zones["w_deadland_1"];

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(100, 100, 0), Nuian, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    [Test]
    public async Task Place_CategoryNotAllowedByZoneType_RejectedInvalidArea()
    {
        // zone with only the luxury group → small house (cat 1) must be rejected
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_luxury_1", 5));
        var landZone = zones["w_luxury_1"];

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(100, 100, 0), Nuian, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    [Test]
    public async Task Place_CategoryAllowedByZoneType_Accepted()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_luxury_1", 5));
        var landZone = zones["w_luxury_1"];

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, Mansion(), new Vector3(100, 100, 0), Nuian, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task Place_FactionMismatchOnClaimedZone_RejectedInvalidArea()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_solzreed_1", 1));
        var landZone = zones["w_solzreed_1"];

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(100, 100, 0), Haranya, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    [Test]
    public async Task Place_NeutralZone_AnyFaction_Accepted()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("s_neutral_1", 1));
        var landZone = zones["s_neutral_1"];

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, FactionsEnum.Invalid, SmallHouse(), new Vector3(100, 100, 0), Haranya, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task Place_HouselessOnlyZone_OwnerAlreadyHasHouse_RejectedInvalidArea()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_newbie_1", 12));
        var landZone = zones["w_newbie_1"];
        var existing = new[] { HouseAt(OwnerCharId, SmallHouse(), 300, 300) };

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(100, 100, 0), Nuian, characterOwnsHouses: true, existing);

        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    [Test]
    public async Task Place_HouselessOnlyZone_OwnerWithoutHouse_Accepted()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_newbie_1", 12));
        var landZone = zones["w_newbie_1"];

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(100, 100, 0), Nuian, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task Place_OverlappingExistingHouse_RejectedOverlapHouse()
    {
        // small houses (r 7.5 + 7.5 = 15m required) placed 10m apart → overlap
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_solzreed_1", 1));
        var landZone = zones["w_solzreed_1"];
        var existing = new[] { HouseAt(OwnerCharId, SmallHouse(), 0, 0) };

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(10, 0, 0), Nuian, characterOwnsHouses: false, existing);

        await Assert.That(error).IsEqualTo(HousingPlacementError.OverlapHouse);
    }

    [Test]
    public async Task Place_AdjacentHomesteads_Accepted()
    {
        // M3a exit case: two players, adjacent homesteads, gardens must not overlap
        // (7.5 + 7.5 = 15m required) — 16m apart is a legal adjacent pair
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_solzreed_1", 1));
        var landZone = zones["w_solzreed_1"];
        var existing = new[] { HouseAt(OwnerCharId, SmallHouse(), 0, 0) };

        var error = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, SmallHouse(), new Vector3(16, 0, 0), Nuian, characterOwnsHouses: false, existing);

        await Assert.That(error).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task Place_ZeroRadiusPlaceholderTemplates_SpacingFloorEnforced()
    {
        var zones = BuildZones(DefaultGroups(), DefaultGroupCategories(), ("w_solzreed_1", 1));
        var landZone = zones["w_solzreed_1"];
        var existing = new[] { HouseAt(OwnerCharId, PlaceholderDesign(), 0, 0) };

        // 3m apart → inside the 5m floor → overlap
        var tooClose = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, PlaceholderDesign(), new Vector3(3, 0, 0), Nuian, characterOwnsHouses: false, existing);
        // 6m apart → outside the floor → accepted
        var farEnough = HousingPlacementValidator.ValidatePlacement(
            landZone, Nuian, PlaceholderDesign(), new Vector3(6, 0, 0), Nuian, characterOwnsHouses: false, existing);

        await Assert.That(tooClose).IsEqualTo(HousingPlacementError.OverlapHouse);
        await Assert.That(farEnough).IsEqualTo(HousingPlacementError.None);
    }

    [Test]
    public async Task Place_NullTemplate_RejectedInvalidArea()
    {
        var error = HousingPlacementValidator.ValidatePlacement(
            null, Nuian, null, new Vector3(100, 100, 0), Nuian, characterOwnsHouses: false, []);

        await Assert.That(error).IsEqualTo(HousingPlacementError.InvalidArea);
    }

    // --- Ownership (claim/owner binding) -----------------------------------------------

    [Test]
    public async Task CanManage_OwnerCharacter_True()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0);
        await Assert.That(HousingPlacementValidator.CanManage(house, OwnerCharId)).IsTrue();
    }

    [Test]
    public async Task CanManage_OtherCharacter_False()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0);
        await Assert.That(HousingPlacementValidator.CanManage(house, GuestCharId)).IsFalse();
    }

    [Test]
    public async Task CanManage_NullHouse_False()
    {
        await Assert.That(HousingPlacementValidator.CanManage(null, OwnerCharId)).IsFalse();
    }

    // --- Permissions (who can interact with what) --------------------------------------

    [Test]
    public async Task Interact_Private_OwnerAllowed()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Private);
        var owner = Character(OwnerCharId, OwnerAccountId);
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, owner, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task Interact_Private_SameAccountAltAllowed()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Private);
        var alt = Character(GuestCharId, OwnerAccountId); // same account, different character
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, alt, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task Interact_Private_GuestOnOtherAccount_Denied()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Private);
        var guest = Character(GuestCharId, GuestAccountId);
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, guest, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task Interact_Family_FamilyMemberAllowed()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Family);
        var member = Character(GuestCharId, GuestAccountId, family: 9);
        var family = new Family { Id = 9 };
        family.Members.Add(new FamilyMember { Id = OwnerCharId, Character = Character(OwnerCharId, OwnerAccountId) });
        var resolver = (Func<uint, Family>)(id => id == 9 ? family : null);

        var allowed = HousingPlacementValidator.CanInteract(house, member, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task Interact_Family_NonFamilyMember_Denied()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Family);
        var stranger = Character(GuestCharId, GuestAccountId, family: 10); // different family
        var family = new Family { Id = 9 };
        family.Members.Add(new FamilyMember { Id = OwnerCharId, Character = Character(OwnerCharId, OwnerAccountId) });
        var resolver = (Func<uint, Family>)(id => id == 9 ? family : null);

        var allowed = HousingPlacementValidator.CanInteract(house, stranger, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task Interact_Family_PlayerWithoutFamily_Denied()
    {
        // regression: upstream fell through to Public when the player had no family
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Family);
        var noFamily = Character(GuestCharId, GuestAccountId, family: 0);
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, noFamily, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task Interact_Guild_ExpeditionMemberAllowed()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Guild);
        var member = Character(GuestCharId, GuestAccountId);
        member.Expedition = new Expedition { Id = (FactionsEnum)42, Members = [new ExpeditionMember { CharacterId = OwnerCharId }] };
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, member, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task Interact_Guild_PlayerWithoutExpedition_Denied()
    {
        // regression: upstream fell through to Public when the player had no expedition
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Guild);
        var noGuild = Character(GuestCharId, GuestAccountId);
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, noGuild, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task Interact_Public_AnyPlayerAllowed()
    {
        var house = HouseAt(OwnerCharId, SmallHouse(), 0, 0, HousingPermission.Public);
        var guest = Character(GuestCharId, GuestAccountId);
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, guest, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task Interact_AlwaysPublicTemplate_AnyPlayerAllowed()
    {
        var alwaysPublic = new HousingTemplate { Id = 900, Name = "public_house", CategoryId = 1, GardenRadius = 7.5f, AlwaysPublic = true, HousingBindingDoodad = [] };
        var house = HouseAt(OwnerCharId, alwaysPublic, 0, 0, HousingPermission.Private);
        var guest = Character(GuestCharId, GuestAccountId);
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, guest, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task Interact_UnfinishedHouse_AnyPlayerAllowed()
    {
        // unfinished houses can't be used to private store — always interactable.
        // The CurrentStep setter reads BuildSteps[_currentStep].ModelId, so the
        // under-construction template carries step 0.
        var underConstruction = new HousingTemplate
        {
            Id = 111, Name = "small_house_uc", CategoryId = 1, GardenRadius = 7.5f,
            HousingBindingDoodad = [], MainModelId = 1
        };
        underConstruction.BuildSteps.Add(0, new HousingBuildStep { Step = 0, ModelId = 2, NumActions = 1 });

        var house = HouseAt(OwnerCharId, underConstruction, 0, 0, HousingPermission.Private, currentStep: 0);
        var guest = Character(GuestCharId, GuestAccountId);
        var resolver = (Func<uint, Family>)(_ => null);

        var allowed = HousingPlacementValidator.CanInteract(house, guest, NameManagerReturning(OwnerCharId, OwnerAccountId), resolver);

        await Assert.That(allowed).IsTrue();
    }

    // --- Data join (engine boot path) --------------------------------------------------

    [Test]
    public async Task BuildFromData_MultipleAreaGroupsInOneZone_UnionsCategoriesAndFlagsHouseless()
    {
        var groups = DefaultGroups();
        groups[13] = Group(13, houseless: true);
        var categories = DefaultGroupCategories();
        categories[13] = [16];
        // zone "w_mixed_1" carries a normal area (g1: cats 1,10,11,12,16) AND a houseless-only area (g13: cat 16)
        var zones = BuildZones(groups, categories,
            ("w_mixed_1", 1), ("w_mixed_1", 13));
        var landZone = zones["w_mixed_1"];

        await Assert.That(landZone).IsNotNull();
        await Assert.That(landZone.AllowedCategories.Contains(1)).IsTrue();
        await Assert.That(landZone.AllowedCategories.Contains(16)).IsTrue();
        await Assert.That(landZone.IsHouselessOnly).IsTrue();
        await Assert.That(landZone.Groups).HasCount().EqualTo(2);
    }

    [Test]
    public async Task BuildFromData_AreaWithUnknownGroup_Skipped()
    {
        var groups = DefaultGroups();
        var zones = BuildZones(groups, DefaultGroupCategories(), ("w_orphan_1", 999));

        await Assert.That(zones.ContainsKey("w_orphan_1")).IsFalse();
    }
}
