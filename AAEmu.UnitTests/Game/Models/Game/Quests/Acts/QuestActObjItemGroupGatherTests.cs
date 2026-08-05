using System.Reflection;
using System.Runtime.Serialization;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

/// <summary>
/// QuestActObjItemGroupGather — any item belonging to the quest item group must count
/// toward the objective (group item A, group item B, summed), while non-group items
/// must not. Mirrors the QuestActObjItemGather objective-counting pattern, group-expanded.
/// </summary>
public class QuestActObjItemGroupGatherTests
{
    private const uint GroupId = 1;
    private const uint GroupItemA = 100;
    private const uint GroupItemB = 101;
    private const uint NonGroupItem = 999;
    private const int ObjectiveCount = 2;

    private static readonly uint[] GroupItems = [GroupItemA, GroupItemB];

    /// <summary>
    /// Builds a QuestActObjItemGroupGather act for GroupId plus a Quest owned by a real
    /// Character. The QuestManager singleton is seeded with the item-group index so the
    /// act can resolve GroupId -> item ids without a database.
    /// </summary>
    private static (QuestActObjItemGroupGather Act, Quest Quest) SetupActAndQuest(Inventory inventory)
    {
        SeedQuestManager();

        var questTemplate = new QuestTemplate { Id = 123 };
        var componentTemplate = new QuestComponentTemplate(questTemplate) { KindId = QuestComponentKind.Start };

        var character = new Character(new UnitCustomModelParams())
        {
            ObjId = 1,
            Id = 1,
            Name = "Tester",
            Inventory = inventory
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

        var act = new QuestActObjItemGroupGather(componentTemplate)
        {
            ItemGroupId = GroupId,
            Count = ObjectiveCount,
            ThisComponentObjectiveIndex = 0
        };
        return (act, quest);
    }

    /// <summary>
    /// Builds the QuestAct + QuestComponent chain required by the event-driven path
    /// (InitializeAction / OnItemGroupGather).
    /// </summary>
    private static (QuestActObjItemGroupGather Act, Quest Quest, QuestAct QuestAct) SetupWithQuestAct(Inventory inventory)
    {
        var (act, quest) = SetupActAndQuest(inventory);
        var questStep = new QuestStep(QuestComponentKind.Progress, quest);
        var questComponent = new QuestComponent(questStep, act.ParentComponent);
        var questAct = new QuestAct(questComponent, act);
        return (act, quest, questAct);
    }

    /// <summary>
    /// Injects a QuestManager with a seeded item-group index into the singleton,
    /// so QuestManager.Instance.GetGroupItems/CheckGroupItem work in tests.
    /// </summary>
    private static void SeedQuestManager()
    {
        var manager = new QuestManager(Mock.Of<ITaskManager>().Object, Mock.Of<IZoneManager>().Object);
        var groupItemsField = typeof(QuestManager).GetField("_groupItems", BindingFlags.NonPublic | BindingFlags.Instance);
        groupItemsField?.SetValue(manager, new Dictionary<uint, List<uint>> { [GroupId] = [.. GroupItems] });
        var instanceField = typeof(Singleton<QuestManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static);
        instanceField?.SetValue(null, manager);
    }

    /// <summary>
    /// Builds an Inventory (bypassing the ItemManager singleton) holding the given items.
    /// </summary>
    private static Inventory CreateInventory(params (uint TemplateId, int Count)[] items)
    {
        var inventory = (Inventory)FormatterServices.GetUninitializedObject(typeof(Inventory));
        var bag = new ItemContainer(1, SlotType.Inventory, createWithNewId: false, null);
        var slot = 0;
        foreach (var (templateId, count) in items)
        {
            bag.Items.Add(new ItemMock((uint)(++slot), new ItemTemplate { Id = templateId }, count));
        }

        // _itemContainers is an auto-property with a private setter; set its backing field
        var containersField = typeof(Inventory).GetField("<_itemContainers>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?? typeof(Inventory).GetField("_itemContainers", BindingFlags.NonPublic | BindingFlags.Instance);
        containersField?.SetValue(inventory, new Dictionary<SlotType, ItemContainer> { [SlotType.Inventory] = bag });
        return inventory;
    }

    [Test]
    public async Task RunAct_WithEnoughOfGroupItemA_ReturnsTrue()
    {
        // Arrange
        var (act, quest) = SetupActAndQuest(CreateInventory((GroupItemA, ObjectiveCount)));

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RunAct_WithEnoughOfGroupItemB_ReturnsTrue()
    {
        // Arrange
        // Regression: only the first group member used to count if the act was implemented
        // with a single-item check; every member of the group must count.
        var (act, quest) = SetupActAndQuest(CreateInventory((GroupItemB, ObjectiveCount)));

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RunAct_GroupCountsSumAcrossDifferentGroupItems_ReturnsTrue()
    {
        // Arrange
        // 1x group item A + 1x group item B = 2 total, objective requires 2
        var (act, quest) = SetupActAndQuest(CreateInventory((GroupItemA, 1), (GroupItemB, 1)));

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RunAct_WithOnlyNonGroupItems_ReturnsFalse()
    {
        // Arrange
        var (act, quest) = SetupActAndQuest(CreateInventory((NonGroupItem, ObjectiveCount)));

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_WithPartialGroupItems_ReturnsFalse()
    {
        // Arrange
        var (act, quest) = SetupActAndQuest(CreateInventory((GroupItemA, 1)));

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_WithUnknownGroup_ReturnsFalse()
    {
        // Arrange
        var (act, quest) = SetupActAndQuest(CreateInventory((GroupItemA, ObjectiveCount)));
        act.ItemGroupId = 9999;

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// Adds an item to the test inventory's bag after construction (simulates acquiring an item).
    /// </summary>
    private static void AddItemToInventory(Inventory inventory, uint templateId, int count)
    {
        var bag = inventory._itemContainers[SlotType.Inventory];
        bag.Items.Add(new ItemMock((uint)(bag.Items.Count + 1), new ItemTemplate { Id = templateId }, count));
    }

    [Test]
    public async Task OnItemGroupGather_MatchingGroup_UpdatesObjectiveToInventoryTotal()
    {
        // Arrange
        var (act, quest, questAct) = SetupWithQuestAct(CreateInventory((GroupItemA, 1)));
        act.InitializeAction(quest, questAct);

        // Acquire another group item after the act was initialized
        AddItemToInventory(quest.Owner.Inventory, GroupItemA, 1);

        // Act
        quest.Owner.Events.OnItemGroupGather(quest.Owner, new OnItemGroupGatherArgs
        {
            ItemId = GroupItemA,
            Count = 1,
            ItemGroupId = GroupId
        });

        // Assert
        await Assert.That(quest.Objectives[0]).IsEqualTo(ObjectiveCount);
    }

    [Test]
    public async Task OnItemGroupGather_NonMatchingGroup_DoesNotUpdateObjective()
    {
        // Arrange
        var (act, quest, questAct) = SetupWithQuestAct(CreateInventory((GroupItemA, 1)));
        act.InitializeAction(quest, questAct);

        // Acquire another group item after the act was initialized
        AddItemToInventory(quest.Owner.Inventory, GroupItemA, 1);

        // Act
        quest.Owner.Events.OnItemGroupGather(quest.Owner, new OnItemGroupGatherArgs
        {
            ItemId = GroupItemA,
            Count = 1,
            ItemGroupId = 9999
        });

        // Assert
        await Assert.That(quest.Objectives[0]).IsEqualTo(1);
    }
}
