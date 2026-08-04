using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

public class QuestActCheckGuardTests
{
    private const uint GuardNpcId = 500;

    /// <summary>
    /// Builds a QuestActCheckGuard act for GuardNpcId, plus a Quest owned by a real Character
    /// that is attached to a fresh WorldInstance.
    /// </summary>
    private static (QuestActCheckGuard Act, Quest Quest, WorldInstance World) SetupActAndQuest()
    {
        var questTemplate = new QuestTemplate { Id = 123 };
        var componentTemplate = new QuestComponentTemplate(questTemplate) { KindId = QuestComponentKind.Start };

        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 1,
            Id = 1,
            Name = "Tester"
        };

        var worldTemplate = new WorldTemplate
        {
            Id = 1,
            Name = "test_world",
            ZoneKeys = new List<uint>(),
            CellX = 2,
            CellY = 2,
            ZoneKeyByRegions = new uint[32, 32]
        };
        var world = new WorldInstance(worldTemplate, 0, true, 1);
        // Setting ParentWorld via the property setter requires the DI WorldManager.Instance
        // singleton (Transform.InstanceId → WorldManager.Instance.GetWorld), which is not
        // available in unit tests — set the backing field directly instead.
        var parentWorldField = typeof(GameObject).GetField("_parentWorld", BindingFlags.NonPublic | BindingFlags.Instance);
        parentWorldField?.SetValue(character, world);

        var mockQuestManager = Mock.Of<IQuestManager>();
        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var taskManagerInstance = new TaskManager(mockTickManager.Object);
        var mockSkillManager = Mock.Of<ISkillManager>();
        var mockExpressTextManager = Mock.Of<IExpressTextManager>();
        var mockWorldManager = Mock.Of<IWorldManager>();

        var quest = new Quest(
            questTemplate,
            character,
            mockQuestManager.Object,
            taskManagerInstance,
            mockSkillManager.Object,
            mockExpressTextManager.Object,
            mockWorldManager.Object);

        var act = new QuestActCheckGuard(componentTemplate) { NpcId = GuardNpcId };
        return (act, quest, world);
    }

    private static Npc CreateGuardInWorld(WorldInstance world, uint objId, bool alive)
    {
        var npc = new Npc
        {
            ObjId = objId,
            TemplateId = GuardNpcId,
            Hp = alive ? 100 : 0,
            MaxHp = 100
        };
        world.AddObject(npc);
        return npc;
    }

    [Test]
    public async Task RunAct_GuardAliveInWorld_ReturnsTrue()
    {
        // Arrange
        var (act, quest, world) = SetupActAndQuest();
        CreateGuardInWorld(world, objId: 100, alive: true);

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RunAct_GuardDeadInWorld_ReturnsFalse()
    {
        // Arrange
        // Regression: the old stub returned true unconditionally, silently passing the
        // escort/protect objective even when the guard NPC had been killed.
        var (act, quest, world) = SetupActAndQuest();
        CreateGuardInWorld(world, objId: 100, alive: false);

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_GuardNotSpawned_ReturnsFalse()
    {
        // Arrange
        // Regression: a guard that is missing or has despawned must not pass the check.
        var (act, quest, _) = SetupActAndQuest();

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }
}
