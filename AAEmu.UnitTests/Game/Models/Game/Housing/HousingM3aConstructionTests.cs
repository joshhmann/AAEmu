using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.Stream;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.UnitTests.Utils.Mocks;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

/// <summary>
/// M3a-2 (t_fe9a5e72): construction + decoration-limit enforcement + housing UI data
/// harness on the real engine paths.
///
/// - Construction: House.AddBuildAction (called by CraftEffect's Building wi-group) drives
///   the build steps — each action advances the step, swaps the structure model, and at the
///   final action the house completes (CurrentStep == -1, main model). CraftEffect itself is
///   driven end-to-end with a CharacterMock.
/// - Deco limits: DecorationLimitEvaluator (the gate DecorateHouse calls) enforces
///   absolute_deco_limit (count) and housing_deco_limit_elems per-actability-group
///   allowances (type), plus the deco_limit backstop.
/// - Housing UI data: HousingGameData loads housing_groups/housing_group_categories and the
///   house serialization (SCMyHousePacket/SCHouseStatePacket payload) reflects built state.
///
/// Real-data rig: HousingGameData is loaded from the canonical compact.sqlite3 (same
/// loader as production), and the deco-limit rows are canonical 1.2 values
/// (limit group 1 = actability group 1 x3 + group 5 x2; deco_limit 40, absolute 51).
/// </summary>
[NotInParallel] // seeds shared singletons (HousingGameData, WorldManager, QuestManager) — same convention as QuestActCheckSphereTests / NpcMoveTowardsTests
public class HousingM3aConstructionTests
{
    /// <summary>Canonical 1.2 '아담한 누이아 주택' — deco limit group 1, 3 build steps, 1 action each.</summary>
    private const uint CanonicalHousing172 = 172;

    /// <summary>Canonical group-1 decoration designs (deco_actability_group_id = 1).</summary>
    private static readonly uint[] Group1DesignIds = [270, 271, 273];

    /// <summary>Canonical group-1 decoration doodad templates (housing_decorations.doodad_id).</summary>
    private static readonly uint[] Group1DoodadIds = [6500, 6387, 6388];

    /// <summary>Canonical group-5 decoration doodad template (deco_actability_group_id = 5).</summary>
    private const uint Group5DoodadId = 2721;

    private object _previousHousingGameData;
    private object _previousWorldManager;
    private object _previousQuestManager;

    [Before(Test)]
    public void SetUp()
    {
        SeedWorldManager();
        SeedQuestManager();
        LoadRealHousingGameData();
    }

