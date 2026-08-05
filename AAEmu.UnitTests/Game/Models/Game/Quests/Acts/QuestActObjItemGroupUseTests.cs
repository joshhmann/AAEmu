using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

/// <summary>
/// QuestActObjItemGroupUse — using any item belonging to the quest item group must count
/// toward the objective, while using a non-group item must not. Mirrors the
/// QuestActObjItemUse pattern (OnItemUse event + AddObjective), group-expanded via the
/// QuestManager item-group index.
/// </summary>
public class QuestActObjItemGroupUseTests
{
    private const uint GroupId = 1;
    private const uint GroupItemA = 100;
    private const uint GroupItemB = 101;
    private const uint NonGroupItem = 999;
    private const int ObjectiveCount = 2;

    private static readonly uint[] GroupItems = [GroupItemA, GroupItemB];

    /// <summary>
    /// Builds a QuestActObjItemGroupUse act for GroupId plus a Quest owned by a real
    /// Character (real UnitEvents so handler registration actually works). The
    /// QuestManager singleton is seeded with the item-group index.
    /// </summary>
    private static (QuestActObjItemGroupUse Act, Quest Quest, QuestAct QuestAct) SetupActAndQuest()
    {
        SeedQuestManager();

        var questTemplate = new QuestTemplate { Id = 123 };
        var componentTemplate = new QuestComponentTemplate(questTemplate) { KindId = QuestComponentKind.Start };

        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 1,
            Id = 1,
            Name = "Tester"
        };

        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var taskManagerInstance = new TaskManager(mockTickManager.Object);

        var quest = new Quest(
            questTemplate,
            character,
            Mock.Of<IQuestManager>().Object,
            taskManagerInstance,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object);

        var act = new QuestActObjItemGroupUse(componentTemplate)
        {
            ItemGroupId = GroupId,
            Count = ObjectiveCount,
            ThisComponentObjectiveIndex = 0
        };

        var questStep = new QuestStep(QuestComponentKind.Progress, quest);
        var questComponent = new QuestComponent(questStep, componentTemplate);
        var questAct = new QuestAct(questComponent, act);
        return (act, quest, questAct);
    }

    /// <summary>
    /// Injects a QuestManager with a seeded item-group index into the singleton,
    /// so QuestManager.Instance.CheckGroupItem works in tests.
    /// </summary>
    private static void SeedQuestManager()
    {
        var manager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        var groupItemsField = typeof(QuestManager).GetField("_groupItems", BindingFlags.NonPublic | BindingFlags.Instance);
        groupItemsField?.SetValue(manager, new Dictionary<uint, List<uint>> { [GroupId] = [.. GroupItems] });
        var instanceField = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        instanceField?.SetValue(null, manager);
    }

    private static void FireItemUse(Quest quest, uint itemId)
    {
        quest.Owner.Events.OnItemUse(quest.Owner, new OnItemUseArgs { ItemId = itemId });
    }

    [Test]
    public async Task OnItemUse_GroupItemA_CountsTowardObjective()
    {
        // Arrange
        var (act, quest, questAct) = SetupActAndQuest();
        act.InitializeAction(quest, questAct);

        // Act
        FireItemUse(quest, GroupItemA);
        FireItemUse(quest, GroupItemA);

        // Assert
        await Assert.That(quest.Objectives[0]).IsEqualTo(ObjectiveCount);
    }

    [Test]
    public async Task OnItemUse_GroupItemB_CountsTowardObjective()
    {
        // Arrange
        // Regression: only the first group member used to count if the act was implemented
        // with a single-item check; every member of the group must count.
        var (act, quest, questAct) = SetupActAndQuest();
        act.InitializeAction(quest, questAct);

        // Act
        FireItemUse(quest, GroupItemB);

        // Assert
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
    }

    [Test]
    public async Task OnItemUse_NonGroupItem_DoesNotCount()
    {
        // Arrange
        var (act, quest, questAct) = SetupActAndQuest();
        act.InitializeAction(quest, questAct);

        // Act
        FireItemUse(quest, NonGroupItem);
        FireItemUse(quest, NonGroupItem);

        // Assert
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
    }

    [Test]
    public async Task RunAct_WithObjectiveCountMet_ReturnsTrue()
    {
        // Arrange
        var (act, quest, questAct) = SetupActAndQuest();
        act.InitializeAction(quest, questAct);
        FireItemUse(quest, GroupItemA);
        FireItemUse(quest, GroupItemB);

        // Act
        var result = act.RunAct(quest, questAct, quest.Objectives[0]);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RunAct_WithObjectiveCountNotMet_ReturnsFalse()
    {
        // Arrange
        var (act, quest, questAct) = SetupActAndQuest();
        act.InitializeAction(quest, questAct);
        FireItemUse(quest, GroupItemA);

        // Act
        var result = act.RunAct(quest, questAct, quest.Objectives[0]);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task FinalizeAction_UnregistersEventHandler()
    {
        // Arrange
        var (act, quest, questAct) = SetupActAndQuest();
        act.InitializeAction(quest, questAct);
        act.FinalizeAction(quest, questAct);

        // Act
        FireItemUse(quest, GroupItemA);

        // Assert
        await Assert.That(quest.Objectives[0]).IsEqualTo(0);
    }
}
