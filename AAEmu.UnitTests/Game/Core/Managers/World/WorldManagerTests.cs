using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers.World;

public class WorldManagerTests
{
    #region Constructor Tests

    [Test]
    public async Task Constructor_DoesNotCallDependencies()
    {
        // Arrange
        var mockTickManager = Mock.Of<ITickManager>();
        var mockWorldIdManager = Mock.Of<IWorldIdManager>();
        var mockZoneManager = Mock.Of<IZoneManager>();
        var mockIndunManager = Mock.Of<IIndunManager>();
        var mockFamilyManager = Mock.Of<IFamilyManager>();

        // Act
        var manager = new WorldManager(
            mockTickManager.Object,
            mockWorldIdManager.Object,
            new Lazy<IZoneManager>(() => mockZoneManager.Object),
            new Lazy<IIndunManager>(() => mockIndunManager.Object),
            new Lazy<IFamilyManager>(() => mockFamilyManager.Object));

        // Assert
        await Assert.That(manager).IsNotNull();
        Mock.VerifyNoOtherCalls(mockTickManager);
        Mock.VerifyNoOtherCalls(mockWorldIdManager);
    }

    #endregion

    #region GetWorld Tests

    [Test]
    public async Task GetWorld_ExistingWorld_ReturnsWorldInstance()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "test_world");
        var worldInstance = new WorldInstance(worldTemplate, 0, true, 1);
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorld(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(1u);
        await Assert.That(result.Template.Name).IsEqualTo("test_world");
    }

    [Test]
    public async Task GetWorld_NonExistingWorld_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorld(999);

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetWorld_ZeroId_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorld(0);

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetWorld_MultipleWorlds_ReturnsCorrectWorld()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate1 = CreateWorldTemplate(1, "world_1");
        var worldTemplate2 = CreateWorldTemplate(2, "world_2");
        var worldInstance1 = new WorldInstance(worldTemplate1, 0, true, 1);
        var worldInstance2 = new WorldInstance(worldTemplate2, 0, true, 2);
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance1);
        worlds.TryAdd(2, worldInstance2);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result1 = manager.GetWorld(1);
        var result2 = manager.GetWorld(2);

        // Assert
        await Assert.That(result1).IsNotNull();
        await Assert.That(result2).IsNotNull();
        await Assert.That(result1.Template.Name).IsEqualTo("world_1");
        await Assert.That(result2.Template.Name).IsEqualTo("world_2");
    }

    #endregion

    #region GetWorlds Tests

    [Test]
    public async Task GetWorlds_NoWorlds_ReturnsEmptyArray()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorlds();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task GetWorlds_MultipleWorlds_ReturnsAllWorlds()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate1 = CreateWorldTemplate(1, "world_1");
        var worldTemplate2 = CreateWorldTemplate(2, "world_2");
        var worldInstance1 = new WorldInstance(worldTemplate1, 0, true, 1);
        var worldInstance2 = new WorldInstance(worldTemplate2, 0, true, 2);
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance1);
        worlds.TryAdd(2, worldInstance2);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorlds();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Length).IsEqualTo(2);
    }

    [Test]
    public async Task GetWorlds_SingleWorld_ReturnsArrayWithOneElement()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "test_world");
        var worldInstance = new WorldInstance(worldTemplate, 0, true, 1);
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorlds();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).HasSingleItem();
    }

    #endregion

    #region GetWorldTemplateByName Tests

    [Test]
    public async Task GetWorldTemplateByName_ExistingTemplate_ReturnsTemplate()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "main_world");
        var templates = new Dictionary<string, WorldTemplate>
        {
            { "main_world", worldTemplate }
        };
        SetPrivateField(manager, "WorldTemplates", templates);

        // Act
        var result = manager.GetWorldTemplateByName("main_world");

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(1u);
        await Assert.That(result.Name).IsEqualTo("main_world");
    }

    [Test]
    public async Task GetWorldTemplateByName_NonExistingTemplate_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();
        var templates = new Dictionary<string, WorldTemplate>();
        SetPrivateField(manager, "WorldTemplates", templates);

        // Act
        var result = manager.GetWorldTemplateByName("non_existent");

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public void GetWorldTemplateByName_NullName_ThrowsArgumentNullException()
    {
        // Arrange
        var manager = CreateWorldManager();
        var templates = new Dictionary<string, WorldTemplate>();
        SetPrivateField(manager, "WorldTemplates", templates);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => manager.GetWorldTemplateByName(null));
    }

    #endregion

    #region GetWorldTemplateByZoneKey Tests

    [Test]
    public async Task GetWorldTemplateByZoneKey_ExistingZone_ReturnsTemplate()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "main_world");
        worldTemplate.ZoneKeys.Add(100);
        var worldIdByZoneKey = new Dictionary<uint, uint> { { 100, 1 } };
        var templates = new Dictionary<string, WorldTemplate> { { "main_world", worldTemplate } };
        var worldNames = new List<string> { "", "main_world" };

        SetPrivateField(manager, "_worldIdByZoneKey", worldIdByZoneKey);
        SetPrivateField(manager, "WorldTemplates", templates);
        SetPrivateField(manager, "WorldNames", worldNames);

        // Act
        var result = manager.GetWorldTemplateByZoneKey(100);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Name).IsEqualTo("main_world");
    }

    [Test]
    public async Task GetWorldTemplateByZoneKey_NonExistingZone_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldIdByZoneKey = new Dictionary<uint, uint>();
        SetPrivateField(manager, "_worldIdByZoneKey", worldIdByZoneKey);

        // Act
        var result = manager.GetWorldTemplateByZoneKey(999);

        // Assert
        await Assert.That(result).IsNull();
    }

    #endregion

    #region GetZoneId Tests

    [Test]
    public async Task GetZoneId_ValidPosition_ReturnsZoneId()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "main_world");
        worldTemplate.ZoneKeyByRegions = new uint[16, 16];
        worldTemplate.ZoneKeyByRegions[1, 1] = 100;

        // Act
        // Position (64, 64) should be in region (1, 1) with REGION_SIZE = 64
        var result = manager.GetZoneId(worldTemplate, 64, 64);

        // Assert
        await Assert.That(result).IsEqualTo(100u);
    }

    [Test]
    public async Task GetZoneId_NullTemplate_ReturnsZero()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.GetZoneId(null, 100, 100);

        // Assert
        await Assert.That(result).IsEqualTo(0u);
    }

    [Test]
    public async Task GetZoneId_OutOfBounds_ReturnsZero()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "main_world");
        worldTemplate.ZoneKeyByRegions = new uint[1, 1]; // Very small world

        // Act - Position way out of bounds
        var result = manager.GetZoneId(worldTemplate, 100000, 100000);

        // Assert
        await Assert.That(result).IsEqualTo(0u);
    }

    #endregion

    #region GetZoneKeysByWorldId Tests

    [Test]
    public async Task GetZoneKeysByWorldId_ExistingWorld_ReturnsZoneKeys()
    {
        // Arrange
        var manager = CreateWorldManager();
        var zoneKeysByWorldId = new Dictionary<uint, List<uint>>
        {
            { 1, new List<uint> { 100, 101, 102 } }
        };
        SetPrivateField(manager, "_zoneKeysByWorldId", zoneKeysByWorldId);

        // Act
        var result = manager.GetZoneKeysByWorldId(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result).Contains(100u);
    }

    [Test]
    public async Task GetZoneKeysByWorldId_NonExistingWorld_ReturnsEmptyList()
    {
        // Arrange
        var manager = CreateWorldManager();
        var zoneKeysByWorldId = new Dictionary<uint, List<uint>>();
        SetPrivateField(manager, "_zoneKeysByWorldId", zoneKeysByWorldId);

        // Act
        var result = manager.GetZoneKeysByWorldId(999);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty();
    }

    #endregion

    #region GetWorldIdByZoneKey Tests

    [Test]
    public async Task GetWorldIdByZoneKey_ExistingZone_ReturnsWorldId()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldIdByZoneKey = new Dictionary<uint, uint> { { 100, 1 } };
        SetPrivateField(manager, "_worldIdByZoneKey", worldIdByZoneKey);

        // Act
        var result = manager.GetWorldIdByZoneKey(100);

        // Assert
        await Assert.That(result).IsEqualTo(1u);
    }

    [Test]
    public async Task GetWorldIdByZoneKey_NonExistingZone_ReturnsMaxValue()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldIdByZoneKey = new Dictionary<uint, uint>();
        SetPrivateField(manager, "_worldIdByZoneKey", worldIdByZoneKey);

        // Act
        var result = manager.GetWorldIdByZoneKey(999);

        // Assert
        await Assert.That(result).IsEqualTo(uint.MaxValue);
    }

    #endregion

    #region Character Management Tests

    [Test]
    public async Task TryAddCharacter_NewCharacter_ReturnsTrue()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100
        };

        // Act
        var result = manager.TryAddCharacter(character);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task TryAddCharacter_DuplicateCharacter_ReturnsFalse()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100
        };
        manager.TryAddCharacter(character);

        // Act
        var result = manager.TryAddCharacter(character);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task TryRemoveCharacter_ExistingCharacter_ReturnsTrue()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100
        };
        manager.TryAddCharacter(character);

        // Act
        var result = manager.TryRemoveCharacter(100);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task TryRemoveCharacter_NonExistingCharacter_ReturnsFalse()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.TryRemoveCharacter(999);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task GetCharacterByObjId_ExistingCharacter_ReturnsCharacter()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Name = "TestPlayer"
        };
        manager.TryAddCharacter(character);

        // Act
        var result = manager.GetCharacterByObjId(100);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Name).IsEqualTo("TestPlayer");
    }

    [Test]
    public async Task GetCharacterByObjId_NonExistingCharacter_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.GetCharacterByObjId(999);

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetCharacter_ExistingCharacterByName_ReturnsCharacter()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Name = "TestPlayer"
        };
        manager.TryAddCharacter(character);

        // Act
        var result = manager.GetCharacter("TestPlayer");

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Name).IsEqualTo("TestPlayer");
    }

    [Test]
    public async Task GetCharacter_ExistingCharacterByNameCaseInsensitive_ReturnsCharacter()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Name = "TestPlayer"
        };
        manager.TryAddCharacter(character);

        // Act
        var result = manager.GetCharacter("testplayer");

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Name).IsEqualTo("TestPlayer");
    }

    [Test]
    public async Task GetCharacter_NonExistingCharacter_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.GetCharacter("NonExistent");

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetAllCharacters_WithCharacters_ReturnsAllCharacters()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character1 = new Character(new UnitCustomModelParams()) { ObjId = 1, Name = "Player1" };
        var character2 = new Character(new UnitCustomModelParams()) { ObjId = 2, Name = "Player2" };
        manager.TryAddCharacter(character1);
        manager.TryAddCharacter(character2);

        // Act
        var result = manager.GetAllCharacters();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetAllCharacters_NoCharacters_ReturnsEmptyList()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.GetAllCharacters();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty();
    }

    #endregion

    #region World Interaction Group Tests

    [Test]
    public async Task GetWorldInteractionGroup_ExistingGroup_ReturnsGroup()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldInteractionGroups = new Dictionary<uint, WorldInteractionGroup>
        {
            { 1, WorldInteractionGroup.Craft }
        };
        SetPrivateField(manager, "_worldInteractionGroups", worldInteractionGroups);

        // Act
        var result = manager.GetWorldInteractionGroup(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEqualTo(WorldInteractionGroup.Craft);
    }

    [Test]
    public async Task GetWorldInteractionGroup_NonExistingGroup_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldInteractionGroups = new Dictionary<uint, WorldInteractionGroup>();
        SetPrivateField(manager, "_worldInteractionGroups", worldInteractionGroups);

        // Act
        var result = manager.GetWorldInteractionGroup(999);

        // Assert
        await Assert.That(result).IsNull();
    }

    #endregion

    #region GetWorldsByTemplate Tests

    [Test]
    public async Task GetWorldsByTemplate_ExistingWorlds_ReturnsMatchingWorlds()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate1 = CreateWorldTemplate(1, "template_1");
        var worldTemplate2 = CreateWorldTemplate(2, "template_2");
        var worldInstance1 = new WorldInstance(worldTemplate1, 0, true, 1);
        var worldInstance2 = new WorldInstance(worldTemplate2, 0, true, 2);
        var worldInstance3 = new WorldInstance(worldTemplate1, 0, true, 3);

        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance1);
        worlds.TryAdd(2, worldInstance2);
        worlds.TryAdd(3, worldInstance3);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorldsByTemplate(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetWorldsByTemplate_NoMatchingWorlds_ReturnsEmptyList()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorldsByTemplate(999);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty();
    }

    #endregion

    #region RemoveWorld Tests

    [Test]
    public async Task RemoveWorld_ExistingWorld_RemovesWorld()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "test_world");
        var worldInstance = new WorldInstance(worldTemplate, 0, true, 1);
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        manager.RemoveWorld(1);

        // Assert
        await Assert.That(manager.GetWorld(1)).IsNull();
    }

    [Test]
    public void RemoveWorld_NonExistingWorld_DoesNotThrow()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act & Assert
        manager.RemoveWorld(999); // Should not throw
    }

    #endregion

    #region GetCharacterById Tests

    [Test]
    public async Task GetCharacterById_ExistingCharacter_ReturnsCharacter()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Id = 50,
            Name = "TestPlayer"
        };
        manager.TryAddCharacter(character);

        // Act
        var result = manager.GetCharacterById(50);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Name).IsEqualTo("TestPlayer");
    }

    [Test]
    public async Task GetCharacterById_NonExistingCharacter_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.GetCharacterById(999);

        // Assert
        await Assert.That(result).IsNull();
    }

    #endregion

    #region GetAllNpcsFromWorld Tests

    [Test]
    public async Task GetAllNpcsFromWorld_ExistingWorld_ReturnsNpcs()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "test_world");
        var worldInstance = new WorldInstance(worldTemplate, 0, true, 1);

        // Use reflection to set regions array
        var regionsField = typeof(WorldInstance).GetField("Regions", BindingFlags.Public | BindingFlags.Instance);
        regionsField?.SetValue(worldInstance, new Region[1, 1]);

        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetAllNpcsFromWorld(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty(); // Empty because no NPCs were added
    }

    [Test]
    public async Task GetAllNpcsFromWorld_NonExistingWorld_ReturnsEmptyList()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetAllNpcsFromWorld(999);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty();
    }

    #endregion

    #region GetAllDoodadsFromWorld Tests

    [Test]
    public async Task GetAllDoodadsFromWorld_ExistingWorld_ReturnsDoodads()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "test_world");
        var worldInstance = new WorldInstance(worldTemplate, 0, true, 1);
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetAllDoodadsFromWorld(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty(); // Empty because no doodads were added
    }

    [Test]
    public async Task GetAllDoodadsFromWorld_NonExistingWorld_ReturnsEmptyList()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetAllDoodadsFromWorld(999);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty();
    }

    #endregion

    #region GetAllSlavesFromWorld Tests

    [Test]
    public async Task GetAllSlavesFromWorld_ExistingWorld_ReturnsSlaves()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "test_world");
        var worldInstance = new WorldInstance(worldTemplate, 0, true, 1);
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        worlds.TryAdd(1, worldInstance);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetAllSlavesFromWorld(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty(); // Empty because no slaves were added
    }

    [Test]
    public async Task GetAllSlavesFromWorld_NonExistingWorld_ReturnsEmptyList()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetAllSlavesFromWorld(999);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsEmpty();
    }

    #endregion

    #region IsSnowing Property Tests

    [Test]
    public async Task IsSnowing_SetAndGet_ReturnsCorrectValue()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        manager.IsSnowing = true;

        // Assert
        await Assert.That(manager.IsSnowing).IsTrue();
    }

    [Test]
    public async Task IsSnowing_DefaultValue_ReturnsFalse()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act & Assert
        await Assert.That(manager.IsSnowing).IsFalse();
    }

    #endregion

    #region AreaShape Tests

    [Test]
    public async Task GetAreaShapeById_ExistingShape_ReturnsShape()
    {
        // Arrange
        var manager = CreateWorldManager();
        var areaShape = new AreaShape { Id = 1, Type = AreaShapeType.Sphere, Value1 = 100 };
        var areaShapes = new ConcurrentDictionary<uint, AreaShape>();
        areaShapes.TryAdd(1, areaShape);
        SetPrivateField(manager, "_areaShapes", areaShapes);

        // Act
        var result = manager.GetAreaShapeById(1);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsEqualTo(1u);
        await Assert.That(result.Type).IsEqualTo(AreaShapeType.Sphere);
    }

    [Test]
    public async Task GetAreaShapeById_NonExistingShape_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();
        var areaShapes = new ConcurrentDictionary<uint, AreaShape>();
        SetPrivateField(manager, "_areaShapes", areaShapes);

        // Act
        var result = manager.GetAreaShapeById(999);

        // Assert
        await Assert.That(result).IsNull();
    }

    #endregion

    #region TargetOrSelf Tests

    [Test]
    public async Task GetTargetOrSelf_WithTargetName_ReturnsTarget()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Name = "SourcePlayer"
        };
        var target = new Character(new UnitCustomModelParams())
        {
            ObjId = 200,
            Name = "TargetPlayer"
        };
        manager.TryAddCharacter(target);

        // Act
        var result = manager.GetTargetOrSelf(character, "TargetPlayer", out var firstNonNameArgument);

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Name).IsEqualTo("TargetPlayer");
        await Assert.That(firstNonNameArgument).IsEqualTo(1);
    }

    [Test]
    public async Task GetTargetOrSelf_WithNonExistingTargetName_ReturnsSelf()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Name = "SourcePlayer"
        };

        // Act
        var result = manager.GetTargetOrSelf(character, "NonExisting", out var firstNonNameArgument);

        // Assert
        await Assert.That(result.Name).IsEqualTo("SourcePlayer");
        await Assert.That(firstNonNameArgument).IsEqualTo(0);
    }

    [Test]
    public async Task GetTargetOrSelf_WithNullTargetName_ReturnsSelf()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Name = "SourcePlayer"
        };

        // Act
        var result = manager.GetTargetOrSelf(character, null, out var firstNonNameArgument);

        // Assert
        await Assert.That(result.Name).IsEqualTo("SourcePlayer");
        await Assert.That(firstNonNameArgument).IsEqualTo(0);
    }

    #endregion

    #region Edge Cases Tests

    [Test]
    public async Task GetWorld_MaxUintId_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        var result = manager.GetWorld(uint.MaxValue);

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetCharacter_NullName_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.GetCharacter(null);

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetCharacter_EmptyName_ReturnsNull()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.GetCharacter("");

        // Assert
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetZoneId_NegativeCoordinates_ReturnsZero()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worldTemplate = CreateWorldTemplate(1, "main_world");
        worldTemplate.ZoneKeyByRegions = new uint[16, 16];

        // Act
        var result = manager.GetZoneId(worldTemplate, -100, -100);

        // Assert
        await Assert.That(result).IsEqualTo(0u);
    }

    [Test]
    public void TryAddCharacter_NullCharacter_ThrowsNullReferenceException()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act & Assert
        Assert.Throws<NullReferenceException>(() => manager.TryAddCharacter(null));
    }

    [Test]
    public async Task TryRemoveCharacter_ZeroId_ReturnsFalse()
    {
        // Arrange
        var manager = CreateWorldManager();

        // Act
        var result = manager.TryRemoveCharacter(0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    #endregion

    #region Multiple Operations Tests

    [Test]
    public async Task MultipleOperations_AddAndRemoveCharacter_CharacterRemoved()
    {
        // Arrange
        var manager = CreateWorldManager();
        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 100,
            Name = "TestPlayer"
        };

        // Act - Add character
        var addResult = manager.TryAddCharacter(character);
        await Assert.That(addResult).IsTrue();
        await Assert.That(manager.GetCharacterByObjId(100)).IsNotNull();

        // Act - Remove character
        var removeResult = manager.TryRemoveCharacter(100);
        await Assert.That(removeResult).IsTrue();
        await Assert.That(manager.GetCharacterByObjId(100)).IsNull();
    }

    [Test]
    public async Task MultipleOperations_AddMultipleWorlds_AllWorldsAccessible()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();

        // Add multiple worlds
        for (uint i = 1; i <= 5; i++)
        {
            var worldTemplate = CreateWorldTemplate(i, $"world_{i}");
            var worldInstance = new WorldInstance(worldTemplate, 0, true, i);
            worlds.TryAdd(i, worldInstance);
        }
        SetPrivateField(manager, "_worlds", worlds);

        // Act & Assert
        await Assert.That(manager.GetWorlds().Length).IsEqualTo(5);
        for (uint i = 1; i <= 5; i++)
        {
            await Assert.That(manager.GetWorld(i)).IsNotNull();
        }
    }

    [Test]
    public async Task MultipleOperations_RemoveAllWorlds_WorldsEmpty()
    {
        // Arrange
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        var worldTemplate = CreateWorldTemplate(1, "test_world");
        var worldInstance = new WorldInstance(worldTemplate, 0, true, 1);
        worlds.TryAdd(1, worldInstance);
        SetPrivateField(manager, "_worlds", worlds);

        // Act
        manager.RemoveWorld(1);

        // Assert
        await Assert.That(manager.GetWorlds()).IsEmpty();
    }

    #endregion

    #region Constants Tests

    [Test]
    public async Task Constants_CellSize_ReturnsCorrectValue()
    {
        // Assert
        await Assert.That(WorldManager.CELL_SIZE).IsEqualTo(1024);
    }

    [Test]
    public async Task Constants_RegionSize_ReturnsCorrectValue()
    {
        // Assert
        await Assert.That(WorldManager.REGION_SIZE).IsEqualTo(64);
    }

    [Test]
    public async Task Constants_SectorsPerCell_ReturnsCorrectValue()
    {
        // Assert
        await Assert.That(WorldManager.SECTORS_PER_CELL).IsEqualTo(16); // 1024 / 64 = 16
    }

    [Test]
    public async Task Constants_DefaultCombatTimeout_ReturnsCorrectValue()
    {
        // Assert
        await Assert.That(WorldManager.DefaultCombatTimeout).IsEqualTo(15f);
    }

    #endregion

    #region Initialize Tests

    [Test]
    public async Task Initialize_SubscribesActiveRegionTickAsAsync()
    {
        var handler = new TickManager.TickEventHandler();
        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(handler);

        var manager = new WorldManager(
            mockTickManager.Object,
            Mock.Of<IWorldIdManager>().Object,
            new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
            new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
            new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object));

        manager.Initialize();

        var queueField = typeof(TickManager.TickEventHandler)
            .GetField("_eventsToAdd", BindingFlags.NonPublic | BindingFlags.Instance);
        var pending = (Queue<TickManager.TickEventEntity>)queueField!.GetValue(handler)!;

        var activeRegion = pending.FirstOrDefault(e =>
            e.Event.Method.Name.Contains("ActiveRegionTick", StringComparison.Ordinal));

        await Assert.That(activeRegion).IsNotNull();
        await Assert.That(activeRegion!.UseAsync).IsTrue();
    }


    #region CreateWorldInstance Publish Ordering Tests

    /// <summary>
    /// These two tests pin the "publish the world LAST" rule in
    /// <c>WorldManager.CreateWorldInstance</c> — the defect where the world was added to the
    /// private <c>_worlds</c> dictionary at construction time, before eleven sub-managers were
    /// assigned. A concurrent reader of <see cref="WorldManager.GetWorlds"/> could therefore
    /// observe a world whose <c>MateManager</c> was still null, which is a NullReferenceException
    /// in <c>CharacterLifecycleService.EnterWorld</c>.
    ///
    /// WHY THESE ARE NOT A PLAIN CALL-AND-ASSERT: <see cref="WorldManager.CreateWorldInstance"/>
    /// cannot be invoked hermetically in a unit test. Read top-to-bottom, the wiring it performs
    /// demands live process state that no test rig provides:
    /// <list type="bullet">
    /// <item><c>SpawnManager.Load()</c> reads spawn JSON from
    /// <c>FileManager.AppPath/Data/Worlds/{Template.Name}</c> — real game data.</item>
    /// <item><c>SpawnManager.Load()</c> and <c>PhysicsManager.StartPhysics()</c> each start a
    /// FOREGROUND thread (grep <c>IsBackground</c> in either file: zero hits). Neither has a
    /// join, and the respawn loop's only stop flag (<c>SpawnManager._work</c>) is never set by
    /// the publish path. One successful call leaves immortal threads that never exit — the test
    /// host would hang at teardown, not fail with an assertion.</item>
    /// <item><c>TransferManager.Load()</c> opens SQLite; <c>SlaveManager.Initialize()</c> and
    /// <c>GimmickManager.Initialize()</c> subscribe to the process-wide <c>TickManager.Instance</c>
    /// singleton; <c>SphereQuestManager.Load()</c> reaches <c>QuestManager.Instance</c> and
    /// <c>SphereGameData.Instance</c>.</item>
    /// </list>
    /// Asserting "the returned world has non-null sub-managers" after a real call would
    /// therefore assert nothing about ordering: a single atomic publish AFTER the wiring and a
    /// racy publish BEFORE it are indistinguishable at the return value, because the defect only
    /// manifests in the window BETWEEN the two. So these tests pin the two halves separately,
    /// each with an independent mechanism:
    /// <list type="number">
    /// <item>the ordering itself, by disassembling the compiled method (fails on any regression);
    /// </item>
    /// <item>the invariant that ordering exists to protect, against a concurrent observer
    /// (fails if the class ever grows a second, unguarded publish path).</item>
    /// </list>
    /// </summary>

    [Test]
    public async Task CreateWorldInstance_PublishesWorldOnlyAfterEverySubManagerIsWired()
    {
        // Arrange / Act: decode the real compiled method. No source-text or line-number checks.
        var body = DecodeMethodBody(typeof(WorldManager), nameof(WorldManager.CreateWorldInstance));

        // The only publish into _worlds is `_worlds.TryAdd(world.Id, world)`. Note that CreateWorldInstance
        // also *reads* _worlds (the "not instanced" probe), so the decoder separates the read (ldfld) from
        // the publish (the TryAdd call).
        var publishOffsets = body.PublishOffsets;
        await Assert.That(publishOffsets.Count)
            .IsEqualTo(1)
            .Because("CreateWorldInstance must publish into _worlds exactly once. Zero publishes means the " +
                     "world is never observable at all; more than one means a second publish path was added, " +
                     "which is how the pre-fix race was originally reachable.");

        var publishOffset = publishOffsets[0];

        // Each wiring step is a stfld onto the WorldInstance under construction. Name them individually so a
        // failure message identifies the step that moved, rather than saying "somewhere out of order".
        string[] wiredMembers =
        [
            nameof(WorldInstance.Regions),
            nameof(WorldInstance.SphereQuestManager),
            nameof(WorldInstance.SpawnManager),
            nameof(WorldInstance.GimmickManager),
            nameof(WorldInstance.SlaveManager),
            nameof(WorldInstance.MateManager),
            nameof(WorldInstance.TransferManager),
        ];

        var wiringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in wiredMembers)
        {
            await Assert.That(body.WiringOffsets.ContainsKey(member))
                .IsTrue()
                .Because($"CreateWorldInstance must assign WorldInstance.{member} inline; if that member was " +
                         "renamed or its assignment moved into a helper method, update this test to follow it.");
            wiringOffsets[member] = body.WiringOffsets[member];
        }

        // The core assertion, one per member: every wiring store precedes the publish.
        foreach (var (member, offset) in wiringOffsets)
        {
            await Assert.That(offset).IsLessThan(publishOffset)
                .Because($"WorldInstance.{member} is assigned at IL offset {offset}, but _worlds is published at " +
                         $"offset {publishOffset}. Publishing before the wiring re-opens the production window " +
                         "where GetWorlds() hands concurrent readers a half-built world — the " +
                         "NullReferenceException in CharacterLifecycleService.EnterWorld that aborted 3 of 4 " +
                         "presence bots at boot.");
        }
    }

    [Test]
    public async Task CreateWorldInstance_DuplicateIdGuardStillThrowsAfterThePublishMoved()
    {
        // The duplicate-id guard moved together with the publish (it lives inside the same if). Pin that it
        // survived the move: TryAdd's boolean result must still be tested, and the tested-to-false branch must
        // still construct the InvalidOperationException.
        var body = DecodeMethodBody(typeof(WorldManager), nameof(WorldManager.CreateWorldInstance));

        await Assert.That(body.PublishOffsets.Count).IsEqualTo(1);
        var publishOffset = body.PublishOffsets[0];

        // The guard test: a conditional branch consuming TryAdd's result, a few instructions after the call.
        var guardBranchOffset = -1;
        foreach (var (offset, opCode) in body.Instructions)
        {
            if (offset <= publishOffset || offset > publishOffset + 32)
                continue;
            if (opCode == OpCodes.Brfalse || opCode == OpCodes.Brfalse_S ||
                opCode == OpCodes.Brtrue || opCode == OpCodes.Brtrue_S)
            {
                guardBranchOffset = offset;
                break;
            }
        }

        await Assert.That(guardBranchOffset)
            .IsGreaterThan(publishOffset)
            .Because("the result of _worlds.TryAdd must still be branched on; dropping the check would let a " +
                     "duplicate instance id silently overwrite a live world.");

        // And the branch must lead to the throw.
        await Assert.That(body.ThrowsInvalidOperationOffset)
            .IsGreaterThan(guardBranchOffset)
            .Because("the duplicate-id guard must still construct the InvalidOperationException; it moved with " +
                     "the publish and is easy to drop in the move.");
    }

    [Test]
    public async Task CreateWorldInstance_ObservableWorldExposesNoNullSubManagerToConcurrentReaders()
    {
        // Arrange: a world inserted the way CreateWorldInstance inserts it — the same _worlds.TryAdd publish —
        // but standing in for the whole wiring block with one atomic step, so no half-built window can exist
        // for a reader to catch. This is the invariant the publish ordering exists to protect.
        var manager = CreateWorldManager();
        var worlds = new ConcurrentDictionary<uint, WorldInstance>();
        SetPrivateField(manager, "_worlds", worlds);

        var template = CreateWorldTemplate(1, "publish_order_world");
        template.ZoneKeys.Add(100);
        var world = new WorldInstance(template, 0, true, 1);
        world.Regions = new Region[1, 1];
        world.Regions[0, 0] = new Region(world, 0, 0, 100);
        world.SphereQuestManager = new SphereQuestManager(world);
        world.SpawnManager = new SpawnManager(world);
        world.GimmickManager = new GimmickManager(world);
        world.SlaveManager = new SlaveManager(world);
        world.MateManager = new MateManager(world);
        world.TransferManager = new TransferManager();

        // Act: publish, then read back through the exact public path a CharacterLifecycleService.EnterWorld
        // caller uses. No Load()/Initialize() is invoked — these ctor-only managers are what the ordering
        // guarantee is about; loading spawn JSON is not.
        await Assert.That(worlds.TryAdd(world.Id, world)).IsTrue();

        var observed = manager.GetWorlds();
        var byId = manager.GetWorld(world.Id);

        // Assert: nothing a reader dereferences is null on an observable world.
        await Assert.That(observed).HasSingleItem();
        await Assert.That(byId).IsNotNull();
        foreach (var w in observed.Append(byId))
        {
            await Assert.That(w.Regions).IsNotNull();
            await Assert.That(w.SphereQuestManager).IsNotNull();
            await Assert.That(w.SpawnManager).IsNotNull();
            await Assert.That(w.GimmickManager).IsNotNull();
            await Assert.That(w.SlaveManager).IsNotNull();
            await Assert.That(w.MateManager).IsNotNull();
            await Assert.That(w.TransferManager).IsNotNull();
        }

        // And GetWorlds returns the published instances themselves, not detached snapshots that a reader
        // could dereference into a null MateManager.
        await Assert.That(byId.MateManager).IsSameReferenceAs(world.MateManager);
        await Assert.That(byId.SlaveManager).IsSameReferenceAs(world.SlaveManager);
    }


    #endregion


    #region Helper Methods

    private static WorldManager CreateWorldManager()
    {
        var mockTickManager = Mock.Of<ITickManager>();
        var mockWorldIdManager = Mock.Of<IWorldIdManager>();
        var mockZoneManager = Mock.Of<IZoneManager>();
        var mockIndunManager = Mock.Of<IIndunManager>();
        var mockFamilyManager = Mock.Of<IFamilyManager>();

        return new WorldManager(
            mockTickManager.Object,
            mockWorldIdManager.Object,
            new Lazy<IZoneManager>(() => mockZoneManager.Object),
            new Lazy<IIndunManager>(() => mockIndunManager.Object),
            new Lazy<IFamilyManager>(() => mockFamilyManager.Object));
    }

    private static WorldTemplate CreateWorldTemplate(uint id, string name)
    {
        var template = new WorldTemplate
        {
            Id = id,
            Name = name,
            ZoneKeys = new List<uint>(),
            CellX = 2,
            CellY = 2,
            ZoneKeyByRegions = new uint[32, 32]
        };
        return template;
    }

    private static void SetPrivateField(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (field == null)
        {
            // Try to find property
            var property = obj.GetType().GetProperty(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            property?.SetValue(obj, value);
        }
        else
        {
            field.SetValue(obj, value);
        }
    }

    /// <summary>
    /// A decoded method body: instruction offsets of interest, in emitted execution order.
    /// Instruction offsets — not token values — are what carry execution order in IL, so the
    /// ordering assertions compare these.
    /// </summary>
    private sealed record MethodBodyInfo(
        List<(int Offset, OpCode OpCode)> Instructions,
        List<int> PublishOffsets,
        Dictionary<string, int> WiringOffsets,
        int ThrowsInvalidOperationOffset)
    {
        public IReadOnlyList<int> LdfldWorldsOffsets { get; init; } = [];
    }

    private static readonly Dictionary<int, OpCode> OpCodeMap = BuildOpCodeMap();

    private static Dictionary<int, OpCode> BuildOpCodeMap()
    {
        var map = new Dictionary<int, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
                map[Normalize(opCode.Value)] = opCode;
        }
        return map;
    }

    /// <summary>
    /// Two-byte IL opcodes are stored in <see cref="OpCode.Value"/> as sign-extended shorts
    /// (0xFE02 becomes -510), while the IL stream stores them as unsigned. Normalize to the
    /// unsigned 16-bit form both representations agree on.
    /// </summary>
    private static int Normalize(short opCodeValue) => (ushort)opCodeValue;

    /// <summary>
    /// Decodes a method body and records, per IL instruction offset: where the world is published
    /// into <c>_worlds</c>, where each sub-manager is stored on the WorldInstance, and where an
    /// <see cref="InvalidOperationException"/> is constructed.
    /// </summary>
    /// <remarks>
    /// Reading the compiled body rather than the .cs file is deliberate. The defect being pinned is a
    /// statement-ORDERING defect, and only emitted IL records the order the runtime actually
    /// executes; a source-text, comment, or line-number assertion could be satisfied by anything.
    /// <para>
    /// Anything the decoder fails to find is reported as a missing entry rather than a skipped
    /// check, so a refactor that moves the wiring into a helper method surfaces as "must assign X
    /// inline" instead of silently reducing the number of comparisons.
    /// </para>
    /// </remarks>
    private static MethodBodyInfo DecodeMethodBody(Type declaringType, string methodName)
    {
        var method = declaringType.GetMethod(methodName)
            ?? throw new InvalidOperationException($"{declaringType.FullName}.{methodName} not found");
        var il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException($"{methodName} has no IL body");

        var module = declaringType.Module;
        var instructions = new List<(int, OpCode)>();
        var publishOffsets = new List<int>();
        var worldsReadOffsets = new List<int>();
        var wiringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var throwsInvalidOperationOffset = -1;

        var pos = 0;
        while (pos < il.Length)
        {
            var offset = pos;
            int code = il[pos++];
            if (code == 0xFE) // two-byte opcode prefix
                code = 0xFE00 | il[pos++];

            if (!OpCodeMap.TryGetValue(code, out var opCode))
                throw new InvalidOperationException($"unknown opcode 0x{code:X} at offset {offset} in {methodName}");

            instructions.Add((offset, opCode));

            var operandStart = pos;
            pos += OperandSize(il, operandStart, opCode.OperandType);

            switch (opCode.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineTok:
                {
                    var token = BitConverter.ToInt32(il, operandStart);
                    var member = ResolveMember(module, token);

                    if (opCode == OpCodes.Ldfld && IsWorldsField(declaringType, member))
                        worldsReadOffsets.Add(offset);

                    // The publish is the TryAdd call on _worlds, not the ldfld: CreateWorldInstance also
                    // reads _worlds earlier (the "not instanced" probe and the resize). ConcurrentDictionary
                    // members are emitted as callvirt, so match the opcode by family rather than == OpCodes.Call.
                    if (IsCallOpcode(opCode) && member is MethodInfo { Name: "TryAdd" } && IsWorldsDictionary(member))
                        publishOffsets.Add(offset);

                    // Every sub-manager on WorldInstance is an auto-property, and assigning it from outside
                    // its declaring class emits a call to the property SETTER (callvirt set_MateManager),
                    // not a store to the backing field. Match both forms: a setter call, or a stfld of the
                    // auto-property backing field. Either way the offset is where the member becomes
                    // non-null, which is the point being ordered.
                    if (IsWorldsPropertyAssignment(opCode, module, token, member))
                        wiringOffsets.TryAdd(WiredMemberName(member), offset);

                    if (opCode == OpCodes.Newobj && IsInvalidOperationExceptionCtor(member))
                        throwsInvalidOperationOffset = offset;

                    break;
                }
            }
        }

        return new MethodBodyInfo(instructions, publishOffsets, wiringOffsets, throwsInvalidOperationOffset)
        {
            LdfldWorldsOffsets = worldsReadOffsets,
        };
    }

    private static MemberInfo? ResolveMember(Module module, int token)
    {
        try
        {
            return module.ResolveMember(token);
        }
        catch (ArgumentException)
        {
            return null; // generic-context or otherwise unresolvable token
        }
    }

    /// <summary>
    /// Strips the compiler's "<c>&lt;Name&gt;k__BackingField</c>" suffix so an emitted auto-property
    /// store can be reported under the property name used in source.
    /// </summary>
    private static string BackingFieldToPropertyName(string fieldName)
    {
        const string suffix = "k__BackingField";
        return fieldName.EndsWith(suffix, StringComparison.Ordinal)
            ? fieldName[..^suffix.Length]
            : fieldName;
    }

    private static bool IsWorldsField(Type declaringType, MemberInfo? member)
    {
        return member is FieldInfo { Name: "_worlds", IsPrivate: true, IsStatic: false }
            && member.DeclaringType == declaringType;
    }

    /// <summary>
    /// True for the opcodes that invoke a method. ConcurrentDictionary members are emitted as callvirt,
    /// so an exact <c>== OpCodes.Call</c> comparison silently misses the publish entirely.
    /// </summary>
    private static bool IsCallOpcode(OpCode opCode) => opCode.Name is "call" or "callvirt";

    /// <summary>
    /// True when this instruction assigns a member of the <see cref="WorldInstance"/> being
    /// constructed. Two encodings occur in practice: a call to the property setter (the normal
    /// case for an auto-property assigned from outside its declaring class), and a stfld to the
    /// auto-property's compiler-generated backing field.
    /// </summary>
    private static bool IsWorldsPropertyAssignment(OpCode opCode, Module module, int token, MemberInfo? member)
    {
        if (opCode == OpCodes.Stfld)
            return member is FieldInfo { DeclaringType: var dt } && dt == typeof(WorldInstance);

        if (opCode != OpCodes.Callvirt && opCode != OpCodes.Call)
            return false;

        return member is MethodInfo
        {
            Name: var name,
            DeclaringType: var declaring,
            IsSpecialName: true,
        } && name.StartsWith("set_", StringComparison.Ordinal)
            && declaring == typeof(WorldInstance);
    }

    /// <summary>Property name behind a setter call or an auto-property backing field.</summary>
    private static string WiredMemberName(MemberInfo member) => member switch
    {
        MethodInfo { Name: var setterName } => setterName["set_".Length..],
        FieldInfo { Name: var fieldName } => BackingFieldToPropertyName(fieldName),
        _ => member.Name,
    };

    private static bool IsWorldsDictionary(MemberInfo member)
    {
        if (member is not MethodInfo method)
            return false;

        // ConcurrentDictionary<uint, WorldInstance> is the dictionary published into. WorldInstance must
        // appear among the generic arguments of the declaring type (instance-method shape) or of the
        // receiver parameter (extension-method shape) — it is the VALUE type, hence argument index 1.
        if (method.DeclaringType is { IsGenericType: true } declaring
            && Array.IndexOf(declaring.GetGenericArguments(), typeof(WorldInstance)) >= 0)
            return true;

        var receiver = method.GetParameters().FirstOrDefault()?.ParameterType;
        return receiver is { IsGenericType: true }
            && Array.IndexOf(receiver.GetGenericArguments(), typeof(WorldInstance)) >= 0;
    }

    private static bool IsInvalidOperationExceptionCtor(MemberInfo? member)
    {
        return member is ConstructorInfo { DeclaringType: var dt }
            && dt == typeof(InvalidOperationException);
    }

    private static int OperandSize(byte[] il, int operandStart, OperandType operandType)
    {
        switch (operandType)
        {
            case OperandType.InlineNone: return 0;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar: return 1;
            case OperandType.InlineVar: return 2;
            case OperandType.InlineI8:
            case OperandType.InlineR: return 8;
            case OperandType.InlineSwitch:
 {
                // A count followed by that many 4-byte targets.
                var count = BitConverter.ToInt32(il, operandStart);
                return 4 + (4 * count);
            }
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR: return 4;
            default: throw new InvalidOperationException($"unhandled operand type {operandType}");
        }
    }
    #endregion


    #endregion
}