    [After(Test)]
    public void TearDown()
    {
        var housingField = typeof(Singleton<HousingGameData>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        housingField?.SetValue(null, _previousHousingGameData);
        var worldField = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        worldField?.SetValue(null, _previousWorldManager);
        var questField = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        questField?.SetValue(null, _previousQuestManager);
    }

    private static string CanonicalDbPath
    {
        get
        {
            // Test host bin dir → repo root is 4 levels up (net10.0/Release/bin/<project>);
            // also accept the working-directory layout and the legacy 5-up walk.
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

    /// <summary>
    /// Loads the canonical housing data into the HousingGameData singleton via its real
    /// loader (production path). Save/restore the singleton so no other test sees it.
    /// </summary>
    private void LoadRealHousingGameData()
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

    /// <summary>Minimal WorldManager: only the world-interaction group map CraftEffect reads.</summary>
    private void SeedWorldManager()
    {
        var field = typeof(Singleton<WorldManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousWorldManager = field?.GetValue(null);
        var worldManager = new WorldManager(
            Mock.Of<ITickManager>().Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));
        SetField(worldManager, "_worldInteractionGroups", new Dictionary<uint, WorldInteractionGroup>
        {
            [(uint)WorldInteractionType.Building] = WorldInteractionGroup.Building
        });
        field?.SetValue(null, worldManager);
    }

    /// <summary>Unloaded QuestManager so CraftEffect's trailing quest event is a no-op (QuestActCheckSphereTests pattern).</summary>
    private void SeedQuestManager()
    {
        var field = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        _previousQuestManager = field?.GetValue(null);
        var questManager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        SetField(questManager, "_componentTemplates", new Dictionary<uint, QuestComponentTemplate>());
        field?.SetValue(null, questManager);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static HousingTemplate CanonicalTemplate(uint designId = CanonicalHousing172)
    {
        return HousingGameData.Instance.GetTemplate(designId);
    }

    /// <summary>
    /// A house on its plot at the given build step. IsBeingLoadedFromDb suppresses bound-doodad
    /// spawns (DoodadManager is not part of this rig; production spawns them at completion).
    /// </summary>
    private static House CreateHouse(HousingTemplate template, int currentStep, uint houseId = 1)
    {
        var house = new House
        {
            Template = template,
            TlId = (ushort)houseId,
            Id = houseId,
            ObjId = 100 + houseId,
            OwnerId = 1,
            Name = "Test House"
        };
        house.Transform = new Transform(house, null, Vector3.Zero);
        house.IsBeingLoadedFromDb = true;
        house.CurrentStep = currentStep;
        return house;
    }

    private static Doodad DecorationDoodad(uint objId, uint doodadTemplateId, uint houseId = 1)
    {
        return new Doodad
        {
            ObjId = objId,
            TemplateId = doodadTemplateId,
            OwnerDbId = houseId,
            AttachPoint = AttachPointKind.None
        };
    }

    private static CraftEffect BuildCraftEffect()
    {
        return new CraftEffect { WorldInteraction = WorldInteractionType.Building };
    }

    private static CharacterMock CraftingCharacter()
    {
        var character = new CharacterMock();
        character.Craft = new CharacterCraft(character);
        return character;
    }

    private static void ApplyBuild(CraftEffect effect, CharacterMock character, House house, uint skillId)
    {
        effect.Apply(character, null, house, null, new CastSkill(skillId, house.TlId), new EffectSource(), null, DateTime.UtcNow);
    }

    #region Construction — real engine path (House.AddBuildAction via CraftEffect)

    [Test]
    public async Task AddBuildAction_ProgressesThroughSteps_SwapsModelPerStep()
    {
        var template = CanonicalTemplate();
        var house = CreateHouse(template, currentStep: 0);

        await Assert.That(house.CurrentStep).IsEqualTo(0);
        await Assert.That(house.ModelId).IsEqualTo(template.BuildSteps[0].ModelId);
        await Assert.That(house.AllAction).IsEqualTo(3); // 3 steps x 1 action

        house.AddBuildAction();
        await Assert.That(house.CurrentStep).IsEqualTo(1);
        await Assert.That(house.ModelId).IsEqualTo(template.BuildSteps[1].ModelId);

        house.AddBuildAction();
        await Assert.That(house.CurrentStep).IsEqualTo(2);
        await Assert.That(house.ModelId).IsEqualTo(template.BuildSteps[2].ModelId);

        // Final action completes the structure: main model, all actions consumed
        house.AddBuildAction();
        await Assert.That(house.CurrentStep).IsEqualTo(-1);
        await Assert.That(house.ModelId).IsEqualTo(template.MainModelId);
        // The client-facing progress (SCHouseBuildProgressPacket formula) reports all actions done
        await Assert.That(house.CurrentStep == -1 ? house.AllAction : house.CurrentAction).IsEqualTo(house.AllAction);
    }

    [Test]
    public async Task AddBuildAction_OnCompletedHouse_IsNoOp()
    {
        var template = CanonicalTemplate();
        var house = CreateHouse(template, currentStep: -1);

        house.AddBuildAction();

        await Assert.That(house.CurrentStep).IsEqualTo(-1);
        await Assert.That(house.NumAction).IsEqualTo(0);
        await Assert.That(house.ModelId).IsEqualTo(template.MainModelId);
    }

    [Test]
    public async Task CraftEffect_BuildingWithCorrectSkill_BuildsStructureToCompletion()
    {
        var template = CanonicalTemplate();
        var house = CreateHouse(template, currentStep: 0);
        var character = CraftingCharacter();
        var effect = BuildCraftEffect();

        // Step 0 and 1 use skill 14575, step 2 uses skill 14574 (canonical housing_build_steps)
        ApplyBuild(effect, character, house, template.BuildSteps[0].SkillId);
        await Assert.That(house.CurrentStep).IsEqualTo(1);

        ApplyBuild(effect, character, house, template.BuildSteps[1].SkillId);
        await Assert.That(house.CurrentStep).IsEqualTo(2);

        ApplyBuild(effect, character, house, template.BuildSteps[2].SkillId);
        await Assert.That(house.CurrentStep).IsEqualTo(-1);
        await Assert.That(house.ModelId).IsEqualTo(template.MainModelId);
    }

    [Test]
    public async Task CraftEffect_BuildingOnCompletedHouse_DoesNotThrow_AndLeavesState()
    {
        var template = CanonicalTemplate();
        var house = CreateHouse(template, currentStep: -1);
        var character = CraftingCharacter();
        var effect = BuildCraftEffect();

        // Regression: the step lookup used to index BuildSteps with CurrentStep == -1
        // (KeyNotFoundException) and then touched SkillTask (NRE). A finished house must
        // simply end the craft with no state change.
        ApplyBuild(effect, character, house, template.BuildSteps[0].SkillId);

        await Assert.That(house.CurrentStep).IsEqualTo(-1);
        await Assert.That(house.NumAction).IsEqualTo(0);
        await Assert.That(house.ModelId).IsEqualTo(template.MainModelId);
    }

    #endregion

    #region Decoration limits — DecorationLimitEvaluator rules (pure)

    private static HousingTemplate LimitTemplate(uint absolute = 51, uint decoLimit = 40, uint limitGroupId = 1)
    {
        return new HousingTemplate { AbsoluteDecoLimit = absolute, DecoLimit = decoLimit, HousingDecoLimitId = limitGroupId };
    }

    /// <summary>Canonical group-1 limit table: group 1 x3, group 5 x2 (housing_deco_limit_id 1).</summary>
    private static readonly Dictionary<uint, int> CanonicalGroupLimits = new()
    {
        [1] = 3,
        [5] = 2
    };

    /// <summary>Canonical doodad-template → decoration design mapping for the pure-rule tests.</summary>
    private static readonly Dictionary<uint, HousingDecoration> CanonicalDesignLookup = new()
    {
        [Group1DoodadIds[0]] = Design(1),
        [Group1DoodadIds[1]] = Design(1),
        [Group1DoodadIds[2]] = Design(1),
        [Group5DoodadId] = Design(5)
    };

    private static HousingDecoration Design(uint groupId)
    {
        return new HousingDecoration { DecoActAbilityGroupId = groupId };
    }

    private static bool Evaluate(
        HousingTemplate template,
        HousingDecoration newDesign,
        IReadOnlyCollection<Doodad> existing,
        out ErrorMessageType error)
    {
        return DecorationLimitEvaluator.IsDecorationAllowed(
            template,
            newDesign,
            existing,
            doodadTemplateId => CanonicalDesignLookup.GetValueOrDefault(doodadTemplateId),
            (limitId, groupId) => CanonicalGroupLimits.GetValueOrDefault(groupId),
            out error);
    }

    [Test]
    public async Task Evaluator_OverAbsoluteLimit_RejectedWithHouseTooManyDecorations()
    {
        var template = LimitTemplate(absolute: 2, decoLimit: 40);
        var existing = new List<Doodad>
        {
            DecorationDoodad(1, 1000),
            DecorationDoodad(2, 1001)
        };

        var allowed = Evaluate(template, Design(0), existing, out var error);

        await Assert.That(allowed).IsFalse();
        await Assert.That(error).IsEqualTo(ErrorMessageType.HouseTooManyDecorations);
    }

    [Test]
    public async Task Evaluator_PerGroupLimit_RejectedWithActabilityDecoLimited()
    {
        var template = LimitTemplate();
        // 3 canonical group-1 doodads already placed — group 1 allows exactly 3
        var existing = Group1DoodadIds.Select((doodadId, i) => DecorationDoodad((uint)i + 1, doodadId)).ToList();

        var allowed = Evaluate(template, Design(1), existing, out var error);

        await Assert.That(allowed).IsFalse();
        await Assert.That(error).IsEqualTo(ErrorMessageType.HousingActabilityDecoLimited);
    }

    [Test]
    public async Task Evaluator_AtGroupLimit_Allowed()
    {
        var template = LimitTemplate();
        // 2 of 3 group-1 slots used — one more is fine
        var existing = Group1DoodadIds.Take(2).Select((doodadId, i) => DecorationDoodad((uint)i + 1, doodadId)).ToList();

        var allowed = Evaluate(template, Design(1), existing, out var error);

        await Assert.That(allowed).IsTrue();
        await Assert.That(error).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Evaluator_DifferentGroup_DoesNotConsumeGroupAllowance()
    {
        var template = LimitTemplate();
        // Group-5 doodads are at their own allowance (2) and must not count toward group 1
        var existing = new List<Doodad>
        {
            DecorationDoodad(1, Group5DoodadId),
            DecorationDoodad(2, Group5DoodadId)
        };

        var allowed = Evaluate(template, Design(1), existing, out var error);

        await Assert.That(allowed).IsTrue();
        await Assert.That(error).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Evaluator_AttachedDoodads_DoNotCountTowardLimits()
    {
        var template = LimitTemplate(absolute: 2);
        // 3 attached doodads (doors/windows etc. — canonical housing attach points) — structure, not decoration
        var existing = new List<Doodad>
        {
            new() { ObjId = 1, TemplateId = 5000, OwnerDbId = 1, AttachPoint = AttachPointKind.Driver },
            new() { ObjId = 2, TemplateId = 5001, OwnerDbId = 1, AttachPoint = AttachPointKind.Cannon0 },
            new() { ObjId = 3, TemplateId = 5002, OwnerDbId = 1, AttachPoint = AttachPointKind.NamePlate01 }
        };

        var allowed = Evaluate(template, Design(0), existing, out var error);

        await Assert.That(allowed).IsTrue();
        await Assert.That(error).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Evaluator_PlainDesign_OnlyAbsoluteLimitApplies()
    {
        var template = LimitTemplate(decoLimit: 1, absolute: 51);
        // One grouped doodad already fills the deco_limit backstop, but a plain design
        // (no actability group) is only capped by the absolute limit
        var existing = new List<Doodad> { DecorationDoodad(1, Group1DoodadIds[0]) };

        var allowed = Evaluate(template, Design(0), existing, out var error);

        await Assert.That(allowed).IsTrue();
        await Assert.That(error).IsEqualTo(ErrorMessageType.NoErrorMessage);
    }

    [Test]
    public async Task Evaluator_GroupedTotalBackstop_RejectedWhenDecoLimitFilled()
    {
        var template = LimitTemplate(decoLimit: 2, absolute: 51);
        // No elem for group 3, but the grouped total backstop (deco_limit) still applies
        var existing = new List<Doodad>
        {
            DecorationDoodad(1, Group1DoodadIds[0]),
            DecorationDoodad(2, Group1DoodadIds[1])
        };

        var allowed = Evaluate(template, Design(3), existing, out var error);

        await Assert.That(allowed).IsFalse();
        await Assert.That(error).IsEqualTo(ErrorMessageType.HousingActabilityDecoLimited);
    }

    #endregion

    #region Decoration limits — DecorateHouse enforcement (real wiring)

    private static HousingManager CreateHousingManager(
        out Mock<IItemManager> itemMock,
        out Mock<IDoodadManager> doodadMock,
        out Mock<IUccManager> uccMock)
    {
        itemMock = Mock.Of<IItemManager>();
        doodadMock = Mock.Of<IDoodadManager>();
        uccMock = Mock.Of<IUccManager>();
        return new HousingManager(
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IFactionManager>().Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<IWorldManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IHousingIdManager>().Object,
            Mock.Of<IHousingTldManager>().Object,
            itemMock.Object,
            Mock.Of<IMailManager>().Object,
            Mock.Of<INameManager>().Object,
            Mock.Of<IZoneManager>().Object,
            doodadMock.Object,
            uccMock.Object);
    }

    private static WorldInstance DecorationWorld(params Doodad[] doodads)
    {
        var world = new WorldInstance(new WorldTemplate { Id = 1, Name = "test_world" }, 0, true, 1);
        foreach (var doodad in doodads)
            world.AddObject(doodad);
        // GameObject.ParentWorld walks WorldManager.Instance.GetWorld(instanceId) and re-enters
        // the setter — the test world must be registered for that lookup to resolve to itself.
        SetField(WorldManager.Instance, "_worlds", new ConcurrentDictionary<uint, WorldInstance> { [world.Id] = world });
        return world;
    }

    private static CharacterMock HomeOwner(uint characterId)
    {
        return new CharacterMock { Id = characterId };
    }

    private static Item DecorationItem(ulong itemId, uint ownerCharacterId)
    {
        return new Item { OwnerId = ownerCharacterId };
    }

    [Test]
    public async Task DecorateHouse_OverPerGroupLimit_Rejected_NoDoodadCreated()
    {
        var template = CanonicalTemplate();
        // 3 group-1 decorations already on the plot — canonical allowance is 3
        var world = DecorationWorld(Group1DoodadIds.Select((d, i) => DecorationDoodad((uint)i + 1, d)).ToArray());
        var house = CreateHouse(template, currentStep: -1);
        house.ParentWorld = world;

        var manager = CreateHousingManager(out var itemMock, out var doodadMock, out _);
        SetField(manager, "_housesTl", new Dictionary<ushort, House> { [house.TlId] = house });

        var player = HomeOwner(1);
        var item = DecorationItem(7000, player.Id);
        itemMock.GetItemByItemId(Arg.Any<ulong>()).Returns(item);

        // Design 270 is a canonical group-1 decoration (doodad 6500)
        var placed = manager.DecorateHouse(player, house.TlId, 270, Vector3.Zero, Quaternion.Identity, 0, 7000);

        await Assert.That(placed).IsFalse();
        // Item lookup was exercised → the rejection came from the decoration-limit gate, not the item check
        await Assert.That(Mock.Invocations(itemMock).Count).IsGreaterThan(0);
        // No doodad was ever created
        await Assert.That(Mock.Invocations(doodadMock).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DecorateHouse_OverAbsoluteLimit_Rejected_NoDoodadCreated()
    {
        // Synthetic 2-item house: two plain decorations already placed
        var template = LimitTemplate(absolute: 2, decoLimit: 40);
        var world = DecorationWorld(DecorationDoodad(1, 1294), DecorationDoodad(2, 1295));
        var house = CreateHouse(template, currentStep: -1);
        house.ParentWorld = world;

        var manager = CreateHousingManager(out var itemMock, out var doodadMock, out _);
        SetField(manager, "_housesTl", new Dictionary<ushort, House> { [house.TlId] = house });

        var player = HomeOwner(1);
        var item = DecorationItem(7001, player.Id);
        itemMock.GetItemByItemId(Arg.Any<ulong>()).Returns(item);

        var placed = manager.DecorateHouse(player, house.TlId, 270, Vector3.Zero, Quaternion.Identity, 0, 7001);

        await Assert.That(placed).IsFalse();
        await Assert.That(Mock.Invocations(itemMock).Count).IsGreaterThan(0);
        await Assert.That(Mock.Invocations(doodadMock).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DecorateHouse_UnknownDesign_Rejected()
    {
        var template = CanonicalTemplate();
        var world = DecorationWorld();
        var house = CreateHouse(template, currentStep: -1);
        house.ParentWorld = world;

        var manager = CreateHousingManager(out var itemMock, out var doodadMock, out _);
        SetField(manager, "_housesTl", new Dictionary<ushort, House> { [house.TlId] = house });

        var player = HomeOwner(1);
        var item = DecorationItem(7002, player.Id);
        itemMock.GetItemByItemId(Arg.Any<ulong>()).Returns(item);

        // 999999 is not a housing_decorations id
        var placed = manager.DecorateHouse(player, house.TlId, 999999, Vector3.Zero, Quaternion.Identity, 0, 7002);

        await Assert.That(placed).IsFalse();
        await Assert.That(Mock.Invocations(itemMock).Count).IsGreaterThan(0);
        await Assert.That(Mock.Invocations(doodadMock).Count).IsEqualTo(0);
    }

    #endregion

    #region Housing UI data — canonical game data + house serialization

    [Test]
    public async Task HousingGameData_Load_CanonicalDecoLimitData()
    {
        await Assert.That(HousingGameData.Instance.GetDecoLimit(1).Name).IsEqualTo("아담한 누이아 주택");
        // Canonical housing_deco_limit_elems for limit group 1: group 1 x3, group 5 x2
        await Assert.That(HousingGameData.Instance.GetDecoLimitCount(1, 1)).IsEqualTo(3);
        await Assert.That(HousingGameData.Instance.GetDecoLimitCount(1, 5)).IsEqualTo(2);
        // No elem for group 4 → no per-group limit
        await Assert.That(HousingGameData.Instance.GetDecoLimitCount(1, 4)).IsEqualTo(0);
        // Limit id 0 / group 0 are sentinels
        await Assert.That(HousingGameData.Instance.GetDecoLimitCount(0, 1)).IsEqualTo(0);
        await Assert.That(HousingGameData.Instance.GetDecoLimitCount(1, 0)).IsEqualTo(0);
    }

    [Test]
    public async Task HousingGameData_Load_CanonicalHousingGroupUiData()
    {
        // housing_groups: 15 canonical groups
        await Assert.That(HousingGameData.Instance.GetHousingGroups().Count).IsEqualTo(15);
        // Group 10 is the thatched-farm area, carrying a plot doodad
        var group10 = HousingGameData.Instance.GetHousingGroup(10);
        await Assert.That(group10).IsNotNull();
        await Assert.That(group10.Name).IsEqualTo("초가지붕 농장 지역");
        await Assert.That(group10.DoodadId).IsEqualTo((uint?)6229);
        // Group 1 carries 9 category allowances (housing_group_categories)
        await Assert.That(HousingGameData.Instance.GetHousingGroupCategories(1).Count).IsEqualTo(9);
    }

    [Test]
    public async Task HouseWrite_CompletedHouse_SerializesFinishedState()
    {
        var template = CanonicalTemplate();
        var house = CreateHouse(template, currentStep: -1);

        var stream = new PacketStream();
        house.Write(stream);
        stream.Pos = 0;

        await Assert.That(stream.ReadUInt16()).IsEqualTo(house.TlId);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(house.Id);
        await Assert.That(stream.ReadBc()).IsEqualTo(house.ObjId);
        await Assert.That(stream.ReadUInt32()).IsEqualTo(house.TemplateId);
        // House.Write emits WritePisc(ModelId, 0) — read the full pair so the stream stays aligned
        var pisc = stream.ReadPiscW(2);
        await Assert.That(pisc[0]).IsEqualTo((long)template.MainModelId); // built structure model
        await Assert.That(pisc[1]).IsEqualTo(0);
        stream.ReadUInt32(); // co-owner
        stream.ReadUInt32(); // owner
        stream.ReadString(); // owner name
        stream.ReadUInt32(); // account id
        stream.ReadByte(); // permission
        // Finished houses serialize build progress as 0/0
        await Assert.That(stream.ReadInt32()).IsEqualTo(0);
        await Assert.That(stream.ReadInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task HouseWrite_UnderConstruction_SerializesProgress()
    {
        var template = CanonicalTemplate();
        var house = CreateHouse(template, currentStep: 0);
        house.AddBuildAction(); // 1-action steps: one action completes step 0 → step 1

        var stream = new PacketStream();
        house.Write(stream);
        stream.Pos = 0;

        stream.ReadUInt16();
        stream.ReadUInt32();
        stream.ReadBc();
        stream.ReadUInt32();
        // House.Write emits WritePisc(ModelId, 0) — read the full pair so the stream stays aligned
        var pisc = stream.ReadPiscW(2);
        await Assert.That(pisc[0]).IsEqualTo((long)template.BuildSteps[1].ModelId); // step-1 structure model
        await Assert.That(pisc[1]).IsEqualTo(0);
        stream.ReadUInt32();
        stream.ReadUInt32();
        stream.ReadString();
        stream.ReadUInt32();
        stream.ReadByte();
        // Under construction: all actions vs current action
        await Assert.That(stream.ReadInt32()).IsEqualTo(house.AllAction);
        await Assert.That(stream.ReadInt32()).IsEqualTo(house.CurrentAction);
    }

    #endregion
}
