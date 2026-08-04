using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

public class QuestActConAcceptNpcKillTests
{
    /// <summary>
    /// Builds a QuestActConAcceptNpcKill act for the given NpcId, plus a Quest
    /// whose acceptor state (type + id) can be set per test.
    /// </summary>
    private static (QuestActConAcceptNpcKill Act, Quest Quest) SetupActAndQuest(uint npcId)
    {
        var questTemplate = new QuestTemplate { Id = 123 };
        var componentTemplate = new QuestComponentTemplate(questTemplate) { KindId = QuestComponentKind.Start };

        var mockCharacter = Mock.Of<ICharacter>();
        mockCharacter.Name.Returns("Tester");
        mockCharacter.Id.Returns(1u);

        var mockQuestManager = Mock.Of<IQuestManager>();
        var mockTickManager = Mock.Of<ITickManager>();
        mockTickManager.OnTick.Returns(new TickManager.TickEventHandler());
        var taskManagerInstance = new TaskManager(mockTickManager.Object);
        var mockSkillManager = Mock.Of<ISkillManager>();
        var mockExpressTextManager = Mock.Of<IExpressTextManager>();
        var mockWorldManager = Mock.Of<IWorldManager>();

        var quest = new Quest(
            questTemplate,
            mockCharacter.Object,
            mockQuestManager.Object,
            taskManagerInstance,
            mockSkillManager.Object,
            mockExpressTextManager.Object,
            mockWorldManager.Object);

        var act = new QuestActConAcceptNpcKill(componentTemplate) { NpcId = npcId };
        return (act, quest);
    }

    [Test]
    public async Task RunAct_WithKillAcceptorAndMatchingNpcId_ReturnsTrue()
    {
        // Arrange
        var (act, quest) = SetupActAndQuest(npcId: 500);
        quest.QuestAcceptorType = QuestAcceptorType.Kill;
        quest.AcceptorId = 500;

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RunAct_WithKillAcceptorAndDifferentNpcId_ReturnsFalse()
    {
        // Arrange
        var (act, quest) = SetupActAndQuest(npcId: 500);
        quest.QuestAcceptorType = QuestAcceptorType.Kill;
        quest.AcceptorId = 999;

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_WithNpcAcceptor_ReturnsFalse()
    {
        // Arrange
        // Regression: this act used to be a copy-paste of QuestActConAcceptNpc,
        // passing for QuestAcceptorType.Npc — which meant no code path could ever
        // start a kill-acceptor quest (the Npc acceptor is never set for kills).
        var (act, quest) = SetupActAndQuest(npcId: 500);
        quest.QuestAcceptorType = QuestAcceptorType.Npc;
        quest.AcceptorId = 500;

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task RunAct_WithUnknownAcceptor_ReturnsFalse()
    {
        // Arrange
        var (act, quest) = SetupActAndQuest(npcId: 500);
        quest.QuestAcceptorType = QuestAcceptorType.Unknown;
        quest.AcceptorId = 500;

        // Act
        var result = act.RunAct(quest, null, 0);

        // Assert
        await Assert.That(result).IsFalse();
    }
}
